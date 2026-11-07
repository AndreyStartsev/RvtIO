using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile's IfcGrid export: grouping into orthogonal pairs and triples and the GlobalId.</summary>
public class IfcExporterGridTests
{
    internal static Element Grid(int id, string label, double x0, double y0, double x1, double y1, double z = 4.0,
        (double Min, double Max)? interval = null) => new()
    {
        Id = id,
        CategoryId = -2000220,
        Category = "OST_Grids",
        Name = "6.5mm Bubble",
        Parameters = [new Parameter { Id = -1008000, StorageType = StorageType.String, Value = label }],
        Location = new LocationCurve { CurveType = "Line", StartPoint = [x0, y0, z], EndPoint = [x1, y1, z] },
        SerializedGridZInterval = interval,
    };

    private static IReadOnlyList<IfcExporter.GridLine> Lines(params Element[] grids) => IfcExporter.CollectGridLines(grids);

    [Fact]
    public void Rectangular_TwoOrthogonalFamilies_OneGridWithAntiParallelLinesJoined()
    {
        var lines = Lines(
            Grid(1, "A", 0, 0, 10, 0), Grid(2, "B", 10, 5, 0, 5),    // along x: opposite senses, one family pair
            Grid(3, "1", 0, 0, 0, 10), Grid(4, "2", 5, 10, 5, 0));   // along y, both senses
        var sets = IfcExporter.SplitGridLines(lines);

        // keys in x order: (-1, 0), (0, -1), (0, 1), (1, 0); the first orthogonal pair is (-1, 0) and (0, -1),
        // each followed by its anti-parallel family.
        var set = Assert.Single(sets);
        Assert.Equal(["B", "A"], set.U.Select(line => line.Label).ToArray());
        Assert.Equal(["2", "1"], set.V.Select(line => line.Label).ToArray());
        Assert.Empty(set.W);
    }

    [Fact]
    public void Triangular_ThreeDirectionFamilies_UVAndWInDirectionOrder()
    {
        // directions (-1, 0), (0, -1) and (1, 0): three keys, so no rectangular pass; they go out as U, V, W.
        var lines = Lines(
            Grid(1, "D", 5, 0, 0, 0), Grid(2, "E", 5, 1, 0, 1),
            Grid(3, "1", 0, 5, 0, 0),
            Grid(4, "A", 0, 2, 5, 2));
        var set = Assert.Single(IfcExporter.SplitGridLines(lines));

        Assert.Equal(["D", "E"], set.U.Select(line => line.Label).ToArray());
        Assert.Equal(["1"], set.V.Select(line => line.Label).ToArray());
        Assert.Equal(["A"], set.W.Select(line => line.Label).ToArray());
    }

    [Fact]
    public void FourFamilies_RectangularPairFirstThenTheRestAsATriple()
    {
        // two orthogonal axis-aligned directions with their opposites, plus a 45 degree line and its opposite
        var lines = Lines(
            Grid(1, "A", 0, 0, 10, 0), Grid(2, "1", 0, 0, 0, 10),
            Grid(3, "AA", 0, 0, 5, 5), Grid(4, "BB", 5, 5, 0, 0));
        var sets = IfcExporter.SplitGridLines(lines);

        // keys in x order: (-0.707, -0.707) BB, (0, 1) 1, (0.707, 0.707) AA, (1, 0) A. Nothing is orthogonal to the
        // first; the first orthogonal pair is (0, 1) with (1, 0). The two 45 degree directions are left and go out as
        // U and V of a triangular grid (they are not merged as anti-parallel there).
        Assert.Equal(2, sets.Count);
        Assert.Equal(["1"], sets[0].U.Select(line => line.Label).ToArray());
        Assert.Equal(["A"], sets[0].V.Select(line => line.Label).ToArray());
        Assert.Equal(["BB"], sets[1].U.Select(line => line.Label).ToArray());
        Assert.Equal(["AA"], sets[1].V.Select(line => line.Label).ToArray());
        Assert.Empty(sets[1].W);
    }

