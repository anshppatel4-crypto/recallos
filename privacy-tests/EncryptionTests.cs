using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Search;
using RecallOS.Core.Security;
using RecallOS.Core.Storage;
using RecallOS.Core.Tests.Fakes;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// The promise is that nothing readable is left on disk, so these read the disk.
/// </summary>
public sealed class EncryptionTests : IAsyncLifetime
{
    private const string Secret = "zebrafrobnicatorsecretword";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "recallos-tests", Guid.NewGuid().ToString("N"));
    private RecallPaths _paths = null!;
    private StoreKeys _keys = null!;

    public Task InitializeAsync()
    {
        _paths = new RecallPaths(_root).EnsureCreated();
        _keys = StoreKeys.Generate();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static CaptureFrame Frame(string text) => new()
    {
        CapturedAt = DateTimeOffset.UtcNow,
        ImagePath = $"2026/01/01/{Guid.NewGuid():N}.png",
        Width = 100,
        Height = 100,
        FileSizeBytes = 1,
        WindowTitle = "Secret window " + text,
        ProcessName = "chrome",
        OcrStatus = OcrStatus.Completed,
        Text = text
    };

    private async Task SeedAsync(SqliteConnectionFactory factory, int count = 25)
    {
        await new DatabaseBootstrapper(factory).InitializeAsync();
        var frames = new FrameRepository(factory);
        for (var i = 0; i < count; i++)
        {
            var inserted = await frames.InsertAsync(Frame($"{Secret} number {i}"));
            await frames.ReplaceChunksAsync(inserted.Id, [new TextChunk { FrameId = inserted.Id, Ordinal = 0, Text = Secret }]);
        }
    }

    /// <summary>Every file in the store, as raw bytes, checked for a needle in ASCII and UTF-16.</summary>
    private void AssertNotOnDisk(string needle)
    {
        var ascii = Encoding.ASCII.GetBytes(needle);
        var wide = Encoding.Unicode.GetBytes(needle);
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            byte[] data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }

            Assert.True(data.AsSpan().IndexOf(ascii) < 0 && data.AsSpan().IndexOf(wide) < 0, $"'{needle}' is readable in {path}");
        }
    }

    // ---- the database --------------------------------------------------------------------

    [Fact]
    public async Task ANewEncryptedStoreLeavesNothingReadableInTheDatabaseOrItsLog()
    {
        using var factory = new SqliteConnectionFactory(_paths, _keys);
        await SeedAsync(factory);

        Assert.True(factory.IsEncrypted);
        Assert.False(DatabaseEncryption.IsPlaintext(_paths.DatabaseFile));

        // Before any checkpoint, while the newest rows are still in the write-ahead log.
        AssertNotOnDisk(Secret);

        var hits = await new HybridSearchService(factory, new FrameRepository(factory), new HashingEmbeddingProvider(128))
            .SearchAsync(SearchQueryParser.Parse(Secret, SearchMode.Keyword));
        Assert.NotEmpty(hits.Hits);
    }

    [Fact]
    public async Task TheDatabaseOpensWithNeitherTheWrongKeyNorNoKey()
    {
        using (var factory = new SqliteConnectionFactory(_paths, _keys))
        {
            await SeedAsync(factory, 1);
        }

        SqliteConnection.ClearAllPools();

        Assert.True(DatabaseEncryption.CanOpen(_paths.DatabaseFile, _keys));
        Assert.False(DatabaseEncryption.CanOpen(_paths.DatabaseFile, StoreKeys.Generate()));

        using var bare = new SqliteConnection($"Data Source={_paths.DatabaseFile};Pooling=false");
        bare.Open();
        using var command = bare.CreateCommand();
        command.CommandText = "SELECT count(*) FROM frames;";
        Assert.Throws<SqliteException>(() => command.ExecuteScalar());
    }

    // ---- moving an existing store ---------------------------------------------------------

    [Fact]
    public async Task AnExistingPlainDatabaseIsEncryptedWithEverythingIntact()
    {
        using (var plain = new SqliteConnectionFactory(_paths))
        {
            await SeedAsync(plain);
            Assert.False(plain.IsEncrypted);
        }

        SqliteConnection.ClearAllPools();
        Assert.True(DatabaseEncryption.IsPlaintext(_paths.DatabaseFile));

        Assert.Equal(DatabaseEncryptionOutcome.Encrypted, DatabaseEncryption.EncryptIfNeeded(_paths, _keys));

        Assert.False(File.Exists(_paths.DatabaseFile + ".plain-old"));
        Assert.False(File.Exists(_paths.DatabaseFile + ".encrypting"));
        AssertNotOnDisk(Secret);

        using var factory = new SqliteConnectionFactory(_paths, _keys);
        Assert.True(factory.IsEncrypted);
        await new DatabaseBootstrapper(factory).InitializeAsync();

        var repository = new FrameRepository(factory);
        Assert.Equal(25, (await repository.GetRecentAsync(100)).Count);

        // The full-text index came across and still takes new rows.
        await repository.InsertAsync(Frame("newwordafterupgrade"));
        var search = new HybridSearchService(factory, repository, new HashingEmbeddingProvider(128));
        Assert.NotEmpty((await search.SearchAsync(SearchQueryParser.Parse(Secret, SearchMode.Keyword))).Hits);
        Assert.NotEmpty((await search.SearchAsync(SearchQueryParser.Parse("newwordafterupgrade", SearchMode.Keyword))).Hits);

        // And it is a one-time move.
        factory.Dispose();
        SqliteConnection.ClearAllPools();
        Assert.Equal(DatabaseEncryptionOutcome.AlreadyEncrypted, DatabaseEncryption.EncryptIfNeeded(_paths, _keys));
    }

    [Fact]
    public async Task IfEncryptingFailsThePlainDatabaseIsUntouchedAndStillWorks()
    {
        using (var plain = new SqliteConnectionFactory(_paths))
        {
            await SeedAsync(plain, 3);
        }

        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(_paths.DatabaseFile);

        // A folder where the encrypted copy should go makes the export fail part way.
        Directory.CreateDirectory(_paths.DatabaseFile + ".encrypting");

        Assert.Equal(DatabaseEncryptionOutcome.Failed, DatabaseEncryption.EncryptIfNeeded(_paths, _keys));

        Directory.Delete(_paths.DatabaseFile + ".encrypting");
        Assert.True(DatabaseEncryption.IsPlaintext(_paths.DatabaseFile));
        Assert.True(File.ReadAllBytes(_paths.DatabaseFile).Length >= before.Length / 2);

        using var factory = new SqliteConnectionFactory(_paths, _keys);
        Assert.False(factory.IsEncrypted);
        Assert.Equal(3, (await new FrameRepository(factory).GetRecentAsync(100)).Count);
    }

    [Fact]
    public async Task APowerCutBetweenTheRenamesPutsTheOriginalBack()
    {
        using (var plain = new SqliteConnectionFactory(_paths))
        {
            await SeedAsync(plain, 3);
        }

        SqliteConnection.ClearAllPools();
        File.Move(_paths.DatabaseFile, _paths.DatabaseFile + ".plain-old");

        Assert.Equal(DatabaseEncryptionOutcome.Encrypted, DatabaseEncryption.EncryptIfNeeded(_paths, _keys));

        using var factory = new SqliteConnectionFactory(_paths, _keys);
        Assert.Equal(3, (await new FrameRepository(factory).GetRecentAsync(100)).Count);
    }

    [Fact]
    public async Task APowerCutAfterTheSwapStillDestroysThePlainFile()
    {
        using (var encrypted = new SqliteConnectionFactory(_paths, _keys))
        {
            await SeedAsync(encrypted, 1);
        }

        SqliteConnection.ClearAllPools();
        File.WriteAllText(_paths.DatabaseFile + ".plain-old", Secret + "-leftover");

        DatabaseEncryption.Recover(_paths);

        Assert.False(File.Exists(_paths.DatabaseFile + ".plain-old"));
        Assert.True(DatabaseEncryption.CanOpen(_paths.DatabaseFile, _keys));
    }

    // ---- the key --------------------------------------------------------------------------

    [Fact]
    public void TheKeyFileOpensOnlyForThisAccountAndHoldsNoPlainKey()
    {
        var first = StoreKeyring.OpenOrCreate(_paths);
        var again = StoreKeyring.OpenOrCreate(_paths);

        Assert.Equal(first.Master, again.Master);
        Assert.True(File.ReadAllBytes(_paths.KeyFile).AsSpan().IndexOf(first.Master) < 0);
    }

    [Fact]
    public void ADamagedKeyFileLocksTheStoreRatherThanReplacingTheKey()
    {
        StoreKeyring.OpenOrCreate(_paths);
        var data = File.ReadAllBytes(_paths.KeyFile);
        data[^1] ^= 0xFF;
        File.WriteAllBytes(_paths.KeyFile, data);

        Assert.Throws<StoreLockedException>(() => StoreKeyring.OpenOrCreate(_paths));
    }

    [Fact]
    public async Task AMissingKeyFileWithAnEncryptedDatabaseLocksItRatherThanMakingANewKey()
    {
        using (var factory = new SqliteConnectionFactory(_paths, _keys))
        {
            await SeedAsync(factory, 1);
        }

        SqliteConnection.ClearAllPools();
        Assert.Throws<StoreLockedException>(() => StoreKeyring.OpenOrCreate(_paths));
    }

    [Fact]
    public async Task TheBackupCodeRestoresAccessAfterTheKeyFileIsLost()
    {
        var code = StoreKeyring.ToBackupCode(_keys);
        using (var factory = new SqliteConnectionFactory(_paths, _keys))
        {
            await SeedAsync(factory, 1);
        }

        SqliteConnection.ClearAllPools();

        // Case, spaces and look-alike characters are forgiven; a wrong character is not.
        Assert.True(StoreKeyring.TryParseBackupCode(code.ToLowerInvariant().Replace('-', ' '), out var restored));
        Assert.Equal(_keys.Master, restored!.Master);
        Assert.True(DatabaseEncryption.CanOpen(_paths.DatabaseFile, restored));

        var swap = code[0] == '2' ? '3' : '2';
        Assert.False(StoreKeyring.TryParseBackupCode(swap + code[1..], out _));
        Assert.False(StoreKeyring.TryParseBackupCode("not a code", out _));

        StoreKeyring.Save(_paths, restored);
        Assert.Equal(_keys.Master, StoreKeyring.OpenOrCreate(_paths).Master);
    }

    // ---- image files ----------------------------------------------------------------------

    private static Bitmap Solid(int width = 64, int height = 48)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.CornflowerBlue);
        return bitmap;
    }

    [Fact]
    public async Task ACapturedImageAndItsThumbnailAreEncryptedAndStillLoad()
    {
        var files = new FrameFileStore(_paths, cipher: new FrameCipher(_keys));
        using var bitmap = Solid();

        var stored = await files.SaveAsync(bitmap, DateTimeOffset.Now, 32);

        foreach (var path in new[] { files.ResolveImage(stored.RelativeImagePath), files.ResolveThumbnail(stored.RelativeThumbnailPath!) })
        {
            Assert.True(FrameCipher.IsEncrypted(File.ReadAllBytes(path)));
            using var loaded = files.LoadBitmap(path);
            Assert.True(loaded.Width > 0);
        }

        using var full = files.LoadBitmap(files.ResolveImage(stored.RelativeImagePath));
        Assert.Equal(bitmap.Width, full.Width);
        Assert.Equal(bitmap.GetPixel(5, 5).ToArgb(), full.GetPixel(5, 5).ToArgb());
    }

    [Fact]
    public async Task AnAlteredImageIsRefusedRatherThanShown()
    {
        var files = new FrameFileStore(_paths, cipher: new FrameCipher(_keys));
        using var bitmap = Solid();
        var stored = await files.SaveAsync(bitmap, DateTimeOffset.Now, 32);
        var path = files.ResolveImage(stored.RelativeImagePath);

        var data = File.ReadAllBytes(path);
        data[^1] ^= 0x01;
        File.WriteAllBytes(path, data);

        Assert.ThrowsAny<CryptographicException>(() => files.ReadBytes(path));
    }

    [Fact]
    public void AnImageCannotBeDecryptedWithAnotherStoresKey()
    {
        var encrypted = new FrameCipher(_keys).Encrypt("pixels"u8);
        Assert.ThrowsAny<CryptographicException>(() => new FrameCipher(StoreKeys.Generate()).Decrypt(encrypted));
    }

    [Fact]
    public async Task OldPlainImagesAreEncryptedInTheBackgroundAndLeftoversAreCleared()
    {
        // As an older version would have left them.
        var plainStore = new FrameFileStore(_paths);
        using var bitmap = Solid();
        var one = await plainStore.SaveAsync(bitmap, DateTimeOffset.Now, 32);
        var two = await plainStore.SaveAsync(bitmap, DateTimeOffset.Now, 32);
        var onePath = plainStore.ResolveImage(one.RelativeImagePath);
        var twoPath = plainStore.ResolveImage(two.RelativeImagePath);
        Assert.False(FrameCipher.IsEncrypted(File.ReadAllBytes(onePath)));

        // As a power cut in the middle of encrypting would.
        var leftoverPlain = onePath + FrameFileStore.PlainSuffix;
        var leftoverTemp = twoPath + FrameFileStore.TemporarySuffix;
        File.WriteAllBytes(leftoverPlain, Encoding.ASCII.GetBytes(Secret));
        File.WriteAllBytes(leftoverTemp, [1, 2, 3]);

        var files = new FrameFileStore(_paths, cipher: new FrameCipher(_keys));

        // Readable before it has been reached, as well as after.
        using (files.LoadBitmap(onePath)) { }

        var encryptor = new StoreFileEncryptor(files, _paths);
        await encryptor.RunAsync();

        Assert.True(encryptor.IsComplete);
        Assert.Equal(4, encryptor.EncryptedThisRun);
        Assert.False(File.Exists(leftoverPlain));
        Assert.False(File.Exists(leftoverTemp));

        foreach (var path in Directory.EnumerateFiles(_paths.Frames, "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(_paths.Thumbnails, "*", SearchOption.AllDirectories)))
        {
            Assert.True(FrameCipher.IsEncrypted(File.ReadAllBytes(path)), path);
        }

        using var loaded = files.LoadBitmap(twoPath);
        Assert.Equal(bitmap.Width, loaded.Width);

        // Running again does nothing.
        var second = new StoreFileEncryptor(files, _paths);
        await second.RunAsync();
        Assert.Equal(0, second.EncryptedThisRun);
    }

    [Fact]
    public async Task TheWholeCapturePathWorksOnAnEncryptedStore()
    {
        // Captured, read, redacted, indexed and searched, with nothing plain left behind.
        const string card = "4111111111111111";
        using var factory = new SqliteConnectionFactory(_paths, _keys);
        await new DatabaseBootstrapper(factory).InitializeAsync();

        var repository = new FrameRepository(factory);
        var files = new FrameFileStore(_paths, cipher: new FrameCipher(_keys));
        var settings = new SqliteSettingsStore(factory);
        await settings.LoadAsync();

        var screen = new FakeScreen { WindowTitle = "Checkout " + Secret, Text = "anything" };
        var pipeline = new CapturePipeline(screen, repository, files, settings);
        var embeddings = new HashingEmbeddingProvider(128);
        var engine = new ScriptedOcr($"{Secret} pays with {card}");

        await using var ocr = new OcrQueueProcessor(repository, engine, files, new TextChunker(), embeddings, new HeuristicIntentClassifier(), settings);
        pipeline.FrameCaptured += (_, frame) => ocr.Enqueue(frame.Id);
        ocr.Start();

        var outcome = await pipeline.CaptureAsync(CaptureRequest.Manual());
        var id = outcome.Frame!.Id;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && (await repository.GetAsync(id))?.OcrStatus != OcrStatus.Completed)
        {
            await Task.Delay(50);
        }

        var frame = await repository.GetAsync(id);
        Assert.Equal(OcrStatus.Completed, frame!.OcrStatus);
        Assert.True(engine.SawBitmap, "The engine should be given the decrypted picture, not a file path.");
        Assert.DoesNotContain(card, frame.Text);
        Assert.Contains(Secret, frame.Text);

        var search = new HybridSearchService(factory, repository, embeddings);
        Assert.Contains((await search.SearchAsync(SearchQueryParser.Parse(Secret, SearchMode.Keyword))).Hits, h => h.Frame.Id == id);

        AssertNotOnDisk(Secret);
        Assert.True(FrameCipher.IsEncrypted(File.ReadAllBytes(files.ResolveImage(frame.ImagePath))));
    }

    private sealed class ScriptedOcr(string text) : IOcrEngine
    {
        public bool SawBitmap { get; private set; }

        public string Name => "scripted";

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An encrypted frame has no readable path.");

        public Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
        {
            SawBitmap = true;
            return Task.FromResult(new OcrResult { Text = text, Confidence = 0.9, Engine = Name });
        }

        public void Dispose()
        {
        }
    }
}
