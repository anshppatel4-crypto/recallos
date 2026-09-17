using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using RecallOS.Core.Common;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Proves the OCR path actually reads text, against the real Tesseract engine.
/// </summary>
/// <remarks>
/// <para>
/// The text is rendered to a bitmap here rather than captured from the screen: it keeps the
/// expected result known exactly, keeps the test deterministic, and means running the suite
/// never reads or stores anyone's actual desktop.
/// </para>
/// <para>
/// Language data is an optional, user-installed asset, so these tests no-op when it is
/// absent instead of failing. A machine without a language pack is a supported
/// configuration, not a broken one — it is precisely the state the app ships in.
/// </para>
/// </remarks>
public sealed class OcrEngineTests
{
    /// <summary>
    /// Resolved against the default store so an installed language pack is picked up.
    /// Returns null when none is installed, which makes every test here skip.
    /// </summary>
    private static TessDataLocator? LocateInstalledLanguage()
    {
        var locator = new TessDataLocator(new RecallPaths());
        return locator.Resolve("eng") is null ? null : locator;
    }

    /// <summary>Render text as a clean, high-contrast page — what screen text looks like.</summary>
    private static Bitmap RenderText(string text, int width = 900, int height = 260)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var font = new Font("Segoe UI", 28, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.Black);
        graphics.DrawString(text, font, brush, new RectangleF(24, 24, width - 48, height - 48));

        return bitmap;
    }

    [Fact]
    public async Task TheEngineReadsRenderedText()
    {
        var locator = LocateInstalledLanguage();
        if (locator is null)
        {
            return;
        }

        using var engine = new TesseractOcrEngine(locator);
        Assert.True(engine.IsAvailable, engine.UnavailableReason);

        using var bitmap = RenderText("The quarterly revenue report is ready for review");

        var result = await engine.RecognizeAsync(bitmap);

        Assert.False(result.IsEmpty);
        Assert.Contains("quarterly", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revenue", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Confidence > 0.5, $"confidence was {result.Confidence:P0}");
        Assert.StartsWith("tesseract:", result.Engine);
    }

    [Fact]
    public async Task WordGeometryIsReportedForEachWord()
    {
        // The bounding boxes are what later allow a search hit to be highlighted on the
        // replayed screenshot rather than merely listed.
        var locator = LocateInstalledLanguage();
        if (locator is null)
        {
            return;
        }

        using var engine = new TesseractOcrEngine(locator);
        using var bitmap = RenderText("Deployment pipeline failed");

        var result = await engine.RecognizeAsync(bitmap);

        Assert.NotEmpty(result.Words);
        Assert.All(result.Words, word =>
        {
            Assert.False(string.IsNullOrWhiteSpace(word.Text));
            Assert.True(word.Bounds.Width > 0, $"'{word.Text}' had no width");
            Assert.True(word.Bounds.Height > 0, $"'{word.Text}' had no height");
        });
    }

    [Fact]
    public async Task RecognisedTextFlowsIntoSearchableChunks()
    {
        // The seam between OCR and retrieval: geometry drives the chunking, so a break here
        // would leave the index subtly wrong rather than obviously broken.
        var locator = LocateInstalledLanguage();
        if (locator is null)
        {
            return;
        }

        using var engine = new TesseractOcrEngine(locator);
        using var bitmap = RenderText("Migration notes for the storage layer rewrite");

        var result = await engine.RecognizeAsync(bitmap);
        var chunks = new TextChunker(minimumSize: 4).Chunk(frameId: 1, result.Text, result.Words);

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, c => c.Text.Contains("torage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnEmptyPageYieldsNoTextRatherThanThrowing()
    {
        var locator = LocateInstalledLanguage();
        if (locator is null)
        {
            return;
        }

        using var engine = new TesseractOcrEngine(locator);
        using var blank = new Bitmap(400, 200, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(blank))
        {
            graphics.Clear(Color.White);
        }

        var result = await engine.RecognizeAsync(blank);

        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void AMissingLanguageIsReportedRatherThanThrown()
    {
        // The shipping default: no language data installed. The app must stay usable.
        var empty = Path.Combine(Path.GetTempPath(), "recallos-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var engine = new TesseractOcrEngine(new TessDataLocator(new RecallPaths(empty)), "zzz");

            Assert.False(engine.IsAvailable);
            Assert.NotNull(engine.UnavailableReason);
        }
        finally
        {
            try
            {
                Directory.Delete(empty, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AnUnavailableEngineReturnsEmptyInsteadOfThrowing()
    {
        var empty = Path.Combine(Path.GetTempPath(), "recallos-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var engine = new TesseractOcrEngine(new TessDataLocator(new RecallPaths(empty)), "zzz");
            using var bitmap = RenderText("anything at all");

            var result = await engine.RecognizeAsync(bitmap);

            Assert.True(result.IsEmpty);
        }
        finally
        {
            try
            {
                Directory.Delete(empty, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void NullEngineIsAlwaysSafeToUse()
    {
        using var engine = new NullOcrEngine();

        Assert.False(engine.IsAvailable);
        Assert.Equal("none", engine.Name);
    }
}
