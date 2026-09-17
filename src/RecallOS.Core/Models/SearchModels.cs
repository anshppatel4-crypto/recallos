namespace RecallOS.Core.Models;

/// <summary>
/// A parsed user query. The raw string the user typed is kept alongside the structured
/// filters so the UI can echo it back and so searches can be replayed verbatim.
/// </summary>
public sealed class SearchQuery
{
    /// <summary>Exactly what the user typed.</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>Free-text terms with the <c>key:value</c> filters stripped out.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>FTS5 MATCH expression built from <see cref="Text"/>, or null when there are no terms.</summary>
    public string? MatchExpression { get; init; }

    /// <summary>Bare words and phrases, used for snippet highlighting and semantic scoring.</summary>
    public IReadOnlyList<string> Terms { get; init; } = Array.Empty<string>();

    /// <summary>Process names from <c>app:</c> filters. Matched case-insensitively.</summary>
    public IReadOnlyList<string> Apps { get; init; } = Array.Empty<string>();

    /// <summary>Substrings from <c>title:</c> filters.</summary>
    public IReadOnlyList<string> Titles { get; init; } = Array.Empty<string>();

    /// <summary>Inclusive lower bound from <c>after:</c> / <c>since:</c>.</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary>Exclusive upper bound from <c>before:</c> / <c>until:</c>.</summary>
    public DateTimeOffset? Before { get; init; }

    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }

    /// <summary>True when nothing would be filtered: the caller should just page the timeline.</summary>
    public bool IsEmpty =>
        MatchExpression is null && Apps.Count == 0 && Titles.Count == 0 && After is null && Before is null;
}

/// <summary>One frame that matched, with the evidence for why it matched.</summary>
public sealed class SearchHit
{
    public required CaptureFrame Frame { get; init; }

    /// <summary>Final rank score; higher is better, comparable only within one result set.</summary>
    public double Score { get; init; }

    /// <summary>BM25-derived keyword contribution, 0 when the hit came only from semantics.</summary>
    public double KeywordScore { get; init; }

    /// <summary>Cosine similarity of the best-matching chunk, 0 when semantics were not used.</summary>
    public double SemanticScore { get; init; }

    /// <summary>A short excerpt around the match, ready to render.</summary>
    public string Snippet { get; init; } = string.Empty;

    /// <summary>Ranges within <see cref="Snippet"/> that should be highlighted.</summary>
    public IReadOnlyList<TextSpan> Highlights { get; init; } = Array.Empty<TextSpan>();
}

/// <summary>A half-open character range within a string.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>The outcome of one search, including what it cost.</summary>
public sealed class SearchResult
{
    public static readonly SearchResult Empty = new();

    public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();

    public SearchMode Mode { get; init; }

    public int TotalMatches { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Set when the query was syntactically unusable and a fallback ran instead.</summary>
    public string? Warning { get; init; }
}
