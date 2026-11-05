using System.Buffers.Binary;
using System.Numerics;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>Result of <see cref="PageCodec.Verify"/>.</summary>
internal enum PageCheckStatus
{
    /// <summary>Every full page and the tail carry the expected check bytes.</summary>
    Valid,
    /// <summary>The check bytes of a full page do not match its payload.</summary>
    BadPage,
    /// <summary>The check bytes of the last, short page do not match its content.</summary>
    BadTail,
    /// <summary>The length of the last, short page is not one this codec writes, so it was not checked.</summary>
    UnsupportedTail,
}

/// <summary>Status and a page index: the first bad page (the index of the last page for a bad or unsupported tail), or for
/// <see cref="PageCheckStatus.Valid"/> the number of full pages.</summary>
internal readonly record struct PageCheck(PageCheckStatus Status, int Page);

/// <summary>
/// Writer of the page layer of the paged streams (Partitions, Formats/Latest, Global): the layout that
/// <see cref="PagedGzipStream"/> reads, with the check bytes of every page.
///
/// Every page holds 64,896 payload bytes and 353 check bytes (65,249 bytes in all). Read as a little-endian bit string, a page
/// is split into D interleaved lanes: bit k belongs to lane k mod D. Each lane is a Hamming code of 2,047 bits with the generator
/// y^11 + y^9 + 1, holding 2,036 data bits and 11 parity bits. A full page has D = 255 lanes: its 519,168 payload bits and 12 zero
/// bits make the 2,036 data bits of each lane, and the 2,805 parity bits come next, followed by 7 zero bits. This is the cyclic
/// code with generator x^2805 + x^2295 + 1 (the lane generator in y = x^255).
///
/// The last page of a stream may be shorter. This codec writes it in one form only, the form measured on the long last pages of
/// the streams of the sample files: D is the smallest odd number of lanes whose data bits hold the content and two zero bytes of
/// slack, the data bits not used by the content are written as zero (the files keep a few set bits at the end of that region, which
/// is not reproduced), and the parity bits follow the data bits directly. The stored page is ceil(D * 2,047 / 8) bytes long. Shorter last pages are stored differently (other lane codes), and a last page that holds exactly
/// 64,896 bytes does not follow this rule; neither is written (see <see cref="NaturalPadding"/>).
///
/// <see cref="PagedGzipStream"/> only looks at the first two bytes of the check bytes of a page and removes them; it does not read the
/// parity bits, so a flipped payload bit is found there only through the CRC of the gzip member. <see cref="Verify"/> checks the parity bits.
/// </summary>
internal static class PageCodec
{
    private const int LaneDataBits = 2_036;
    private const int ParityBitsPerLane = 11;
    private const int FullPageLanes = 255;
    private const int SlackBytes = 2;

    /// <summary>Shortest last page (content bytes) written. Below it the stored form differs (fewer lanes, some of them shortened).</summary>
    internal const int MinTailLength = 16_541;

    /// <summary>Longest last page (content bytes) written: a full page minus one byte.</summary>
    internal const int MaxTailLength = PagedGzipStream.PageDataLength - 1;

    /// <summary>Stored bytes of <paramref name="content"/> as a paged stream: full pages with their check bytes, then the last page.
    /// Throws <see cref="NotSupportedException"/> when the last page would be empty, shorter than <see cref="MinTailLength"/> or an
    /// exact page, which this codec does not write.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> content)
    {
        int tailLength = TailLength(content.Length);
        if (tailLength < MinTailLength || tailLength > MaxTailLength)
            throw new NotSupportedException(
                $"The last page would hold {tailLength} bytes; only {MinTailLength} to {MaxTailLength} bytes are written. " +
                "Append the zero bytes of NaturalPadding to the content first.");
        int pages = (content.Length - tailLength) / PagedGzipStream.PageDataLength;
        int lanes = TailLanes(tailLength);
        var stored = new byte[checked(pages * PagedGzipStream.PageStride + TailStoredLength(lanes))];
        for (int page = 0; page < pages; page++)
        {
            Span<byte> block = stored.AsSpan(page * PagedGzipStream.PageStride, PagedGzipStream.PageStride);
            content.Slice(page * PagedGzipStream.PageDataLength, PagedGzipStream.PageDataLength).CopyTo(block);
            Seal(block, FullPageLanes);
        }
        Span<byte> last = stored.AsSpan(pages * PagedGzipStream.PageStride);
        content[(pages * PagedGzipStream.PageDataLength)..].CopyTo(last);
        Seal(last, lanes);
        return stored;
    }

