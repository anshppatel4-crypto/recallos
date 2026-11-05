using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using RecallOS.Core.Common;
using RecallOS.Core.Maintenance;
using RecallOS.Core.Models;
using RecallOS.Core.Storage;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Retention is the one subsystem whose job is to destroy the user's data, so it is tested
/// against real files on disk: that images are actually erased, that limits are honoured,
/// and — most importantly — that it stops when it should.
/// </summary>
public sealed class RetentionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "recallos-tests",
        Guid.NewGuid().ToString("N"));

    private RecallPaths _paths = null!;
    private SqliteConnectionFactory _factory = null!;
    private FrameRepository _repository = null!;
    private FrameFileStore _files = null!;
    private SqliteSettingsStore _settings = null!;
    private RetentionService _retention = null!;

    public async Task InitializeAsync()
    {
        _paths = new RecallPaths(_root).EnsureCreated();
        _factory = new SqliteConnectionFactory(_paths);
        await new DatabaseBootstrapper(_factory).InitializeAsync();

        _repository = new FrameRepository(_factory);
        _files = new FrameFileStore(_paths);
        _settings = new SqliteSettingsStore(_factory);
        await _settings.LoadAsync();

        _retention = new RetentionService(_repository, _files, _settings);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>Write a real image through the file store and record it, as capture would.</summary>
    private async Task<CaptureFrame> AddFrameWithImageAsync(DateTimeOffset at)
    {
        using var bitmap = new Bitmap(64, 48, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.CornflowerBlue);
        }

        var stored = await _files.SaveAsync(bitmap, at, 120);

        return await _repository.InsertAsync(new CaptureFrame
        {
            CapturedAt = at,
            ImagePath = stored.RelativeImagePath,
            ThumbnailPath = stored.RelativeThumbnailPath,
            Width = stored.Width,
            Height = stored.Height,
            FileSizeBytes = stored.FileSizeBytes,
            OcrStatus = OcrStatus.Completed,
            Text = "content"
        });
    }

    [Fact]
    public async Task SavingWritesBothTheImageAndItsThumbnail()
    {
        var frame = await AddFrameWithImageAsync(DateTimeOffset.UtcNow);

        Assert.True(File.Exists(_files.ResolveImage(frame.ImagePath)));
        Assert.NotNull(frame.ThumbnailPath);
        Assert.True(File.Exists(_files.ResolveThumbnail(frame.ThumbnailPath!)));
        Assert.True(frame.FileSizeBytes > 0);
    }

    [Fact]
    public async Task DeletingAFrameErasesItsFilesFromDisk()
    {
        var frame = await AddFrameWithImageAsync(DateTimeOffset.UtcNow);
        var image = _files.ResolveImage(frame.ImagePath);
        var thumbnail = _files.ResolveThumbnail(frame.ThumbnailPath!);

        await _retention.DeleteAsync([frame.Id]);

        Assert.False(File.Exists(image));
        Assert.False(File.Exists(thumbnail));
        Assert.Null(await _repository.GetAsync(frame.Id));
    }

    [Fact]
    public async Task TheAgeLimitRemovesOnlyFramesOlderThanTheCutoff()
    {
        var settings = _settings.Current.Clone();
        settings.RetentionDays = 7;
        settings.MaxStorageGigabytes = 0;
        await _settings.SaveAsync(settings);

        var old = await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddDays(-30));
        var recent = await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddDays(-1));

        var removed = await _retention.EnforceAsync();

        Assert.Equal(1, removed);
        Assert.Null(await _repository.GetAsync(old.Id));
        Assert.NotNull(await _repository.GetAsync(recent.Id));
    }

    [Fact]
    public async Task BothLimitsDisabledMeansNothingIsEverRemoved()
    {
        var settings = _settings.Current.Clone();
        settings.RetentionDays = 0;
        settings.MaxStorageGigabytes = 0;
        await _settings.SaveAsync(settings);

        await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddYears(-5));

        Assert.Equal(0, await _retention.EnforceAsync());
        Assert.Equal(1, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task AGenerousSizeBudgetEvictsNothingAndTerminates()
    {
        // The guard against the eviction loop running away: well under budget, it must do
        // nothing at all rather than delete "just in case".
        var settings = _settings.Current.Clone();
        settings.RetentionDays = 0;
        settings.MaxStorageGigabytes = 100;
        await _settings.SaveAsync(settings);

        for (var i = 0; i < 3; i++)
        {
            await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddMinutes(-i));
        }

        Assert.Equal(0, await _retention.EnforceAsync());
        Assert.Equal(3, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Fact]
    public async Task FramesWithNoRecordedSizeDoNotCauseTheEvictionLoopToSpin()
    {
        // A zero recorded size used to be able to convince a size-accumulating loop that it
        // had reclaimed nothing. The loop must still terminate, and must stop once there is
        // nothing left to evict rather than looping forever.
        var settings = _settings.Current.Clone();
        settings.RetentionDays = 0;
        settings.MaxStorageGigabytes = 1;
        await _settings.SaveAsync(settings);

        for (var i = 0; i < 5; i++)
        {
            await _repository.InsertAsync(new CaptureFrame
            {
                CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                ImagePath = $"missing-{i}.png",
                FileSizeBytes = 0,
                OcrStatus = OcrStatus.Completed
            });
        }

        // Completes rather than hanging; the assertion is that this returns at all.
        var removed = await _retention.EnforceAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // A 1 GB budget is never exceeded by five empty rows, so nothing should go.
        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task DeletingARangeRemovesEverythingInsideItAndNothingOutside()
    {
        var inside = await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddHours(-3));
        var outside = await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddHours(-20));

        var removed = await _retention.DeleteRangeAsync(
            DateTimeOffset.UtcNow.AddHours(-5),
            DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.Null(await _repository.GetAsync(inside.Id));
        Assert.NotNull(await _repository.GetAsync(outside.Id));
    }

    [Fact]
    public async Task ErasingEverythingLeavesAnEmptyStore()
    {
        for (var i = 0; i < 4; i++)
        {
            await AddFrameWithImageAsync(DateTimeOffset.UtcNow.AddMinutes(-i));
        }

        var removed = await _retention.DeleteRangeAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.Equal(4, removed);

        var statistics = await _repository.GetStatisticsAsync();
        Assert.Equal(0, statistics.FrameCount);

        // Every image file should have gone with the rows.
        Assert.Empty(Directory.EnumerateFiles(_paths.Frames, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DeletingNothingIsAHarmlessNoOp()
    {
        await AddFrameWithImageAsync(DateTimeOffset.UtcNow);

        await _retention.DeleteAsync([]);

        Assert.Equal(1, (await _repository.GetStatisticsAsync()).FrameCount);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void ByteSizesAreFormattedTheWayAPersonWouldSayThem(long bytes, string expected)
    {
        Assert.Equal(expected, RetentionService.FormatBytes(bytes));
    }
}
