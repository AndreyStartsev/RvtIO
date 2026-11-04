using RvtIO;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The allocation-free record chunk GUID scan gives the same map as the per-offset byte[16] + Guid scan it replaced.</summary>
public class RecordChunkGuidScanTests
{
    private static Dictionary<Guid, uint> Reference(IEnumerable<byte[]> chunks, Dictionary<Guid, string> captions, HashSet<uint> known)
    {
        var map = new Dictionary<Guid, uint>();
        foreach (var chunk in chunks)
            for (int i = 0; i + 20 <= chunk.Length; i += 2)
            {
                var gb = new byte[16];
                Array.Copy(chunk, i, gb, 0, 16);
                var g = new Guid(gb);
                if (!captions.ContainsKey(g)) continue;
                uint eid = BitConverter.ToUInt32(chunk, i + 16);
                if (!known.Contains(eid) || eid < 100) continue;
                map[g] = eid;
            }
        return map;
    }

    private static Dictionary<Guid, uint> Actual(IEnumerable<byte[]> chunks, Dictionary<Guid, string> captions, HashSet<uint> known)
    {
        var map = new Dictionary<Guid, uint>();
        var prefixes = RvtFileReader.BuildGuidPrefixSet(captions.Keys);
        foreach (var chunk in chunks) RvtFileReader.MapRecordChunkGuids(chunk, captions, prefixes, known, map);
        return map;
    }

    [Fact]
    public void Random_chunks_with_planted_guids_match_the_reference()
    {
        var random = new Random(7);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var captions = new Dictionary<Guid, string>();
            for (int k = random.Next(0, 6); k > 0; k--) captions[Guid.NewGuid()] = "c" + k;
            var known = new HashSet<uint>(Enumerable.Range(0, 40).Select(_ => (uint)random.Next(0, 400)));
            var chunks = new List<byte[]>();
            for (int c = random.Next(1, 4); c > 0; c--)
            {
                var chunk = new byte[random.Next(0, 600)];
                random.NextBytes(chunk);
                foreach (var g in captions.Keys)
                    for (int plant = random.Next(0, 3); plant > 0 && chunk.Length >= 20; plant--)
                    {
                        int at = random.Next(0, chunk.Length - 19);   // odd offsets must stay unmatched, even ones match
                        g.TryWriteBytes(chunk.AsSpan(at, 16));
                        BitConverter.TryWriteBytes(chunk.AsSpan(at + 16, 4), known.Count > 0 ? known.ElementAt(random.Next(known.Count)) : 0u);
                    }
                chunks.Add(chunk);
            }
            var expected = Reference(chunks, captions, known);
            var actual = Actual(chunks, captions, known);
            Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        }
    }

    [Fact]
    public void Empty_captions_map_nothing_and_short_chunks_are_safe()
    {
        var map = Actual([new byte[19], new byte[20], []], new Dictionary<Guid, string>(), [1u]);
        Assert.Empty(map);
        var g = Guid.NewGuid();
        var chunk = new byte[20];
        g.TryWriteBytes(chunk);
        BitConverter.TryWriteBytes(chunk.AsSpan(16, 4), 150u);
        Assert.Equal(150u, Actual([chunk], new() { [g] = "x" }, [150u])[g]);
        Assert.Empty(Actual([chunk], new() { [g] = "x" }, [99u]));
    }
}
