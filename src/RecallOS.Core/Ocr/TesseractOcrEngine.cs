using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;
using Tesseract;
using TesseractRect = Tesseract.Rect;

namespace RecallOS.Core.Ocr;

/// <summary>
/// Tesseract-backed text extraction, with word geometry.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="TesseractEngine"/> is expensive to build (it maps the language model) and
/// is not thread-safe, so exactly one is created lazily and every recognition takes a
/// semaphore. OCR is the slowest stage of the pipeline by an order of magnitude, which is
/// precisely why it runs off the capture path in a background queue rather than inline.
/// </para>
/// <para>
/// Word bounding boxes are collected as well as the text. They cost almost nothing to read
/// while the page is open, and they are what later makes it possible to highlight a search
/// hit on the replayed screenshot instead of only listing it.
/// </para>
/// </remarks>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private readonly TessDataLocator _locator;
    private readonly ILogger<TesseractOcrEngine> _logger;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly string _languages;

    private TesseractEngine? _engine;
    private bool _initialized;
    private bool _disposed;

    public TesseractOcrEngine(
        TessDataLocator locator,
        string languages = "eng",
        ILogger<TesseractOcrEngine>? logger = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _languages = string.IsNullOrWhiteSpace(languages) ? "eng" : languages;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TesseractOcrEngine>.Instance;
    }

    public string Name => $"tesseract:{_languages}";

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _engine is not null;
        }
    }

    public string? UnavailableReason { get; private set; }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        return await RunAsync(
            () => Pix.LoadFromFile(imagePath),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // Tesseract 5 moved its System.Drawing bridge into a separate package. Rather than
        // take that dependency for one conversion, the bitmap is encoded to an in-memory
        // PNG, which Leptonica reads directly. Lossless, so recognition is unaffected.
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        var bytes = buffer.ToArray();

        return await RunAsync(
            () => Pix.LoadFromMemory(bytes),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OcrResult> RunAsync(Func<Pix> loadImage, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (_engine is null)
            {
                return new OcrResult { Engine = Name };
            }

            return await Task.Run(
                () =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    using var pix = loadImage();
                    using var page = _engine.Process(pix);

                    var text = page.GetText() ?? string.Empty;
                    var words = ReadWords(page);
                    stopwatch.Stop();

                    return new OcrResult
                    {
                        Text = OcrTextNormalizer.Normalize(text),
                        // Tesseract reports 0..100; the rest of the system works in 0..1.
                        Confidence = Math.Clamp(page.GetMeanConfidence(), 0f, 1f),
                        Engine = Name,
                        Words = words,
                        Duration = stopwatch.Elapsed
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private static IReadOnlyList<OcrWord> ReadWords(Page page)
    {
        var words = new List<OcrWord>();

        try
        {
            using var iterator = page.GetIterator();
            iterator.Begin();

            var lineIndex = 0;
            var blockIndex = 0;

            do
            {
                if (iterator.IsAtBeginningOf(PageIteratorLevel.Block))
                {
                    blockIndex++;
                }

                if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine))
                {
                    lineIndex++;
                }

                var text = iterator.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var bounds = iterator.TryGetBoundingBox(PageIteratorLevel.Word, out TesseractRect rect)
                    ? new PixelRect(rect.X1, rect.Y1, rect.Width, rect.Height)
                    : default;

                words.Add(new OcrWord(
                    text.Trim(),
                    bounds,
                    Math.Clamp(iterator.GetConfidence(PageIteratorLevel.Word) / 100d, 0d, 1d),
                    lineIndex,
                    blockIndex));
            }
            while (iterator.Next(PageIteratorLevel.Word));
        }
        catch (Exception)
        {
            // Geometry is an enhancement, not the product. If the iterator misbehaves on an
            // odd page the caller still gets the full text from GetText().
            return words;
        }

        return words;
    }

    /// <summary>
    /// Build the engine on first use. Failure is recorded, not thrown: the app must keep
    /// capturing and stay usable as a screenshot timeline even with no OCR installed.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var dataPath = _locator.Resolve(_languages);
        if (dataPath is null)
        {
            UnavailableReason =
                $"No Tesseract language data for '{_languages}'. Install it from Settings to enable text search.";
            _logger.LogWarning("{Reason}", UnavailableReason);
            return;
        }

        try
        {
            _engine = new TesseractEngine(dataPath, _languages, EngineMode.Default);

            // Screens are dense, mixed-layout pages: headers, sidebars, code, chat. Auto
            // segmentation handles that far better than assuming a single column of prose.
            _engine.DefaultPageSegMode = PageSegMode.Auto;
            UnavailableReason = null;
            _logger.LogInformation("Tesseract initialised from {DataPath} for '{Languages}'.", dataPath, _languages);
        }
        catch (Exception ex)
        {
            _engine = null;
            UnavailableReason = $"Tesseract failed to start: {ex.Message}";
            _logger.LogError(ex, "Tesseract initialisation failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _engine = null;
        _engineLock.Dispose();
    }
}

/// <summary>
/// Stands in when OCR is switched off. Keeps the pipeline free of null checks: frames are
/// still captured, stored and browsable, they are simply marked as having no text.
/// </summary>
public sealed class NullOcrEngine : IOcrEngine
{
    public string Name => "none";

    public bool IsAvailable => false;

    public string? UnavailableReason => "Text extraction is turned off.";

    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(OcrResult.Empty);

    public Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default) =>
        Task.FromResult(OcrResult.Empty);

    public void Dispose()
    {
    }
}