    [Fact]
    public void ASingleDirection_IsNotAGrid()
    {
        Assert.Empty(IfcExporter.SplitGridLines(Lines(Grid(1, "A", 0, 0, 10, 0), Grid(2, "B", 0, 5, 10, 5))));
    }

    [Fact]
    public void ExactDuplicates_AreCollectedOnce_AndCurvedOrDegenerateLinesAreDropped()
    {
        Element duplicate = Grid(2, "A", 0, 0, 10, 0);
        Element point = Grid(3, "P", 1, 1, 1, 1);
        Element arc = Grid(4, "R", 0, 0, 1, 1);
        arc.Location!.ArcRadius = 5.0;

        var lines = Lines(Grid(1, "A", 0, 0, 10, 0), duplicate, point, arc);

        Assert.Single(lines);
        Assert.Equal(1, lines[0].Element.Id);
    }

    [Fact]
    public void MainPlane_KeepsOnlyTheGridsOfTheMostCommonPlane()
    {
        var lines = Lines(
            Grid(1, "A", 0, 0, 10, 0, z: 4.0), Grid(2, "B", 0, 5, 10, 5, z: 4.0), Grid(3, "1", 0, 0, 0, 5, z: 4.0000000000000009),
            Grid(4, "x", 0, 0, 3, 3, z: -8.0), Grid(5, "y", 0, 0, 3, -3, z: -8.0));

        var main = IfcExporter.MainPlaneGridLines(lines);

        Assert.Equal(["A", "B", "1"], main.Select(line => line.Label).ToArray());
    }

    [Fact]
    public void DirectionText_IsPrintfSixDecimals_KeepingTheSignOfZero()
    {
        Assert.Equal("-1.000000; 0.000000; 0.000000", IfcExporter.GridDirectionText([-1.0, 0.0, 0.0]));
        Assert.Equal("-0.000000; -1.000000; -0.000000", IfcExporter.GridDirectionText([-0.0, -1.0, -0.0]));
        Assert.Equal("-0.000000; -1.000000; 0.000000", IfcExporter.GridDirectionText([-1.68e-15, -1.0, 0.0]));
        Assert.Equal("-0.707107; 0.707107; 0.000000", IfcExporter.GridDirectionText([-Math.Sqrt(0.5), Math.Sqrt(0.5), 0.0]));
    }

    [Fact]
    public void GlobalId_IsTheMd5OfTheFirstByteOfTheNameTheDirectionKeyAndTheStoreyGlobalId()
    {
        string key = "-1.000000; 0.000000; 0.000000:-0.000000; -1.000000; -0.000000:1.000000; -0.000000; -0.000000";

        Assert.Equal("2evLhyez7jKG33tgyAcHEb", IfcExporter.GridGlobalId("Default Grid", key, "3Zu5Bv0LOHrPC10026FoQQ"));
        Assert.Equal("0GXWUMoqDDhvLFq2To6XEB", IfcExporter.GridGlobalId("Default Grid", key, "15Z0v90RiHrPC20026FoKR"));
        string pair = "-1.000000; 0.000000; -0.000000:-0.000000; -1.000000; 0.000000";
        Assert.NotEqual(IfcExporter.GridGlobalId("Default Grid", pair, "3Zu5Bv0LOHrPC10026FoQQ"),
            IfcExporter.GridGlobalId("Default Grid", key, "3Zu5Bv0LOHrPC10026FoQQ"));
    }

    /// <summary>A grid whose datum plane stands at <paramref name="originZ"/> and reaches <paramref name="vMin"/> to
    /// <paramref name="vMax"/> along the vertical.</summary>
    private static Element PlanedGrid(int id, string label, double x0, double y0, double x1, double y1,
        double originZ, double vMin, double vMax)
    {
        var grid = Grid(id, label, x0, y0, x1, y1, originZ);
        grid.GridPlane = new GridPlaneData([0, vMin, 10, vMax], [x0, y0, originZ], [1, 0, 0], [0, 0, 1]);
        return grid;
    }

