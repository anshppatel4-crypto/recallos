using System.Diagnostics;
using System.IO;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Search;
using RecallOS.Core.Storage;
using RecallOS.Core.Tests.Fakes;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// The two promises the product actually makes: pressing Record keeps recording until you
/// stop it, and what it recorded can then be found by searching for words that were on the
/// screen.
/// </summary>
/// <remarks>
/// These run the real scheduler, the real OCR queue, the real SQLite store and the real
/// search service against a synthetic screen. They are slower than the unit tests because
/// they wait on actual timers — but they are the only tests that would catch the pipeline
/// being wired up wrongly, which is precisely the failure a user would describe as "it
/// doesn't work".
/// </remarks>
public sealed class RecordingAndSearchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "recallos-tests",
        Guid.NewGuid().ToString("N"));

    private RecallPaths _paths = null!;
    private SqliteConnectionFactory _factory = null!;
    private FrameRepository _repository = null!;
    private FrameFileStore _files = null!;
    private SqliteSettingsStore _settings = null!;
    private FakeScreen _screen = null!;
    private CapturePipeline _pipeline = null!;
    private CaptureScheduler _scheduler = null!;

    public async Task InitializeAsync()
    {
        _paths = new RecallPaths(_root).EnsureCreated();
        _factory = new SqliteConnectionFactory(_paths);
        await new DatabaseBootstrapper(_factory).InitializeAsync();

        _repository = new FrameRepository(_factory);
        _files = new FrameFileStore(_paths);
        _settings = new SqliteSettingsStore(_factory);
        await _settings.LoadAsync();

        _screen = new FakeScreen { VaryEachFrame = true };
        _pipeline = new CapturePipeline(_screen, _repository, _files, _settings);
        _scheduler = new CaptureScheduler(_pipeline, _settings);
    }

    public async Task DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        _factory.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task StartRecordingAsync(int intervalSeconds = 2)
    {
        var settings = _settings.Current.Clone();
        settings.AutoCaptureEnabled = true;
        settings.CaptureIntervalSeconds = intervalSeconds;
        settings.IdleThresholdSeconds = 0;   // the fake screen has no real input to report
        settings.OcrEnabled = false;         // not under test here
        await _settings.SaveAsync(settings);

        _scheduler.Start();
    }

    private async Task<int> FrameCountAsync() =>
        (await _repository.GetStatisticsAsync()).FrameCount;

    /// <summary>Poll until a condition holds or the budget runs out; returns whether it held.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan budget)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < budget)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return await condition();
    }

    // ---- recording ------------------------------------------------------------------

    [Fact]
    public async Task PressingRecordKeepsCapturingUntilItIsStopped()
    {
        await StartRecordingAsync(intervalSeconds: 2);

        // Three frames means the loop ran repeatedly rather than firing once.
        var reachedThree = await WaitUntilAsync(async () => await FrameCountAsync() >= 3, TimeSpan.FromSeconds(20));
        Assert.True(reachedThree, $"only {await FrameCountAsync()} frames were captured");

        _scheduler.Pause();

        // Allow anything already in flight to land, then confirm it has genuinely stopped.
        await Task.Delay(1500);
        var afterPause = await FrameCountAsync();

        await Task.Delay(5000);
        Assert.Equal(afterPause, await FrameCountAsync());
    }

    [Fact]
    public async Task RecordingResumesAfterAPause()
    {
        await StartRecordingAsync(intervalSeconds: 2);
        Assert.True(await WaitUntilAsync(async () => await FrameCountAsync() >= 1, TimeSpan.FromSeconds(15)));

        _scheduler.Pause();
        await Task.Delay(1200);
        var whilePaused = await FrameCountAsync();

        _scheduler.Resume();

        Assert.True(
            await WaitUntilAsync(async () => await FrameCountAsync() > whilePaused, TimeSpan.FromSeconds(15)),
            "recording did not resume");
    }

    [Fact]
    public async Task StoppingTheSchedulerEndsTheLoop()
    {
        await StartRecordingAsync(intervalSeconds: 2);
        Assert.True(await WaitUntilAsync(async () => await FrameCountAsync() >= 1, TimeSpan.FromSeconds(15)));

        await _scheduler.StopAsync();
        await Task.Delay(1000);

        var afterStop = await FrameCountAsync();
        await Task.Delay(5000);

        Assert.Equal(afterStop, await FrameCountAsync());
        Assert.False(_scheduler.IsRunning);
    }

    [Fact]
    public async Task ACaptureFailureDoesNotEndTheRecordingSession()
    {
        // One bad frame must not silently stop a recording the user believes is running.
        var failing = new ThrowingScreen();
        var pipeline = new CapturePipeline(failing, _repository, _files, _settings);
        await using var scheduler = new CaptureScheduler(pipeline, _settings);

        var settings = _settings.Current.Clone();
        settings.AutoCaptureEnabled = true;
        settings.CaptureIntervalSeconds = 2;
        settings.IdleThresholdSeconds = 0;
        await _settings.SaveAsync(settings);

        var outcomes = 0;
        scheduler.CaptureCompleted += (_, _) => Interlocked.Increment(ref outcomes);
        scheduler.Start();

        // It keeps trying rather than dying on the first exception.
        Assert.True(
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref outcomes) >= 2), TimeSpan.FromSeconds(20)),
            $"scheduler stopped after {outcomes} attempts");
    }

    [Fact]
    public async Task RecordingIsOffUntilItIsTurnedOn()
    {
        // The shipping default. Starting the scheduler with auto-capture disabled must not
        // record anything.
        _scheduler.Start();
        await Task.Delay(4000);

        Assert.Equal(0, await FrameCountAsync());
        Assert.Equal(0, _screen.CaptureCount);
    }

    // ---- recording, then finding it again -------------------------------------------

    [Fact]
    public async Task WhatWasRecordedCanThenBeFoundBySearching()
    {
        // The whole product in one test: record a screen, let the text be extracted and
        // indexed, then find that frame by typing a word that was on it.
        var locator = new TessDataLocator(new RecallPaths());
        if (locator.Resolve("eng") is null)
        {
            // No language pack installed on this machine; text search cannot work and the
            // app says so rather than pretending otherwise.
            return;
        }

        using var engine = new TesseractOcrEngine(locator);
        var embeddings = new HashingEmbeddingProvider(128);
        var search = new HybridSearchService(_factory, _repository, embeddings);

        await using var ocrQueue = new OcrQueueProcessor(
            _repository,
            engine,
            _files,
            new TextChunker(),
            embeddings,
            new HeuristicIntentClassifier(),
            _settings);

        // Exactly the wiring the application performs at startup.
        _pipeline.FrameCaptured += (_, frame) => ocrQueue.Enqueue(frame.Id);
        ocrQueue.Start();

        var settings = _settings.Current.Clone();
        settings.OcrEnabled = true;
        settings.SemanticIndexingEnabled = true;
        await _settings.SaveAsync(settings);

        _screen.Text = "Quarterly revenue projections for Northwind";
        var outcome = await _pipeline.CaptureAsync(CaptureRequest.Manual());
        Assert.True(outcome.WasStored);

        var frameId = outcome.Frame!.Id;

        var extracted = await WaitUntilAsync(
            async () => (await _repository.GetAsync(frameId))?.OcrStatus == OcrStatus.Completed,
            TimeSpan.FromSeconds(45));

        var frame = await _repository.GetAsync(frameId);
        Assert.True(extracted, $"text extraction did not finish (status {frame?.OcrStatus}, error: {frame?.OcrError})");
        Assert.Contains("revenue", frame!.Text, StringComparison.OrdinalIgnoreCase);

        // Keyword search finds it.
        var keyword = await search.SearchAsync(SearchQueryParser.Parse("revenue", SearchMode.Keyword));
        Assert.Contains(keyword.Hits, h => h.Frame.Id == frameId);

        // And so does the default hybrid mode, with a readable snippet to show the user.
        var hybrid = await search.SearchAsync(SearchQueryParser.Parse("quarterly revenue", SearchMode.Hybrid));
        var hit = Assert.Single(hybrid.Hits, h => h.Frame.Id == frameId);
        Assert.NotEmpty(hit.Snippet);

        // A word that was never on screen must not match.
        var miss = await search.SearchAsync(SearchQueryParser.Parse("kangaroo", SearchMode.Keyword));
        Assert.DoesNotContain(miss.Hits, h => h.Frame.Id == frameId);
    }

    [Fact]
    public async Task ContinuousRecordingProducesSearchableFrames()
    {
        // The same guarantee, but for frames the scheduler produced rather than a manual
        // capture — this is the path a user is on after pressing Record.
        var locator = new TessDataLocator(new RecallPaths());
        if (locator.Resolve("eng") is null)
        {
            return;
        }

        using var engine = new TesseractOcrEngine(locator);
        var embeddings = new HashingEmbeddingProvider(128);
        var search = new HybridSearchService(_factory, _repository, embeddings);

        await using var ocrQueue = new OcrQueueProcessor(
            _repository, engine, _files, new TextChunker(), embeddings,
            new HeuristicIntentClassifier(), _settings);

        _pipeline.FrameCaptured += (_, frame) => ocrQueue.Enqueue(frame.Id);
        ocrQueue.Start();

        _screen.Text = "Deployment checklist for the Helsinki cluster";

        var settings = _settings.Current.Clone();
        settings.AutoCaptureEnabled = true;
        settings.CaptureIntervalSeconds = 2;
        settings.IdleThresholdSeconds = 0;
        settings.OcrEnabled = true;
        await _settings.SaveAsync(settings);

        _scheduler.Start();

        var found = await WaitUntilAsync(
            async () =>
            {
                var result = await search.SearchAsync(SearchQueryParser.Parse("Helsinki", SearchMode.Keyword));
                return result.Hits.Count > 0;
            },
            TimeSpan.FromSeconds(60));

        _scheduler.Pause();

        Assert.True(found, "a recorded frame never became searchable");
    }
}
