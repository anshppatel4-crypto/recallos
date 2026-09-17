using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;

namespace RecallOS.Core.Storage;

/// <summary>
/// SQLite-backed persistence for frames, chunks and embeddings.
/// </summary>
/// <remarks>
/// All reads open their own pooled connection and run concurrently against WAL; all writes
/// go through the factory write scope, which serialises them. Nothing here caches, so the
/// database stays the single source of truth and a second RecallOS window would see the
/// same data.
/// </remarks>
public sealed class FrameRepository : IFrameRepository
{
    /// <summary>
    /// The projection every frame read shares. Keeping one list means the ordinals in
    /// <see cref="MapFrame"/> are valid for every query in this class.
    /// </summary>
    internal const string FrameColumns = """
        id, captured_at_utc, image_path, thumbnail_path, width, height, file_size_bytes,
        capture_target, capture_origin, window_title, process_name, display_name,
        perceptual_hash, ocr_status, text, ocr_confidence, ocr_engine, ocr_error,
        ocr_completed_at, intent_label, intent_confidence, is_deleted
        """;

    /// <summary>
    /// The same projection, qualified with a table alias, for queries that join. Ordinals
    /// are preserved, so <see cref="MapFrame"/> reads either form.
    /// </summary>
    internal static string ProjectionFor(string alias) =>
        string.Join(
            ", ",
            FrameColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(column => $"{alias}.{column.Trim()}"));

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<FrameRepository> _logger;

    public FrameRepository(SqliteConnectionFactory factory, ILogger<FrameRepository>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FrameRepository>.Instance;
    }

    // ---- writes ---------------------------------------------------------------------

    public async Task<CaptureFrame> InsertAsync(CaptureFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        using var command = scope.CreateCommand("""
            INSERT INTO frames (
                captured_at_utc, captured_at_ticks, image_path, thumbnail_path, width, height,
                file_size_bytes, capture_target, capture_origin, window_title, process_name,
                display_name, perceptual_hash, ocr_status, text, text_length, ocr_confidence,
                ocr_engine, ocr_error, ocr_completed_at, intent_label, intent_confidence)
            VALUES (
                $capturedAt, $ticks, $imagePath, $thumbnailPath, $width, $height,
                $fileSize, $target, $origin, $windowTitle, $processName,
                $displayName, $hash, $ocrStatus, $text, $textLength, $ocrConfidence,
                $ocrEngine, $ocrError, $ocrCompletedAt, $intentLabel, $intentConfidence);
            SELECT last_insert_rowid();
            """);

        var capturedAt = frame.CapturedAt.ToUniversalTime();
        command
            .WithParameter("$capturedAt", Iso(capturedAt))
            .WithParameter("$ticks", capturedAt.UtcTicks)
            .WithParameter("$imagePath", frame.ImagePath)
            .WithParameter("$thumbnailPath", frame.ThumbnailPath)
            .WithParameter("$width", frame.Width)
            .WithParameter("$height", frame.Height)
            .WithParameter("$fileSize", frame.FileSizeBytes)
            .WithParameter("$target", (int)frame.Target)
            .WithParameter("$origin", (int)frame.Origin)
            .WithParameter("$windowTitle", frame.WindowTitle)
            .WithParameter("$processName", frame.ProcessName)
            .WithParameter("$displayName", frame.DisplayName)
            // SQLite integers are signed; round-trip the hash bit pattern rather than the value.
            .WithParameter("$hash", unchecked((long)frame.PerceptualHash))
            .WithParameter("$ocrStatus", (int)frame.OcrStatus)
            .WithParameter("$text", frame.Text)
            .WithParameter("$textLength", frame.Text.Length)
            .WithParameter("$ocrConfidence", frame.OcrConfidence)
            .WithParameter("$ocrEngine", frame.OcrEngine)
            .WithParameter("$ocrError", frame.OcrError)
            .WithParameter("$ocrCompletedAt", frame.OcrCompletedAt is { } done ? Iso(done) : null)
            .WithParameter("$intentLabel", frame.IntentLabel)
            .WithParameter("$intentConfidence", frame.IntentConfidence);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        return new CaptureFrame
        {
            Id = id,
            CapturedAt = capturedAt,
            ImagePath = frame.ImagePath,
            ThumbnailPath = frame.ThumbnailPath,
            Width = frame.Width,
            Height = frame.Height,
            FileSizeBytes = frame.FileSizeBytes,
            Target = frame.Target,
            Origin = frame.Origin,
            WindowTitle = frame.WindowTitle,
            ProcessName = frame.ProcessName,
            DisplayName = frame.DisplayName,
            PerceptualHash = frame.PerceptualHash,
            OcrStatus = frame.OcrStatus,
            Text = frame.Text,
            OcrConfidence = frame.OcrConfidence,
            OcrEngine = frame.OcrEngine,
            OcrError = frame.OcrError,
            OcrCompletedAt = frame.OcrCompletedAt,
            IntentLabel = frame.IntentLabel,
            IntentConfidence = frame.IntentConfidence
        };
    }

