using RecallOS.Core.Capture;
using RecallOS.Core.Models;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Exercises the real Win32 capture path.
/// </summary>
/// <remarks>
/// Nothing here is written to disk: each capture is inspected in memory and disposed, so
/// running the suite never adds anything to a user's store. The tests degrade to a no-op
/// when there is no interactive desktop (a locked session or a headless build agent),
/// because BitBlt legitimately cannot succeed there and failing would be a false alarm.
/// </remarks>
public class ScreenCaptureTests
{
    private static bool HasDesktop(GdiScreenCaptureService capture) => !capture.IsSessionLocked();

    [Fact]
    public async Task CapturingAllScreensProducesANonEmptyBitmap()
    {
        var capture = new GdiScreenCaptureService();
        if (!HasDesktop(capture))
        {
            return;
        }

        using var image = await capture.CaptureAsync(CaptureRequest.Manual());

        Assert.True(image.Width > 0);
        Assert.True(image.Height > 0);
        Assert.Equal(CaptureTarget.AllScreens, image.Target);
        Assert.True(image.CapturedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CapturingTheActiveScreenProducesABitmapNoLargerThanTheDesktop()
    {
        var capture = new GdiScreenCaptureService();
        if (!HasDesktop(capture))
        {
            return;
        }

        using var everything = await capture.CaptureAsync(CaptureRequest.Manual(CaptureTarget.AllScreens));
        using var single = await capture.CaptureAsync(CaptureRequest.Manual(CaptureTarget.ActiveScreen));

        Assert.True(single.Width > 0 && single.Height > 0);
        Assert.True(single.Width <= everything.Width);
        Assert.True(single.Height <= everything.Height);
    }

    [Fact]
    public async Task ACapturedBitmapCanBeHashed()
    {
        // The capture and change-detection paths have to agree on pixel format; this is
        // where a mismatch between them would surface.
        var capture = new GdiScreenCaptureService();
        if (!HasDesktop(capture))
        {
            return;
        }

        using var image = await capture.CaptureAsync(CaptureRequest.Manual());

        var hash = PerceptualHash.Compute(image.Bitmap);

        Assert.Equal(0, PerceptualHash.Distance(hash, hash));
    }

    [Fact]
    public async Task RepeatedCapturesDoNotLeakGdiObjects()
    {
        // Every capture allocates a device context, a bitmap and a selected object. A leak
        // here would exhaust the process GDI quota (10,000 objects by default) within hours
        // of continuous recording, so the loop is the whole point of the test.
        //
        // GDI objects are counted rather than total process handles: the handle count moves
        // with file and socket activity elsewhere in the process, which made the assertion
        // report leaks that were not there.
        var capture = new GdiScreenCaptureService();
        if (!HasDesktop(capture))
        {
            return;
        }

        // Warm up first: the first capture faults in native libraries and caches that are
        // allocated once and would otherwise read as a leak.
        for (var i = 0; i < 5; i++)
        {
            using var warmup = await capture.CaptureAsync(CaptureRequest.Manual());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GdiObjectCount();

        for (var i = 0; i < 20; i++)
        {
            using var image = await capture.CaptureAsync(CaptureRequest.Manual());
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GdiObjectCount();

        // A per-capture leak would add at least one object for each of the 20 passes.
        Assert.True(after - before < 20, $"GDI objects grew from {before} to {after}");
    }

    private static int GdiObjectCount()
    {
        const int GR_GDIOBJECTS = 0;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return GetGuiResources(process.Handle, GR_GDIOBJECTS);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetGuiResources(nint hProcess, int uiFlags);

    [Fact]
    public void ForegroundWindowInformationIsReadable()
    {
        var capture = new GdiScreenCaptureService();

        var foreground = capture.GetForegroundWindow();

        // The specific window depends on what is running; only the shape of the result is
        // knowable, and it must never throw.
        if (foreground.IsValid)
        {
            Assert.NotEqual(0, foreground.Handle);
        }
    }

    [Fact]
    public void IdleTimeIsNonNegativeAndPlausible()
    {
        var idle = new GdiScreenCaptureService().GetUserIdleTime();

        Assert.True(idle >= TimeSpan.Zero);
        // GetTickCount wraps every ~49.7 days; unsigned subtraction should keep this sane.
        Assert.True(idle < TimeSpan.FromDays(50));
    }
}