    /// <summary><see cref="Encode"/> of the content followed by <see cref="NaturalPadding"/> zero bytes. A reader that stops at the
    /// end of a gzip member ignores the padding.</summary>
    internal static byte[] EncodePadded(ReadOnlySpan<byte> content)
    {
        int padding = NaturalPadding(content.Length);
        if (padding == 0) return Encode(content);
        var padded = new byte[checked(content.Length + padding)];
        content.CopyTo(padded);
        return Encode(padded);
    }

    /// <summary>Number of zero bytes to append to content of this length so that <see cref="Encode"/> accepts it: 0 when the last
    /// page already holds <see cref="MinTailLength"/> to <see cref="MaxTailLength"/> bytes, otherwise enough to make it
    /// <see cref="MinTailLength"/> bytes.</summary>
    internal static int NaturalPadding(int contentLength)
    {
        int tail = TailLength(contentLength);
        return tail < MinTailLength ? MinTailLength - tail : tail > MaxTailLength ? MinTailLength : 0;
    }

    /// <summary>The 353 check bytes of one full page payload of 64,896 bytes.</summary>
    internal static byte[] FullPageTrailer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PagedGzipStream.PageDataLength)
            throw new ArgumentException($"A page holds {PagedGzipStream.PageDataLength} payload bytes.", nameof(payload));
        var block = new byte[PagedGzipStream.PageStride];
        payload.CopyTo(block);
        Seal(block, FullPageLanes);
        return block[PagedGzipStream.PageDataLength..];
    }

    /// <summary>Checks the check bytes of every full page and of the last page of a stored stream, without correcting anything.
    /// A stream that ends right after a full page is valid when that page is. The first bad page is reported.</summary>
    internal static PageCheck Verify(ReadOnlySpan<byte> stored)
    {
        int page = 0, at = 0;
        for (; stored.Length - at >= PagedGzipStream.PageStride; at += PagedGzipStream.PageStride, page++)
            if (!IsSealed(stored.Slice(at, PagedGzipStream.PageStride), FullPageLanes))
                return new PageCheck(PageCheckStatus.BadPage, page);
        int rest = stored.Length - at;
        if (rest == 0) return new PageCheck(PageCheckStatus.Valid, page);
        int lanes = LanesOfStoredTail(rest);
        if (lanes == 0) return new PageCheck(PageCheckStatus.UnsupportedTail, page);
        return new PageCheck(IsSealed(stored[at..], lanes) ? PageCheckStatus.Valid : PageCheckStatus.BadTail, page);
    }

    // Content bytes of the last page: the last 1 to 64,896 bytes (0 only for empty content).
    private static int TailLength(int contentLength) =>
        contentLength == 0 ? 0 : (contentLength - 1) % PagedGzipStream.PageDataLength + 1;

    // Smallest odd lane count whose data bits hold the content and the slack.
    private static int TailLanes(int tailLength) =>
        (int)((8L * (tailLength + SlackBytes) + LaneDataBits - 1) / LaneDataBits) | 1;

    private static int TailStoredLength(int lanes) => (int)(((long)lanes * (LaneDataBits + ParityBitsPerLane) + 7) >> 3);

    // The lane count whose stored last page is this long, or 0. Odd counts from the shortest written form up to a full page
    // (a stored page of 65,249 bytes is a full page and is checked as one).
    private static int LanesOfStoredTail(int length)
    {
        for (int lanes = TailLanes(MinTailLength); lanes < FullPageLanes; lanes += 2)
            if (TailStoredLength(lanes) == length) return lanes;
        return 0;
    }

    private static bool IsSealed(ReadOnlySpan<byte> block, int lanes)
    {
        byte[] copy = block.ToArray();
        Seal(copy, lanes);
        return copy.AsSpan().SequenceEqual(block);
    }

    /// <summary>Clears every bit of the block from the end of the data bits on, then writes the parity bits there. The data bits
    /// are the first lanes * 2,036 bits; the block holds lanes * 2,047 bits rounded up to whole bytes.</summary>
    private static void Seal(Span<byte> block, int lanes)
    {
        long dataBits = (long)lanes * LaneDataBits;
        int wholeBytes = (int)(dataBits >> 3), extraBits = (int)(dataBits & 7);
        if (extraBits != 0) block[wholeBytes++] &= (byte)((1 << extraBits) - 1);
        block[wholeBytes..].Clear();

        // Per lane the parity is (data(y) * y^11) mod (y^11 + y^9 + 1), by long division from the highest data bit down.
        // The remainder coefficients are kept as 11 planes, each holding that coefficient for all lanes (bit j = lane j);
        // plane i sits in slot (head + i) mod 11, so shifting the remainder up one place only moves head.
        int words = (lanes + 63) >> 6;
        Span<ulong> planes = stackalloc ulong[ParityBitsPerLane * words];
        planes.Clear();
        int head = 0;
        for (int step = 0; step < LaneDataBits + ParityBitsPerLane; step++)
        {
            int slot = (head + ParityBitsPerLane - 1) % ParityBitsPerLane; // the plane that leaves through y^11
            Span<ulong> carry = planes.Slice(slot * words, words);
            Span<ulong> nine = planes.Slice((slot + 9) % ParityBitsPerLane * words, words);
            for (int w = 0; w < words; w++) nine[w] ^= carry[w]; // y^11 = y^9 + 1
            if (step < LaneDataBits)
            {
                long offset = (long)lanes * (LaneDataBits - 1 - step); // bit k = j + lanes * t is lane j, position t
                for (int w = 0; w < words; w++) carry[w] ^= Read64(block, offset + 64L * w);
                int lastBits = lanes & 63;
                if (lastBits != 0) carry[words - 1] &= (1UL << lastBits) - 1;
            }
            head = slot;
        }

        for (int plane = 0; plane < ParityBitsPerLane; plane++)
        {
            int slot = (head + plane) % ParityBitsPerLane;
            for (int w = 0; w < words; w++)
            {
                ulong bits = planes[slot * words + w];
                while (bits != 0)
                {
                    long bit = dataBits + (long)lanes * plane + 64L * w + BitOperations.TrailingZeroCount(bits);
                    block[(int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
                    bits &= bits - 1;
                }
            }
        }
    }

    // The 64 bits that start at this bit offset (bit i of byte j has index 8 * j + i); bits past the end read as zero.
    private static ulong Read64(ReadOnlySpan<byte> bytes, long bitOffset)
    {
        int index = (int)(bitOffset >> 3), shift = (int)(bitOffset & 7);
        ulong low = Read64Bytes(bytes, index);
        if (shift == 0) return low;
        ulong next = index + 8 < bytes.Length ? bytes[index + 8] : 0UL;
        return (low >> shift) | (next << (64 - shift));
    }

    private static ulong Read64Bytes(ReadOnlySpan<byte> bytes, int index)
    {
        if (index + 8 <= bytes.Length) return BinaryPrimitives.ReadUInt64LittleEndian(bytes[index..]);
        ulong value = 0;
        for (int i = bytes.Length - 1; i >= index; i--) value = (value << 8) | bytes[i];
        return value;
    }
}
