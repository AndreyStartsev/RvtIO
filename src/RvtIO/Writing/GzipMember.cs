using System.Buffers.Binary;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Checksum;

namespace RvtIO.Writing;

/// <summary>
/// Writer of the single gzip member that the Formats/Latest and Global streams hold: the 10 header bytes of the files
/// (no name, no comment, no extra field, no time stamp, operating system byte 0x0B), the raw deflate data, then the CRC-32 and
/// the length of the data modulo 2^32. The deflate encoder is free; the bytes it produces can differ between runtimes, so a
/// caller must not depend on them.
/// </summary>
internal static class GzipMember
{
    /// <summary>Length of the member header.</summary>
    internal const int HeaderLength = 10;

    private static readonly byte[] Header = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B];

    // One final block with no data (fixed Huffman code, end of block only).
    private static readonly byte[] EmptyDeflate = [0x03, 0x00];

    /// <summary>The gzip member that inflates to <paramref name="data"/>.</summary>
    internal static byte[] Write(ReadOnlySpan<byte> data)
    {
        using var stream = new MemoryStream();
        stream.Write(Header);
        if (data.IsEmpty)
            stream.Write(EmptyDeflate); // DeflateStream writes nothing at all for empty input on some runtimes
        else
            using (var deflate = new DeflateStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
                deflate.Write(data);
        var crc = new Crc32();
        crc.Update(data.ToArray());
        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, (uint)crc.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], (uint)data.Length);
        stream.Write(trailer);
        return stream.ToArray();
    }
}
