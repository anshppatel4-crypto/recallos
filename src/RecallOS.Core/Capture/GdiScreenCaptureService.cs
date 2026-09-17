using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Capture.Win32;
using RecallOS.Core.Models;

namespace RecallOS.Core.Capture;

/// <summary>
/// Captures the desktop with GDI BitBlt.
/// </summary>
/// <remarks>
/// <para>
/// The blit is done into an unmanaged compatible bitmap and only then handed to GDI+,
/// which keeps the copy on the graphics device for as long as possible and makes a
/// full-screen grab cheap enough to run on a timer.
/// </para>
/// <para>
/// A lock serialises captures. Two concurrent BitBlts against the screen DC do not make
/// anything faster -- they contend on the same device -- and serialising keeps the
/// perceptual-hash comparison against "the previous frame" meaningful.
/// </para>
/// </remarks>
public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<GdiScreenCaptureService> _logger;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public GdiScreenCaptureService(ILogger<GdiScreenCaptureService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GdiScreenCaptureService>.Instance;
    }

    public async Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // GDI work is blocking and can take tens of milliseconds on a 4K multi-monitor
            // desktop, so it never runs on the caller's thread.
            return await Task.Run(() => CaptureCore(request), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public ForegroundWindowInfo GetForegroundWindow() => DesktopInterop.GetForegroundWindowInfo();

    public TimeSpan GetUserIdleTime() => DesktopInterop.GetIdleTime();

    public bool IsSessionLocked() => DesktopInterop.IsSessionLocked();

    private CapturedImage CaptureCore(CaptureRequest request)
    {
        var foreground = DesktopInterop.GetForegroundWindowInfo();
        var (bounds, displayName) = ResolveBounds(request.Target, foreground);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ScreenCaptureException(
                $"Resolved an empty capture region for target {request.Target}.");
        }

        var bitmap = BlitRegion(bounds, request.IncludeCursor);

        return new CapturedImage
        {
            Bitmap = bitmap,
            CapturedAt = DateTimeOffset.UtcNow,
            Target = request.Target,
            WindowTitle = foreground.Title,
            ProcessName = foreground.ProcessName,
            DisplayName = displayName
        };
    }

    /// <summary>
    /// Work out what rectangle of the desktop to grab. Window targets degrade gracefully:
    /// a minimised or vanished window falls back to its monitor rather than failing, because
    /// a slightly-wrong frame is worth more to the user than a gap in their history.
    /// </summary>
    private (Rectangle Bounds, string? DisplayName) ResolveBounds(CaptureTarget target, ForegroundWindowInfo foreground)
    {
        switch (target)
        {
            case CaptureTarget.ActiveWindow when foreground.IsValid && !NativeMethods.IsIconic(foreground.Handle):
            {
                var windowBounds = DesktopInterop.GetWindowBounds(foreground.Handle);
                if (windowBounds.Width > 0 && windowBounds.Height > 0)
                {
                    // Clip to the virtual screen: an off-screen window would otherwise
                    // produce a bitmap full of garbage.
                    var clipped = Rectangle.Intersect(windowBounds, DesktopInterop.GetVirtualScreenBounds());
                    if (clipped.Width > 0 && clipped.Height > 0)
                    {
                        var (_, device) = DesktopInterop.GetMonitorForWindow(foreground.Handle);
                        return (clipped, device);
                    }
                }

                _logger.LogDebug("Active window had no usable bounds; falling back to its monitor.");
                goto case CaptureTarget.ActiveScreen;
            }

            case CaptureTarget.ActiveWindow:
            case CaptureTarget.ActiveScreen:
            {
                var (monitorBounds, device) = DesktopInterop.GetMonitorForWindow(foreground.Handle);
                return (monitorBounds, device);
            }

            default:
                return (DesktopInterop.GetVirtualScreenBounds(), null);
        }
    }

    /// <summary>
    /// The actual pixel copy. Every GDI handle acquired here is released in the finally
    /// block: a leak in this method would bleed the process GDI handle quota within hours
    /// of continuous capture.
    /// </summary>
    private static Bitmap BlitRegion(Rectangle bounds, bool includeCursor)
    {
        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            throw new ScreenCaptureException("Could not acquire the screen device context.");
        }

        nint memoryDc = 0;
        nint hBitmap = 0;
        nint previousBitmap = 0;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                throw new ScreenCaptureException("Could not create a compatible device context.");
            }

            hBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (hBitmap == 0)
            {
                throw new ScreenCaptureException(
                    $"Could not allocate a {bounds.Width}x{bounds.Height} bitmap for capture.");
            }

            previousBitmap = NativeMethods.SelectObject(memoryDc, hBitmap);

            // CAPTUREBLT is what makes layered windows (tooltips, Acrylic surfaces,
            // most Electron chrome) appear instead of showing through as black.
            var copied = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                bounds.Width,
                bounds.Height,
                screenDc,
                bounds.X,
                bounds.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!copied)
            {
                throw new ScreenCaptureException(
                    "BitBlt failed. This normally means the session is locked or on a secure desktop.");
            }

            if (includeCursor)
            {
                DesktopInterop.DrawCursor(memoryDc, bounds.X, bounds.Y);
            }

            // Image.FromHbitmap copies into a managed bitmap, so the GDI handle can go.
            using var shared = Image.FromHbitmap(hBitmap);

            // Normalise to 24bpp RGB: the alpha channel BitBlt produces is meaningless
            // and both PNG encoding and Tesseract are happier without it.
            var result = new Bitmap(shared.Width, shared.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.DrawImageUnscaled(shared, 0, 0);
            }

            return result;
        }
        finally
        {
            if (memoryDc != 0)
            {
                if (previousBitmap != 0)
                {
                    NativeMethods.SelectObject(memoryDc, previousBitmap);
                }

                NativeMethods.DeleteDC(memoryDc);
            }

            if (hBitmap != 0)
            {
                NativeMethods.DeleteObject(hBitmap);
            }

            NativeMethods.ReleaseDC(0, screenDc);
        }
    }
}

/// <summary>Raised when the desktop could not be read. Always recoverable: try again later.</summary>
public sealed class ScreenCaptureException : Exception
{
    public ScreenCaptureException(string message)
        : base(message)
    {
    }

    public ScreenCaptureException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
