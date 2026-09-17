using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using RecallOS.Core.Storage;

namespace RecallOS.Core.Search;

/// <summary>
/// The retrieval engine: FTS5 keyword matching, brute-force vector similarity, and a
/// blend of the two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyword</b> is BM25 over the FTS5 index. It is exact, fast and explains itself, and
/// it is the right default for the common case where the user remembers an actual word
/// they saw.
/// </para>
/// <para>
/// <b>Semantic</b> scans stored chunk vectors and scores by cosine similarity. It has no
/// index -- every vector is compared. That is a deliberate choice at this scale: at 256
/// dimensions a SIMD dot product is a few nanoseconds, so a few hundred thousand chunks
/// scan in well under a second, and an ANN index would add a structure to maintain,
/// rebuild and corrupt for no gain the user could perceive. If a store grows past the
/// point where that holds, this method is the one place that has to change.
/// </para>
/// <para>
/// <b>Hybrid</b> is the default because the two fail in opposite directions: keyword
/// returns nothing when the user misremembers the wording, semantic returns something
/// vaguely related when the user knows exactly what they want. Scores are min-max
/// normalised within each result set before blending, because BM25 and cosine are not on
/// comparable scales and mixing them raw would let whichever happened to be larger decide
/// the ranking.
/// </para>
/// </remarks>
public sealed class HybridSearchService : ISearchService
{
    /// <summary>Column weights for BM25: a match in a window title is worth more than one in body text.</summary>
    private const double TextWeight = 1.0;
    private const double TitleWeight = 4.0;
    private const double ProcessWeight = 2.0;

    /// <summary>How much each signal contributes in <see cref="SearchMode.Hybrid"/>.</summary>
    private const double KeywordBlend = 0.65;
    private const double SemanticBlend = 0.35;

    /// <summary>Cosine below this is noise; including it would bury good hits under vague ones.</summary>
    private const double MinimumSemanticScore = 0.12;

    private readonly SqliteConnectionFactory _factory;
    private readonly IFrameRepository _repository;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(
        SqliteConnectionFactory factory,
        IFrameRepository repository,
        IEmbeddingProvider embeddings,
        ILogger<HybridSearchService>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HybridSearchService>.Instance;
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var stopwatch = Stopwatch.StartNew();

        // Nothing to match on: this is "show me my history", not a failed search.
        if (query.IsEmpty)
        {
            var recent = await _repository.GetRecentAsync(query.Limit, query.Offset, cancellationToken)
                .ConfigureAwait(false);

            return new SearchResult
            {
                Hits = recent.Select(f => ToHit(f, 0, 0, 0, query.Terms)).ToArray(),
                Mode = query.Mode,
                TotalMatches = recent.Count,
                Elapsed = stopwatch.Elapsed
            };
        }

        try
        {
            var result = query.Mode switch
            {
                SearchMode.Keyword => await KeywordOnlyAsync(query, cancellationToken).ConfigureAwait(false),
                SearchMode.Semantic => await SemanticOnlyAsync(query, cancellationToken).ConfigureAwait(false),
                _ => await HybridAsync(query, cancellationToken).ConfigureAwait(false)
            };

            return new SearchResult
            {
                Hits = result.Hits,
                Mode = query.Mode,
                TotalMatches = result.Hits.Count,
                Elapsed = stopwatch.Elapsed,
                Warning = result.Warning
            };
        }
        catch (SqliteException ex)
        {
            // A malformed MATCH expression should degrade to a substring scan, not show the
            // user a database error for something they typed.
            _logger.LogWarning(ex, "FTS query failed; falling back to a LIKE scan.");

            var fallback = await LikeFallbackAsync(query, cancellationToken).ConfigureAwait(false);
            return new SearchResult
            {
                Hits = fallback,
                Mode = SearchMode.Keyword,
                TotalMatches = fallback.Count,
                Elapsed = stopwatch.Elapsed,
                Warning = "That query could not be indexed, so a slower plain-text scan was used."
            };
        }
    }

    // ---- modes ----------------------------------------------------------------------

    private async Task<(IReadOnlyList<SearchHit> Hits, string? Warning)> KeywordOnlyAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        var scored = await RunKeywordAsync(query, query.Limit, cancellationToken).ConfigureAwait(false);
        var normalized = Normalize(scored.Select(s => s.Score).ToArray());

        var hits = scored
            .Select((s, i) => ToHit(s.Frame, normalized[i], normalized[i], 0, query.Terms))
            .ToArray();

