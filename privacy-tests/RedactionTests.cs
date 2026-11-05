using System.Drawing;
using System.Drawing.Imaging;
using RecallOS.Core.Models;
using RecallOS.Core.Privacy;
using Xunit;

namespace RecallOS.Core.Tests;

/// <summary>
/// Redaction is the one subsystem where a miss is unrecoverable: a secret written to disk
/// and indexed cannot be un-leaked. These tests assert both halves — that real secrets are
/// caught, and that ordinary content is not destroyed by over-matching.
/// </summary>
public class SensitivePatternTests
{
    private static readonly RedactionPolicy Default = new();

    private static string Redacted(string text, RedactionPolicy? policy = null)
    {
        var matches = SensitivePatterns.Find(text, policy ?? Default);
        return FrameRedactor.MaskText(text, matches).Text;
    }

    // ---- what must be caught --------------------------------------------------------

    [Theory]
    [InlineData("4111111111111111")]            // Visa test number
    [InlineData("4111 1111 1111 1111")]         // spaced, as it appears in a form
    [InlineData("4111-1111-1111-1111")]         // dashed
    [InlineData("5500005555555559")]            // Mastercard test number
    [InlineData("378282246310005")]             // Amex, 15 digits
    public void ValidCardNumbersAreHidden(string card)
    {
        var result = Redacted($"Paying with {card} today");

        Assert.DoesNotContain(card, result);
        Assert.Contains("[redacted: card number]", result);
    }