    [Fact]
    public void GridZInterval_IsTheEnvelopeRangeAlongTheVerticalFromThePlaneOrigin()
    {
        var grid = PlanedGrid(1, "A", 0, 0, 10, 0, originZ: 3.9, vMin: -4.9, vMax: 34.1);

        var interval = IfcExporter.GridZInterval(grid);

        Assert.NotNull(interval);
        Assert.Equal(-1.0, interval.Value.Lower, 9);
        Assert.Equal(38.0, interval.Value.Upper, 9);
        Assert.Null(IfcExporter.GridZInterval(Grid(2, "B", 0, 0, 10, 0)));
    }

    [Fact]
    public void ExportCurrent_AGridStandsOnlyOnTheLevelsItsPlaneReaches()
    {
        // levels at 0, 10 and 20 feet; the plane reaches z in [-1, 12]: the first two levels
        var model = new[]
        {
            Level(1, "Ground", 0.0), Level(2, "Upper", 10.0), Level(3, "Top", 20.0),
            PlanedGrid(10, "A", 0, 0, 10, 0, 4, -5, 8), PlanedGrid(11, "B", 0, 5, 10, 5, 4, -5, 8),
            PlanedGrid(12, "1", 0, 0, 0, 5, 4, -5, 8), PlanedGrid(13, "2", 5, 0, 5, 5, 4, -5, 8),
        };

        string ifc = Export(IfcProfile.Current, model);

        Assert.Equal(2, Regex.Matches(ifc, @"=IFCGRID\(").Count);
        Assert.Equal(8, Regex.Matches(ifc, @"IFCGRIDAXIS\(").Count);
    }

    [Fact]
    public void ExportCurrent_ALineThatReachesOneLevelOnlyChangesThatLevelsFamilies()
    {
        // The diagonal "x" reaches only the ground level: there it is a third direction family and the two orthogonal
        // pairs (rectangular grid) become a different key than on the upper level.
        var model = new[]
        {
            Level(1, "Ground", 0.0), Level(2, "Upper", 10.0),
            PlanedGrid(10, "A", 0, 0, 10, 0, 4, -5, 8), PlanedGrid(11, "1", 0, 0, 0, 5, 4, -5, 8),
            PlanedGrid(12, "x", 0, 0, 3, 3, 4, -5, 2),
        };

        string ifc = Export(IfcProfile.Current, model);

        // Ground: three families, one triangular grid with all three lines. Upper: two families, one rectangular grid.
        Assert.Equal(2, Regex.Matches(ifc, @"=IFCGRID\(").Count);
        Assert.Equal(5, Regex.Matches(ifc, @"IFCGRIDAXIS\(").Count);
    }

    [Fact]
    public void CollectGridLines_DropsADatumPlaneWithoutACategory()
    {
        Element uncategorised = Grid(2, "Ag", 0, 5, 10, 5);
        uncategorised.Parameters.Add(new Parameter { Id = -1140362, StorageType = StorageType.ElementId, Value = -1 });
        Element categorised = Grid(3, "B", 0, 7, 10, 7);
        categorised.Parameters.Add(new Parameter { Id = -1140362, StorageType = StorageType.ElementId, Value = -2000220 });

        var lines = Lines(Grid(1, "A", 0, 0, 10, 0), uncategorised, categorised);

        Assert.Equal(["A", "B"], lines.Select(line => line.Label).ToArray());
    }

    [Fact]
    public void CollectGridLines_TakesTheStoredDirectionWhenItRunsAlongTheLine()
    {
        Element stored = Grid(1, "A", 10, 0, 0, 0);
        stored.Location!.Direction = [-1.0, 3.2e-15, -0.0];
        Element opposite = Grid(2, "B", 10, 1, 0, 1);
        opposite.Location!.Direction = [1.0, 0.0, 0.0];   // against the end points: ignored

        var lines = Lines(stored, opposite);

        Assert.Equal("-1.000000; 0.000000; -0.000000", IfcExporter.GridDirectionText(lines[0].Direction));
        Assert.Equal("-1.000000; 0.000000; 0.000000", IfcExporter.GridDirectionText(lines[1].Direction));
    }

