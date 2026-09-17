namespace RecallOS.Core.Models;

/// <summary>
/// A contiguous, semantically coherent run of text from one frame. Chunks are the
/// unit of embedding and semantic retrieval: a whole screenful of text is too coarse
/// to match a short query against, a single word is too fine.
/// </summary>
public sealed class TextChunk
{
    public long Id { get; init; }

    public long FrameId { get; init; }

    /// <summary>Position of this chunk within its frame, starting at zero.</summary>
    public int Ordinal { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>Bounding box in image pixels, when the engine reported word geometry.</summary>
    public PixelRect? Bounds { get; init; }

    public double Confidence { get; init; }
}

/// <summary>An axis-aligned rectangle in image pixel space.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public static PixelRect Union(PixelRect a, PixelRect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
