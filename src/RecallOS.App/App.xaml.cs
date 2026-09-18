using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecallOS.App.Services;
using RecallOS.App.ViewModels;
using RecallOS.App.Views;
using RecallOS.Core;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Maintenance;
using RecallOS.Core.Models;
using RecallOS.Core.Pipeline;

namespace RecallOS.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// <para>
/// Startup order matters and is explicit: build the container, migrate the database and
/// load settings, then create the window. The window's view model touches the store on
/// construction, so it cannot be built before the schema exists.
/// </para>
/// <para>
/// A single-instance mutex guards the store. Two processes writing the same SQLite file
/// would technically work under WAL, but they would fight over capture scheduling and
/// double every recorded frame, so the second instance surrenders to the first.
/// </para>
/// </remarks>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\RecallOS.SingleInstance";

    private Mutex? _instanceMutex;
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayIconService? _tray;
    private GlobalHotkeyService? _hotkey;
    private DispatcherTimer? _maintenanceTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "RecallOS is already running. Look for it in the notification area.",
                "RecallOS",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // Nothing is rendered yet, so an exception here would otherwise be an invisible
        // process death. Failures during startup are reported and then exit cleanly.
        try
        {
            _services = BuildServices();
            await _services.InitializeRecallOsAsync();

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            CreateTrayIcon();
            CreateWindow();
            StartMaintenance();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"RecallOS could not start.\n\n{ex.Message}",
                "RecallOS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddRecallOs();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);

            // RecallOS spends most of its life with no console attached, so diagnostics go
            // to a rolling file in the store. Registered after AddRecallOs so the paths the
            // engine resolved are the ones the log is written beside.
            builder.Services.AddSingleton<ILoggerProvider>(provider =>
                new FileLoggerProvider(provider.GetRequiredService<RecallPaths>()));
        });

        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TutorialViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<GlobalHotkeyService>();

        return services.BuildServiceProvider();
    }

    private void CreateWindow()
    {
        var viewModel = _services!.GetRequiredService<MainViewModel>();

        _window = new MainWindow(viewModel);
        MainWindow = _window;

        _window.MinimizedToTray += (_, _) =>
            _tray?.ShowMessage("RecallOS is still running in the notification area.");

        // The hotkey needs a live HWND, which only exists once the window is sourced.
        _window.SourceInitialized += (_, _) => RegisterHotkey();

        viewModel.Settings.Saved += (_, settings) =>
        {
            RegisterHotkey();
            _tray?.SetRecording(settings.AutoCaptureEnabled, viewModel.TotalFrames);
        };

        viewModel.Settings.EraseRequested += async (_, _) => await EraseEverythingAsync();

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsRecording) or nameof(MainViewModel.TotalFrames))
            {
                _tray?.SetRecording(viewModel.IsRecording, viewModel.TotalFrames);
            }
        };

        _window.Show();
    }

    private void CreateTrayIcon()
    {
        _tray = new TrayIconService();

        _tray.OpenRequested += (_, _) => _window?.RestoreFromTray();
        _tray.CaptureRequested += async (_, _) => await CaptureFromBackgroundAsync();
        _tray.ExitRequested += (_, _) => Shutdown();

        _tray.AutoCaptureToggled += async (_, enabled) =>
        {
            var settings = _services!.GetRequiredService<ISettingsStore>();
            var updated = settings.Current.Clone();
            updated.AutoCaptureEnabled = enabled;
            await settings.SaveAsync(updated);
        };
    }

    private void RegisterHotkey()
    {
        if (_window is null || _services is null)
        {
            return;
        }

        _hotkey ??= _services.GetRequiredService<GlobalHotkeyService>();
        _hotkey.Pressed -= OnHotkeyPressed;
        _hotkey.Pressed += OnHotkeyPressed;

        var gesture = _services.GetRequiredService<ISettingsStore>().Current.CaptureHotkey;

        if (!_hotkey.Register(_window, gesture) && _hotkey.LastError is { } error)
        {
            // A refused hotkey is worth telling the user about once: silently not working
            // would look like the feature is broken.
            _window.Dispatcher.InvokeAsync(() =>
                _services.GetRequiredService<MainViewModel>().StatusMessage = error);
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e) => await CaptureFromBackgroundAsync();

    /// <summary>
    /// Capture triggered from outside the window (tray or hotkey). Routed through the view
    /// model so the result lands in the list and the status bar exactly as a click would.
    /// </summary>
    private async Task CaptureFromBackgroundAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var viewModel = _services.GetRequiredService<MainViewModel>();
            await viewModel.CaptureNowAsync();
            _tray?.ShowMessage("Captured.");
        }
        catch (Exception ex)
        {
            _tray?.ShowMessage($"Capture failed: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// A background sweep for retention. Hourly, because the limits are expressed in days
    /// and gigabytes: checking more often would burn I/O to discover nothing has changed.
    /// </summary>
    private void StartMaintenance()
    {
        // Captured as locals so the closures do not depend on the nullable fields still
        // being set when they eventually run.
        var services = _services!;
        var retention = services.GetRequiredService<RetentionService>();
        var logger = services.GetService<ILogger<App>>();

        _maintenanceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(1)
        };

        _maintenanceTimer.Tick += async (_, _) =>
        {
            try
            {
                await retention.EnforceAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Scheduled retention sweep failed.");
            }
        };

        _maintenanceTimer.Start();

        // Run once at startup too, so limits tightened while the app was closed take effect.
        _ = Task.Run(async () =>
        {
            try
            {
                await retention.EnforceAsync();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Startup retention sweep failed.");
            }
        });
    }

    /// <summary>
    /// Delete the entire history. Confirmed twice, because it cannot be undone and the
    /// thing being destroyed is the user's record of their own work.
    /// </summary>
    private async Task EraseEverythingAsync()
    {
        if (_services is null || _window is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            _window,
            "Delete every captured frame, its extracted text and its search index?\n\n"
            + "This cannot be undone.",
            "Erase everything",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var viewModel = _services.GetRequiredService<MainViewModel>();
        var retention = _services.GetRequiredService<RetentionService>();

        try
        {
            viewModel.StatusMessage = "Erasing…";

            var removed = await retention.DeleteRangeAsync(
                DateTimeOffset.MinValue,
                DateTimeOffset.MaxValue);

            await viewModel.RefreshAsync();

            viewModel.StatusMessage = $"Erased {removed:N0} frames.";
            viewModel.Settings.StatusMessage = $"Erased {removed:N0} frames.";
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = $"Erase failed: {ex.Message}";
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Stop producing work before tearing down what consumes it, so nothing is
        // half-written when the process ends.
        if (_services is not null)
        {
            try
            {
                await _services.GetRequiredService<CaptureScheduler>().StopAsync();
                await _services.GetRequiredService<OcrQueueProcessor>().DisposeAsync();
            }
            catch (Exception)
            {
                // Shutdown is best-effort; the store is crash-safe either way.
            }
        }

        _maintenanceTimer?.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _services?.Dispose();

        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
