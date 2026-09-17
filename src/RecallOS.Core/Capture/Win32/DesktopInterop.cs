using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using RecallOS.Core.Abstractions;

namespace RecallOS.Core.Capture.Win32;

/// <summary>
/// Thin, safe wrappers over <see cref="NativeMethods"/>. Everything here returns managed
/// types and swallows the failure modes that are normal on a live desktop: windows close
/// between two calls, the shell hands out handles that die immediately, and a locked
/// session refuses DC access. None of those are exceptional enough to stop recording.
/// </summary>
internal static class DesktopInterop
{
    /// <summary>The bounding box of every monitor combined. The origin can be negative.</summary>
    internal static Rectangle GetVirtualScreenBounds()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        // A zero-size virtual screen means the metrics call failed (session 0, for example).
        return width <= 0 || height <= 0
            ? new Rectangle(0, 0, 1920, 1080)
            : new Rectangle(x, y, width, height);
    }

    /// <summary>Bounds and device name of the monitor that hosts <paramref name="window"/>.</summary>
    internal static (Rectangle Bounds, string? DeviceName) GetMonitorForWindow(nint window)
    {
        var monitor = window != 0
            ? NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : NativeMethods.MonitorFromPoint(default, NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (monitor == 0)
        {
            return (GetVirtualScreenBounds(), null);
        }

        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
            szDevice = string.Empty
        };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return (GetVirtualScreenBounds(), null);
        }

        var rect = info.rcMonitor;
        return (new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height), info.szDevice);
    }

    /// <summary>
    /// The window rectangle as the user perceives it. Win32 reports a rectangle that
    /// includes an invisible drop-shadow border on composited windows, so DWM is asked
    /// first and the raw rect is only a fallback.
    /// </summary>
    internal static Rectangle GetWindowBounds(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return Rectangle.Empty;
        }

        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT frame,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && frame.Width > 0
            && frame.Height > 0)
        {
            return new Rectangle(frame.Left, frame.Top, frame.Width, frame.Height);
        }

        return NativeMethods.GetWindowRect(window, out var rect) && rect.Width > 0 && rect.Height > 0
            ? new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height)
            : Rectangle.Empty;
    }

    internal static string? GetWindowTitle(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return null;
        }

        var length = NativeMethods.GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowTextW(window, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : null;
    }

    /// <summary>Process name without the extension, or null when the process is gone or protected.</summary>
    internal static string? GetWindowProcessName(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        if (NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // The process exited between the handle lookup and here.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            // Elevated or protected process; the title is still useful on its own.
            return null;
        }
    }

    internal static ForegroundWindowInfo GetForegroundWindowInfo()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == 0)
        {
            return ForegroundWindowInfo.None;
        }

        return new ForegroundWindowInfo(handle, GetWindowTitle(handle), GetWindowProcessName(handle));
    }

    /// <summary>Time since the last input event anywhere in the session.</summary>
    internal static TimeSpan GetIdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values wrap every ~49.7 days; unsigned subtraction stays correct across the wrap.
        var elapsed = unchecked(NativeMethods.GetTickCount() - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>
    /// True when the input desktop cannot be opened, which is what a locked workstation,
    /// a UAC prompt or the login screen look like from a normal user process.
    /// </summary>
    internal static bool IsSessionLocked()
    {
        const uint DESKTOP_SWITCHDESKTOP = 0x0100;

        var desktop = NativeMethods.OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (desktop == 0)
        {
            return true;
        }

        NativeMethods.CloseDesktop(desktop);
        return false;
    }

    /// <summary>Paint the current mouse pointer onto a device context, offset into image space.</summary>
    internal static void DrawCursor(nint hdc, int originX, int originY)
    {
        var cursorInfo = new NativeMethods.CURSORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>()
        };

        if (!NativeMethods.GetCursorInfo(ref cursorInfo) || cursorInfo.flags != NativeMethods.CURSOR_SHOWING)
        {
            return;
        }

        // CopyIcon gives us a handle whose lifetime we control; the live one can be recycled.
        var icon = NativeMethods.CopyIcon(cursorInfo.hCursor);
        if (icon == 0)
        {
            return;
        }

        try
        {
            if (!NativeMethods.GetIconInfo(icon, out var iconInfo))
            {
                return;
            }

            if (iconInfo.hbmMask != 0)
            {
                NativeMethods.DeleteObject(iconInfo.hbmMask);
            }

            if (iconInfo.hbmColor != 0)
            {
                NativeMethods.DeleteObject(iconInfo.hbmColor);
            }

            // The hotspot is where the pointer actually points; drawing at the raw
            // position would offset the arrow by its own tip.
            var x = cursorInfo.ptScreenPos.X - originX - iconInfo.xHotspot;
            var y = cursorInfo.ptScreenPos.Y - originY - iconInfo.yHotspot;
            NativeMethods.DrawIconEx(hdc, x, y, icon, 0, 0, 0, 0, NativeMethods.DI_NORMAL);
        }
        finally
        {
            NativeMethods.DestroyIcon(icon);
        }
    }
}
