using RecallOS.Core.Models;

namespace RecallOS.Core.Abstractions;

/// <summary>
/// Turns an image into text. Deliberately narrow so the Tesseract implementation can be
/// swapped for a Windows.Media.Ocr or cloud engine without touching the pipeline.
/// </summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>Stable identifier recorded against every frame this engine processes.</summary>
    string Name { get; }

    /// <summary>
    /// False when the engine cannot run -- missing language data, for instance. The pipeline
    /// keeps capturing and marks frames <see cref="OcrStatus.Skipped"/> rather than failing.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Why the engine is unavailable, for display in the UI.</summary>
    string? UnavailableReason { get; }

    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default);

    Task<OcrResult> RecognizeAsync(System.Drawing.Bitmap bitmap, CancellationToken cancellationToken = default);
}
