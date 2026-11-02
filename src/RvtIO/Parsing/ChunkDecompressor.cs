using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using RvtIO.DeflateManaged;

namespace RvtIO.Parsing;

internal class ChunkDecompressor
{
    // The serialized RVT page members use this invariant gzip header in every partition stream.
    // Matching the complete header keeps compressed payload bytes such as 1F 8B 08 xx from being treated as page boundaries.
    private static readonly byte[] RvtGzipHeader =
        [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B];

    /// <summary>Members of the stream read by the last <see cref="DecompressAll"/>: all, decoded, left without output, rescued by a
    /// structural relock, and kept although their gzip trailer did not match.</summary>
    public (int Members, int Decoded, int Failed, int Recovered, int Unverified) LastStats { get; private set; }

    /// <summary>
    /// Find all GZIP markers in raw partition data and decompress each chunk.
    ///
    /// RVT partition streams are a sequence of gzip members whose deflate streams
    /// SHARE the LZ77 window: a member may back-reference data produced by previous
    /// members. Self-contained members decode with plain GZipStream (CRC-validated).
    /// The rest are decoded with the managed inflater seeded with the last 32KB of
    /// the preceding members' output. (Lenient Deflate64 decoders produce output with sparse corruption where
    /// far back-references cannot be resolved.)
    ///
    /// Returns list of decompressed chunks.
    /// </summary>
    public List<byte[]> DecompressAll(byte[] raw)
    {
        raw = MoveSegmentsBeforeSplitHeaders(raw);
        var markers = FindMemberMarkers(raw);

        var results = new byte[]?[markers.Count];
        var statuses = new string?[markers.Count];
        int gzipOk = 0, pagedOk = 0, windowOk = 0, failed = 0, recovered = 0;

        // Pass 1 (parallel): self-contained members via GZipStream (validates CRC32). A member with a page segment
        // inside is left to pass 2: GZipStream does not fail on a truncated stream, so the garbage that follows the
        // segment can come back as a "successful" member.
        Parallel.For(0, markers.Count, mi =>
        {
            int st = markers[mi];
            int end = (mi + 1 < markers.Count) ? markers[mi + 1] : raw.Length;
            if (ContainsPageSegment(raw, st, end)) return;
            try
            {
                using var ms = new MemoryStream();
                using var gs = new GZipStream(new MemoryStream(raw, st, end - st), CompressionMode.Decompress);
                gs.CopyTo(ms);
                if (ms.Length > 10)
                {
                    var decoded = ms.ToArray();
                    results[mi] = decoded;
                    // GZipStream returns what it decoded of a cut member without an error: the trailer must be there and match.
                    int header = ParseGzipHeader(raw, st, end - st);
                    statuses[mi] = header >= 0 && TrailerMatches(raw, header, end, decoded, [], 0) ? "gzip" : "gzip-unverified";
                    Interlocked.Increment(ref gzipOk);
                }
            }
            catch (Exception ex) when (MalformedInput.Is(ex)) { /* window-continued member: pass 2 */ }
        });

        // Pass 2 (sequential): members that continue the deflate window of preceding output.
        var recoveryJobs = new List<(int Mi, int St, int End, byte[] Dict, uint WantLen)>();
        for (int mi = 0; mi < markers.Count; mi++)
        {
            if (results[mi] != null) continue;

            // Build the 32KB dictionary from the tail of preceding members' output.
            var dict = new byte[32768];
            int dictLen = 0;
            for (int pi = mi - 1; pi >= 0 && dictLen < dict.Length; pi--)
            {
                var prev = results[pi];
                if (prev == null) break; // unbroken run of preceding output required
                int take = Math.Min(prev.Length, dict.Length - dictLen);
                // fill from the end: dict = [...prev2 tail][prev1 tail]
                Array.Copy(prev, prev.Length - take, dict, dict.Length - dictLen - take, take);
                dictLen += take;
            }

            int st = markers[mi];
            int end = (mi + 1 < markers.Count) ? markers[mi + 1] : raw.Length;
            int headerEnd = ParseGzipHeader(raw, st, end - st);
            if (headerEnd < 0)
            {
                failed++;
                DiagnosticSink.WriteLine($"  [decomp-fail] marker {mi} @0x{st:X} len={end - st}: bad gzip header");
                continue;
            }

            // The page segments sit at fixed offsets: removing them gives the member as the encoder wrote it,
            // and its gzip trailer (CRC32 and size) validates the result.
            if (TryDecodeWithoutPageSegments(raw, st, end, dict, dictLen, out var stripped))
            {
                results[mi] = stripped;
                statuses[mi] = "stripped-exact";
                pagedOk++;
                continue;
            }

            // RVT paged streams can contain a removable deflate segment which is not
            // counted by the page's fixed header. The header and gzip trailer provide
            // exact compressed size, output size and CRC validation.
            if (TryDecodePagedMember(raw, st, end, headerEnd, dict, dictLen, out var paged))
            {
                results[mi] = paged;
                statuses[mi] = "paged-exact";
                pagedOk++;
                continue;
            }

            bool ok = false;
            byte[]? corruptFallback = null;
            uint corruptWantLen = 0;
            foreach (bool d64 in new[] { false, true }) // some window-continued members are Deflate64
            {
                try
                {
                    var inflater = new InflaterManaged(deflate64: d64, uncompressedSize: -1);
                    if (dictLen > 0)
                        inflater.SetDictionary(dict, dict.Length - dictLen, dictLen);
                    inflater.SetInput(raw, headerEnd, end - headerEnd);
                    using var ms = new MemoryStream();
                    var buf = new byte[65536];
                    while (!inflater.Finished())
                    {
                        int n = inflater.Inflate(buf, 0, buf.Length);
                        if (n == 0) break;
                        ms.Write(buf, 0, n);
                    }
                    if (ms.Length > 10)
                    {
                        var outBytes = ms.ToArray();
                        // Members carry their gzip trailer at a FIXED position before 32 bytes
                        // of container metadata: [CRC32 @memberEnd-40][ISIZE @memberEnd-36].
                        // Pass 1 (GZipStream) verifies it; this raw-inflate path does not, and encoder bit flips mid-member
                        // decompress into silently wrong bytes. A plausible ISIZE disagreeing
                        // with the inflated length, or a CRC mismatch, indicates corruption.
                        // Keep the data either way (dropping shifts the stream) but log it.
                        bool trailerMismatch = false;
                        uint wantLen = 0;
                        if (end - st >= 48)
                        {
                            uint wantCrc = BitConverter.ToUInt32(raw, end - 40);
                            wantLen = BitConverter.ToUInt32(raw, end - 36);
                            trailerMismatch = wantLen > 512 && wantLen < 8_000_000
                                && (outBytes.Length != wantLen || Crc32(outBytes) != wantCrc);
                        }
                        if (trailerMismatch)
                        {
                            if (Environment.GetEnvironmentVariable("RVTIO_RECOVERY_DEBUG") == "1")
                                DiagnosticSink.WriteLine($"  [decoder-mismatch] marker={mi} dialect={(d64 ? "deflate64" : "deflate")} want={wantLen} got={outBytes.Length} crc=0x{Crc32(outBytes):X8}");
                            corruptFallback ??= outBytes;
                            corruptWantLen = wantLen;
                            continue;
                        }
                        results[mi] = outBytes;
                        bool verified = TrailerMatches(raw, headerEnd, end, outBytes, dict, dictLen);
                        statuses[mi] = (d64 ? "window-deflate64" : "window-deflate") + (verified ? "" : "-unverified");
                        windowOk++;
                        ok = true;
                        break;
                    }
                }
                catch (Exception ex) when (MalformedInput.Is(ex))
                {
                    // Expected: this dialect/window does not decode the member; the next candidate is tried.
                    if (Environment.GetEnvironmentVariable("RVTIO_RECOVERY_DEBUG") == "1")
                        DiagnosticSink.WriteLine($"  [decoder-fail] marker={mi} dialect={(d64 ? "deflate64" : "deflate")} {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (!ok && corruptFallback != null)
            {
                results[mi] = corruptFallback;
                statuses[mi] = "window-corrupt";
                var dictCopy = new byte[dictLen];
                Array.Copy(dict, dict.Length - dictLen, dictCopy, 0, dictLen);
                recoveryJobs.Add((mi, st, end, dictCopy, corruptWantLen));
                windowOk++;
                ok = true;
            }
            if (ok) continue;

            // Last resort: legacy multi-decoder path (handles truncated/odd members).
            var candidates = new List<int> { end - st };
            if (end < raw.Length) candidates.Add(raw.Length - st);
            var dLegacy = TryAllDecoders(raw, st, candidates);
            dLegacy ??= DecompressTolerant(raw, st, end - st);
            if (dLegacy != null && dLegacy.Length > 10)
            {
                results[mi] = dLegacy;
                statuses[mi] = TrailerMatches(raw, headerEnd, end, dLegacy, dict, dictLen) ? "legacy" : "legacy-unverified";
                windowOk++;
            }
            else
            {
                // Final resort: structural desync recovery. Its bit-level inflater salvages
                // the blocks before the corruption and relocks past it; the trailer ISIZE
                // sizes the splice. Recovers members every standard decoder gave up on.
                byte[]? dRec = null;
                if (end - st >= 48)
                {
                    uint wantLen = BitConverter.ToUInt32(raw, end - 36);
                    if (wantLen > 512 && wantLen < 8_000_000)
                    {
                        var dictCopy = new byte[dictLen];
                        Array.Copy(dict, dict.Length - dictLen, dictCopy, 0, dictLen);
                        dRec = GBlockRecover.TryRecover(raw, st, end, dictCopy, dictLen, wantLen)
                            // First-block-header corruption: every decoder dies instantly;
                            // start-relock scans for the next valid dynamic Huffman header.
                            ?? GBlockRecover.TryRecoverStart(raw, st, end, dictCopy, dictLen, wantLen);
                    }
                }
                if (dRec != null)
                {
                    results[mi] = dRec;
                    statuses[mi] = "recovered-loss";
                    windowOk++;
                    recovered++;
                    DiagnosticSink.WriteLine($"  [decomp-rescued] marker {mi} @0x{st:X}: {dRec.Length} bytes (was a total loss)");
                }
                else
                {
                    statuses[mi] = "failed";
                    failed++;
                    DiagnosticSink.WriteLine($"  [decomp-fail] marker {mi} @0x{st:X} len={end - st} dictLen={dictLen}");
                }
            }
        }

        // Pass 3 (parallel): structural desync recovery of members flagged corrupt.
        // Returns output of exactly ISIZE bytes, putting every later member back at its
        // true stream position. Falls back to the corrupt bytes on failure.
        if (recoveryJobs.Count > 0)
        {
            int rec = 0;
            var reasons = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            Parallel.ForEach(recoveryJobs, job =>
            {
                var r = GBlockRecover.TryRecover(raw, job.St, job.End, job.Dict, job.Dict.Length, job.WantLen, out string why);
                if (r != null)
                {
                    results[job.Mi] = r;
                    statuses[job.Mi] = "recovered-corrupt";
                    Interlocked.Increment(ref rec);
                }
                else
                {
                    reasons.AddOrUpdate(why, 1, (_, n) => n + 1);
                    if (Environment.GetEnvironmentVariable("RVTIO_RECOVERY_DEBUG") == "1")
                        DiagnosticSink.WriteLine($"  [recovery-detail] marker={job.Mi} want={job.WantLen} kept={results[job.Mi]?.Length ?? 0} reason={why}");
                }
            });
            recovered = rec;
            DiagnosticSink.WriteLine($"  [recovery] {rec}/{recoveryJobs.Count} corrupt members structurally recovered");
            foreach (var kv in reasons.OrderByDescending(k => k.Value))
                DiagnosticSink.WriteLine($"  [recovery-fail] {kv.Key}: {kv.Value}");
        }

        var chunks = new List<byte[]>(markers.Count);
        foreach (var r in results)
            if (r != null) chunks.Add(r);

        if (Environment.GetEnvironmentVariable("RVTIO_DUMP_MEMBER_RANGES") == "1")
        {
            long streamOffset = 0;
            int outputIndex = 0;
            for (int mi = 0; mi < results.Length; mi++)
            {
                var result = results[mi];
                if (result == null) continue;
                DiagnosticSink.WriteLine($"  [member-range] out={outputIndex} marker={mi} status={statuses[mi] ?? "unknown"} start={streamOffset} end={streamOffset + result.Length} len={result.Length}");
                streamOffset += result.Length;
                outputIndex++;
            }
        }

        int unverified = statuses.Count(status => status is "window-corrupt" || status != null && status.EndsWith("-unverified", StringComparison.Ordinal));
        int rescued = statuses.Count(status => status is "recovered-loss" or "recovered-corrupt");
        LastStats = (markers.Count, chunks.Count, markers.Count - chunks.Count, rescued, unverified);
        DiagnosticSink.WriteLine($"Decompression: {gzipOk} gzip, {pagedOk} paged-exact, {windowOk} window-carry, {failed} failed, {recovered} desync-recovered (of {markers.Count} markers)");
        return chunks;
    }

    /// <summary>True when the gzip trailer of the member [header end, <paramref name="end"/>) holds the CRC-32 and the size of
    /// <paramref name="output"/>: at the fixed place of a member (40 bytes before its end), at the end of a plain gzip stream, or right
    /// behind the final deflate block. A cut member, or one that the decoder did not read to its final block, has none.</summary>
    private static bool TrailerMatches(byte[] raw, int headerEnd, int end, byte[] output, byte[] dict, int dictLen)
    {
        uint crc = Crc32(output);
        foreach (int fromEnd in new[] { 40, 8 })
            if (end - fromEnd >= headerEnd
                && BitConverter.ToUInt32(raw, end - fromEnd) == crc && BitConverter.ToUInt32(raw, end - fromEnd + 4) == (uint)output.Length)
                return true;
        try
        {
            var inflater = new InflaterManaged(deflate64: false, uncompressedSize: -1);
            if (dictLen > 0)
                inflater.SetDictionary(dict, dict.Length - dictLen, dictLen);
            inflater.SetInput(raw, headerEnd, end - headerEnd);
            long total = 0;
            var buf = new byte[65536];
            while (!inflater.Finished())
            {
                int n = inflater.Inflate(buf, 0, buf.Length);
                if (n == 0) break;
                total += n;
            }
            if (!inflater.Finished() || total != output.Length) return false;
            int trailer = end - inflater.UnconsumedInputBytes;
            return trailer + 8 <= end && BitConverter.ToUInt32(raw, trailer) == crc && BitConverter.ToUInt32(raw, trailer + 4) == (uint)output.Length;
        }
        // an undecodable tail is a member without a verified trailer
        catch (Exception ex) when (MalformedInput.Is(ex)) { return false; }
    }

    /// <summary>True when a page segment (<see cref="PagedGzipStream"/>: offsets 64896 + k * 65249, 353 bytes) lies in
    /// [start, end) of a stream; a trailing partial page has none.</summary>
    private static bool ContainsPageSegment(byte[] raw, int start, int end)
    {
        long first = start <= PagedGzipStream.PageDataLength ? PagedGzipStream.PageDataLength
            : PagedGzipStream.PageDataLength + (start - PagedGzipStream.PageDataLength + PagedGzipStream.PageStride - 1)
                / PagedGzipStream.PageStride * PagedGzipStream.PageStride;
        return first < end && first + PagedGzipStream.SegmentLength <= raw.Length;
    }

    /// <summary>A member with its page segments removed ends with the gzip trailer (CRC32, size) and 32 bytes of container
    /// metadata. The member is inflated
    /// with the preceding output as the preset dictionary and accepted only when the size and CRC32 of the trailer match,
    /// so a wrong page rule gives no data rather than wrong data. False when no segment lies inside the member, a segment
    /// lacks the segment tag (a zero byte, then a byte with a zero low nibble), or the trailer does not match.</summary>
    internal static bool TryDecodeWithoutPageSegments(
        byte[] raw, int start, int end, byte[] dict, int dictLen, out byte[] output)
    {
        output = Array.Empty<byte>();
        const int TrailerOffsetFromEnd = 40;
        if (!ContainsPageSegment(raw, start, end)) return false;
        var body = new MemoryStream(end - start);
        int source = start;
        for (long segment = PagedGzipStream.PageDataLength; segment < end; segment += PagedGzipStream.PageStride)
        {
            if (segment < start) continue;
            if (segment + PagedGzipStream.SegmentLength > raw.Length) break;
            long overrun = segment + PagedGzipStream.SegmentLength - end;
            long at = overrun >= 1 && overrun < RvtGzipHeader.Length ? segment - overrun : segment;
            if (at + PagedGzipStream.SegmentLength > end || raw[at] != 0 || (raw[at + 1] & 0x0F) != 0)
                return false;
            body.Write(raw, source, (int)at - source);
            source = (int)at + PagedGzipStream.SegmentLength;
        }
        body.Write(raw, source, end - source);
        byte[] member = body.ToArray();
        if (member.Length < RvtGzipHeader.Length + TrailerOffsetFromEnd
            || !member.AsSpan(0, RvtGzipHeader.Length).SequenceEqual(RvtGzipHeader))
            return false;
        uint wantCrc = BitConverter.ToUInt32(member, member.Length - TrailerOffsetFromEnd);
        uint wantLength = BitConverter.ToUInt32(member, member.Length - TrailerOffsetFromEnd + 4);
        if (wantLength > 10 && wantLength <= 8_000_000)
        {
            int deflateLength = member.Length - TrailerOffsetFromEnd - RvtGzipHeader.Length;
            var deflate = new byte[deflateLength];
            Buffer.BlockCopy(member, RvtGzipHeader.Length, deflate, 0, deflateLength);
            byte[]? candidate = InflateRaw(deflate, dict, dictLen);
            if (candidate != null && candidate.Length == wantLength && Crc32(candidate) == wantCrc)
            {
                output = candidate;
                return true;
            }
        }
        return TryDecodeAtDeflateEnd(member, dict, dictLen, out output);
    }

    /// <summary>A member can be followed by more than the 32 bytes of container metadata (for example 220 bytes in some Revit 2025
    /// partitions), so its gzip trailer is not at a fixed distance from the member end. The trailer sits right behind
    /// the final deflate block: inflate to that block's end and accept only when the 8 bytes there are the CRC-32 and size of the
    /// inflated output. A member with a split gzip header is repaired first by <see cref="MoveSegmentsBeforeSplitHeaders"/>.</summary>
    private static bool TryDecodeAtDeflateEnd(byte[] member, byte[] dict, int dictLen, out byte[] output)
    {
        output = Array.Empty<byte>();
        try
        {
            var inflater = new InflaterManaged(deflate64: false, uncompressedSize: -1);
            if (dictLen > 0)
                inflater.SetDictionary(dict, dict.Length - dictLen, dictLen);
            inflater.SetInput(member, RvtGzipHeader.Length, member.Length - RvtGzipHeader.Length);
            using var ms = new MemoryStream();
            var buf = new byte[65536];
            while (!inflater.Finished())
            {
                int n = inflater.Inflate(buf, 0, buf.Length);
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
            if (!inflater.Finished()) return false;
            int trailer = member.Length - inflater.UnconsumedInputBytes;
            if (trailer + 8 > member.Length) return false;
            var candidate = ms.ToArray();
            if (candidate.Length != BitConverter.ToUInt32(member, trailer + 4)
                || Crc32(candidate) != BitConverter.ToUInt32(member, trailer)) return false;
            output = candidate;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>The stream carries a 353-byte page segment at every offset 64896 + k * 65249 (<see cref="PagedGzipStream"/>), and a
    /// member whose gzip header straddles such a boundary is cut by it: [first j header bytes][segment][remaining 10 - j]. The
    /// member is then invisible to <see cref="FindMemberMarkers"/> and its data is lost.
    /// For each such header this returns a copy in which the segment is moved in front of the header, the layout of a
    /// member that starts right after a segment; the other bytes and offsets stay. A boundary is repaired only when the segment
    /// has the segment tag (a zero byte, then a byte with a zero low nibble) and both header parts match exactly.</summary>
    internal static byte[] MoveSegmentsBeforeSplitHeaders(byte[] raw)
    {
        byte[]? repaired = null;
        for (long boundary = PagedGzipStream.PageDataLength;
            boundary + PagedGzipStream.SegmentLength + RvtGzipHeader.Length <= raw.Length;
            boundary += PagedGzipStream.PageStride)
        {
            int segment = (int)boundary;
            if (raw[segment] != 0 || (raw[segment + 1] & 0x0F) != 0) continue;
            int after = segment + PagedGzipStream.SegmentLength;
            for (int head = 1; head < RvtGzipHeader.Length; head++)
            {
                if (!raw.AsSpan(segment - head, head).SequenceEqual(RvtGzipHeader.AsSpan(0, head))
                    || !raw.AsSpan(after, RvtGzipHeader.Length - head).SequenceEqual(RvtGzipHeader.AsSpan(head)))
                    continue;
                repaired ??= (byte[])raw.Clone();
                Buffer.BlockCopy(raw, segment, repaired, segment - head, PagedGzipStream.SegmentLength);
                Buffer.BlockCopy(RvtGzipHeader, 0, repaired, segment - head + PagedGzipStream.SegmentLength, RvtGzipHeader.Length);
                break;
            }
        }
        return repaired ?? raw;
    }

    internal static List<int> FindMemberMarkers(byte[] raw)
    {
        var markers = new List<int>();
        for (int i = 0; i <= raw.Length - RvtGzipHeader.Length; i++)
        {
            if (!raw.AsSpan(i, RvtGzipHeader.Length).SequenceEqual(RvtGzipHeader))
                continue;
            markers.Add(i);
            i += RvtGzipHeader.Length - 1;
        }

        if (markers.Count > 0)
            return markers;

        // Keep the decompressor usable for an isolated standards-compliant gzip member
        // (for example diagnostic input not wrapped by an RVT partition stream).
        for (int i = 0; i <= raw.Length - 10; i++)
        {
            if (raw[i] != 0x1F || raw[i + 1] != 0x8B || raw[i + 2] != 0x08
                || (raw[i + 3] & 0xE0) != 0)
                continue;
            markers.Add(i);
        }
        return markers;
    }

    /// <summary>
    /// Decode a paged gzip member whose physical deflate body contains segments
    /// excluded from the logical compressed-size field. A member is terminated by
    /// [gzip trailer:8][fixed page footer:8]; optional page metadata may follow it.
    /// A candidate is accepted only when deflate reaches BFINAL and both trailer
    /// ISIZE and CRC32 match.
    /// </summary>
    private static bool TryDecodePagedMember(
        byte[] raw, int st, int end, int headerEnd, byte[] dict, int dictLen,
        out byte[] output)
    {
        const int ExcludedSegmentSize = 353;
        output = Array.Empty<byte>();
        const int FixedFooterSize = 8;

        if (end - st < headerEnd - st + 16)
            return false;

        // The footer is normally immediately before the next 24-byte contents header,
        // but some page variants append their own metadata. Locate the self-describing
        // footer and let the gzip trailer validate it.
        for (int footer = end - 32; footer >= headerEnd + 8; footer--)
        {
            if (BitConverter.ToUInt16(raw, footer) != 0x0DE2
                || BitConverter.ToUInt16(raw, footer + 6) != 0x0DE9)
                continue;

            uint declaredRecordSize = BitConverter.ToUInt32(raw, footer + 2);
            int physicalRecordSize = footer + FixedFooterSize - st;
            long extraLong = physicalRecordSize - declaredRecordSize;
            if (extraLong < 0 || extraLong % ExcludedSegmentSize != 0)
                continue;

            int segmentCount = (int)(extraLong / ExcludedSegmentSize);
            if (segmentCount > 4)
                continue;

            int extra = (int)extraLong;
            int deflateEnd = footer - 8;
            int deflateLength = deflateEnd - headerEnd;
            if (deflateLength <= 0 || extra >= deflateLength)
                continue;

            uint wantCrc = BitConverter.ToUInt32(raw, deflateEnd);
            uint wantLength = BitConverter.ToUInt32(raw, deflateEnd + 4);
            if (wantLength <= 10 || wantLength > 8_000_000)
                continue;

            byte[]? decoded = DecodeAtFooter(
                deflateEnd, deflateLength, extra, segmentCount, wantCrc, wantLength);
            if (decoded == null)
                continue;

            output = decoded;
            return true;
        }

        return false;

        byte[]? DecodeAtFooter(
            int deflateEnd, int deflateLength, int extra, int segmentCount,
            uint wantCrc, uint wantLength)
        {
            // Excluded segments are byte-aligned: the zero tag is followed by a byte
            // whose low nibble is padding. Trailer validation remains authoritative.
            var starts = new List<int>();
            for (int cut = headerEnd; cut <= deflateEnd - ExcludedSegmentSize; cut++)
            {
                if (raw[cut] == 0 && cut + 1 < deflateEnd && (raw[cut + 1] & 0x0F) == 0)
                    starts.Add(cut);
            }

            var chosen = new int[segmentCount];
            return Search(0, 0);

            byte[]? Search(int depth, int firstCandidate)
            {
                if (depth == segmentCount)
                {
                    var compressed = new byte[deflateLength - extra];
                    int source = headerEnd;
                    int target = 0;
                    foreach (int cut in chosen)
                    {
                        int keep = cut - source;
                        Buffer.BlockCopy(raw, source, compressed, target, keep);
                        target += keep;
                        source = cut + ExcludedSegmentSize;
                    }
                    Buffer.BlockCopy(raw, source, compressed, target, deflateEnd - source);

                    byte[]? candidate = InflateRaw(compressed, dict, dictLen);
                    return candidate != null && candidate.Length == wantLength
                        && Crc32(candidate) == wantCrc ? candidate : null;
                }

                for (int i = firstCandidate; i < starts.Count; i++)
                {
                    int cut = starts[i];
                    if (depth > 0 && cut < chosen[depth - 1] + ExcludedSegmentSize)
                        continue;
                    chosen[depth] = cut;
                    byte[]? candidate = Search(depth + 1, i + 1);
                    if (candidate != null)
                        return candidate;
                }
                return null;
            }
        }
    }

    private static byte[]? InflateRaw(byte[] compressed, byte[] dict, int dictLen)
    {
        try
        {
            var inflater = new InflaterManaged(deflate64: false, uncompressedSize: -1);
            if (dictLen > 0)
                inflater.SetDictionary(dict, dict.Length - dictLen, dictLen);
            inflater.SetInput(compressed, 0, compressed.Length);
            using var ms = new MemoryStream();
            var buf = new byte[65536];
            while (!inflater.Finished())
            {
                int n = inflater.Inflate(buf, 0, buf.Length);
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
            return inflater.Finished() ? ms.ToArray() : null;
        }
        // The probed bytes are not a clean deflate stream with this dictionary: no result.
        catch (Exception ex) when (MalformedInput.Is(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Try all managed decoders (GZipStream, managed Deflate64, SharpZipLib Inflater)
    /// with all boundary candidates. Returns the largest successful result.
    /// </summary>
    private static byte[]? TryAllDecoders(byte[] raw, int st, List<int> candidates)
    {
        byte[]? best = null;

        foreach (int len in candidates)
        {
            if (len <= 0) continue;

            // Try GZipStream with extended boundaries
            try
            {
                using var ms = new MemoryStream();
                using var gs = new GZipStream(new MemoryStream(raw, st, len), CompressionMode.Decompress);
                gs.CopyTo(ms);
                if (ms.Length > (best?.Length ?? 0))
                    best = ms.ToArray();
            }
            // Not a valid gzip member at this length: try the next decoder.
            catch (Exception ex) when (MalformedInput.Is(ex)) { }

            // Try managed Deflate64 decoder
            var d64 = DecompressDeflate64Gzip(raw, st, len);
            if (d64 != null && d64.Length > (best?.Length ?? 0))
                best = d64;

            // Try SharpZipLib Inflater (bypasses CRC, different Deflate implementation)
            var szl = DecompressSharpZipLib(raw, st, len);
            if (szl != null && szl.Length > (best?.Length ?? 0))
                best = szl;
        }

        return best;
    }

    /// <summary>
    /// Parse GZIP header and return offset of raw deflate data.
    /// Returns -1 if header is invalid.
    /// </summary>
    private static int ParseGzipHeader(byte[] data, int offset, int length)
    {
        if (length < 10 || offset < 0 || offset > data.Length - length
            || data[offset] != 0x1F || data[offset + 1] != 0x8B
            || data[offset + 2] != 0x08 || (data[offset + 3] & 0xE0) != 0)
            return -1;

        int pos = offset + 10;
        byte flags = data[offset + 3];
        if ((flags & 0x04) != 0 && pos + 2 <= offset + length)
        {
            int xlen = data[pos] | (data[pos + 1] << 8);
            pos += 2 + xlen;
        }
        if ((flags & 0x08) != 0)
            while (pos < offset + length && data[pos++] != 0) { }
        if ((flags & 0x10) != 0)
            while (pos < offset + length && data[pos++] != 0) { }
        if ((flags & 0x02) != 0)
            pos += 2;

        return pos <= offset + length ? pos : -1;
    }

    public static byte[]? DecompressDeflate64Gzip(byte[] data, int offset, int length)
    {
        int headerEnd = ParseGzipHeader(data, offset, length);
        if (headerEnd < 0) return null;

        int rawLen = offset + length - headerEnd;
        if (rawLen <= 0) return null;

        foreach (bool deflate64 in new[] { true, false })
        {
            foreach (int trailerSize in new[] { 8, 0 })
            {
                int compLen = rawLen - trailerSize;
                if (compLen <= 0) continue;
                try
                {
                    using var cs = new MemoryStream(data, headerEnd, compLen);
                    if (deflate64)
                    {
                        using var ds = new Deflate64Stream(cs);
                        using var output = new MemoryStream();
                        ds.CopyTo(output);
                        if (output.Length > 0) return output.ToArray();
                    }
                    else
                    {
                        using var ds = new DeflateStream(cs, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        ds.CopyTo(output);
                        if (output.Length > 0) return output.ToArray();
                    }
                }
                // Not this framing: try the next candidate.
                catch (Exception ex) when (MalformedInput.Is(ex)) { }
            }
        }
        return null;
    }

    private static byte[]? DecompressSharpZipLib(byte[] data, int offset, int length)
    {
        int headerEnd = ParseGzipHeader(data, offset, length);
        if (headerEnd < 0) return null;

        int rawLen = offset + length - headerEnd;
        if (rawLen <= 0) return null;

        foreach (int trailerSize in new[] { 8, 0 })
        {
            int compLen = rawLen - trailerSize;
            if (compLen <= 0) continue;
            try
            {
                using var cs = new MemoryStream(data, headerEnd, compLen);
                using var ds = new InflaterInputStream(cs, new Inflater(noHeader: true));
                using var output = new MemoryStream();
                ds.CopyTo(output);
                if (output.Length > 0) return output.ToArray();
            }
            // Wrong trailer size: try the next one.
            catch (Exception ex) when (MalformedInput.Is(ex)) { }
        }
        return null;
    }

    /// <summary>Inflates the gzip member at [offset, offset + length) and returns the bytes decoded before a damaged or
    /// truncated deflate stream ends the decoding; the gzip trailer (CRC32, size) is not checked and bytes behind the final
    /// block are ignored. A back-reference to data before the start of the member ends the decoding as well, since the member
    /// is decoded without a dictionary. Null when the gzip header is invalid.</summary>
    internal static byte[]? DecompressTolerant(byte[] data, int offset, int length)
    {
        int headerEnd = ParseGzipHeader(data, offset, length);
        if (headerEnd < 0) return null;
        const int PaddingBytes = 16;
        int deflateLength = offset + length - headerEnd;
        var padded = new byte[deflateLength + PaddingBytes];
        Buffer.BlockCopy(data, headerEnd, padded, 0, deflateLength);
        Array.Fill(padded, (byte)0xFF, deflateLength, PaddingBytes);
        var inflater = new InflaterManaged(deflate64: false, uncompressedSize: -1)
            { Strict = true, PaddingBits = PaddingBytes * 8 };
        inflater.SetInput(padded, 0, padded.Length);
        return inflater.InflateTolerant();
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] data)
    {
        if (_crcTable == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            _crcTable = t;
        }
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
