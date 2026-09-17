using System.IO;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using RecallOS.Core.Search;
using RecallOS.Core.Storage;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Exercises the real schema, the real FTS5 index and the real search service against a
/// throwaway store. These paths are almost all SQL, so mocking the database would test
/// nothing that could actually break.
/// </summary>
public sealed class StorageAndSearchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "recallos-tests",
        Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private FrameRepository _repository = null!;
    private HybridSearchService _search = null!;
    private HashingEmbeddingProvider _embeddings = null!;

    public async Task InitializeAsync()
    {
        var paths = new RecallPaths(_root).EnsureCreated();

        _factory = new SqliteConnectionFactory(paths);
        await new DatabaseBootstrapper(_factory).InitializeAsync();

        _repository = new FrameRepository(_factory);
        _embeddings = new HashingEmbeddingProvider(128);
        _search = new HybridSearchService(_factory, _repository, _embeddings);
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
            // A leftover temp directory is not worth failing a test run over.
        }

        return Task.CompletedTask;
    }

    private async Task<CaptureFrame> AddFrameAsync(
        string text,
        string? process = "chrome",
        string? title = "Example",
        DateTimeOffset? at = null,
        ulong hash = 0)
    {
        var frame = await _repository.InsertAsync(new CaptureFrame
        {
            CapturedAt = at ?? DateTimeOffset.UtcNow,
            ImagePath = $"2026/01/01/{Guid.NewGuid():N}.png",
            Width = 1920,
            Height = 1080,
            FileSizeBytes = 1024,
            WindowTitle = title,
            ProcessName = process,
            PerceptualHash = hash,
            OcrStatus = OcrStatus.Completed,
            Text = text
        });

        return frame;
    }

    // ---- schema and round-tripping --------------------------------------------------

    [Fact]
    public async Task MigrationsAreIdempotent()
    {
        // Running the bootstrapper twice must not fail or duplicate objects.
        await new DatabaseBootstrapper(_factory).InitializeAsync();
        await new DatabaseBootstrapper(_factory).InitializeAsync();

        Assert.True(File.Exists(_factory.DatabasePath));
    }

    [Fact]
    public async Task AFrameRoundTripsThroughTheDatabase()
    {
        var inserted = await AddFrameAsync("hello world", "code", "Program.cs");

        var loaded = await _repository.GetAsync(inserted.Id);

        Assert.NotNull(loaded);
        Assert.Equal("hello world", loaded!.Text);
        Assert.Equal("code", loaded.ProcessName);
        Assert.Equal("Program.cs", loaded.WindowTitle);
        Assert.Equal(1920, loaded.Width);
    }

    [Fact]
    public async Task ThePerceptualHashSurvivesTheSignedIntegerColumn()
    {
        // ulong.MaxValue is -1 as a signed 64-bit integer; a naive cast would corrupt it.
        var inserted = await AddFrameAsync("x", hash: ulong.MaxValue);

        var loaded = await _repository.GetAsync(inserted.Id);

        Assert.Equal(ulong.MaxValue, loaded!.PerceptualHash);
        Assert.Equal(ulong.MaxValue, await _repository.GetLatestHashAsync());
    }

    [Fact]
    public async Task UpdatingOcrTextMakesTheFrameFindable()
    {
        var frame = await _repository.InsertAsync(new CaptureFrame
        {
            CapturedAt = DateTimeOffset.UtcNow,
            ImagePath = "a.png",
            OcrStatus = OcrStatus.Pending
        });

        await _repository.UpdateOcrAsync(
            frame.Id, OcrStatus.Completed, "quarterly revenue projections", 0.9, "test", null);

        var result = await _search.SearchAsync(SearchQueryParser.Parse("revenue", SearchMode.Keyword));

        Assert.Contains(result.Hits, h => h.Frame.Id == frame.Id);
    }

    // ---- full-text search -----------------------------------------------------------

    [Fact]
    public async Task KeywordSearchFindsMatchingText()
    {
        await AddFrameAsync("the deployment pipeline failed on staging");
        await AddFrameAsync("a recipe for sourdough bread");

        var result = await _search.SearchAsync(SearchQueryParser.Parse("deployment", SearchMode.Keyword));

        var hit = Assert.Single(result.Hits);
        Assert.Contains("deployment", hit.Frame.Text);
    }

    [Fact]
    public async Task SearchMatchesOnAPrefixWhileStillTyping()
    {
        await AddFrameAsync("configuration management");

        var result = await _search.SearchAsync(SearchQueryParser.Parse("config", SearchMode.Keyword));

        Assert.NotEmpty(result.Hits);
    }

    [Fact]
    public async Task WindowTitlesAreIndexedAlongsideBodyText()
    {
        await AddFrameAsync("body text with nothing special", title: "Annual Budget Spreadsheet");

        var result = await _search.SearchAsync(SearchQueryParser.Parse("Budget", SearchMode.Keyword));

        Assert.NotEmpty(result.Hits);
    }

    [Fact]
    public async Task TheAppFilterNarrowsResults()
    {
        await AddFrameAsync("shared keyword here", process: "chrome");
        await AddFrameAsync("shared keyword here", process: "code");

        var result = await _search.SearchAsync(SearchQueryParser.Parse("shared app:code", SearchMode.Keyword));

        var hit = Assert.Single(result.Hits);
        Assert.Equal("code", hit.Frame.ProcessName);
    }

    [Fact]
    public async Task DateFiltersBoundTheResults()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        await AddFrameAsync("timeless content", at: old);
        await AddFrameAsync("timeless content", at: DateTimeOffset.UtcNow);

        var query = SearchQueryParser.Parse("timeless", SearchMode.Keyword) is { } parsed
            ? new SearchQuery
            {
                Raw = parsed.Raw,
                Text = parsed.Text,
                MatchExpression = parsed.MatchExpression,
                Terms = parsed.Terms,
                Mode = SearchMode.Keyword,
                Limit = 100,
                After = DateTimeOffset.UtcNow.AddDays(-1)
            }
            : throw new InvalidOperationException();

        var result = await _search.SearchAsync(query);

        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task AQueryOfPureOperatorsDegradesGracefullyInsteadOfThrowing()
    {
        await AddFrameAsync("some ordinary content");

        // Raw, this would be an FTS5 syntax error.
        var result = await _search.SearchAsync(SearchQueryParser.Parse("\"\"\" NEAR( ^ *", SearchMode.Keyword));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task AnEmptyQueryReturnsTheMostRecentFramesNewestFirst()
    {
        await AddFrameAsync("older", at: DateTimeOffset.UtcNow.AddHours(-2));
        await AddFrameAsync("newer", at: DateTimeOffset.UtcNow);

        var result = await _search.SearchAsync(SearchQueryParser.Parse(""));

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("newer", result.Hits[0].Frame.Text);
    }

    [Fact]
    public async Task DeletingAFrameRemovesItFromTheSearchIndex()
    {
        var frame = await AddFrameAsync("ephemeral secret content");

        var paths = await _repository.DeleteAsync([frame.Id]);

        Assert.Single(paths);

        // If the FTS delete trigger were missing, the row would resurrect here.
        var result = await _search.SearchAsync(SearchQueryParser.Parse("ephemeral", SearchMode.Keyword));
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task EditingTextRemovesTheOldTermsFromTheIndex()
    {
        var frame = await AddFrameAsync("originaltoken");

        await _repository.UpdateOcrAsync(frame.Id, OcrStatus.Completed, "replacementtoken", 1, "test", null);

        var stale = await _search.SearchAsync(SearchQueryParser.Parse("originaltoken", SearchMode.Keyword));
        var fresh = await _search.SearchAsync(SearchQueryParser.Parse("replacementtoken", SearchMode.Keyword));

        Assert.Empty(stale.Hits);
        Assert.NotEmpty(fresh.Hits);
    }

    // ---- chunks and embeddings ------------------------------------------------------

    [Fact]
    public async Task ChunksAreReplacedRatherThanAccumulated()
    {
        var frame = await AddFrameAsync("content");

        await _repository.ReplaceChunksAsync(frame.Id,
        [
            new TextChunk { FrameId = frame.Id, Ordinal = 0, Text = "first version" }
        ]);

        await _repository.ReplaceChunksAsync(frame.Id,
        [
            new TextChunk { FrameId = frame.Id, Ordinal = 0, Text = "second version" },
            new TextChunk { FrameId = frame.Id, Ordinal = 1, Text = "and more" }
        ]);

        var chunks = await _repository.GetChunksAsync(frame.Id);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("second version", chunks[0].Text);
    }

    [Fact]
    public async Task EmbeddingsRoundTripThroughTheBlobColumn()
    {
        var frame = await AddFrameAsync("vector content");
        await _repository.ReplaceChunksAsync(frame.Id,
            [new TextChunk { FrameId = frame.Id, Ordinal = 0, Text = "vector content" }]);

        var chunk = (await _repository.GetChunksAsync(frame.Id)).Single();
        var vector = _embeddings.Embed("vector content");

        await _repository.SaveEmbeddingAsync(chunk.Id, _embeddings.ModelId, vector);

        var stored = new List<EmbeddingRecord>();
        await foreach (var record in _repository.StreamEmbeddingsAsync(_embeddings.ModelId))
        {
            stored.Add(record);
        }

        var single = Assert.Single(stored);
        Assert.Equal(frame.Id, single.FrameId);
        Assert.Equal(vector.ToArray(), single.Vector);
    }

    [Fact]
    public async Task SavingAnEmbeddingTwiceUpdatesRatherThanDuplicates()
    {
        var frame = await AddFrameAsync("content");
        await _repository.ReplaceChunksAsync(frame.Id,
            [new TextChunk { FrameId = frame.Id, Ordinal = 0, Text = "content" }]);

        var chunk = (await _repository.GetChunksAsync(frame.Id)).Single();

        await _repository.SaveEmbeddingAsync(chunk.Id, _embeddings.ModelId, _embeddings.Embed("one"));
        await _repository.SaveEmbeddingAsync(chunk.Id, _embeddings.ModelId, _embeddings.Embed("two"));

        var count = 0;
        await foreach (var _ in _repository.StreamEmbeddingsAsync(_embeddings.ModelId))
        {
            count++;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DeletingAFrameCascadesToItsChunksAndEmbeddings()
    {
        var frame = await AddFrameAsync("content");
        await _repository.ReplaceChunksAsync(frame.Id,
            [new TextChunk { FrameId = frame.Id, Ordinal = 0, Text = "content" }]);

        var chunk = (await _repository.GetChunksAsync(frame.Id)).Single();
        await _repository.SaveEmbeddingAsync(chunk.Id, _embeddings.ModelId, _embeddings.Embed("content"));

        await _repository.DeleteAsync([frame.Id]);

        Assert.Empty(await _repository.GetChunksAsync(frame.Id));

        var remaining = 0;
        await foreach (var _ in _repository.StreamEmbeddingsAsync(_embeddings.ModelId))
        {
            remaining++;
        }

        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task SemanticSearchRanksTheRelatedFrameFirst()
    {
        var related = await AddFrameAsync("the database migration script failed halfway through");
        await AddFrameAsync("a photograph of a mountain range at sunrise");

        // Index both frames as single chunks.
        foreach (var frame in new[] { related.Id }.Concat(
                     (await _repository.GetRecentAsync(10)).Select(f => f.Id)).Distinct())
        {
            var loaded = await _repository.GetAsync(frame);
            await _repository.ReplaceChunksAsync(frame,
                [new TextChunk { FrameId = frame, Ordinal = 0, Text = loaded!.Text }]);

            var chunk = (await _repository.GetChunksAsync(frame)).Single();
            await _repository.SaveEmbeddingAsync(chunk.Id, _embeddings.ModelId, _embeddings.Embed(loaded.Text));
        }

        var result = await _search.SearchAsync(
            SearchQueryParser.Parse("database migration", SearchMode.Semantic));

        Assert.NotEmpty(result.Hits);
        Assert.Equal(related.Id, result.Hits[0].Frame.Id);
    }

    // ---- navigation, timeline and statistics ----------------------------------------

    [Fact]
    public async Task NeighbourNavigationWalksForwardsAndBackwardsInTime()
    {
        var first = await AddFrameAsync("one", at: DateTimeOffset.UtcNow.AddMinutes(-10));
        var second = await AddFrameAsync("two", at: DateTimeOffset.UtcNow.AddMinutes(-5));
        var third = await AddFrameAsync("three", at: DateTimeOffset.UtcNow);

        Assert.Equal(third.Id, (await _repository.GetNeighborAsync(second.Id, forward: true))!.Id);
        Assert.Equal(first.Id, (await _repository.GetNeighborAsync(second.Id, forward: false))!.Id);
        Assert.Null(await _repository.GetNeighborAsync(third.Id, forward: true));
        Assert.Null(await _repository.GetNeighborAsync(first.Id, forward: false));
    }

    [Fact]
    public async Task TheTimelineBucketsFramesAndNamesTheDominantApp()
    {
        // Anchored to a fixed instant in the middle of an hour, not to "now". Relative
        // timestamps put the frames either side of an hour boundary whenever the suite
        // happens to run near one, splitting them across two buckets — correct behaviour,
        // but it makes the assertion depend on the clock.
        var anchor = new DateTimeOffset(2026, 3, 4, 14, 30, 0, TimeSpan.Zero);

        await AddFrameAsync("a", process: "code", at: anchor);
        await AddFrameAsync("b", process: "code", at: anchor.AddMinutes(5));
        await AddFrameAsync("c", process: "chrome", at: anchor.AddMinutes(10));

        var buckets = await _repository.GetTimelineAsync(
            anchor.AddHours(-1), anchor.AddHours(1), TimelineGranularity.Hour);

        var bucket = Assert.Single(buckets);
        Assert.Equal(3, bucket.FrameCount);
        Assert.Equal("code", bucket.DominantApp);
    }

    [Fact]
    public async Task FramesEitherSideOfAnHourBoundaryLandInDifferentBuckets()
    {
        // The complement of the test above: the split really is what should happen.
        var justBefore = new DateTimeOffset(2026, 3, 4, 13, 59, 0, TimeSpan.Zero);

        await AddFrameAsync("a", process: "code", at: justBefore);
        await AddFrameAsync("b", process: "chrome", at: justBefore.AddMinutes(2));

        var buckets = await _repository.GetTimelineAsync(
            justBefore.AddHours(-1), justBefore.AddHours(2), TimelineGranularity.Hour);

        Assert.Equal(2, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(1, b.FrameCount));
    }

    [Fact]
    public async Task StatisticsReportWhatIsActuallyStored()
    {
        await AddFrameAsync("one");
        await AddFrameAsync("two");

        var statistics = await _repository.GetStatisticsAsync();

        Assert.Equal(2, statistics.FrameCount);
        Assert.True(statistics.DatabaseBytes > 0);
        Assert.NotNull(statistics.OldestFrame);
        Assert.NotNull(statistics.NewestFrame);
    }

    [Fact]
    public async Task PendingFramesAreReturnedOldestFirstSoABacklogDrainsInOrder()
    {
        var older = await _repository.InsertAsync(new CaptureFrame
        {
            CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ImagePath = "old.png",
            OcrStatus = OcrStatus.Pending
        });

        await _repository.InsertAsync(new CaptureFrame
        {
            CapturedAt = DateTimeOffset.UtcNow,
            ImagePath = "new.png",
            OcrStatus = OcrStatus.Pending
        });

        var pending = await _repository.GetPendingOcrAsync(10);

        Assert.Equal(2, pending.Count);
        Assert.Equal(older.Id, pending[0].Id);
    }

    [Fact]
    public async Task KnownAppsAreListedByFrequency()
    {
        await AddFrameAsync("x", process: "code");
        await AddFrameAsync("y", process: "code");
        await AddFrameAsync("z", process: "chrome");

        var apps = await _repository.GetKnownAppsAsync();

        Assert.Equal("code", apps[0]);
        Assert.Contains("chrome", apps);
    }
}
