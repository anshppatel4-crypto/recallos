using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RecallOS.Core.Capture;

/// <summary>
/// A 64-bit difference hash ("dHash") of a frame.
/// </summary>
/// <remarks>
/// Auto-capture on a timer produces enormous runs of identical screens -- a user reading a
/// page for ten minutes generates twenty byte-different but visually identical PNGs. Comparing
/// file bytes will not catch those, because a blinking caret or a clock changes the encoding
/// completely while changing nothing the user would call "a different screen".
/// <para>
/// dHash works by shrinking the frame to 9x8 greyscale and recording, for each row, whether
/// each pixel is brighter than the one to its right. That makes it sensitive to layout and
/// insensitive to brightness, compression and one-pixel noise, which is exactly the tradeoff
/// we want. Two frames within a small Hamming distance are treated as the same screen.
/// </para>
/// </remarks>
public static class PerceptualHash
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;

    /// <summary>Compute the dHash of a bitmap. Never throws for a valid bitmap.</summary>
    public static ulong Compute(Bitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var small = new Bitmap(HashWidth, HashHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            // Bilinear, not NearestNeighbor: we want the average of a region to survive the
            // downscale so that small movements do not flip a bit.
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, HashWidth, HashHeight));
        }

        Span<double> luminance = stackalloc double[HashWidth * HashHeight];
        var data = small.LockBits(
            new Rectangle(0, 0, HashWidth, HashHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                var scan0 = (byte*)data.Scan0;
                for (var y = 0; y < HashHeight; y++)
                {
                    var row = scan0 + (y * data.Stride);
                    for (var x = 0; x < HashWidth; x++)
                    {
                        var pixel = row + (x * 3);
                        // Rec. 601 luma; matches how a person perceives brightness.
                        luminance[(y * HashWidth) + x] =
                            (0.114 * pixel[0]) + (0.587 * pixel[1]) + (0.299 * pixel[2]);
                    }
                }
            }
        }
        finally
        {
            small.UnlockBits(data);
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < HashHeight; y++)
        {
            for (var x = 0; x < HashWidth - 1; x++)
            {
                var left = luminance[(y * HashWidth) + x];
                var right = luminance[(y * HashWidth) + x + 1];
                if (left > right)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    /// <summary>Number of differing bits between two hashes, 0..64.</summary>
    public static int Distance(ulong left, ulong right) => System.Numerics.BitOperations.PopCount(left ^ right);

    /// <summary>True when two frames are close enough to count as the same screen.</summary>
    public static bool AreSimilar(ulong left, ulong right, int tolerance) => Distance(left, right) <= tolerance;
}
