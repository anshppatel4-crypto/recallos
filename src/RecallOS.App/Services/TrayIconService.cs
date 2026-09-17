using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// This file draws with GDI+ and hosts a Windows Forms NotifyIcon. It does not import
// System.Windows.Media, so Color, Brush and Pen resolve to the System.Drawing types
// without further qualification.

namespace RecallOS.App.Services;

/// <summary>
/// The notification-area presence: status, a menu, and the way back to the window.
/// </summary>
/// <remarks>
/// RecallOS is meant to behave like part of the operating system, which means it keeps
/// running when its window is closed. That is only acceptable if the user can always see
/// that it is running and stop it in one click -- a background recorder with no visible
/// handle is exactly the thing people are right to distrust. The tray icon is that handle.
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _autoCaptureItem;
    private readonly ToolStripMenuItem _statusItem;
    private bool _disposed;

    public TrayIconService()
    {
        _statusItem = new ToolStripMenuItem("RecallOS") { Enabled = false };
        _captureItem = new ToolStripMenuItem("Capture now");
        _autoCaptureItem = new ToolStripMenuItem("Record continuously") { CheckOnClick = true };

        var openItem = new ToolStripMenuItem("Open RecallOS");
        var exitItem = new ToolStripMenuItem("Quit");

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            openItem,
            _captureItem,
            _autoCaptureItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(recording: false),
            Text = "RecallOS",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _captureItem.Click += (_, _) => CaptureRequested?.Invoke(this, EventArgs.Empty);
        _autoCaptureItem.Click += (_, _) => AutoCaptureToggled?.Invoke(this, _autoCaptureItem.Checked);

        _icon.BalloonTipTitle = "RecallOS";
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? CaptureRequested;

    public event EventHandler<bool>? AutoCaptureToggled;

    /// <summary>Reflect the recorder state in the icon, tooltip and menu.</summary>
    public void SetRecording(bool recording, int frameCount)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _icon.Icon;
        _icon.Icon = CreateIcon(recording);
        previous?.Dispose();

        // The tooltip is capped at 63 characters by the shell; anything longer is dropped
        // silently, tooltip and all.
        var status = recording ? "Recording" : "Paused";
        _icon.Text = Truncate($"RecallOS - {status} - {frameCount:N0} frames", 63);

        _statusItem.Text = $"{status} - {frameCount:N0} frames";

        // Assigning Checked would re-enter the Click handler through CheckOnClick.
        if (_autoCaptureItem.Checked != recording)
        {
            _autoCaptureItem.Checked = recording;
        }
    }

    public void ShowMessage(string message, bool isError = false)
    {
        if (_disposed)
        {
            return;
        }

        _icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
        _icon.BalloonTipText = Truncate(message, 255);
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// Draw the icon rather than ship a resource: it keeps the build self-contained and lets
    /// the glyph carry state. A filled dot means recording, a hollow ring means paused.
    /// </summary>
    private static Icon CreateIcon(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var ring = recording ? Color.FromArgb(0xFF, 0x4C, 0x8D, 0xFF) : Color.FromArgb(0xFF, 0x8A, 0x93, 0xA6);

            using var pen = new Pen(ring, 3f);
            graphics.DrawEllipse(pen, 4, 4, 23, 23);

            if (recording)
            {
                using var fill = new SolidBrush(ring);
                graphics.FillEllipse(fill, 11, 11, 10, 10);
            }
        }

        // The HICON must be cloned: the handle from GetHicon is owned by the caller and
        // destroying the bitmap would invalidate an Icon built directly on it.
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeIcon.DestroyIcon(handle);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Hide before disposing, or the icon lingers in the tray until the user hovers it.
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private static class NativeIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(nint handle);
    }
}
