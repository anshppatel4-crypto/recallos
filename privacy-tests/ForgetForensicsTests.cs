using System.IO;
using System.Text;
using RecallOS.Core.Common;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Memory;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Privacy;
using RecallOS.Core.Storage;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// "Forget" checked the way someone hostile would check it: by reading the raw bytes of
/// every file the store keeps, not by asking the store whether it still has the rows.
/// </summary>
/// <remarks>
/// <para>
/// A query that comes back empty proves only that the database will not hand the data
/// back. It says nothing about free pages, the full-text index's segment files, or a
/// write-ahead log still holding copies of pages from before the delete -- all of which
/// can be read with a hex editor, and all of which a "forget" that only deletes rows
/// leaves behind.
/// </para>
/// <para>
/// Each test first proves the scan works, by finding the marker in the files before the
/// forget. Without that positive control a passing test could simply mean the scan was
/// looking in the wrong place.
/// </para>
/// </remarks>
public sealed class ForgetForensicsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 3, 4, 20, 0, 0, TimeSpan.Zero);

    // Words that occur nowhere else, so finding one in the bytes can only mean the
    // forgotten frames survived somewhere.
    private const string SecretWord = "quorbinax";
    private const string SecretPhrase = "marigold-velvet-7731";
    private const string KeptWord = "zephyrlumen";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "recallos-tests", Guid.NewGuid().ToString("N"));

    private SqliteConnectionFactory _factory = null!;
    private FrameRepository _frames = null!;
    private MemoryRepository _memory = null!;
    private FrameFileStore _files = null!;

    public async Task InitializeAsync()
    {
        var paths = new RecallPaths(_root).EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        await new DatabaseBootstrapper(_factory).InitializeAsync();

        _frames = new FrameRepository(_factory);
        _memory = new MemoryRepository(_factory);
        _files = new FrameFileStore(paths);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Record frames the way the pipeline does: the row, its text chunks, their vectors,
    /// then analysis into sessions and entities -- every table that can hold a copy.
    /// </summary>
    private async Task SeedAsync(int keptStartMinute = 20)
    {
        var chunker = new TextChunker();
        var embedder = new HashingEmbeddingProvider(128);

        async Task AddAsync(int minute, string process, string title, string text)
        {
            var relative = $"2026/03/04/frame-{Guid.NewGuid():N}.png";
            var full = _files.ResolveImage(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, Encoding.UTF8.GetBytes($"image {text}"));

            var frame = await _frames.InsertAsync(new CaptureFrame
            {
                CapturedAt = Base.AddMinutes(minute),
                ImagePath = relative,
                ProcessName = process,
                WindowTitle = title,
                Text = text,
                OcrStatus = OcrStatus.Completed,
                FileSizeBytes = 64
            });

            var chunks = chunker.Chunk(frame.Id, text);
            await _frames.ReplaceChunksAsync(frame.Id, chunks);
            foreach (var chunk in await _frames.GetChunksAsync(frame.Id))
            {
                await _frames.SaveEmbeddingAsync(chunk.Id, embedder.ModelId, embedder.Embed(chunk.Text));
            }
        }

        for (var i = 0; i < 12; i++)
        {
            await AddAsync(
                i,
                "notepad",
                $"{SecretWord} plan.txt - Notepad",
                $"The {SecretWord} launch code is {SecretPhrase}. Do not share the {SecretWord} plan " +
                $"with anyone outside the team. Contact {SecretWord}@example.com before Friday.");
        }

        for (var i = keptStartMinute; i < keptStartMinute + 12; i++)
        {
            await AddAsync(
                i,
                "chrome",
                $"{KeptWord} research - Google Search",
                $"Notes on {KeptWord} from the public documentation, chapter {i}. " +
                "Ordinary reading that the user has not asked to forget.");
        }

        await new MemoryAnalyzer(_frames, _memory).AnalyzeAsync(Base.AddMinutes(-10), Base.AddMinutes(keptStartMinute + 60));
    }

    /// <summary>Every file SQLite may keep for the store, read without disturbing it.</summary>
    private string[] StoreFiles() =>
    [
        .. new[] { string.Empty, "-wal", "-shm", "-journal" }
            .Select(suffix => _factory.DatabasePath + suffix)
            .Where(File.Exists)
    ];

    /// <summary>Whether the text occurs anywhere in the store's files, as UTF-8 or UTF-16.</summary>
    private bool OccursOnDisk(string needle) => FindOnDisk(needle) is not null;

    /// <summary>
    /// Where the text survives, as "file @ offset: surrounding bytes", or null if nowhere.
    /// The context is what turns a failure into a diagnosis: it shows which table's pages
    /// the text is sitting in.
    /// </summary>
    private string? FindOnDisk(string needle)
    {
        var lower = needle.ToLowerInvariant();
        var patterns = new[]
        {
            Encoding.UTF8.GetBytes(lower),
            Encoding.UTF8.GetBytes(needle),
            Encoding.Unicode.GetBytes(lower)
        };

        foreach (var path in StoreFiles())
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            // Case-folded copy too: the full-text index stores tokens lower-cased.
            var folded = Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(bytes).ToLowerInvariant());

            foreach (var haystack in new[] { bytes, folded })
            {
                foreach (var pattern in patterns)
                {
                    var at = haystack.AsSpan().IndexOf(pattern);
                    if (at < 0)
                    {
                        continue;
                    }

                    var from = Math.Max(0, at - 60);
                    var to = Math.Min(haystack.Length, at + pattern.Length + 60);
                    var context = new string(Encoding.Latin1.GetString(haystack, from, to - from)
                        .Select(c => c is >= ' ' and < (char)127 ? c : '.').ToArray());

                    return $"{Path.GetFileName(path)} @ {at} of {bytes.Length}: {context}";
                }
            }
        }

        return null;
    }

    private void AssertGone(string needle)
    {
        var found = FindOnDisk(needle);
        Assert.True(found is null, $"'{needle}' survived on disk after forgetting: {found}");
    }

    private ForgetService NewForgetService() => new(_frames, _memory, _files, _factory);

    [Fact]
    public async Task ForgottenTextIsNotInAnyByteOfTheStore()
    {
        await SeedAsync();

        // Positive control: the scan can see the data while it is there.
        Assert.True(OccursOnDisk(SecretWord), "control failed: the scan cannot see live data");
        Assert.True(OccursOnDisk(SecretPhrase), "control failed: the scan cannot see live data");

        var report = await NewForgetService().ForgetAsync(ForgetRequest.ForApp("notepad"));
        Assert.Equal(12, report.FramesDeleted);

        AssertGone(SecretWord);
        AssertGone(SecretPhrase);

        // And forgetting one thing did not take the rest with it.
        Assert.True(OccursOnDisk(KeptWord));
    }

    [Fact]
    public async Task ATimeRangeForgetLeavesNoTraceEither()
    {
        await SeedAsync();
        Assert.True(OccursOnDisk(SecretPhrase), "control failed: the scan cannot see live data");

        await NewForgetService().ForgetAsync(ForgetRequest.ForRange(Base.AddMinutes(-1), Base.AddMinutes(15)));

        AssertGone(SecretWord);
        AssertGone(SecretPhrase);
        Assert.True(OccursOnDisk(KeptWord));
    }

    [Fact]
    public async Task ForgettingAWholeEpisodeTakesItsSessionWithIt()
    {
        // Hours apart, so the forgotten frames form an episode of their own, titled from
        // what was on them. Deleting the frames alone would leave that title behind.
        await SeedAsync(keptStartMinute: 240);

        var sessions = await _memory.GetSessionsAsync(Base.AddHours(-1), Base.AddHours(6));
        Assert.Equal(2, sessions.Count);

        await NewForgetService().ForgetAsync(ForgetRequest.ForRange(Base.AddMinutes(-1), Base.AddMinutes(15)));

        var after = await _memory.GetSessionsAsync(Base.AddHours(-1), Base.AddHours(6));
        Assert.Single(after);

        AssertGone(SecretWord);
        AssertGone(SecretPhrase);
        Assert.True(OccursOnDisk(KeptWord));
    }

    [Fact]
    public async Task AnAppWithinATimeRangeTakesOnlyThatOverlap()
    {
        await SeedAsync();

        // Chrome before minute 26 goes; notepad, and Chrome after 26, stay.
        var report = await NewForgetService().ForgetAsync(
            ForgetRequest.ForAppInRange("chrome", Base.AddMinutes(-1), Base.AddMinutes(25.5)));

        Assert.Equal(6, report.FramesDeleted);
        Assert.Equal(12, (await _frames.FindFrameIdsAsync(new Abstractions.FrameFilter { ProcessName = "notepad" })).Count);
        Assert.Equal(6, (await _frames.FindFrameIdsAsync(new Abstractions.FrameFilter { ProcessName = "chrome" })).Count);
    }

    [Fact]
    public async Task APreviewCountsWithoutDestroying()
    {
        await SeedAsync();

        var preview = await NewForgetService().PreviewAsync(ForgetRequest.ForApp("notepad"));

        Assert.Equal(12, preview.Count);
        Assert.Equal("notepad", preview.Apps.Single().App);
        Assert.Equal(Base, preview.Earliest);
        Assert.Equal(Base.AddMinutes(11), preview.Latest);
        Assert.Equal(12, (await _frames.FindFrameIdsAsync(new Abstractions.FrameFilter { ProcessName = "notepad" })).Count);
    }

    [Fact]
    public async Task ImageFilesAreGoneAfterForgetting()
    {
        await SeedAsync();

        var ids = await _frames.FindFrameIdsAsync(new Abstractions.FrameFilter { ProcessName = "notepad" });
        var paths = new List<string>();
        foreach (var id in ids)
        {
            paths.Add(_files.ResolveImage((await _frames.GetAsync(id))!.ImagePath));
        }

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.True(File.Exists(p)));

        await NewForgetService().ForgetAsync(ForgetRequest.ForApp("notepad"));

        Assert.All(paths, p => Assert.False(File.Exists(p), $"{p} survived"));
    }

    [Fact]
    public void OverwritingReplacesEveryByteWithZero()
    {
        // The unlink that follows makes the overwrite unobservable through the file system,
        // so the overwrite is checked on its own, before anything is deleted.
        var path = Path.Combine(_root, "shred-me.png");
        var content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat($"{SecretPhrase} ", 30_000)));
        File.WriteAllBytes(path, content);

        FrameFileStore.Overwrite(path);

        var after = File.ReadAllBytes(path);
        Assert.Equal(content.Length, after.Length);
        Assert.All(after, b => Assert.Equal(0, b));
    }
}