        return (hits, null);
    }

    private async Task<(IReadOnlyList<SearchHit> Hits, string? Warning)> SemanticOnlyAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Terms.Count == 0)
        {
            // Filters with no words: semantics have nothing to rank, so just apply them.
            return (await LikeFallbackAsync(query, cancellationToken).ConfigureAwait(false), null);
        }

        var similarities = await ScoreBySimilarityAsync(query, cancellationToken).ConfigureAwait(false);
        if (similarities.Count == 0)
        {
            return (
                await LikeFallbackAsync(query, cancellationToken).ConfigureAwait(false),
                "No semantic index yet, so a plain-text scan was used. Indexing runs in the background.");
        }

        var hits = new List<SearchHit>();
        foreach (var (frameId, score) in similarities.OrderByDescending(p => p.Value).Take(query.Limit))
        {
            var frame = await _repository.GetAsync(frameId, cancellationToken).ConfigureAwait(false);
            if (frame is not null && PassesFilters(frame, query))
            {
                hits.Add(ToHit(frame, score, 0, score, query.Terms));
            }
        }

        return (hits, null);
    }

    private async Task<(IReadOnlyList<SearchHit> Hits, string? Warning)> HybridAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        // Over-fetch on the keyword side: the blend can promote a result that BM25 ranked
        // outside the requested page, and it cannot promote what was never retrieved.
        var keyword = await RunKeywordAsync(query, query.Limit * 2, cancellationToken).ConfigureAwait(false);
        var keywordNormalized = Normalize(keyword.Select(k => k.Score).ToArray());

        var semantic = query.Terms.Count > 0
            ? await ScoreBySimilarityAsync(query, cancellationToken).ConfigureAwait(false)
            : new Dictionary<long, double>();

        var frames = new Dictionary<long, CaptureFrame>();
        var keywordScores = new Dictionary<long, double>();

        for (var i = 0; i < keyword.Count; i++)
        {
            frames[keyword[i].Frame.Id] = keyword[i].Frame;
            keywordScores[keyword[i].Frame.Id] = keywordNormalized[i];
        }

        // Semantic-only frames still deserve a place; they are the recall the keyword pass missed.
        foreach (var frameId in semantic.Keys)
        {
            if (!frames.ContainsKey(frameId))
            {
                var frame = await _repository.GetAsync(frameId, cancellationToken).ConfigureAwait(false);
                if (frame is not null && PassesFilters(frame, query))
                {
                    frames[frameId] = frame;
                }
            }
        }

        var hits = new List<SearchHit>(frames.Count);

        foreach (var (frameId, frame) in frames)
        {
            var keywordScore = keywordScores.GetValueOrDefault(frameId);
            var semanticScore = semantic.GetValueOrDefault(frameId);
            var blended = (keywordScore * KeywordBlend) + (semanticScore * SemanticBlend);

            // Agreement between two independent signals is stronger evidence than either
            // alone, so a frame both passes found gets a modest lift.
            if (keywordScore > 0 && semanticScore > 0)
            {
                blended += 0.1 * Math.Min(keywordScore, semanticScore);
            }

            hits.Add(ToHit(frame, blended, keywordScore, semanticScore, query.Terms));
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Frame.CapturedAt)
            .Take(query.Limit)
            .ToArray();

        return (ordered, null);
    }

    // ---- keyword --------------------------------------------------------------------

    private async Task<IReadOnlyList<(CaptureFrame Frame, double Score)>> RunKeywordAsync(
        SearchQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (query.MatchExpression is null)
        {
            var filtered = await LikeFallbackAsync(query, cancellationToken).ConfigureAwait(false);
            return filtered.Select(h => (h.Frame, 0d)).ToArray();
        }

        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();

        var sql = new StringBuilder($"""
            SELECT {FrameRepository.ProjectionFor("f")}, bm25(frames_fts, {TextWeight}, {TitleWeight}, {ProcessWeight}) AS rank
              FROM frames_fts
              JOIN frames f ON f.id = frames_fts.rowid
             WHERE frames_fts MATCH $match
               AND f.is_deleted = 0
            """);

        AppendFilters(sql, command, query, "f");

        // bm25() is negative with better matches more negative, so ascending is best-first.
        sql.Append(" ORDER BY rank ASC LIMIT $limit;");

        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$match", query.MatchExpression);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<(CaptureFrame, double)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var frame = FrameRepository.MapFrame(reader);

            // Flip the sign so that, everywhere above this line, higher means better.
            var rank = reader.IsDBNull(22) ? 0d : -reader.GetDouble(22);
            results.Add((frame, rank));
        }

        return results;
    }

    /// <summary>
    /// Substring matching for the cases FTS cannot serve: a malformed expression, a
    /// filter-only query, or a store with no index yet.
    /// </summary>
    private async Task<IReadOnlyList<SearchHit>> LikeFallbackAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();

        var sql = new StringBuilder($"""
            SELECT {FrameRepository.ProjectionFor("f")}
              FROM frames f
             WHERE f.is_deleted = 0
            """);

        for (var i = 0; i < query.Terms.Count; i++)
        {
            sql.Append($" AND (f.text LIKE $term{i} ESCAPE '\\' OR f.window_title LIKE $term{i} ESCAPE '\\')");
            command.Parameters.AddWithValue($"$term{i}", $"%{Escape(query.Terms[i])}%");
        }

        AppendFilters(sql, command, query, "f");
        sql.Append(" ORDER BY f.captured_at_ticks DESC LIMIT $limit;");

        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", query.Limit);

        var hits = new List<SearchHit>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hits.Add(ToHit(FrameRepository.MapFrame(reader), 0, 0, 0, query.Terms));
        }

        return hits;
    }

    /// <summary>Append the structured filters shared by every SQL path.</summary>
    private static void AppendFilters(StringBuilder sql, SqliteCommand command, SearchQuery query, string alias)
    {
        if (query.Apps.Count > 0)
        {
            var names = query.Apps.Select((_, i) => $"$app{i}");
            sql.Append($" AND LOWER({alias}.process_name) IN ({string.Join(", ", names)})");

            for (var i = 0; i < query.Apps.Count; i++)
            {
                // The stored name never carries the extension, so neither should the filter.
                var app = query.Apps[i].ToLowerInvariant();
                if (app.EndsWith(".exe", StringComparison.Ordinal))
                {
                    app = app[..^4];
                }

                command.Parameters.AddWithValue($"$app{i}", app);
            }
        }

        for (var i = 0; i < query.Titles.Count; i++)
        {
            sql.Append($" AND {alias}.window_title LIKE $title{i} ESCAPE '\\'");
            command.Parameters.AddWithValue($"$title{i}", $"%{Escape(query.Titles[i])}%");
        }

        if (query.After is { } after)
        {
            sql.Append($" AND {alias}.captured_at_ticks >= $after");
            command.Parameters.AddWithValue("$after", after.UtcTicks);
        }

        if (query.Before is { } before)
        {
            sql.Append($" AND {alias}.captured_at_ticks < $before");
            command.Parameters.AddWithValue("$before", before.UtcTicks);
        }
    }

    // ---- semantic -------------------------------------------------------------------

    /// <summary>
    /// Score every stored chunk against the query vector and keep, for each frame, the
    /// score of its single best chunk. Taking the best rather than the mean matters: one
    /// highly relevant paragraph on an otherwise unrelated screen is exactly the thing the
    /// user is trying to find, and averaging would dilute it away.
    /// </summary>
    private async Task<Dictionary<long, double>> ScoreBySimilarityAsync(
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        var queryVector = _embeddings.Embed(query.Text);
        var best = new Dictionary<long, double>();

        await foreach (var record in _repository
                           .StreamEmbeddingsAsync(_embeddings.ModelId, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (record.Vector.Length != queryVector.Length)
            {
                // A vector from a different model version; the id filter should have
                // excluded it, so skip rather than compare nonsense.
                continue;
            }

            var score = VectorMath.Dot(queryVector.Span, record.Vector);
            if (score < MinimumSemanticScore)
            {
                continue;
            }

            if (!best.TryGetValue(record.FrameId, out var existing) || score > existing)
            {
                best[record.FrameId] = score;
            }
        }

        return best;
    }

    // ---- suggestions ----------------------------------------------------------------

    public async Task<IReadOnlyList<string>> SuggestAsync(
        string prefix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 2)
        {
            return Array.Empty<string>();
        }

        // Suggestions come from window titles rather than body text: they are short, they
        // are what the user is most likely to half-remember, and they read as labels.
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT window_title, COUNT(*) AS n
              FROM frames
             WHERE is_deleted = 0
               AND window_title IS NOT NULL
               AND window_title LIKE $prefix ESCAPE '\'
             GROUP BY window_title
             ORDER BY n DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$prefix", $"%{Escape(prefix)}%");
        command.Parameters.AddWithValue("$limit", limit);

        var suggestions = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            suggestions.Add(reader.GetString(0));
        }

        return suggestions;
    }

    // ---- helpers --------------------------------------------------------------------

    private static SearchHit ToHit(
        CaptureFrame frame,
        double score,
        double keywordScore,
        double semanticScore,
        IReadOnlyList<string> terms)
    {
        var snippet = SnippetBuilder.Build(frame.Text, terms);

        return new SearchHit
        {
            Frame = frame,
            Score = score,
            KeywordScore = keywordScore,
            SemanticScore = semanticScore,
            Snippet = snippet.Text,
            Highlights = snippet.Highlights
        };
    }

    /// <summary>
    /// Min-max scaling into 0..1. BM25 values are unbounded and depend on corpus
    /// statistics, so only their order within one result set is meaningful -- which is
    /// exactly what this preserves while making them blendable with cosine.
    /// </summary>
    private static double[] Normalize(double[] scores)
    {
        if (scores.Length == 0)
        {
            return scores;
        }

        var min = scores.Min();
        var max = scores.Max();
        var range = max - min;

        if (range <= double.Epsilon)
        {
            // Every result scored the same; they are all equally good, not all worthless.
            return scores.Select(_ => 1d).ToArray();
        }

        return scores.Select(s => (s - min) / range).ToArray();
    }

    private static bool PassesFilters(CaptureFrame frame, SearchQuery query)
    {
        if (query.Apps.Count > 0)
        {
            var process = frame.ProcessName ?? string.Empty;
            if (!query.Apps.Any(a => process.Equals(
                    a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (query.Titles.Count > 0)
        {
            var title = frame.WindowTitle ?? string.Empty;
            if (!query.Titles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (query.After is { } after && frame.CapturedAt < after)
        {
            return false;
        }

        return query.Before is not { } before || frame.CapturedAt < before;
    }

    /// <summary>Neutralise LIKE wildcards so a literal % or _ in a query matches itself.</summary>
    private static string Escape(string value) =>
        value.Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}
