using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Slope of the current profile (SlopeCalculator): the first column of each mapped body, whose body the body exporter extrudes
/// vertically, carries 90 degrees; the other columns that reuse the mapped representation carry none, a column with a body of its own
/// carries its own. A member
/// with solids of its own takes the slope of its largest upward face.</summary>
public class IfcExporterCurrentSlopeTests
{
    private const int PhaseId = 3;

    [Fact]
    public void FirstColumnOfEachType_WithAVerticalExtrusion_ReceivesNinetyDegrees()
    {
        var type = ColumnType(901);
        var other = ColumnType(902);
        string ifc = Export(IfcProfile.Current, type, other, Symbol(700), Symbol(701), Column(100, 901, Placed(700)), Column(90, 901, Placed(700)), Column(120, 902, Placed(701)));
        Assert.Equal("IFCPLANEANGLEMEASURE(90.)", ColumnSet(ifc, 90).GetValueOrDefault("Slope"));
        Assert.Equal("IFCPLANEANGLEMEASURE(90.)", ColumnSet(ifc, 120).GetValueOrDefault("Slope"));
        Assert.DoesNotContain("Slope", ColumnSet(ifc, 100).Keys);
    }

    [Fact]
    public void ColumnsOfOneTypeWithBodiesOfTheirOwn_EachReceiveNinetyDegrees()
    {
        string ifc = Export(IfcProfile.Current, ColumnType(901), Column(100, 901, Prism()), Column(90, 901, Prism(height: 3)));
        Assert.Equal("IFCPLANEANGLEMEASURE(90.)", ColumnSet(ifc, 90).GetValueOrDefault("Slope"));
        Assert.Equal("IFCPLANEANGLEMEASURE(90.)", ColumnSet(ifc, 100).GetValueOrDefault("Slope"));
    }

