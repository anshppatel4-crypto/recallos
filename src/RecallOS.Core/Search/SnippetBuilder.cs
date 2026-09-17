using RecallOS.Core.Models;

namespace RecallOS.Core.Search;

/// <summary>
/// Builds the excerpt shown under each search result.
/// </summary>
/// <remarks>
/// The snippet is what tells the user whether a hit is the one they meant, without opening
/// it. It is built in managed code rather than with SQLite's <c>snippet()</c> so that the
/// same logic serves keyword and semantic hits alike -- a semantic hit has no FTS match to
/// point at, but still needs a readable excerpt -- and so highlight offsets come back as
/// structured spans the UI can style, instead of embedded marker text it would have to
/// re-parse.
/// </remarks>
public static class SnippetBuilder
{
    private const int DefaultLength = 240;

    public static Snippet Build(string text, IReadOnlyList<string> terms, int maxLength = DefaultLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Snippet(string.Empty, Array.Empty<TextSpan>());
        }

        var single = text.ReplaceLineEndings(" ");
        var anchor = FindBestAnchor(single, terms);

        // No term matched (a pure semantic hit): the opening of the text is the most
        // representative thing available.
        var start = anchor < 0 ? 0 : Math.Max(0, anchor - (maxLength / 3));
        start = BackTrackToWordBoundary(single, start);

        var length = Math.Min(maxLength, single.Length - start);
        var excerpt = single.Substring(start, length);

        if (start > 0)
        {
            excerpt = "…" + excerpt;
        }

        if (start + length < single.Length)
        {
            excerpt += "…";
        }

        return new Snippet(excerpt.Trim(), FindHighlights(excerpt, terms));
    }

    /// <summary>
    /// Prefer the position where the most distinct terms appear close together: a window
    /// containing two query words is far more informative than the first occurrence of one.
    /// </summary>
    private static int FindBestAnchor(string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return -1;
        }

        var positions = new List<(int Index, string Term)>();

        foreach (var term in terms)
        {
            if (term.Length < 2)
            {
                continue;
            }

            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (index >= 0 && positions.Count < 200)
            {
                positions.Add((index, term));
                index = text.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (positions.Count == 0)
        {
            return -1;
        }

        positions.Sort((a, b) => a.Index.CompareTo(b.Index));

        var bestIndex = positions[0].Index;
        var bestCount = 0;

        foreach (var (index, _) in positions)
        {
            var distinct = positions
                .Where(p => p.Index >= index && p.Index < index + DefaultLength)
                .Select(p => p.Term)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (distinct > bestCount)
            {
                bestCount = distinct;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    /// <summary>Nudge the start back to a space so a snippet never opens mid-word.</summary>
    private static int BackTrackToWordBoundary(string text, int start)
    {
        if (start <= 0)
        {
            return 0;
        }

        var limit = Math.Max(0, start - 20);
        for (var i = start; i > limit; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i + 1;
            }
        }

        return start;
    }

    private static IReadOnlyList<TextSpan> FindHighlights(string excerpt, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return Array.Empty<TextSpan>();
        }

        var spans = new List<TextSpan>();

        foreach (var term in terms)
        {
            if (term.Length < 2)
            {
                continue;
            }

            var index = excerpt.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                spans.Add(new TextSpan(index, term.Length));
                index = excerpt.IndexOf(term, index + term.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return Merge(spans);
    }

    /// <summary>
    /// Collapse overlapping spans. Two query terms that overlap in the text would otherwise
    /// produce nested ranges, which any renderer would double-apply.
    /// </summary>
    private static IReadOnlyList<TextSpan> Merge(List<TextSpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<TextSpan> { spans[0] };

        foreach (var span in spans.Skip(1))
        {
            var last = merged[^1];
            if (span.Start <= last.End)
            {
                merged[^1] = new TextSpan(last.Start, Math.Max(last.End, span.End) - last.Start);
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }
}

/// <summary>An excerpt and the ranges within it that matched.</summary>
public readonly record struct Snippet(string Text, IReadOnlyList<TextSpan> Highlights);
