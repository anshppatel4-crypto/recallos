using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Maintenance;
using RecallOS.Core.Models;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Search;
using RecallOS.Core.Storage;

namespace RecallOS.App.ViewModels;

/// <summary>
/// The shell: search, results, replay, recording state and status.
/// </summary>
/// <remarks>
/// <para>
/// Search runs as the user types, debounced. Every keystroke cancels the in-flight query
/// before starting the next, which matters because a semantic pass scans the whole vector
/// store -- without cancellation, typing an eight-letter word would queue eight full scans
/// and the results would arrive out of order.
/// </para>
/// <para>
/// The view model subscribes to the pipeline rather than polling it, so a frame captured by
/// the background scheduler or the global hotkey appears in the list without the user doing
/// anything. Those events arrive on background threads and are marshalled here, once, at
/// the boundary.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Long enough to absorb typing, short enough to feel immediate.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(220);

    private readonly ISearchService _search;
    private readonly IFrameRepository _repository;
    private readonly CapturePipeline _pipeline;
    private readonly CaptureScheduler _scheduler;
    private readonly OcrQueueProcessor _ocrQueue;
    private readonly RetentionService _retention;
    private readonly FrameFileStore _files;
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<MainViewModel> _logger;

    private readonly DispatcherTimer _searchTimer;

    /// <summary>
    /// Ticks once a second while recording to refresh the countdown to the next capture.
    /// </summary>
    /// <remarks>
    /// Between captures nothing changes on screen, so without this the interface looks
    /// identical whether the recorder is running or dead. A visible countdown is the
    /// cheapest possible proof that it is still working.
    /// </remarks>
    private readonly DispatcherTimer _heartbeatTimer;

    private CancellationTokenSource? _searchCancellation;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private SearchMode _searchMode = SearchMode.Hybrid;

    [ObservableProperty]
    private FrameItemViewModel? _selectedFrame;

    [ObservableProperty]
    private string _selectedFrameText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private int _totalFrames;

    [ObservableProperty]
    private int _queueDepth;

    [ObservableProperty]
    private string _storageSummary = string.Empty;

    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>
    /// Which pane the sidebar has selected. The window is a shell with one content area
    /// rather than a single dense screen, so a new user lands somewhere that explains
    /// itself instead of on an empty result list.
    /// </summary>
    [ObservableProperty]
    private AppSection _section = AppSection.Home;

    /// <summary>True once anything at all has been captured.</summary>
    [ObservableProperty]
    private bool _hasAnyFrames;

    /// <summary>True when a language pack is installed, so captures can be searched.</summary>
    [ObservableProperty]
    private bool _isTextSearchReady;

    /// <summary>True once the user has set at least one exclusion.</summary>
    [ObservableProperty]
    private bool _hasExclusions;

    /// <summary>How many of the three getting-started steps are done.</summary>
    [ObservableProperty]
    private int _setupProgress;

    [ObservableProperty]
    private string _oldestFrameText = string.Empty;

    /// <summary>
    /// How many captures were deliberately discarded this session, and why.
    /// </summary>
    /// <remarks>
    /// Surfaced because a skip is indistinguishable from a failure otherwise. If the
    /// recorder is running and the frame count is not moving, the user needs to be told
    /// that this is a decision rather than a fault.
    /// </remarks>
    [ObservableProperty]
    private int _skippedCount;

    [ObservableProperty]
    private string _skipSummary = string.Empty;

    /// <summary>Countdown to the next automatic capture, e.g. "next in 6s".</summary>
    [ObservableProperty]
    private string _recordingStatus = string.Empty;

    public MainViewModel(
        ISearchService search,
        IFrameRepository repository,
        CapturePipeline pipeline,
        CaptureScheduler scheduler,
        OcrQueueProcessor ocrQueue,
        RetentionService retention,
        FrameFileStore files,
        ISettingsStore settings,
        TimelineViewModel timeline,
        SettingsViewModel settingsViewModel,
        TutorialViewModel tutorial,
        ILogger<MainViewModel>? logger = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _ocrQueue = ocrQueue ?? throw new ArgumentNullException(nameof(ocrQueue));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MainViewModel>.Instance;

        Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        Settings = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));
        Tutorial = tutorial ?? throw new ArgumentNullException(nameof(tutorial));

        Tutorial.SettingsRequested += (_, _) => IsSettingsOpen = true;
        Tutorial.Finished += async (_, _) => await RefreshSetupStateAsync().ConfigureAwait(true);

        _dispatcher = Dispatcher.CurrentDispatcher;
        _searchMode = _settings.Current.DefaultSearchMode;
        _isRecording = _settings.Current.AutoCaptureEnabled;

        _searchTimer = new DispatcherTimer { Interval = SearchDebounce };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RunSearchAsync().ConfigureAwait(true);
        };

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeatTimer.Tick += (_, _) => UpdateRecordingStatus();

        // Background events cross onto the UI thread exactly here.
        _pipeline.FrameCaptured += OnFrameCaptured;
        _pipeline.CaptureSkipped += OnCaptureSkipped;
        _ocrQueue.FrameProcessed += OnFrameProcessed;
        _ocrQueue.QueueDepthChanged += OnQueueDepthChanged;
        Timeline.BucketSelected += OnTimelineBucketSelected;
        Settings.Saved += OnSettingsSaved;
    }

    public ObservableCollection<FrameItemViewModel> Results { get; } = [];

    public TimelineViewModel Timeline { get; }

    public SettingsViewModel Settings { get; }

    public TutorialViewModel Tutorial { get; }

    public IReadOnlyList<SearchMode> SearchModes { get; } =
        [SearchMode.Hybrid, SearchMode.Keyword, SearchMode.Semantic];

    /// <summary>Full-resolution image of the selected frame, for the replay pane.</summary>
    public string? SelectedImagePath => SelectedFrame?.FullImagePath;

    public bool HasSelection => SelectedFrame is not null;

    public bool HasResults => Results.Count > 0;

    // ---- lifecycle ------------------------------------------------------------------

    /// <summary>Populate the first screen. Called once the window is up.</summary>
    public async Task InitializeAsync()
    {
        Settings.ReadFrom(_settings.Current);

        // Shown before anything is recorded, so the user meets the explanation first.
        if (!_settings.Current.HasSeenWelcome)
        {
            Tutorial.Start();
        }

        await RunSearchAsync().ConfigureAwait(true);
        await Timeline.LoadAsync().ConfigureAwait(true);
        await RefreshStatisticsAsync().ConfigureAwait(true);

        if (_settings.Current.AutoCaptureEnabled)
        {
            _scheduler.Start();
            IsRecording = true;
            _heartbeatTimer.Start();
        }

        await RefreshSetupStateAsync().ConfigureAwait(true);

        StatusMessage = TotalFrames == 0
            ? "Nothing recorded yet. Press Capture for one frame, or Record to keep going."
            : "Ready.";
    }

    // ---- search ---------------------------------------------------------------------

    partial void OnSearchTextChanged(string value)
    {
        // Restart the debounce window on every keystroke.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnSearchModeChanged(SearchMode value) => _ = RunSearchAsync();

    partial void OnSelectedFrameChanged(FrameItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedImagePath));
        OnPropertyChanged(nameof(HasSelection));

        SelectedFrameText = value?.Frame.Text ?? string.Empty;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchTimer.Stop();
        await RunSearchAsync().ConfigureAwait(true);
    }

    private async Task RunSearchAsync()
    {
        // Cancel whatever is still running: its results are already stale.
        var previous = _searchCancellation;
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;

        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        IsSearching = true;
        Warning = null;

        try
        {
            var query = SearchQueryParser.Parse(SearchText, SearchMode, limit: 300);
            var result = await _search.SearchAsync(query, token).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                return;
            }

            var previousSelectionId = SelectedFrame?.Id;

            Results.Clear();
            foreach (var hit in result.Hits)
            {
                Results.Add(new FrameItemViewModel(hit, _files));
            }

            OnPropertyChanged(nameof(HasResults));

            // Keep the user's place across a refresh if the frame is still in the results.
            SelectedFrame = Results.FirstOrDefault(r => r.Id == previousSelectionId) ?? Results.FirstOrDefault();

            Warning = result.Warning;
            ResultSummary = result.Hits.Count switch
            {
                0 when query.IsEmpty => "Nothing recorded yet.",
                0 => "No matches.",
                1 => $"1 result in {result.Elapsed.TotalMilliseconds:F0} ms",
                _ => $"{result.Hits.Count:N0} results in {result.Elapsed.TotalMilliseconds:F0} ms"
            };
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed.");
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        _searchTimer.Stop();
        await RunSearchAsync().ConfigureAwait(true);
    }

    // ---- capture --------------------------------------------------------------------

    /// <summary>
    /// Capture right now. Always stores a frame, even if the screen has not changed: the
    /// user asked, and silently discarding an explicit request would be indefensible.
    /// </summary>
    [RelayCommand]
    public async Task CaptureNowAsync()
    {
        if (IsCapturing)
        {
            return;
        }

        IsCapturing = true;
        StatusMessage = "Capturing…";

        try
        {
            var outcome = await _pipeline.CaptureAsync(
                new CaptureRequest
                {
                    Target = CaptureTarget.AllScreens,
                    Origin = CaptureOrigin.Manual,
                    IncludeCursor = _settings.Current.IncludeCursor,
                    SkipIfUnchanged = false
                }).ConfigureAwait(true);

            StatusMessage = outcome switch
            {
                { WasStored: true } => "Captured.",
                { Error: not null } => $"Capture failed: {outcome.Error.Message}",
                _ => $"Capture skipped: {Describe(outcome.SkipReason)}"
            };
        }
        finally
        {
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        var settings = _settings.Current.Clone();
        settings.AutoCaptureEnabled = !IsRecording;
        await _settings.SaveAsync(settings).ConfigureAwait(true);

        if (settings.AutoCaptureEnabled)
        {
            SkippedCount = 0;
            SkipSummary = string.Empty;
            _scheduler.Start();
            IsRecording = true;
            _heartbeatTimer.Start();
            StatusMessage = $"Recording every {settings.CaptureIntervalSeconds} seconds until you press Pause.";
        }
        else
        {
            _scheduler.Pause();
            IsRecording = false;
            _heartbeatTimer.Stop();
            RecordingStatus = string.Empty;
            StatusMessage = "Recording paused.";
        }

        Settings.ReadFrom(_settings.Current);
    }

    // ---- replay ---------------------------------------------------------------------

    /// <summary>
    /// Step to the neighbouring frame in time, not in the result list. Reconstructing what
    /// happened means walking the recording, and the frames either side of a hit are often
    /// exactly the context the hit is missing.
    /// </summary>
    [RelayCommand]
    private async Task StepAsync(string? direction)
    {
        if (SelectedFrame is null)
        {
            return;
        }

        var forward = direction == "forward";
        var neighbor = await _repository.GetNeighborAsync(SelectedFrame.Id, forward).ConfigureAwait(true);

        if (neighbor is null)
        {
            StatusMessage = forward ? "This is the most recent frame." : "This is the earliest frame.";
            return;
        }

        // The neighbour may not be in the current result set, so it is shown directly
        // rather than by trying to select a row that is not there.
        var item = new FrameItemViewModel(
            new SearchHit { Frame = neighbor, Snippet = Excerpt(neighbor.Text) },
            _files);

        var existing = Results.FirstOrDefault(r => r.Id == neighbor.Id);
        if (existing is not null)
        {
            SelectedFrame = existing;
        }
        else
        {
            Results.Insert(0, item);
            OnPropertyChanged(nameof(HasResults));
            SelectedFrame = item;
        }

        StatusMessage = $"Replaying {neighbor.CapturedAt.ToLocalTime():dddd d MMM HH:mm:ss}.";
    }

    [RelayCommand]
    private void OpenImage()
    {
        var path = SelectedImagePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusMessage = "That image is no longer on disk.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the image: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyText()
    {
        if (string.IsNullOrEmpty(SelectedFrameText))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(SelectedFrameText);
            StatusMessage = "Text copied to the clipboard.";
        }
        catch (Exception ex)
        {
            // The clipboard is a shared, lockable resource; another app can be holding it.
            StatusMessage = $"Could not copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedFrame is null)
        {
            return;
        }

        var id = SelectedFrame.Id;
        var index = Results.IndexOf(SelectedFrame);

        await _retention.DeleteAsync([id]).ConfigureAwait(true);

        Results.Remove(SelectedFrame);
        OnPropertyChanged(nameof(HasResults));

        // Land on the next row rather than nothing, so deleting several in a row works.
        SelectedFrame = Results.Count == 0
            ? null
            : Results[Math.Min(index, Results.Count - 1)];

        StatusMessage = "Frame deleted.";
        await RefreshStatisticsAsync().ConfigureAwait(true);
    }

    // ---- status ---------------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RunSearchAsync().ConfigureAwait(true);
        await Timeline.LoadAsync().ConfigureAwait(true);
        await RefreshStatisticsAsync().ConfigureAwait(true);
    }

    public async Task RefreshStatisticsAsync()
    {
        try
        {
            var statistics = await _repository.GetStatisticsAsync().ConfigureAwait(true);

            TotalFrames = statistics.FrameCount;
            QueueDepth = statistics.PendingOcrCount;

            StorageSummary =
                $"{statistics.FrameCount:N0} frames · {RetentionService.FormatBytes(statistics.TotalBytes)}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read store statistics.");
        }
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>Switch the content pane from the sidebar.</summary>
    [RelayCommand]
    private async Task NavigateAsync(string? target)
    {
        if (!Enum.TryParse<AppSection>(target, ignoreCase: true, out var section))
        {
            return;
        }

        Section = section;

        if (section == AppSection.Home)
        {
            await RefreshSetupStateAsync().ConfigureAwait(true);
        }
        else if (section == AppSection.Timeline)
        {
            await Timeline.LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Jump to search with a query already filled in, from the Home shortcuts.</summary>
    [RelayCommand]
    private async Task SearchForAsync(string? query)
    {
        Section = AppSection.Search;
        SearchText = query ?? string.Empty;
        _searchTimer.Stop();
        await RunSearchAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowTutorial() => Tutorial.Start();

    /// <summary>
    /// Recompute the getting-started checklist. Cheap, and it keeps Home honest about
    /// what is actually set up rather than assuming the user followed the walkthrough.
    /// </summary>
    public async Task RefreshSetupStateAsync()
    {
        try
        {
            var statistics = await _repository.GetStatisticsAsync().ConfigureAwait(true);

            HasAnyFrames = statistics.FrameCount > 0;
            OldestFrameText = statistics.OldestFrame is { } oldest
                ? $"since {oldest.ToLocalTime():d MMM}"
                : "nothing recorded yet";

            IsTextSearchReady = Settings.IsOcrReady;

            var settings = _settings.Current;
            HasExclusions = settings.ParseExcludedProcesses().Count > 0
                            || settings.ParseExcludedTitleKeywords().Count > 0;

            SetupProgress = (HasAnyFrames ? 1 : 0) + (IsTextSearchReady ? 1 : 0) + (HasExclusions ? 1 : 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the setup checklist.");
        }
    }

    // ---- events ---------------------------------------------------------------------

    private void OnFrameCaptured(object? sender, CaptureFrame frame) =>
        _dispatcher.InvokeAsync(async () =>
        {
            // Only fold a new frame into the list when the user is browsing rather than
            // searching; injecting it into a filtered result set would be wrong.
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                Results.Insert(0, new FrameItemViewModel(
                    new SearchHit { Frame = frame, Snippet = Excerpt(frame.Text) },
                    _files));

                OnPropertyChanged(nameof(HasResults));

                // Keep the list bounded; older rows are one refresh away.
                while (Results.Count > 400)
                {
                    Results.RemoveAt(Results.Count - 1);
                }
            }

            TotalFrames++;
            await Timeline.LoadAsync().ConfigureAwait(true);
        });

    private void OnFrameProcessed(object? sender, CaptureFrame frame) =>
        _dispatcher.InvokeAsync(() =>
        {
            // Replace the placeholder row in place, so the snippet appears without the
            // list jumping under the user.
            var index = IndexOf(frame.Id);
            if (index < 0)
            {
                return;
            }

            var wasSelected = SelectedFrame?.Id == frame.Id;

            Results[index] = new FrameItemViewModel(
                new SearchHit { Frame = frame, Snippet = Excerpt(frame.Text) },
                _files);

            if (wasSelected)
            {
                SelectedFrame = Results[index];
            }
        });

    private void OnCaptureSkipped(object? sender, CaptureSkipReason reason) =>
        _dispatcher.InvokeAsync(() =>
        {
            SkippedCount++;
            SkipSummary = $"{SkippedCount:N0} skipped · {Describe(reason)}";
        });

    private void OnQueueDepthChanged(object? sender, int depth) =>
        _dispatcher.InvokeAsync(() => QueueDepth = depth);

    private async void OnTimelineBucketSelected(object? sender, (DateTimeOffset From, DateTimeOffset To) range)
    {
        try
        {
            // Translate the click into a query the user can see, edit and re-run.
            SearchText = $"after:{range.From:yyyy-MM-ddTHH:mm:ss} before:{range.To:yyyy-MM-ddTHH:mm:ss}";
            _searchTimer.Stop();
            await RunSearchAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeline navigation failed.");
        }
    }

    private async void OnSettingsSaved(object? sender, RecallSettings settings)
    {
        SearchMode = settings.DefaultSearchMode;
        await RefreshSetupStateAsync().ConfigureAwait(true);

        if (settings.AutoCaptureEnabled)
        {
            _scheduler.Start();
            IsRecording = true;
            _heartbeatTimer.Start();
        }
        else
        {
            _scheduler.Pause();
            IsRecording = false;
            _heartbeatTimer.Stop();
            RecordingStatus = string.Empty;
        }
    }

    /// <summary>Refresh the countdown shown while recording.</summary>
    private void UpdateRecordingStatus()
    {
        if (!IsRecording)
        {
            RecordingStatus = string.Empty;
            return;
        }

        if (_scheduler.NextCaptureAt is not { } next)
        {
            RecordingStatus = "capturing…";
            return;
        }

        var remaining = next - DateTimeOffset.Now;
        RecordingStatus = remaining <= TimeSpan.Zero
            ? "capturing…"
            : $"next in {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
    }

    // ---- helpers --------------------------------------------------------------------

    private int IndexOf(long frameId)
    {
        for (var i = 0; i < Results.Count; i++)
        {
            if (Results[i].Id == frameId)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Excerpt(string text) =>
        text.Length == 0 ? string.Empty : SnippetBuilder.Build(text, Array.Empty<string>()).Text;

    private static string Describe(CaptureSkipReason reason) => reason switch
    {
        CaptureSkipReason.UserIdle => "no activity detected",
        CaptureSkipReason.ScreenUnchanged => "the screen has not changed",
        CaptureSkipReason.Excluded => "this window is on the exclusion list",
        CaptureSkipReason.SessionLocked => "the session is locked",
        CaptureSkipReason.Paused => "recording is paused",
        CaptureSkipReason.StorageBudgetReached => "the storage limit was reached",
        _ => "no reason given"
    };

    public void Dispose()
    {
        _searchTimer.Stop();
        _heartbeatTimer.Stop();

        _pipeline.FrameCaptured -= OnFrameCaptured;
        _pipeline.CaptureSkipped -= OnCaptureSkipped;
        _ocrQueue.FrameProcessed -= OnFrameProcessed;
        _ocrQueue.QueueDepthChanged -= OnQueueDepthChanged;
        Timeline.BucketSelected -= OnTimelineBucketSelected;
        Settings.Saved -= OnSettingsSaved;

        _searchCancellation?.Dispose();
    }
}
