using System.Text;
using RecallOS.Core.Models;

namespace RecallOS.Core.Ocr;

/// <summary>
/// Splits a frame's text into the units that get embedded and retrieved.
/// </summary>
/// <remarks>
/// <para>
/// Chunk size is the whole game for semantic recall. Embed a full screenful and every
/// vector drifts toward the average of a sidebar, a header and the actual content, so
/// nothing matches a specific question. Embed single lines and there is too little context
/// for a vector to mean anything. A few hundred characters -- roughly a paragraph -- is the
/// range where a chunk is about one thing and still has enough signal to match against.
/// </para>
/// <para>
/// Chunks overlap slightly so a sentence spanning a boundary is still findable from either
/// side, which is the standard fix for the "the answer was split in half" failure.
/// </para>
/// </remarks>
public sealed class TextChunker
{
    private readonly int _targetSize;
    private readonly int _maximumSize;
    private readonly int _overlap;
    private readonly int _minimumSize;

    public TextChunker(int targetSize = 420, int maximumSize = 700, int overlap = 60, int minimumSize = 16)
    {
        _targetSize = Math.Max(64, targetSize);
        _maximumSize = Math.Max(_targetSize, maximumSize);
        _overlap = Math.Clamp(overlap, 0, _targetSize / 2);
        _minimumSize = Math.Max(1, minimumSize);
    }

    /// <summary>
    /// Chunk a frame. When word geometry is available it drives the split, because the
    /// engine's block structure is a far better guide to what belongs together than
    /// character counting is. Otherwise the plain text is split on line boundaries.
    /// </summary>
    public IReadOnlyList<TextChunk> Chunk(long frameId, string text, IReadOnlyList<OcrWord>? words = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<TextChunk>();
        }

        return words is { Count: > 0 }
            ? ChunkByLayout(frameId, words)
            : ChunkByText(frameId, text);
    }

    /// <summary>Group words into blocks, then pack each block into chunks with geometry.</summary>
    private List<TextChunk> ChunkByLayout(long frameId, IReadOnlyList<OcrWord> words)
    {
        var chunks = new List<TextChunk>();
        var builder = new StringBuilder();
        PixelRect? bounds = null;
        var confidenceSum = 0d;
        var wordCount = 0;
        var currentBlock = words[0].BlockIndex;
        var currentLine = words[0].LineIndex;

        void Flush()
        {
            var content = builder.ToString().Trim();
            builder.Clear();

            var boundsForChunk = bounds;
            var averageConfidence = wordCount > 0 ? confidenceSum / wordCount : 0d;

            bounds = null;
            confidenceSum = 0;
            wordCount = 0;

            if (content.Length < _minimumSize)
            {
                return;
            }

            chunks.Add(new TextChunk
            {
                FrameId = frameId,
                Ordinal = chunks.Count,
                Text = content,
                Bounds = boundsForChunk,
                Confidence = averageConfidence
            });
        }

        foreach (var word in words)
        {
            // A new block is a hard boundary: Tesseract puts a sidebar and an article body
            // in different blocks, and merging them would blur two unrelated things.
            var blockChanged = word.BlockIndex != currentBlock;
            var tooLong = builder.Length >= _targetSize;

            if (blockChanged || (tooLong && word.LineIndex != currentLine))
            {
                Flush();
            }

            if (builder.Length > 0)
            {
                builder.Append(word.LineIndex != currentLine ? '\n' : ' ');
            }

            builder.Append(word.Text);

            bounds = bounds is { } existing && word.Bounds.Width > 0
                ? PixelRect.Union(existing, word.Bounds)
                : word.Bounds.Width > 0 ? word.Bounds : bounds;

            confidenceSum += word.Confidence;
            wordCount++;
            currentBlock = word.BlockIndex;
            currentLine = word.LineIndex;

            if (builder.Length >= _maximumSize)
            {
                Flush();
            }
        }

        Flush();
        return chunks;
    }

    /// <summary>Pack whole lines into chunks, carrying a little text across each boundary.</summary>
    private List<TextChunk> ChunkByText(long frameId, string text)
    {
        var chunks = new List<TextChunk>();
        var lines = text.Split('\n', StringSplitOptions.None);
        var builder = new StringBuilder();

        void Flush()
        {
            var content = builder.ToString().Trim();
            if (content.Length >= _minimumSize)
            {
                chunks.Add(new TextChunk
                {
                    FrameId = frameId,
                    Ordinal = chunks.Count,
                    Text = content,
                    Confidence = 0d
                });
            }

            builder.Clear();

            // Carry the tail forward so a boundary never cuts a thought in half.
            if (_overlap > 0 && content.Length > _overlap)
            {
                var tail = content[^_overlap..];
                var space = tail.IndexOf(' ');
                if (space >= 0 && space < tail.Length - 1)
                {
                    // Start the overlap at a word boundary, not mid-token.
                    builder.Append(tail[(space + 1)..]).Append('\n');
                }
            }
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                // A blank line is a paragraph break, and paragraphs are exactly the
                // boundaries we would have chosen anyway.
                if (builder.Length >= _targetSize / 2)
                {
                    Flush();
                }

                continue;
            }

            // A single line longer than the maximum (a minified file, a long URL) has to be
            // broken by length; there is no better structure available.
            if (trimmed.Length > _maximumSize)
            {
                Flush();
                foreach (var slice in SplitLongLine(trimmed))
                {
                    chunks.Add(new TextChunk
                    {
                        FrameId = frameId,
                        Ordinal = chunks.Count,
                        Text = slice,
                        Confidence = 0d
                    });
                }

                continue;
            }

            if (builder.Length + trimmed.Length > _maximumSize)
            {
                Flush();
            }

            builder.Append(trimmed).Append('\n');

            if (builder.Length >= _targetSize)
            {
                Flush();
            }
        }

        // The final flush can leave only carried-over overlap behind, which would duplicate
        // text already stored, so it is dropped rather than emitted.
        var remainder = builder.ToString().Trim();
        if (remainder.Length >= _minimumSize && (chunks.Count == 0 || !chunks[^1].Text.EndsWith(remainder, StringComparison.Ordinal)))
        {
            chunks.Add(new TextChunk
            {
                FrameId = frameId,
                Ordinal = chunks.Count,
                Text = remainder,
                Confidence = 0d
            });
        }

        return chunks;
    }

    private IEnumerable<string> SplitLongLine(string line)
    {
        for (var start = 0; start < line.Length; start += _targetSize)
        {
            yield return line.Substring(start, Math.Min(_targetSize, line.Length - start));
        }
    }
}
