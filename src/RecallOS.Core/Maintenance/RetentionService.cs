using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;
using RecallOS.Core.Storage;

namespace RecallOS.Core.Maintenance;

/// <summary>
/// Enforces the storage limits the user set, and provides explicit deletion.
/// </summary>
/// <remarks>
/// <para>
/// A system that records continuously will fill any disk given time, so retention is not a
/// nice-to-have: without it RecallOS eventually breaks the machine it runs on. Two limits
/// apply together -- an age cap and a size cap -- and the age sweep runs first, because
/// deleting things because they are old is more predictable to the user than deleting
/// things because a threshold was crossed.
/// </para>
/// <para>
/// Rows go before files. If the process dies mid-sweep, the result is an orphaned image
/// with no row, which is invisible and reclaimed by the next sweep. The opposite order
/// would leave rows pointing at files that no longer exist, which the user would meet as
/// broken results in their own history.
/// </para>
/// </remarks>
public sealed class RetentionService
{
    /// <summary>Deleting in batches keeps each write-lock acquisition short.</summary>
    private const int BatchSize = 200;

    private readonly IFrameRepository _repository;
    private readonly FrameFileStore _files;
    private readonly ISettingsStore _settings;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IFrameRepository repository,
        FrameFileStore files,
        ISettingsStore settings,
        ILogger<RetentionService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RetentionService>.Instance;
    }

    /// <summary>Apply both limits. Returns how many frames were removed.</summary>
    public async Task<int> EnforceAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var removed = 0;

        if (settings.RetentionDays > 0)
        {
            removed += await EnforceAgeAsync(
                DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays),
                cancellationToken).ConfigureAwait(false);
        }

        if (settings.MaxStorageGigabytes > 0)
        {
            removed += await EnforceSizeAsync(
                (long)settings.MaxStorageGigabytes * 1024 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
        }

        if (removed > 0)
        {
            _logger.LogInformation("Retention removed {Count} frames.", removed);
        }

        return removed;
    }

    private async Task<int> EnforceAgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var expired = await _repository.GetExpiredAsync(cutoff, BatchSize, cancellationToken).ConfigureAwait(false);
            if (expired.Count == 0)
            {
                break;
            }

            await DeleteAsync(expired.Select(f => f.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            total += expired.Count;

            // A short batch means the store is drained; another query would return nothing.
            if (expired.Count < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Evict oldest-first until the store fits inside the budget.</summary>
    /// <remarks>
    /// <para>
    /// The size is measured from the file system each round rather than accumulated from
    /// the frames deleted. Per-frame sizes are only what was recorded at capture time: they
    /// exclude thumbnails and the database, and are zero for any frame whose size was never
    /// written. A loop that trusted them could decide it had reclaimed nothing and keep
    /// deleting. Measuring drives the loop by the condition it is actually trying to meet.
    /// </para>
    /// <para>
    /// Walking the image tree is far too slow for the per-capture statistics used by the
    /// status bar, which is why that path sums the column instead. Here it runs hourly at
    /// most, so accuracy is worth more than speed.
    /// </para>
    /// </remarks>
    private async Task<int> EnforceSizeAsync(long budgetBytes, CancellationToken cancellationToken)
    {
        var size = await MeasureStoreAsync(cancellationToken).ConfigureAwait(false);
        if (size <= budgetBytes)
        {
            return 0;
        }

        _logger.LogInformation(
            "Store is {Actual}, over the {Budget} budget; evicting oldest frames.",
            FormatBytes(size),
            FormatBytes(budgetBytes));

        var total = 0;

        while (size > budgetBytes && !cancellationToken.IsCancellationRequested)
        {
            var oldest = await _repository.GetOldestAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            if (oldest.Count == 0)
            {
                // Nothing left to evict. Whatever remains is database overhead, which the
                // next vacuum reclaims; deleting further would achieve nothing.
                break;
            }

            await DeleteAsync(oldest.Select(f => f.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            total += oldest.Count;

            size = await MeasureStoreAsync(cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>Actual bytes on disk: the image and thumbnail trees plus the database.</summary>
    private async Task<long> MeasureStoreAsync(CancellationToken cancellationToken)
    {
        var imageBytes = await Task.Run(_files.CalculateSizeBytes, cancellationToken).ConfigureAwait(false);
        var statistics = await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);

        return imageBytes + statistics.DatabaseBytes;
    }

    /// <summary>
    /// Remove frames and their images. This is the single deletion path: manual deletes
    /// from the UI and automatic eviction both come through here, so "forget this" always
    /// means the same thing.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<long> frameIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frameIds);

        if (frameIds.Count == 0)
        {
            return;
        }

        var paths = await _repository.DeleteAsync(frameIds, cancellationToken).ConfigureAwait(false);
        _files.Delete(paths);
    }

    /// <summary>
    /// Erase everything. Exposed because a tool that records the user's screen must offer
    /// an unambiguous way to undo that, without hunting through the file system.
    /// </summary>
    public async Task<int> DeleteRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frames = await _repository.GetRangeAsync(from, to, BatchSize, cancellationToken).ConfigureAwait(false);
            if (frames.Count == 0)
            {
                break;
            }

            await DeleteAsync(frames.Select(f => f.Id).ToArray(), cancellationToken).ConfigureAwait(false);
            total += frames.Count;
        }

        if (total > 0)
        {
            // Reclaim the pages on disk: after a bulk delete the user expects the space back.
            await _repository.VacuumAsync(cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
