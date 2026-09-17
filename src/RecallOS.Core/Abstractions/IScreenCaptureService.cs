using RecallOS.Core.Models;

namespace RecallOS.Core.Abstractions;

/// <summary>Grabs pixels off the desktop. Implementations are expected to be thread-safe.</summary>
public interface IScreenCaptureService
{
    /// <summary>
    /// Take one capture. The caller owns the returned image and must dispose it.
    /// </summary>
    Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>Read the foreground window title and owning process without capturing anything.</summary>
    ForegroundWindowInfo GetForegroundWindow();

    /// <summary>How long since the last keyboard or mouse input, session-wide.</summary>
    TimeSpan GetUserIdleTime();

    /// <summary>True when the workstation is locked or a secure desktop is up.</summary>
    bool IsSessionLocked();
}

/// <summary>What was in front when we looked.</summary>
public readonly record struct ForegroundWindowInfo(nint Handle, string? Title, string? ProcessName)
{
    public static readonly ForegroundWindowInfo None = new(0, null, null);

    public bool IsValid => Handle != 0;
}
