using System.Drawing;
using System.Drawing.Imaging;
using RecallOS.Core.Capture;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Change detection decides whether a frame is worth storing at all, so these properties
/// are what stand between the user and a disk full of identical screenshots.
/// </summary>
public class PerceptualHashTests
{
    private static Bitmap MakeBitmap(int width, int height, Func<int, int, Color> shade)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, shade(x, y));
            }
        }

        return bitmap;
    }

    /// <summary>
    /// A banded test pattern defined in normalised coordinates, so the same "content"
    /// can be rendered at any resolution. Using absolute pixel coordinates here would
    /// make a scaled copy a genuinely different image, not the same one larger.
    /// </summary>
    private static Bitmap Gradient(int width = 120, int height = 90, bool invert = false) =>
        MakeBitmap(width, height, (x, y) =>
        {
            var nx = (double)x / width;
            var ny = (double)y / height;

            var band = ((int)(nx * 6) + (int)(ny * 4)) % 3;
            var value = band switch { 0 => 30, 1 => 128, _ => 220 };

            if (invert)
            {
                value = 255 - value;
            }

            return Color.FromArgb(value, value, value);
        });

    [Fact]
    public void TheSameImageAlwaysHashesTheSame()
    {
        using var a = Gradient();
        using var b = Gradient();

        Assert.Equal(PerceptualHash.Compute(a), PerceptualHash.Compute(b));
    }

    [Fact]
    public void ADifferentImageHashesDifferently()
    {
        using var gradient = Gradient();
        using var checker = MakeBitmap(120, 90, (x, y) =>
            (x / 8 + y / 8) % 2 == 0 ? Color.Black : Color.White);

        Assert.NotEqual(PerceptualHash.Compute(gradient), PerceptualHash.Compute(checker));
    }

    [Fact]
    public void ATinyLocalChangeStaysWithinTheDefaultTolerance()
    {
        // This is the blinking-cursor case: a handful of pixels differ and the frame
        // must not be treated as a new screen.
        using var original = Gradient();
        using var nudged = Gradient();

        for (var x = 40; x < 44; x++)
        {
            for (var y = 40; y < 44; y++)
            {
                nudged.SetPixel(x, y, Color.Red);
            }
        }

        var distance = PerceptualHash.Distance(
            PerceptualHash.Compute(original),
            PerceptualHash.Compute(nudged));

        Assert.True(distance <= 6, $"distance was {distance}");
        Assert.True(PerceptualHash.AreSimilar(
            PerceptualHash.Compute(original), PerceptualHash.Compute(nudged), 6));
    }

    [Fact]
    public void AWhollyDifferentScreenExceedsTheTolerance()
    {
        using var gradient = Gradient();
        using var inverted = Gradient(invert: true);

        var distance = PerceptualHash.Distance(
            PerceptualHash.Compute(gradient),
            PerceptualHash.Compute(inverted));

        Assert.False(PerceptualHash.AreSimilar(
            PerceptualHash.Compute(gradient), PerceptualHash.Compute(inverted), 6),
            $"distance was {distance}");
    }

    [Fact]
    public void ScalingTheSameContentPreservesTheHash()
    {
        // Resolution changes (a monitor switch, DPI change) should not read as new content.
        using var large = Gradient(480, 360);
        using var small = Gradient(120, 90);

        var distance = PerceptualHash.Distance(
            PerceptualHash.Compute(large),
            PerceptualHash.Compute(small));

        Assert.True(distance <= 10, $"distance was {distance}");
    }

    [Fact]
    public void DistanceIsSymmetricAndZeroForIdenticalHashes()
    {
        Assert.Equal(0, PerceptualHash.Distance(0xDEADBEEFUL, 0xDEADBEEFUL));
        Assert.Equal(
            PerceptualHash.Distance(0x1234UL, 0x5678UL),
            PerceptualHash.Distance(0x5678UL, 0x1234UL));
    }

    [Fact]
    public void DistanceNeverExceedsTheHashWidth()
    {
        Assert.Equal(64, PerceptualHash.Distance(0UL, ulong.MaxValue));
    }

    [Fact]
    public void AOnePixelImageDoesNotThrow()
    {
        using var tiny = MakeBitmap(1, 1, (_, _) => Color.White);

        PerceptualHash.Compute(tiny);
    }
}
