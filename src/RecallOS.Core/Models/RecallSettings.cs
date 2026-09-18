namespace RecallOS.Core.Models;

/// <summary>
/// User-controlled behaviour. Persisted as key/value rows in the same SQLite file as
/// the frames, so a RecallOS store is one self-contained folder that can be moved,
/// backed up or deleted wholesale.
/// </summary>
public sealed class RecallSettings
{
    /// <summary>Run the background capture scheduler.</summary>
    public bool AutoCaptureEnabled { get; set; }

    /// <summary>
    /// Seconds between automatic captures. Clamped to 2..3600 on load.
    /// </summary>
    /// <remarks>
    /// Ten seconds, not thirty. At thirty a user who switches recording on and watches sees
    /// a single frame and nothing else for half a minute, which reads as broken rather than
    /// as working-but-patient. Ten is frequent enough that the timeline visibly fills while
    /// you watch, and the retention caps still bound what it costs.
    /// </remarks>
    public int CaptureIntervalSeconds { get; set; } = 10;

    public CaptureTarget AutoCaptureTarget { get; set; } = CaptureTarget.AllScreens;

    /// <summary>Stop capturing after this many seconds without keyboard or mouse input.</summary>
    public int IdleThresholdSeconds { get; set; } = 120;

    /// <summary>
    /// Drop automatic frames that look the same as the previous one.
    /// </summary>
    /// <remarks>
    /// Off by default, because "Record" has to mean record. With it on, a screen that is
    /// not changing much produces one frame and then nothing, which is indistinguishable
    /// from the recorder being broken — and the user has no way to tell which it is.
    /// Storage is bounded by the retention caps instead, which prune predictably by age and
    /// size rather than silently refusing to record in the first place. It remains
    /// available for anyone who would rather trade timeline continuity for disk.
    /// </remarks>
    public bool SkipUnchangedFrames { get; set; }

    /// <summary>
    /// Maximum Hamming distance between perceptual hashes still counted as "unchanged".
    /// Zero demands an exact match; around 6 tolerates a blinking cursor and a clock tick.
    /// </summary>
    public int UnchangedHashTolerance { get; set; } = 6;

    /// <summary>Paint the mouse pointer into captures.</summary>
    public bool IncludeCursor { get; set; }

    /// <summary>Run OCR over new frames.</summary>
    public bool OcrEnabled { get; set; } = true;

    /// <summary>Tesseract language codes, plus-joined, e.g. <c>eng</c> or <c>eng+deu</c>.</summary>
    public string OcrLanguages { get; set; } = "eng";

    /// <summary>
    /// Processes whose windows are never captured, one per line, matched
    /// case-insensitively without the <c>.exe</c> suffix.
    /// </summary>
    public string ExcludedProcesses { get; set; } = "";

    /// <summary>Window-title substrings that suppress a capture, one per line.</summary>
    public string ExcludedTitleKeywords { get; set; } = "";

    /// <summary>Delete frames older than this. Zero keeps everything forever.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Start evicting the oldest frames past this size. Zero means no cap.</summary>
    public int MaxStorageGigabytes { get; set; } = 20;

    /// <summary>Generate embeddings so semantic and hybrid search have something to rank.</summary>
    public bool SemanticIndexingEnabled { get; set; } = true;

    public SearchMode DefaultSearchMode { get; set; } = SearchMode.Hybrid;

    /// <summary>Longest edge of the generated thumbnail, in pixels.</summary>
    public int ThumbnailMaxEdge { get; set; } = 480;

    /// <summary>Global hotkey for capture-now, in the form <c>Ctrl+Shift+R</c>.</summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Shift+R";

    /// <summary>Keep running in the notification area when the window is closed.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// False until the user has dismissed the first-run introduction. A tool that records
    /// the screen should explain itself before it starts, not after.
    /// </summary>
    public bool HasSeenWelcome { get; set; }

    /// <summary>Folded set of processes, parsed from <see cref="ExcludedProcesses"/>.</summary>
    public IReadOnlySet<string> ParseExcludedProcesses() => ParseLines(ExcludedProcesses);

    public IReadOnlySet<string> ParseExcludedTitleKeywords() => ParseLines(ExcludedTitleKeywords);

    private static IReadOnlySet<string> ParseLines(string value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return set;
        }

        foreach (var line in value.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^4];
            }

            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }

    /// <summary>Pull every value back into a sane range. Called after loading from disk.</summary>
    public RecallSettings Normalized()
    {
        CaptureIntervalSeconds = Math.Clamp(CaptureIntervalSeconds, 2, 3600);
        IdleThresholdSeconds = Math.Clamp(IdleThresholdSeconds, 0, 86_400);
        UnchangedHashTolerance = Math.Clamp(UnchangedHashTolerance, 0, 32);
        RetentionDays = Math.Clamp(RetentionDays, 0, 3650);
        MaxStorageGigabytes = Math.Clamp(MaxStorageGigabytes, 0, 10_000);
        ThumbnailMaxEdge = Math.Clamp(ThumbnailMaxEdge, 120, 2000);
        if (string.IsNullOrWhiteSpace(OcrLanguages))
        {
            OcrLanguages = "eng";
        }

        return this;
    }

    public RecallSettings Clone() => (RecallSettings)MemberwiseClone();
}
