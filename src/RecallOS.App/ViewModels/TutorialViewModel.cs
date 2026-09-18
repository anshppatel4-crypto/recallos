using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecallOS.Core.Abstractions;

namespace RecallOS.App.ViewModels;

/// <summary>One page of the first-run walkthrough.</summary>
public sealed class TutorialStep
{
    public required string Glyph { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    /// <summary>Optional supporting line, shown smaller beneath the body.</summary>
    public string? Detail { get; init; }

    /// <summary>Label for the primary button on this step.</summary>
    public string ActionLabel { get; init; } = "Next";
}

/// <summary>
/// The guided introduction shown the first time RecallOS runs.
/// </summary>
/// <remarks>
/// <para>
/// A tool that records the screen has to explain itself <em>before</em> it starts, not
/// after. The walkthrough covers what gets recorded, how to stop it, how to make captures
/// searchable, and how to exclude things — in that order, because that is the order a
/// reasonable person would want to be told.
/// </para>
/// <para>
/// It is a sequence of plain pages rather than coach marks pinned to controls: pinned
/// overlays break whenever the layout moves, and this has to keep working as the interface
/// evolves.
/// </para>
/// </remarks>
public sealed partial class TutorialViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _index;

    public TutorialViewModel(ISettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        Steps =
        [
            new TutorialStep
            {
                Glyph = "",
                Title = "A memory layer for your screen",
                Body = "RecallOS records what you see, reads the text on it, and makes every moment "
                     + "searchable — by the words that were on screen, or just by what you meant.",
                Detail = "Everything stays on this machine. There is no account, no sync and no telemetry.",
                ActionLabel = "Show me"
            },
            new TutorialStep
            {
                Glyph = "",
                Title = "Nothing is recorded until you say so",
                Body = "Press Capture for a single frame, or Record to keep capturing until you press "
                     + "Pause. A dot in the title bar pulses while recording, with a countdown to the "
                     + "next frame so you can always tell it is working.",
                Detail = "Ctrl+Shift+R captures from anywhere, even when this window is closed."
            },
            new TutorialStep
            {
                Glyph = "",
                Title = "Then find it again",
                Body = "Type a word you remember seeing. Narrow it with app:chrome, \"an exact phrase\", "
                     + "or after:yesterday. Switch between matching words, matching meaning, or both.",
                Detail = "Select any result to replay the original screenshot beside the text that was on it."
            },
            new TutorialStep
            {
                Glyph = "",
                Title = "Turn on text search",
                Body = "Reading the text inside your captures needs a language pack — a one-click "
                     + "download of about 4 MB. Until it is installed, frames are still captured and "
                     + "stored, they just cannot be searched by their contents yet.",
                Detail = "This is the only time RecallOS uses the network, and only when you press the button.",
                ActionLabel = "Open Settings"
            },
            new TutorialStep
            {
                Glyph = "",
                Title = "Decide what it may never see",
                Body = "Add a process name or a window-title phrase to the exclusion lists and those "
                     + "windows are never captured. They are checked before the screen is read, and "
                     + "again against whatever was actually in front.",
                Detail = "Excluded frames never reach the disk. Erase everything removes the whole history at once.",
                ActionLabel = "Start using RecallOS"
            }
        ];
    }

    public IReadOnlyList<TutorialStep> Steps { get; }

    public TutorialStep Current => Steps[Math.Clamp(Index, 0, Steps.Count - 1)];

    public bool IsFirst => Index == 0;

    public bool IsLast => Index >= Steps.Count - 1;

    public string Progress => $"{Index + 1} of {Steps.Count}";

    /// <summary>Raised when the walkthrough finishes on the step that offers to open Settings.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the walkthrough is dismissed, however it ended.</summary>
    public event EventHandler? Finished;

    partial void OnIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsFirst));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(Progress));
    }

    /// <summary>Show the walkthrough from the beginning.</summary>
    public void Start()
    {
        Index = 0;
        IsVisible = true;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // The language-pack step offers to take the user straight there, which is the one
        // action they cannot discover on their own.
        if (Current.ActionLabel == "Open Settings")
        {
            await FinishAsync().ConfigureAwait(true);
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsLast)
        {
            await FinishAsync().ConfigureAwait(true);
            return;
        }

        Index++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst)
        {
            Index--;
        }
    }

    [RelayCommand]
    private void GoTo(int step) => Index = Math.Clamp(step, 0, Steps.Count - 1);

    [RelayCommand]
    private async Task SkipAsync() => await FinishAsync().ConfigureAwait(true);

    /// <summary>Hide the walkthrough and remember that it has been seen.</summary>
    public async Task FinishAsync()
    {
        IsVisible = false;

        if (!_settings.Current.HasSeenWelcome)
        {
            var updated = _settings.Current.Clone();
            updated.HasSeenWelcome = true;
            await _settings.SaveAsync(updated).ConfigureAwait(true);
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }
}
