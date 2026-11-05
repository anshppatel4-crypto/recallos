using System.Drawing;
using System.IO;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Privacy;
using RecallOS.Core.Search;
using RecallOS.Core.Storage;
using RecallOS.Core.Tests.Fakes;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Redaction through the whole pipeline: a secret on screen must not survive into the
/// stored image, the extracted text, or the search index.
/// </summary>
/// <remarks>
/// The unit tests prove the matcher and the blur work in isolation. These prove they are
/// actually wired in — which is the failure that would matter, because a redaction engine
/// nobody calls looks identical to one that works until someone searches their history and
/// finds their own card number.
/// </remarks>
public sealed class RedactionPipelineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "recallos-tests", Guid.NewGuid().ToString("N"));

    private RecallPaths _paths = null!;
    private SqliteConnectionFactory _factory = null!;
    private FrameRepository _repository = null!;
    private FrameFileStore _files = null!;
    private SqliteSettingsStore _settings = null!;
    private FakeScreen _screen = null!;

    public async Task InitializeAsync()
    {
        _paths = new RecallPaths(_root).EnsureCreated();
        _factory = new SqliteConnectionFactory(_paths);
        await new DatabaseBootstrapper(_factory).InitializeAsync();

        _repository = new FrameRepository(_factory);
        _files = new FrameFileStore(_paths);
        _settings = new SqliteSettingsStore(_factory);
        await _settings.LoadAsync();

        _screen = new FakeScreen();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>A detector that reports a fixed rectangle, standing in for a password box.</summary>
    private sealed class FixedRegionDetector(PixelRect region) : ISensitiveRegionDetector
    {
        public IReadOnlyList<PixelRect> FindRegions(int x, int y, int w, int h) => [region];
    }

    [Fact]
    public async Task APasswordFieldIsBlurredBeforeTheFrameIsWritten()
    {
        // Real text inside the region, so there is genuine detail to destroy.
        _screen.Text = "SuperSecretPassword123";

        var region = new PixelRect(20, 20, 400, 80);

        // Capture the same screen twice: once untouched, once with the region reported as
        // sensitive. Comparing the two is what proves the blur removed information —
        // an absolute threshold would only be measuring the tint.
        var plain = new CapturePipeline(_screen, _repository, _files, _settings);
        var plainOutcome = await plain.CaptureAsync(CaptureRequest.Manual());

        var redacting = new CapturePipeline(
            _screen, _repository, _files, _settings, new FixedRegionDetector(region));
        var redactedOutcome = await redacting.CaptureAsync(CaptureRequest.Manual());

        Assert.True(redactedOutcome.WasStored);
        Assert.Equal(1, redactedOutcome.Frame!.RedactedRegionCount);
        Assert.True(redactedOutcome.Frame.WasRedacted);
        Assert.Equal(0, plainOutcome.Frame!.RedactedRegionCount);

        // Measured on the bytes that were actually written, not on the in-memory bitmap.
        var sample = new Rectangle(30, 30, 380, 60);

        using var plainImage = new Bitmap(_files.ResolveImage(plainOutcome.Frame.ImagePath));
        using var redactedImage = new Bitmap(_files.ResolveImage(redactedOutcome.Frame.ImagePath));

        var before = VarianceOf(plainImage, sample);
        var after = VarianceOf(redactedImage, sample);

        Assert.True(before > 500, $"the control frame had no detail to begin with (variance {before:F0})");
        Assert.True(after < before / 10,
            $"detail survived the blur: variance only fell from {before:F0} to {after:F0}");
    }

    [Fact]
    public async Task NothingIsBlurredWhenThePasswordRuleIsOff()
    {
        var settings = _settings.Current.Clone();
        settings.BlurPasswordFields = false;
        await _settings.SaveAsync(settings);

        var pipeline = new CapturePipeline(
            _screen, _repository, _files, _settings,
            new FixedRegionDetector(new PixelRect(20, 20, 120, 60)));

        var outcome = await pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.Equal(0, outcome.Frame!.RedactedRegionCount);
    }

    [Fact]
    public async Task ADetectorThatThrowsDoesNotCostTheFrame()
    {
        var pipeline = new CapturePipeline(
            _screen, _repository, _files, _settings, new ThrowingDetector());

        var outcome = await pipeline.CaptureAsync(CaptureRequest.Manual());

        // The frame is still recorded; only the pre-emptive blur was lost.
        Assert.True(outcome.WasStored);
        Assert.Equal(0, outcome.Frame!.RedactedRegionCount);
    }

    private sealed class ThrowingDetector : ISensitiveRegionDetector
    {
        public IReadOnlyList<PixelRect> FindRegions(int x, int y, int w, int h) =>
            throw new InvalidOperationException("UI Automation exploded");
    }

    [Fact]
    public async Task ACardNumberOnScreenNeverReachesTheSearchIndex()
    {
        // The full journey: rendered on screen, captured, read by OCR, redacted, indexed.
        var locator = new TessDataLocator(new RecallPaths());
        if (locator.Resolve("eng") is null)
        {
            return;   // no language pack on this machine; covered by the unit tests
        }

        using var engine = new TesseractOcrEngine(locator);
        var embeddings = new HashingEmbeddingProvider(128);
        var search = new HybridSearchService(_factory, _repository, embeddings);
        var pipeline = new CapturePipeline(_screen, _repository, _files, _settings);

        await using var ocrQueue = new OcrQueueProcessor(
            _repository, engine, _files, new TextChunker(), embeddings,
            new HeuristicIntentClassifier(), _settings);

        pipeline.FrameCaptured += (_, frame) => ocrQueue.Enqueue(frame.Id);
        ocrQueue.Start();

        const string card = "4111111111111111";
        _screen.Text = $"Card {card} exp 12/28";

        var outcome = await pipeline.CaptureAsync(CaptureRequest.Manual());
        var frameId = outcome.Frame!.Id;

        var done = await WaitUntilAsync(
            async () => (await _repository.GetAsync(frameId))?.OcrStatus == OcrStatus.Completed,
            TimeSpan.FromSeconds(45));

        var frame = await _repository.GetAsync(frameId);
        Assert.True(done, $"OCR did not finish (status {frame?.OcrStatus}, error {frame?.OcrError})");

        // OCR on a rendered card number is reliable enough to assert on; if it failed to
        // read the digits at all there is nothing to redact and nothing to leak either.
        if (!frame!.Text.Contains("4111", StringComparison.Ordinal)
            && !frame.Text.Contains("redacted", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.DoesNotContain(card, frame.Text);
        Assert.Contains("redacted", frame.Text, StringComparison.OrdinalIgnoreCase);

        // And it is not reachable through search either.
        var hits = await search.SearchAsync(SearchQueryParser.Parse(card, SearchMode.Keyword));
        Assert.DoesNotContain(hits.Hits, h => h.Frame.Id == frameId);

        // The stored chunks, which are what get embedded, are clean too.
        var chunks = await _repository.GetChunksAsync(frameId);
        Assert.All(chunks, c => Assert.DoesNotContain(card, c.Text));
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return await condition();
    }

    /// <summary>True when a region has almost no variation left in it.</summary>
    private static bool IsFlat(Bitmap bitmap, Rectangle area) => VarianceOf(bitmap, area) < 40;

    private static double VarianceOf(Bitmap bitmap, Rectangle area)
    {
        double sum = 0, sumSq = 0;
        var n = 0;

        for (var y = area.Top; y < area.Bottom && y < bitmap.Height; y++)
        {
            for (var x = area.Left; x < area.Right && x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                double lum = (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
                sum += lum;
                sumSq += lum * lum;
                n++;
            }
        }

        if (n == 0)
        {
            return 0;
        }

        var mean = sum / n;
        return (sumSq / n) - (mean * mean);
    }
}
