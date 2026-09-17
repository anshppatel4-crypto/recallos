using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace RecallOS.App.Services;

/// <summary>
/// Registers a system-wide hotkey for capture-now.
/// </summary>
/// <remarks>
/// The point of a memory layer is that you reach for it when something matters, which is
/// precisely when the RecallOS window is not in front. A global hotkey is what makes
/// "capture this" a reflex instead of a task; without it the user has to interrupt what
/// they are doing to record what they are doing.
/// </remarks>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xBEEF;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [Flags]
    internal enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Windows = 0x0008,

        /// <summary>Stops the key auto-repeating while held, which would fire a burst of captures.</summary>
        NoRepeat = 0x4000
    }

    private readonly ILogger<GlobalHotkeyService> _logger;
    private HwndSource? _source;
    private bool _registered;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalHotkeyService>.Instance;
    }

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>Null when registration succeeded, otherwise why it did not.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Bind the hotkey to a window's message loop. Safe to call again to rebind after the
    /// user changes the shortcut.
    /// </summary>
    public bool Register(Window window, string gesture)
    {
        ArgumentNullException.ThrowIfNull(window);

        Unregister();

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        if (!TryParse(gesture, out var modifiers, out var key))
        {
            LastError = $"'{gesture}' is not a shortcut RecallOS understands.";
            _logger.LogWarning("{Error}", LastError);
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            // The HWND does not exist until the window is sourced; callers register from
            // the SourceInitialized handler.
            LastError = "The window is not ready to receive hotkeys yet.";
            return false;
        }

        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            LastError = "Could not attach to the window message loop.";
            return false;
        }

        _source.AddHook(WndProc);

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(handle, HotkeyId, (uint)(modifiers | HotkeyModifiers.NoRepeat), virtualKey);

        if (!_registered)
        {
            // Almost always means another application already owns the combination.
            var code = Marshal.GetLastWin32Error();
            LastError = $"Windows refused the shortcut {gesture}; another app is probably using it (error {code}).";
            _logger.LogWarning("{Error}", LastError);
            _source.RemoveHook(WndProc);
            _source = null;
            return false;
        }

        LastError = null;
        _logger.LogInformation("Global hotkey {Gesture} registered.", gesture);
        return true;
    }

    public void Unregister()
    {
        if (_source is null)
        {
            return;
        }

        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _source.RemoveHook(WndProc);
        _source = null;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    /// <summary>Parse a gesture like <c>Ctrl+Shift+R</c> or <c>Alt+F9</c>.</summary>
    internal static bool TryParse(string gesture, out HotkeyModifiers modifiers, out Key key)
    {
        modifiers = HotkeyModifiers.None;
        key = Key.None;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "win" or "windows":
                    modifiers |= HotkeyModifiers.Windows;
                    continue;
            }

            if (!Enum.TryParse(part, ignoreCase: true, out Key parsed) || parsed == Key.None)
            {
                return false;
            }

            key = parsed;
        }

        // A bare key would swallow that key system-wide, so a modifier is required.
        return key != Key.None && modifiers != HotkeyModifiers.None;
    }

    public void Dispose() => Unregister();
}
