namespace RecallOS.Core.Models;

/// <summary>
/// Activity density for one slice of time. The timeline strip is a histogram of these,
/// which is what lets the user scrub to "that busy stretch yesterday afternoon".
/// </summary>
public sealed class TimelineBucket
{
    /// <summary>Inclusive start of the slice, in local time.</summary>
    public DateTimeOffset Start { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTimeOffset End => Start + Duration;

    public int FrameCount { get; init; }

    /// <summary>The intent label that occurred most often in this slice.</summary>
    public string? DominantIntent { get; init; }

    /// <summary>The process seen most often in this slice.</summary>
    public string? DominantApp { get; init; }

    /// <summary>Id of a representative frame, so clicking the bucket can open something.</summary>
    public long? RepresentativeFrameId { get; init; }
}