    [Fact]
    public void RoundColumn_WithExactEndFacesOutsideItsShell_IsAVerticalExtrusion()
    {
        Assert.True(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(), out _));
    }

    [Fact]
    public void RoundColumn_WhoseEndFacesAreNotOneCircleOfOneArea_IsNoVerticalExtrusion()
    {
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(topShortfall: 0.05), out _));
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(topShift: 0.1), out _));
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(stray: 0.05), out _));
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(topTilt: 0.1), out _));
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(height: 0), out _));
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(extraSolid: true), out _));
    }

    [Fact]
    public void RectangularColumn_WithAnExactEndFaceOutsideItsShell_KeepsTheJudgementOfItsShell()
    {
        // the rule of the round column does not reach other geometry: the box without its top face in the shell is no extrusion, whatever
        // the faces beside the shell say
        GeometryData box = Prism();
        box.Faces[1].PlaneSurface = true;
        box.Faces[1].QuadratureArea = 12;
        box.BrepSolids = [new GeoBrepSolid { IsClosed = false, FaceIndices = [0, 2, 3, 4, 5] }];
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(box, out _));
    }

    [Fact]
    public void RoundColumn_InTheExport_ReceivesNinetyDegrees()
    {
        string ifc = Export(IfcProfile.Current, ColumnType(901), Column(100, 901, IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry()));
        Assert.Equal("IFCPLANEANGLEMEASURE(90.)", ColumnSet(ifc, 100).GetValueOrDefault("Slope"));
        ifc = Export(IfcProfile.Current, ColumnType(901), Column(100, 901, IfcExporterBaseQuantitiesCurrentTests.RoundColumnGeometry(topShortfall: 0.05)));
        Assert.DoesNotContain("Slope", ColumnSet(ifc, 100).Keys);
    }

    [Fact]
    public void BodyOfAProduct_IsMappedOwnOrUnrecognised()
    {
        static (bool Known, int? Map) Read(string representationIdentifier, string representationType, bool withShape = true, bool unknownProduct = false)
        {
            var w = new StepWriter();
            int map = w.Add("IFCREPRESENTATIONMAP", "$,$");
            int item = w.Add("IFCMAPPEDITEM", $"#{map},$");
            int representation = w.Add("IFCSHAPEREPRESENTATION", $"$,'{representationIdentifier}','{representationType}',(#{item})");
            int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
            int product = w.Add("IFCCOLUMN", $"'g',$,'P',$,$,$,{(withShape ? "#" + shape : "$")},'1'");
            var indexById = new Dictionary<int, int>();
            for (int i = 0; i < w.EntityCount; i++) indexById[w.EntityAt(i).Id] = i;
            bool known = IfcExporter.TryMappedBodySourceCurrent(w, indexById, unknownProduct ? 999 : product, out int? source);
            return (known, source == map ? -1 : source);
        }

        Assert.Equal((true, (int?)-1), Read("Body", "MappedRepresentation"));
        Assert.Equal((true, (int?)null), Read("Body", "SweptSolid"));
        Assert.Equal((false, (int?)null), Read("Axis", "Curve3D"));
        Assert.Equal((false, (int?)null), Read("Body", "MappedRepresentation", withShape: false));
        Assert.Equal((false, (int?)null), Read("Body", "MappedRepresentation", unknownProduct: true));
    }

    [Fact]
    public void ColumnWithATiltedTop_ReceivesNoSlope()
    {
        string ifc = Export(IfcProfile.Current, ColumnType(901), Column(100, 901, Prism(topTilt: 0.5)));
        Assert.DoesNotContain("Slope", ColumnSet(ifc, 100).Keys);
    }

    [Fact]
    public void Slope_IsAbsentInLegacy()
    {
        string ifc = Export(IfcProfile.Legacy, ColumnType(901), Column(100, 901, Prism()));
        Assert.DoesNotContain("'Slope'", ifc);
    }

    [Fact]
    public void VerticalExtrusion_RequiresEverySolidToPass()
    {
        GeometryData box = Prism();
        Assert.True(IfcExporter.HasVerticalExtrusionCurrent(box, out _));
        GeometryData tilted = Prism(topTilt: 0.5);
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(tilted, out _));
        GeometryData mixed = Prism();
        GeometryData failing = Prism(topTilt: 0.5);
        int shift = mixed.Faces.Count;
        mixed.Faces.AddRange(failing.Faces);
        mixed.BrepSolids!.Add(new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(shift, failing.Faces.Count).ToList() });
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(mixed, out _));
        Assert.False(IfcExporter.HasVerticalExtrusionCurrent(new GeometryData(), out _));
    }

    [Fact]
    public void LargestUpwardFace_GivesItsTiltAngle_AndNothingForAHorizontalOne()
    {
        // The roof-like prism: top face rising 1 over 2 in x -> tilt asin(sin) = atan(0.5) from horizontal.
        double? sloped = IfcExporter.LargestUpwardFaceSlopeCurrent(Prism(topTilt: 1.0, planeVectors: true));
        Assert.NotNull(sloped);
        Assert.Equal(Math.Atan(0.5) * 180 / Math.PI, sloped!.Value, 9);
        // A level box: the top face is the only upward one and its horizontal projection is zero.
        Assert.Null(IfcExporter.LargestUpwardFaceSlopeCurrent(Prism(planeVectors: true)));
        Assert.Null(IfcExporter.LargestUpwardFaceSlopeCurrent(new GeometryData()));
    }

    private static Element Symbol(int id) => new() { Id = id, IsBlockOnly = true, Geometry = Prism() };

    /// <summary>The geometry of an instance that is the geometry of the symbol, placed: the body of the instance is a mapped item.</summary>
    private static GeometryData Placed(int symbolId)
    {
        GeometryData geometry = Prism();
        geometry.ExactMappedInstances = [new GeoInstance { SymbolId = symbolId, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0] }];
        return geometry;
    }

    private static Element ColumnType(int id) => new() { Id = id, CategoryId = -2001330, Name = "Column type" };

    private static Element Column(int id, int typeId, GeometryData geometry) => new()
    {
        Id = id, TypeId = typeId, IsDiscovered = true, SuperInstanceId = 999, CategoryId = -2001330, Category = "OST_Test",
        Name = "Column type", References = [PhaseId],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = PhaseId },
            new Parameter
            {
                Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = typeId,
            },
        ],
        BoundingBox = [0, 0, 0, 4, 3, 2],
        Geometry = geometry,
    };

    /// <summary>A 4 x 3 x 2 box; a positive <paramref name="topTilt"/> raises the x = 4 edge of the top face.</summary>
    private static GeometryData Prism(double topTilt = 0, bool planeVectors = false, double height = 2)
    {
        double[][] points =
        [
            [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0],
            [0, 0, height], [4, 0, height + topTilt * 2], [4, 3, height + topTilt * 2], [0, 3, height],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        var list = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(index => points[index]).ToList() }).ToList();
        foreach (GeoFace face in list)
        {
            var v = face.OuterVertices!;
            double[] x = [v[1][0] - v[0][0], v[1][1] - v[0][1], v[1][2] - v[0][2]];
            double[] y = [v[2][0] - v[1][0], v[2][1] - v[1][1], v[2][2] - v[1][2]];
            face.Origin = (double[])v[0].Clone();
            face.Normal = [x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]];
            if (planeVectors)
            {
                face.XVector = x;
                face.YVector = y;
            }
        }
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, height + Math.Max(0, topTilt * 2)],
            Faces = list,
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-currentProfile-slope-{Guid.NewGuid():N}.ifc");
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

    /// <summary>The properties of the Pset_ColumnCommon of the column with the given Tag (its element id).</summary>
    private static Dictionary<string, string> ColumnSet(string ifc, int elementId)
    {
        Match column = Regex.Match(ifc, $@"#(\d+)=IFCCOLUMN\([^;]*,'{elementId}'\);");
        Assert.True(column.Success, $"column {elementId} not exported");
        int product = int.Parse(column.Groups[1].Value);
        foreach (Match rel in Regex.Matches(ifc, $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,'','',\(#{product}\),#(\d+)\);"))
        {
            Match pset = Regex.Match(ifc, $@"#{rel.Groups[1].Value}=IFCPROPERTYSET\('[^']*',#\d+,'Pset_ColumnCommon','',\(([^)]*)\)\);");
            if (!pset.Success) continue;
            var properties = new Dictionary<string, string>();
            foreach (Match property in Regex.Matches(pset.Groups[1].Value, @"#(\d+)"))
            {
                Match single = Assert.Single(Regex.Matches(ifc,
                    $@"#{property.Groups[1].Value}=IFCPROPERTYSINGLEVALUE\('([^']*)','',(.*),\$\);"));
                properties[single.Groups[1].Value] = single.Groups[2].Value;
            }
            return properties;
        }
        throw new Xunit.Sdk.XunitException($"column {elementId} has no Pset_ColumnCommon");
    }
}
