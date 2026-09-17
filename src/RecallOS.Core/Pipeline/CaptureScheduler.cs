using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;

namespace RecallOS.Core.Pipeline;

/// <summary>
/// Drives automatic capture on an interval.
/// </summary>
/// <remarks>
/// <para>
/// The loop measures its delay from the end of each capture, not from a fixed wall-clock
/// grid. A periodic timer that fires while the previous capture is still running would
/// queue overlapping captures and, on a slow machine, spiral. Waiting a full interval after
/// each completion means the system degrades by capturing slightly less often, which is the
/// right direction to fail in.
/// </para>
/// <para>
/// Settings are re-read every tick so toggling the interval, or pausing, takes effect at
/// the next capture instead of requiring a restart.
/// </para>
/// </remarks>
public sealed class CaptureScheduler : IAsyncDisposable
{
    private readonly CapturePipeline _pipeline;
    private readonly ISettingsStore _settings;
    private readonly ILogger<CaptureScheduler> _logger;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _paused;

    public CaptureScheduler(
        CapturePipeline pipeline,
        ISettingsStore settings,
        ILogger<CaptureScheduler>? logger = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CaptureScheduler>.Instance;
    }

    /// <summary>True while the background loop is running and not paused.</summary>
    public bool IsRunning => _loop is { IsCompleted: false } && !_paused;

    /// <summary>When the next automatic capture is expected, for the status bar.</summary>
    public DateTimeOffset? NextCaptureAt { get; private set; }

    public event EventHandler<CaptureOutcome>? CaptureCompleted;

    public event EventHandler? StateChanged;

    public void Start()
    {
        if (_loop is { IsCompleted: false })
        {
            _paused = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _cancellation = new CancellationTokenSource();
        _paused = false;
        _loop = Task.Run(() => RunAsync(_cancellation.Token));

        _logger.LogInformation(
            "Automatic capture started at {Interval}s intervals.",
            _settings.Current.CaptureIntervalSeconds);

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stop capturing but keep the loop alive, so resuming is instant.</summary>
    public void Pause()
    {
        _paused = true;
        NextCaptureAt = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Automatic capture paused.");
    }

    public void Resume()
    {
        if (_loop is null or { IsCompleted: true })
        {
            Start();
            return;
        }

        _paused = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Automatic capture resumed.");
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug("Capture loop did not stop within the shutdown window.");
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _loop = null;
        NextCaptureAt = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _settings.Current;
            var interval = TimeSpan.FromSeconds(Math.Max(2, settings.CaptureIntervalSeconds));

            try
            {
                if (_paused || !settings.AutoCaptureEnabled)
                {
                    NextCaptureAt = null;

                    // Poll at a fixed, short cadence while idle so that re-enabling capture
                    // takes effect promptly regardless of how long the interval is set to.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var outcome = await _pipeline.CaptureAsync(
                    new CaptureRequest
                    {
                        Target = settings.AutoCaptureTarget,
                        Origin = CaptureOrigin.Automatic,
                        IncludeCursor = settings.IncludeCursor,
                        SkipIfUnchanged = true
                    },
                    cancellationToken).ConfigureAwait(false);

                CaptureCompleted?.Invoke(this, outcome);

                NextCaptureAt = DateTimeOffset.Now + interval;
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Capture scheduler error; retrying after one interval.");

                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
