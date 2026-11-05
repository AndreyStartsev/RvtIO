using System.Buffers.Binary;
using System.IO.Compression;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public class GzipMemberTests
{
    private static readonly byte[] Header = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B];

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Compressible but not trivial: runs of a few hundred bytes drawn from a small alphabet.
    private static byte[] Compressible(int length, int seed)
    {
        var random = new Random(seed);
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)('a' + (i / 300 + random.Next(3)) % 7);
        return data;
    }

    // CRC-32 (reflected, polynomial 0xEDB88320) bit by bit, independent of the checksum class the writer uses.
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] Inflate(byte[] member)
    {
        using var input = new MemoryStream(member);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static IEnumerable<object[]> Inputs()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 0x42 }];
        yield return [new byte[100_000]];
        yield return [Random(100_000, 1)];
        yield return [Compressible(1_000_000, 2)];
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Write_MakesAStandardMemberWithTheFixedHeader(byte[] data)
    {
        byte[] member = GzipMember.Write(data);

        Assert.Equal(Header, member[..GzipMember.HeaderLength]);
        Assert.Equal(Crc32(data), BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(member.Length - 8)));
        Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(member.Length - 4)));
        Assert.Equal(data, Inflate(member));
    }

    [Fact]
    public void Write_Compresses()
    {
        byte[] data = Compressible(1_000_000, 3);
        Assert.True(GzipMember.Write(data).Length < data.Length / 2);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ReaderDecodesTheMemberInAPagedStream_WithAndWithoutThePrefixOfAGlobalStream(byte[] data)
    {
        byte[] member = GzipMember.Write(data);
        byte[] prefix = [5, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(data, PagedGzipStream.Decompress(PageCodec.EncodePadded(member)));
        Assert.Equal(data, PagedGzipStream.Decompress(PageCodec.EncodePadded([.. prefix, .. member])));
    }

    [Fact]
    public void ReaderDecodesAMemberThatEndsExactlyOnAPageBoundary_AfterPadding()
    {
        // A member plus zero bytes of exactly two pages is a last page the codec does not write; the padding makes it one.
        byte[] data = Random(100_000, 4);
        byte[] member = GzipMember.Write(data);
        Assert.True(member.Length < 2 * PagedGzipStream.PageDataLength);
        byte[] content = [.. member, .. new byte[2 * PagedGzipStream.PageDataLength - member.Length]];

        Assert.Throws<NotSupportedException>(() => PageCodec.Encode(content));
        Assert.Equal(data, PagedGzipStream.Decompress(PageCodec.EncodePadded(content)));
    }

    [Fact]
    public void ReaderDecodesAMemberOfManyPages()
    {
        byte[] data = Random(1_000_000, 5);
        byte[] stored = PageCodec.EncodePadded(GzipMember.Write(data));

        Assert.True(stored.Length > 15 * PagedGzipStream.PageStride);
        Assert.Equal(new PageCheck(PageCheckStatus.Valid, stored.Length / PagedGzipStream.PageStride), PageCodec.Verify(stored));
        Assert.Equal(data, PagedGzipStream.Decompress(stored));
    }

    // What the reader does with a damaged stored stream today, recorded so that the codec tests and the reader stay in step: it
    // detects a payload bit through the CRC of the member and a page segment that does not start with the expected zero bits,
    // and it does not read the parity bits.
    [Fact]
    public void Reader_NeverReturnsWrongData_ForAFlippedPayloadBit_AndNoticesMostOfThem()
    {
        byte[] data = Random(150_000, 6);
        byte[] stored = PageCodec.EncodePadded(GzipMember.Write(data));
        int member = GzipMember.Write(data).Length;
        var random = new Random(7);
        int rejected = 0;
        const int trials = 200;
        for (int i = 0; i < trials; i++)
        {
            long bit = random.NextInt64(8L * GzipMember.HeaderLength + 8, 8L * (member - 8)); // inside the deflate data
            int index = (int)(bit >> 3);
            index += PagedGzipStream.SegmentLength * (index / PagedGzipStream.PageDataLength); // skip the segments
            byte[] damaged = (byte[])stored.Clone();
            damaged[index] ^= (byte)(1 << (int)(bit & 7));
            byte[]? decoded = PagedGzipStream.Decompress(damaged);
            Assert.True(decoded is null || decoded.AsSpan().SequenceEqual(data), "the reader returned data that is not the original");
            if (decoded is null) rejected++;
            Assert.NotEqual(PageCheckStatus.Valid, PageCodec.Verify(damaged).Status);
        }
        Assert.True(rejected >= trials * 9 / 10, $"only {rejected} of {trials} flipped bits were rejected by the reader");
    }

    [Fact]
    public void Reader_RejectsAFirstSegmentWithoutTheZeroBits_ButIgnoresItsParityBits()
    {
        byte[] stored = PageCodec.EncodePadded(GzipMember.Write(Random(150_000, 8)));
        byte[] data = PagedGzipStream.Decompress(stored)!;
        int segment = PagedGzipStream.PageDataLength;

        byte[] tag0 = (byte[])stored.Clone();
        tag0[segment] ^= 0x01;
        Assert.Null(PagedGzipStream.Decompress(tag0));
        Assert.Equal(PageCheckStatus.BadPage, PageCodec.Verify(tag0).Status);

        byte[] tag1 = (byte[])stored.Clone();
        tag1[segment + 1] ^= 0x08;
        Assert.Null(PagedGzipStream.Decompress(tag1));
        Assert.Equal(PageCheckStatus.BadPage, PageCodec.Verify(tag1).Status);

        byte[] parity = (byte[])stored.Clone();
        parity[segment + 100] ^= 0x04;
        Assert.Equal(data, PagedGzipStream.Decompress(parity));
        Assert.Equal(new PageCheck(PageCheckStatus.BadPage, 0), PageCodec.Verify(parity));

        byte[] cut = stored[..^1];
        Assert.Equal(PageCheckStatus.UnsupportedTail, PageCodec.Verify(cut).Status);
    }
}
