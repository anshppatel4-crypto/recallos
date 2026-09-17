using System.Globalization;
using System.Text;
using RecallOS.Core.Models;

namespace RecallOS.Core.Search;

/// <summary>
/// Turns what the user typed into a structured <see cref="SearchQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// The syntax is the one people already expect from mail and issue trackers:
/// <c>invoice app:chrome after:yesterday</c>, <c>"pull request" title:github before:2026-09-01</c>.
/// Anything not recognised as a filter is free text, so a user who knows none of this can
/// just type words and get results.
/// </para>
/// <para>
/// The FTS5 expression is built here rather than by string-concatenating user input at the
/// call site. Every term is quoted and internal quotes are doubled, which both prevents a
/// stray character from being read as FTS operator syntax and means a query like
/// <c>C++ "or" AND</c> searches for those words instead of failing to parse.
/// </para>
/// </remarks>
public static class SearchQueryParser
{
    public static SearchQuery Parse(string? raw, SearchMode mode = SearchMode.Hybrid, int limit = 200)
    {
        raw ??= string.Empty;

        var terms = new List<string>();
        var apps = new List<string>();
        var titles = new List<string>();
        DateTimeOffset? after = null;
        DateTimeOffset? before = null;
        var freeText = new StringBuilder();

        foreach (var token in Tokenize(raw))
        {
            var separator = token.IndexOf(':');

            // A colon only introduces a filter when it follows a known key; "http://x" and
            // "note: remember" stay free text.
            if (separator > 0 && separator < token.Length - 1)
            {
                var key = token[..separator].ToLowerInvariant();
                var value = Unquote(token[(separator + 1)..]);

                switch (key)
                {
                    case "app" or "process":
                        apps.Add(value);
                        continue;
                    case "title" or "window":
                        titles.Add(value);
                        continue;
                    case "after" or "since" or "from":
                        after = ParseDate(value, isLowerBound: true) ?? after;
                        continue;
                    case "before" or "until" or "to":
                        before = ParseDate(value, isLowerBound: false) ?? before;
                        continue;
                    case "on" or "day":
                    {
                        // A single day is just both bounds at once.
                        var start = ParseDate(value, isLowerBound: true);
                        if (start is { } day)
                        {
                            after = day;
                            before = day.AddDays(1);
                            continue;
                        }

                        break;
                    }
                }
            }

            var text = Unquote(token);
            if (text.Length == 0)
            {
                continue;
            }

            terms.Add(text);
            freeText.Append(text).Append(' ');
        }

        return new SearchQuery
        {
            Raw = raw,
            Text = freeText.ToString().Trim(),
            MatchExpression = BuildMatchExpression(terms),
            Terms = terms,
            Apps = apps,
            Titles = titles,
            After = after,
            Before = before,
            Mode = mode,
            Limit = Math.Clamp(limit, 1, 2000)
        };
    }

    /// <summary>Split on whitespace, keeping double-quoted runs together.</summary>
    internal static IEnumerable<string> Tokenize(string input)
    {
        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                builder.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            builder.Append(c);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>
    /// Compose the FTS5 MATCH expression. Multi-word values become phrase queries; single
    /// words get a prefix wildcard so results appear while the user is still typing.
    /// </summary>
    internal static string? BuildMatchExpression(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(terms.Count);

        foreach (var term in terms)
        {
            var sanitized = SanitizeForFts(term);
            if (sanitized.Length == 0)
            {
                continue;
            }

            // Doubling embedded quotes is how FTS5 escapes them inside a quoted string.
            // SanitizeForFts has already removed every quote, so this is belt-and-braces:
            // it keeps the expression well-formed if that filter is ever loosened.
            var quoted = sanitized.Replace("\"", "\"\"", StringComparison.Ordinal);

            parts.Add(sanitized.Contains(' ', StringComparison.Ordinal)
                ? $"\"{quoted}\""
                : $"\"{quoted}\"*");
        }

        // FTS5 ANDs adjacent terms by default, which is what "narrow as I add words" means.
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// Strip characters FTS5 treats as syntax. They are separators to the tokenizer anyway,
    /// so removing them changes which documents match not at all, while removing every way
    /// for user input to become an operator.
    /// </summary>
    private static string SanitizeForFts(string term)
    {
        var builder = new StringBuilder(term.Length);

        foreach (var c in term)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '\'')
            {
                builder.Append(c);
            }
            else if (c is '-' or '.' or '/' or '@' or ':')
            {
                // Keep the pieces of URLs, paths and emails apart rather than glued together.
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value.Trim();
    }

    /// <summary>
    /// Accepts absolute dates, relative offsets (<c>7d</c>, <c>36h</c>) and the words people
    /// actually type. Returns local-midnight boundaries for day-granularity input, since
    /// "after:yesterday" means the whole of yesterday, not this time yesterday.
    /// </summary>
    internal static DateTimeOffset? ParseDate(string value, bool isLowerBound)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().ToLowerInvariant();
        var today = DateTimeOffset.Now.Date;

        switch (value)
        {
            case "today":
                return isLowerBound ? StartOfDay(today) : StartOfDay(today.AddDays(1));
            case "yesterday":
                return isLowerBound ? StartOfDay(today.AddDays(-1)) : StartOfDay(today);
            case "week" or "thisweek" or "lastweek":
                return StartOfDay(today.AddDays(-7));
            case "month" or "thismonth" or "lastmonth":
                return StartOfDay(today.AddMonths(-1));
            case "now":
                return DateTimeOffset.Now;
        }

        // Relative form: a count followed by a unit, always measured back from now.
        if (value.Length > 1 && char.IsDigit(value[0]))
        {
            var unit = value[^1];
            if (int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                return unit switch
                {
                    'd' => DateTimeOffset.Now.AddDays(-amount),
                    'h' => DateTimeOffset.Now.AddHours(-amount),
                    'm' => DateTimeOffset.Now.AddMinutes(-amount),
                    'w' => DateTimeOffset.Now.AddDays(-amount * 7),
                    _ => null
                };
            }
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            // A bare date used as an upper bound should include that whole day.
            var dateOnly = parsed.TimeOfDay == TimeSpan.Zero;
            return !isLowerBound && dateOnly ? parsed.AddDays(1) : parsed;
        }

        return null;
    }

    private static DateTimeOffset StartOfDay(DateTime day) => new(day, DateTimeOffset.Now.Offset);
}
