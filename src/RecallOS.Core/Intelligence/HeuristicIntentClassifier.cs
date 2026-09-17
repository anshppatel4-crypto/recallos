using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;

namespace RecallOS.Core.Intelligence;

/// <summary>
/// Labels a frame with the kind of activity it shows.
/// </summary>
/// <remarks>
/// <para>
/// Intent is what turns a flat list of screenshots into a reconstructable workflow: "the
/// hour I spent debugging, then the docs I read, then the message I sent". Grouping by
/// application alone is not enough, because a browser is used for all three.
/// </para>
/// <para>
/// The rules combine two weak signals -- the owning process, which is reliable but coarse,
/// and vocabulary in the extracted text, which is specific but noisy. Confidence reflects
/// how much evidence agreed, and is deliberately reported low when only the process matched,
/// so the UI can present a label without implying certainty it does not have.
/// </para>
/// <para>
/// This is the seam a learned classifier replaces: same <see cref="IIntentClassifier"/>
/// contract, same stored columns, no schema change.
/// </para>
/// </remarks>
public sealed class HeuristicIntentClassifier : IIntentClassifier
{
    private sealed record Rule(
        string Label,
        string[] Processes,
        string[] Keywords,
        string[] TitleHints);

    /// <summary>
    /// Ordered by specificity: the first rule to reach the acceptance threshold wins, so
    /// narrow categories are tested before broad ones like Browsing.
    /// </summary>
    private static readonly Rule[] Rules =
    [
        new Rule(
            "Coding",
            ["code", "devenv", "rider", "idea64", "pycharm", "sublime_text", "notepad++", "webstorm", "clion", "cursor"],
            ["function", "class ", "import ", "const ", "return", "public ", "private ", "def ", "async ",
             "null", "void", "namespace", "git", "commit", "branch", "compile", "=>", "();"],
            ["visual studio", ".cs", ".py", ".ts", ".js", ".rs", ".go", ".java", "repository"]),

        new Rule(
            "Terminal",
            ["windowsterminal", "cmd", "powershell", "pwsh", "wt", "conhost", "bash", "wsl"],
            ["$ ", "> ", "error:", "warning:", "npm ", "dotnet ", "pip ", "sudo ", "cd ", "exit code"],
            ["terminal", "command prompt", "powershell"]),

        new Rule(
            "Communicating",
            ["slack", "teams", "discord", "outlook", "thunderbird", "telegram", "whatsapp", "zoom", "skype"],
            ["reply", "sent", "inbox", "unread", "subject:", "to:", "cc:", "meeting", "typing", "message"],
            ["inbox", "chat", "mail", "call", "meeting"]),

        new Rule(
            "Writing",
            ["winword", "notion", "obsidian", "onenote", "typora", "notepad", "wordpad", "scrivener"],
            ["draft", "paragraph", "chapter", "heading", "outline", "notes", "summary", "agenda"],
            ["document", "untitled", "draft", ".docx", ".md"]),

        new Rule(
            "Designing",
            ["figma", "photoshop", "illustrator", "blender", "sketch", "affinity", "canva", "inkscape"],
            ["layer", "canvas", "artboard", "opacity", "gradient", "stroke", "frame", "component"],
            ["design", "mockup", "prototype", "artboard"]),

        new Rule(
            "Analyzing",
            ["excel", "tableau", "powerbi", "rstudio", "jupyter", "dbeaver", "ssms"],
            ["select ", "where ", "group by", "sum(", "average", "chart", "pivot", "query", "dataset", "rows"],
            ["spreadsheet", ".xlsx", ".csv", "dashboard", "report"]),

        new Rule(
            "Watching",
            ["vlc", "mpc-hc", "potplayer", "spotify", "netflix"],
            ["subscribe", "views", "playlist", "episode", "season", "play", "pause", "buffering"],
            ["youtube", "netflix", "twitch", "video", "- vlc"]),

        new Rule(
            "Reading",
            ["acrord32", "sumatrapdf", "foxitreader", "calibre", "kindle"],
            ["abstract", "introduction", "conclusion", "chapter", "references", "figure ", "documentation",
             "according to", "published", "article"],
            [".pdf", "docs", "documentation", "wiki", "manual", "guide"]),

        new Rule(
            "Browsing",
            ["chrome", "msedge", "firefox", "brave", "opera", "arc", "vivaldi"],
            ["http", "www.", "search", "results", "sign in", "cookies", "subscribe", "menu"],
            ["- google", "search", "http"])
    ];

    /// <summary>Below this the label is not worth showing, and the frame stays unlabelled.</summary>
    private const double AcceptanceThreshold = 0.30;

    public IntentPrediction Classify(CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var process = frame.ProcessName ?? string.Empty;
        var title = frame.WindowTitle ?? string.Empty;

        // Only the head of the text is scanned. Intent lives in what is on screen now, and
        // scanning a whole dense screenful per frame would make backfilling a large store
        // needlessly slow.
        var text = frame.Text.Length > 4000 ? frame.Text[..4000] : frame.Text;

        var best = IntentPrediction.Unknown;

        foreach (var rule in Rules)
        {
            var score = Score(rule, process, title, text);
            if (score > best.Confidence)
            {
                best = new IntentPrediction(rule.Label, score);
            }
        }

        return best.Confidence >= AcceptanceThreshold ? best : IntentPrediction.Unknown;
    }

    private static double Score(Rule rule, string process, string title, string text)
    {
        double score = 0;

        // The process is the strongest single signal but never conclusive on its own: it
        // tops out below the threshold that a full match reaches.
        if (rule.Processes.Any(p => process.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.45;
        }

        var titleHits = rule.TitleHints.Count(h => title.Contains(h, StringComparison.OrdinalIgnoreCase));
        score += Math.Min(0.25, titleHits * 0.12);

        if (text.Length > 0)
        {
            var keywordHits = rule.Keywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

            // Diminishing returns: three matching terms is meaningful evidence, twenty is
            // not proportionally more meaningful.
            score += Math.Min(0.40, keywordHits * 0.08);
        }

        return Math.Min(1.0, score);
    }
}
