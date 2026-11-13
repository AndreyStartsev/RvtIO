using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Pset_WindowCommon: Reference only (IsExternal has no native source and stays omitted, as for Pset_WallCommon/Pset_DoorCommon).
/// Reference reuses the QTO property entity like every other common Pset. Its value folds in the window-specific native override
/// (<see cref="Element.ValidatedWindowTypeReferenceName"/>, <see cref="ElementBuilder.ApplyValidatedWindowTypeReferenceNames"/>).</summary>
public class IfcExporterWindowCommonPropertySetTests
{
    [Fact]
    public void EmittedWindow_ReusesQtoReferencePropertyEntity_AndEmitsIsExternalAndReference()
    {
        Element window = HostedProduct(id: 4000, typeId: 4001, name: "Window type");

        string ifc = ExportToText(window);

        int windowId = SoleEntityId(ifc, "IFCWINDOW");
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_WindowCommon");
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);

        Assert.Equal(windowId, SolePsetTarget(ifc, qto.Id));
        Assert.Equal(windowId, SolePsetTarget(ifc, common.Id));
        Assert.Equal(2, common.ValueIds.Count);
        Assert.Equal(Assert.Single(qto.ValueIds), common.ValueIds[1]);
        Assert.Equal(qto.ValueIds[0], properties["Reference"].Id);
        Assert.Equal("IFCIDENTIFIER('Window type')", properties["Reference"].Expression);
        // The window has no host wall: the null host result gives the default .F.
        Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        Assert.Equal(new[] { "IsExternal", "Reference" }, properties.Keys);
        Assert.Contains(
            $"IFCPROPERTYSET('{IfcGuid.FromName("window-common-pset:4000")}',", ifc);
        Assert.Contains(
            $"IFCRELDEFINESBYPROPERTIES('{IfcGuid.FromName("window-common-rel:4000")}',",
            ifc);
        Assert.DoesNotContain("IFCWINDOWSTYLE", ifc);
    }

    [Theory]
    [InlineData(null, "IFCBOOLEAN(.T.)")]
    [InlineData("Pset_WallCommon.IsExternal", "IFCBOOLEAN(.T.)")]
    [InlineData("IsExternal", "IFCBOOLEAN(.F.)")]
    public void LegacyWindow_HostWallClassificationReadsThePropertyNameAlone(string? wallParameter, string expected)
    {
        // The legacy profile takes a window's IsExternal from its host wall's classification, which a wall parameter named
        // for the wall's own Pset_WallCommon property does not change.
        Element window = HostedProduct(id: 4100, typeId: 4101, name: "Window type");
        window.SerializedHostId = 4200;
        var wallType = new Element { Id = 4201, CategoryId = -2000011, SerializedBasicWallTypeFunction = 1 };
        var wall = new Element
        {
            Id = 4200, CategoryId = -2000011,
            Parameters = [new Parameter { Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = 4201 }],
        };
        if (wallParameter != null)
            wall.Parameters.Add(new Parameter { Definition = new Definition { Name = wallParameter },
                StorageType = StorageType.Integer, Value = 0 });

        var info = new RevitFileInfo();
        info.Elements.AddRange([window, wall, wallType]);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-window-host-{Guid.NewGuid():N}.ifc");
        string ifc;
        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            ifc = File.ReadAllText(path);
        }
        finally { File.Delete(path); }

        Assert.Equal(expected, Properties(ifc, SolePset(ifc, "Pset_WindowCommon"))["IsExternal"].Expression);
    }

    [Fact]
    public void ValidatedWindowTypeReference_OverridesWrongGeneralName()
    {
        // A window's general name fan-out can resolve the wrong FamilySymbol name (an unrelated sibling family's name attached to a window type).
        // The window's own decoded m_masterSymbolId link (ValidatedWindowTypeReferenceName, computed by ApplyValidatedWindowTypeReferenceNames from
        // ParsingState.ElementNameTable) overrides that baseline for Reference and, as a product is named after its type, for Name/ObjectType too.
        // ASCII stand-ins are used for the names; STEP escaping of non-Latin1 text is exercised elsewhere.
        Element window = HostedProduct(id: 4010, typeId: 4011, name: "Wrong sibling name");
        window.ValidatedWindowTypeReferenceName = "Correct window type Reference";

        string ifc = ExportToText(window);

        Pset common = SolePset(ifc, "Pset_WindowCommon");
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);
        Assert.Equal(
            "IFCIDENTIFIER('Correct window type Reference')",
            properties["Reference"].Expression);
        // Name/ObjectType follow the same type name.
        Assert.Contains("'Hosted family:Correct window type Reference:4010'", ifc);
    }

    [Fact]
    public void SiblingWithoutValidatedWindowTypeReference_FallsBackToBaselineName()
    {
        // No validation (ValidatedWindowTypeReferenceName left null, as ApplyValidatedWindowTypeReferenceNames leaves a window lacking its own decoded
        // m_masterSymbolId link): keep the ordinary baseline Reference (elem.Name) instead of going bare or picking up an unrelated validation.
        Element sibling = HostedProduct(id: 4020, typeId: 4021, name: "Existing window name");
        Assert.Null(sibling.ValidatedWindowTypeReferenceName);

        string ifc = ExportToText(sibling);

        Pset common = SolePset(ifc, "Pset_WindowCommon");
        Assert.Equal(
            "IFCIDENTIFIER('Existing window name')",
            Properties(ifc, common)["Reference"].Expression);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOrBlockedQtoReference_CreatesNoOrphanWindowCommon(bool blocked)
    {
        Element window = HostedProduct(
            id: 4030, typeId: 4031, name: blocked ? "Fallback Reference" : null);
        if (blocked)
        {
            window.Parameters.Add(new Parameter
            {
                Id = 7030,
                Definition = new Definition { Id = 7030, Name = "Reference" },
                StorageType = StorageType.Double,
                Value = 12.5,
            });
        }

        string ifc = ExportToText(window);

        Assert.True(SoleEntityId(ifc, "IFCWINDOW") > 0);
        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        // Without a Reference the window still carries IsExternal alone.
        Pset common = SolePset(ifc, "Pset_WindowCommon");
        Assert.Equal(new[] { "IsExternal" }, Properties(ifc, common).Keys);
    }

    [Fact]
    public void EmittedDoor_KeepsQtoButReceivesNoWindowCommon()
    {
        Element door = HostedProduct(id: 4040, typeId: 4041, name: "Door type");
        door.CategoryId = -2000023;
        door.Category = "OST_Doors";

        string ifc = ExportToText(door);

        Assert.True(SoleEntityId(ifc, "IFCDOOR") > 0);
        Assert.Single(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.DoesNotContain("Pset_WindowCommon", ifc);
        Assert.DoesNotContain("window-common-pset", ifc);
        Assert.DoesNotContain("window-common-rel", ifc);
    }

    private sealed record Pset(int Id, IReadOnlyList<int> ValueIds);

    private sealed record PropertyValue(int Id, string Expression);

    private static Dictionary<string, PropertyValue> Properties(string ifc, Pset pset)
    {
        var result = new Dictionary<string, PropertyValue>();
        foreach (int valueId in pset.ValueIds)
        {
            Match match = Assert.Single(Regex.Matches(
                ifc,
                $@"#{valueId}=IFCPROPERTYSINGLEVALUE\('((?:''|[^'])*)',(?:\$|''),(.*),\$\);"));
            result.Add(
                match.Groups[1].Value.Replace("''", "'"),
                new PropertyValue(valueId, match.Groups[2].Value));
        }
        return result;
    }

    private static Pset SolePset(string ifc, string psetName) =>
        Assert.Single(Psets(ifc, psetName));

    private static List<Pset> Psets(string ifc, string psetName) =>
        Regex.Matches(
                ifc,
                $@"#(\d+)=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\((#[0-9]+(?:,#[0-9]+)*)\)\);")
            .Select(match => new Pset(
                int.Parse(match.Groups[1].Value),
                match.Groups[2].Value.Split(',')
                    .Select(value => int.Parse(value[1..])).ToList()))
            .ToList();

    private static int SolePsetTarget(string ifc, int psetId)
    {
        Match match = Assert.Single(Regex.Matches(
            ifc,
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#(\d+)\),#{psetId}\);"));
        return int.Parse(match.Groups[1].Value);
    }

    private static int SoleEntityId(string ifc, string ifcType)
    {
        Match match = Assert.Single(Regex.Matches(ifc, $@"#(\d+)={ifcType}\("));
        return int.Parse(match.Groups[1].Value);
    }

    private static Element HostedProduct(
        int id, int typeId, string? name) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            SuperInstanceId = 999,
            IsDiscovered = true,
            CategoryId = -2000014,
            Category = "OST_Windows",
            Name = name,
            FamilyName = "Hosted family",
            References = [3],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
            ],
            BoundingBox = [id, 0, 0, id + 4, 3, 2],
            Geometry = SolidPrism(id),
        };

    private static GeometryData SolidPrism(double centreX)
    {
        double[][] points =
        [
            [centreX, 0, 0], [centreX + 4, 0, 0],
            [centreX + 4, 3, 0], [centreX, 3, 0],
            [centreX, 0, 2], [centreX + 4, 0, 2],
            [centreX + 4, 3, 2], [centreX, 3, 2],
        ];
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7],
            [0, 1, 5, 4], [1, 2, 6, 5],
            [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        return new GeometryData
        {
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                },
            ],
        };
    }

    private static string ExportToText(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"revitreader-window-common-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter().Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