    [Fact]
    public void GridDirection_KeepsTheSignOfZeroComponentsOfTheStoredVector()
    {
        double[] direction = ElementBuilder.GridDirection([-0.0, -1.0, -0.0], 0, -5, 0, 5);
        Assert.True(double.IsNegative(direction[0]) && double.IsNegative(direction[2]));
        Assert.Equal(-1.0, direction[1]);

        // a stored vector against the line, or none, falls back on the end points
        double[] fallback = ElementBuilder.GridDirection([0.0, 1.0, 0.0], 0, -5, 0, 5);
        Assert.Equal(-1.0, fallback[1]);
        Assert.Equal(-1.0, ElementBuilder.GridDirection(null, 0, -5, 0, 5)[1]);
        // a stored vector that is no unit vector is normalised
        Assert.Equal(1.0, ElementBuilder.GridDirection([0.0, 2.0, 0.0], 0, 5, 0, 5)[1], 12);
    }

    private const int PhaseId = 3;

    internal static Element Level(int id, string name, double elevationFeet) => new()
    {
        Id = id,
        TypeId = 1001,
        CategoryId = -2000240,
        Category = "OST_Levels",
        Name = name,
        IsNativeLevel = true,
        SerializedFamilyId = -1,
        SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = elevationFeet },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        foreach (var element in elements) info.Elements.Add(element);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-grid-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Element[] Model() =>
    [
        Level(1, "Ground", 0.0), Level(2, "Upper", 10.0),
        Grid(10, "A", 0, 0, 10, 0), Grid(11, "B", 0, 5, 10, 5), Grid(12, "1", 0, 0, 0, 5), Grid(13, "2", 5, 0, 5, 5),
    ];

    [Fact]
    public void ExportCurrent_OneGridPerStorey_NamedDefaultGrid_WithAnEmptyWAxisList()
    {
        string ifc = Export(IfcProfile.Current, Model());

        var grids = Regex.Matches(ifc, @"#\d+=IFCGRID\('([^']{22})',#\d+,'Default Grid','','',#\d+,#\d+,\((#\d+(?:,#\d+)*)\),\((#\d+(?:,#\d+)*)\),\(\)\);");
        Assert.Equal(2, grids.Count);
        Assert.NotEqual(grids[0].Groups[1].Value, grids[1].Groups[1].Value);
        Assert.Equal(2, grids[0].Groups[2].Value.Split(',').Length);
        Assert.Equal(2, grids[0].Groups[3].Value.Split(',').Length);
        Assert.Equal(8, Regex.Matches(ifc, @"IFCGRIDAXIS\(").Count);   // four lines on each of the two storeys
    }

    [Fact]
    public void ExportCurrent_GridGlobalIdFollowsTheStoreyGlobalIdAndTheDirectionKey()
    {
        string ifc = Export(IfcProfile.Current, Model());

        string storey = Regex.Match(ifc, @"IFCBUILDINGSTOREY\('([^']{22})',#\d+,'Ground'").Groups[1].Value;
        string key = IfcExporter.GridDirectionText([0.0, 1.0, 0.0]) + ":" + IfcExporter.GridDirectionText([1.0, 0.0, 0.0]);
        Assert.Contains($"IFCGRID('{IfcExporter.GridGlobalId("Default Grid", key, storey)}'", ifc);
    }

