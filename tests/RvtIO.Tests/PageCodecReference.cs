using System.Numerics;
using RvtIO.Parsing;

namespace RvtIO.Tests;

/// <summary>Independent construction of the page layer for the tests: polynomial arithmetic over GF(2) on big integers, written
/// from the description of the code (generator x^(11 D) + x^(9 D) + 1 in the bit string of D interleaved lanes) and sharing nothing
/// with the lane and bit-plane implementation of the codec.</summary>
internal static class PageCodecReference
{
    /// <summary>The bytes that follow <paramref name="data"/> in a page of <paramref name="lanes"/> lanes: zero bits up to 2,036 data
    /// bits per lane, then the 11 parity bits per lane, then zero bits up to a whole byte.</summary>
    internal static byte[] Trailer(byte[] data, int lanes)
    {
        int degree = 11 * lanes;
        BigInteger value = new BigInteger(data, isUnsigned: true) << degree;
        BigInteger mask = (BigInteger.One << degree) - 1;
        while (value.GetBitLength() > degree)
        {
            BigInteger high = value >> degree;
            value = (value & mask) ^ high ^ (high << (9 * lanes));
        }
        int gap = lanes * 2036 - 8 * data.Length;
        byte[] bits = (value << gap).ToByteArray(isUnsigned: true);
        var trailer = new byte[(gap + degree + 7) / 8];
        Array.Copy(bits, trailer, Math.Min(bits.Length, trailer.Length));
        return trailer;
    }

    /// <summary>The stored stream of <paramref name="content"/> (last page 16,541 to 64,895 bytes): full pages, then the last page
    /// with the smallest odd lane count that holds the content and two bytes of slack.</summary>
    internal static byte[] Encode(byte[] content)
    {
        var stored = new List<byte>();
        int at = 0;
        while (content.Length - at > PagedGzipStream.PageDataLength)
        {
            byte[] page = content[at..(at + PagedGzipStream.PageDataLength)];
            stored.AddRange(page);
            stored.AddRange(Trailer(page, 255));
            at += PagedGzipStream.PageDataLength;
        }
        byte[] tail = content[at..];
        int lanes = (8 * (tail.Length + 2) + 2035) / 2036;
        if (lanes % 2 == 0) lanes++;
        stored.AddRange(tail);
        stored.AddRange(Trailer(tail, lanes));
        return [.. stored];
    }
}
