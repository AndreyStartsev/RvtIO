using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Occurrence Height and IsExternal of Pset_RailingCommon. Height needs the named BaseRailing occurrence-to-attribute chain and a finite positive top rail height; IsExternal additionally needs the materialized own railing type. Type-level Psets and generic railing occurrences are out of scope.</summary>
public class IfcExporterRailingCommonHeightPropertySetTests
{
    private const double SampleTopHeightFeet = 2.9527559055118107;
    private const double SecondTopHeightFeet = 3.608923884514436;

    [Fact]
    public void ValidatedBaseRailingHeight_EmitsPrivateHeightOnly()
    {
        Element railing = Product(
            id: 9000, typeId: 9001, categoryId: -2000126,
            category: "OST_StairsRailing", name: "Railing type");
        AddExactHeightSource(railing, 19000, SampleTopHeightFeet);

        string ifc = ExportToText(railing);

        int railingId = SoleEntityId(ifc, "IFCRAILING");
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_RailingCommon");
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);

        Assert.Equal(railingId, SolePsetTarget(ifc, qto.Id));
        Assert.Equal(railingId, SolePsetTarget(ifc, common.Id));
        Assert.Equal(new[] { "Height" }, properties.Keys);
        Assert.Equal(
            "IFCPOSITIVELENGTHMEASURE(899.99999999999989)",
            properties["Height"].Expression);
        Assert.DoesNotContain(qto.ValueIds, id => id == properties["Height"].Id);
        Assert.Contains(IfcGuid.FromName("railing-common-pset:9000"), ifc);
        Assert.Contains(IfcGuid.FromName("railing-common-rel:9000"), ifc);
        Assert.DoesNotContain("'IsExternal'", ifc);
        Assert.DoesNotContain("Pset_RailingTypeCommon", ifc);
    }

    [Fact]
    public void SampleSerializedHeight_PreservesExact1100Token()
    {
        Element railing = Product(
            id: 9010, typeId: 9011, categoryId: -2000126,
            category: "OST_StairsRailing", name: "Sample railing");
        AddExactHeightSource(railing, 19010, SecondTopHeightFeet);

        string ifc = ExportToText(railing);

        Assert.Equal(
            "IFCPOSITIVELENGTHMEASURE(1100.)",
            Properties(ifc, SolePset(ifc, "Pset_RailingCommon"))["Height"].Expression);
    }

    [Fact]
    public void EqualHeightsRemainPrivatePerOccurrence()
    {
        Element first = Product(
            9020, 9022, -2000126, "OST_StairsRailing", "Shared railing", 0);
        Element second = Product(
            9021, 9022, -2000126, "OST_StairsRailing", "Shared railing", 10);
        AddExactHeightSource(first, 19020, SampleTopHeightFeet);
        AddExactHeightSource(second, 19021, SampleTopHeightFeet);

        string ifc = ExportToText(first, second);
        List<Pset> commons = Psets(ifc, "Pset_RailingCommon");

        Assert.Equal(2, commons.Count);
        Assert.Equal(2, commons.SelectMany(pset => pset.ValueIds).Distinct().Count());
        Assert.Equal(2, Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Height',(?:\$|''),IFCPOSITIVELENGTHMEASURE\(899\.99999999999989\),\$\)").Count);
        Assert.Equal(
            EntityIds(ifc, "IFCRAILING").OrderBy(id => id),
            commons.Select(pset => SolePsetTarget(ifc, pset.Id)).OrderBy(id => id));
        foreach (int id in new[] { 9020, 9021 })
        {
            Assert.Contains(IfcGuid.FromName($"railing-common-pset:{id}"), ifc);
            Assert.Contains(IfcGuid.FromName($"railing-common-rel:{id}"), ifc);
        }
    }

    [Fact]
    public void MissingOrInvalidSerializedSource_OmitsRailingCommon()
    {
        Element missingHeight = Product(
            9030, 9040, -2000126, "OST_StairsRailing", "Missing height", 0);
        missingHeight.SerializedStairsRailingAttrId = 19030;

        Element missingAttribute = Product(
            9031, 9041, -2000126, "OST_StairsRailing", "Missing link", 10);
        missingAttribute.SerializedRailingTopHeightFeet = SampleTopHeightFeet;

        Element zero = Product(
            9032, 9042, -2000126, "OST_StairsRailing", "Zero height", 20);
        AddExactHeightSource(zero, 19032, 0);

        Element nonFinite = Product(
            9033, 9043, -2000126, "OST_StairsRailing", "Invalid height", 30);
        AddExactHeightSource(nonFinite, 19033, double.NaN);

        string ifc = ExportToText(
            missingHeight, missingAttribute, zero, nonFinite);

        Assert.Equal(4, EntityIds(ifc, "IFCRAILING").Count);
        Assert.Equal(4, Psets(ifc, "Pset_QuantityTakeOff").Count);
        Assert.Empty(Psets(ifc, "Pset_RailingCommon"));
        Assert.DoesNotContain("'Height'", ifc);
        Assert.DoesNotContain("railing-common-pset", ifc);
        Assert.DoesNotContain("railing-common-rel", ifc);
    }

    [Fact]
    public void NullOrBlockedQtoReference_CreatesNoRailingCommonOrphan()
    {
        Element unnamed = Product(
            9050, 9052, -2000126, "OST_StairsRailing", null, 0);
        AddExactHeightSource(unnamed, 19050, SampleTopHeightFeet);

        Element blocked = Product(
            9051, 9053, -2000126, "OST_StairsRailing", "Fallback railing", 10);
        AddExactHeightSource(blocked, 19051, SampleTopHeightFeet);
        blocked.Parameters.Add(new Parameter
        {
            Id = 29051,
            Definition = new Definition { Id = 29051, Name = "Reference" },
            StorageType = StorageType.Double,
            Value = 12.5,
        });

        string ifc = ExportToText(unnamed, blocked);

        Assert.Equal(2, EntityIds(ifc, "IFCRAILING").Count);
        Assert.Empty(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.Empty(Psets(ifc, "Pset_RailingCommon"));
        Assert.DoesNotContain("railing-common-pset", ifc);
        Assert.DoesNotContain("railing-common-rel", ifc);
    }

    [Fact]
    public void NonRailingWithSerializedFields_ReceivesNoRailingCommon()
    {
        Element covering = Product(
            9060, 9061, -2000038, "OST_Ceilings", "Ceiling");
        AddExactHeightSource(covering, 19060, SampleTopHeightFeet);

        string ifc = ExportToText(covering);

        Assert.True(SoleEntityId(ifc, "IFCCOVERING") > 0);
        Assert.Single(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.Empty(Psets(ifc, "Pset_RailingCommon"));
        Assert.DoesNotContain("railing-common-pset", ifc);
        Assert.DoesNotContain("railing-common-rel", ifc);
    }

    [Fact]
    public void GeometrylessRailing_CreatesNoProductQtoOrRailingCommon()
    {
        Element railing = Product(
            9070, 9071, -2000126, "OST_StairsRailing", "Suppressed railing");
        AddExactHeightSource(railing, 19070, SampleTopHeightFeet);
        railing.Geometry = null;

        string ifc = ExportToText(railing);

        Assert.Empty(EntityIds(ifc, "IFCRAILING"));
        Assert.Empty(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.Empty(Psets(ifc, "Pset_RailingCommon"));
        Assert.DoesNotContain("railing-common-pset", ifc);
        Assert.DoesNotContain("railing-common-rel", ifc);
    }

    [Fact]
    public void MaterializedRailingTypeWithoutSource_IsInternalAndPooled()
    {
        Element first = Product(9100, 9102, -2000126, "OST_StairsRailing", "Railing", 0);
        Element second = Product(9101, 9102, -2000126, "OST_StairsRailing", "Railing", 10);
        AddExactHeightSource(first, 19100, SampleTopHeightFeet);
        AddExactHeightSource(second, 19100, SampleTopHeightFeet);

        string ifc = ExportToText(first, second, RailingType(19100));
        List<Pset> commons = Psets(ifc, "Pset_RailingCommon");

        Assert.Equal(2, commons.Count);
        foreach (Pset common in commons)
        {
            Dictionary<string, PropertyValue> properties = Properties(ifc, common);
            Assert.Equal(new[] { "IsExternal", "Height" }, properties.Keys);
            Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        }
        Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('IsExternal',"));
        Assert.Equal(2, EntityIds(ifc, "IFCRAILING").Count);
        Assert.Empty(Psets(ifc, "Pset_RailingTypeCommon"));
    }

    [Theory]
    [InlineData("occurrence", "IsExternal", 1, "IFCBOOLEAN(.T.)")]
    [InlineData("occurrence", "IsExternal", 0, "IFCBOOLEAN(.F.)")]
    [InlineData("type", "IsExternal", 1, "IFCBOOLEAN(.T.)")]
    [InlineData("type", "IsExternal[Type]", 1, "IFCBOOLEAN(.T.)")]
    [InlineData("occurrence", "Pset_RailingCommon.IsExternal", 0, "IFCBOOLEAN(.F.)")]
    [InlineData("type", "Pset_RailingCommon.IsExternal[Type]", 1, "IFCBOOLEAN(.T.)")]
    [InlineData("function", null, 1, "IFCBOOLEAN(.T.)")]
    [InlineData("function", null, 0, "IFCBOOLEAN(.F.)")]
    public void OfficialPrecedence_NamedParameterThenTypeFunction(
        string scope, string? name, int value, string expected)
    {
        Element railing = Product(9110, 9111, -2000126, "OST_StairsRailing", "Railing");
        AddExactHeightSource(railing, 19110, SampleTopHeightFeet);
        Element type = RailingType(19110);
        Parameter parameter = name == null
            ? new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = value }
            : IntParameter(29110, name, value);
        (scope == "occurrence" ? railing : type).Parameters.Add(parameter);

        string ifc = ExportToText(railing, type);

        Assert.Equal(expected,
            Properties(ifc, SolePset(ifc, "Pset_RailingCommon"))["IsExternal"].Expression);
    }

    [Fact]
    public void SetQualifiedIsExternal_WinsOverThePropertyName()
    {
        Element railing = Product(9125, 9126, -2000126, "OST_StairsRailing", "Railing");
        AddExactHeightSource(railing, 19125, SampleTopHeightFeet);
        railing.Parameters.Add(IntParameter(29125, "IsExternal", 1));
        railing.Parameters.Add(IntParameter(29126, "Pset_RailingCommon.IsExternal", 0));
        Element type = RailingType(19125);

        string ifc = ExportToText(railing, type);

        Assert.Equal("IFCBOOLEAN(.F.)",
            Properties(ifc, SolePset(ifc, "Pset_RailingCommon"))["IsExternal"].Expression);
    }

    [Fact]
    public void OccurrenceParameterPrecedesTypeFunction()
    {
        Element railing = Product(9120, 9121, -2000126, "OST_StairsRailing", "Railing");
        AddExactHeightSource(railing, 19120, SampleTopHeightFeet);
        railing.Parameters.Add(IntParameter(29120, "IsExternal", 0));
        Element type = RailingType(19120);
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 1 });

        string ifc = ExportToText(railing, type);

        Assert.Equal("IFCBOOLEAN(.F.)",
            Properties(ifc, SolePset(ifc, "Pset_RailingCommon"))["IsExternal"].Expression);
    }

    [Theory]
    [InlineData("approximate")] [InlineData("conflict")] [InlineData("function_text")]
    [InlineData("missing_type")] [InlineData("duplicate_type")] [InlineData("type_category")]
    [InlineData("self_type")] [InlineData("no_attribute")]
    public void BlockedOrUninspectableSource_OmitsOnlyIsExternal(string mutation)
    {
        Element railing = Product(9130, 9131, -2000126, "OST_StairsRailing", "Railing");
        AddExactHeightSource(railing, 19130, SampleTopHeightFeet);
        Element type = RailingType(19130);
        var elements = new List<Element> { railing, type };
        switch (mutation)
        {
            case "approximate":
                Parameter approximate = IntParameter(29130, "IsExternal", 1);
                approximate.IsApproximate = true;
                railing.Parameters.Add(approximate);
                break;
            case "conflict":
                type.Parameters.Add(IntParameter(29131, "IsExternal", 1));
                type.Parameters.Add(IntParameter(29132, "IsExternal", 0));
                break;
            case "function_text":
                type.Parameters.Add(new Parameter
                    { Id = -1001006, StorageType = StorageType.String, Value = "Exterior" });
                break;
            case "missing_type": elements.Remove(type); break;
            case "duplicate_type": elements.Add(RailingType(19130)); break;
            case "type_category": type.CategoryId = -2000120; break;
            case "self_type": railing.SerializedStairsRailingAttrId = railing.Id; break;
            case "no_attribute":
                railing.SerializedStairsRailingAttrId = null;
                railing.ValidatedRoofOrRailingTypeReferenceName = "Own type";
                break;
        }

        string ifc = ExportToText(elements.ToArray());

        Assert.Single(EntityIds(ifc, "IFCRAILING"));
        Assert.DoesNotContain("'IsExternal'", ifc);
        if (mutation != "self_type" && mutation != "no_attribute")
            Assert.Equal(new[] { "Height" },
                Properties(ifc, SolePset(ifc, "Pset_RailingCommon")).Keys);
    }

    [Fact]
    public void NonRailingCategoryIsNeverEvaluatedAsRailing()
    {
        Element covering = Product(9140, 9141, -2000038, "OST_Ceilings", "Ceiling");
        AddExactHeightSource(covering, 19140, SampleTopHeightFeet);
        Element type = RailingType(19140);
        type.CategoryId = -2000038;

        Assert.Null(IfcExporter.ResolveRailingIsExternal(
            covering,
            new Dictionary<int, Element> { [9140] = covering, [19140] = type },
            new Dictionary<int, int> { [9140] = 1, [19140] = 1 }));
        Assert.DoesNotContain("'IsExternal'", ExportToText(covering, type));
    }

    [Fact]
    public void RailingFamilyInstanceWithOwnSymbolName_SharesReferenceAndIsNotExternal()
    {
        Element railing = Product(9150, 9151, -2000126, "OST_StairsRailing", "In-place railing");
        railing.NativeMasterSymbolId = 9151;
        railing.ValidatedRoofOrRailingTypeReferenceName = "Own symbol";

        string ifc = ExportToText(railing, RailingType(9151));
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Dictionary<string, PropertyValue> properties =
            Properties(ifc, SolePset(ifc, "Pset_RailingCommon"));

        Assert.Equal(new[] { "IsExternal", "Reference" }, properties.Keys.OrderBy(key => key));
        Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        Assert.Equal("IFCIDENTIFIER('Own symbol')", properties["Reference"].Expression);
        Assert.Equal(Assert.Single(qto.ValueIds), properties["Reference"].Id);
    }

    private static Element RailingType(int id) => new()
    {
        Id = id,
        CategoryId = -2000126,
        Category = "OST_StairsRailing",
        Name = "Railing type",
    };

    private static Parameter IntParameter(int id, string name, int value) => new()
    {
        Id = id,
        Definition = new Definition { Id = id, Name = name },
        StorageType = StorageType.Integer,
        Value = value,
    };

    private static void AddExactHeightSource(
        Element railing, int attributeId, double heightFeet)
    {
        railing.SerializedStairsRailingAttrId = attributeId;
        railing.SerializedRailingTopHeightFeet = heightFeet;
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

    private static int SoleEntityId(string ifc, string ifcType) =>
        Assert.Single(EntityIds(ifc, ifcType));

    private static List<int> EntityIds(string ifc, string ifcType) =>
        Regex.Matches(ifc, $@"#(\d+)={ifcType}\(")
            .Select(match => int.Parse(match.Groups[1].Value)).ToList();

    private static Element Product(
        int id, int typeId, int categoryId, string category, string? name,
        double centreX = 0) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            SuperInstanceId = 999,
            IsDiscovered = true,
            CategoryId = categoryId,
            Category = category,
            Name = name,
            FamilyName = "Railing-common test family",
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
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Geometry = SolidPrism(centreX),
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

    [Fact]
    public void Current_StairRailingHeight_IsZeroForEveryValidatedHeight()
    {
        Element railing = Product(
            id: 9200, typeId: 9201, categoryId: -2000126,
            category: "OST_StairsRailing", name: "Railing type");
        AddExactHeightSource(railing, 19200, SampleTopHeightFeet);
        Element unvalidated = Product(
            id: 9210, typeId: 9211, categoryId: -2000126,
            category: "OST_StairsRailing", name: "Railing type", centreX: 10);

        string ifc = ExportToText(IfcProfile.Current, railing, unvalidated);

        Assert.Equal(
            "IFCPOSITIVELENGTHMEASURE(0.)",
            Properties(ifc, Assert.Single(Psets(ifc, "Pset_RailingCommon"),
                pset => Properties(ifc, pset).ContainsKey("Height")))["Height"].Expression);
        Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Height',"));
    }

    private static string ExportToText(params Element[] elements) =>
        // The legacy profile contract: the current profile writes a zero Height for a stair railing.
        ExportToText(IfcProfile.Legacy, elements);

    private static string ExportToText(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"revitreader-railing-common-height-{Guid.NewGuid():N}.ifc");
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
}
