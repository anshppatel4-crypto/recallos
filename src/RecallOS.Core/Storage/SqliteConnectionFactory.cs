using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using RecallOS.Core.Common;

namespace RecallOS.Core.Storage;

/// <summary>
/// Hands out configured SQLite connections and serialises writers.
/// </summary>
/// <remarks>
/// <para>
/// The store runs in WAL mode, which lets the UI read the timeline while the capture
/// pipeline is inserting. WAL permits many readers but exactly one writer, so rather than
/// letting concurrent writes collide and surface as SQLITE_BUSY, writers take an in-process
/// semaphore and queue politely.
/// </para>
/// <para>
/// Connections themselves are pooled by Microsoft.Data.Sqlite on the connection string, so
/// opening one per operation is cheap and avoids the long-lived-connection problems (stale
/// transactions pinning the WAL) that a single shared connection would create.
/// </para>
/// </remarks>
public sealed class SqliteConnectionFactory : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _connectionString;
    private bool _disposed;

    public SqliteConnectionFactory(RecallPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        paths.EnsureCreated();
        DatabasePath = paths.DatabaseFile;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            // Wait rather than fail if another process (a second RecallOS instance, or a
            // SQLite browser) holds the write lock.
            DefaultTimeout = 30
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Open a connection for reading. Safe to call concurrently.</summary>
    public SqliteConnection OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPerConnectionPragmas(connection);
        return connection;
    }

    /// <summary>
    /// Take the writer slot and open a connection. Dispose the returned scope to release
    /// both. Always use this for anything that mutates the database.
    /// </summary>
    public async Task<WriteScope> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            ApplyPerConnectionPragmas(connection);
            return new WriteScope(connection, _writeLock);
        }
        catch
        {
            _writeLock.Release();
            throw;
        }
    }

    /// <summary>Size of the database file plus its WAL and shared-memory sidecars.</summary>
    public long GetDatabaseSizeBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = DatabasePath + suffix;
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static void ApplyPerConnectionPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // journal_mode is persistent in the file; the rest are per-connection and must be
        // re-applied every time, including on pooled connections.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA temp_store = MEMORY;
            PRAGMA busy_timeout = 30000;
            PRAGMA cache_size = -20000;
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>A held writer slot plus its connection. Disposing releases both, in order.</summary>
    public sealed class WriteScope : IDisposable, IAsyncDisposable
    {
        private readonly SemaphoreSlim _lock;
        private bool _released;

        internal WriteScope(SqliteConnection connection, SemaphoreSlim writeLock)
        {
            Connection = connection;
            _lock = writeLock;
        }

        public SqliteConnection Connection { get; }

        public SqliteCommand CreateCommand(string sql)
        {
            var command = Connection.CreateCommand();
            command.CommandText = sql;
            return command;
        }

        public SqliteTransaction BeginTransaction() => Connection.BeginTransaction();

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Connection.Dispose();
            _lock.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            await Connection.DisposeAsync().ConfigureAwait(false);
            _lock.Release();
        }
    }
}

/// <summary>Small helpers that keep the repository SQL readable.</summary>
internal static class SqliteExtensions
{
    public static SqliteCommand WithParameter(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static string? GetNullableString(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? null : record.GetString(ordinal);

    public static double GetDoubleOrDefault(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? 0d : record.GetDouble(ordinal);

    public static long GetInt64OrDefault(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? 0L : record.GetInt64(ordinal);

    public static int GetInt32OrDefault(this IDataRecord record, int ordinal) =>
        record.IsDBNull(ordinal) ? 0 : record.GetInt32(ordinal);
}
