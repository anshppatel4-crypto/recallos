namespace RecallOS.Core.Models;

/// <summary>What to grab and how to treat it.</summary>
public sealed class CaptureRequest
{
    public static CaptureRequest Manual(CaptureTarget target = CaptureTarget.AllScreens) =>
        new() { Target = target, Origin = CaptureOrigin.Manual };

    public CaptureTarget Target { get; init; } = CaptureTarget.AllScreens;

    public CaptureOrigin Origin { get; init; } = CaptureOrigin.Manual;

    /// <summary>Draw the mouse pointer into the image.</summary>
    public bool IncludeCursor { get; init; }

    /// <summary>
    /// Skip the frame when it is perceptually identical to the previous one.
    /// Manual captures set this false: if the user asked, the user gets a frame.
    /// </summary>
    public bool SkipIfUnchanged { get; init; }
}

/// <summary>Raw pixels plus the window context that was true at the instant of the grab.</summary>
public sealed class CapturedImage : IDisposable
{
    public required System.Drawing.Bitmap Bitmap { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public CaptureTarget Target { get; init; }

    public string? WindowTitle { get; init; }

    public string? ProcessName { get; init; }

    public string? DisplayName { get; init; }

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// The result of pushing one capture all the way through the pipeline. A skipped
/// capture is a normal, expected outcome, not an error, so it is modelled explicitly.
/// </summary>
public sealed class CaptureOutcome
{
    public static CaptureOutcome Skipped(CaptureSkipReason reason) => new() { SkipReason = reason };

    public static CaptureOutcome Stored(CaptureFrame frame) => new() { Frame = frame };

    public static CaptureOutcome Failed(Exception error) => new() { Error = error };

    /// <summary>The persisted frame, present only when <see cref="WasStored"/>.</summary>
    public CaptureFrame? Frame { get; init; }

    public CaptureSkipReason SkipReason { get; init; } = CaptureSkipReason.None;

    public Exception? Error { get; init; }

    public bool WasStored => Frame is not null;

    public bool WasSkipped => Frame is null && Error is null;
}
