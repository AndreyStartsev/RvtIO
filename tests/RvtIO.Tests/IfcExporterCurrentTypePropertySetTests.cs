using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Type-held property sets of the current profile: every registered type receives the sets of all descriptions whose entity list names the instance or the type class, in registration order; Reference is the type's own name; IsExternal of a type is false unless the type is a roof. The legacy profile contract stays in <see cref="IfcExporterTypeObjectsTests"/>.</summary>
public class IfcExporterCurrentTypePropertySetTests
{
    private const int ElemTypeParamId = -1002050;
    private const int PhaseCreatedParamId = -1012100;
    private const int AllModelManufacturerParamId = -1010108;

    [Fact]
    public void RailingType_ReceivesQuantityTakeOffThenRailingCommon_WithThePlainTypeName()
    {
        var type = Type(500, "h 900", "Railing family");
        string ifc = Export(IfcProfile.Current, type, Product(100, -2000126, 500, 500));
        var sets = TypeSets(ifc, "IFCRAILINGTYPE");
        Assert.Equal(["Pset_QuantityTakeOff", "Pset_RailingCommon"], sets.Select(set => set.Name));
        Assert.Equal("IFCIDENTIFIER('h 900')", sets[0].Properties["Reference"]);
        Assert.Equal("IFCIDENTIFIER('h 900')", sets[1].Properties["Reference"]);
        Assert.Equal("IFCBOOLEAN(.F.)", sets[1].Properties["IsExternal"]);
        Assert.DoesNotContain("Height", sets[1].Properties.Keys);
    }

    [Fact]
    public void RailingType_InLegacy_KeepsTheSingleCommonSetWithoutReference()
    {
        var type = Type(500, "h 900", "Railing family");
        string ifc = Export(IfcProfile.Legacy, type, Product(100, -2000126, 500, 500));
        var sets = TypeSets(ifc, "IFCRAILINGTYPE");
        Assert.Equal(["Pset_RailingCommon"], sets.Select(set => set.Name));
        Assert.DoesNotContain("Reference", sets[0].Properties.Keys);
    }

    [Fact]
    public void BeamType_ReceivesTheColumnPitchSetNamedAfterTheBeamTwice()
    {
        var type = Type(500, "300x460", "Beam family");
        type.CategoryId = -2001320;
        string ifc = Export(IfcProfile.Current, type, Product(100, -2001320, 500, 500));
        var sets = TypeSets(ifc, "IFCBEAMTYPE");
        Assert.Equal(["Pset_BeamCommon", "Pset_QuantityTakeOff",
            "Pset_ReinforcementBarPitchOfBeam", "Pset_ReinforcementBarPitchOfBeam"], sets.Select(set => set.Name));
        Assert.Equal("IFCBOOLEAN(.T.)", sets[0].Properties["LoadBearing"]);
        Assert.Equal("IFCBOOLEAN(.F.)", sets[0].Properties["IsExternal"]);
        Assert.Equal("IFCLABEL('300x460')", sets[2].Properties["Reference"]);
    }

    [Fact]
    public void BraceType_IsAMemberTypeWithMemberCommonAndNoBarPitch_ABeamTypeStaysABeamType()
    {
        var brace = Product(100, -2001320, 500, 500);
        brace.NativeStructuralType = 2;
        var braceType = Type(500, "HSS 114", "Brace family");
        braceType.CategoryId = -2001320;
        string ifc = Export(IfcProfile.Current, braceType, brace);
        Assert.DoesNotContain("=IFCBEAMTYPE(", ifc);
        var sets = TypeSets(ifc, "IFCMEMBERTYPE");
        Assert.Equal(["Pset_MemberCommon", "Pset_QuantityTakeOff"], sets.Select(set => set.Name));
        Assert.Equal("IFCBOOLEAN(.T.)", sets[0].Properties["LoadBearing"]);
        Assert.Equal("IFCBOOLEAN(.F.)", sets[0].Properties["IsExternal"]);
        Assert.Equal("IFCIDENTIFIER('HSS 114')", sets[0].Properties["Reference"]);

        var beam = Product(100, -2001320, 500, 500);
        beam.NativeStructuralType = 1;
        string beamIfc = Export(IfcProfile.Current, braceType, beam);
        Assert.DoesNotContain("=IFCMEMBERTYPE(", beamIfc);
        Assert.Single(Regex.Matches(beamIfc, @"=IFCBEAMTYPE\("));
    }

    [Fact]
    public void NonStructuralFramingType_IsAProxyTypeWithTheProxyCommonSet_OnlyInTheCurrentProfile()
    {
        var framing = Product(100, -2001320, 500, 500);
        framing.NativeStructuralType = 0;
        var framingType = Type(500, "Strut 40", "Strut family");
        framingType.CategoryId = -2001320;

        string current = Export(IfcProfile.Current, framingType, framing);
        Assert.Contains("=IFCBUILDINGELEMENTPROXY(", current);
        Assert.DoesNotContain("=IFCBEAM(", current);
        Assert.DoesNotContain("=IFCBEAMTYPE(", current);
        Assert.Contains("Pset_BuildingElementProxyCommon", TypeSets(current, "IFCBUILDINGELEMENTPROXYTYPE").Select(set => set.Name));

        // measured on one Revit 2021 file only: the legacy profile keeps the beam
        string legacy = Export(IfcProfile.Legacy, framingType, framing);
        Assert.Contains("=IFCBEAM(", legacy);
        Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", legacy);
    }

