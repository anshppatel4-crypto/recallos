using System.Numerics;

namespace RecallOS.Core.Intelligence;

/// <summary>Vector operations used by semantic search.</summary>
/// <remarks>
/// A semantic query scans every stored vector, so this is the hottest loop in the system.
/// The dot product is SIMD-widened, which is worth roughly a 4-8x speedup over the scalar
/// form and is what keeps a brute-force scan of a few hundred thousand chunks interactive.
/// </remarks>
public static class VectorMath
{
    /// <summary>Dot product. Both spans must be the same length.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector lengths differ: {a.Length} and {b.Length}.");
        }

        var width = Vector<float>.Count;
        var accumulator = Vector<float>.Zero;
        var i = 0;

        for (; i <= a.Length - width; i += width)
        {
            accumulator += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        }

        var sum = Vector.Dot(accumulator, Vector<float>.One);

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Cosine similarity, -1..1. Vectors produced by this assembly are already unit length,
    /// in which case this reduces to the dot product; the norms are still computed so the
    /// method is correct for vectors from any source.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var dot = Dot(a, b);
        var magnitude = MathF.Sqrt(Dot(a, a)) * MathF.Sqrt(Dot(b, b));
        return magnitude <= float.Epsilon ? 0f : dot / magnitude;
    }

    /// <summary>Scale a vector to unit length in place. A zero vector is left alone.</summary>
    public static void NormalizeInPlace(Span<float> vector)
    {
        var magnitude = MathF.Sqrt(Dot(vector, vector));
        if (magnitude <= float.Epsilon)
        {
            return;
        }

        var inverse = 1f / magnitude;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] *= inverse;
        }
    }
}
