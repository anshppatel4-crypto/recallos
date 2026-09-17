using System.Text;

namespace RecallOS.Core.Ocr;

/// <summary>
/// Cleans raw OCR output before it is stored and indexed.
/// </summary>
/// <remarks>
/// OCR over a screenshot picks up a lot that is not text: window-chrome fragments, icon
/// edges read as punctuation, single stray characters from borders. Left in, that noise
/// inflates the index, pollutes embeddings and produces search snippets that look broken.
/// Normalising once at ingest is much cheaper than filtering on every query.
/// </remarks>
public static class OcrTextNormalizer
{
    /// <summary>Lines with a lower share of letters and digits than this are treated as noise.</summary>
    private const double MinimumAlphanumericRatio = 0.34;

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        var blankRun = 0;

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = CollapseWhitespace(rawLine);

            if (line.Length == 0)
            {
                // Keep paragraph structure, but never more than one blank line: OCR emits
                // long stretches of empty lines for whitespace on screen.
                blankRun++;
                if (blankRun == 1 && builder.Length > 0)
                {
                    builder.Append('\n');
                }

                continue;
            }

            if (IsNoise(line))
            {
                continue;
            }

            blankRun = 0;
            builder.Append(line).Append('\n');
        }

        return builder.ToString().Trim();
    }

    /// <summary>Squash runs of whitespace (including the tabs OCR inserts between columns) to one space.</summary>
    private static string CollapseWhitespace(string line)
    {
        var builder = new StringBuilder(line.Length);
        var previousWasSpace = false;

        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(c);
            previousWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// True for lines that carry no recoverable meaning: a lone character, or a line that
    /// is mostly punctuation because it was really a border or a row of icons.
    /// </summary>
    private static bool IsNoise(string line)
    {
        if (line.Length <= 1)
        {
            return true;
        }

        var alphanumeric = 0;
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c))
            {
                alphanumeric++;
            }
        }

        if (alphanumeric == 0)
        {
            return true;
        }

        // Short lines are given the benefit of the doubt: "OK", "3:41" and "v2" are all
        // real screen text that a ratio test would otherwise throw away.
        if (line.Length <= 4)
        {
            return false;
        }

        return (double)alphanumeric / line.Length < MinimumAlphanumericRatio;
    }
}