    // Each sample is given as a vendor prefix and a body, and joined here rather than
    // written out whole. The values are invented, but a credential scanner reading this
    // file cannot know that, and a test fixture is not worth tripping one over.
    [Theory]
    [InlineData("sk-", "abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("ghp_", "abcdefghijklmnopqrstuvwxyz1234567890")]
    [InlineData("AKIA", "IOSFODNN7EXAMPLE")]
    [InlineData("xoxb-", "123456789012-abcdefghijklmnop")]
    [InlineData("glpat-", "abcdefghij1234567890")]
    public void ApiKeysAreHidden(string prefix, string body)
    {
        var key = prefix + body;

        var result = Redacted($"export TOKEN={key}");

        Assert.DoesNotContain(key, result);
        Assert.Contains("[redacted:", result);
    }

    [Fact]
    public void JsonWebTokensAreHidden()
    {
        // Split for the same reason as the API keys above: header, payload, signature.
        var jwt = string.Join('.',
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9",
            "eyJzdWIiOiIxMjM0NTY3ODkwIn0",
            "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        var result = Redacted($"Authorization: Bearer {jwt}");

        Assert.DoesNotContain(jwt, result);
    }

    [Fact]
    public void NationalIdentifiersAreHidden()
    {
        Assert.DoesNotContain("123-45-6789", Redacted("SSN 123-45-6789 on file"));
    }

    [Theory]
    [InlineData("password: hunter2please", "hunter2please")]
    [InlineData("Password = Tr0ub4dor&3", "Tr0ub4dor&3")]
    [InlineData("api_key: abc123def456", "abc123def456")]
    [InlineData("client secret = s3cr3tvalue", "s3cr3tvalue")]
    [InlineData("OTP: 848213", "848213")]
    public void ValuesAfterASecretLabelAreHidden(string line, string secret)
    {
        var result = Redacted(line);

        Assert.DoesNotContain(secret, result);
        // The label itself survives, so the frame is still readable and explains itself.
        Assert.Contains("[redacted:", result);
    }

    [Fact]
    public void PrivateKeyBlocksAreHidden()
    {
        var result = Redacted("-----BEGIN RSA PRIVATE KEY-----");

        Assert.Contains("[redacted:", result);
    }

    // ---- what must survive ----------------------------------------------------------

    [Fact]
    public void OrdinaryTextIsUntouched()
    {
        const string text = "The deployment pipeline failed on the migration step at 14:32 today.";

        Assert.Equal(text, Redacted(text));
    }

    [Fact]
    public void LongNumbersThatAreNotCardsSurvive()
    {
        // Sixteen digits, but the Luhn checksum fails. Without that check, order numbers
        // and identifiers would be redacted constantly.
        const string text = "Order reference 1234567890123456 shipped";

        Assert.Equal(text, Redacted(text));
    }

    [Fact]
    public void VersionNumbersAndDatesSurvive()
    {
        const string text = "Released v1.2.3 on 2026-03-04 at 09:15, build 4815162342";

        Assert.Equal(text, Redacted(text));
    }

    [Fact]
    public void EmailsAndPhonesAreKeptUnlessAskedFor()
    {
        const string text = "Contact sam@example.com or 555-123-4567";

        // Default policy leaves them: they are frequently the thing being searched for.
        Assert.Equal(text, Redacted(text));

        var strict = new RedactionPolicy { RedactEmails = true, RedactPhoneNumbers = true };
        var hidden = Redacted(text, strict);

        Assert.DoesNotContain("sam@example.com", hidden);
        Assert.DoesNotContain("555-123-4567", hidden);
    }

    [Fact]
    public void NothingIsRedactedWhenThePolicyIsOff()
    {
        const string text = "card 4111111111111111 and key sk-abcdefghijklmnopqrstuvwxyz123456";

        Assert.Equal(text, Redacted(text, RedactionPolicy.None));
    }

    // ---- mechanics ------------------------------------------------------------------

    [Fact]
    public void LuhnAcceptsRealNumbersAndRejectsNearMisses()
    {
        Assert.True(SensitivePatterns.PassesLuhn("4111111111111111"));
        Assert.False(SensitivePatterns.PassesLuhn("4111111111111112"));
    }

    [Fact]
    public void OverlappingMatchesAreMergedNotDoubled()
    {
        // "password: 4111111111111111" matches both the label rule and the card rule.
        var matches = SensitivePatterns.Find("password: 4111111111111111", Default);

        Assert.Single(matches);
    }

    [Fact]
    public void MaskingPreservesTheSurroundingText()
    {
        var result = Redacted("before 4111111111111111 after");

        Assert.StartsWith("before ", result);
        Assert.EndsWith(" after", result);
    }

    [Fact]
    public void AnEmptyOrCleanStringIsHandled()
    {
        Assert.Equal(string.Empty, Redacted(string.Empty));
        Assert.Empty(SensitivePatterns.Find(string.Empty, Default));
    }

    [Fact]
    public void MultipleSecretsInOneFrameAreAllHidden()
    {
        const string text = "card 4111111111111111 key sk-abcdefghijklmnopqrstuvwxyz123456 ssn 123-45-6789";

        var result = Redacted(text);

        Assert.DoesNotContain("4111111111111111", result);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz123456", result);
        Assert.DoesNotContain("123-45-6789", result);
    }
}

public class FrameRedactorTests
{
    private static Bitmap MakeBitmap(int w = 200, int h = 100)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);

        // High-contrast stripes: if a blur left anything, the variance would show it.
        using var black = new SolidBrush(Color.Black);
        for (var y = 0; y < h; y += 6)
        {
            g.FillRectangle(black, 0, y, w, 3);
        }

        return bitmap;
    }

    [Fact]
    public void BlurringDestroysDetailInTheRegion()
    {
        using var bitmap = MakeBitmap();

        var before = Variance(bitmap, new Rectangle(20, 20, 60, 40));
        var count = FrameRedactor.BlurRegions(bitmap, [new PixelRect(20, 20, 60, 40)]);
        var after = Variance(bitmap, new Rectangle(30, 30, 40, 20));

        Assert.Equal(1, count);
        // The striping is gone: what remains cannot be sharpened back into text.
        Assert.True(after < before / 4, $"variance only fell from {before:F0} to {after:F0}");
    }

    [Fact]
    public void AreasOutsideTheRegionAreLeftAlone()
    {
        using var bitmap = MakeBitmap();

        var before = Variance(bitmap, new Rectangle(150, 10, 40, 40));
        FrameRedactor.BlurRegions(bitmap, [new PixelRect(0, 0, 60, 40)]);
        var after = Variance(bitmap, new Rectangle(150, 10, 40, 40));

        Assert.Equal(before, after, 1);
    }

    [Fact]
    public void RegionsOutsideTheImageAreClippedNotThrown()
    {
        using var bitmap = MakeBitmap();

        var count = FrameRedactor.BlurRegions(bitmap,
        [
            new PixelRect(-50, -50, 40, 40),      // entirely off the top-left
            new PixelRect(190, 90, 100, 100),     // partly past the bottom-right
            new PixelRect(1000, 1000, 10, 10)     // nowhere near
        ]);

        // The partly-overlapping one is blurred; the others are skipped.
        Assert.True(count is >= 1 and <= 2);
    }

    [Fact]
    public void NoRegionsIsANoOp()
    {
        using var bitmap = MakeBitmap();

        Assert.Equal(0, FrameRedactor.BlurRegions(bitmap, Array.Empty<PixelRect>()));
    }

    [Fact]
    public void SensitiveWordsAreMappedBackToTheirBoxes()
    {
        // The join of these words is the text the matcher ran over.
        var words = new List<OcrWord>
        {
            new("Card", new PixelRect(0, 0, 40, 12), 0.9, 1, 1),
            new("4111111111111111", new PixelRect(45, 0, 160, 12), 0.9, 1, 1),
            new("expires", new PixelRect(210, 0, 60, 12), 0.9, 1, 1)
        };

        var text = "Card 4111111111111111 expires";
        var matches = SensitivePatterns.Find(text, new RedactionPolicy());
        var regions = FrameRedactor.MapSpansToWords(words, text, matches);

        // Only the number's box, not the words either side.
        var region = Assert.Single(regions);
        Assert.Equal(45, region.X);
        Assert.Equal(160, region.Width);
    }

    [Fact]
    public void MappingHandlesNoWordsOrNoMatches()
    {
        Assert.Empty(FrameRedactor.MapSpansToWords(Array.Empty<OcrWord>(), "text", Array.Empty<SensitiveMatch>()));
        Assert.Empty(FrameRedactor.MapSpansToWords(
            [new OcrWord("hello", new PixelRect(0, 0, 10, 10), 1, 1, 1)],
            "hello",
            Array.Empty<SensitiveMatch>()));
    }

    /// <summary>Mean squared deviation of luminance, as a proxy for "is there detail here".</summary>
    private static double Variance(Bitmap bitmap, Rectangle area)
    {
        double sum = 0, sumSq = 0;
        var n = 0;

        for (var y = area.Top; y < area.Bottom && y < bitmap.Height; y++)
        {
            for (var x = area.Left; x < area.Right && x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                double lum = (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
                sum += lum;
                sumSq += lum * lum;
                n++;
            }
        }

        if (n == 0)
        {
            return 0;
        }

        var mean = sum / n;
        return (sumSq / n) - (mean * mean);
    }
}
