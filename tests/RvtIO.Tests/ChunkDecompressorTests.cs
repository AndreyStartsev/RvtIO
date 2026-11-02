using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ChunkDecompressorTests
{
    [Fact]
    public void RvtMemberDiscovery_RejectsMarkerBytesInsideCompressedPayload()
    {
        byte[] raw =
        [
            0xAA,
            0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B,
            0x11, 0x22,
            0x1F, 0x8B, 0x08, 0xB0, 0xC6, 0x0F, 0xBD, 0x1D, 0x9B, 0xE0,
            0x33,
            0x1F, 0x8B, 0x08, 0x00, 0x30, 0x22, 0x9D, 0xC1, 0xB0, 0xDB,
            0x44,
            0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B,
        ];

        Assert.Equal([1, 35], ChunkDecompressor.FindMemberMarkers(raw));
    }

    private static readonly byte[] MemberHeader = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B];

    private static byte[] GzipMember(byte[] payload)
    {
        using var stream = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
            stream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(payload);
        byte[] member = stream.ToArray();
        member[8] = 0;
        member[9] = 0x0B; // the RVT member header
        return member;
    }

    /// <summary>[padding][first member][first <paramref name="head"/> header bytes of the second][353-byte page segment]
    /// [rest of the second member] with the segment at the first page boundary of the stream.</summary>
    private static (byte[] Raw, byte[] FirstPayload, byte[] SecondPayload) StreamWithSplitHeader(int head)
    {
        var random = new Random(7);
        byte[] firstPayload = new byte[3000], secondPayload = new byte[2000];
        random.NextBytes(firstPayload);
        random.NextBytes(secondPayload);
        byte[] first = GzipMember(firstPayload), second = GzipMember(secondPayload);
        int boundary = PagedGzipStream.PageDataLength;
        var raw = new byte[boundary + PagedGzipStream.SegmentLength + second.Length - head];
        Array.Fill(raw, (byte)0xEE);
        Array.Copy(first, 0, raw, boundary - head - first.Length, first.Length);
        Array.Copy(second, 0, raw, boundary - head, head);
        raw[boundary] = 0;
        raw[boundary + 1] = 0;
        Array.Copy(second, head, raw, boundary + PagedGzipStream.SegmentLength, second.Length - head);
        return (raw, firstPayload, secondPayload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(9)]
    public void MemberWhoseHeaderStraddlesAPageSegment_IsFoundAndDecoded(int head)
    {
        var (raw, firstPayload, secondPayload) = StreamWithSplitHeader(head);
        Assert.Single(ChunkDecompressor.FindMemberMarkers(raw));

        byte[] repaired = ChunkDecompressor.MoveSegmentsBeforeSplitHeaders(raw);

        Assert.NotSame(raw, repaired);
        Assert.Equal(raw.Length, repaired.Length);
        Assert.Equal(2, ChunkDecompressor.FindMemberMarkers(repaired).Count);
        Assert.True(repaired.AsSpan(PagedGzipStream.PageDataLength - head + PagedGzipStream.SegmentLength, MemberHeader.Length)
            .SequenceEqual(MemberHeader));
        var chunks = new ChunkDecompressor().DecompressAll(raw);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(firstPayload, chunks[0]);
        Assert.Equal(secondPayload, chunks[1]);
    }

    /// <summary>[padding][member with a 353-byte page segment inserted <paramref name="inside"/> bytes into it][32 bytes of
    /// metadata], the segment at the first page boundary of the stream.</summary>
    private static (byte[] Raw, byte[] Payload, int Start) StreamWithSegmentInsideMember(int inside, bool compressible, int tail = 32)
    {
        var random = new Random(11);
        byte[] payload = new byte[30000];
        if (compressible)
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + random.Next(0, 6) * (i % 7 == 0 ? 3 : 1));
        else
            random.NextBytes(payload);
        byte[] member = GzipMember(payload);
        int start = PagedGzipStream.PageDataLength - inside;
        var raw = new byte[start + member.Length + PagedGzipStream.SegmentLength + tail];
        Array.Fill(raw, (byte)0xEE);
        Array.Copy(member, 0, raw, start, inside);
        raw[PagedGzipStream.PageDataLength] = 0;
        raw[PagedGzipStream.PageDataLength + 1] = 0;
        Array.Copy(member, inside, raw, PagedGzipStream.PageDataLength + PagedGzipStream.SegmentLength, member.Length - inside);
        return (raw, payload, start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemberWithAPageSegmentInside_IsDecodedWithoutTheSegment(bool compressible)
    {
        var (raw, payload, start) = StreamWithSegmentInsideMember(1000, compressible);
        Assert.Equal([start], ChunkDecompressor.FindMemberMarkers(raw));

        Assert.True(ChunkDecompressor.TryDecodeWithoutPageSegments(raw, start, raw.Length, new byte[32768], 0, out byte[] output));
        Assert.Equal(payload, output);
        var chunks = new ChunkDecompressor().DecompressAll(raw);
        Assert.Single(chunks);
        Assert.Equal(payload, chunks[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemberWithAPageSegmentInside_IsDecodedWhenMoreThanTheMetadataFollowsTheTrailer(bool compressible)
    {
        // 220 bytes behind the gzip trailer instead of 32: the metadata and a content marker, as in members of a Revit 2025 partition
        var (raw, payload, start) = StreamWithSegmentInsideMember(1000, compressible, tail: 220);
        Assert.True(ChunkDecompressor.TryDecodeWithoutPageSegments(raw, start, raw.Length, new byte[32768], 0, out byte[] output));
        Assert.Equal(payload, output);
        var chunks = new ChunkDecompressor().DecompressAll(raw);
        Assert.Single(chunks);
        Assert.Equal(payload, chunks[0]);

        raw[start + 2000] ^= 0x01;   // still rejected when the data no longer matches the trailer
        Assert.False(ChunkDecompressor.TryDecodeWithoutPageSegments(raw, start, raw.Length, new byte[32768], 0, out _));
    }
    private static (byte[] Raw, byte[] FirstPayload, byte[] SecondPayload) StreamWithSegmentInsideAndSplitHeaderAfter(int head)
    {
        var random = new Random(13);
        int boundary1 = PagedGzipStream.PageDataLength, boundary2 = boundary1 + PagedGzipStream.PageStride;
        int start = boundary1 - 1000;
        byte[] firstPayload = [], first = [];
        for (int size = 64000; size < 66500; size++)
        {
            firstPayload = new byte[size];
            new Random(size).NextBytes(firstPayload);
            first = GzipMember(firstPayload);
            if (start + first.Length + PagedGzipStream.SegmentLength + 32 == boundary2 - head) break;
        }
        Assert.Equal(boundary2 - head, start + first.Length + PagedGzipStream.SegmentLength + 32);
        byte[] secondPayload = new byte[2000];
        random.NextBytes(secondPayload);
        byte[] second = GzipMember(secondPayload);
        var raw = new byte[boundary2 + PagedGzipStream.SegmentLength + second.Length - head];
        Array.Fill(raw, (byte)0xEE);
        Array.Copy(first, 0, raw, start, 1000);
        raw[boundary1] = 0;
        raw[boundary1 + 1] = 0;
        Array.Copy(first, 1000, raw, boundary1 + PagedGzipStream.SegmentLength, first.Length - 1000);
        Array.Fill(raw, (byte)0, boundary2 - head - 32, 32);
        Array.Copy(second, 0, raw, boundary2 - head, head);
        raw[boundary2] = 0;
        raw[boundary2 + 1] = 0;
        Array.Copy(second, head, raw, boundary2 + PagedGzipStream.SegmentLength, second.Length - head);
        return (raw, firstPayload, secondPayload);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(2)]
    public void MemberWithASegmentInsideAndTheRepairedSegmentOfAHeaderAtItsEnd_IsDecodedWithoutBoth(int head)
    {
        var (raw, firstPayload, secondPayload) = StreamWithSegmentInsideAndSplitHeaderAfter(head);

        var chunks = new ChunkDecompressor().DecompressAll(raw);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(firstPayload, chunks[0]);
        Assert.Equal(secondPayload, chunks[1]);
    }

    [Fact]
    public void MemberWithAPageSegmentInside_IsRejectedWhenTheTrailerOrTheSegmentTagDoesNotMatch()
    {
        var (raw, _, start) = StreamWithSegmentInsideMember(1000, compressible: false);
        raw[start + 2000] ^= 0x01; // a changed byte of the deflate data: size or CRC32 differs
        Assert.False(ChunkDecompressor.TryDecodeWithoutPageSegments(raw, start, raw.Length, new byte[32768], 0, out _));

        (raw, _, start) = StreamWithSegmentInsideMember(1000, compressible: false);
        raw[PagedGzipStream.PageDataLength] = 1; // not a segment
        Assert.False(ChunkDecompressor.TryDecodeWithoutPageSegments(raw, start, raw.Length, new byte[32768], 0, out _));
    }

    [Fact]
    public void MemberWithoutAPageSegment_IsLeftToTheOtherDecoders()
    {
        byte[] member = GzipMember([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        byte[] raw = member.Concat(new byte[32]).ToArray();
        Assert.False(ChunkDecompressor.TryDecodeWithoutPageSegments(raw, 0, raw.Length, new byte[32768], 0, out _));
        var chunks = new ChunkDecompressor().DecompressAll(raw);
        Assert.Single(chunks);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, chunks[0]);
    }

    [Fact]
    public void SplitHeaderRepair_LeavesAStreamWithoutASplitHeaderUntouched()
    {
        var (raw, _, _) = StreamWithSplitHeader(4);
        // the segment tag is missing: a zero byte followed by a byte with a zero low nibble
        raw[PagedGzipStream.PageDataLength] = 1;
        Assert.Same(raw, ChunkDecompressor.MoveSegmentsBeforeSplitHeaders(raw));

        // a short stream has no page boundary
        byte[] member = GzipMember([1, 2, 3, 4]);
        Assert.Same(member, ChunkDecompressor.MoveSegmentsBeforeSplitHeaders(member));
    }

    [Fact]
    public void SplitHeaderRepair_RequiresBothHeaderPartsToMatch()
    {
        var (raw, _, _) = StreamWithSplitHeader(4);
        raw[PagedGzipStream.PageDataLength + PagedGzipStream.SegmentLength + 3] ^= 0x01;
        Assert.Same(raw, ChunkDecompressor.MoveSegmentsBeforeSplitHeaders(raw));
    }

    [Fact]
    public void StandaloneGzipDiscovery_AcceptsAValidRfcHeader()
    {
        byte[] raw =
        [
            0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
        ];

        Assert.Equal([0], ChunkDecompressor.FindMemberMarkers(raw));
    }

    /// <summary>Gzip members that are damaged in a different way each, with the bytes a tolerant inflater returns: the ones decoded
    /// before the damage (a symbol that crosses the end of truncated data still decodes, from bits that read as ones).</summary>
    public static TheoryData<string, string, string> TolerantCases => new()
    {
        { "intact", "1F8B0800000000000003F348CDC9C907008289D1F705000000", "48656C6C6F" },
        { "bad-trailer", "1F8B0800000000000003F348CDC9C907000000000000000000", "48656C6C6F" },
        { "trailing-bytes", "1F8B0800000000000003F348CDC9C907008289D1F705000000AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "48656C6C6F" },
        { "no-trailer", "1F8B0800000000000003F348CDC9C90700", "48656C6C6F" },
        { "truncated-4", "1F8B08000000000000030BC94855", "54686527FFFF" },
        { "truncated-9", "1F8B08000000000000030BC94855282CCD4CCE", "54686520717569636FFFFF" },
        { "truncated-14", "1F8B08000000000000030BC94855282CCD4CCE56482ACA2F", "54686520717569636B2062726F77FFFF" },
        { "truncated-20", "1F8B08000000000000030BC94855282CCD4CCE56482ACA2FCF5348CBAF50", "54686520717569636B2062726F776E20666F7827FFFF" },
        { "truncated-30", "1F8B08000000000000030BC94855282CCD4CCE56482ACA2FCF5348CBAF50C82ACD2D2856C82F4B2D", "54686520717569636B2062726F776E20666F78206A756D7073206F766577FFFF" },
        { "backref-before-start", "1F8B08000000000000037304422006000000000000000000", "41" },
        { "distance-code-30", "1F8B08000000000000034B4C4A4E494D033E000000000000000000", "616263646566" },
        { "length-symbol-286", "1F8B0800000000000003731C03000000000000000000", "41414141" },
        { "stored-overrun", "1F8B0800000000000003010A00F5FF61626364", "61626364" },
        { "stored", "1F8B0800000000000003010400FBFF6162636411CD82ED04000000", "61626364" },
        { "stored-bad-nlen", "1F8B08000000000000038A00040300341278797A", "58" },
        { "dynamic-oversubscribed", "1F8B0800000000000003050092040000000000000000", "" },
        { "block-type-3", "1F8B080000000000000372021C00000000", "42" },
        { "bad-header-flags", "1F8B08E0000000000003F348CDC9C907008289D1F705000000", "" },
    };

    [Theory]
    [MemberData(nameof(TolerantCases))]
    public void DecompressTolerant_KeepsTheBytesDecodedBeforeTheDamage(string name, string gzipHex, string expectedHex)
    {
        byte[] gzip = Convert.FromHexString(gzipHex);

        byte[]? actual = ChunkDecompressor.DecompressTolerant(gzip, 0, gzip.Length);

        string actualHex = Convert.ToHexString(actual ?? []);
        Assert.True(expectedHex == actualHex, $"{name}: expected {expectedHex}, got {actualHex}");
    }

    [Fact]
    public void DecompressTolerant_ReturnsNullForAnInvalidGzipHeader()
    {
        byte[] notGzip = new byte[40];

        Assert.Null(ChunkDecompressor.DecompressTolerant(notGzip, 0, notGzip.Length));
    }

    [Fact]
    public void DecompressTolerant_DecodesAMemberInsideALargerBuffer()
    {
        byte[] payload = new byte[5000];
        new Random(3).NextBytes(payload.AsSpan(0, 2500));
        byte[] member = GzipMember(payload);
        byte[] buffer = [.. new byte[17], .. member, .. new byte[9]];

        Assert.Equal(payload, ChunkDecompressor.DecompressTolerant(buffer, 17, member.Length));
    }

    private static ExportReport ReportOf(ChunkDecompressor decompressor)
    {
        var (members, decoded, failed, recovered, unverified) = decompressor.LastStats;
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.Streams.Add(new StreamRecord("Partitions/0", 0, members, decoded, failed, recovered, unverified));
        return ExportReport.ForRead("a.rvt", 1, diagnostics, false, false, [1], new RvtIO.Model.RevitFileInfo());
    }

    [Fact]
    public void AMemberWithItsTrailerIsVerifiedAndTheReportIsComplete()
    {
        byte[] payload = new byte[2048];
        new Random(3).NextBytes(payload);
        var decompressor = new ChunkDecompressor();

        var chunks = decompressor.DecompressAll(GzipMember(payload));

        Assert.Equal(payload, Assert.Single(chunks));
        Assert.Equal((1, 1, 0, 0, 0), decompressor.LastStats);
        Assert.Equal("complete", ReportOf(decompressor).Status);
    }

    [Fact]
    public void AMemberWithItsLast100BytesCutIsUnverifiedAndTheReportIsPartial()
    {
        byte[] payload = new byte[2048];
        new Random(3).NextBytes(payload);
        byte[] member = GzipMember(payload);
        var decompressor = new ChunkDecompressor();

        var chunks = decompressor.DecompressAll(member[..^100]);

        Assert.True(Assert.Single(chunks).Length < payload.Length);
        Assert.Equal((1, 1, 0, 0, 1), decompressor.LastStats);
        Assert.Equal("partial", ReportOf(decompressor).Status);
    }

    [Theory]
    [InlineData("recovered-loss")]
    [InlineData("recovered-corrupt")]
    [InlineData("window-corrupt")]
    [InlineData("gzip-unverified")]
    [InlineData("legacy-unverified")]
    public void AMemberWithoutAConfirmedChecksumIsUnverified(string status) => Assert.True(ChunkDecompressor.IsUnverified(status));

    [Theory]
    [InlineData("gzip")]
    [InlineData("legacy")]
    [InlineData("window-deflate")]
    [InlineData("paged-exact")]
    [InlineData("stripped-exact")]
    [InlineData("failed")]
    [InlineData(null)]
    public void AMemberWithAConfirmedChecksumOrNoDataIsNotCountedUnverified(string? status) => Assert.False(ChunkDecompressor.IsUnverified(status));

    [Fact]
    public void ARecoveredStreamWithALostRangeIsNotCompleteBecauseItsChecksumWasNeverConfirmed()
    {
        // one intact member and one rebuilt after a lost range (zero-filled)
        string?[] statuses = ["gzip", "recovered-loss"];
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.Streams.Add(new StreamRecord("Partitions/0", 0, 2, 2, 0, 1, statuses.Count(ChunkDecompressor.IsUnverified)));

        var report = ExportReport.ForRead("a.rvt", 1, diagnostics, false, false, [1], new RvtIO.Model.RevitFileInfo());

        Assert.Equal("partial", report.Status);
        Assert.Equal(1, report.Streams[0].Recovered);
        Assert.Equal(1, report.Streams[0].Unverified);
    }
}
