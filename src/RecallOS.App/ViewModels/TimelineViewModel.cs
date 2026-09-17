using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;

namespace RecallOS.App.ViewModels;

/// <summary>
/// The activity strip along the bottom of the window.
/// </summary>
/// <remarks>
/// People do not remember timestamps; they remember shapes of days. "That was the afternoon
/// I was heads-down in the editor, right after the long meeting" is a far more reliable
/// retrieval cue than a clock time, and the histogram is what makes that cue clickable.
/// </remarks>
public sealed partial class TimelineViewModel : ObservableObject
{
    private readonly IFrameRepository _repository;

    [ObservableProperty]
    private TimelineGranularity _granularity = TimelineGranularity.QuarterHour;

    [ObservableProperty]
    private DateTimeOffset _rangeStart = DateTimeOffset.Now.Date;

    [ObservableProperty]
    private DateTimeOffset _rangeEnd = DateTimeOffset.Now.Date.AddDays(1);

    [ObservableProperty]
    private string _rangeLabel = "Today";

    [ObservableProperty]
    private int _totalFrames;

    [ObservableProperty]
    private bool _isLoading;

    public TimelineViewModel(IFrameRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public ObservableCollection<TimelineBucket> Buckets { get; } = [];

    /// <summary>Raised when the user clicks a bucket, carrying the range it covers.</summary>
    public event EventHandler<(DateTimeOffset From, DateTimeOffset To)>? BucketSelected;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var buckets = await _repository
                .GetTimelineAsync(RangeStart, RangeEnd, Granularity, cancellationToken)
                .ConfigureAwait(true);

            Buckets.Clear();
            foreach (var bucket in buckets)
            {
                Buckets.Add(bucket);
            }

            TotalFrames = buckets.Sum(b => b.FrameCount);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Move the window by whole days, keeping its width.</summary>
    [RelayCommand]
    private async Task ShiftAsync(string? direction)
    {
        var days = direction == "forward" ? 1 : -1;
        var span = RangeEnd - RangeStart;

        RangeStart = RangeStart.AddDays(days);
        RangeEnd = RangeStart + span;
        UpdateLabel();

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowTodayAsync()
    {
        RangeStart = DateTimeOffset.Now.Date;
        RangeEnd = RangeStart.AddDays(1);
        Granularity = TimelineGranularity.QuarterHour;
        UpdateLabel();

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Widen to the last week. The granularity coarsens with the span: a week of
    /// quarter-hour buckets would be 672 bars, which is noise rather than information.
    /// </summary>
    [RelayCommand]
    private async Task ShowWeekAsync()
    {
        RangeEnd = DateTimeOffset.Now.Date.AddDays(1);
        RangeStart = RangeEnd.AddDays(-7);
        Granularity = TimelineGranularity.Hour;
        UpdateLabel();

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectBucket(TimelineBucket? bucket)
    {
        if (bucket is not null)
        {
            BucketSelected?.Invoke(this, (bucket.Start, bucket.End));
        }
    }

    private void UpdateLabel()
    {
        var days = (RangeEnd - RangeStart).TotalDays;

        RangeLabel = days <= 1
            ? RangeStart.Date == DateTimeOffset.Now.Date
                ? "Today"
                : RangeStart.ToString("dddd d MMM")
            : $"{RangeStart:d MMM} – {RangeEnd.AddDays(-1):d MMM}";
    }
}
