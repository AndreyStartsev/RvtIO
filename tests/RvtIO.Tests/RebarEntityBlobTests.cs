using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>A walk that stops behind an extensible storage entity is cut to the objects before the entity payloads (those are read where they lie);
/// a stop inside that part, or a walk without an entity, has no such part.</summary>
public class RebarEntityBlobTests
{
    private const int GuidId = 1, PairId = 2, EntityId = 3, CellId = 4, RootId = 5, MidId = 6, TailId = 7;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null) => new(name, type, mods, 0, null, reference, null, null);

    private static SchemaDocument Schema() => new()
    {
        Classes =
        [
            new(GuidId, "GUIDvalue", 0, 2, [Field("m_guid", 9)], []),
            new(PairId, "std::pair< GUIDvalue, ESEntity >", 0, 0, [Field("first", 14, 0, GuidId), Field("second", 14, 0, EntityId)], []),
            new(EntityId, "ESEntity", 0, 1, [Field("m_blob", 14, 1)], []),
            new(CellId, "ESEntityCell", 0, 3, [Field("m_entityMap", 14, 80, PairId)], []),
            new(RootId, "Root", 0, 1, [Field("m_cell", 14, 1), Field("m_mid", 14, 1), Field("m_value", 4)], []),
            new(MidId, "Mid", 0, 1, [Field("m_tail", 14, 1)], []),
            new(TailId, "Tail", 0, 1, [Field("m_items", 14, 81)], []),
        ],
    };

    /// <summary>Root queues a cell (empty entity) and a Mid; Mid queues a Tail. With <paramref name="payloadFirst"/> the bytes of an entity payload lie in
    /// front of the Tail, where a walk that does not know them reads its count; without it the Tail is where the walk looks for it.</summary>
    private static WideBlockWalker.Walk Walk(bool payloadFirst, bool entity = true)
    {
        var guid = Enumerable.Range(0x20, 16).Select(i => (byte)i).ToArray();
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)RootId);
        w.Write(0xFFFFFFFFu); w.Write((ushort)CellId);
        w.Write(0xFFFFFFFFu); w.Write((ushort)MidId);
        w.Write(42);                                              // m_value
        if (entity) { w.Write(1u); w.Write(guid); w.Write(0xFFFFFFFFu); w.Write(guid); } else w.Write(0u);
        w.Write(0xFFFFFFFFu); w.Write((ushort)TailId);            // Mid.m_tail
        if (payloadFirst) w.Write(Enumerable.Repeat((byte)0x7F, 12).ToArray());   // the payload of the entity
        w.Write(0u);                                              // Tail: no items
        var block = stream.ToArray();
        return new WideBlockWalker(Schema(), (d, f) => d.Name + "." + f.Name, []).Run(block, 0, block.Length, RootId);
    }

    [Fact]
    public void BeforeEntityBlobs_CutsAWalkThatStoppedBehindTheEntity()
    {
        var walk = Walk(payloadFirst: true);
        Assert.NotNull(walk.Error);
        var cut = walk.BeforeEntityBlobs();
        Assert.NotNull(cut);
        // Root queued the cell and Mid: both were queued when the cell started, the Tail was queued by Mid
        Assert.Equal(2, cut.Queue.Count);
        Assert.Null(cut.Error);
        Assert.DoesNotContain(cut.Captures, c => c.QueueIndex >= 2);
        Assert.Contains(cut.Captures, c => c.Key == "Root.m_value" && c.Integer == 42);
    }

    [Fact]
    public void BeforeEntityBlobs_IsNullForAWalkThatIsWhole()
    {
        var walk = Walk(payloadFirst: false);
        Assert.Null(walk.Error);
        Assert.Null(walk.BeforeEntityBlobs());
    }

    [Fact]
    public void BeforeEntityBlobs_IsNullWithoutAnEntity()
    {
        var walk = Walk(payloadFirst: true, entity: false);
        Assert.NotNull(walk.Error);
        Assert.Null(walk.BeforeEntityBlobs());
    }
}
