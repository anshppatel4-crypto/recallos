using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Capture;
using RecallOS.Core.Models;
using RecallOS.Core.Storage;

namespace RecallOS.Core.Pipeline;

/// <summary>
/// The path a capture takes from screen to searchable record.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is split at the OCR boundary on purpose. Grabbing pixels and writing a PNG
/// takes tens of milliseconds; recognising text on a dense 4K screen takes one to several
/// seconds. If those ran together, a 5-second capture interval would fall behind within a
/// minute and the app would feel like it was hanging.
/// </para>
/// <para>
/// So the synchronous stage does the minimum needed to make the moment durable -- capture,
/// hash, write the file, insert the row -- and returns. The frame is immediately visible in
/// the timeline, marked <see cref="OcrStatus.Pending"/>. Text extraction, chunking,
/// embedding and labelling happen afterwards in <see cref="OcrQueueProcessor"/> and fill
/// the record in. A frame is never lost because the expensive half failed.
/// </para>
/// </remarks>
public sealed class CapturePipeline
{
    private readonly IScreenCaptureService _capture;
    private readonly IFrameRepository _repository;
    private readonly FrameFileStore _files;
    private readonly ISettingsStore _settings;
    private readonly ILogger<CapturePipeline> _logger;

    /// <summary>
    /// The hash of the last frame kept. Held in memory rather than re-read from the
    /// database each tick, because change detection runs on every automatic capture and a
    /// query per tick is pure overhead.
    /// </summary>
    private ulong? _lastStoredHash;

    public CapturePipeline(
        IScreenCaptureService capture,
        IFrameRepository repository,
        FrameFileStore files,
        ISettingsStore settings,
        ILogger<CapturePipeline>? logger = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CapturePipeline>.Instance;
    }

    /// <summary>Raised after a frame has been stored, so the UI can show it immediately.</summary>
    public event EventHandler<CaptureFrame>? FrameCaptured;

    /// <summary>Raised when a capture was deliberately discarded, with the reason.</summary>
    public event EventHandler<CaptureSkipReason>? CaptureSkipped;

    /// <summary>
    /// Run one capture end to end. Returns an outcome rather than throwing for the ordinary
    /// cases -- idle, unchanged, excluded -- because on a timer those are the majority.
    /// </summary>
    public async Task<CaptureOutcome> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = _settings.Current;

        try
        {
            if (request.Origin == CaptureOrigin.Automatic)
            {
                var block = ShouldSuppress(settings);
                if (block != CaptureSkipReason.None)
                {
                    return Skip(block);
                }
            }

            using var image = await _capture.CaptureAsync(request, cancellationToken).ConfigureAwait(false);

            // The exclusion list is re-checked against what was actually in front at the
            // instant of capture, not what was there when the timer fired.
            if (IsExcluded(image.ProcessName, image.WindowTitle, settings))
            {
                return Skip(CaptureSkipReason.Excluded);
            }

            var hash = PerceptualHash.Compute(image.Bitmap);

            if (request.SkipIfUnchanged && settings.SkipUnchangedFrames)
            {
                var previous = _lastStoredHash ??= await _repository
                    .GetLatestHashAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (previous is { } last && PerceptualHash.AreSimilar(hash, last, settings.UnchangedHashTolerance))
                {
                    return Skip(CaptureSkipReason.ScreenUnchanged);
                }
            }

            var stored = await _files
                .SaveAsync(image.Bitmap, image.CapturedAt, settings.ThumbnailMaxEdge, cancellationToken)
                .ConfigureAwait(false);

            var frame = await _repository.InsertAsync(
                new CaptureFrame
                {
                    CapturedAt = image.CapturedAt,
                    ImagePath = stored.RelativeImagePath,
                    ThumbnailPath = stored.RelativeThumbnailPath,
                    Width = stored.Width,
                    Height = stored.Height,
                    FileSizeBytes = stored.FileSizeBytes,
                    Target = image.Target,
                    Origin = request.Origin,
                    WindowTitle = image.WindowTitle,
                    ProcessName = image.ProcessName,
                    DisplayName = image.DisplayName,
                    PerceptualHash = hash,
                    // Queued, not processed. The background worker picks it up from here.
                    OcrStatus = settings.OcrEnabled ? OcrStatus.Pending : OcrStatus.Skipped
                },
                cancellationToken).ConfigureAwait(false);

            _lastStoredHash = hash;

            _logger.LogDebug(
                "Stored frame {FrameId} ({Width}x{Height}) from {Process}.",
                frame.Id,
                frame.Width,
                frame.Height,
                frame.ProcessName ?? "unknown");

            FrameCaptured?.Invoke(this, frame);
            return CaptureOutcome.Stored(frame);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad capture must not stop the scheduler. The next tick tries again.
            _logger.LogError(ex, "Capture failed.");
            return CaptureOutcome.Failed(ex);
        }
    }

    /// <summary>Reasons to not even take the picture. Checked before any pixels are touched.</summary>
    private CaptureSkipReason ShouldSuppress(RecallSettings settings)
    {
        if (_capture.IsSessionLocked())
        {
            return CaptureSkipReason.SessionLocked;
        }

        // Nothing is changing while the user is away, so recording it is pure storage cost.
        if (settings.IdleThresholdSeconds > 0
            && _capture.GetUserIdleTime() > TimeSpan.FromSeconds(settings.IdleThresholdSeconds))
        {
            return CaptureSkipReason.UserIdle;
        }

        // Cheap pre-check on the foreground window; re-verified after capture.
        var foreground = _capture.GetForegroundWindow();
        return IsExcluded(foreground.ProcessName, foreground.Title, settings)
            ? CaptureSkipReason.Excluded
            : CaptureSkipReason.None;
    }

    /// <summary>
    /// The user's privacy list. This is the one control that has to be obeyed without
    /// exception: if a password manager is excluded, no frame of it may ever reach disk.
    /// </summary>
    private static bool IsExcluded(string? processName, string? windowTitle, RecallSettings settings)
    {
        if (!string.IsNullOrEmpty(processName))
        {
            var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            if (settings.ParseExcludedProcesses().Contains(name))
            {
                return true;
            }
        }

        if (string.IsNullOrEmpty(windowTitle))
        {
            return false;
        }

        foreach (var keyword in settings.ParseExcludedTitleKeywords())
        {
            if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private CaptureOutcome Skip(CaptureSkipReason reason)
    {
        _logger.LogTrace("Capture skipped: {Reason}.", reason);
        CaptureSkipped?.Invoke(this, reason);
        return CaptureOutcome.Skipped(reason);
    }
}
