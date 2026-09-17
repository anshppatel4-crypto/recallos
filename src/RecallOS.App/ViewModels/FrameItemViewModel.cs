using CommunityToolkit.Mvvm.ComponentModel;
using RecallOS.Core.Models;
using RecallOS.Core.Storage;

namespace RecallOS.App.ViewModels;

/// <summary>
/// One row in the results list: a frame, prepared for display.
/// </summary>
/// <remarks>
/// The view never touches a <see cref="CaptureFrame"/> directly. Paths in the database are
/// relative to the store so the folder stays movable, and resolving them to absolute paths
/// is a presentation concern -- doing it here means the XAML binds to something it can load
/// and the domain model keeps its portability.
/// </remarks>
public sealed partial class FrameItemViewModel : ObservableObject
{
    private readonly FrameFileStore _files;

    public FrameItemViewModel(SearchHit hit, FrameFileStore files)
    {
        ArgumentNullException.ThrowIfNull(hit);

        _files = files ?? throw new ArgumentNullException(nameof(files));

        Frame = hit.Frame;
        Score = hit.Score;
        KeywordScore = hit.KeywordScore;
        SemanticScore = hit.SemanticScore;
        Highlights = hit.Highlights;

        // A frame whose OCR has not run yet has no snippet to show; saying so is better
        // than an empty row that looks like a bug.
        Snippet = hit.Snippet.Length > 0
            ? hit.Snippet
            : hit.Frame.OcrStatus switch
            {
                OcrStatus.Pending => "Reading text…",
                OcrStatus.Failed => "Text could not be extracted from this frame.",
                OcrStatus.Skipped => "Text extraction was turned off for this frame.",
                _ => "No text was found on screen."
            };
    }

    public CaptureFrame Frame { get; }

    public long Id => Frame.Id;

    public string Title => Frame.DisplayTitle;

    public DateTimeOffset CapturedAt => Frame.CapturedAt;

    public string Snippet { get; }

    public IReadOnlyList<TextSpan> Highlights { get; }

    public double Score { get; }

    public double KeywordScore { get; }

    public double SemanticScore { get; }

    public string? ProcessName => Frame.ProcessName;

    public string? IntentLabel => Frame.IntentLabel;

    public bool HasIntent => !string.IsNullOrEmpty(Frame.IntentLabel);

    /// <summary>Thumbnail if one exists, else the full image, else null for the empty state.</summary>
    public string? PreviewPath => _files.ResolveBestPreview(Frame.ImagePath, Frame.ThumbnailPath);

    public string? FullImagePath => _files.ResolveImage(Frame.ImagePath);

    /// <summary>
    /// Why this result ranked where it did, in plain words. A search tool that cannot
    /// explain its ordering is one the user has to take on faith.
    /// </summary>
    public string MatchExplanation => (KeywordScore > 0, SemanticScore > 0) switch
    {
        (true, true) => $"word and meaning match ({Score:P0})",
        (true, false) => $"word match ({KeywordScore:P0})",
        (false, true) => $"meaning match ({SemanticScore:P0})",
        _ => Frame.CapturedAt.ToLocalTime().ToString("HH:mm:ss")
    };

    public string SizeText => $"{Frame.Width}×{Frame.Height}";
}
