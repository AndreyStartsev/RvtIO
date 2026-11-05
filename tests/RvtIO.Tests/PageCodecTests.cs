using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public class PageCodecTests
{
    private const int PageData = PagedGzipStream.PageDataLength;
    private const int PageStride = PagedGzipStream.PageStride;

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(i * 7 + 3);
        return data;
    }

    private static byte[] Hex(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));

    // Trailer of the payload (byte i = i * 7 + 3 modulo 256) of one full page, from an independent implementation in another language.
    private static readonly byte[] PatternPageTrailer = Hex("""
        004027057ffee2387b9c9bb36cb31500b2feadd6e5c1682eff7992b865c48a5736557fe7d1a08dd0807c2a9abc2330d9
        88eddd506409f01fc95104572887357456096f288b97db1653a0eb3e4b5d782f5f3f5eae86bfc578e1c31cb7d3fc5a27
        ffbd09c6ad9e7aa42cdfa9f942ccb6a6a46eea48916ff4b6a68277af874117bd4b284f6d17968a5013f4f3db04db7d68
        da96c5be8da900469711fad00dace23f5e3d178fb9c8e5b8e81442f2d44b58b1e085b5380c619877a1396c3f40ef5d1c
        3e610740e3ffb37e3bc8835623351047375736c6eed7ad1089ab74dfbb94324f97d561aec5f612cc44b7c1912aa4dece
        cc068220f9079cdeceea1fc7ef297fd5234027057ffee2387b9c9bb36cb31500b2feadd6e5c1682eff7992b865c40a16
        538e31977d87cbb0afd59b478a5bc6f98abbb2a58f33f5f56c047846ed68b0de498e207f9897791bbce17ebb85a7f282
        4421826b7939c899404da73087fc9efd01
        """);

    // Bytes that follow the first 20,000 bytes of the same pattern in a last page (79 lanes), same origin.
    private static readonly byte[] PatternTailTrailer = Hex("""
        000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
        000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000
        000000000000000000b06b4d32a5b4beb67c39d0bec205b20b277a7647839ddaae6015de621637f136e69e49f369985b
        666d919c381f6e25811dbfbaea6796f1565a1eee7931f90be61faa782173e749dc5d47cbd187e8f890978556bfd2c996
        3776c7423049af46971b2f0673dde3f83db1bbe3183d00
        """);

    private static byte[] WithBits(int length, params int[] bits)
    {
        var data = new byte[length];
        foreach (int bit in bits) data[bit >> 3] |= (byte)(1 << (bit & 7));
        return data;
    }

    [Fact]
    public void FullPageTrailer_OfZeroPayload_IsZero()
    {
        byte[] trailer = PageCodec.FullPageTrailer(new byte[PageData]);
        Assert.Equal(new byte[PagedGzipStream.SegmentLength], trailer);
    }

    [Theory]
    [InlineData(0, new[] { 12, 2307 })]
    [InlineData(255, new[] { 267, 2562 })]
    [InlineData(2805, new[] { 12, 267, 777, 1287, 1797, 2562 })]
    public void FullPageTrailer_OfOneBit_IsTheHandDerivedRemainder(int payloadBit, int[] trailerBits)
    {
        // x^2805 = x^2295 + 1 modulo the generator x^2805 + x^2295 + 1, so bit 0 leaves the powers 0 and 2295, shifted by the
        // 12 zero bits in front of the parity; the other rows follow by multiplying with x^255 and x^2805 and reducing again.
        byte[] trailer = PageCodec.FullPageTrailer(WithBits(PageData, payloadBit));
        Assert.Equal(WithBits(PagedGzipStream.SegmentLength, trailerBits), trailer);
    }

    [Fact]
    public void FullPageTrailer_OfPatternPayload_IsTheKnownAnswer()
    {
        Assert.Equal(PatternPageTrailer, PageCodec.FullPageTrailer(Pattern(PageData)));
        Assert.Equal(PatternPageTrailer, PageCodecReference.Trailer(Pattern(PageData), 255));
    }

    [Fact]
    public void LastPage_OfPatternContent_IsTheKnownAnswer()
    {
        byte[] content = Pattern(20_000);
        byte[] stored = PageCodec.Encode(content);
        Assert.Equal(content.Length + PatternTailTrailer.Length, stored.Length);
        Assert.Equal(content, stored[..content.Length]);
        Assert.Equal(PatternTailTrailer, stored[content.Length..]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FullPageTrailer_MatchesTheReference_ForRandomPayloads(int seed)
    {
        byte[] payload = Random(PageData, seed);
        Assert.Equal(PageCodecReference.Trailer(payload, 255), PageCodec.FullPageTrailer(payload));
    }

    [Fact]
    public void FullPageTrailer_MatchesTheReference_ForExtremePayloads()
    {
        foreach (byte fill in new byte[] { 0x00, 0x01, 0x80, 0xAA, 0xFF })
        {
            byte[] payload = new byte[PageData];
            Array.Fill(payload, fill);
            Assert.Equal(PageCodecReference.Trailer(payload, 255), PageCodec.FullPageTrailer(payload));
        }
    }

    [Fact]
    public void FullPageTrailer_RejectsAPayloadOfAnotherLength()
    {
        Assert.Throws<ArgumentException>(() => PageCodec.FullPageTrailer(new byte[PageData - 1]));
        Assert.Throws<ArgumentException>(() => PageCodec.FullPageTrailer(new byte[PageData + 1]));
    }

    [Fact]
    public void Trailer_StartsWithTheZeroBitsThatTheReaderChecks()
    {
        // PagedGzipStream accepts a page segment only when byte 0 is zero and the low nibble of byte 1 is zero.
        foreach (int seed in new[] { 4, 5, 6, 7 })
        {
            byte[] trailer = PageCodec.FullPageTrailer(Random(PageData, seed));
            Assert.Equal(0, trailer[0]);
            Assert.Equal(0, trailer[1] & 0x0F);
            Assert.True(trailer[^1] <= 1);
        }
    }

    // Last page lengths and what the rule gives for them, worked out by hand: D is the smallest odd number with D * 2036 >= 8 * (length + 2)
    // and the stored length is ceil(D * 2047 / 8) (for example 67 * 2047 = 137,149 bits = 17,143.6 bytes).
    [Theory]
    [InlineData(16_541, 67, 17_144)]
    [InlineData(17_049, 67, 17_144)]
    [InlineData(17_050, 69, 17_656)]
    [InlineData(20_000, 79, 20_215)]
    [InlineData(30_000, 119, 30_450)]
    [InlineData(64_386, 253, 64_737)]
    [InlineData(64_387, 255, 65_249)]
    [InlineData(64_895, 255, 65_249)]
    public void LastPage_FollowsTheLaneRule(int length, int lanes, int storedLength)
    {
        byte[] content = Random(length, length);
        byte[] stored = PageCodec.Encode(content);
        Assert.Equal(storedLength, stored.Length);
        Assert.Equal(content, stored[..length]);
        Assert.Equal(PageCodecReference.Trailer(content, lanes), stored[length..]);
        Assert.Equal(new PageCheck(PageCheckStatus.Valid, stored.Length / PageStride), PageCodec.Verify(stored));

        // After one full page the same last page follows the full page.
        byte[] longer = [.. Random(PageData, 9), .. content];
        byte[] storedLonger = PageCodec.Encode(longer);
        Assert.Equal(PageStride + storedLength, storedLonger.Length);
        Assert.Equal(stored, storedLonger[PageStride..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16_540)]
    [InlineData(PageData)]
    [InlineData(PageData + 1)]
    [InlineData(PageData + 16_540)]
    [InlineData(2 * PageData)]
    [InlineData(3 * PageData)]
    public void Encode_RefusesALastPageItDoesNotWrite(int length)
    {
        Assert.Throws<NotSupportedException>(() => PageCodec.Encode(new byte[length]));
    }

    [Theory]
    [InlineData(0, 16_541)]
    [InlineData(1, 16_540)]
    [InlineData(16_540, 1)]
    [InlineData(16_541, 0)]
    [InlineData(PageData - 1, 0)]
    [InlineData(PageData, 16_541)]
    [InlineData(PageData + 1, 16_540)]
    [InlineData(PageData + 16_541, 0)]
    [InlineData(2 * PageData, 16_541)]
    [InlineData(3 * PageData - 1, 0)]
    public void NaturalPadding_GivesTheBytesThatMakeALastPageThatIsWritten(int length, int padding)
    {
        Assert.Equal(padding, PageCodec.NaturalPadding(length));
        Assert.NotNull(PageCodec.Encode(new byte[length + padding]));
    }

    public static IEnumerable<object[]> Sizes()
    {
        // The sizes around every boundary of the layout, a few pages, and random ones.
        var sizes = new SortedSet<int>
        {
            0, 1, 2, 3, 255, 4_096, 16_539, 16_540, 16_541, 16_542, PageData - 2, PageData - 1, PageData, PageData + 1,
            PageData + 16_540, PageData + 16_541, 2 * PageData - 1, 2 * PageData, 2 * PageData + 1, 3 * PageData + 17_000,
        };
        var random = new Random(2024);
        for (int i = 0; i < 12; i++) sizes.Add(random.Next(0, 4 * PageData));
        return sizes.Select(size => new object[] { size });
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EncodePadded_IsTheReferenceStream_AndTheReaderRemovesExactlyItsSegments(int length)
    {
        byte[] content = Random(length, length + 1);
        byte[] stored = PageCodec.EncodePadded(content);
        byte[] padded = [.. content, .. new byte[PageCodec.NaturalPadding(length)]];

        Assert.Equal(PageCodecReference.Encode(padded), stored);
        Assert.Equal(new PageCheck(PageCheckStatus.Valid, stored.Length / PageStride), PageCodec.Verify(stored));

        byte[]? body = PagedGzipStream.RemoveSegments(stored);
        Assert.NotNull(body);
        Assert.True(body.Length >= padded.Length);
        Assert.Equal(padded, body[..padded.Length]);
    }

    [Fact]
    public void Verify_ChecksAPageThatEndsTheStreamLikeAnyFullPage()
    {
        byte[] payload = Random(PageData, 21);
        byte[] stored = [.. payload, .. PageCodec.FullPageTrailer(payload)];
        Assert.Equal(new PageCheck(PageCheckStatus.Valid, 1), PageCodec.Verify(stored));

        stored[^3] ^= 0x10;
        Assert.Equal(new PageCheck(PageCheckStatus.BadPage, 0), PageCodec.Verify(stored));
        Assert.Equal(new PageCheck(PageCheckStatus.Valid, 0), PageCodec.Verify([]));
    }

    [Fact]
    public void Verify_DetectsEveryFlippedBitOfASample()
    {
        byte[] stored = PageCodec.Encode(Random(2 * PageData + 20_000, 31));
        Assert.Equal(2 * PageStride + 20_215, stored.Length);
        Assert.Equal(new PageCheck(PageCheckStatus.Valid, 2), PageCodec.Verify(stored));

        // Named positions: payload, zero bits, parity and padding bits of a full page, then the same in the last page.
        var positions = new List<(long Bit, PageCheckStatus Status, int Page)>
        {
            (0, PageCheckStatus.BadPage, 0),
            (8L * (PageData - 1) + 7, PageCheckStatus.BadPage, 0),
            (8L * PageData + 3, PageCheckStatus.BadPage, 0),
            (8L * PageData + 12, PageCheckStatus.BadPage, 0),
            (8L * (PageData + 352) + 1, PageCheckStatus.BadPage, 0),
            (8L * (PageStride + 100) + 5, PageCheckStatus.BadPage, 1),
            (8L * 2 * PageStride + 8, PageCheckStatus.BadTail, 2),
            (8L * (2 * PageStride + 19_999) + 7, PageCheckStatus.BadTail, 2),
            (8L * (2 * PageStride + 20_100) + 2, PageCheckStatus.BadTail, 2),
            (8L * stored.Length - 1, PageCheckStatus.BadTail, 2),
        };
        var random = new Random(5);
        for (int i = 0; i < 150; i++)
        {
            long bit = random.NextInt64(8L * stored.Length);
            int page = Math.Min((int)(bit / 8 / PageStride), 2);
            positions.Add((bit, page == 2 ? PageCheckStatus.BadTail : PageCheckStatus.BadPage, page));
        }

        foreach (var (bit, status, page) in positions)
        {
            byte[] damaged = (byte[])stored.Clone();
            damaged[bit >> 3] ^= (byte)(1 << (int)(bit & 7));
            Assert.Equal(new PageCheck(status, page), PageCodec.Verify(damaged));
        }
    }

    [Fact]
    public void Verify_DoesNotGuess_ForALastPageOfAnotherLength()
    {
        byte[] stored = PageCodec.Encode(Random(PageData + 20_000, 41));
        Assert.Equal(PageCheckStatus.UnsupportedTail, PageCodec.Verify(stored[..^1]).Status);
        Assert.Equal(PageCheckStatus.UnsupportedTail, PageCodec.Verify([.. stored, 0]).Status);
        Assert.Equal(new PageCheck(PageCheckStatus.UnsupportedTail, 0), PageCodec.Verify(stored[..100]));
        Assert.Equal(new PageCheck(PageCheckStatus.UnsupportedTail, 0), PageCodec.Verify(stored[..(PageStride - 1)]));
    }

    [Fact]
    public void Verify_ReportsTheFirstBadPage()
    {
        byte[] stored = PageCodec.Encode(Random(3 * PageData + 20_000, 51));
        stored[PageStride + 10] ^= 0x01;
        stored[2 * PageStride + 10] ^= 0x01;
        Assert.Equal(new PageCheck(PageCheckStatus.BadPage, 1), PageCodec.Verify(stored));
    }
}
