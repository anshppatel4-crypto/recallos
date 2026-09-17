namespace RecallOS.Core.Models;

/// <summary>
/// One moment of the user's screen: the image on disk, what produced it, and the
/// text RecallOS extracted from it. This is the central record of the system --
/// search results, the timeline and replay are all projections of it.
/// </summary>
public sealed class CaptureFrame
{
    /// <summary>Database identity. Zero until the frame has been persisted.</summary>
    public long Id { get; init; }

    /// <summary>When the pixels were grabbed, always UTC.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Image location relative to the frame store root, e.g. <c>2026/09/16/frame-....png</c>.</summary>
    public string ImagePath { get; init; } = string.Empty;

    /// <summary>Downscaled preview relative to the store root, or null if none was produced.</summary>
    public string? ThumbnailPath { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Byte size of the full-resolution image.</summary>
    public long FileSizeBytes { get; init; }

    public CaptureTarget Target { get; init; }

    public CaptureOrigin Origin { get; init; }

    /// <summary>Title of the foreground window at capture time, when one could be read.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>Process that owned the foreground window, without the <c>.exe</c> suffix.</summary>
    public string? ProcessName { get; init; }

    /// <summary>Device name of the monitor the capture came from, e.g. <c>\.\DISPLAY1</c>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>64-bit perceptual difference hash, used to drop visually identical frames.</summary>
    public ulong PerceptualHash { get; init; }

    public OcrStatus OcrStatus { get; init; }

    /// <summary>Everything the OCR engine read, newline separated, in reading order.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Mean engine confidence across recognised words, 0..1.</summary>
    public double OcrConfidence { get; init; }

    /// <summary>Identifier of the engine that produced <see cref="Text"/>.</summary>
    public string? OcrEngine { get; init; }

    /// <summary>Failure detail when <see cref="OcrStatus"/> is <see cref="Models.OcrStatus.Failed"/>.</summary>
    public string? OcrError { get; init; }

    public DateTimeOffset? OcrCompletedAt { get; init; }

    /// <summary>Heuristic activity label, e.g. <c>Coding</c> or <c>Reading</c>.</summary>
    public string? IntentLabel { get; init; }

    public double IntentConfidence { get; init; }

    /// <summary>True once the user has removed the frame; rows are soft-deleted before the file is purged.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>A short human label for lists: the window title, else the process, else the time.</summary>
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(WindowTitle) ? WindowTitle!
        : !string.IsNullOrWhiteSpace(ProcessName) ? ProcessName!
        : CapturedAt.ToLocalTime().ToString("g");

    public bool HasText => Text.Length > 0;
}
