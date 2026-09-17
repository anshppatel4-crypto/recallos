using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RecallOS.Core.Storage;

/// <summary>
/// Creates and upgrades the schema.
/// </summary>
/// <remarks>
/// Migrations are numbered and applied in order inside one transaction each, with the
/// applied version recorded in <c>schema_version</c>. RecallOS is expected to accumulate
/// data for months, so the schema has to be able to move forward without the user ever
/// being asked to discard their history.
/// </remarks>
public sealed class DatabaseBootstrapper
{
    /// <summary>Bump this and add a matching case in <see cref="Apply"/> when the schema changes.</summary>
    public const int TargetVersion = 1;

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<DatabaseBootstrapper> _logger;

    public DatabaseBootstrapper(SqliteConnectionFactory factory, ILogger<DatabaseBootstrapper>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseBootstrapper>.Instance;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);

        using (var command = scope.CreateCommand(
                   "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);"))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadVersionAsync(scope.Connection, cancellationToken).ConfigureAwait(false);
        if (current >= TargetVersion)
        {
            _logger.LogDebug("Schema already at version {Version}.", current);
            return;
        }

        for (var version = current + 1; version <= TargetVersion; version++)
        {
            _logger.LogInformation("Applying schema migration {Version}.", version);

            using var transaction = scope.BeginTransaction();
            foreach (var statement in Apply(version))
            {
                using var command = scope.Connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var stamp = scope.Connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($v);";
                stamp.Parameters.AddWithValue("$v", version);
                await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result);
    }

    private static IEnumerable<string> Apply(int version) => version switch
    {
        1 => Migration001,
        _ => throw new InvalidOperationException($"No migration defined for schema version {version}.")
    };

    private static readonly string[] Migration001 =
    [
        // ---- frames ------------------------------------------------------------------
        // captured_at_ticks duplicates captured_at_utc as an integer purely so range scans
        // and ORDER BY use an index instead of comparing ISO strings.
        """
        CREATE TABLE frames (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            captured_at_utc     TEXT    NOT NULL,
            captured_at_ticks   INTEGER NOT NULL,
            image_path          TEXT    NOT NULL,
            thumbnail_path      TEXT,
            width               INTEGER NOT NULL DEFAULT 0,
            height              INTEGER NOT NULL DEFAULT 0,
            file_size_bytes     INTEGER NOT NULL DEFAULT 0,
            capture_target      INTEGER NOT NULL DEFAULT 0,
            capture_origin      INTEGER NOT NULL DEFAULT 0,
            window_title        TEXT,
            process_name        TEXT,
            display_name        TEXT,
            perceptual_hash     INTEGER NOT NULL DEFAULT 0,
            ocr_status          INTEGER NOT NULL DEFAULT 0,
            text                TEXT    NOT NULL DEFAULT '',
            text_length         INTEGER NOT NULL DEFAULT 0,
            ocr_confidence      REAL    NOT NULL DEFAULT 0,
            ocr_engine          TEXT,
            ocr_error           TEXT,
            ocr_completed_at    TEXT,
            intent_label        TEXT,
            intent_confidence   REAL    NOT NULL DEFAULT 0,
            is_deleted          INTEGER NOT NULL DEFAULT 0
        );
        """,
        "CREATE INDEX idx_frames_time ON frames (captured_at_ticks DESC);",
        "CREATE INDEX idx_frames_ocr_status ON frames (ocr_status) WHERE is_deleted = 0;",
        "CREATE INDEX idx_frames_process ON frames (process_name);",
        "CREATE INDEX idx_frames_intent ON frames (intent_label);",

        // ---- chunks ------------------------------------------------------------------
        // The unit of embedding: a screenful is too coarse to rank a short query against.
        """
        CREATE TABLE chunks (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            frame_id    INTEGER NOT NULL REFERENCES frames (id) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL,
            text        TEXT    NOT NULL,
            confidence  REAL    NOT NULL DEFAULT 0,
            bounds_x    INTEGER,
            bounds_y    INTEGER,
            bounds_w    INTEGER,
            bounds_h    INTEGER
        );
        """,
        "CREATE INDEX idx_chunks_frame ON chunks (frame_id, ordinal);",

        // ---- embeddings --------------------------------------------------------------
        // model is stored per row so vectors from different models are never compared; a
        // future upgrade can index alongside the old vectors and swap over atomically.
        """
        CREATE TABLE embeddings (
            chunk_id   INTEGER NOT NULL REFERENCES chunks (id) ON DELETE CASCADE,
            model      TEXT    NOT NULL,
            dimensions INTEGER NOT NULL,
            vector     BLOB    NOT NULL,
            PRIMARY KEY (chunk_id, model)
        );
        """,
        "CREATE INDEX idx_embeddings_model ON embeddings (model);",

        // ---- settings ----------------------------------------------------------------
        """
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,

        // ---- full-text index ---------------------------------------------------------
        // External-content FTS5: the index stores only the inverted terms and reads
        // column values back from `frames`, which keeps the text from being duplicated.
        // unicode61 with diacritic folding means "resume" finds "resumé".
        """
        CREATE VIRTUAL TABLE frames_fts USING fts5 (
            text,
            window_title,
            process_name,
            content = 'frames',
            content_rowid = 'id',
            tokenize = "unicode61 remove_diacritics 2"
        );
        """,

        // Triggers keep the index in step with the table. An external-content table needs
        // the explicit 'delete' command with the OLD values before a row is replaced,
        // otherwise stale postings accumulate and rows resurrect in search results.
        """
        CREATE TRIGGER frames_fts_insert AFTER INSERT ON frames BEGIN
            INSERT INTO frames_fts (rowid, text, window_title, process_name)
            VALUES (new.id, new.text, new.window_title, new.process_name);
        END;
        """,
        """
        CREATE TRIGGER frames_fts_delete AFTER DELETE ON frames BEGIN
            INSERT INTO frames_fts (frames_fts, rowid, text, window_title, process_name)
            VALUES ('delete', old.id, old.text, old.window_title, old.process_name);
        END;
        """,
        """
        CREATE TRIGGER frames_fts_update AFTER UPDATE OF text, window_title, process_name ON frames BEGIN
            INSERT INTO frames_fts (frames_fts, rowid, text, window_title, process_name)
            VALUES ('delete', old.id, old.text, old.window_title, old.process_name);
            INSERT INTO frames_fts (rowid, text, window_title, process_name)
            VALUES (new.id, new.text, new.window_title, new.process_name);
        END;
        """
    ];
}
