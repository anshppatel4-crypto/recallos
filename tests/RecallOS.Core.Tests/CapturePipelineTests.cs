using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Models;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Storage;
using RecallOS.Core.Tests.Fakes;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Tests the decision logic of the capture pipeline against a synthetic screen.
/// </summary>
/// <remarks>
/// The capture device is faked so these never read the real desktop: the behaviour under
/// test is what the pipeline decides to keep and discard, which is exactly the logic the
/// privacy guarantees rest on. A real screen would make the tests both slower and
/// non-deterministic without exercising anything more.
/// </remarks>
public sealed class CapturePipelineTests : IAsyncLifetime
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
        _pipeline = new CapturePipeline(_screen, _repository, _files, _settings);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private async Task ConfigureAsync(Action<RecallSettings> configure)
    {
        var settings = _settings.Current.Clone();
        configure(settings);
        await _settings.SaveAsync(settings);
    }

    private static CaptureRequest Auto() => new()
    {
        Origin = CaptureOrigin.Automatic,
        SkipIfUnchanged = true
    };

    [Fact]
    public async Task AManualCaptureIsStoredWithItsImageAndWindowContext()
    {
        var outcome = await _pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.True(outcome.WasStored);

        var frame = outcome.Frame!;
        Assert.Equal("chrome", frame.ProcessName);
        Assert.Equal("Example Window", frame.WindowTitle);
        Assert.Equal(CaptureOrigin.Manual, frame.Origin);
        Assert.True(File.Exists(_files.ResolveImage(frame.ImagePath)));

        // Queued for text extraction rather than processed inline.
        Assert.Equal(OcrStatus.Pending, frame.OcrStatus);
    }

    [Fact]
    public async Task AnUnchangedScreenIsSkippedOnAutomaticCapture()
    {
        // Opt in: deduplication is off by default so that Record keeps recording.
        await ConfigureAsync(s => s.SkipUnchangedFrames = true);

        await _pipeline.CaptureAsync(Auto());
        var second = await _pipeline.CaptureAsync(Auto());

        Assert.True(second.WasSkipped);
        Assert.Equal(CaptureSkipReason.ScreenUnchanged, second.SkipReason);
        Assert.Equal(1, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task AChangedScreenIsStoredEvenWhenDeduplicationIsOn()
    {
        await ConfigureAsync(s => s.SkipUnchangedFrames = true);

        await _pipeline.CaptureAsync(Auto());

        _screen.Fill = Color.DarkRed;
        var second = await _pipeline.CaptureAsync(Auto());

        Assert.True(second.WasStored);
        Assert.Equal(2, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task AManualCaptureIsAlwaysStoredEvenIfNothingChanged()
    {
        // The user asked. Silently discarding an explicit request would be indefensible.
        await _pipeline.CaptureAsync(CaptureRequest.Manual());
        var second = await _pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.True(second.WasStored);
        Assert.Equal(2, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task AnExcludedProcessIsNeverWrittenToDisk()
    {
        await ConfigureAsync(s => s.ExcludedProcesses = "keepass\nchrome");

        var outcome = await _pipeline.CaptureAsync(Auto());

        Assert.True(outcome.WasSkipped);
        Assert.Equal(CaptureSkipReason.Excluded, outcome.SkipReason);
        Assert.Equal(0, (await _repository.GetStatisticsAsync()).FrameCount);
        Assert.Empty(Directory.EnumerateFiles(_paths.Frames, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TheExclusionListIgnoresTheExeSuffix()
    {
        await ConfigureAsync(s => s.ExcludedProcesses = "chrome.exe");

        Assert.Equal(CaptureSkipReason.Excluded, (await _pipeline.CaptureAsync(Auto())).SkipReason);
    }

    [Fact]
    public async Task AnExcludedWindowTitleSuppressesTheCapture()
    {
        await ConfigureAsync(s => s.ExcludedTitleKeywords = "private browsing");
        _screen.WindowTitle = "Bank - Private Browsing";

        var outcome = await _pipeline.CaptureAsync(Auto());

        Assert.True(outcome.WasSkipped);
        Assert.Equal(CaptureSkipReason.Excluded, outcome.SkipReason);
        Assert.Equal(0, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task ExclusionsApplyToManualCapturesToo()
    {
        // The privacy list is not a scheduling optimisation; it must hold whatever
        // triggered the capture.
        await ConfigureAsync(s => s.ExcludedProcesses = "chrome");

        var outcome = await _pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.True(outcome.WasSkipped);
        Assert.Equal(0, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task AnIdleUserPausesAutomaticCaptureWithoutTouchingTheScreen()
    {
        await ConfigureAsync(s => s.IdleThresholdSeconds = 60);
        _screen.IdleTime = TimeSpan.FromMinutes(5);

        var outcome = await _pipeline.CaptureAsync(Auto());

        Assert.Equal(CaptureSkipReason.UserIdle, outcome.SkipReason);
        Assert.Equal(0, _screen.CaptureCount);
    }

    [Fact]
    public async Task AZeroIdleThresholdMeansRecordRegardless()
    {
        await ConfigureAsync(s => s.IdleThresholdSeconds = 0);
        _screen.IdleTime = TimeSpan.FromHours(3);

        Assert.True((await _pipeline.CaptureAsync(Auto())).WasStored);
    }

    [Fact]
    public async Task ALockedSessionIsNeverCaptured()
    {
        _screen.Locked = true;

        var outcome = await _pipeline.CaptureAsync(Auto());

        Assert.Equal(CaptureSkipReason.SessionLocked, outcome.SkipReason);
        Assert.Equal(0, _screen.CaptureCount);
    }

    [Fact]
    public async Task WithOcrDisabledFramesAreStoredAndMarkedSkipped()
    {
        await ConfigureAsync(s => s.OcrEnabled = false);

        var outcome = await _pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.True(outcome.WasStored);
        Assert.Equal(OcrStatus.Skipped, outcome.Frame!.OcrStatus);
    }

    [Fact]
    public async Task TheFrameCapturedEventFiresForStoredFramesOnly()
    {
        var raised = new List<CaptureFrame>();
        _pipeline.FrameCaptured += (_, frame) => raised.Add(frame);

        await _pipeline.CaptureAsync(CaptureRequest.Manual());

        await ConfigureAsync(s => s.ExcludedProcesses = "chrome");
        await _pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.Single(raised);
    }

    [Fact]
    public async Task ACaptureFailureIsReportedRatherThanThrown()
    {
        // A scheduler that threw on a bad frame would stop recording entirely.
        var failing = new ThrowingScreen();
        var pipeline = new CapturePipeline(failing, _repository, _files, _settings);

        var outcome = await pipeline.CaptureAsync(CaptureRequest.Manual());

        Assert.False(outcome.WasStored);
        Assert.NotNull(outcome.Error);
    }
}