    [Fact]
    public void ExportCurrent_PutsAGridOnTheLevelsInsideItsVerticalExtent()
    {
        (double, double) whole = (-5, 25), partial = (5, 15), touching = (10 - 1e-10, 10 + 1e-10), above = (10 + 1e-8, 30);
        var elements = new List<Element>
        {
            Level(1, "Ground", 0.0), Level(2, "Upper", 10.0), Level(3, "Roof", 20.0),
            Grid(10, "A", 0, 0, 10, 0, interval: whole), Grid(11, "B", 0, 5, 10, 5, interval: whole),
            Grid(12, "1", 0, 0, 0, 5, interval: whole), Grid(13, "2", 5, 0, 5, 5, interval: whole),
            Grid(14, "C", 0, 8, 10, 8, interval: partial), Grid(15, "3", 9, 0, 9, 5, interval: partial),
            Grid(16, "D", 0, 9, 10, 9, interval: touching), Grid(17, "4", 8, 0, 8, 5, interval: above),
        };

        string ifc = Export(IfcProfile.Current, elements.ToArray());

        var grids = Regex.Matches(ifc, @"#\d+=IFCGRID\('[^']{22}',#\d+,'Default Grid','','',#\d+,#\d+,\(([^)]*)\),\(([^)]*)\),\(\)\);");
        // Ground: A, B, 1, 2. Upper: A, B, C, D (touching, within the 1e-9 tolerance) + 1, 2, 3; line 4 starts 1e-8 above it.
        // Roof: A, B + 1, 2, 4 (line 4 reaches up to 30).
        Assert.Equal([4, 7, 5], grids.Select(grid => grid.Groups[1].Value.Split(',').Length + grid.Groups[2].Value.Split(',').Length).ToArray());
        Assert.Equal(16, Regex.Matches(ifc, @"IFCGRIDAXIS\(").Count);
    }

    [Fact]
    public void ExportCurrent_ALevelWithoutAnyGridInRangeGetsNoGrid()
    {
        (double, double) low = (-50, -20);
        string ifc = Export(IfcProfile.Current, Level(1, "Ground", 0.0),
            Grid(10, "A", 0, 0, 10, 0, interval: low), Grid(11, "1", 0, 0, 0, 5, interval: low));

        Assert.DoesNotContain("IFCGRID(", ifc);
    }

    [Fact]
    public void CollectGridLines_SkipsGridsWithoutAnElemTableRecord_AndKeepsTheOthers()
    {
        var discovered = Grid(3, "C", 0, 7, 10, 7);
        discovered.IsDiscovered = true;
        var lines = Lines(Grid(1, "A", 0, 0, 10, 0), Grid(2, "1", 0, 0, 0, 10), discovered);

        Assert.Equal(["A", "1"], lines.Select(line => line.Label).ToArray());
        Assert.Empty(Lines(discovered));
    }

    [Fact]
    public void ExportLegacy_KeepsTheOneGridPerConnectedComponent()
    {
        string ifc = Export(IfcProfile.Legacy, Model());

        var grids = Regex.Matches(ifc, @"#\d+=IFCGRID\('[^']{22}',#\d+,'Default Grid',\$,\$,");
        Assert.Equal(2, grids.Count);
    }

    private static Element WithRange(Element grid, double lowerFeet, double upperFeet)
    {
        grid.GridElevationRangeFeet = [lowerFeet, upperFeet];
        return grid;
    }

    [Fact]
    public void GridIsOnLevel_TheIntervalIsClosed_AndAnUnknownExtentIsEveryLevel()
    {
        var line = IfcExporter.CollectGridLines([WithRange(Grid(1, "A", 0, 0, 10, 0), -1.0, 17.8425)]).Single();

        Assert.True(IfcExporter.GridIsOnLevel(line, -1.0));
        Assert.True(IfcExporter.GridIsOnLevel(line, 13.287));
        Assert.True(IfcExporter.GridIsOnLevel(line, 17.8425));
        Assert.False(IfcExporter.GridIsOnLevel(line, -12.04));
        Assert.False(IfcExporter.GridIsOnLevel(line, 22.15));
        Assert.True(IfcExporter.GridIsOnLevel(IfcExporter.CollectGridLines([Grid(2, "B", 0, 0, 10, 0)]).Single(), 1e6));
    }

    [Fact]
    public void ExportCurrent_AGridIsPutOnTheLevelsInsideItsElevationInterval()
    {
        // levels at 0 and 10 ft: the lines A and 1 reach the ground level only (interval -1..5 ft), B and 2 reach both
        string ifc = Export(IfcProfile.Current,
            Level(1, "Ground", 0.0), Level(2, "Upper", 10.0),
            WithRange(Grid(10, "A", 0, 0, 10, 0), -1.0, 5.0), WithRange(Grid(11, "1", 0, 0, 0, 5), -1.0, 5.0),
            WithRange(Grid(12, "B", 0, 5, 10, 5), -1.0, 20.0), WithRange(Grid(13, "2", 5, 0, 5, 5), -1.0, 20.0));

        // ground: {1, 2 | A, B}; upper: {2 | B}
        Assert.Equal(2, Regex.Matches(ifc, @"=IFCGRID\(").Count);
        Assert.Equal(6, Regex.Matches(ifc, @"=IFCGRIDAXIS\(").Count);
    }

