using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Exact occurrence-only Pset_ColumnCommon group: a successfully emitted,
/// tagged IFCCOLUMN with QTO reuses that QTO Reference property, resolves IsExternal
/// through the shared exact cache, and emits LoadBearing only from an exact occurrence
/// value or the structural-column category calculator branch.</summary>
public class IfcExporterColumnCommonPropertySetTests
{
    [Fact]
    public void StructuralColumn_ReusesQtoReferenceAndEmitsOnlyValidatedProperties()
    {
        Element column = Product(
            id: 2000, typeId: 2001, categoryId: -2001330,
            name: "Structural column type");
        string ifc = ExportToText(column);

        int columnId = SoleEntityId(ifc, "IFCCOLUMN");
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_ColumnCommon");
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);

        Assert.Equal(columnId, SolePsetTarget(ifc, qto.Id));
        Assert.Equal(columnId, SolePsetTarget(ifc, common.Id));
        Assert.Equal(qto.ValueIds[0], properties["Reference"].Id);
        Assert.Equal(
            "IFCIDENTIFIER('Structural column type')",
            properties["Reference"].Expression);
        Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        Assert.Equal("IFCBOOLEAN(.T.)", properties["LoadBearing"].Expression);
        Assert.Equal(
            new[] { "Reference", "IsExternal", "LoadBearing" },
            properties.Keys);
        Assert.Contains(
            $"IFCPROPERTYSET('{IfcGuid.FromName("column-common-pset:2000")}',",
            ifc);
        Assert.Contains(
            $"IFCRELDEFINESBYPROPERTIES('{IfcGuid.FromName("column-common-rel:2000")}',",
            ifc);
        Assert.DoesNotContain("'Slope'", ifc);
        Assert.DoesNotContain("'FireRating'", ifc);
        Assert.DoesNotContain("IFCCOLUMNTYPE", ifc);
    }

    [Fact]
    public void SameValues_PoolReferenceAndBooleansAcrossOccurrencePsets()
    {
        string ifc = ExportToText(
            Product(2010, 2012, -2001330, "Shared column type", centreX: 0),
            Product(2011, 2012, -2001330, "Shared column type", centreX: 10));

        List<Pset> qtos = Psets(ifc, "Pset_QuantityTakeOff");
        List<Pset> commons = Psets(ifc, "Pset_ColumnCommon");
        Assert.Equal(2, qtos.Count);
        Assert.Equal(2, commons.Count);

        int qtoReferenceId = Assert.Single(
            qtos.Select(pset => pset.ValueIds[0]).Distinct());
        Assert.All(commons, common =>
        {
            Dictionary<string, PropertyValue> properties = Properties(ifc, common);
            Assert.Equal(qtoReferenceId, properties["Reference"].Id);
        });
        Assert.Single(commons.Select(common =>
            Properties(ifc, common)["IsExternal"].Id).Distinct());
        Assert.Single(commons.Select(common =>
            Properties(ifc, common)["LoadBearing"].Id).Distinct());
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('IsExternal',(?:\$|''),IFCBOOLEAN\(\.F\.\),\$\)"));
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('LoadBearing',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));
        List<string> globalIds = Regex.Matches(
                ifc,
                @"#\d+=(?:IFCPROPERTYSET|IFCRELDEFINESBYPROPERTIES)\('([^']+)'")
            .Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(globalIds.Count, globalIds.Distinct().Count());
        Assert.DoesNotContain("IFCCOLUMNTYPE", ifc);
    }

    [Fact]
    public void IsExternalBooleanPool_IsSharedWithExistingCommonPsets()
    {
        string ifc = ExportToText(
            Product(2015, 2017, -2001330, "Column type", centreX: 0),
            Product(2016, 2018, -2000170, "Panel type", centreX: 10));

        Dictionary<string, PropertyValue> columnProperties = Properties(
            ifc, "Pset_ColumnCommon");
        Dictionary<string, PropertyValue> plateProperties = Properties(
            ifc, "Pset_PlateCommon");

        Assert.Equal(
            columnProperties["IsExternal"].Id,
            plateProperties["IsExternal"].Id);
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('IsExternal',(?:\$|''),IFCBOOLEAN\(\.F\.\),\$\)"));
    }

    [Theory]
    [InlineData(-2001330, 0, ".F.")]
    [InlineData(-2000100, 1, ".T.")]
    public void DirectOccurrenceLoadBearing_WinsBeforeCategoryCalculator(
        int categoryId, int value, string expectedLiteral)
    {
        Element column = Product(
            id: 2020, typeId: 2021, categoryId,
            name: "Direct load-bearing column");
        column.Parameters.Add(BooleanParameter(
            id: 8001, name: "Load Bearing", value));

        Dictionary<string, PropertyValue> properties = Properties(
            ExportToText(column), "Pset_ColumnCommon");

        Assert.Equal(
            $"IFCBOOLEAN({expectedLiteral})",
            properties["LoadBearing"].Expression);
    }

    [Fact]
    public void IsExternalUsesResolvedType_ButTypeLoadBearingCannotOverrideCategory()
    {
        var columnType = new Element
        {
            Id = 2031,
            Parameters =
            [
                BooleanParameter(8101, "IsExternal", 1),
                BooleanParameter(8102, "LoadBearing", 0),
            ],
        };
        Element column = Product(
            id: 2030, typeId: 2032, categoryId: -2001330,
            name: "Typed structural column");
        column.Parameters.Add(TypeLink(columnType.Id));

        string ifc = ExportToText(columnType, column);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, "Pset_ColumnCommon");

        Assert.Equal("IFCBOOLEAN(.T.)", properties["IsExternal"].Expression);
        Assert.Equal("IFCBOOLEAN(.T.)", properties["LoadBearing"].Expression);
        Assert.DoesNotContain(
            "IFCPROPERTYSINGLEVALUE('LoadBearing',$,IFCBOOLEAN(.F.),$)",
            ifc);
    }

    [Fact]
    public void DirectIsExternalValueFlowsThroughSharedExactResolver()
    {
        Element column = Product(
            id: 2040, typeId: 2041, categoryId: -2001330,
            name: "Exterior column");
        column.Parameters.Add(BooleanParameter(
            id: 8201, name: "is external", value: 1));

        Dictionary<string, PropertyValue> properties = Properties(
            ExportToText(column), "Pset_ColumnCommon");

        Assert.Equal("IFCBOOLEAN(.T.)", properties["IsExternal"].Expression);
    }

    [Theory]
    [InlineData(0, 1, "IFCBOOLEAN(.F.)")]
    [InlineData(1, 0, "IFCBOOLEAN(.T.)")]
    public void SetQualifiedLoadBearing_WinsOverThePropertyName(int qualified, int plain, string expression)
    {
        Element column = Product(
            id: 2060, typeId: 2062, categoryId: -2001330,
            name: "Qualified column", centreX: 0);
        column.Parameters.Add(BooleanParameter(8310, "LoadBearing", plain));
        column.Parameters.Add(BooleanParameter(8311, "Pset_ColumnCommon.LoadBearing", qualified));

        string ifc = ExportToText(column);

        Assert.Equal(expression, Properties(ifc, Psets(ifc, "Pset_ColumnCommon").Single())["LoadBearing"].Expression);
    }

    [Fact]
    public void BlockedOrConflictingOccurrenceLoadBearing_OmitsWithoutCategoryFallback()
    {
        Element approximate = Product(
            id: 2050, typeId: 2052, categoryId: -2001330,
            name: "Approximate load-bearing column", centreX: 0);
        approximate.Parameters.Add(new Parameter
        {
            Id = 8301,
            Definition = new Definition { Id = 8301, Name = "LoadBearing" },
            StorageType = StorageType.Integer,
            Value = 1,
            IsApproximate = true,
        });
        Element conflicting = Product(
            id: 2051, typeId: 2053, categoryId: -2001330,
            name: "Conflicting load-bearing column", centreX: 10);
        conflicting.Parameters.Add(BooleanParameter(8302, "LoadBearing", 0));
        conflicting.Parameters.Add(BooleanParameter(8303, "Load Bearing", 1));

        string ifc = ExportToText(approximate, conflicting);
        List<Pset> commons = Psets(ifc, "Pset_ColumnCommon");

        Assert.Equal(2, commons.Count);
        Assert.All(commons, common => Assert.Equal(
            new[] { "Reference", "IsExternal" },
            Properties(ifc, common).Keys));
        Assert.DoesNotContain("'LoadBearing'", ifc);
    }

    [Fact]
    public void WrongStorageOrUnresolvedOccurrenceLoadBearing_OmitsWithoutCategoryFallback()
    {
        (StorageType StorageType, object? Value)[] invalidSource =
        [
            (StorageType.String, "invalid"),
            (StorageType.String, "1"),
            (StorageType.Double, 1.0),
            (StorageType.ElementId, 1),
            (StorageType.None, null),
            (StorageType.Integer, null),
        ];
        Element[] columns = invalidSource.Select((source, index) =>
        {
            Element column = Product(
                id: 2054 + index, typeId: 2064 + index,
                categoryId: -2001330,
                name: $"Invalid load-bearing column {index}",
                centreX: index * 10);
            column.Parameters.Add(new Parameter
            {
                Id = 8350 + index,
                Definition = new Definition
                {
                    Id = 8350 + index,
                    Name = "LoadBearing",
                },
                StorageType = source.StorageType,
                Value = source.Value,
            });
            return column;
        }).ToArray();

        string ifc = ExportToText(columns);
        List<Pset> commons = Psets(ifc, "Pset_ColumnCommon");

        Assert.Equal(columns.Length, commons.Count);
        Assert.All(commons, common => Assert.Equal(
            new[] { "Reference", "IsExternal" },
            Properties(ifc, common).Keys));
        Assert.DoesNotContain("'LoadBearing'", ifc);
    }

    [Fact]
    public void ArchitecturalColumnWithoutDirectValue_EmitsReferenceAndIsExternalOnly()
    {
        string ifc = ExportToText(Product(
            id: 2060, typeId: 2061, categoryId: -2000100,
            name: "Architectural column"));
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, "Pset_ColumnCommon");

        Assert.Equal(
            new[] { "Reference", "IsExternal" },
            properties.Keys);
        Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        Assert.DoesNotContain("'LoadBearing'", ifc);
    }

    [Fact]
    public void QtoLessColumnAndTaggedNonColumn_CreateNoColumnCommonOrphan()
    {
        Element suppressedColumn = Product(
            2072, 2075, -2001330, "Suppressed column", centreX: 20);
        suppressedColumn.Geometry = null;
        string ifc = ExportToText(
            Product(2070, 2072, -2001330, name: null, centreX: 0),
            Product(2071, 2073, -2001320, name: "Beam type", centreX: 10),
            suppressedColumn);

        Assert.Single(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.Single(Regex.Matches(ifc, "=IFCCOLUMN\\("));
        Assert.DoesNotContain("Pset_ColumnCommon", ifc);
        Assert.DoesNotContain("column-common-pset", ifc);
        Assert.DoesNotContain("column-common-rel", ifc);
    }

    private sealed record Pset(int Id, IReadOnlyList<int> ValueIds);

    private sealed record PropertyValue(int Id, string Expression);

    private static Dictionary<string, PropertyValue> Properties(
        string ifc, string psetName) => Properties(ifc, SolePset(ifc, psetName));

    private static Dictionary<string, PropertyValue> Properties(
        string ifc, Pset pset)
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

    private static Parameter BooleanParameter(
        int id, string name, int value) => new()
    {
        Id = id,
        Definition = new Definition { Id = id, Name = name },
        StorageType = StorageType.Integer,
        Value = value,
    };

    private static Parameter TypeLink(int typeId) => new()
    {
        Id = -1002050,
        Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId,
        Value = typeId,
    };

    private static Element Product(
        int id, int typeId, int categoryId, string? name, double centreX = 0) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            IsDiscovered = true,
            CategoryId = categoryId,
            Category = categoryId switch
            {
                -2001330 => "OST_StructuralColumns",
                -2000100 => "OST_Columns",
                _ => "OST_StructuralFraming",
            },
            Name = name,
            FamilyName = "Test family",
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
            $"revitreader-column-common-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
