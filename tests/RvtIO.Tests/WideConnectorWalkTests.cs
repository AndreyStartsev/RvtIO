using System.Text;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The connectors of an element in a file with its own schema (Revit 2020-2021, 2023 and later): an empty extensible storage entity
/// in front of them does not stop the field walk, and each connector keeps its own mode and calculation (a connector without one, such as a
/// communication or surface connector, takes none of a later connector).</summary>
public class WideConnectorWalkTests
{
    private const int IdentifierId = 1, ElementIdId = 2, GuidId = 3, PairId = 4, EntityId = 5, CellId = 6, RootId = 7, ConnectorId = 8,
        ConnectorIdItemId = 9, SegmentCalculationId = 10, ElectricalCalculationId = 11;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null) =>
        new(name, type, mods, 0, null, reference, null, null);

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
            new(RootId, "FamilyInstance", 0, 1,
                [Field("m_pCell", 14, 2), Field("m_pFirst", 14, 2), Field("m_pSecond", 14, 2), Field("m_pThird", 14, 2)], []),
            new(ConnectorId, "Connector", 0, 7,
                [Field("m_arrRefs", 14, 80, ConnectorIdItemId), Field("m_nIndex", 4), Field("m_mode", 4), Field("m_pCalc", 14, 2)], []),
            new(ConnectorIdItemId, "ConnectorId", 0, 7, [Field("m_id", 14, 0, ElementIdId), Field("m_nIndex", 4), Field("m_connType", 4)], []),
            new(SegmentCalculationId, "SegmentConnectorCalculation", 0, 1,
                [Field("m_idSystem", 14, 0, ElementIdId), Field("m_nSection", 4), Field("m_eSystemType", 4), Field("m_eDirection", 4)], []),
            new(ElectricalCalculationId, "MEPFamilyConnectorCalculationElectrical", 0, 1,
                [Field("m_idSystem", 14, 0, ElementIdId), Field("m_idDownstreamCircuit", 14, 0, ElementIdId)], []),
        ],
    };

    private static readonly WideNativeParams Params = new(Schema());

    private static readonly byte[] SchemaGuid = Enumerable.Range(0x20, 16).Select(i => (byte)i).ToArray();

    private static byte[] Block(Action<BinaryWriter> body)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)RootId);
        body(w);
        return stream.ToArray();
    }

    private static void Pointer(BinaryWriter w, int classId) { w.Write(0xFFFFFFFFu); w.Write((ushort)classId); }

    private static void ConnectorBody(BinaryWriter w, int index, int mode, int calcClass)
    {
        w.Write(0u);                       // m_arrRefs: none
        w.Write(index); w.Write(mode);
        if (calcClass == 0) w.Write(0u); else Pointer(w, calcClass);
    }

    private static void ReferenceCalculation(BinaryWriter w, long system, int direction)
    {
        w.Write(system); w.Write(0); w.Write(0); w.Write(direction);
    }

    /// <summary>Root, then its queue: the entity cell, three connectors (hvac-like with a calculation, one without, an electrical one) and the calculations.</summary>
    private static byte[] Element(bool emptyEntity)
    {
        return Block(w =>
        {
            Pointer(w, CellId); Pointer(w, ConnectorId); Pointer(w, ConnectorId); Pointer(w, ConnectorId);
            // the entity cell: one pair whose entity holds no value (the schema GUID again, without a class tag)
            w.Write(1u); w.Write(SchemaGuid); w.Write(0xFFFFFFFFu);
            if (emptyEntity) w.Write(SchemaGuid); else w.Write(Enumerable.Repeat((byte)0xFF, 16).ToArray());
            ConnectorBody(w, 1, 2, 0);                         // no calculation
            ConnectorBody(w, 2, 1, SegmentCalculationId);
            ConnectorBody(w, 3, 1, ElectricalCalculationId);
            ReferenceCalculation(w, 77, 2);                    // of the second connector
            w.Write(0L); w.Write(88L);                         // electrical: no system, circuit 88
        });
    }

    [Fact]
    public void Walk_ContinuesBehindAnEmptyExtensibleStorageEntity()
    {
        var block = Element(emptyEntity: true);
        var walk = Params.Walker.Run(block, 0, block.Length, RootId);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(3, walk.Records.Count(record => record.ClassId == ConnectorId));
    }

    [Fact]
    public void Walk_StopsAtAnEntityThatIsNotTheSchemaGuidAgain()
    {
        var block = Element(emptyEntity: false);
        var walk = Params.Walker.Run(block, 0, block.Length, RootId);
        Assert.False(walk.Exact);
        Assert.DoesNotContain(walk.Records, record => record.ClassId == ConnectorId);
    }

    [Fact]
    public void Build_AssignsEachCalculationToTheConnectorThatQueuedIt()
    {
        var block = Element(emptyEntity: true);
        var np = Params.Build(Params.Walker.Run(block, 0, block.Length, RootId));
        Assert.NotNull(np);
        Assert.Equal(new[] { 2, 1, 1 }, Enumerable.Range(0, 3).Select(k => np.Fields[$"771#{k}:m_mode"]));
        Assert.False(np.Fields.ContainsKey("771#0:calcClass"));
        Assert.Equal(3556, np.Fields["771#1:calcClass"]);
        Assert.Equal(0, np.Fields["771#1:calcOccurrence"]);
        Assert.Equal(2380, np.Fields["771#2:calcClass"]);
    }

    [Fact]
    public void ApplyConnectorGraph_TakesDirectionAndSystemFromTheOwnCalculationAndKeepsTheMode()
    {
        var block = Element(emptyEntity: true);
        var np = Params.Build(Params.Walker.Run(block, 0, block.Length, RootId))!;
        var host = new Element { Id = 100 };
        var info = new RevitFileInfo();
        info.Elements.Add(host);
        var state = new ParsingState();
        state.NativeParams[100] = np;

        ElementBuilder.ApplyConnectorGraph(info, state);

        Assert.Equal([1, 2, 3], host.SerializedConnectorGraph!.Select(connector => connector.Index));
        // the connector without a calculation is bidirectional and has no system; the calculation belongs to the second one
        Assert.Equal([0, 2, -1], host.SerializedConnectorGraph!.Select(connector => connector.Direction));
        Assert.Equal([0, 77, 0], host.SerializedConnectorSystems);
        Assert.Equal([2, 1, 1], host.SerializedConnectorModes);
    }
}
