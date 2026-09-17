using RecallOS.Core.Models;

namespace RecallOS.Core.Abstractions;

/// <summary>Turns a user query into ranked frames.</summary>
public interface ISearchService
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Terms worth suggesting while the user types, drawn from indexed content.</summary>
    Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Maps text to a vector. The seam behind which a real embedding model can land later.</summary>
public interface IEmbeddingProvider
{
    /// <summary>Identifier stored with every vector so mismatched models are never compared.</summary>
    string ModelId { get; }

    int Dimensions { get; }

    ReadOnlyMemory<float> Embed(string text);

    /// <summary>Batch form; implementations may parallelise.</summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

/// <summary>Assigns an activity label to a frame, for timeline grouping and workflow replay.</summary>
public interface IIntentClassifier
{
    IntentPrediction Classify(CaptureFrame frame);
}

/// <summary>A label and how much the classifier believes it.</summary>
public readonly record struct IntentPrediction(string Label, double Confidence)
{
    public static readonly IntentPrediction Unknown = new("Unknown", 0d);
}
