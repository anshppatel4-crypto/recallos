using System.Text;
using RecallOS.Core.Abstractions;

namespace RecallOS.Core.Intelligence;

/// <summary>
/// A local, dependency-free embedding built by feature hashing.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is.</b> Each token is hashed to a dimension and a sign, then accumulated
/// with sub-linear term weighting and L2-normalised -- a random projection of a bag of
/// words and word bigrams, plus character trigrams so that typos and inflections
/// ("deploying" / "deployment") still overlap.
/// </para>
/// <para>
/// <b>What this is not.</b> It carries no learned semantics. It will not match "car" to
/// "automobile", because nothing in it has ever seen the two used alike. What it does buy
/// is fuzzy lexical matching that degrades gracefully where exact FTS matching returns
/// nothing, at zero install cost and a few microseconds per chunk.
/// </para>
/// <para>
/// <b>Why it is shaped this way.</b> <see cref="ModelId"/> is stored on every vector, so
/// dropping in a real sentence-transformer later means registering a new provider and
/// letting the indexer backfill under the new id. Old and new vectors coexist and are
/// never compared, so the upgrade needs no migration and no reindex-before-you-can-search
/// downtime.
/// </para>
/// </remarks>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>
    /// English function words carry no retrieval signal but appear in nearly every chunk,
    /// so they would otherwise dominate the vector and make everything look alike.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "has", "have",
        "he", "her", "his", "i", "in", "is", "it", "its", "of", "on", "or", "our", "she",
        "that", "the", "their", "them", "then", "there", "these", "they", "this", "to", "was",
        "we", "were", "what", "when", "which", "who", "will", "with", "you", "your"
    };

    private readonly int _dimensions;

    public HashingEmbeddingProvider(int dimensions = 256)
    {
        if (dimensions is < 32 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 32..4096.");
        }

        _dimensions = dimensions;
    }

    /// <summary>Versioned: a change to the tokenizer or weighting must not silently reuse old vectors.</summary>
    public string ModelId => $"hashing-v1-{_dimensions}";

    public int Dimensions => _dimensions;

    public ReadOnlyMemory<float> Embed(string text)
    {
        var vector = new float[_dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        var counts = new Dictionary<int, float>();
        var tokens = Tokenize(text);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            Accumulate(counts, token, 1.0f);

            // Bigrams give the vector a little word order, so "merge request" and
            // "request merge" are not identical.
            if (i + 1 < tokens.Count)
            {
                Accumulate(counts, $"{token}␟{tokens[i + 1]}", 0.5f);
            }

            // Character trigrams make matching robust to OCR slips and word endings.
            foreach (var trigram in CharacterTrigrams(token))
            {
                Accumulate(counts, trigram, 0.25f);
            }
        }

        foreach (var (feature, weight) in counts)
        {
            var index = (int)((uint)feature % (uint)_dimensions);

            // The sign comes from the hash's high bit, which is independent of the low bits
            // that chose the index. Without a sign, collisions could only ever add, so
            // unrelated features would reinforce each other and every similarity would drift
            // upward. With it, collisions cancel on average instead.
            var sign = (feature & (1 << 31)) == 0 ? 1f : -1f;

            // Sub-linear scaling: the tenth occurrence of a word says much less than the
            // first. log(1 + w) rather than 1 + log(w), so the fractional weights used for
            // bigrams and trigrams stay positive instead of flipping the feature's sign.
            vector[index] += sign * MathF.Log(1f + weight);
        }

        VectorMath.NormalizeInPlace(vector);
        return vector;
    }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var results = new ReadOnlyMemory<float>[texts.Count];

        // Embedding is pure and CPU-bound, so a parallel loop is both safe and the whole
        // point: backfilling a large store should use the cores that are sitting idle.
        Parallel.For(
            0,
            texts.Count,
            new ParallelOptions { CancellationToken = cancellationToken },
            i => results[i] = Embed(texts[i]));

        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
    }

    private static void Accumulate(Dictionary<int, float> counts, string feature, float weight)
    {
        var hash = StableHash(feature);
        counts[hash] = counts.TryGetValue(hash, out var existing) ? existing + weight : weight;
    }

    /// <summary>Lowercased alphanumeric tokens, stop words and single characters removed.</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                continue;
            }

            AddToken(tokens, builder);
        }

        AddToken(tokens, builder);
        return tokens;

        static void AddToken(List<string> tokens, StringBuilder builder)
        {
            if (builder.Length > 1)
            {
                var token = builder.ToString();
                if (!StopWords.Contains(token))
                {
                    tokens.Add(token);
                }
            }

            builder.Clear();
        }
    }

    private static IEnumerable<string> CharacterTrigrams(string token)
    {
        // Below four characters a trigram is nearly the whole token, so it adds noise
        // rather than robustness.
        if (token.Length < 4)
        {
            yield break;
        }

        for (var i = 0; i <= token.Length - 3; i++)
        {
            yield return token.Substring(i, 3);
        }
    }

    /// <summary>
    /// FNV-1a. Chosen because it is stable across processes and runtimes, which matters:
    /// <see cref="string.GetHashCode()"/> is randomised per process, so vectors written
    /// today would not be comparable with vectors written after a restart.
    /// </summary>
    internal static int StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= prime;
        }

        return unchecked((int)hash);
    }
}
