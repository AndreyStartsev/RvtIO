using System.Text;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The layers of a RebarSystem are read whether or not an extensible storage entity lies in front of them.</summary>
public class RebarInSystemMemberTests
{
    private const int IdentifierId = 1, ElementIdId = 2, GuidId = 3, PairId = 4, EntityId = 5, CellId = 6, CurveId = 7, LineId = 8, LayerId = 9, SystemId = 10;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null, int? fixedCount = null) =>
        new(name, type, mods, 0, fixedCount, reference, null, null);

    private static SchemaDocument Schema() => new()
    {
        Classes =
        [
            new(IdentifierId, "Identifier", 0, 2, [Field("m_id64", 11)], []),
            new(ElementIdId, "ElementId", 0, 1, [Field("m_id", 14, 0, IdentifierId)], []),
            new(GuidId, "GUIDvalue", 0, 2, [Field("m_guid", 9)], []),
            new(PairId, "std::pair< GUIDvalue, ESEntity >", 0, 0, [Field("first", 14, 0, GuidId), Field("second", 14, 0, EntityId)], []),
            new(EntityId, "ESEntity", 0, 1, [Field("m_blob", 14, 1)], []),
            new(CellId, "ESEntityCell", 0, 3, [Field("m_entityMap", 14, 80, PairId)], []),
            new(CurveId, "GCurve", 0, 1, [Field("m_endParams", 7, 16, null, 2)], []),
            new(LineId, "GLine", CurveId, 1, [Field("m_origin", 7, 16, null, 3), Field("m_dirVec", 7, 16, null, 3)], []),
            new(LayerId, "RebarSystemLayer", 0, 1,
                [Field("m_barType", 14, 0, ElementIdId), Field("m_uncutCurves", 14, 81), Field("m_uncutRebarInstances", 14, 81)], []),
            new(SystemId, "RebarSystem", 0, 1, [Field("m_pCell", 14, 2), Field("m_layer", 14, 0, LayerId)], []),
        ],
    };

    /// <summary>A system of one layer with one bar line; the entity cell in front of the line is empty or absent.</summary>
    private static List<object>? ReadLayers(bool emptyEntity)
    {
        var guid = Enumerable.Range(0x20, 16).Select(i => (byte)i).ToArray();
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)SystemId);
        if (emptyEntity) { w.Write(0xFFFFFFFFu); w.Write((ushort)CellId); } else w.Write(0u);
        w.Write(9L);                                                                   // bar type
        w.Write(1u); w.Write(0xFFFFFFFFu); w.Write((ushort)LineId);                    // one bar line
        w.Write(0u);                                                                   // no instances
        if (emptyEntity) { w.Write(1u); w.Write(guid); w.Write(0xFFFFFFFFu); w.Write(guid); }
        foreach (double v in new[] { 0.0, 2.0, 0, 0, 0, 1, 0, 0 }) w.Write(v);          // the line
        var block = stream.ToArray();
        var walker = new WideBlockWalker(Schema(), RebarReader.Key, []);
        var walk = walker.Run(block, 0, block.Length, SystemId);
        Assert.True(walk.Exact, walk.Error);
        var byQueue = new Dictionary<int, List<WideBlockWalker.Capture>>();
        foreach (var c in walk.Captures)
        {
            if (!byQueue.TryGetValue(c.QueueIndex, out var list)) byQueue[c.QueueIndex] = list = [];
            list.Add(c);
        }
        return RebarReader.ReadLayers(walker, walk, byQueue)?.Cast<object>().ToList();
    }

    [Fact]
    public void ReadLayers_ReadsASystemWithoutAnEntity() => Assert.Single(ReadLayers(emptyEntity: false)!);

    [Fact]
    public void ReadLayers_ReadsASystemBehindAnEmptyEntity() => Assert.Single(ReadLayers(emptyEntity: true)!);
}
