using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>An HVAC zone (ZoneElement) becomes an IfcZone that groups its exported spaces.</summary>
public class IfcExporterHvacZoneTests
{
    private const int ZoneNameParameter = -1114300;

    private static Element Zone(int id, string? name, bool isDefault) =>
        new()
        {
            Id = id,
            SerializedExportId = new Guid("11112222-3333-4444-5555-6666777788" + id.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)),
            SerializedZoneIsDefault = isDefault,
            Parameters = name == null ? [] : [new Parameter { Id = ZoneNameParameter, StorageType = StorageType.String, Value = name }],
        };

    private static Element Space(int id, int? zoneId) => new() { Id = id, SerializedSpaceZoneId = zoneId };

    private static Dictionary<int, Element> Model(params Element[] elements) => elements.ToDictionary(element => element.Id);

    private static List<(string Type, string Args)> WriteZones(
        Dictionary<int, Element> elements, Dictionary<int, int> spaceEntityByRoomId)
    {
        var writer = new StepWriter();
        typeof(IfcExporter).GetMethod("WriteHvacZones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(new IfcExporter(), [writer, 20, elements, spaceEntityByRoomId, new Dictionary<(string, string), int>()]);
        return Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index)).Select(entity => (entity.Type, entity.Args)).ToList();
    }

    [Fact]
    public void Zone_BecomesAnIfcZoneNamedByNumberAndIdWithItsSpacesInIdOrder()
    {
        var zone = Zone(50, "3", false);
        var entities = WriteZones(
            Model(zone, Space(12, 50), Space(4, 50), Space(7, 50), Space(8, 51), Space(9, null)),
            new() { [4] = 30, [7] = 31, [8] = 33, [9] = 34, [12] = 32 });

        Assert.Equal(["IFCZONE", "IFCRELASSIGNSTOGROUP", "IFCPROPERTYSINGLEVALUE", "IFCPROPERTYSET", "IFCRELDEFINESBYPROPERTIES"],
            entities.Select(entity => entity.Type));
        Assert.Equal($"'{IfcGuid.ToIfcGuid(zone.SerializedExportId!.Value)}',#20,'3:50','',''", entities[0].Args);
        Assert.EndsWith(",#20,'','',(#30,#31,#32),$,#1", entities[1].Args);
        Assert.Contains("'Reference',$,IFCIDENTIFIER('3')", entities[2].Args);
        Assert.Contains("'Pset_ZoneCommon'", entities[3].Args);
        Assert.EndsWith(",#20,'','',(#1),#4", entities[4].Args);
    }

    [Fact]
    public void Zone_TheDefaultZoneHasTheNumberDefaultWhateverItsStoredName()
    {
        var entities = WriteZones(Model(Zone(50, "0", true), Space(4, 50)), new() { [4] = 30 });
        Assert.Contains(",'Default:50','',''", entities[0].Args);
        Assert.Contains("IFCIDENTIFIER('Default')", entities[2].Args);
    }

    [Fact]
    public void Zone_ListsOnlyTheSpacesThatWereExported()
    {
        var entities = WriteZones(Model(Zone(50, "1", false), Space(4, 50), Space(99, 50)), new() { [4] = 30, [5] = 31 });
        Assert.EndsWith(",(#30),$,#1", entities[1].Args);
    }

    [Fact]
    public void Zone_WithoutAnExportedSpaceIsNotWritten()
    {
        var elements = Model(
            Zone(50, "1", false),                       // no space names it
            Zone(51, "2", false), Space(99, 51),        // its only space is not exported
            new Element { Id = 52 },                    // not a zone
            Space(4, 53));                              // a space of a zone that is not in the file
        Assert.Empty(WriteZones(elements, new() { [4] = 30 }));
    }

    [Fact]
    public void Zone_WhoseExportFlagIsNoIsNotWritten()
    {
        var zone = Zone(50, "1", false);
        zone.Parameters.Add(new Parameter { Id = (int)BuiltInParameter.IFC_EXPORT_ELEMENT, StorageType = StorageType.Integer, Value = 2 });
        Assert.Empty(WriteZones(Model(zone, Space(4, 50)), new() { [4] = 30 }));
    }

    [Fact]
    public void Zones_AreWrittenInElementIdOrder()
    {
        var entities = WriteZones(
            Model(Zone(70, "2", false), Zone(50, "1", false), Space(4, 70), Space(5, 50)), new() { [4] = 30, [5] = 31 });
        var zones = entities.Where(entity => entity.Type == "IFCZONE").ToList();
        Assert.Contains("'1:50'", zones[0].Args);
        Assert.Contains("'2:70'", zones[1].Args);
    }

    [Fact]
    public void ApplyMepSystemParams_KeepsTheDefaultFlagOfTheZoneAndTheZoneOfTheSpace()
    {
        var zone = new Element { Id = 50 };
        var defaultZone = new Element { Id = 51 };
        var space = new Element { Id = 52 };
        var freeSpace = new Element { Id = 53 };
        var plain = new Element { Id = 54 };
        var info = new RevitFileInfo();
        info.Elements.AddRange([zone, defaultZone, space, freeSpace, plain]);
        var state = new ParsingState();
        state.NativeParams[50] = new TfBlockDeserializer.NativeParams { Fields = { ["4215:m_bDefault"] = 0 } };
        state.NativeParams[51] = new TfBlockDeserializer.NativeParams { Fields = { ["4215:m_bDefault"] = 1 } };
        state.NativeParams[52] = new TfBlockDeserializer.NativeParams { Fields = { ["3471:m_zoneElementId"] = 50 } };
        state.NativeParams[53] = new TfBlockDeserializer.NativeParams { Fields = { ["3471:m_zoneElementId"] = -1 } };
        state.NativeParams[54] = new TfBlockDeserializer.NativeParams();

        ElementBuilder.ApplyMepSystemParams(info, state, null);

        Assert.False(zone.SerializedZoneIsDefault);
        Assert.True(defaultZone.SerializedZoneIsDefault);
        Assert.Equal(50, space.SerializedSpaceZoneId);
        Assert.Null(space.SerializedZoneIsDefault);
        Assert.Null(freeSpace.SerializedSpaceZoneId);
        Assert.Null(plain.SerializedZoneIsDefault);
        Assert.Null(plain.SerializedSpaceZoneId);
    }
}
