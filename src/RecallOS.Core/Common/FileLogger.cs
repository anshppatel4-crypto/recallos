using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RecallOS.Core.Common;

/// <summary>
/// A small rolling file logger writing into the store's <c>logs</c> folder.
/// </summary>
/// <remarks>
/// RecallOS spends most of its life as a background process with no console attached, so
/// when something goes wrong -- OCR failing to initialise, a capture being refused, a
/// hotkey already taken -- there has to be somewhere to look. Writes are queued and drained
/// by a single background thread so that logging never blocks a capture, and the file is
/// rolled by day and pruned, because a diagnostic aid must not itself become a disk-space
/// problem on a machine that is already recording continuously.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly Thread _writer;
    private readonly int _retainDays;
    private bool _disposed;

    public FileLoggerProvider(RecallPaths paths, LogLevel minimum = LogLevel.Information, int retainDays = 7)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _directory = paths.Logs;
        _minimum = minimum;
        _retainDays = Math.Max(1, retainDays);

        Directory.CreateDirectory(_directory);
        PruneOldLogs();

        _writer = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "RecallOS.FileLogger",
            // Below normal: diagnostics must never compete with capture or the UI.
            Priority = ThreadPriority.BelowNormal
        };

        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    private void Enqueue(string line)
    {
        // Never block the caller: if the queue is full, the line is dropped. Losing a log
        // line is always preferable to stalling a capture.
        if (!_disposed)
        {
            _queue.TryAdd(line);
        }
    }

    private void DrainLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(CurrentFile(), line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // A logger that throws takes down the thing it was meant to diagnose.
            }
        }
    }

    private string CurrentFile() =>
        Path.Combine(_directory, $"recallos-{DateTime.Now:yyyy-MM-dd}.log");

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);

            foreach (var file in Directory.EnumerateFiles(_directory, "recallos-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // Bounded: a stuck disk must not hold up shutdown.
        _writer.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _category = ShortenCategory(category);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder(160)
                .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                .Append("  ")
                .Append(Abbreviate(logLevel))
                .Append("  ")
                .Append(_category)
                .Append("  ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                builder.Append(Environment.NewLine).Append(exception);
            }

            provider.Enqueue(builder.ToString());
        }

        /// <summary>Drop the namespace: the type name alone is what identifies the source.</summary>
        private static string ShortenCategory(string category)
        {
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