    [Fact]
    public void DoorStyle_ReceivesDistributionFlowElementCommonLikeTheDoorDescriptions()
    {
        var type = Type(500, "800 x 2100", "Single-Flush");
        type.CategoryId = -2000023;
        string ifc = Export(IfcProfile.Current, type, Product(100, -2000023, 500, 500));
        Assert.Equal(["Pset_DistributionFlowElementCommon", "Pset_DoorCommon", "Pset_QuantityTakeOff"],
            TypeSets(ifc, "IFCDOORSTYLE").Select(set => set.Name));
        // The style also lists the lining and panel definitions of the door that made it, ahead of the sets.
        Assert.Matches(@"=IFCDOORSTYLE\([^;]*,\(#\d+,#\d+,#\d+,", ifc);
        Assert.Single(Regex.Matches(ifc, "=IFCDOORLININGPROPERTIES\\("));
        Assert.Single(Regex.Matches(ifc, "=IFCDOORPANELPROPERTIES\\("));
    }

    [Fact]
    public void SlabType_ReceivesBarPitchAndCommonSetsAfterQuantityTakeOff()
    {
        var type = Type(500, "Generic 150mm", "Floor");
        type.CategoryId = -2000032;
        string ifc = Export(IfcProfile.Current, type, Product(100, -2000032, 500, null));
        var sets = TypeSets(ifc, "IFCSLABTYPE");
        Assert.Equal(["Pset_QuantityTakeOff", "Pset_ReinforcementBarPitchOfSlab", "Pset_SlabCommon"],
            sets.Select(set => set.Name));
        Assert.Equal("IFCLABEL('Generic 150mm')", sets[1].Properties["Reference"]);
        Assert.Equal("IFCBOOLEAN(.F.)", sets[2].Properties["IsExternal"]);
    }

    [Fact]
    public void ManufacturerSet_FollowsTheHeldParameter_NotTheParserDefault()
    {
        var referencedEmpty = Type(500, "Held, empty", "Family");
        referencedEmpty.CategoryId = -2001330;
        referencedEmpty.References = [AllModelManufacturerParamId];
        referencedEmpty.Parameters.Add(Text(AllModelManufacturerParamId, ""));
        var decoded = Type(501, "Decoded", "Family");
        decoded.CategoryId = -2001330;
        decoded.Parameters.Add(Text(AllModelManufacturerParamId, "Kohler"));
        var defaulted = Type(502, "Parser default", "Family");
        defaulted.CategoryId = -2001330;
        defaulted.Parameters.Add(Text(AllModelManufacturerParamId, ""));
        string ifc = Export(IfcProfile.Current, referencedEmpty, decoded, defaulted,
            Product(100, -2001330, 500, 500), Product(101, -2001330, 501, 501), Product(102, -2001330, 502, 502));
        var byName = TypeSetsByTypeName(ifc, "IFCCOLUMNTYPE");
        Assert.Equal("IFCLABEL('')", byName["Held, empty"].Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);
        Assert.Equal("IFCLABEL('Kohler')", byName["Decoded"].Single(set => set.Name == "Pset_ManufacturerTypeInformation").Properties["Manufacturer"]);
        Assert.DoesNotContain(byName["Parser default"], set => set.Name == "Pset_ManufacturerTypeInformation");
    }

    [Fact]
    public void TypesOfClassesWithoutRegisteredTypeProperties_CarryNone()
    {
        Assert.False(IfcExporter.TypeReceivesPropertySetsCurrent("IFCFURNITURETYPE", "IFCFURNISHINGELEMENT", Type(1, "t", null)));
        Assert.False(IfcExporter.TypeReceivesPropertySetsCurrent("IFCSPACETYPE", "IFCSPACE", Type(1, "t", null)));
        Assert.False(IfcExporter.TypeReceivesPropertySetsCurrent("IFCDISTRIBUTIONELEMENTTYPE", "IFCFLOWSEGMENT", Type(1, "t", null)));
        Assert.True(IfcExporter.TypeReceivesPropertySetsCurrent("IFCDISTRIBUTIONELEMENTTYPE", "IFCFLOWTERMINAL", Type(1, "t", null)));
        var stringer = Type(1, "t", null);
        stringer.CategoryId = -2000123;
        Assert.False(IfcExporter.TypeReceivesPropertySetsCurrent("IFCMEMBERTYPE", "IFCMEMBER", stringer));
        stringer.CategoryId = -2000171;
        Assert.True(IfcExporter.TypeReceivesPropertySetsCurrent("IFCMEMBERTYPE", "IFCMEMBER", stringer));
    }

