using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile exports each MEP system element that received members from the connectors as an IfcSystem with IfcRelServicesBuildings and IfcRelAssignsToGroup.</summary>
public class IfcExporterMepSystemTests
{
    private static Element ConnectorHost(int id, params (int Index, int Direction, int System, (int Id, int Index, int Type)[] Refs)[] connectors) =>
        new()
        {
            Id = id,
            SerializedConnectorGraph = connectors.Select(connector =>
                (connector.Index, connector.Direction, (IReadOnlyList<(int Id, int Index, int Type)>)connector.Refs)).ToList(),
            SerializedConnectorSystems = connectors.Select(connector => connector.System).ToList(),
        };

    private static (List<(string Type, string Args)> Entities, SortedDictionary<int, SortedSet<int>> Systems) WritePorts(
        Dictionary<int, Element> elements, Dictionary<int, (int entityId, int placementId, string ifcType)> products, bool currentProfile = true)
    {
        var writer = new StepWriter();
        // The writer reads each host's GlobalId back from its entity.
        foreach (var (id, product) in products.OrderBy(pair => pair.Value.entityId))
        {
            while (writer.EntityCount < product.entityId - 1)
                writer.Add("IFCCARTESIANPOINT", "(0.,0.)");
            writer.Add(product.ifcType, $"'{IfcGuid.FromName($"host:{id}")}',#1,'host',$,$,$,$,'{id}'");
        }
        var method = typeof(IfcExporter).GetMethod("WriteDistributionPorts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var systems = (SortedDictionary<int, SortedSet<int>>)method.Invoke(null, [writer, 20, elements, products, currentProfile])!;
        return (Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index)).Select(entity => (entity.Type, entity.Args)).ToList(), systems);
    }

    [Fact]
    public void Ports_StandaloneConnectorJoinsItsHostAndPortToTheConnectorSystem()
    {
        var segment = ConnectorHost(10, (0, 0, 50, [(10, 1, 1)]), (1, 0, 0, [(10, 0, 1)]));
        var (entities, systems) = WritePorts(
            new() { [10] = segment }, new() { [10] = (3, 4, "IFCFLOWSEGMENT") });
        var portIds = entities.Select((entity, index) => (entity, id: index + 1))
            .Where(item => item.entity.Type == "IFCDISTRIBUTIONPORT").Select(item => item.id).ToList();
        Assert.Equal(2, portIds.Count);
        // Only connector 0 belongs to a system (50); connector 1 has none.
        var system = Assert.Single(systems);
        Assert.Equal(50, system.Key);
        Assert.Equal([3, portIds[0]], system.Value);
    }

    [Fact]
    public void Ports_ConnectedPairJoinsHostsAndPortsToTheSystemOfTheInHostsConnectorOnly()
    {
        // Segment 10 (system 50) and fitting 12 (system 51) reference each other through connector 1; the segment is
        // visited first and is the in host, so system 50 takes both hosts and both ports, system 51 nothing.
        var segment = ConnectorHost(10, (1, 0, 50, [(12, 1, 1)]));
        var fitting = ConnectorHost(12, (1, 0, 51, [(10, 1, 1)]));
        var (entities, systems) = WritePorts(
            new() { [10] = segment, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var portIds = entities.Select((entity, index) => (entity, id: index + 1))
            .Where(item => item.entity.Type == "IFCDISTRIBUTIONPORT").Select(item => item.id).ToList();
        Assert.Equal(2, portIds.Count);
        var system = Assert.Single(systems);
        Assert.Equal(50, system.Key);
        Assert.Equal(new SortedSet<int> { 3, 5, portIds[0], portIds[1] }, system.Value);
    }

    [Fact]
    public void Ports_ConnectorsWithoutASystemAndTheLegacyExportJoinNothing()
    {
        var segment = ConnectorHost(10, (0, 0, 0, [(10, 1, 1)]), (1, 0, -1, [(10, 0, 1)]));
        var products = new Dictionary<int, (int entityId, int placementId, string ifcType)> { [10] = (3, 4, "IFCFLOWSEGMENT") };
        Assert.Empty(WritePorts(new() { [10] = segment }, products).Systems);
        // The graph is not used for the legacy profile at all.
        segment = ConnectorHost(10, (0, 0, 50, [(10, 1, 1)]));
        Assert.Empty(WritePorts(new() { [10] = segment }, products, currentProfile: false).Systems);
    }

    [Fact]
    public void Ports_RecordsRecoveredOutsideTheElementTableGetNoPortsAndNoSystem()
    {
        var discovered = ConnectorHost(10, (1, 0, 50, [(12, 1, 1)]));
        discovered.IsDiscovered = true;
        var fitting = ConnectorHost(12, (1, 0, 51, [(10, 1, 1)]));
        var (entities, systems) = WritePorts(
            new() { [10] = discovered, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var port = Assert.Single(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT");
        Assert.Contains("'Port_12_1'", port.Args);
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        var system = Assert.Single(systems);
        Assert.Equal(51, system.Key);
    }

    [Fact]
    public void Ports_PipeInsulationWithConnectorDataIsNoHostAndDoesNotConnectThePipe()
    {
        var pipe = ConnectorHost(10, (0, 0, 50, []));
        var insulation = ConnectorHost(20, (0, 0, 50, [(10, 0, 1)]));
        var (entities, systems) = WritePorts(
            new() { [10] = pipe, [20] = insulation },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [20] = (5, 6, "IFCCOVERING") });
        var port = Assert.Single(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT");
        Assert.Contains("'Port_10_0'", port.Args);
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal(1, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT"));
        // The insulation does not join the system either.
        int portId = entities.Select((entity, index) => (entity, id: index + 1))
            .Single(item => item.entity.Type == "IFCDISTRIBUTIONPORT").id;
        Assert.Equal([3, portId], Assert.Single(systems).Value);
    }

    [Fact]
    public void Ports_ConnectorReferencingARegisteredHostWithoutConnectorGraphGetsNoPortOfItsOwn()
    {
        var segment = ConnectorHost(10, (0, 0, 0, [(12, 0, 1)]), (1, 0, 0, []));
        var fitting = new Element { Id = 12 };
        var (entities, _) = WritePorts(
            new() { [10] = segment, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var port = Assert.Single(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT");
        Assert.Contains("'Port_10_1'", port.Args);
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_ReferencesToNothingOrToANonHostLeaveTheConnectorFree()
    {
        // A reference to an id without a product, to a furnishing element or to a recovered record does not connect.
        var furniture = ConnectorHost(14, (0, 0, 0, []));
        var segment = ConnectorHost(10, (0, 0, 0, [(99, 0, 1)]), (1, 0, 0, [(14, 0, 1)]), (2, 0, 0, [(10, 0, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = segment, [14] = furniture },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [14] = (5, 6, "IFCFURNISHINGELEMENT") });
        var names = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains(names, args => args.Contains("'Port_10_0'"));
        Assert.Contains(names, args => args.Contains("'Port_10_1'"));
        Assert.Contains(names, args => args.Contains("'Port_10_2'"));
    }

    private static Element Wire(int id, int category = -2008039, int? header = null) =>
        new() { Id = id, CategoryId = category, NativeHeaderCategoryId = header };

    private static Dictionary<int, (int entityId, int placementId, string ifcType)> Terminals(params int[] hostIds) =>
        hostIds.Select((id, position) => (id, position)).ToDictionary(item => item.id, item => (3 + 2 * item.position, 4 + 2 * item.position, "IFCFLOWTERMINAL"));

    private static string HostGlobalId(int id) => IfcGuid.FromName($"host:{id}");

    [Theory]
    [InlineData(10, 20)]
    [InlineData(20, 10)]
    public void Ports_TwoHostsOnTheEndsOfAWireArePairedWhateverTheirIds(int hostAtEnd0, int hostAtEnd1)
    {
        // The lower id of the two is not the in host: the end 0 of the wire is.
        var first = ConnectorHost(hostAtEnd0, (1, -1, 0, [(30, 0, 1), (40, 7, 4)]));
        var second = ConnectorHost(hostAtEnd1, (1, -1, 0, [(30, 1, 1), (40, 8, 4)]));
        var (entities, systems) = WritePorts(
            new() { [hostAtEnd0] = first, [hostAtEnd1] = second, [30] = Wire(30) }, Terminals(10, 20));
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(2, ports.Count);
        Assert.Contains($"'{IfcGuid.FromMd5($"InPort1{HostGlobalId(hostAtEnd0)}{HostGlobalId(hostAtEnd1)}")}'", ports[0]);
        Assert.Contains($"'Port_{hostAtEnd0}_1'", ports[0]);
        Assert.Contains(".SOURCEANDSINK.", ports[0]);
        Assert.Contains($"'{IfcGuid.FromMd5($"OutPort1{HostGlobalId(hostAtEnd1)}{HostGlobalId(hostAtEnd0)}")}'", ports[1]);
        Assert.Contains($"'Port_{hostAtEnd1}_1'", ports[1]);
        Assert.Contains(".SOURCEANDSINK.", ports[1]);
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal(2, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT"));
        Assert.Empty(systems);
    }

    [Fact]
    public void Ports_AConnectorOnTheEndOfAWireWhoseOtherEndHasNoHostStaysAFreePort()
    {
        var lamp = ConnectorHost(10, (1, -1, 0, [(30, 0, 1)]));
        // End 1 names a record that is no host, and the wire of host 12 has one end only.
        var other = ConnectorHost(12, (1, -1, 0, [(31, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = lamp, [12] = other, [30] = Wire(30), [31] = Wire(31) }, Terminals(10, 12));
        Assert.Equal(["'Port_10_1'", "'Port_12_1'"], entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT")
            .Select(entity => entity.Args.Split(',')[2]));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_AnElementThatIsNoWireOrAReferenceOfAnotherTypeIsNoWire()
    {
        // Element 30 has the ends 0 and 1 but is a circuit (another category); the circuit reference of type 4 joins the hosts too.
        var first = ConnectorHost(10, (1, -1, 0, [(30, 0, 1), (40, 7, 4)]));
        var second = ConnectorHost(20, (1, -1, 0, [(30, 1, 1), (40, 8, 4)]));
        var (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30, category: -2008044) }, Terminals(10, 20));
        Assert.Equal(2, entities.Count(entity => entity.Type == "IFCDISTRIBUTIONPORT"));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        // A wire reference with another type number (a circuit with the same ends) is no wire either.
        first = ConnectorHost(10, (1, -1, 0, [(30, 0, 4)]));
        second = ConnectorHost(20, (1, -1, 0, [(30, 1, 4)]));
        (entities, _) = WritePorts(new() { [10] = first, [20] = second, [30] = Wire(30) }, Terminals(10, 20));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal(2, entities.Count(entity => entity.Type == "IFCDISTRIBUTIONPORT"));
    }

    [Fact]
    public void Ports_TheCategoryStoredInTheRecordDecidesWhetherAnElementIsAWire()
    {
        // The derived category of a wire of a newer file can be another one; the stored header category is the wire.
        var first = ConnectorHost(10, (1, -1, 0, [(30, 0, 1)]));
        var second = ConnectorHost(20, (1, -1, 0, [(30, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30, category: -2001120, header: -2008039) }, Terminals(10, 20));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        // A stored category that is not the wire wins over a derived one that is.
        (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30, category: -2008039, header: -2008044) }, Terminals(10, 20));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        // A stored null id leaves the derived category.
        (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30, header: -1) }, Terminals(10, 20));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_AWireWithOneHostAtBothEndsOrDifferentConnectorNumbersLeavesTheConnectorsFree()
    {
        // Host 10 at both ends of wire 30; hosts 12 and 14 meet on wire 31 at different connector numbers.
        var loop = ConnectorHost(10, (1, -1, 0, [(30, 0, 1)]), (2, -1, 0, [(30, 1, 1)]));
        var left = ConnectorHost(12, (1, -1, 0, [(31, 0, 1)]));
        var right = ConnectorHost(14, (2, -1, 0, [(31, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = loop, [12] = left, [14] = right, [30] = Wire(30), [31] = Wire(31) }, Terminals(10, 12, 14));
        Assert.Equal(4, entities.Count(entity => entity.Type == "IFCDISTRIBUTIONPORT"));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_ARunOfTwoWiresGivesTheMiddleDeviceTwoPortsWithDifferentGlobalIds()
    {
        // Wire 30: 10 (end 0) to 20 (end 1); wire 31: 20 (end 0) to 12 (end 1); the middle device holds both on one connector.
        var first = ConnectorHost(10, (1, -1, 0, [(30, 0, 1)]));
        var middle = ConnectorHost(20, (1, -1, 0, [(30, 1, 1), (31, 0, 1)]));
        var last = ConnectorHost(12, (1, -1, 0, [(31, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = first, [20] = middle, [12] = last, [30] = Wire(30), [31] = Wire(31) }, Terminals(10, 20, 12));
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(4, ports.Count);
        Assert.Equal(2, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTS"));
        var middlePorts = ports.Where(args => args.Contains("'Port_20_1'")).Select(args => args.Split(',')[0]).ToList();
        Assert.Equal(2, middlePorts.Count);
        Assert.NotEqual(middlePorts[0], middlePorts[1]);
        // No connector of the run is written as a free port as well.
        Assert.Equal(4, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT"));
    }

    [Fact]
    public void Ports_ConnectorsOnAWireJoinTheSystemsOfTheirConnectors()
    {
        var first = ConnectorHost(10, (1, -1, 50, [(30, 0, 1)]));
        var second = ConnectorHost(20, (1, -1, 51, [(30, 1, 1)]));
        var (entities, systems) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30) }, Terminals(10, 20));
        var portIds = entities.Select((entity, index) => (entity, id: index + 1))
            .Where(item => item.entity.Type == "IFCDISTRIBUTIONPORT").Select(item => item.id).ToList();
        var system = Assert.Single(systems);
        Assert.Equal(50, system.Key);
        Assert.Equal(new SortedSet<int> { 3, 5, portIds[0], portIds[1] }, system.Value);
    }

    private static string[] WriteSystems(Dictionary<int, Element> elements, SortedDictionary<int, SortedSet<int>> members, out List<(string Type, string Args)> entities)
    {
        var writer = new StepWriter();
        typeof(IfcExporter).GetMethod("WriteMepSystems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(new IfcExporter(), [writer, 20, 9, elements, members]);
        entities = Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index))
            .Select(entity => (entity.Type, entity.Args)).ToList();
        return entities.Select(entity => entity.Type).ToArray();
    }

    [Fact]
    public void MepSystem_BecomesAnIfcSystemServingTheBuildingWithItsMembersAssigned()
    {
        var exportId = new Guid("11112222-3333-4444-5555-666677778888");
        var system = new Element { Id = 50, SerializedExportId = exportId, SerializedMepSystem = ("Supply 1", 60) };
        var type = new Element { Id = 60, Name = "Supply type" };
        var types = WriteSystems(new() { [50] = system, [60] = type },
            new() { [50] = new SortedSet<int> { 12, 4, 7 } }, out var entities);

        Assert.Equal(["IFCSYSTEM", "IFCRELSERVICESBUILDINGS", "IFCRELASSIGNSTOGROUP"], types);
        string globalId = IfcGuid.ToIfcGuid(exportId);
        Assert.Equal($"'{globalId}',#20,'Supply 1','','Supply type'", entities[0].Args);
        Assert.EndsWith(",#20,'','',#1,(#9)", entities[1].Args);
        Assert.Equal($"'{IfcGuid.FromMd5($"IfcRelAssignsToGroup:{globalId}")}',#20,'','',(#4,#7,#12),$,#1", entities[2].Args);
    }

    [Fact]
    public void MepSystem_WithoutMembersOrSystemDataIsNotWritten()
    {
        var withData = new Element { Id = 50, SerializedMepSystem = ("Supply 1", 60) };
        var withoutData = new Element { Id = 51 };
        var types = WriteSystems(new() { [50] = withData, [51] = withoutData },
            new() { [50] = new SortedSet<int>(), [51] = new SortedSet<int> { 4 }, [52] = new SortedSet<int> { 5 } }, out _);
        Assert.Empty(types);
    }

    [Fact]
    public void MepSystem_UnknownTypeGivesAnEmptyObjectTypeAndSystemsFollowElementIdOrder()
    {
        var first = new Element { Id = 50, SerializedMepSystem = ("A 1", 99) };
        var second = new Element { Id = 70, SerializedMepSystem = ("B 1", 99) };
        WriteSystems(new() { [70] = second, [50] = first },
            new() { [70] = new SortedSet<int> { 4 }, [50] = new SortedSet<int> { 5 } }, out var entities);
        var systems = entities.Where(entity => entity.Type == "IFCSYSTEM").ToList();
        Assert.Equal(2, systems.Count);
        Assert.Contains(",'A 1','',''", systems[0].Args);
        Assert.Contains(",'B 1','',''", systems[1].Args);
    }

    private static void Connector(TfBlockDeserializer.NativeParams native, int k, int index)
    {
        native.Fields[$"771#{k}:m_nIndex"] = index;
        native.Fields[$"771#{k}:m_arrRefs#"] = 0;
    }

    [Fact]
    public void ApplyConnectorGraph_ReadsTheConnectorSystemsFromTheCalculationOfTheElementKind()
    {
        var pipe = new Element { Id = 100 };
        var fitting = new Element { Id = 101, SerializedMepConnectors = [(1, 2, 19), (2, 1, 20)] };
        var mixed = new Element { Id = 102 };
        var equipment = new Element { Id = 103 }; // mixed calculations: SerializedMepConnectors stays null
        var info = new RevitFileInfo();
        info.Elements.AddRange([pipe, fitting, mixed, equipment]);
        var state = new ParsingState();
        var pipeParams = new TfBlockDeserializer.NativeParams();
        Connector(pipeParams, 0, 0);
        Connector(pipeParams, 1, 1);
        pipeParams.Fields["3556#0:m_idSystem"] = 7;
        pipeParams.Fields["3556#1:m_idSystem"] = 7;
        var fittingParams = new TfBlockDeserializer.NativeParams();
        Connector(fittingParams, 0, 1);
        Connector(fittingParams, 1, 2);
        fittingParams.Fields["2382#0:m_idSystem"] = 8;
        fittingParams.Fields["2382#1:m_idSystem"] = 0;
        var mixedParams = new TfBlockDeserializer.NativeParams();
        Connector(mixedParams, 0, 1);
        mixedParams.Fields["2381#0:m_idSystem"] = 9;
        var equipmentParams = new TfBlockDeserializer.NativeParams();
        Connector(equipmentParams, 0, 1);
        equipmentParams.Fields["2381#0:m_idSystem"] = 9;
        equipmentParams.Fields["2382#0:m_idSystem"] = 10;
        state.NativeParams[100] = pipeParams;
        state.NativeParams[101] = fittingParams;
        state.NativeParams[102] = mixedParams;
        state.NativeParams[103] = equipmentParams;

        ElementBuilder.ApplyConnectorGraph(info, state);

        Assert.Equal([7, 7], pipe.SerializedConnectorSystems);
        Assert.Equal([8, 0], fitting.SerializedConnectorSystems);
        // Without the one-to-one calculations of a single domain the system of a connector is not known.
        Assert.Equal([0], mixed.SerializedConnectorSystems);
        Assert.Equal([0], equipment.SerializedConnectorSystems);
    }

    [Fact]
    public void ApplyMepSystemParams_KeepsNameAndTypeOfTheSystemElement()
    {
        var system = new Element { Id = 50 };
        var plain = new Element { Id = 51 };
        var info = new RevitFileInfo();
        info.Elements.AddRange([system, plain]);
        var state = new ParsingState();
        var systemParams = new TfBlockDeserializer.NativeParams();
        systemParams.StringFields["3170:m_strName"] = "Supply 1";
        systemParams.Fields["3170:m_typeId"] = 60;
        state.NativeParams[50] = systemParams;
        state.NativeParams[51] = new TfBlockDeserializer.NativeParams();

        ElementBuilder.ApplyMepSystemParams(info, state, null);

        Assert.Equal(("Supply 1", 60), system.SerializedMepSystem);
        Assert.Null(plain.SerializedMepSystem);
    }

    [Fact]
    public void Ports_AreWrittenForAFileWithItsOwnSchemaToo()
    {
        var segment = ConnectorHost(10, (0, 0, 0, []));
        segment.NativeRootClassName = "RbsPipeCurve";
        var (entities, _) = WritePorts(new() { [10] = segment }, new() { [10] = (3, 4, "IFCFLOWSEGMENT") });
        Assert.Contains("'Port_10_0'", Assert.Single(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT").Args);
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT");
    }

    [Fact]
    public void Ports_AConnectorOfModeFourSixteenOrThirtyTwoGetsNoPort()
    {
        var equipment = ConnectorHost(10, (1, 0, 0, []), (2, 0, 0, []), (6001, 0, 0, []), (50000, 0, 0, []), (3, 0, 0, []));
        equipment.SerializedConnectorModes = [1, 32, 16, 4, 2];
        var (entities, _) = WritePorts(new() { [10] = equipment }, new() { [10] = (3, 4, "IFCBUILDINGELEMENTPROXY") });
        var names = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains(names, args => args.Contains("'Port_10_1'"));
        Assert.Contains(names, args => args.Contains("'Port_10_3'"));
    }

    [Fact]
    public void Ports_APairOfConnectorsOfAnotherModeIsNotConnected()
    {
        // A mode 16 connector of the pipe that references a fitting does not pair with it.
        var pipe = ConnectorHost(10, (1, 0, 0, [(12, 1, 1)]));
        pipe.SerializedConnectorModes = [16];
        var fitting = ConnectorHost(12, (1, 0, 0, [(10, 1, 1)]));
        fitting.SerializedConnectorModes = [1];
        var (entities, _) = WritePorts(new() { [10] = pipe, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        Assert.Empty(entities.Where(entity => entity.Type == "IFCRELCONNECTSPORTS"));
    }

    [Fact]
    public void Ports_AFittingWithoutADirectionTakesTheOppositeOfTheConnectorItReferences()
    {
        // The pipe's connector 0 (direction 2) is the one the fitting's connector 1 references; the pair is formed on index 1 (both hosts
        // have it) and the pipe's side is the out port, the fitting's the in port.
        var pipe = ConnectorHost(10, (0, 2, 50, [(12, 1, 1)]), (1, 0, 50, []));
        var fitting = ConnectorHost(12, (1, 0, 0, [(10, 0, 1)]));
        var (entities, _) = WritePorts(new() { [10] = pipe, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Contains(ports, args => args.Contains("'InPort_12_1'") && args.EndsWith(".SINK."));
        Assert.Contains(ports, args => args.Contains("'OutPort_10_1'") && args.EndsWith(".SOURCE."));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Ports_AConnectorOfTheSystemOfTheConnectorItReferencesWithoutADirectionStaysBidirectional(int referencedDirection)
    {
        // The pipe's connector 0 has a direction and the fitting's connector 1 references it, but the fitting's connector belongs to the same
        // system and stores direction 0: the reference output keeps both ports bidirectional.
        var pipe = ConnectorHost(10, (0, referencedDirection, 50, [(12, 1, 1)]), (1, 0, 50, []));
        var fitting = ConnectorHost(12, (1, 0, 50, [(10, 0, 1)]));
        var (entities, systems) = WritePorts(new() { [10] = pipe, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.All(ports, args => Assert.EndsWith(".SOURCEANDSINK.", args));
        Assert.Contains(ports, args => args.Contains("'Port_12_1'"));
        Assert.DoesNotContain(ports, args => args.Contains("'InPort_") || args.Contains("'OutPort_"));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Contains(5, systems[50]);
    }

    [Fact]
    public void Ports_AConnectorOfAnotherSystemThanTheConnectorItReferencesTakesTheOppositeDirection()
    {
        // Same as above, but the fitting's connector stores another system (an unrelated value is common in files whose connectors share
        // no calculation): its direction is not its own and it takes the opposite of the referenced connector.
        var pipe = ConnectorHost(10, (0, 2, 50, [(12, 1, 1)]), (1, 0, 50, []));
        var fitting = ConnectorHost(12, (1, 0, 51, [(10, 0, 1)]));
        var (entities, _) = WritePorts(new() { [10] = pipe, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Contains(ports, args => args.Contains("'InPort_12_1'") && args.EndsWith(".SINK."));
        Assert.Contains(ports, args => args.Contains("'OutPort_10_1'") && args.EndsWith(".SOURCE."));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void Ports_AFittingJoinsTheSystemOfTheConnectorItReferencesNotOfTheOneWithItsOwnNumber(int referencedDirection)
    {
        // The fitting's connector 1 references connector 0 of the equipment (system 50); connector 1 of the equipment belongs to system 60.
        // The pair is formed on index 1, but the fitting is connected to connector 0, whichever side is the in side.
        var equipment = ConnectorHost(10, (0, referencedDirection, 50, [(12, 1, 1)]), (1, 0, 60, []));
        var fitting = ConnectorHost(12, (1, 0, 0, [(10, 0, 1)]));
        var (entities, systems) = WritePorts(new() { [10] = equipment, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Contains(5, systems[50]);
        Assert.DoesNotContain(5, systems[60]);
    }

    [Fact]
    public void Ports_AReferenceToAConnectorThatGetsNoPortIsNotPairedWithAnotherConnectorOfTheHost()
    {
        // The fitting's connector 2 references connector 1 of the pipe, a connector of mode 16 that is left out; the pipe's connector 2 is
        // another connector and has nothing to do with the reference.
        var pipe = ConnectorHost(10, (1, 0, 50, []), (2, 0, 50, []));
        pipe.SerializedConnectorModes = [16, 1];
        var fitting = ConnectorHost(12, (2, 0, 0, [(10, 1, 1)]));
        fitting.SerializedConnectorModes = [1];
        var (entities, systems) = WritePorts(new() { [10] = pipe, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal("'Port_10_2'", Assert.Single(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT").Args.Split(',')[2]);
        Assert.DoesNotContain(5, systems.SelectMany(system => system.Value));
    }

    [Fact]
    public void Ports_ADirectionOfAConnectorWithoutASystemIsNotTakenOverByTheConnectorPairedWithIt()
    {
        // The fitting's connector stores a direction but no system (an electrical calculation); the segment's connector has none
        // and the reference output keeps both ports bidirectional.
        var segment = ConnectorHost(10, (1, 0, 0, [(12, 1, 1)]));
        var fitting = ConnectorHost(12, (1, 2, 0, [(10, 1, 1)]));
        var (entities, _) = WritePorts(new() { [10] = segment, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(2, ports.Count);
        Assert.All(ports, args => Assert.EndsWith(".SOURCEANDSINK.", args));
    }

    [Fact]
    public void Ports_TwoConnectorsWithoutADirectionStayBidirectionalWhenPaired()
    {
        var pipe = ConnectorHost(10, (1, 0, 0, [(12, 1, 1)]));
        var fitting = ConnectorHost(12, (1, 0, 0, [(10, 1, 1)]));
        var (entities, _) = WritePorts(new() { [10] = pipe, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(2, ports.Count);
        Assert.All(ports, args => Assert.EndsWith(".SOURCEANDSINK.", args));
    }

    [Fact]
    public void Ports_TwoHostsJoinedAtBothEndsHaveOneConnectionBetweenTheirPorts()
    {
        // The segment is connected to the fitting at both of its ends (connectors 1 and 2 of both, without a direction): the connection between two bidirectional hosts is written once, not once per
        // connector pair and side.
        var segment = ConnectorHost(10, (1, 0, 0, [(12, 1, 1)]), (2, 0, 0, [(12, 2, 1)]));
        var fitting = ConnectorHost(12, (1, 0, 0, [(10, 1, 1)]), (2, 0, 0, [(10, 2, 1)]));
        var (entities, _) = WritePorts(new() { [10] = segment, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal(2, entities.Count(entity => entity.Type == "IFCDISTRIBUTIONPORT"));
    }

    [Fact]
    public void Ports_APairWhoseInHostStoresNoSystemBelongsToTheSystemOfTheOtherConnector()
    {
        // The fitting (id 12, the in host: the segment's connector 0 has direction 2) stores no system; the segment's connector has system 50.
        var segment = ConnectorHost(10, (1, 2, 50, [(12, 1, 1)]));
        var fitting = ConnectorHost(12, (1, 0, 0, [(10, 1, 1)]));
        var (entities, systems) = WritePorts(new() { [10] = segment, [12] = fitting },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var portIds = entities.Select((entity, index) => (entity, id: index + 1))
            .Where(item => item.entity.Type == "IFCDISTRIBUTIONPORT").Select(item => item.id).ToList();
        var system = Assert.Single(systems);
        Assert.Equal(50, system.Key);
        Assert.Equal(new SortedSet<int> { 3, 5, portIds[0], portIds[1] }, system.Value);
    }
}
