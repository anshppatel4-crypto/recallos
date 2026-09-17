using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Search;
using Xunit;

namespace RecallOS.Core.Tests;

public class OcrTextNormalizerTests
{
    [Fact]
    public void RunsOfWhitespace_CollapseToSingleSpaces()
    {
        Assert.Equal("hello world", OcrTextNormalizer.Normalize("hello     \t  world"));
    }

    [Fact]
    public void LongRunsOfBlankLines_CollapseToOne()
    {
        var result = OcrTextNormalizer.Normalize("first\n\n\n\n\nsecond");

        Assert.Equal("first\n\nsecond", result);
    }

    [Fact]
    public void PunctuationOnlyLines_AreDroppedAsBorderNoise()
    {
        var result = OcrTextNormalizer.Normalize("real content here\n|||||||||||\nmore real content");

        Assert.DoesNotContain("|||", result);
        Assert.Contains("real content here", result);
        Assert.Contains("more real content", result);
    }

    [Fact]
    public void ShortRealTokens_SurviveTheNoiseFilter()
    {
        // A ratio test alone would discard these; short lines are exempted for exactly this.
        var result = OcrTextNormalizer.Normalize("OK\n3:41\nv2");

        Assert.Contains("OK", result);
        Assert.Contains("3:41", result);
        Assert.Contains("v2", result);
    }

    [Fact]
    public void SingleCharacterLines_AreDropped()
    {
        Assert.Equal("actual text", OcrTextNormalizer.Normalize("x\nactual text\n-"));
    }

    [Fact]
    public void NullOrBlank_YieldsAnEmptyString()
    {
        Assert.Equal(string.Empty, OcrTextNormalizer.Normalize(null));
        Assert.Equal(string.Empty, OcrTextNormalizer.Normalize("   \n\n  "));
    }
}

public class TextChunkerTests
{
    [Fact]
    public void EmptyText_ProducesNoChunks()
    {
        Assert.Empty(new TextChunker().Chunk(1, string.Empty));
    }

    [Fact]
    public void ShortText_BecomesASingleChunk()
    {
        var chunks = new TextChunker().Chunk(7, "A short paragraph about deployment pipelines.");

        Assert.Single(chunks);
        Assert.Equal(7, chunks[0].FrameId);
        Assert.Equal(0, chunks[0].Ordinal);
    }

    [Fact]
    public void LongText_IsSplitAndOrdinalsAreSequential()
    {
        var paragraph = string.Join("\n", Enumerable.Range(0, 60)
            .Select(i => $"Line {i} describing something moderately long about the system."));

        var chunks = new TextChunker(targetSize: 200, maximumSize: 320).Chunk(1, paragraph);

        Assert.True(chunks.Count > 1);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 400));
    }

    [Fact]
    public void AVeryLongSingleLine_IsBrokenByLength()
    {
        // A minified file or a huge URL has no structure to split on.
        var line = new string('a', 3000);
        var chunks = new TextChunker(targetSize: 200, maximumSize: 300).Chunk(1, line);

        Assert.True(chunks.Count >= 10);
    }

    [Fact]
    public void WordGeometry_DrivesTheSplitWhenAvailable()
    {
        // Two separate blocks must not be merged into one chunk.
        var words = new List<OcrWord>
        {
            new("Sidebar", new PixelRect(0, 0, 50, 10), 0.9, 1, 1),
            new("Navigation", new PixelRect(0, 12, 50, 10), 0.9, 1, 1),
            new("Article", new PixelRect(200, 0, 50, 10), 0.9, 2, 2),
            new("Content", new PixelRect(200, 12, 50, 10), 0.9, 2, 2)
        };

        var chunks = new TextChunker(minimumSize: 1).Chunk(1, "ignored", words);

        Assert.Equal(2, chunks.Count);
        Assert.Contains("Sidebar", chunks[0].Text);
        Assert.Contains("Article", chunks[1].Text);
    }

    [Fact]
    public void ChunksFromGeometry_CarryABoundingBox()
    {
        var words = new List<OcrWord>
        {
            new("Alpha", new PixelRect(10, 10, 40, 12), 0.8, 1, 1),
            new("Beta", new PixelRect(60, 10, 30, 12), 0.8, 1, 1)
        };

        var chunk = new TextChunker(minimumSize: 1).Chunk(1, "ignored", words).Single();

        Assert.NotNull(chunk.Bounds);
        // The union spans both words.
        Assert.Equal(10, chunk.Bounds!.Value.X);
        Assert.Equal(90, chunk.Bounds!.Value.Right);
    }
}

public class SnippetBuilderTests
{
    [Fact]
    public void TheSnippetIsCenteredOnTheMatch()
    {
        var text = new string('x', 500) + " TARGETWORD " + new string('y', 500);

        var snippet = SnippetBuilder.Build(text, ["TARGETWORD"]);

        Assert.Contains("TARGETWORD", snippet.Text);
        Assert.NotEmpty(snippet.Highlights);
    }

    [Fact]
    public void HighlightOffsetsPointAtTheMatchInsideTheSnippet()
    {
        var snippet = SnippetBuilder.Build("the quick brown fox", ["brown"]);

        var span = Assert.Single(snippet.Highlights);
        Assert.Equal("brown", snippet.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void OverlappingMatches_AreMergedIntoOneSpan()
    {
        // "deploy" and "deployment" overlap; two nested spans would double-render.
        var snippet = SnippetBuilder.Build("running a deployment now", ["deploy", "deployment"]);

        Assert.Single(snippet.Highlights);
    }

    [Fact]
    public void WithNoTerms_TheOpeningOfTheTextIsUsed()
    {
        var snippet = SnippetBuilder.Build("Beginning of the document text.", []);

        Assert.StartsWith("Beginning", snippet.Text);
        Assert.Empty(snippet.Highlights);
    }

    [Fact]
    public void EmptyText_YieldsAnEmptySnippet()
    {
        var snippet = SnippetBuilder.Build(string.Empty, ["anything"]);

        Assert.Equal(string.Empty, snippet.Text);
        Assert.Empty(snippet.Highlights);
    }

    [Fact]
    public void LongTextIsTruncatedToTheRequestedLength()
    {
        var snippet = SnippetBuilder.Build(new string('a', 5000), [], maxLength: 100);

        // Allowing for the ellipsis characters added at the cut points.
        Assert.True(snippet.Text.Length <= 102);
    }
}
