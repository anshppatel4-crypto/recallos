using System.Buffers.Binary;

namespace RecallOS.Core.Storage;

/// <summary>
/// Converts embedding vectors to and from the BLOB form stored in SQLite.
/// </summary>
/// <remarks>
/// Little-endian IEEE-754 singles, packed with no header. The dimension count lives in its
/// own column, so the blob stays exactly 4 bytes per component -- at 256 dimensions that is
/// 1 KB per chunk, which is what keeps a semantic index over months of capture in the tens
/// of megabytes rather than the gigabytes a text-encoded form would cost.
/// </remarks>
internal static class VectorCodec
{
    public static byte[] ToBlob(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] FromBlob(ReadOnlySpan<byte> blob)
    {
        var count = blob.Length / sizeof(float);
        var vector = new float[count];
        for (var i = 0; i < count; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * sizeof(float))..]);
        }

        return vector;
    }
}
