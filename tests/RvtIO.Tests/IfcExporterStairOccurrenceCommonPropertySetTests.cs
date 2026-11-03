using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Exact occurrence-only stair groups: emitted tagged IFCSTAIR and
/// IFCSTAIRFLIGHT products reuse their QTO Reference property entity in the corresponding
/// Common Pset. Every other Common property and every type-level Pset is out of scope.</summary>
public class IfcExporterStairOccurrenceCommonPropertySetTests
{
    [Theory]
    [InlineData(-2000120, "OST_Stairs", "IFCSTAIR", "Pset_StairCommon", "stair-common")]
    [InlineData(-2000919, "OST_StairsRuns", "IFCSTAIRFLIGHT", "Pset_StairFlightCommon", "stair-flight-common")]
    public void EmittedOccurrence_ReusesQtoReferenceEntity_AndEmitsReferenceOnly(
        int categoryId, string category, string ifcType, string psetName, string seedPrefix)
    {
        Element element = Product(4000, 4001, categoryId, category, "Exact stair reference");
        AddExcludedCommonProperties(element);

        string ifc = ExportToText(element);

        int productId = SoleEntityId(ifc, ifcType);
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, psetName);
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);

        Assert.Equal(productId, SolePsetTarget(ifc, qto.Id));
        Assert.Equal(productId, SolePsetTarget(ifc, common.Id));
        Assert.Equal(Assert.Single(qto.ValueIds), Assert.Single(common.ValueIds));
        Assert.Equal(qto.ValueIds[0], properties["Reference"].Id);
        Assert.Equal("IFCIDENTIFIER('Exact stair reference')", properties["Reference"].Expression);
        Assert.Equal(new[] { "Reference" }, properties.Keys);
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('Exact stair reference'\)"));
        Assert.Contains(IfcGuid.FromName($"{seedPrefix}-pset:4000"), ifc);
        Assert.Contains(IfcGuid.FromName($"{seedPrefix}-rel:4000"), ifc);
        Assert.DoesNotContain("'IsExternal'", ifc);
        Assert.DoesNotContain("'NumberOfRiser'", ifc);
        Assert.DoesNotContain("'NumberOfTreads'", ifc);
        Assert.DoesNotContain("'RiserHeight'", ifc);
        Assert.DoesNotContain("'TreadLength'", ifc);
        Assert.DoesNotContain("'NosingLength'", ifc);
        Assert.DoesNotContain("'WalkingLineOffset'", ifc);
        Assert.DoesNotContain("'TreadLengthAtOffset'", ifc);
        Assert.DoesNotContain("'TreadLengthAtInnerSide'", ifc);
        Assert.DoesNotContain("IFCSTAIRTYPE", ifc);
        Assert.DoesNotContain("IFCSTAIRFLIGHTTYPE", ifc);
    }

    [Fact]
    public void SharedReferenceAcrossBothClasses_PoolsOneValue_ButKeepsUniqueAttachments()
    {
        string ifc = ExportToText(
            Product(4010, 4014, -2000120, "OST_Stairs", "Shared stair reference", 0),
            Product(4011, 4014, -2000120, "OST_Stairs", "Shared stair reference", 10),
            Product(4012, 4014, -2000919, "OST_StairsRuns", "Shared stair reference", 20),
            Product(4013, 4014, -2000919, "OST_StairsRuns", "Shared stair reference", 30));

        List<Pset> qtos = Psets(ifc, "Pset_QuantityTakeOff");
        List<Pset> stairs = Psets(ifc, "Pset_StairCommon");
        List<Pset> flights = Psets(ifc, "Pset_StairFlightCommon");

        Assert.Equal(4, qtos.Count);
        Assert.Equal(2, stairs.Count);
        Assert.Equal(2, flights.Count);
        int referenceId = Assert.Single(qtos.SelectMany(pset => pset.ValueIds).Distinct());
        Assert.All(stairs.Concat(flights), common =>
            Assert.Equal(referenceId, Assert.Single(common.ValueIds)));
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('Shared stair reference'\)"));

        List<string> commonGlobalIds = Regex.Matches(
                ifc,
                @"#\d+=(?:IFCPROPERTYSET|IFCRELDEFINESBYPROPERTIES)\('([^']+)'[^\r\n]*(?:Pset_Stair|stair-(?:flight-)?common)" )
            .Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(commonGlobalIds.Count, commonGlobalIds.Distinct().Count());
        foreach (int id in new[] { 4010, 4011 })
        {
            Assert.Contains(IfcGuid.FromName($"stair-common-pset:{id}"), ifc);
            Assert.Contains(IfcGuid.FromName($"stair-common-rel:{id}"), ifc);
        }
        foreach (int id in new[] { 4012, 4013 })
        {
            Assert.Contains(IfcGuid.FromName($"stair-flight-common-pset:{id}"), ifc);
            Assert.Contains(IfcGuid.FromName($"stair-flight-common-rel:{id}"), ifc);
        }
    }

    [Theory]
    [InlineData(-2000120, "OST_Stairs")]
    [InlineData(-2000919, "OST_StairsRuns")]
    public void NullOrBlockedQtoReference_CreatesNoCommonOrphan(
        int categoryId, string category)
    {
        Element missing = Product(4020, 4022, categoryId, category, null, 0);
        Element blocked = Product(4021, 4023, categoryId, category, "Fallback", 10);
        blocked.Parameters.Add(new Parameter
        {
            Id = 7021,
            Definition = new Definition { Id = 7021, Name = "Reference" },
            StorageType = StorageType.Double,
            Value = 12.5,
        });

        string ifc = ExportToText(missing, blocked);

        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        Assert.DoesNotContain("Pset_StairCommon", ifc);
        Assert.DoesNotContain("Pset_StairFlightCommon", ifc);
        Assert.DoesNotContain("stair-common-pset", ifc);
        Assert.DoesNotContain("stair-flight-common-pset", ifc);
    }

    [Fact]
    public void AdjacentClass_KeepsQtoButReceivesNeitherStairCommonPset()
    {
        string ifc = ExportToText(Product(
            4030, 4031, -2000126, "OST_StairsRailing", "Railing reference"));

        Assert.True(SoleEntityId(ifc, "IFCRAILING") > 0);
        Assert.Single(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.DoesNotContain("Pset_StairCommon", ifc);
        Assert.DoesNotContain("Pset_StairFlightCommon", ifc);
    }

    [Theory]
    [InlineData(-2000120, "OST_Stairs")]
    [InlineData(-2000919, "OST_StairsRuns")]
    public void GeometrylessOccurrence_CreatesNoProductOrOccurrencePset(
        int categoryId, string category)
    {
        Element element = Product(4040, 4041, categoryId, category, "Suppressed stair");
        element.Geometry = null;

        string ifc = ExportToText(element);

        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        Assert.DoesNotContain("Pset_StairCommon", ifc);
        Assert.DoesNotContain("Pset_StairFlightCommon", ifc);
    }

    private static void AddExcludedCommonProperties(Element element)
    {
        (string Name, StorageType Storage, object Value)[] excluded =
        [
            ("IsExternal", StorageType.Integer, 1),
            ("NumberOfRiser", StorageType.Integer, 17),
            ("NumberOfTreads", StorageType.Integer, 16),
            ("RiserHeight", StorageType.Double, 0.5),
            ("TreadLength", StorageType.Double, 1.0),
            ("NosingLength", StorageType.Double, 0.0),
            ("WalkingLineOffset", StorageType.Double, 0.2),
            ("TreadLengthAtOffset", StorageType.Double, 1.0),
            ("TreadLengthAtInnerSide", StorageType.Double, 1.0),
        ];
        int id = 7100;
        foreach ((string name, StorageType storage, object value) in excluded)
        {
            element.Parameters.Add(new Parameter
            {
                Id = id,
                Definition = new Definition { Id = id++, Name = name },
                StorageType = storage,
                Value = value,
            });
        }
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

    internal static Element Product(
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
            FamilyName = "Stair family",
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

    private static string ExportToText(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"revitreader-stair-common-{Guid.NewGuid():N}.ifc");
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
