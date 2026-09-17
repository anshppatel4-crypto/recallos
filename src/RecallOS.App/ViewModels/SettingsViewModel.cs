using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Common;
using RecallOS.Core.Maintenance;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;

namespace RecallOS.App.ViewModels;

/// <summary>
/// The settings panel, including the controls that make the system's behaviour visible
/// and reversible: what it records, what it skips, how long it keeps it, and how to erase it.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly TessDataLocator _tessData;
    private readonly RetentionService _retention;
    private readonly RecallPaths _paths;
    private readonly IOcrEngine _ocr;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private bool _autoCaptureEnabled;

    [ObservableProperty]
    private int _captureIntervalSeconds = 30;

    [ObservableProperty]
    private CaptureTarget _autoCaptureTarget = CaptureTarget.AllScreens;

    [ObservableProperty]
    private int _idleThresholdSeconds = 120;

    [ObservableProperty]
    private bool _skipUnchangedFrames = true;

    [ObservableProperty]
    private int _unchangedHashTolerance = 6;

    [ObservableProperty]
    private bool _includeCursor;

    [ObservableProperty]
    private bool _ocrEnabled = true;

    [ObservableProperty]
    private string _ocrLanguages = "eng";

    [ObservableProperty]
    private string _excludedProcesses = string.Empty;

    [ObservableProperty]
    private string _excludedTitleKeywords = string.Empty;

    [ObservableProperty]
    private int _retentionDays = 30;

    [ObservableProperty]
    private int _maxStorageGigabytes = 20;

    [ObservableProperty]
    private bool _semanticIndexingEnabled = true;

    [ObservableProperty]
    private SearchMode _defaultSearchMode = SearchMode.Hybrid;

    [ObservableProperty]
    private string _captureHotkey = "Ctrl+Shift+R";

    [ObservableProperty]
    private bool _minimizeToTray = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _ocrAvailability = string.Empty;

    /// <summary>Drives the status dot beside <see cref="OcrAvailability"/>.</summary>
    [ObservableProperty]
    private bool _isOcrReady;

    public SettingsViewModel(
        ISettingsStore store,
        TessDataLocator tessData,
        RetentionService retention,
        RecallPaths paths,
        IOcrEngine ocr,
        ILogger<SettingsViewModel>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tessData = tessData ?? throw new ArgumentNullException(nameof(tessData));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsViewModel>.Instance;

        ReadFrom(_store.Current);
        RefreshLanguages();
    }

    public ObservableCollection<string> InstalledLanguages { get; } = [];

    public string StoreRoot => _paths.Root;

    public IReadOnlyList<CaptureTarget> CaptureTargets { get; } =
        [CaptureTarget.AllScreens, CaptureTarget.ActiveScreen, CaptureTarget.ActiveWindow];

    public IReadOnlyList<SearchMode> SearchModes { get; } =
        [SearchMode.Hybrid, SearchMode.Keyword, SearchMode.Semantic];

    /// <summary>Raised after a save so the shell can re-apply the hotkey and capture state.</summary>
    public event EventHandler<RecallSettings>? Saved;

    /// <summary>Raised when the user asks to erase everything, so the shell can confirm first.</summary>
    public event EventHandler? EraseRequested;

    public void ReadFrom(RecallSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AutoCaptureEnabled = settings.AutoCaptureEnabled;
        CaptureIntervalSeconds = settings.CaptureIntervalSeconds;
        AutoCaptureTarget = settings.AutoCaptureTarget;
        IdleThresholdSeconds = settings.IdleThresholdSeconds;
        SkipUnchangedFrames = settings.SkipUnchangedFrames;
        UnchangedHashTolerance = settings.UnchangedHashTolerance;
        IncludeCursor = settings.IncludeCursor;
        OcrEnabled = settings.OcrEnabled;
        OcrLanguages = settings.OcrLanguages;
        ExcludedProcesses = settings.ExcludedProcesses;
        ExcludedTitleKeywords = settings.ExcludedTitleKeywords;
        RetentionDays = settings.RetentionDays;
        MaxStorageGigabytes = settings.MaxStorageGigabytes;
        SemanticIndexingEnabled = settings.SemanticIndexingEnabled;
        DefaultSearchMode = settings.DefaultSearchMode;
        CaptureHotkey = settings.CaptureHotkey;
        MinimizeToTray = settings.MinimizeToTray;

        IsOcrReady = _ocr.IsAvailable;
        OcrAvailability = IsOcrReady
            ? $"Text extraction is active ({_ocr.Name})."
            : _ocr.UnavailableReason ?? "Text extraction is unavailable.";
    }

    private RecallSettings WriteTo()
    {
        var settings = _store.Current.Clone();

        settings.AutoCaptureEnabled = AutoCaptureEnabled;
        settings.CaptureIntervalSeconds = CaptureIntervalSeconds;
        settings.AutoCaptureTarget = AutoCaptureTarget;
        settings.IdleThresholdSeconds = IdleThresholdSeconds;
        settings.SkipUnchangedFrames = SkipUnchangedFrames;
        settings.UnchangedHashTolerance = UnchangedHashTolerance;
        settings.IncludeCursor = IncludeCursor;
        settings.OcrEnabled = OcrEnabled;
        settings.OcrLanguages = OcrLanguages;
        settings.ExcludedProcesses = ExcludedProcesses;
        settings.ExcludedTitleKeywords = ExcludedTitleKeywords;
        settings.RetentionDays = RetentionDays;
        settings.MaxStorageGigabytes = MaxStorageGigabytes;
        settings.SemanticIndexingEnabled = SemanticIndexingEnabled;
        settings.DefaultSearchMode = DefaultSearchMode;
        settings.CaptureHotkey = CaptureHotkey;
        settings.MinimizeToTray = MinimizeToTray;

        return settings;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var settings = WriteTo();
            await _store.SaveAsync(settings).ConfigureAwait(true);

            // Re-read, because normalisation may have clamped what was typed.
            ReadFrom(_store.Current);

            StatusMessage = "Settings saved.";
            Saved?.Invoke(this, _store.Current);

            if (!string.Equals(_ocr.Name, $"tesseract:{OcrLanguages}", StringComparison.OrdinalIgnoreCase)
                && _ocr.IsAvailable)
            {
                // The engine binds its language at construction, so a language change only
                // takes full effect next launch. Saying so beats silently doing nothing.
                StatusMessage = "Settings saved. Restart RecallOS to apply the language change.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed.");
            StatusMessage = $"Could not save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        ReadFrom(_store.Current);
        StatusMessage = "Reverted to the saved settings.";
    }

    /// <summary>
    /// Fetch language data on request. This is the only outbound network call in the whole
    /// application, and it never happens without this button being pressed.
    /// </summary>
    [RelayCommand]
    private async Task DownloadLanguageAsync(string? language)
    {
        language = string.IsNullOrWhiteSpace(language) ? "eng" : language.Trim();

        IsBusy = true;
        DownloadProgress = 0;
        StatusMessage = $"Downloading '{language}' language data…";

        try
        {
            var progress = new Progress<double>(value => DownloadProgress = value);
            await _tessData.DownloadLanguageAsync(language, progress).ConfigureAwait(true);

            RefreshLanguages();
            StatusMessage = $"Installed '{language}'. Restart RecallOS to begin extracting text.";
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            _logger.LogWarning(ex, "Language download failed.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            _logger.LogError(ex, "Language download failed.");
        }
        finally
        {
            IsBusy = false;
            DownloadProgress = 0;
        }
    }

    [RelayCommand]
    private async Task RunRetentionAsync()
    {
        IsBusy = true;
        try
        {
            var removed = await _retention.EnforceAsync().ConfigureAwait(true);
            StatusMessage = removed == 0
                ? "Nothing to clean up: the store is within its limits."
                : $"Removed {removed:N0} frames to stay within the limits.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention sweep failed.");
            StatusMessage = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Hands off to the shell, which confirms before anything is destroyed.</summary>
    [RelayCommand]
    private void EraseEverything() => EraseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenStoreFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the folder: {ex.Message}";
        }
    }

    private void RefreshLanguages()
    {
        InstalledLanguages.Clear();
        foreach (var language in _tessData.GetInstalledLanguages())
        {
            InstalledLanguages.Add(language);
        }
    }
}
