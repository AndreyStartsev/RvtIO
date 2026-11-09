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

    private static List<string> PortNames(List<(string Type, string Args)> entities) =>
        entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args.Split(',')[2].Trim((char)39)).ToList();

    [Fact]
    public void Ports_OneConnectorThatNamesBothEndsOfAWireLeavesItAFreePort()
    {
        // The same host and connector at end 0 and end 1: no pair of a host with itself.
        var lamp = ConnectorHost(10, (1, -1, 0, [(30, 0, 1), (30, 1, 1)]));
        var (entities, _) = WritePorts(new() { [10] = lamp, [30] = Wire(30) }, Terminals(10));
        Assert.Equal(["Port_10_1"], PortNames(entities));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_AWireEndWithTwoHostsLeavesAllTheirConnectorsFree()
    {
        // Hosts 10 and 12 both name end 0 of wire 30, host 20 names end 1: the wire is not used.
        var a = ConnectorHost(10, (1, -1, 0, [(30, 0, 1)]));
        var b = ConnectorHost(12, (1, -1, 0, [(30, 0, 1)]));
        var c = ConnectorHost(20, (1, -1, 0, [(30, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = a, [12] = b, [20] = c, [30] = Wire(30) }, Terminals(10, 12, 20));
        Assert.Equal(["Port_10_1", "Port_12_1", "Port_20_1"], PortNames(entities));
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_TwoWiresOnTheSameConnectorsWritePortsAndAPairOnce()
    {
        var first = ConnectorHost(10, (1, -1, 0, [(30, 0, 1), (31, 0, 1)]));
        var second = ConnectorHost(20, (1, -1, 0, [(30, 1, 1), (31, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30), [31] = Wire(31) }, Terminals(10, 20));
        Assert.Equal(["Port_10_1", "Port_20_1"], PortNames(entities));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void Ports_TwoWiresBetweenTwoHostsOnOtherConnectorsPairTheHostsOnceAndTheOtherConnectorsKeepFreePorts()
    {
        // The pair of two hosts is written once: the connectors 2 of the second wire are not left without a port.
        var first = ConnectorHost(10, (1, -1, 0, [(30, 0, 1)]), (2, -1, 0, [(31, 0, 1)]));
        var second = ConnectorHost(20, (1, -1, 0, [(30, 1, 1)]), (2, -1, 0, [(31, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30), [31] = Wire(31) }, Terminals(10, 20));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal(["Port_10_1", "Port_20_1", "Port_10_2", "Port_20_2"], PortNames(entities));
        Assert.Equal(4, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT"));
    }

    [Fact]
    public void Ports_AWireBetweenHostsThatAreAlreadyConnectedDirectlyLeavesItsConnectorsFreePorts()
    {
        // Connectors 1 reference each other; the wire joins connectors 2 of the same two hosts.
        var first = ConnectorHost(10, (1, -1, 0, [(20, 1, 1)]), (2, -1, 0, [(30, 0, 1)]));
        var second = ConnectorHost(20, (1, -1, 0, [(10, 1, 1)]), (2, -1, 0, [(30, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = first, [20] = second, [30] = Wire(30) }, Terminals(10, 20));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.Equal(["Port_10_1", "Port_20_1", "Port_10_2", "Port_20_2"], PortNames(entities));
    }

    [Fact]
    public void Ports_AConnectorWithADirectLinkAndAWireToAnotherHostIsPairedTwice()
    {
        // Connector 1 of host 10 is connected to host 12 and, by a wire, to host 20.
        var a = ConnectorHost(10, (1, -1, 0, [(12, 1, 1), (30, 0, 1)]));
        var b = ConnectorHost(12, (1, -1, 0, [(10, 1, 1)]));
        var c = ConnectorHost(20, (1, -1, 0, [(30, 1, 1)]));
        var (entities, _) = WritePorts(
            new() { [10] = a, [12] = b, [20] = c, [30] = Wire(30) }, Terminals(10, 12, 20));
        Assert.Equal(2, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTS"));
        Assert.Equal(4, entities.Count(entity => entity.Type == "IFCDISTRIBUTIONPORT"));
        Assert.Equal(1, PortNames(entities).Count(name => name == "Port_20_1"));
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

    private const int ElectricalCircuitCategory = -2008037;

    // The circuit's own connector manager: connector 0 holds the circuit's reference to itself (type 4) and a load (type 1), the last
    // connector holds the reference to the panel (type 4, index 50000 + the circuit's number on the panel).
    private static TfBlockDeserializer.NativeParams CircuitRecord(int circuitId, int panelId, int panelIndex = 50001, int panelType = 4)
    {
        var native = new TfBlockDeserializer.NativeParams();
        native.StringFields["3170:m_strName"] = "";
        native.Fields["3170:m_typeId"] = -1;
        Connector(native, 0, 0);
        native.Fields["771#0:m_arrRefs#"] = 2;
        native.Fields["771#0:m_arrRefs@0:id"] = circuitId;
        native.Fields["771#0:m_arrRefs@0:index"] = 9;
        native.Fields["771#0:m_arrRefs@0:type"] = 4;
        native.Fields["771#0:m_arrRefs@1:id"] = 467291;
        native.Fields["771#0:m_arrRefs@1:index"] = 1;
        native.Fields["771#0:m_arrRefs@1:type"] = 1;
        Connector(native, 1, 1);
        native.Fields["771#1:m_arrRefs#"] = 1;
        native.Fields["771#1:m_arrRefs@0:id"] = panelId;
        native.Fields["771#1:m_arrRefs@0:index"] = panelIndex;
        native.Fields["771#1:m_arrRefs@0:type"] = panelType;
        return native;
    }

    private static Element ReadCircuit(TfBlockDeserializer.NativeParams native, int category = ElectricalCircuitCategory)
    {
        var circuit = new Element { Id = 50, CategoryId = category };
        var state = new ParsingState();
        state.NativeParams[50] = native;
        ElementBuilder.ApplyMepSystemParams(new RevitFileInfo { Elements = [circuit] }, state, null);
        return circuit;
    }

    [Fact]
    public void ApplyMepSystemParams_ReadsTheBaseEquipmentOfAnElectricalCircuit()
    {
        var circuit = ReadCircuit(CircuitRecord(50, 581483));
        Assert.Equal(581483, circuit.SerializedCircuitBaseEquipmentId);
        Assert.Equal(("", -1), circuit.SerializedMepSystem);
    }

    [Fact]
    public void ApplyMepSystemParams_CircuitWithoutAPanelReferenceHasNoBaseEquipment()
    {
        // Only its reference to itself and its loads (type 1): no base equipment.
        var withoutPanel = CircuitRecord(50, 581483);
        withoutPanel.Fields.Remove("771#1:m_nIndex");
        Assert.Null(ReadCircuit(withoutPanel).SerializedCircuitBaseEquipmentId);
        // A type 4 reference below index 50000, a load reference (type 1) at index 50000 and a reference to the circuit itself are not base equipment.
        Assert.Null(ReadCircuit(CircuitRecord(50, 581483, panelIndex: 49999)).SerializedCircuitBaseEquipmentId);
        Assert.Null(ReadCircuit(CircuitRecord(50, 581483, panelType: 1)).SerializedCircuitBaseEquipmentId);
        Assert.Null(ReadCircuit(CircuitRecord(50, 50)).SerializedCircuitBaseEquipmentId);
    }

    // A connector was removed from the circuit: the connector numbers run 0, 2, 3, so the panel connector (number 2) sits at array
    // position 1 and the connector at array position 2 is the load with number 3.
    private static TfBlockDeserializer.NativeParams CircuitRecordWithARemovedConnector(int circuitId, int panelId, int panelConnectorNumber)
    {
        var native = CircuitRecord(circuitId, panelId);
        native.Fields["771#1:m_nIndex"] = panelConnectorNumber;
        Connector(native, 2, 3);
        native.Fields["771#2:m_arrRefs#"] = 2;
        native.Fields["771#2:m_arrRefs@0:id"] = 700;
        native.Fields["771#2:m_arrRefs@0:index"] = 1;
        native.Fields["771#2:m_arrRefs@0:type"] = 1;
        native.Fields["771#2:m_arrRefs@1:id"] = 701;
        native.Fields["771#2:m_arrRefs@1:index"] = 1;
        native.Fields["771#2:m_arrRefs@1:type"] = 1;
        return native;
    }

    [Fact]
    public void ApplyMepSystemParams_ConnectorNumberNotEqualToItsPositionTakesTheFirstReferenceOfTheConnectorAtThatPosition()
    {
        // Two candidates: the panel (reference at index 50001) and the first load of the connector at array position 2.
        Assert.Equal(700, ReadCircuit(CircuitRecordWithARemovedConnector(50, 581483, panelConnectorNumber: 2)).SerializedCircuitBaseEquipmentId);
    }

    [Fact]
    public void ApplyMepSystemParams_NoConnectorAtThePositionOfThePanelConnectorNumberGivesNoBaseEquipment()
    {
        Assert.Null(ReadCircuit(CircuitRecordWithARemovedConnector(50, 581483, panelConnectorNumber: 3)).SerializedCircuitBaseEquipmentId);
        // The connector at that position holds only the circuit itself.
        var onlyItself = CircuitRecordWithARemovedConnector(50, 581483, panelConnectorNumber: 2);
        onlyItself.Fields["771#2:m_arrRefs#"] = 1;
        onlyItself.Fields["771#2:m_arrRefs@0:id"] = 50;
        Assert.Null(ReadCircuit(onlyItself).SerializedCircuitBaseEquipmentId);
    }

    [Fact]
    public void ApplyMepSystemParams_OnlyAnElectricalCircuitReadsBaseEquipment()
    {
        Assert.Null(ReadCircuit(CircuitRecord(50, 581483), category: -2008013).SerializedCircuitBaseEquipmentId);
    }

    [Fact]
    public void ApplyMepSystemParams_ACircuitWithoutAReadCategoryIsRecognisedByItsClass()
    {
        // A circuit with no loads can have a stored category that is not read; its class still says it is a circuit.
        var native = CircuitRecord(50, 581483);
        var circuit = new Element { Id = 50, NativeRootClassName = "RbsElectricalSystem" };
        var state = new ParsingState();
        state.NativeParams[50] = native;
        ElementBuilder.ApplyMepSystemParams(new RevitFileInfo { Elements = [circuit] }, state, null);
        Assert.Equal(581483, circuit.SerializedCircuitBaseEquipmentId);
    }

    [Fact]
    public void ElectricalCircuit_BecomesAnUnnamedIfcSystemWithItsBaseEquipmentAsTheOnlyMember()
    {
        var exportId = new Guid("11112222-3333-4444-5555-666677778888");
        var circuit = new Element { Id = 50, SerializedExportId = exportId, SerializedMepSystem = ("", -1), SerializedCircuitBaseEquipmentId = 581483 };
        var elements = new Dictionary<int, Element> { [50] = circuit };
        var members = new SortedDictionary<int, SortedSet<int>>();
        IfcExporter.AddCircuitBaseEquipment(elements, new Dictionary<int, (int, int, string)> { [581483] = (7, 8, "IFCBUILDINGELEMENTPROXY") }, members);

        var types = WriteSystems(elements, members, out var entities);

        Assert.Equal(["IFCSYSTEM", "IFCRELSERVICESBUILDINGS", "IFCRELASSIGNSTOGROUP"], types);
        string globalId = IfcGuid.ToIfcGuid(exportId);
        Assert.Equal($"'{globalId}',#20,'','',''", entities[0].Args);
        Assert.EndsWith(",#20,'','',#1,(#9)", entities[1].Args);
        Assert.Equal($"'{IfcGuid.FromMd5($"IfcRelAssignsToGroup:{globalId}")}',#20,'','',(#7),$,#1", entities[2].Args);
    }

    [Fact]
    public void ElectricalCircuit_WithoutAnExportedBaseEquipmentIsNotWritten()
    {
        var withBase = new Element { Id = 50, SerializedMepSystem = ("", -1), SerializedCircuitBaseEquipmentId = 581483 };
        var withoutBase = new Element { Id = 51, SerializedMepSystem = ("", -1) };
        var elements = new Dictionary<int, Element> { [50] = withBase, [51] = withoutBase, [52] = new Element { Id = 52 } };
        var members = new SortedDictionary<int, SortedSet<int>>();
        // The panel of the first circuit is not an exported product.
        IfcExporter.AddCircuitBaseEquipment(elements, new Dictionary<int, (int, int, string)> { [999] = (7, 8, "IFCBUILDINGELEMENTPROXY") }, members);
        Assert.Empty(members);
        Assert.Empty(WriteSystems(elements, members, out _));
    }

    private static string ExportText(IfcProfile profile, params Element[] elements)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-circuit-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = profile }.Export(new RevitFileInfo { Elements = [.. elements] }, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(IfcProfile.Current, true)]
    [InlineData(IfcProfile.Legacy, false)]
    public void ElectricalCircuit_IsWrittenAsAnIfcSystemOnlyInTheCurrentProfile(IfcProfile profile, bool written)
    {
        var panel = IfcExporterBeamCommonPropertySetTests.Product(581483, 600, -2001040, "OST_ElectricalEquipment", "Panel");
        panel.SerializedExportId = Guid.NewGuid();
        var circuit = new Element
        {
            Id = 50, Name = "", CategoryId = ElectricalCircuitCategory, Category = "OST_ElectricalCircuit", SerializedExportId = Guid.NewGuid(),
            SerializedMepSystem = ("", -1), SerializedCircuitBaseEquipmentId = 581483,
        };
        string ifc = ExportText(profile, panel, circuit);
        Assert.Contains("=IFCBUILDINGELEMENTPROXY(", ifc);
        if (written)
        {
            Assert.Contains("=IFCSYSTEM(", ifc);
            Assert.Contains("=IFCRELASSIGNSTOGROUP(", ifc);
        }
        else
        {
            Assert.DoesNotContain("=IFCSYSTEM(", ifc);
            Assert.DoesNotContain("=IFCRELASSIGNSTOGROUP(", ifc);
        }
    }

    [Fact]
    public void ElectricalCircuit_DoesNotChangeANamedSystemsMembers()
    {
        var system = new Element { Id = 50, SerializedMepSystem = ("Supply 1", 60) };
        var circuit = new Element { Id = 70, SerializedMepSystem = ("", -1), SerializedCircuitBaseEquipmentId = 581483 };
        var members = new SortedDictionary<int, SortedSet<int>> { [50] = new SortedSet<int> { 4, 5 } };
        IfcExporter.AddCircuitBaseEquipment(new Dictionary<int, Element> { [50] = system, [70] = circuit },
            new Dictionary<int, (int, int, string)> { [581483] = (7, 8, "IFCBUILDINGELEMENTPROXY") }, members);
        Assert.Equal([4, 5], members[50]);
        Assert.Equal([7], members[70]);
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
    public void Ports_AFittingPassesTheDirectionOnToTheFittingItIsConnectedTo()
    {
        // Pipe 10 (connector 0 out) feeds fitting 12, which feeds fitting 14, which feeds pipe 16 (connector 0 in). Neither fitting stores a direction
        // or a system: 12 takes the in side from the pipe and 14 takes the in side from 12, so the pair of the fittings is no longer bidirectional.
        var pipeIn = ConnectorHost(10, (0, 2, 50, [(12, 0, 1)]));
        var first = ConnectorHost(12, (0, 0, 0, [(10, 0, 1)]), (1, 0, 0, [(14, 0, 1)]));
        var second = ConnectorHost(14, (0, 0, 0, [(12, 1, 1)]), (1, 0, 0, [(16, 0, 1)]));
        var pipeOut = ConnectorHost(16, (0, 1, 50, [(14, 1, 1)]));
        var (entities, _) = WritePorts(new() { [10] = pipeIn, [12] = first, [14] = second, [16] = pipeOut },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING"), [14] = (7, 8, "IFCFLOWFITTING"), [16] = (9, 10, "IFCFLOWSEGMENT") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(6, ports.Count);
        Assert.Equal(3, ports.Count(args => args.EndsWith(".SINK.")));
        Assert.Equal(3, ports.Count(args => args.EndsWith(".SOURCE.")));
        Assert.Contains(ports, args => args.Contains("'InPort_14_") && args.EndsWith(".SINK."));
        Assert.Contains(ports, args => args.Contains("'OutPort_12_") && args.EndsWith(".SOURCE."));
    }

    [Fact]
    public void Ports_AFittingWithThreeConnectorsDoesNotPassTheDirectionOnBetweenItsConnectors()
    {
        // Fitting 12 has three connectors and knows the direction of one of them only (from pipe 10). Whether the others are in or out cannot be told
        // (a tee collects as well as it splits), so its pair with the bare fitting 14 stays bidirectional.
        var pipe = ConnectorHost(10, (0, 2, 50, [(12, 0, 1)]));
        var tee = ConnectorHost(12, (0, 0, 0, [(10, 0, 1)]), (1, 0, 0, [(14, 0, 1)]), (2, 0, 0, []));
        var bare = ConnectorHost(14, (0, 0, 0, [(12, 1, 1)]));
        var (entities, _) = WritePorts(new() { [10] = pipe, [12] = tee, [14] = bare },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING"), [14] = (7, 8, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Contains(ports, args => args.Contains("'Port_14_0'") && args.EndsWith(".SOURCEANDSINK."));
        Assert.Contains(ports, args => args.Contains("'Port_12_0'") && args.EndsWith(".SOURCEANDSINK."));
    }

    [Fact]
    public void Ports_AFittingConnectorOfASystemKeepsItsOwnBidirectionalPortWhenTheOtherConnectorHasADirection()
    {
        // Fitting 12 has two connectors; connector 0 takes the in side from pipe 10, connector 1 stores system 50, which is its own calculation, so it stays bidirectional.
        var pipe = ConnectorHost(10, (0, 2, 50, [(12, 0, 1)]));
        var fitting = ConnectorHost(12, (0, 0, 0, [(10, 0, 1)]), (1, 0, 50, [(14, 0, 1)]));
        var bare = ConnectorHost(14, (0, 0, 0, [(12, 1, 1)]));
        var (entities, _) = WritePorts(new() { [10] = pipe, [12] = fitting, [14] = bare },
            new() { [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING"), [14] = (7, 8, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Contains(ports, args => args.Contains("'Port_14_") && args.EndsWith(".SOURCEANDSINK."));
    }

    [Fact]
    public void Ports_AHostWithoutADirectionTakesTheOppositeOfTheFittingConnectorItIsPairedWith()
    {
        // The bare host 8 (lower id, so it is met first) stores no direction; the fitting connector it is connected to has taken the out side from
        // pipe 10 through the fitting's other connector.
        var pipe = ConnectorHost(10, (0, 2, 50, [(12, 0, 1)]));
        var fitting = ConnectorHost(12, (0, 0, 0, [(10, 0, 1)]), (1, 0, 0, [(8, 1, 1)]));
        var bare = ConnectorHost(8, (1, 0, 0, [(12, 1, 1)]));
        var (entities, _) = WritePorts(new() { [8] = bare, [10] = pipe, [12] = fitting },
            new() { [8] = (1, 2, "IFCBUILDINGELEMENTPROXY"), [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING") });
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Contains(ports, args => args.Contains("'InPort_8_") && args.EndsWith(".SINK."));
        Assert.Contains(ports, args => args.Contains("'OutPort_12_") && args.EndsWith(".SOURCE."));
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
