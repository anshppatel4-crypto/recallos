using RecallOS.Core.Models;

namespace RecallOS.Core.Abstractions;

/// <summary>Persistence for frames, their text chunks and their embeddings.</summary>
public interface IFrameRepository
{
    Task<CaptureFrame> InsertAsync(CaptureFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Write back OCR output and re-index the frame text.</summary>
    Task UpdateOcrAsync(
        long frameId,
        OcrStatus status,
        string text,
        double confidence,
        string? engine,
        string? error,
        CancellationToken cancellationToken = default);

    Task UpdateIntentAsync(long frameId, string? label, double confidence, CancellationToken cancellationToken = default);

    Task<CaptureFrame?> GetAsync(long frameId, CancellationToken cancellationToken = default);

    /// <summary>Most recent frames first.</summary>
    Task<IReadOnlyList<CaptureFrame>> GetRecentAsync(int limit, int offset = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureFrame>> GetRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>The frame immediately before or after this one in time, for replay stepping.</summary>
    Task<CaptureFrame?> GetNeighborAsync(long frameId, bool forward, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureFrame>> GetPendingOcrAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Perceptual hash of the newest stored frame, used for change detection.</summary>
    Task<ulong?> GetLatestHashAsync(CancellationToken cancellationToken = default);

    Task ReplaceChunksAsync(long frameId, IReadOnlyList<TextChunk> chunks, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TextChunk>> GetChunksAsync(long frameId, CancellationToken cancellationToken = default);

    Task SaveEmbeddingAsync(long chunkId, string model, ReadOnlyMemory<float> vector, CancellationToken cancellationToken = default);

    /// <summary>Stream every stored vector for brute-force similarity scanning.</summary>
    IAsyncEnumerable<EmbeddingRecord> StreamEmbeddingsAsync(string model, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> GetChunkIdsWithoutEmbeddingsAsync(string model, int limit, CancellationToken cancellationToken = default);

    Task<TextChunk?> GetChunkAsync(long chunkId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TimelineBucket>> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimelineGranularity granularity,
        CancellationToken cancellationToken = default);

    Task<StorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>Delete frames and return the store-relative files that should now be erased.</summary>
    Task<IReadOnlyList<string>> DeleteAsync(IReadOnlyList<long> frameIds, CancellationToken cancellationToken = default);

    /// <summary>Frames older than the cutoff, oldest first, for retention sweeps.</summary>
    Task<IReadOnlyList<CaptureFrame>> GetExpiredAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureFrame>> GetOldestAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Distinct process names seen in the store, for filter suggestions.</summary>
    Task<IReadOnlyList<string>> GetKnownAppsAsync(CancellationToken cancellationToken = default);

    Task VacuumAsync(CancellationToken cancellationToken = default);
}

/// <summary>A stored vector together with the chunk and frame it belongs to.</summary>
public sealed record EmbeddingRecord(long ChunkId, long FrameId, float[] Vector);
