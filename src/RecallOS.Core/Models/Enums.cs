namespace RecallOS.Core.Models;

/// <summary>What region of the desktop a capture covers.</summary>
public enum CaptureTarget
{
    /// <summary>Every monitor, stitched into one virtual-screen image.</summary>
    AllScreens = 0,

    /// <summary>Only the monitor that currently hosts the foreground window.</summary>
    ActiveScreen = 1,

    /// <summary>Only the foreground window's extended frame bounds.</summary>
    ActiveWindow = 2
}

/// <summary>Who asked for the capture.</summary>
public enum CaptureOrigin
{
    /// <summary>The user pressed the capture button or the global hotkey.</summary>
    Manual = 0,

    /// <summary>The background scheduler produced it.</summary>
    Automatic = 1
}

/// <summary>Lifecycle of the OCR pass over a stored frame.</summary>
public enum OcrStatus
{
    /// <summary>Queued but not processed yet.</summary>
    Pending = 0,

    /// <summary>Text was extracted and indexed.</summary>
    Completed = 1,

    /// <summary>The engine ran but threw; <see cref="CaptureFrame.OcrError"/> holds the reason.</summary>
    Failed = 2,

    /// <summary>Deliberately not attempted (no engine installed, or OCR disabled).</summary>
    Skipped = 3
}

/// <summary>Why the scheduler decided not to keep a frame it had just grabbed.</summary>
public enum CaptureSkipReason
{
    None = 0,
    UserIdle = 1,
    ScreenUnchanged = 2,
    Excluded = 3,
    Paused = 4,
    SessionLocked = 5,
    StorageBudgetReached = 6
}

/// <summary>Ranking strategy used to produce a result set.</summary>
public enum SearchMode
{
    /// <summary>FTS5 full-text matching ranked by BM25.</summary>
    Keyword = 0,

    /// <summary>Embedding cosine similarity over indexed chunks.</summary>
    Semantic = 1,

    /// <summary>Keyword recall, re-scored with semantic similarity.</summary>
    Hybrid = 2
}

/// <summary>Granularity of a timeline histogram.</summary>
public enum TimelineGranularity
{
    Minute = 0,
    QuarterHour = 1,
    Hour = 2,
    Day = 3
}
