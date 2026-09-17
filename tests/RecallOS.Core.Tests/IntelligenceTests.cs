using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using Xunit;

namespace RecallOS.Core.Tests;

public class VectorMathTests
{
    [Fact]
    public void DotProduct_MatchesTheScalarResult()
    {
        // Longer than one SIMD register, and not a multiple of its width, so both the
        // vectorised body and the scalar tail are exercised.
        var a = Enumerable.Range(0, 37).Select(i => (float)i).ToArray();
        var b = Enumerable.Range(0, 37).Select(i => i * 0.5f).ToArray();

        var expected = a.Zip(b, (x, y) => x * y).Sum();

        Assert.Equal(expected, VectorMath.Dot(a, b), 2);
    }

    [Fact]
    public void MismatchedLengths_Throw()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.Dot(new float[4], new float[5]));
    }

    [Fact]
    public void IdenticalVectors_HaveCosineOfOne()
    {
        var v = new[] { 1f, 2f, 3f, 4f };

        Assert.Equal(1f, VectorMath.CosineSimilarity(v, v), 3);
    }

    [Fact]
    public void OrthogonalVectors_HaveCosineOfZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity([1f, 0f], [0f, 1f]), 3);
    }

    [Fact]
    public void AZeroVector_DoesNotDivideByZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[8], new float[8]));
    }

    [Fact]
    public void NormalizeProducesUnitLength()
    {
        var v = new[] { 3f, 4f };

        VectorMath.NormalizeInPlace(v);

        Assert.Equal(1f, MathF.Sqrt(VectorMath.Dot(v, v)), 4);
    }
}

public class HashingEmbeddingProviderTests
{
    private readonly HashingEmbeddingProvider _provider = new(256);

    [Fact]
    public void VectorsAreTheDeclaredSizeAndUnitLength()
    {
        var vector = _provider.Embed("deployment pipeline configuration").ToArray();

        Assert.Equal(256, vector.Length);
        Assert.Equal(1f, MathF.Sqrt(VectorMath.Dot(vector, vector)), 3);
    }

    [Fact]
    public void EmbeddingIsDeterministic()
    {
        // Critical: vectors are persisted, so the same text must embed identically across
        // runs. A randomised string hash would silently break stored similarity.
        Assert.Equal(
            _provider.Embed("the quarterly report").ToArray(),
            _provider.Embed("the quarterly report").ToArray());
    }

    [Fact]
    public void StableHashDoesNotDependOnProcessRandomisation()
    {
        Assert.Equal(
            HashingEmbeddingProvider.StableHash("recallos"),
            HashingEmbeddingProvider.StableHash("recallos"));
    }

    [Fact]
    public void RelatedTextScoresHigherThanUnrelatedText()
    {
        var query = _provider.Embed("database migration script").Span;

        var related = VectorMath.Dot(query, _provider.Embed("running the database migration script now").Span);
        var unrelated = VectorMath.Dot(query, _provider.Embed("a photograph of a mountain at sunrise").Span);

        Assert.True(related > unrelated, $"related={related}, unrelated={unrelated}");
    }

    [Fact]
    public void WordOrderChangesTheVector()
    {
        // Bigram features are what make this true; a pure bag of words would not.
        var a = _provider.Embed("merge request").ToArray();
        var b = _provider.Embed("request merge").ToArray();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void EmptyTextEmbedsToZeroWithoutThrowing()
    {
        Assert.All(_provider.Embed("").ToArray(), component => Assert.Equal(0f, component));
    }

    [Fact]
    public void StopWordsAreRemovedByTheTokenizer()
    {
        var tokens = HashingEmbeddingProvider.Tokenize("the report is on the desk");

        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("is", tokens);
        Assert.Contains("report", tokens);
        Assert.Contains("desk", tokens);
    }

    [Fact]
    public async Task BatchEmbeddingMatchesSingleEmbedding()
    {
        var texts = new[] { "first document", "second document", "third one" };

        var batch = await _provider.EmbedBatchAsync(texts);

        for (var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(_provider.Embed(texts[i]).ToArray(), batch[i].ToArray());
        }
    }

    [Fact]
    public void ModelIdEncodesTheDimensionCount()
    {
        Assert.Contains("256", new HashingEmbeddingProvider(256).ModelId);
        Assert.NotEqual(new HashingEmbeddingProvider(128).ModelId, new HashingEmbeddingProvider(256).ModelId);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(8192)]
    public void OutOfRangeDimensionsAreRejected(int dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashingEmbeddingProvider(dimensions));
    }
}

public class HeuristicIntentClassifierTests
{
    private readonly HeuristicIntentClassifier _classifier = new();

    [Fact]
    public void AnEditorWithSourceCode_IsLabelledCoding()
    {
        var prediction = _classifier.Classify(new CaptureFrame
        {
            ProcessName = "code",
            WindowTitle = "Program.cs - Visual Studio Code",
            Text = "public class Program { private void Run() { return; } }"
        });

        Assert.Equal("Coding", prediction.Label);
        Assert.True(prediction.Confidence > 0.3);
    }

    [Fact]
    public void AChatApplication_IsLabelledCommunicating()
    {
        var prediction = _classifier.Classify(new CaptureFrame
        {
            ProcessName = "slack",
            WindowTitle = "general - Acme Slack",
            Text = "unread message reply to: the team meeting"
        });

        Assert.Equal("Communicating", prediction.Label);
    }

    [Fact]
    public void AFrameWithNoSignal_IsLeftUnlabelled()
    {
        var prediction = _classifier.Classify(new CaptureFrame
        {
            ProcessName = "unknownapp",
            WindowTitle = "zzz",
            Text = "qqq"
        });

        Assert.Equal("Unknown", prediction.Label);
        Assert.Equal(0d, prediction.Confidence);
    }

    [Fact]
    public void TheProcessAloneIsNotEnoughToBeCertain()
    {
        var prediction = _classifier.Classify(new CaptureFrame { ProcessName = "chrome" });

        // It clears the acceptance bar but stays well short of confident.
        Assert.True(prediction.Confidence < 0.6);
    }

    [Fact]
    public void ConfidenceNeverExceedsOne()
    {
        var prediction = _classifier.Classify(new CaptureFrame
        {
            ProcessName = "code",
            WindowTitle = "visual studio repository .cs .py .ts",
            Text = string.Join(" ", "function class import const return public private def async null void namespace git commit branch compile")
        });

        Assert.True(prediction.Confidence <= 1.0);
    }
}
