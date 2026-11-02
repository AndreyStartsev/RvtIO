namespace RvtIO.Parsing;

/// <summary>
/// Bit-desync-recovering DEFLATE inflater for the ElemTable stream of some files.
///
/// Revit's gzip encoder injects localized 1-2 bit corruption inside dynamic DEFLATE
/// blocks. A standard inflater takes a spurious early end-of-block, then reads what looks
/// like an invalid stored-block header at the next position and stops - so GZipStream
/// recovers only part of the table. This inflater keeps going: at each
/// desync it re-uses the corrupt block's Huffman table, scans candidate bit offsets past
/// the divergence, and relocks on the structural record grammar (ascending [eid][eid]
/// dup-pair records) - pinning absolute output alignment via an exact-prefix match so the
/// 32 KB LZ window stays coherent. Scoring uses only structural signals.
/// </summary>
internal static class ElemTableRecover
{
    // Standard RFC1951 tables (32K window; length code 285 = 258, NOT Deflate64).
    internal static readonly int[] LenBase =
        { 3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258 };
    internal static readonly int[] LenExtra =
        { 0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0 };
    internal static readonly int[] DistBase =
        { 1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577 };
    internal static readonly int[] DistExtra =
        { 0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13 };
    private static readonly int[] ClcOrder =
        { 16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 };

    private const int MaxBits = 15;

    // Structural constants of the relock algorithm.
    private const uint EidMin = 1000;
    private const uint EidMax = 0x0F000000;      // 251,658,240
    private const uint MaxEidGap = 2_000_000;    // max +delta between adjacent record eids
    private const int MaxRecGap = 600;           // max byte gap between adjacent record starts
    private const uint BandWidth = 20000;        // tight eid band above E_last for relock scoring
    private const int MinRecords = 4;            // min in-band records for a non-exact candidate
    private const int ProbeBytes = 6000;         // decode this many bytes to score a candidate
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    // Diagnostics. Everything below is inert while Diag == null: the only cost on the normal path is a
    // null check per desync iteration / per relock scan (never per decoded symbol).
    /// <summary>Set to a writer to emit desync/relock diagnostics. Null = no instrumentation. Per thread, so a read on another
    /// thread never sees it; the counters below are written only while it is set (the developer harness is single threaded).</summary>
    [ThreadStatic]
    internal static TextWriter? Diag;
    /// <summary>Per-relock-scan statistics, appended while <see cref="Diag"/> is non-null.</summary>
    internal sealed class RelockStats
    {
        public long InfoBit;
        public int Lo, Hi, Probe;
        public int Candidates;          // boff values tried
        public int DecodeFailed;        // threw Desync/OutputLimit while probing
        public int TooShort;            // decoded, but shorter than needed prefix + 16
        public int Survived;            // reached the scoring stage
        public int ExactPrefix;         // exact-prefix matches
        public int BandRejected;        // !exact && nb < MinRecords
        public int Scored;              // entered the ranking
        public double Ms;
        public readonly List<string> Top = new();
    }
    internal static readonly List<RelockStats> DiagRelocks = new();
    /// <summary>Desync index (1-based) -&gt; forced relock delta in bits from info.Bit.</summary>
    internal static readonly Dictionary<int, long> DiagForceRelock = new();
    internal static bool DiagBlockLog;                // log every deflate block header
    internal static int DiagBlocks;                   // deflate blocks entered
    internal static int DiagMaxCandidateProbeBytes;   // largest probe buffer actually filled
    internal static long DiagProbeSymbolBudget;       // total probe bytes decoded across all scans

    internal sealed class DesyncException : Exception { }
    internal sealed class OutputLimitExceededException : Exception { }

    internal static void Bail() => throw new DesyncException();

    internal sealed class BitReader
    {
        private readonly byte[] _d;
        public long BitPos;
        public readonly long N;

        public BitReader(byte[] d, long bitpos)
        {
            _d = d;
            BitPos = bitpos;
            N = (long)d.Length * 8;
        }

        public uint Get(int k)
        {
            long v = 0, bp = BitPos;
            for (int i = 0; i < k; i++)
            {
                if (bp >= N) Bail();
                int by = _d[bp >> 3];
                v |= (long)((by >> (int)(bp & 7)) & 1) << i;
                bp++;
            }
            BitPos = bp;
            return (uint)v;
        }

        public void Align()
        {
            if ((BitPos & 7) != 0) BitPos = (BitPos + 7) & ~7L;
        }

        public byte[] Data => _d;
    }

    // Canonical Huffman decoding (puff-style, no allocation per symbol).
    internal sealed class Huff
    {
        private readonly int[] _count = new int[MaxBits + 1];
        private readonly int[] _symbol;

        public Huff(int[] lengths)
        {
            foreach (int l in lengths)
                if (l > 0)
                {
                    if (l > MaxBits) Bail();
                    _count[l]++;
                }
            _symbol = new int[lengths.Length];
            var offs = new int[MaxBits + 2];
            for (int l = 1; l <= MaxBits; l++) offs[l + 1] = offs[l] + _count[l];
            for (int sym = 0; sym < lengths.Length; sym++)
                if (lengths[sym] != 0) _symbol[offs[lengths[sym]]++] = sym;
        }

