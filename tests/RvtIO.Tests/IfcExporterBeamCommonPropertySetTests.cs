using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Occurrence-only Pset_BeamCommon: every tagged IFCBEAM reuses the exact QTO
/// Reference entity and carries one pooled true LoadBearing value. The other BeamCommon
/// properties and all type-level Psets are not covered here.</summary>
public class IfcExporterBeamCommonPropertySetTests
{
    [Fact]
    public void StructuralBeam_ReusesQtoReferenceAndEmitsValidatedLoadBearing()
    {
        Element beam = Product(
            id: 5000, typeId: 5001, categoryId: -2001320,
            category: "OST_StructuralFraming", name: "300x460");
        AddStillExcludedBeamCommonProperties(beam);

        string ifc = ExportToText(beam);

        int beamId = SoleEntityId(ifc, "IFCBEAM");
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_BeamCommon");
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);

        Assert.Equal(beamId, SolePsetTarget(ifc, qto.Id));
        Assert.Equal(beamId, SolePsetTarget(ifc, common.Id));
        Assert.Equal(Assert.Single(qto.ValueIds), properties["Reference"].Id);
        Assert.Equal(qto.ValueIds[0], properties["Reference"].Id);
        Assert.Equal("IFCIDENTIFIER('300x460')", properties["Reference"].Expression);
        Assert.Equal("IFCBOOLEAN(.T.)", properties["LoadBearing"].Expression);
        Assert.Equal(new[] { "Reference", "LoadBearing" }, properties.Keys);
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('300x460'\)"));
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('LoadBearing',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));
        Assert.DoesNotContain("'IsExternal'", ifc);
        Assert.DoesNotContain("'Slope'", ifc);
        Assert.DoesNotContain("'Span'", ifc);
        Assert.Contains(IfcGuid.FromName("beam-common-pset:5000"), ifc);
        Assert.Contains(IfcGuid.FromName("beam-common-rel:5000"), ifc);
        Assert.DoesNotContain("IFCBEAMTYPE", ifc);
    }

    [Fact]
    public void SharedReferenceAcrossBeams_PoolsValue_ButKeepsUniqueAttachments()
    {
        string ifc = ExportToText(
            Product(5010, 5012, -2001320, "OST_StructuralFraming", "Shared beam", 0),
            Product(5011, 5012, -2001320, "OST_StructuralFraming", "Shared beam", 10));

        int[] beamIds = EntityIds(ifc, "IFCBEAM").OrderBy(id => id).ToArray();
        List<Pset> qtos = Psets(ifc, "Pset_QuantityTakeOff");
        List<Pset> commons = Psets(ifc, "Pset_BeamCommon");

        Assert.Equal(2, beamIds.Length);
        Assert.Equal(2, qtos.Count);
        Assert.Equal(2, commons.Count);
        int referenceId = Assert.Single(qtos.SelectMany(pset => pset.ValueIds).Distinct());
        Assert.All(commons, common => Assert.Equal(
            referenceId,
            Properties(ifc, common)["Reference"].Id));
        Assert.Single(commons.Select(common =>
            Properties(ifc, common)["LoadBearing"].Id).Distinct());
        Assert.Equal(
            beamIds,
            commons.Select(pset => SolePsetTarget(ifc, pset.Id))
                .OrderBy(id => id).ToArray());
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('Shared beam'\)"));
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('LoadBearing',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));

        foreach (int id in new[] { 5010, 5011 })
        {
            Assert.Contains(IfcGuid.FromName($"beam-common-pset:{id}"), ifc);
            Assert.Contains(IfcGuid.FromName($"beam-common-rel:{id}"), ifc);
        }
        List<string> commonGlobalIds = Regex.Matches(
                ifc,
                @"#\d+=(?:IFCPROPERTYSET|IFCRELDEFINESBYPROPERTIES)\('([^']+)'[^\r\n]*(?:Pset_BeamCommon|beam-common)")
            .Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(commonGlobalIds.Count, commonGlobalIds.Distinct().Count());
    }

    [Fact]
    public void TrueLoadBearingPool_IsSharedWithColumnCommon()
    {
        string ifc = ExportToText(
            Product(5013, 5023, -2001320, "OST_StructuralFraming", "Beam", 0),
            Product(5014, 5024, -2001330, "OST_StructuralColumns", "Column", 10));

        int beamLoadBearingId = Properties(
            ifc, SolePset(ifc, "Pset_BeamCommon"))["LoadBearing"].Id;
        int columnLoadBearingId = Properties(
            ifc, SolePset(ifc, "Pset_ColumnCommon"))["LoadBearing"].Id;

        Assert.Equal(beamLoadBearingId, columnLoadBearingId);
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('LoadBearing',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));
    }

    [Fact]
    public void DirectOccurrenceFalse_WinsBeforeStructuralFramingCalculator()
    {
        Element beam = Product(
            id: 5015, typeId: 5016, categoryId: -2001320,
            category: "OST_StructuralFraming", name: "Non-bearing beam");
        beam.Parameters.Add(BooleanParameter(
            id: 7050, name: "Load Bearing", value: 0));

        string ifc = ExportToText(beam);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, "Pset_BeamCommon");

        Assert.Equal("IFCBOOLEAN(.F.)", properties["LoadBearing"].Expression);
        Assert.DoesNotContain(
            "IFCPROPERTYSINGLEVALUE('LoadBearing',$,IFCBOOLEAN(.T.),$)",
            ifc);
    }

    [Fact]
    public void BlockedOrConflictingOccurrenceValue_OmitsWithoutCategoryFallback()
    {
        Element approximate = Product(
            id: 5017, typeId: 5019, categoryId: -2001320,
            category: "OST_StructuralFraming", name: "Approximate bearing", centreX: 0);
        approximate.Parameters.Add(new Parameter
        {
            Id = 7060,
            Definition = new Definition { Id = 7060, Name = "LoadBearing" },
            StorageType = StorageType.Integer,
            Value = 1,
            IsApproximate = true,
        });
        Element conflicting = Product(
            id: 5018, typeId: 5020, categoryId: -2001320,
            category: "OST_StructuralFraming", name: "Conflicting bearing", centreX: 10);
        conflicting.Parameters.Add(BooleanParameter(7061, "LoadBearing", 0));
        conflicting.Parameters.Add(BooleanParameter(7062, "Load Bearing", 1));

        string ifc = ExportToText(approximate, conflicting);
        List<Pset> commons = Psets(ifc, "Pset_BeamCommon");

        Assert.Equal(2, commons.Count);
        Assert.All(commons, common => Assert.Equal(
            new[] { "Reference" },
            Properties(ifc, common).Keys));
        Assert.DoesNotContain("'LoadBearing'", ifc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOrBlockedQtoReference_CreatesNoBeamCommonOrphan(bool blocked)
    {
        Element beam = Product(
            id: 5020, typeId: 5021, categoryId: -2001320,
            category: "OST_StructuralFraming",
            name: blocked ? "Fallback beam reference" : null);
        if (blocked)
        {
            beam.Parameters.Add(new Parameter
            {
                Id = 7020,
                Definition = new Definition { Id = 7020, Name = "Reference" },
                StorageType = StorageType.Double,
                Value = 12.5,
            });
        }

        string ifc = ExportToText(beam);

        Assert.True(SoleEntityId(ifc, "IFCBEAM") > 0);
        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        Assert.DoesNotContain("Pset_BeamCommon", ifc);
        Assert.DoesNotContain("beam-common-pset", ifc);
        Assert.DoesNotContain("beam-common-rel", ifc);
    }

    [Fact]
    public void ExplicitReference_FlowsThroughExistingResolverIntoBothPsets()
    {
        Element beam = Product(
            id: 5030, typeId: 5031, categoryId: -2001320,
            category: "OST_StructuralFraming", name: "Fallback beam name");
        beam.Parameters.Add(new Parameter
        {
            Id = 7030,
            Definition = new Definition { Id = 7030, Name = "Re fer ence" },
            StorageType = StorageType.String,
            Value = "Authored beam reference",
        });

        string ifc = ExportToText(beam);
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_BeamCommon");

        Assert.Equal(
            Assert.Single(qto.ValueIds),
            Properties(ifc, common)["Reference"].Id);
        Assert.Equal(
            "IFCIDENTIFIER('Authored beam reference')",
            Properties(ifc, common)["Reference"].Expression);
        Assert.DoesNotContain("IFCIDENTIFIER('Fallback beam name')", ifc);
    }

    [Fact]
    public void SampleStyleModelWithoutBeam_IsExactEmptyControlForBeamCommon()
    {
        string ifc = ExportToText(
            Product(5040, 5043, -2001330, "OST_StructuralColumns", "Column", 0),
            Product(5041, 5044, -2000126, "OST_StairsRailing", "Railing", 10),
            Product(5042, 5045, -2000014, "OST_Windows", "Window", 20));

        Assert.Empty(EntityIds(ifc, "IFCBEAM"));
        Assert.Equal(3, Psets(ifc, "Pset_QuantityTakeOff").Count);
        Assert.Empty(Psets(ifc, "Pset_BeamCommon"));
        Assert.DoesNotContain("beam-common-pset", ifc);
        Assert.DoesNotContain("beam-common-rel", ifc);
    }

    [Fact]
    public void GeometrylessBeam_CreatesNoProductQtoOrBeamCommon()
    {
        Element beam = Product(
            id: 5050, typeId: 5051, categoryId: -2001320,
            category: "OST_StructuralFraming", name: "Suppressed beam");
        beam.Geometry = null;

        string ifc = ExportToText(beam);

        Assert.Empty(EntityIds(ifc, "IFCBEAM"));
        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        Assert.DoesNotContain("Pset_BeamCommon", ifc);
        Assert.DoesNotContain("beam-common-pset", ifc);
        Assert.DoesNotContain("beam-common-rel", ifc);
    }

    private static void AddStillExcludedBeamCommonProperties(Element beam)
    {
        (string Name, StorageType Storage, object Value)[] excluded =
        [
            ("IsExternal", StorageType.Integer, 1),
            ("Slope", StorageType.Double, 0.125),
            ("Span", StorageType.Double, 30.0),
        ];
        int id = 7100;
        foreach ((string name, StorageType storage, object value) in excluded)
        {
            beam.Parameters.Add(new Parameter
            {
                Id = id,
                Definition = new Definition { Id = id++, Name = name },
                StorageType = storage,
                Value = value,
            });
        }
    }

    private static Parameter BooleanParameter(int id, string name, int value) =>
        new()
        {
            Id = id,
            Definition = new Definition { Id = id, Name = name },
            StorageType = StorageType.Integer,
            Value = value,
        };

    private static Dictionary<string, PropertyValue> Properties(
        string ifc, string psetName) =>
        Properties(ifc, SolePset(ifc, psetName));

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
            FamilyName = "Common-property test family",
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
            $"revitreader-beam-common-{Guid.NewGuid():N}.ifc");
        try
        {
            // The legacy profile contract: the current profile also writes IsExternal into the beam common set.
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BeamSystemAssembly_IsCountedAsExportedAndTakenOutOfTheSkippedElements(bool withSkipLog)
    {
        var system = new Element
        {
            Id = 5200, CategoryId = -2001327, Category = "OST_StructuralFramingSystem",
            JoistSystem = new RvtIO.Parsing.JoistSystemData(1, "Joist type", [5100, 5101]),
        };
        Element[] elements = [Product(5100, 5102, -2001320, "OST_StructuralFraming", "Joist", 0),
            Product(5101, 5102, -2001320, "OST_StructuralFraming", "Joist", 10), system];
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-joist-{Guid.NewGuid():N}.ifc");
        var skipLog = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = withSkipLog ? skipLog : null };
        try
        {
            exporter.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Single(Regex.Matches(ifc, @"IFCELEMENTASSEMBLY\("));
            Assert.Equal(2, Regex.Matches(ifc, @"=IFCBEAM\(").Count);
            Assert.Equal(3, exporter.ExportedProducts);
            Assert.Equal(0, skipLog.Total);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