    [Fact]
    public void ExportCurrent_AGridWithoutAnyExtent_KeepsEveryLevel()
    {
        string ifc = Export(IfcProfile.Current, Model());

        Assert.Equal(2, Regex.Matches(ifc, @"=IFCGRID\(").Count);
        Assert.Equal(8, Regex.Matches(ifc, @"=IFCGRIDAXIS\(").Count);
    }

    [Fact]
    public void ExportCurrent_AStaleGridOfAnExactElemTableIsNotExported()
    {
        var info = new RevitFileInfo { ElemTableIsExact = true };
        info.Elements.Add(Level(1, "Ground", 0.0));
        info.Elements.Add(WithRange(Grid(10, "A", 0, 0, 10, 0), -1.0, 5.0));
        info.Elements.Add(WithRange(Grid(11, "1", 0, 0, 0, 5), -1.0, 5.0));
        var stale = WithRange(Grid(12, "9", 3, 0, 3, 5), -1.0, 5.0);
        stale.IsDiscovered = true;
        info.Elements.Add(stale);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-grid-stale-{Guid.NewGuid():N}.ifc");
        string ifc;
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            ifc = File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        Assert.Equal(2, Regex.Matches(ifc, @"=IFCGRIDAXIS\(").Count);
        Assert.DoesNotContain("IFCGRIDAXIS('9'", ifc);
    }

    [Fact]
    public void ExportCurrent_AnAxisIsATrimmedLineOfOneFootVectorWithThePlanPointsAtZeroHeight()
    {
        string ifc = Export(IfcProfile.Current, Model());

        Match axis = Regex.Match(ifc,
            @"#(\d+)=IFCCARTESIANPOINT\(\(0\.,1524\.,0\.\)\);\r?\n#(\d+)=IFCVECTOR\(#(\d+),304\.8\);\r?\n#(\d+)=IFCLINE\(#\1,#\2\);\r?\n"
            + @"#(\d+)=IFCCARTESIANPOINT\(\(3048\.,1524\.,0\.\)\);\r?\n#(\d+)=IFCTRIMMEDCURVE\(#\4,\(#\1\),\(#\5\),\.T\.,\.CARTESIAN\.\);\r?\n"
            + @"#(\d+)=IFCGRIDAXIS\('B',#\6,\.T\.\);");
        Assert.True(axis.Success);
        Assert.Matches($@"#{axis.Groups[3].Value}=IFCDIRECTION\(\(1\.,0\.,0\.\)\);", ifc);
        // four axes along two global directions share two direction entities
        Assert.Equal(2, Regex.Matches(ifc, @"=IFCVECTOR\(#(\d+),304\.8\);").Select(m => m.Groups[1].Value).Distinct().Count());
        Assert.DoesNotContain("IFCPOLYLINE", ifc);
    }

    [Fact]
    public void ExportCurrent_ADiagonalAxisHasItsOwnDirectionWith17Digits()
    {
        string ifc = Export(IfcProfile.Current,
            Level(1, "Ground", 0.0),
            Grid(10, "A", 0, 0, 10, 0), Grid(11, "1", 0, 0, 0, 10), Grid(12, "D", 0, 0, 3, 3), Grid(13, "E", 0, 5, 3, 8));

        Assert.Equal(2, Regex.Matches(ifc, @"=IFCDIRECTION\(\(0\.7071067811865\d{3,4},0\.7071067811865\d{3,4},0\.\)\);").Count);
    }

    [Fact]
    public void ExportLegacy_KeepsThePolylineAxes()
    {
        string ifc = Export(IfcProfile.Legacy, Model());

        Assert.Contains("IFCPOLYLINE", ifc);
        Assert.DoesNotContain("IFCTRIMMEDCURVE", ifc);
    }
}
