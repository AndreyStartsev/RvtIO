using System.IO.Compression;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The managed inflater behind the raw-deflate path of <see cref="ChunkDecompressor"/> has to decode every
/// ordinary deflate stream completely, whatever size or level produced it.</summary>
public class InflaterManagedTests
{
    private static readonly byte[] NoDictionary = new byte[32768];

    /// <summary>Compressible but not trivial: words from a small vocabulary and short random runs.</summary>
    private static byte[] Sample(int size, int seed = 5)
    {
        var random = new Random(seed);
        string[] words = ["wall", "floor", "level", "door", "0.0000", "1.5708", "name", "type", "id", "ref"];
        var data = new byte[size];
        int pos = 0;
        while (pos < size)
        {
            byte[] word = System.Text.Encoding.ASCII.GetBytes(words[random.Next(words.Length)] + (random.Next(4) == 0 ? random.Next(1000).ToString() : ""));
            for (int i = 0; i < word.Length && pos < size; i++) data[pos++] = word[i];
            if (random.Next(8) == 0) data[Math.Min(pos, size - 1)] = (byte)random.Next(256);
        }
        return data;
    }

    private static byte[] DotNetDeflate(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, level, leaveOpen: true)) d.Write(data);
        return ms.ToArray();
    }


    public static TheoryData<int, string> Cases()
    {
        var data = new TheoryData<int, string>();
        foreach (int size in new[] { 1, 1000, 65535, 65536, 65537, 131071, 131072, 131073, 300000, 700000 })
            foreach (string level in new[] { "Fastest", "Optimal", "SmallestSize", "NoCompression" })
                data.Add(size, level);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DotNetDeflateStreams_AreDecodedCompletely(int size, string level)
    {
        byte[] data = Sample(size);
        byte[] deflate = DotNetDeflate(data, Enum.Parse<CompressionLevel>(level));
        Assert.Equal(data, ChunkDecompressor.InflateRaw(deflate, NoDictionary, 0));
    }

    [Theory]
    [InlineData(70000)]
    [InlineData(131072)]
    [InlineData(400000)]
    public void StreamsDecodedWithALoadedDictionary_AreDecodedCompletely(int size)
    {
        byte[] data = Sample(size);
        byte[] history = Sample(40000, seed: 9);
        Assert.Equal(data, ChunkDecompressor.InflateRaw(DotNetDeflate(data, CompressionLevel.Optimal), history, 32768));
    }

#if NET9_0_OR_GREATER
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void ZlibLevels_AreDecodedCompletely(int level)
    {
        foreach (int size in new[] { 65535, 65536, 65537, 131072, 131073, 500000 })
        {
            byte[] data = Sample(size, seed: level);
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true)) z.Write(data);
            Assert.Equal(data, ChunkDecompressor.InflateRaw(ms.ToArray(), NoDictionary, 0));
        }
    }
#endif

    [Theory]
    [InlineData(100, false)]
    [InlineData(65535, false)]
    [InlineData(65536, false)]
    [InlineData(65537, false)]
    [InlineData(131072, false)]
    [InlineData(300000, false)]
    [InlineData(65537, true)]
    [InlineData(300000, true)]
    public void FixedHuffmanBlocks_AreDecodedCompletely(int size, bool runs)
    {
        byte[] data = new byte[size];
        var random = new Random(3);
        for (int i = 0; i < size; i++) data[i] = runs ? (byte)(i / 1000 % 256) : (byte)random.Next(256);
        Assert.Equal(data, ChunkDecompressor.InflateRaw(FixedHuffman(data, runs), NoDictionary, 0));
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(200000)]
    public void StoredBlocks_AreDecodedCompletely(int size)
    {
        byte[] data = new byte[size];
        new Random(4).NextBytes(data);
        var ms = new MemoryStream();
        for (int pos = 0; pos < size || pos == 0;)
        {
            int n = Math.Min(65535, size - pos);
            bool last = pos + n >= size;
            ms.WriteByte((byte)(last ? 1 : 0));
            ms.Write([(byte)n, (byte)(n >> 8), (byte)~n, (byte)(~n >> 8)]);
            ms.Write(data, pos, n);
            pos += n;
            if (last) break;
        }
        Assert.Equal(data, ChunkDecompressor.InflateRaw(ms.ToArray(), NoDictionary, 0));
    }

    [Theory]
    [InlineData(70000)]
    [InlineData(131072)]
    [InlineData(300000)]
    public void CutStreams_AreNotAccepted(int size)
    {
        byte[] deflate = DotNetDeflate(Sample(size), CompressionLevel.Optimal);
        foreach (int cut in new[] { 1, 2, 10, deflate.Length / 3, deflate.Length / 2, deflate.Length - 70000 / 70 })
            Assert.Null(ChunkDecompressor.InflateRaw(deflate[..^cut], NoDictionary, 0));
        Assert.Null(ChunkDecompressor.InflateRaw([], NoDictionary, 0));
    }

    [Theory]
    [InlineData(70000)]
    [InlineData(131072)]
    [InlineData(300000)]
    public void BytesBehindTheFinalBlock_AreNotPartOfTheOutput(int size)
    {
        byte[] data = Sample(size);
        byte[] deflate = [.. DotNetDeflate(data, CompressionLevel.Optimal), .. new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }];
        Assert.Equal(data, ChunkDecompressor.InflateRaw(deflate, NoDictionary, 0));
    }

    /// <summary>One final block with the fixed Huffman codes of RFC 1951; with <paramref name="runs"/> a repeated byte becomes a
    /// distance-1 match of length 258.</summary>
    private static byte[] FixedHuffman(byte[] data, bool runs)
    {
        var bytes = new List<byte>();
        int bit = 0, acc = 0;
        void Bits(int value, int count)   // least significant bit first
        {
            for (int i = 0; i < count; i++)
            {
                acc |= ((value >> i) & 1) << bit;
                if (++bit == 8) { bytes.Add((byte)acc); acc = 0; bit = 0; }
            }
        }
        void Code(int code, int length)   // Huffman codes go most significant bit first
        {
            for (int i = length - 1; i >= 0; i--) Bits((code >> i) & 1, 1);
        }
        void Literal(int b)
        {
            if (b < 144) Code(0x30 + b, 8); else Code(0x190 + b - 144, 9);
        }
        Bits(1, 1);
        Bits(1, 2);
        for (int i = 0; i < data.Length; i++)
        {
            int run = 1;
            while (runs && i + run < data.Length && run < 259 && data[i + run] == data[i]) run++;
            Literal(data[i]);
            if (run - 1 >= 3)
            {
                int length = run - 1;
                if (length > 258) length = 258;
                // length 258 is code 285; any other length is written as the longest codes below it (3..10 have no extra bits)
                if (length == 258) Code(0xC0 + 5, 8);
                else { length = Math.Min(length, 10); Code(length - 3 + 1, 7); }   // codes 257..264 are 7-bit 0000001..0001000
                Code(0, 5);    // distance 1
                i += length;
            }
        }
        Code(0, 7);            // end of block
        if (bit > 0) bytes.Add((byte)acc);
        return bytes.ToArray();
    }
}
