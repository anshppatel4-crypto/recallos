namespace RecallOS.Core.Models;

/// <summary>
/// What RecallOS is currently holding. Surfaced in the UI because a system that
/// silently records everything has to be able to answer "what do you have on me?".
/// </summary>
public sealed class StorageStatistics
{
    public int FrameCount { get; init; }

    public int ChunkCount { get; init; }

    public int EmbeddingCount { get; init; }

    public int PendingOcrCount { get; init; }

    public int FailedOcrCount { get; init; }

    /// <summary>Size of the SQLite database and its write-ahead log.</summary>
    public long DatabaseBytes { get; init; }

    /// <summary>Total size of stored images and thumbnails.</summary>
    public long ImageBytes { get; init; }

    public DateTimeOffset? OldestFrame { get; init; }

    public DateTimeOffset? NewestFrame { get; init; }

    public long TotalBytes => DatabaseBytes + ImageBytes;
}
