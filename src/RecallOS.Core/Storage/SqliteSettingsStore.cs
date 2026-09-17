using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;

namespace RecallOS.Core.Storage;

/// <summary>
/// Persists <see cref="RecallSettings"/> as key/value rows beside the frames.
/// </summary>
/// <remarks>
/// Properties are read and written by reflection against the settings type, so adding a
/// setting is a one-line change to <see cref="RecallSettings"/> with no schema migration
/// and no serializer contract to keep in step. Unknown keys in the table are ignored and
/// missing keys fall back to the property default, which makes both upgrades and
/// downgrades non-destructive.
/// </remarks>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly PropertyInfo[] Properties = typeof(RecallSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToArray();

    public SqliteSettingsStore(SqliteConnectionFactory factory, ILogger<SqliteSettingsStore>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSettingsStore>.Instance;
        Current = new RecallSettings();
    }

    public RecallSettings Current { get; private set; }

    public event EventHandler<RecallSettings>? SettingsChanged;

    public async Task<RecallSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = new RecallSettings();
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var connection = _factory.OpenRead())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT key, value FROM settings;";
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    stored[reader.GetString(0)] = reader.GetString(1);
                }
            }

            foreach (var property in Properties)
            {
                if (!stored.TryGetValue(property.Name, out var raw))
                {
                    continue;
                }

                try
                {
                    property.SetValue(settings, Parse(raw, property.PropertyType));
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
                {
                    // A corrupted value must not stop the app booting; the default stands.
                    _logger.LogWarning(ex, "Ignoring unreadable value for setting {Setting}.", property.Name);
                }
            }

            Current = settings.Normalized();
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RecallSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = settings.Normalized();

            await using var scope = await _factory.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = scope.BeginTransaction();

            using (var command = scope.Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO settings (key, value) VALUES ($key, $value)
                    ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                    """;

                var keyParameter = command.Parameters.Add("$key", Microsoft.Data.Sqlite.SqliteType.Text);
                var valueParameter = command.Parameters.Add("$value", Microsoft.Data.Sqlite.SqliteType.Text);

                foreach (var property in Properties)
                {
                    keyParameter.Value = property.Name;
                    valueParameter.Value = Format(property.GetValue(normalized));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            transaction.Commit();
            Current = normalized;
        }
        finally
        {
            _gate.Release();
        }

        // Raised outside the lock: a handler that saves again would otherwise deadlock.
        SettingsChanged?.Invoke(this, Current);
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static object Parse(string raw, Type type)
    {
        if (type == typeof(string))
        {
            return raw;
        }

        if (type == typeof(bool))
        {
            // Tolerate the integer form an external editor might leave behind.
            return raw is "1" || bool.Parse(raw);
        }

        if (type == typeof(int))
        {
            return int.Parse(raw, CultureInfo.InvariantCulture);
        }

        if (type == typeof(double))
        {
            return double.Parse(raw, CultureInfo.InvariantCulture);
        }

        return type.IsEnum
            ? Enum.Parse(type, raw, ignoreCase: true)
            : Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
    }
}
