using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Re-encodes single-member paged streams (<c>Formats/*</c> and <c>Global/*</c>): preserves the stream's header prefix (empty for
/// <c>Formats/*</c>, 8 bytes for <c>Global/*</c>), writes one gzip member (<see cref="GzipMember"/>), seals page parity trailers
/// (<see cref="PageCodec.EncodePadded"/>), and verifies both parity and round-trip decompression before returning.
/// </summary>
internal static class PagedStreamCodec
{
    /// <summary>Extracts the 0-byte or 8-byte header prefix that precedes the gzip member of <paramref name="storedStream"/>.</summary>
    /// <exception cref="InvalidDataException">The stored stream has corrupt page parity or no valid gzip member at offset 0 or 8.</exception>
    internal static byte[] ReadPrefix(byte[] storedStream)
    {
        var pageCheck = PageCodec.Verify(storedStream);
        if (pageCheck.Status is PageCheckStatus.BadPage or PageCheckStatus.BadTail)
            throw new InvalidDataException($"The stored paged stream fails page parity at page {pageCheck.Page} ({pageCheck.Status}).");

        byte[]? body = PagedGzipStream.RemoveSegments(storedStream)
            ?? PagedGzipStream.RemoveSegments(storedStream, tolerateTail: true);
        if (body is null || PagedGzipStream.Decompress(storedStream) is null)
            throw new InvalidDataException("The stored paged stream cannot be decompressed.");

        if (IsGzipMagic(body, 0)) return [];
        if (IsGzipMagic(body, PagedGzipStream.PrefixLength)) return body[..PagedGzipStream.PrefixLength];
        throw new InvalidDataException("The stored paged stream has no gzip header at offset 0 or 8.");
    }

    /// <summary>Re-encodes <paramref name="uncompressedPayload"/> with the header prefix of <paramref name="originalStoredStream"/>.</summary>
    internal static byte[] Reencode(byte[] originalStoredStream, ReadOnlySpan<byte> uncompressedPayload) =>
        Encode(ReadPrefix(originalStoredStream), uncompressedPayload);

    /// <summary>Encodes <paramref name="uncompressedPayload"/> after <paramref name="prefix"/> (0 or 8 bytes) as a verified paged stream.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> uncompressedPayload)
    {
        if (prefix.Length is not (0 or PagedGzipStream.PrefixLength))
            throw new ArgumentException($"A paged stream prefix must be 0 or {PagedGzipStream.PrefixLength} bytes.", nameof(prefix));

        byte[] member = GzipMember.Write(uncompressedPayload);
        byte[] content = new byte[checked(prefix.Length + member.Length)];
        prefix.CopyTo(content);
        member.CopyTo(content.AsSpan(prefix.Length));

        byte[] encoded = PageCodec.EncodePadded(content);
        Verify(encoded, prefix, uncompressedPayload);
        return encoded;
    }

    private static void Verify(byte[] encoded, ReadOnlySpan<byte> expectedPrefix, ReadOnlySpan<byte> expectedPayload)
    {
        var check = PageCodec.Verify(encoded);
        if (check.Status != PageCheckStatus.Valid)
            throw new InvalidDataException($"Encoded paged stream failed parity check ({check.Status} at page {check.Page}).");

        if (expectedPrefix.Length > 0 && !encoded.AsSpan(0, expectedPrefix.Length).SequenceEqual(expectedPrefix))
            throw new InvalidDataException("Encoded paged stream lost its header prefix.");

        byte[]? roundTrip = PagedGzipStream.Decompress(encoded);
        if (roundTrip is null || !roundTrip.AsSpan().SequenceEqual(expectedPayload))
            throw new InvalidDataException("Encoded paged stream did not round-trip to the expected payload.");
    }

    private static bool IsGzipMagic(byte[] data, int offset) =>
        data.Length >= offset + 18
        && data[offset] == 0x1F
        && data[offset + 1] == 0x8B
        && data[offset + 2] == 0x08;
}
