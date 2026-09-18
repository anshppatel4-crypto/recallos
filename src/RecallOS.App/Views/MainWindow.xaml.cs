using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using RecallOS.App.ViewModels;

namespace RecallOS.App.Views;

/// <summary>
/// The shell window.
/// </summary>
/// <remarks>
/// Code-behind is limited to what is genuinely the window's business: the custom chrome
/// (WindowChrome gives us the frame but not the buttons), focus, keyboard shortcuts that
/// need the visual tree, and the close-to-tray decision. Everything else is in
/// <see cref="MainViewModel"/>.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>Set by the application when the user really is quitting, not just closing.</summary>
    public bool ForceClose { get; set; }

    /// <summary>Raised when the window was closed but the app should keep recording.</summary>
    public event EventHandler? MinimizedToTray;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();

        // A short fade-and-rise on first paint. Long enough to read as intentional, short
        // enough that it never delays someone who opened the window to search something.
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// Shrink and re-centre the window if the default size does not fit the screen.
    /// </summary>
    /// <remarks>
    /// The designed size assumes a reasonably large display. On a laptop at a high scale
    /// factor the work area can be smaller than that, and a window larger than the screen
    /// would push its own controls off the edge — including, on a frameless window, the
    /// close button. The size is a preference; fitting on screen is not.
    /// </remarks>
    private void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;

        // A small inset so the window never sits flush against the screen edges.
        var maxWidth = work.Width - 40;
        var maxHeight = work.Height - 40;

        var changed = false;

        if (Width > maxWidth)
        {
            Width = Math.Max(MinWidth, maxWidth);
            changed = true;
        }

        if (Height > maxHeight)
        {
            Height = Math.Max(MinHeight, maxHeight);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
    }

    /// <summary>
    /// A WindowStyle=None window has no system frame, so maximising would otherwise cover
    /// the taskbar. WindowChrome handles the geometry; this only keeps the glyph and the
    /// inset honest.
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;

        // Restore and Maximise glyphs, as escapes so the source stays ASCII.
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximise";

        // Without this the maximised window is clipped by the invisible resize border.
        BorderThickness = maximized ? new Thickness(7) : new Thickness(0);
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // The walkthrough is modal in intent: arrow keys page through it, Escape leaves it,
        // and nothing else reaches the app behind.
        if (_viewModel.Tutorial.IsVisible)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _viewModel.Tutorial.SkipCommand.Execute(null);
                    break;
                case Key.Right or Key.Enter:
                    _viewModel.Tutorial.NextCommand.Execute(null);
                    break;
                case Key.Left:
                    _viewModel.Tutorial.BackCommand.Execute(null);
                    break;
            }

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            // Ctrl+F is universal; "/" is what people who live in search boxes reach for.
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                FocusSearch();
                e.Handled = true;
                break;

            case Key.Escape when _viewModel.IsSettingsOpen:
                _viewModel.IsSettingsOpen = false;
                e.Handled = true;
                break;

            case Key.Escape when !SearchBox.IsFocused:
                FocusSearch();
                e.Handled = true;
                break;

            // Let the arrow keys walk the results while the caret is still in the search
            // box, so searching and browsing do not need a mouse trip in between.
            case Key.Down when SearchBox.IsFocused && ResultsList.Items.Count > 0:
                ResultsList.Focus();
                if (ResultsList.SelectedIndex < 0)
                {
                    ResultsList.SelectedIndex = 0;
                }

                e.Handled = true;
                break;
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window is not the same as stopping the recorder. If the user asked
        // RecallOS to keep running, it hides instead — and the tray icon stays visible, so
        // it is never running invisibly.
        if (!ForceClose && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            MinimizedToTray?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Bring the window back from the tray and put the caret in the search box.</summary>
    public void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        FocusSearch();
    }
}