        public int Decode(BitReader br)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= MaxBits; len++)
            {
                code |= (int)br.Get(1);
                int c = _count[len];
                if (code - first < c) return _symbol[index + (code - first)];
                index += c;
                first += c;
                first <<= 1;
                code <<= 1;
            }
            Bail();
            return -1;
        }
    }

    internal static readonly Huff FixedLit = BuildFixedLit();
    internal static readonly Huff FixedDist = new(NArray(30 /*unused*/, 5, 32));

    private static Huff BuildFixedLit()
    {
        var l = new int[288];
        for (int i = 0; i < 144; i++) l[i] = 8;
        for (int i = 144; i < 256; i++) l[i] = 9;
        for (int i = 256; i < 280; i++) l[i] = 7;
        for (int i = 280; i < 288; i++) l[i] = 8;
        return new Huff(l);
    }

    private static int[] NArray(int _, int val, int count)
    {
        var a = new int[count];
        for (int i = 0; i < count; i++) a[i] = val;
        return a;
    }

    internal static void ReadDynHeader(BitReader br, out Huff lit, out Huff dist)
    {
        int hlit = (int)br.Get(5) + 257;
        int hdist = (int)br.Get(5) + 1;
        int hclen = (int)br.Get(4) + 4;
        var cl = new int[19];
        for (int i = 0; i < hclen; i++) cl[ClcOrder[i]] = (int)br.Get(3);
        var clh = new Huff(cl);
        var lens = new List<int>(hlit + hdist);
        while (lens.Count < hlit + hdist)
        {
            int s = clh.Decode(br);
            if (s < 16) lens.Add(s);
            else if (s == 16)
            {
                if (lens.Count == 0) Bail();
                int rep = 3 + (int)br.Get(2), prev = lens[^1];
                for (int r = 0; r < rep; r++) lens.Add(prev);
            }
            else if (s == 17)
            {
                int rep = 3 + (int)br.Get(3);
                for (int r = 0; r < rep; r++) lens.Add(0);
            }
            else
            {
                int rep = 11 + (int)br.Get(7);
                for (int r = 0; r < rep; r++) lens.Add(0);
            }
        }
        if (lens.Count != hlit + hdist) Bail();
        var litLens = lens.GetRange(0, hlit).ToArray();
        var distLens = lens.GetRange(hlit, hdist).ToArray();
        bool allZero = true;
        foreach (int x in litLens) if (x != 0) { allZero = false; break; }
        if (allZero) Bail();
        lit = new Huff(litLens);
        dist = new Huff(distLens);
    }

    internal sealed class ByteBuf
    {
        public byte[] Arr;
        public int Count;
        public int MaxCount { get; }

        public ByteBuf(int cap, int maxCount = int.MaxValue)
        {
            if (cap < 0) throw new ArgumentOutOfRangeException(nameof(cap));
            if (maxCount < 0) throw new ArgumentOutOfRangeException(nameof(maxCount));
            MaxCount = maxCount;
            Arr = new byte[Math.Min(cap, maxCount)];
            Count = 0;
        }

        private void Ensure(int extra)
        {
            if (extra < 0) throw new ArgumentOutOfRangeException(nameof(extra));
            long required = (long)Count + extra;
            if (required > MaxCount) throw new OutputLimitExceededException();
            if (required <= Arr.Length) return;

            long doubled = Math.Max(64L, (long)Arr.Length * 2);
            int newSize = (int)Math.Min(MaxCount, Math.Max(required, doubled));
            Array.Resize(ref Arr, newSize);
        }
        public void Add(byte b) { Ensure(1); Arr[Count++] = b; }
        public void AddRange(byte[] s, int off, int len) { Ensure(len); Buffer.BlockCopy(s, off, Arr, Count, len); Count += len; }
        public void Clear() { Count = 0; }
        public void Truncate(int len) { if (len < Count) Count = len; }
        public byte[] ToArray() { var r = new byte[Count]; Buffer.BlockCopy(Arr, 0, r, 0, Count); return r; }
        public uint U32(int off) => (uint)(Arr[off] | (Arr[off + 1] << 8) | (Arr[off + 2] << 16) | (Arr[off + 3] << 24));
    }

    private enum Why { BFinal, StoredBad, RanOut, Btype3, Error }

    private sealed class DesyncInfo
    {
        public Why Why;
        public long Bit;
        /// <summary>Raw bit position immediately after this block finished decoding (only
        /// meaningful when <see cref="Why"/> is <see cref="Why.BFinal"/>: the true end of the
        /// gzip member's deflate data, before byte alignment). Used to locate the member's own
        /// trailer instead of assuming it sits at a fixed offset from the end of the stream.</summary>
        public long EndBit;
        public Huff? Lit;
        public Huff? Dist;
    }

    // Standard multi-block inflate into `out`, returning when it hits BFINAL, runs out,
    // or detects the desync signature (a stored-block header with LEN != ~NLEN). Remembers
    // the Huffman table of the block that was decoding, for the relock.
    private static DesyncInfo InflateUntilDesync(BitReader br, ByteBuf outp)
    {
        Huff? lastLit = null, lastDist = null;
        while (true)
        {
            if (Diag != null) DiagBlocks++; // deflate blocks seen
            long bstart = br.BitPos;
            if (DiagBlockLog && Diag != null && bstart + 3 <= br.N)
            {
                long save = br.BitPos;
                uint bf = br.Get(1), bt = br.Get(2);
                br.BitPos = save;
                Diag.WriteLine($"    blk#{DiagBlocks} bit={bstart} byte={bstart / 8}.{bstart % 8} " +
                               $"bfinal={bf} btype={bt} outBefore={outp.Count}");
            }
            if (bstart + 3 > br.N) return new DesyncInfo { Why = Why.RanOut, Bit = bstart, Lit = lastLit, Dist = lastDist };
            uint bfinal, btype;
            try { bfinal = br.Get(1); btype = br.Get(2); }
            catch (DesyncException) { return new DesyncInfo { Why = Why.RanOut, Bit = bstart, Lit = lastLit, Dist = lastDist }; }

            if (btype == 0)
            {
                br.Align();
                int bytepos = (int)(br.BitPos >> 3);
                if (bytepos + 4 > br.Data.Length)
                    return new DesyncInfo { Why = Why.RanOut, Bit = bstart, Lit = lastLit, Dist = lastDist };
                int ln = br.Data[bytepos] | (br.Data[bytepos + 1] << 8);
                int nlen = br.Data[bytepos + 2] | (br.Data[bytepos + 3] << 8);
                if (ln != ((~nlen) & 0xFFFF))                    // <-- DESYNC SIGNATURE
                    return new DesyncInfo { Why = Why.StoredBad, Bit = bstart, Lit = lastLit, Dist = lastDist };
                bytepos += 4;
                if (bytepos + ln > br.Data.Length)
                    return new DesyncInfo { Why = Why.RanOut, Bit = bstart, Lit = lastLit, Dist = lastDist };
                outp.AddRange(br.Data, bytepos, ln);
                br.BitPos = (long)(bytepos + ln) * 8;
                if (bfinal != 0) return new DesyncInfo { Why = Why.BFinal, Bit = bstart, EndBit = br.BitPos, Lit = lastLit, Dist = lastDist };
                continue;
            }
            if (btype == 3)
                return new DesyncInfo { Why = Why.Btype3, Bit = bstart, Lit = lastLit, Dist = lastDist };

            Huff lit, dist;
            try
            {
                if (btype == 1) { lit = FixedLit; dist = FixedDist; }
                else ReadDynHeader(br, out lit, out dist);
            }
            catch (DesyncException)
            {
                return new DesyncInfo { Why = Why.Error, Bit = bstart, Lit = lastLit, Dist = lastDist };
            }
            lastLit = lit; lastDist = dist;

            try
            {
                while (true)
                {
                    int sym = lit.Decode(br);
                    if (sym < 256) outp.Add((byte)sym);
                    else if (sym == 256) break;
                    else
                    {
                        int i = sym - 257;
                        if (i >= LenBase.Length) Bail();
                        int length = LenBase[i] + (int)br.Get(LenExtra[i]);
                        int ds = dist.Decode(br);
                        if (ds >= DistBase.Length) Bail();
                        int d = DistBase[ds] + (int)br.Get(DistExtra[ds]);
                        if (d > outp.Count) Bail();
                        int st = outp.Count - d;
                        for (int k = 0; k < length; k++) outp.Add(outp.Arr[st + k]);
                    }
                }
            }
            catch (DesyncException)
            {
                return new DesyncInfo { Why = Why.Error, Bit = bstart, Lit = lastLit, Dist = lastDist };
            }
            if (bfinal != 0) return new DesyncInfo { Why = Why.BFinal, Bit = bstart, EndBit = br.BitPos, Lit = lastLit, Dist = lastDist };
        }
    }

    private static void FindLastGoodRecord(ByteBuf outp, out int rEnd, out uint eLast)
    {
        int start = Math.Max(0, outp.Count - 300000);
        int bestReach = -1, bestP = 0, bestStride = 28;
        uint bestE = 0;

        for (int phase = 0; phase < 4; phase++)
        {
            int p = start;
            while ((p & 3) != phase) p++;
            uint prevE = 0; int prevP = -1, lastDelta = 28;
            int runLen = 0, runLastP = 0, runLastStride = 28; uint runLastE = 0;

            void Commit()
            {
                if (runLen >= 8 && runLastP > bestReach)
                {
                    bestReach = runLastP; bestP = runLastP; bestE = runLastE; bestStride = runLastStride;
                }
            }

            while (p < outp.Count - 8)
            {
                uint e = outp.U32(p);
                bool ok = e == outp.U32(p + 4) && e >= EidMin && e < EidMax && e > prevE
                          && (prevP < 0 || (e - prevE <= MaxEidGap && p - prevP <= MaxRecGap));
                if (ok)
                {
                    if (prevP >= 0) lastDelta = p - prevP;
                    runLen++; runLastP = p; runLastE = e;
                    runLastStride = (lastDelta == 8 || lastDelta == 512) ? lastDelta : 28;
                    prevE = e; prevP = p; p += 8;
                }
                else
                {
                    if (prevP >= 0 && p - prevP > MaxRecGap)
                    {
                        Commit();
                        prevE = 0; prevP = -1; runLen = 0;
                    }
                    p += 4;
                }
            }
            Commit();
        }

        eLast = bestE;
        int stride = (bestStride == 8 || bestStride == 512) ? bestStride : 28;
        rEnd = Math.Min(bestP + stride, outp.Count);
    }

    // Decodes with Huffman table H starting at bit offset boff. Matches may reach back into
    // the already-recovered window `out`. Throws DesyncException on any inconsistency.
    // untilEob=false: stop once buf reaches `limit` bytes (or an EOB is hit). endBit set.
    private static void DecodeWithH(byte[] raw, Huff lit, Huff dist, ByteBuf outp, long boff,
                                    int limit, bool untilEob, ByteBuf buf, out long endBit)
    {
        var br = new BitReader(raw, boff);
        buf.Clear();
        int w = outp.Count;
        while (untilEob || buf.Count < limit)
        {
            int sym = lit.Decode(br);
            if (sym < 256) buf.Add((byte)sym);
            else if (sym == 256) { endBit = br.BitPos; return; }
            else
            {
                int i = sym - 257;
                if (i >= LenBase.Length) Bail();
                int length = LenBase[i] + (int)br.Get(LenExtra[i]);
                int ds = dist.Decode(br);
                if (ds >= DistBase.Length) Bail();
                int d = DistBase[ds] + (int)br.Get(DistExtra[ds]);
                if (d > buf.Count + w) Bail();
                for (int k = 0; k < length; k++)
                {
                    int src = buf.Count - d;
                    buf.Add(src >= 0 ? buf.Arr[src] : outp.Arr[w + src]);
                }
            }
        }
        endBit = br.BitPos;
    }

    private static void ScoreBand(ByteBuf buf, uint eLast, out int nb, out int asc, out long first)
    {
        uint lo = eLast + 1, hi = eLast + BandWidth;
        int i = 0; nb = 0; asc = 0; first = -1; uint last = 0; bool haveLast = false;
        while (i < buf.Count - 8)
        {
            uint e = buf.U32(i);
            if (e == buf.U32(i + 4) && e >= lo && e <= hi)
            {
                if (first < 0) first = e;
                nb++;
                if (haveLast && e >= last) asc++;
                last = e; haveLast = true;
                i += 8;
            }
            else i++;
        }
    }

    private readonly struct Key : IComparable<Key>
    {
        public readonly int Exact, Seam; public readonly long NbAsc, NegFirst, NegDist;
        public Key(int exact, int seam, long nbAsc, long negFirst, long negDist)
        { Exact = exact; Seam = seam; NbAsc = nbAsc; NegFirst = negFirst; NegDist = negDist; }
        public int CompareTo(Key o)
        {
            int c = Exact.CompareTo(o.Exact); if (c != 0) return c;
            c = Seam.CompareTo(o.Seam); if (c != 0) return c;
            c = NbAsc.CompareTo(o.NbAsc); if (c != 0) return c;
            c = NegFirst.CompareTo(o.NegFirst); if (c != 0) return c;
            return NegDist.CompareTo(o.NegDist);
        }
    }

    private static long? RelockOffset(byte[] raw, Huff lit, Huff dist, ByteBuf outp, long infoBit,
                                      uint eLast, byte[] neededPrefix, int probe, int lo, int hi)
    {
        long? best = null; Key bestKey = default; bool have = false;
        var buf = new ByteBuf(probe + 257, probe + 257);
        RelockStats? st = null;
        List<(Key K, long Boff, int Nb, int Asc, long First, bool Exact)>? all = null;
        var sw = Diag != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        if (Diag != null)
        {
            st = new RelockStats { InfoBit = infoBit, Lo = lo, Hi = hi, Probe = probe };
            all = new List<(Key, long, int, int, long, bool)>();
            DiagRelocks.Add(st);
        }
        for (long boff = infoBit + lo; boff < infoBit + hi; boff++)
        {
            if (boff < 0) continue;
            if (st != null) st.Candidates++;
            try { DecodeWithH(raw, lit, dist, outp, boff, probe, false, buf, out _); }
            catch (Exception ex) when (ex is DesyncException or OutputLimitExceededException)
            { if (st != null) { st.DecodeFailed++; DiagProbeSymbolBudget += buf.Count; if (buf.Count > DiagMaxCandidateProbeBytes) DiagMaxCandidateProbeBytes = buf.Count; } continue; }
            if (st != null) { DiagProbeSymbolBudget += buf.Count; if (buf.Count > DiagMaxCandidateProbeBytes) DiagMaxCandidateProbeBytes = buf.Count; }
            if (buf.Count < neededPrefix.Length + 16) { if (st != null) st.TooShort++; continue; }
            if (st != null) st.Survived++;

            bool exact = true;
            for (int j = 0; j < neededPrefix.Length; j++)
                if (buf.Arr[j] != neededPrefix[j]) { exact = false; break; }

            ScoreBand(buf, eLast, out int nb, out int asc, out long first);
            if (st != null && exact) st.ExactPrefix++;
            if (!exact && nb < MinRecords) { if (st != null) st.BandRejected++; continue; }
            if (st != null) st.Scored++;

            bool seam = first == eLast + 1;
            long firstTie = first < 0 ? long.MaxValue : first;
            var key = new Key(exact ? 1 : 0, seam ? 1 : 0, nb + asc, -firstTie, -Math.Abs(boff - infoBit));
            all?.Add((key, boff, nb, asc, first, exact));
            if (!have || key.CompareTo(bestKey) > 0) { bestKey = key; best = boff; have = true; }
        }
        if (st != null && sw != null)
        {
            st.Ms = sw.Elapsed.TotalMilliseconds;
            if (all != null)
            {
                all.Sort((a, b) => b.K.CompareTo(a.K));
                for (int i = 0; i < Math.Min(8, all.Count); i++)
                {
                    var c = all[i];
                    st.Top.Add($"boff={c.Boff} (delta={c.Boff - infoBit}) exact={(c.Exact ? 1 : 0)} " +
                               $"seam={(c.First == eLast + 1 ? 1 : 0)} nb={c.Nb} asc={c.Asc} first={c.First}");
                }
            }
        }
        return best;
    }

    private static byte[] Le32(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    /// <summary>Parses the 10-byte-plus gzip member header and returns the byte offset where
    /// the DEFLATE data begins. Deliberately does NOT look at the gzip trailer (CRC32/ISIZE):
    /// the OLE <c>Global/ElemTable</c> stream is <c>[header][gzip member][trailing
    /// bytes]</c>, so the trailer does not sit at <c>raw.Length-8</c>. The member's true extent
    /// - and thus its own trailer - is located in <c>Recover</c> as a byproduct of
    /// inflating to the member's actual <c>BFINAL</c> block, never by hardcoding an offset.</summary>
    private static bool TryParseGzipHeader(byte[] raw, int gzStart, out int deflateStart)
    {
        deflateStart = 0;
        if (gzStart < 0 || gzStart > raw.Length - 10) return false;
        if (raw[gzStart] != 0x1F || raw[gzStart + 1] != 0x8B || raw[gzStart + 2] != 8) return false;

        int flg = raw[gzStart + 3];
        if ((flg & 0xE0) != 0) return false;
        int limit = raw.Length;   // only a bound against reading past the buffer; NOT the trailer
        int pos = gzStart + 10;

        if ((flg & 0x04) != 0)
        {
            if (pos + 2 > limit) return false;
            int xlen = raw[pos] | (raw[pos + 1] << 8);
            pos += 2;
            if (xlen > limit - pos) return false;
            pos += xlen;
        }

        if ((flg & 0x08) != 0 && !SkipZeroTerminated(raw, ref pos, limit)) return false;
        if ((flg & 0x10) != 0 && !SkipZeroTerminated(raw, ref pos, limit)) return false;
        if ((flg & 0x02) != 0)
        {
            if (pos + 2 > limit) return false;
            pos += 2;
        }
        if (pos >= limit) return false;

        deflateStart = pos;
        return true;
    }

    /// <summary>Rounds a bit position up to the next byte boundary, in bits. Gzip requires the
    /// trailer (CRC32/ISIZE) to start on a byte boundary immediately after the deflate data.</summary>
    private static long AlignUpToByte(long bitPos) => (bitPos & 7) == 0 ? bitPos : (bitPos + 7) & ~7L;

    private static bool SkipZeroTerminated(byte[] raw, ref int pos, int limit)
    {
        while (pos < limit && raw[pos] != 0) pos++;
        if (pos >= limit) return false;
        pos++;
        return true;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint crc = value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[value] = crc;
        }
        return table;
    }

    /// <summary>Diagnostics from a <see cref="Recover(byte[], int)"/>
    /// call: every field is either read directly from the stream at the offset the recovery
    /// itself located, or counted during the run.</summary>
    internal sealed class RecoverOutcome
    {
        /// <summary>Whether the recovery reached the gzip member's own BFINAL block, and so was
        /// able to locate a real trailer at all (independent of whether that trailer's CRC then
        /// matched the recovered bytes).</summary>
        public bool TrailerFound;
        /// <summary>Byte offset of the located trailer (CRC32 first, then ISIZE) within
        /// <c>raw</c>, or -1 if none was located.</summary>
        public int TrailerOffset = -1;
        public uint TrueIsize;
        public uint TrueCrc;
        /// <summary>Bytes actually produced by the inflater, regardless of acceptance.</summary>
        public int RecoveredLength;
        /// <summary>Number of bit-desyncs (spurious stored-block headers) the recovery relocked
        /// past. Zero means the stream decoded straight through to BFINAL.</summary>
        public int DesyncCount;
        /// <summary>True only when the recovered bytes' own CRC32 matches the trailer's.</summary>
        public bool Accepted;
        /// <summary>The limit that stopped the recovery (code and value), or null when no limit did.</summary>
        public (string Limit, long Value)? LimitHit;
        /// <summary>Human-readable reason for the outcome.</summary>
        public string Reason = "";
    }

    /// <summary>Hard safety cap on recovered output size, independent of any per-file ISIZE claim (such a claim
    /// cannot be trusted before the member's real trailer has been located); generous relative to the largest
    /// ElemTable while still bounding a pathological or adversarial stream.</summary>
    internal const int DefaultMaxRecoveredBytes = 256 * 1024 * 1024;

    /// <summary>Limit of the desync relock loop: a damaged table that needs more relocks than this is not recovered.</summary>
    internal const int DefaultMaxIterations = 4096;

    /// <summary>The limits in force: the defaults, or with <paramref name="raise"/> the platform array length and no loop guard
    /// (every iteration advances the bit position, so the loop still ends).</summary>
    internal static (int MaxBytes, int MaxIterations) Limits(bool raise) =>
        raise ? (Array.MaxLength, int.MaxValue) : (DefaultMaxRecoveredBytes, DefaultMaxIterations);

    /// <summary>Convenience overload for callers that only need the payload.</summary>
    public static byte[]? Recover(byte[] raw, int gzStart) => Recover(raw, gzStart, out _);

    /// <summary>
    /// Recovers the full uncompressed ElemTable from a bit-corrupted gzip member, or returns
    /// null when the recovered bytes don't verify. <paramref name="raw"/> is the OLE stream
    /// bytes; <paramref name="gzStart"/> is the offset of the 0x1F 0x8B gzip magic.
    ///
    /// The gzip member's trailer (CRC32 + ISIZE) is located by running the inflater - with the
    /// same desync relock used to recover the payload - through to the member's own BFINAL
    /// block, then reading the 8 bytes at the next byte boundary. The result is accepted only
    /// when the CRC32 of the recovered bytes matches that trailer, never by comparing lengths,
    /// since a corrupt ISIZE must not gate acceptance either way.
    /// </summary>
    public static byte[]? Recover(byte[] raw, int gzStart, out RecoverOutcome outcome, (int MaxBytes, int MaxIterations)? limits = null)
    {
        outcome = new RecoverOutcome();
        var (maxRecoveredBytes, maxIterations) = limits ?? Limits(false);
        var swAll = Diag != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            if (!TryParseGzipHeader(raw, gzStart, out int deflateByteStart))
            {
                outcome.Reason = "gzip header parse failed";
                Diag?.WriteLine("[recover] TryParseGzipHeader FAILED");
                return null;
            }
            long deflateStart = (long)deflateByteStart * 8;
            Diag?.WriteLine($"[recover] raw={raw.Length} gzStart={gzStart} deflateByteStart={deflateByteStart}");

            var outp = new ByteBuf(1 << 20, maxRecoveredBytes);
            var br = new BitReader(raw, deflateStart);

            int guard = 0;
            int desyncCount = 0;
            DesyncInfo? finalInfo = null;
            while (true)
            {
                if (++guard > maxIterations) { outcome.Reason = $"guard exceeded ({maxIterations} loop iterations)"; outcome.LimitHit = ("elem-table-recovery-iterations", maxIterations); Diag?.WriteLine($"[recover] STOP: guard {maxIterations} exceeded"); break; }
                var info = InflateUntilDesync(br, outp);
                if (Diag != null)
                    Diag.WriteLine($"[desync #{guard}] why={info.Why} bit={info.Bit} (byte={info.Bit / 8}.{info.Bit % 8}) " +
                                   $"out={outp.Count} haveHuff={(info.Lit != null)} t={swAll!.Elapsed.TotalSeconds:F1}s");
                if (info.Why == Why.BFinal) { finalInfo = info; break; }
                if (info.Why == Why.RanOut || info.Why == Why.Btype3)
                { outcome.Reason = $"stopped: {info.Why} before reaching the member's BFINAL block"; break; }
                if (info.Lit == null || info.Dist == null)
                { outcome.Reason = "no Huffman table carried into desync"; Diag?.WriteLine("[recover] STOP: no Huffman table carried into desync"); break; }

                desyncCount++;
                var swFind = Diag != null ? System.Diagnostics.Stopwatch.StartNew() : null;
                FindLastGoodRecord(outp, out int rEnd, out uint eLast);
                Diag?.WriteLine($"  [findlast] rEnd={rEnd} eLast={eLast} (out={outp.Count}, tail gap={outp.Count - rEnd}) " +
                                $"in {swFind!.Elapsed.TotalMilliseconds:F0}ms");
                if (eLast == 0) { outcome.Reason = "FindLastGoodRecord found no anchor"; Diag?.WriteLine("[recover] STOP: FindLastGoodRecord found no anchor (eLast==0)"); break; }

                byte[] expected = new byte[8];
                Buffer.BlockCopy(Le32(eLast + 1), 0, expected, 0, 4);
                Buffer.BlockCopy(Le32(eLast + 1), 0, expected, 4, 4);

                int cp = 0;
                while (cp < 8 && rEnd + cp < outp.Count && outp.Arr[rEnd + cp] == expected[cp]) cp++;
                int p = rEnd + cp;
                var neededPrefix = new byte[8 - cp];
                Array.Copy(expected, cp, neededPrefix, 0, 8 - cp);
                outp.Truncate(p);
                Diag?.WriteLine($"  [anchor] eLast+1={eLast + 1} carriedPrefixBytes={cp} truncateTo={p} " +
                                $"neededPrefix={BitConverter.ToString(neededPrefix)}");

                if (Diag != null && DiagWideAt == guard)
                    DiagRunWideScan(raw, info.Lit, info.Dist, outp, info.Bit, eLast, neededPrefix);

                long? boff;
                if (Diag != null && DiagForceRelock.TryGetValue(guard, out long forced))
                {
                    boff = info.Bit + forced; // forced candidate for sweeps
                    Diag.WriteLine($"  [relock] FORCED delta={forced} -> boff={boff}");
                }
                else
                {
                    boff = RelockOffset(raw, info.Lit, info.Dist, outp, info.Bit, eLast, neededPrefix, ProbeBytes, -256, 1400);
                    DiagDumpLastRelock("pass1");
                    if (boff == null)
                    {
                        boff = RelockOffset(raw, info.Lit, info.Dist, outp, info.Bit, eLast, neededPrefix, 9000, -512, 6000);
                        DiagDumpLastRelock("pass2");
                    }
                }
                if (boff == null)
                {
                    outcome.Reason = "relock failed (no acceptable candidate in either pass)";
                    Diag?.WriteLine("[recover] STOP: relock failed (no acceptable candidate in either pass)");
                    if (Diag != null && DiagWideScan) DiagRunWideScan(raw, info.Lit, info.Dist, outp, info.Bit, eLast, neededPrefix);
                    break;
                }
                Diag?.WriteLine($"  [relock] chose boff={boff.Value} (delta={boff.Value - info.Bit})");

                // The tail buffer's cap is the shared safety cap minus what is already recovered,
                // not derived from any per-file expected size (see DefaultMaxRecoveredBytes).
                int remaining = maxRecoveredBytes - outp.Count;
                if (remaining <= 0) { outcome.Reason = "hit the output safety cap"; outcome.LimitHit = ("elem-table-recovery-bytes", maxRecoveredBytes); Diag?.WriteLine("[recover] STOP: remaining <= 0"); break; }
                var tail = new ByteBuf(Math.Min(1 << 20, remaining), remaining);
                DecodeWithH(raw, info.Lit, info.Dist, outp, boff.Value, 0, true, tail, out long endBit);
                Diag?.WriteLine($"  [resume] tail={tail.Count} endBit={endBit} (delta={endBit - info.Bit})");
                if (endBit <= info.Bit)
                {
                    outcome.Reason = "resume did not advance past the desync";
                    Diag?.WriteLine("[recover] FAIL: endBit <= info.Bit");
                    break;
                }
                outp.AddRange(tail.Arr, 0, tail.Count);
                br = new BitReader(raw, endBit);
            }

            outcome.DesyncCount = desyncCount;
            outcome.RecoveredLength = outp.Count;
            if (Diag != null) DiagPartialOutput = outp.ToArray();

            if (finalInfo == null)
            {
                if (outcome.Reason.Length == 0) outcome.Reason = "did not reach the gzip member's own BFINAL block";
                Diag?.WriteLine($"[recover] RESULT REJECTED: {outcome.Reason} (recovered={outp.Count} bytes, desyncs={desyncCount})");
                return null;
            }

            // Locate the member's real trailer: the byte immediately following BFINAL's end,
            // rounded up to the next byte boundary. No offset is assumed or hardcoded.
            long trailerBit = AlignUpToByte(finalInfo.EndBit);
            int trailerOffset = (int)(trailerBit / 8);
            if (trailerOffset < 0 || trailerOffset + 8 > raw.Length)
            {
                outcome.Reason = $"trailer offset {trailerOffset} out of range (raw length {raw.Length})";
                Diag?.WriteLine($"[recover] RESULT REJECTED: {outcome.Reason}");
                return null;
            }

            outcome.TrailerFound = true;
            outcome.TrailerOffset = trailerOffset;
            outcome.TrueCrc = BitConverter.ToUInt32(raw, trailerOffset);
            outcome.TrueIsize = BitConverter.ToUInt32(raw, trailerOffset + 4);
            Diag?.WriteLine($"[recover] trailer at byte {trailerOffset}: true ISIZE={outcome.TrueIsize} true CRC=0x{outcome.TrueCrc:X8}");

            byte[] recovered = outp.ToArray();
            uint actualCrc = ComputeCrc32(recovered);
            if (Diag != null)
                Diag.WriteLine($"[recover] loop finished: recovered={recovered.Length} bytes, desyncs={desyncCount}, " +
                               $"elapsed={swAll!.Elapsed.TotalSeconds:F1}s");

            if (actualCrc == outcome.TrueCrc)
            {
                outcome.Accepted = true;
                outcome.Reason = "CRC match";
                Diag?.WriteLine("[recover] RESULT ACCEPTED: CRC match");
                return recovered;
            }

            outcome.Accepted = false;
            outcome.Reason = $"CRC mismatch (recovered length {recovered.Length}, computed CRC 0x{actualCrc:X8})";
            Diag?.WriteLine($"[recover] RESULT REJECTED: {outcome.Reason}");
            return null;
        }
        catch (Exception ex)
        {
            if (ex is OutputLimitExceededException) outcome.LimitHit = ("elem-table-recovery-bytes", maxRecoveredBytes);
            outcome.Reason = $"exception {ex.GetType().Name}: {ex.Message}";
            Diag?.WriteLine($"[recover] RESULT REJECTED: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            Diag?.WriteLine(ex.StackTrace);
            return null;
        }
    }

    // Diagnostics: wide relaxed scan around a failed relock
    internal static bool DiagWideScan;
    internal static int DiagWideAt = -1;   // run the wide scan at this desync index regardless of relock success
    internal static int DiagWideLo = -4096, DiagWideHi = 40000;
    internal static byte[]? DiagPartialOutput;

    private static void DiagRunWideScan(byte[] raw, Huff lit, Huff dist, ByteBuf outp,
                                        long infoBit, uint eLast, byte[] neededPrefix)
    {
        var w = Diag!;
        w.WriteLine($"  [wide] scanning bit offsets [{DiagWideLo},{DiagWideHi}) around infoBit={infoBit}");
        const int Probe = 6000;
        var buf = new ByteBuf(Probe + 257, Probe + 257);
        var hist = new int[8];   // 0,1-9,10-99,100-999,1000-2999,3000-5999,>=6000, (unused)
        var hits = new List<(long Boff, int Bytes, int Nb, int Asc, long First, bool Exact)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        for (long boff = infoBit + DiagWideLo; boff < infoBit + DiagWideHi; boff++)
        {
            if (boff < 0) continue;
            n++;
            try { DecodeWithH(raw, lit, dist, outp, boff, Probe, false, buf, out _); }
            // Expected: most offsets are not a valid stream start; the partial output is still counted.
            catch (Exception ex) when (ex is DesyncException or OutputLimitExceededException) { }
            int c = buf.Count;
            int b = c == 0 ? 0 : c < 10 ? 1 : c < 100 ? 2 : c < 1000 ? 3 : c < 3000 ? 4 : c < Probe ? 5 : 6;
            hist[b]++;
            if (c < neededPrefix.Length + 16) continue;
            bool exact = true;
            for (int j = 0; j < neededPrefix.Length; j++)
                if (buf.Arr[j] != neededPrefix[j]) { exact = false; break; }
            ScoreBand(buf, eLast, out int nb, out int asc, out long first);
            if (exact || nb > 0 || c >= Probe) hits.Add((boff, c, nb, asc, first, exact));
        }
        w.WriteLine($"  [wide] tried={n} in {sw.Elapsed.TotalSeconds:F1}s  bytes-decoded histogram: " +
                    $"0={hist[0]} 1-9={hist[1]} 10-99={hist[2]} 100-999={hist[3]} 1000-2999={hist[4]} " +
                    $"3000-5999={hist[5]} full({Probe})={hist[6]}");
        w.WriteLine($"  [wide] exact-prefix hits: {hits.Count(h => h.Exact)}; any-in-band hits: {hits.Count(h => h.Nb > 0)}");
        foreach (var h in hits.OrderByDescending(h => (h.Exact ? 1 : 0)).ThenByDescending(h => h.Nb + h.Asc)
                             .ThenByDescending(h => h.Bytes).Take(20))
            w.WriteLine($"    wide: boff={h.Boff} delta={h.Boff - infoBit} bytes={h.Bytes} exact={(h.Exact ? 1 : 0)} nb={h.Nb} asc={h.Asc} first={h.First}");

        // Deep-run the most promising offsets: how far does each decode before EOB/error?
        var deep = hits.OrderByDescending(h => h.Nb + h.Asc).ThenByDescending(h => h.Bytes).Take(12).ToList();
        if (deep.Count > 0)
        {
            w.WriteLine("  [wide-deep] decode-until-EOB length for the 12 most promising offsets:");
            var big = new ByteBuf(1 << 16, 4_000_000);
            foreach (var h in deep)
            {
                string how = "EOB";
                try { DecodeWithH(raw, lit, dist, outp, h.Boff, 0, true, big, out _); }
                catch (DesyncException) { how = "desync"; }
                catch (OutputLimitExceededException) { how = "cap4MB"; }
                w.WriteLine($"    deep: boff={h.Boff} delta={h.Boff - infoBit} produced={big.Count} stop={how}");
            }
        }
    }

    /// <summary>Exposes the gzip CRC32 for diagnostics.</summary>
    internal static uint DiagCrc32(byte[] d) => ComputeCrc32(d);

    /// <summary>Every prefix length of <paramref name="d"/> whose CRC32 equals
    /// <paramref name="target"/>. O(n) - the running CRC is finalized and compared at each step.</summary>
    internal static List<int> DiagCrcPrefixMatches(byte[] d, uint target)
    {
        var hits = new List<int>();
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < d.Length; i++)
        {
            crc = Crc32Table[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
            if ((crc ^ 0xFFFFFFFF) == target) hits.Add(i + 1);
        }
        return hits;
    }

    /// <summary>Structural census of a decoded buffer against the relock grammar.</summary>
    internal static string DiagRecordCensus(byte[] d, int from, int to)
    {
        int pairs = 0, inRange = 0, ascending = 0;
        var strideHist = new Dictionary<int, int>();
        uint prevE = 0; int prevP = -1;
        for (int p = from; p + 8 <= to; p += 4)
        {
            uint e = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
            uint e2 = (uint)(d[p + 4] | (d[p + 5] << 8) | (d[p + 6] << 16) | (d[p + 7] << 24));
            if (e != e2 || e == 0) continue;
            pairs++;
            if (e < EidMin || e >= EidMax) continue;
            inRange++;
            if (prevP >= 0 && e > prevE)
            {
                ascending++;
                int stride = p - prevP;
                strideHist.TryGetValue(stride, out int cc); strideHist[stride] = cc + 1;
            }
            prevE = e; prevP = p;
        }
        var top = strideHist.OrderByDescending(kv => kv.Value).Take(8)
                            .Select(kv => $"{kv.Key}x{kv.Value}");
        return $"range=[{from},{to}) dupPairs={pairs} inEidRange={inRange} ascending={ascending} " +
               $"lastEid={prevE} strides: {string.Join(' ', top)}";
    }

    private static void DiagDumpLastRelock(string tag)
    {
        if (Diag == null || DiagRelocks.Count == 0) return;
        var s = DiagRelocks[^1];
        Diag.WriteLine($"  [relock-{tag}] range=[{s.Lo},{s.Hi}) probe={s.Probe} cand={s.Candidates} " +
                       $"decodeFail={s.DecodeFailed} tooShort={s.TooShort} survived={s.Survived} " +
                       $"exact={s.ExactPrefix} bandRejected={s.BandRejected} scored={s.Scored} " +
                       $"in {s.Ms:F0}ms");
        foreach (var t in s.Top) Diag.WriteLine($"    top: {t}");
    }
}