    public async Task UpdateOcrAsync(
        long frameId,
        OcrStatus status,
        string text,
        double confidence,
        string? engine,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        using var command = scope.CreateCommand("""
            UPDATE frames
               SET ocr_status = $status,
                   text = $text,
                   text_length = $textLength,
                   ocr_confidence = $confidence,
                   ocr_engine = $engine,
                   ocr_error = $error,
                   ocr_completed_at = $completedAt
             WHERE id = $id;
            """);

        command
            .WithParameter("$status", (int)status)
            .WithParameter("$text", text ?? string.Empty)
            .WithParameter("$textLength", text?.Length ?? 0)
            .WithParameter("$confidence", confidence)
            .WithParameter("$engine", engine)
            .WithParameter("$error", error)
            .WithParameter("$completedAt", Iso(DateTimeOffset.UtcNow))
            .WithParameter("$id", frameId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateIntentAsync(
        long frameId,
        string? label,
        double confidence,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        using var command = scope.CreateCommand(
            "UPDATE frames SET intent_label = $label, intent_confidence = $confidence WHERE id = $id;");

        command
            .WithParameter("$label", label)
            .WithParameter("$confidence", confidence)
            .WithParameter("$id", frameId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceChunksAsync(
        long frameId,
        IReadOnlyList<TextChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = scope.BeginTransaction();

        using (var delete = scope.Connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM chunks WHERE frame_id = $frameId;";
            delete.Parameters.AddWithValue("$frameId", frameId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (chunks.Count > 0)
        {
            // One prepared command reused across the batch: preparing per row dominates the
            // cost when a dense screen yields a few dozen chunks.
            using var insert = scope.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO chunks (frame_id, ordinal, text, confidence, bounds_x, bounds_y, bounds_w, bounds_h)
                VALUES ($frameId, $ordinal, $text, $confidence, $x, $y, $w, $h);
                """;

            var pFrame = insert.Parameters.Add("$frameId", SqliteType.Integer);
            var pOrdinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
            var pText = insert.Parameters.Add("$text", SqliteType.Text);
            var pConfidence = insert.Parameters.Add("$confidence", SqliteType.Real);
            var pX = insert.Parameters.Add("$x", SqliteType.Integer);
            var pY = insert.Parameters.Add("$y", SqliteType.Integer);
            var pW = insert.Parameters.Add("$w", SqliteType.Integer);
            var pH = insert.Parameters.Add("$h", SqliteType.Integer);

            foreach (var chunk in chunks)
            {
                pFrame.Value = frameId;
                pOrdinal.Value = chunk.Ordinal;
                pText.Value = chunk.Text;
                pConfidence.Value = chunk.Confidence;
                pX.Value = chunk.Bounds?.X ?? (object)DBNull.Value;
                pY.Value = chunk.Bounds?.Y ?? (object)DBNull.Value;
                pW.Value = chunk.Bounds?.Width ?? (object)DBNull.Value;
                pH.Value = chunk.Bounds?.Height ?? (object)DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.Commit();
    }

    public async Task SaveEmbeddingAsync(
        long chunkId,
        string model,
        ReadOnlyMemory<float> vector,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        using var command = scope.CreateCommand("""
            INSERT INTO embeddings (chunk_id, model, dimensions, vector)
            VALUES ($chunkId, $model, $dimensions, $vector)
            ON CONFLICT (chunk_id, model) DO UPDATE SET
                dimensions = excluded.dimensions,
                vector = excluded.vector;
            """);

        command
            .WithParameter("$chunkId", chunkId)
            .WithParameter("$model", model)
            .WithParameter("$dimensions", vector.Length)
            .WithParameter("$vector", VectorCodec.ToBlob(vector.Span));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(
        IReadOnlyList<long> frameIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frameIds);
        if (frameIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(frameIds.Count);

        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = scope.BeginTransaction();

        // Read the file paths before the rows go: afterwards there is nothing left to
        // tell the caller which files to erase.
        using (var select = scope.Connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT image_path FROM frames WHERE id IN ({Placeholders(frameIds.Count)});";
            BindIds(select, frameIds);

            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                paths.Add(reader.GetString(0));
            }
        }

        using (var delete = scope.Connection.CreateCommand())
        {
            delete.Transaction = transaction;
            // chunks and embeddings fall away through ON DELETE CASCADE; the FTS index is
            // cleaned by the delete trigger.
            delete.CommandText = $"DELETE FROM frames WHERE id IN ({Placeholders(frameIds.Count)});";
            BindIds(delete, frameIds);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        _logger.LogInformation("Deleted {Count} frames.", frameIds.Count);
        return paths;
    }

    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);

        using (var optimize = scope.CreateCommand("INSERT INTO frames_fts (frames_fts) VALUES ('optimize');"))
        {
            await optimize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // VACUUM cannot run inside a transaction and rewrites the whole file, so it is only
        // ever called from the maintenance sweep, never on the capture path.
        using var vacuum = scope.CreateCommand("VACUUM;");
        await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- reads ----------------------------------------------------------------------

    public async Task<CaptureFrame?> GetAsync(long frameId, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FrameColumns} FROM frames WHERE id = $id;";
        command.Parameters.AddWithValue("$id", frameId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapFrame(reader) : null;
    }

    public async Task<IReadOnlyList<CaptureFrame>> GetRecentAsync(
        int limit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FrameColumns} FROM frames
             WHERE is_deleted = 0
             ORDER BY captured_at_ticks DESC
             LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        return await ReadFramesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaptureFrame>> GetRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FrameColumns} FROM frames
             WHERE is_deleted = 0
               AND captured_at_ticks >= $from
               AND captured_at_ticks < $to
             ORDER BY captured_at_ticks DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", from.UtcTicks);
        command.Parameters.AddWithValue("$to", to.UtcTicks);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadFramesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CaptureFrame?> GetNeighborAsync(
        long frameId,
        bool forward,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();

        // Ordering by (ticks, id) breaks ties between frames captured in the same tick, so
        // stepping forward then back always returns to the same frame.
        command.CommandText = forward
            ? $"""
               SELECT {FrameColumns} FROM frames
                WHERE is_deleted = 0
                  AND (captured_at_ticks, id) > (SELECT captured_at_ticks, id FROM frames WHERE id = $id)
                ORDER BY captured_at_ticks ASC, id ASC
                LIMIT 1;
               """
            : $"""
               SELECT {FrameColumns} FROM frames
                WHERE is_deleted = 0
                  AND (captured_at_ticks, id) < (SELECT captured_at_ticks, id FROM frames WHERE id = $id)
                ORDER BY captured_at_ticks DESC, id DESC
                LIMIT 1;
               """;
        command.Parameters.AddWithValue("$id", frameId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapFrame(reader) : null;
    }

    public async Task<IReadOnlyList<CaptureFrame>> GetPendingOcrAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        // Oldest first: a backlog should drain in the order it accumulated.
        command.CommandText = $"""
            SELECT {FrameColumns} FROM frames
             WHERE is_deleted = 0 AND ocr_status = $pending
             ORDER BY captured_at_ticks ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pending", (int)OcrStatus.Pending);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadFramesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ulong?> GetLatestHashAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT perceptual_hash FROM frames
             WHERE is_deleted = 0
             ORDER BY captured_at_ticks DESC
             LIMIT 1;
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : unchecked((ulong)Convert.ToInt64(result));
    }

    public async Task<IReadOnlyList<TextChunk>> GetChunksAsync(
        long frameId,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, frame_id, ordinal, text, confidence, bounds_x, bounds_y, bounds_w, bounds_h
              FROM chunks
             WHERE frame_id = $frameId
             ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$frameId", frameId);

        var chunks = new List<TextChunk>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(MapChunk(reader));
        }

        return chunks;
    }

    public async Task<TextChunk?> GetChunkAsync(long chunkId, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, frame_id, ordinal, text, confidence, bounds_x, bounds_y, bounds_w, bounds_h
              FROM chunks WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", chunkId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapChunk(reader) : null;
    }

    public async IAsyncEnumerable<EmbeddingRecord> StreamEmbeddingsAsync(
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        // Joined to frames so soft-deleted frames never contribute to a semantic result.
        command.CommandText = """
            SELECT e.chunk_id, c.frame_id, e.vector
              FROM embeddings e
              JOIN chunks c ON c.id = e.chunk_id
              JOIN frames f ON f.id = c.frame_id
             WHERE e.model = $model AND f.is_deleted = 0;
            """;
        command.Parameters.AddWithValue("$model", model);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var blob = (byte[])reader.GetValue(2);
            yield return new EmbeddingRecord(reader.GetInt64(0), reader.GetInt64(1), VectorCodec.FromBlob(blob));
        }
    }

    public async Task<IReadOnlyList<long>> GetChunkIdsWithoutEmbeddingsAsync(
        string model,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id
              FROM chunks c
              JOIN frames f ON f.id = c.frame_id
             WHERE f.is_deleted = 0
               AND NOT EXISTS (
                     SELECT 1 FROM embeddings e
                      WHERE e.chunk_id = c.id AND e.model = $model)
             ORDER BY c.id DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$limit", limit);

        var ids = new List<long>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public async Task<IReadOnlyList<TimelineBucket>> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimelineGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        var bucketTicks = GranularityTicks(granularity);

        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();

        // Bucketing is integer division on the tick column, which lets SQLite group without
        // parsing a single timestamp string. The rows are projected into a CTE first so the
        // "most common app / intent in this bucket" subqueries can correlate on a real
        // column rather than on a select-list alias, which SQLite will not resolve there.
        command.CommandText = """
            WITH bucketed AS (
                SELECT captured_at_ticks / $bucket AS bucket, id, process_name, intent_label
                  FROM frames
                 WHERE is_deleted = 0
                   AND captured_at_ticks >= $from
                   AND captured_at_ticks < $to
            )
            SELECT b.bucket,
                   COUNT(*)  AS frame_count,
                   MIN(b.id) AS representative_id,
                   (SELECT x.process_name FROM bucketed x
                     WHERE x.bucket = b.bucket AND x.process_name IS NOT NULL
                     GROUP BY x.process_name ORDER BY COUNT(*) DESC LIMIT 1) AS top_app,
                   (SELECT y.intent_label FROM bucketed y
                     WHERE y.bucket = b.bucket AND y.intent_label IS NOT NULL
                     GROUP BY y.intent_label ORDER BY COUNT(*) DESC LIMIT 1) AS top_intent
              FROM bucketed b
             GROUP BY b.bucket
             ORDER BY b.bucket;
            """;
        command.Parameters.AddWithValue("$bucket", bucketTicks);
        command.Parameters.AddWithValue("$from", from.UtcTicks);
        command.Parameters.AddWithValue("$to", to.UtcTicks);

        var buckets = new List<TimelineBucket>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var startTicks = reader.GetInt64(0) * bucketTicks;
            buckets.Add(new TimelineBucket
            {
                Start = new DateTimeOffset(startTicks, TimeSpan.Zero).ToLocalTime(),
                Duration = TimeSpan.FromTicks(bucketTicks),
                FrameCount = reader.GetInt32(1),
                RepresentativeFrameId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                DominantApp = reader.GetNullableString(3),
                DominantIntent = reader.GetNullableString(4)
            });
        }

        return buckets;
    }

    public async Task<StorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM frames WHERE is_deleted = 0),
                (SELECT COUNT(*) FROM chunks),
                (SELECT COUNT(*) FROM embeddings),
                (SELECT COUNT(*) FROM frames WHERE is_deleted = 0 AND ocr_status = 0),
                (SELECT COUNT(*) FROM frames WHERE is_deleted = 0 AND ocr_status = 2),
                (SELECT MIN(captured_at_ticks) FROM frames WHERE is_deleted = 0),
                (SELECT MAX(captured_at_ticks) FROM frames WHERE is_deleted = 0),
                (SELECT COALESCE(SUM(file_size_bytes), 0) FROM frames WHERE is_deleted = 0);
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new StorageStatistics();
        }

        return new StorageStatistics
        {
            FrameCount = reader.GetInt32(0),
            ChunkCount = reader.GetInt32(1),
            EmbeddingCount = reader.GetInt32(2),
            PendingOcrCount = reader.GetInt32(3),
            FailedOcrCount = reader.GetInt32(4),
            OldestFrame = reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
            NewestFrame = reader.IsDBNull(6) ? null : new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
            ImageBytes = reader.GetInt64(7),
            DatabaseBytes = _factory.GetDatabaseSizeBytes()
        };
    }

    public async Task<IReadOnlyList<CaptureFrame>> GetExpiredAsync(
        DateTimeOffset cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FrameColumns} FROM frames
             WHERE captured_at_ticks < $cutoff
             ORDER BY captured_at_ticks ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.UtcTicks);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadFramesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaptureFrame>> GetOldestAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FrameColumns} FROM frames
             ORDER BY captured_at_ticks ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadFramesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetKnownAppsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _factory.OpenRead();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT process_name, COUNT(*) AS n
              FROM frames
             WHERE is_deleted = 0 AND process_name IS NOT NULL AND process_name <> ''
             GROUP BY process_name
             ORDER BY n DESC
             LIMIT 100;
            """;

        var apps = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            apps.Add(reader.GetString(0));
        }

        return apps;
    }

    // ---- mapping --------------------------------------------------------------------

    private static async Task<IReadOnlyList<CaptureFrame>> ReadFramesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var frames = new List<CaptureFrame>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            frames.Add(MapFrame(reader));
        }

        return frames;
    }

    /// <summary>Ordinals here must stay in step with <see cref="FrameColumns"/>.</summary>
    internal static CaptureFrame MapFrame(IDataRecord record) => new()
    {
        Id = record.GetInt64(0),
        CapturedAt = ParseIso(record.GetString(1)),
        ImagePath = record.GetString(2),
        ThumbnailPath = record.GetNullableString(3),
        Width = record.GetInt32OrDefault(4),
        Height = record.GetInt32OrDefault(5),
        FileSizeBytes = record.GetInt64OrDefault(6),
        Target = (CaptureTarget)record.GetInt32OrDefault(7),
        Origin = (CaptureOrigin)record.GetInt32OrDefault(8),
        WindowTitle = record.GetNullableString(9),
        ProcessName = record.GetNullableString(10),
        DisplayName = record.GetNullableString(11),
        PerceptualHash = unchecked((ulong)record.GetInt64OrDefault(12)),
        OcrStatus = (OcrStatus)record.GetInt32OrDefault(13),
        Text = record.IsDBNull(14) ? string.Empty : record.GetString(14),
        OcrConfidence = record.GetDoubleOrDefault(15),
        OcrEngine = record.GetNullableString(16),
        OcrError = record.GetNullableString(17),
        OcrCompletedAt = record.IsDBNull(18) ? null : ParseIso(record.GetString(18)),
        IntentLabel = record.GetNullableString(19),
        IntentConfidence = record.GetDoubleOrDefault(20),
        IsDeleted = record.GetInt32OrDefault(21) != 0
    };

    private static TextChunk MapChunk(IDataRecord record) => new()
    {
        Id = record.GetInt64(0),
        FrameId = record.GetInt64(1),
        Ordinal = record.GetInt32(2),
        Text = record.GetString(3),
        Confidence = record.GetDoubleOrDefault(4),
        Bounds = record.IsDBNull(5)
            ? null
            : new PixelRect(record.GetInt32(5), record.GetInt32(6), record.GetInt32(7), record.GetInt32(8))
    };

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static long GranularityTicks(TimelineGranularity granularity) => granularity switch
    {
        TimelineGranularity.Minute => TimeSpan.TicksPerMinute,
        TimelineGranularity.QuarterHour => TimeSpan.TicksPerMinute * 15,
        TimelineGranularity.Hour => TimeSpan.TicksPerHour,
        _ => TimeSpan.TicksPerDay
    };

    private static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"$id{i}"));

    private static void BindIds(SqliteCommand command, IReadOnlyList<long> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue($"$id{i}", ids[i]);
        }
    }
}