    [Theory]
    [InlineData(-2000035, true)]   // OST_Roofs
    [InlineData(-2003413, true)]   // OST_MassExteriorWall
    [InlineData(-2000011, false)]  // a wall type has no type of its own, whatever its function
    [InlineData(-2000023, false)]
    public void TypeIsExternal_IsTrueOnlyForRoofCategories(int category, bool expected)
    {
        var type = Type(1, "t", null);
        type.CategoryId = category;
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 1 });
        Assert.Equal(expected, IfcExporter.ResolveTypeIsExternalCurrent(type));
    }

    [Fact]
    public void TypeIsExternal_NamedParameterWins_AndBlockedParameterYieldsNoValue()
    {
        var type = Type(1, "t", null);
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.Integer, Value = 1 });
        Assert.True(IfcExporter.ResolveTypeIsExternalCurrent(type));
        type.Parameters.Clear();
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.String, Value = "{Host}" });
        Assert.Null(IfcExporter.ResolveTypeIsExternalCurrent(type));
    }

    [Fact]
    public void TypeReference_DropsTheFamilyPrefix_AndExplicitParameterWins()
    {
        var type = Type(1, "Interior - Partition", "Basic Wall");
        Assert.Equal("Interior - Partition", IfcExporter.ResolveTypeReferenceValueCurrent(type));
        type.Parameters.Add(Text(0, "Own reference", "Reference"));
        Assert.Equal("Own reference", IfcExporter.ResolveTypeReferenceValueCurrent(type));
        Assert.Null(IfcExporter.ResolveTypeReferenceValueCurrent(Type(2, null, null)));
    }

    private sealed record Pset(string Name, Dictionary<string, string> Properties);

    private static Element Type(int id, string? name, string? familyName) =>
        new() { Id = id, Name = name, FamilyName = familyName };

    private static Parameter Text(int id, string value, string? name = null) => new()
    {
        Id = id,
        Definition = name == null ? null : new Definition { Id = id, Name = name },
        StorageType = StorageType.String,
        Value = value,
    };

    private static Element Product(int id, int categoryId, int typeId, int? masterSymbolId) => new()
    {
        Id = id,
        SuperInstanceId = 999,
        IsDiscovered = true,
        CategoryId = categoryId,
        Name = $"Product {id}",
        References = [3],
        Parameters =
        [
            new Parameter { Id = PhaseCreatedParamId, StorageType = StorageType.ElementId, Value = 3 },
            new Parameter
            {
                Id = ElemTypeParamId,
                Definition = new Definition { Id = ElemTypeParamId, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId,
                Value = typeId,
            },
        ],
        NativeMasterSymbolId = masterSymbolId,
        BoundingBox = [0, 0, 0, 4, 3, 2],
        Geometry = SolidPrism(),
    };

    private static GeometryData SolidPrism()
    {
        double[][] points =
        [
            [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0],
            [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-currentProfile-types-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static List<Pset> TypeSets(string ifc, string typeClass)
    {
        var types = TypeSetsByTypeName(ifc, typeClass);
        return Assert.Single(types).Value;
    }

    private static Dictionary<string, List<Pset>> TypeSetsByTypeName(string ifc, string typeClass)
    {
        var result = new Dictionary<string, List<Pset>>();
        foreach (Match match in Regex.Matches(ifc, $@"#\d+={typeClass}\((.*)\);\r?\n"))
        {
            var args = Split(match.Groups[1].Value);
            string name = Regex.Replace(args[2].Trim('\''), @"\\X2\\.*?\\X0\\", "");
            var sets = new List<Pset>();
            if (args[5] != "$")
                foreach (string token in args[5].Trim('(', ')').Split(','))
                {
                    // The lining and panel properties of a door or window style are no property sets.
                    if (!Regex.IsMatch(ifc, $@"(?m)^{Regex.Escape(token)}=IFCPROPERTYSET\(")) continue;
                    var set = Split(Entity(ifc, int.Parse(token.TrimStart('#')), "IFCPROPERTYSET"));
                    var properties = new Dictionary<string, string>();
                    foreach (string property in set[4].Trim('(', ')').Split(','))
                    {
                        var single = Split(Entity(ifc, int.Parse(property.TrimStart('#')), "IFCPROPERTYSINGLEVALUE"));
                        properties[single[0].Trim('\'')] = single[2];
                    }
                    sets.Add(new Pset(set[2].Trim('\''), properties));
                }
            result[name] = sets;
        }
        return result;
    }

    private static string Entity(string ifc, int id, string type) =>
        Assert.Single(Regex.Matches(ifc, $@"#{id}={type}\((.*)\);\r?\n")).Groups[1].Value;

    private static List<string> Split(string raw)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(raw[start..i]);
                start = i + 1;
            }
        }
        parts.Add(raw[start..]);
        return parts;
    }
}
