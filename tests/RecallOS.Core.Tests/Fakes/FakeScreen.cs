using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;

namespace RecallOS.Core.Tests.Fakes;

/// <summary>
/// A screen the test controls completely.
/// </summary>
/// <remarks>
/// Substituting the capture device is what lets the pipeline, the scheduler and the
/// end-to-end search path be tested deterministically — and means the suite never reads or
/// stores anyone's real desktop. It also makes the rendered content known exactly, so a
/// test can assert that specific words survive capture, OCR, indexing and retrieval.
/// </remarks>
internal sealed class FakeScreen : IScreenCaptureService
{
    private int _captureCount;

    /// <summary>Text drawn into each frame. Changing it changes what OCR should read back.</summary>
    public string Text { get; set; } = string.Empty;

    public Color Fill { get; set; } = Color.White;

    public string? WindowTitle { get; set; } = "Example Window";

    public string? ProcessName { get; set; } = "chrome";

    public TimeSpan IdleTime { get; set; } = TimeSpan.Zero;

    public bool Locked { get; set; }

    /// <summary>
    /// When true, every frame gets a unique mark so the perceptual hash differs. Without
    /// it the deduplicator would correctly drop repeat captures of an unchanging screen.
    /// </summary>
    public bool VaryEachFrame { get; set; }

    public int CaptureCount => Volatile.Read(ref _captureCount);

    public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _captureCount);

        var bitmap = new Bitmap(760, 240, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Fill);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // A perceptual hash measures brightness gradients between neighbouring pixels,
            // so a uniformly filled image hashes to zero whatever colour it is. Real screens
            // always have structure; without some here, two differently coloured blank
            // frames would be indistinguishable to the deduplicator and the fake would be
            // testing something no user could ever encounter.
            using (var contrast = new SolidBrush(Color.FromArgb(255 - Fill.R, 255 - Fill.G, 255 - Fill.B)))
            {
                graphics.FillRectangle(contrast, 30, 96, 240, 28);
            }

            if (VaryEachFrame)
            {
                // Bars across the lower half of the frame. A perceptual hash downsamples to
                // 9x8 before comparing, so a change confined to one small region can survive
                // the similarity tolerance; spreading it across the full width alters enough
                // cells to guarantee each frame reads as a genuinely different screen.
                //
                // Drawn before the text and kept clear of it, so a frame can be both unique
                // to the deduplicator and still legible to OCR.
                for (var bar = 0; bar < 8; bar++)
                {
                    var shade = (index * 53 + bar * 31) % 256;
                    using var brush = new SolidBrush(Color.FromArgb(shade, shade, 255 - shade));
                    graphics.FillRectangle(brush, bar * 95, 130, 95, 110);
                }
            }

            if (Text.Length > 0)
            {
                using var font = new Font("Segoe UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.Black);
                graphics.DrawString(Text, font, brush, new RectangleF(20, 20, 720, 100));
            }
        }

        return Task.FromResult(new CapturedImage
        {
            Bitmap = bitmap,
            CapturedAt = DateTimeOffset.UtcNow,
            Target = request.Target,
            WindowTitle = WindowTitle,
            ProcessName = ProcessName
        });
    }

    public ForegroundWindowInfo GetForegroundWindow() => new(1, WindowTitle, ProcessName);

    public TimeSpan GetUserIdleTime() => IdleTime;

    public bool IsSessionLocked() => Locked;
}

/// <summary>A capture device that always fails, for testing that failures do not stop recording.</summary>
internal sealed class ThrowingScreen : IScreenCaptureService
{
    public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("the display adapter went away");

    public ForegroundWindowInfo GetForegroundWindow() => ForegroundWindowInfo.None;

    public TimeSpan GetUserIdleTime() => TimeSpan.Zero;

    public bool IsSessionLocked() => false;
}
