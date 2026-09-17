namespace RecallOS.Core.Models;

/// <summary>Everything an OCR engine produced for a single image.</summary>
public sealed class OcrResult
{
    public static readonly OcrResult Empty = new();

    /// <summary>Full text in reading order, lines separated by <c>\n</c>.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Mean word confidence, 0..1.</summary>
    public double Confidence { get; init; }

    /// <summary>Identifier of the engine, recorded so results stay attributable across upgrades.</summary>
    public string Engine { get; init; } = "none";

    /// <summary>Individual words with geometry, when the engine exposes them.</summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();

    /// <summary>Wall-clock cost of the recognition pass.</summary>
    public TimeSpan Duration { get; init; }

    public bool IsEmpty => Text.Length == 0;
}

/// <summary>One recognised word and where it sat on screen.</summary>
public readonly record struct OcrWord(string Text, PixelRect Bounds, double Confidence, int LineIndex, int BlockIndex);
