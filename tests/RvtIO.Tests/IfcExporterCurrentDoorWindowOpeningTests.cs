using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The opening of a door or window in a straight wall from the cut loop of its family symbol (the current profile);
/// the numbers are those of Test_2022 door 422466 as the expected export writes it</summary>
public class IfcExporterCurrentDoorWindowOpeningTests
{
    private const int WallElement = 30, DoorElement = 31, SymbolElement = 50;

    private static (IfcExporterCurrentWallExtrusionTests.Fixture Fixture, int Opening, Dictionary<int, Element> Elements, Dictionary<int, int> Products)
        Scene(double[] symbolBox, double turnDegrees = 0, string fillerType = "IFCDOOR")
    {
        // the whole scene turned about the vertical axis: the wall, its location line and the instance transform
        double cos = Math.Cos(turnDegrees * Math.PI / 180), sin = Math.Sin(turnDegrees * Math.PI / 180);
        double[] Turn(double x, double y, double z) => [cos * x - sin * y, sin * x + cos * y, z];
        var f = new IfcExporterCurrentWallExtrusionTests.Fixture();
        // the wall runs along -X; its frame stands at the start of the axis, 9.9009 m / 1.2384 m (the site placement above is no part of the model frame)
        var wallStart = Turn(9.900931517437637, 1.2384060214181773, 0);
        var wallDirection = Turn(-1, 0, 0);
        int wall = f.StandardCaseWall(wallStart[0], wallStart[1], 0, wallDirection[0], wallDirection[1], 3, 0.202, 2.7);

        int openingPoint = f.Point3D(0, 0, 0);
        int openingFrame = f.W.Add("IFCAXIS2PLACEMENT3D", $"#{openingPoint},$,$");
        int openingPlacement = f.W.Add("IFCLOCALPLACEMENT", $"#{f.StoreyPlacement},#{openingFrame}");
        int body = f.W.Add("IFCSHAPEREPRESENTATION", $"#{f.BodyContext},'Body','SweptSolid',($)");
        int shape = f.W.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{body})");
        int opening = f.W.Add("IFCOPENINGELEMENT", $"'g',#1,'Single-Flush:800 x 2100:31',$,'Opening',#{openingPlacement},#{shape},'31'");
        int door = f.W.Add(fillerType, $"'d',#1,'Single-Flush',$,$,#{openingPlacement},$,'31'");
        f.W.Add("IFCRELVOIDSELEMENT", $"'v',#1,$,$,#{wall},#{opening}");
        f.W.Add("IFCRELFILLSELEMENT", $"'f',#1,$,$,#{opening},#{door}");

        double[][] loop = [[-1.3123359580052494, 0.3313648293963368, 0], [1.3123359580052485, 0.33136482939633194, 0],
            [1.3123359580052476, 0.33136482939633194, 6.889763779527558], [-1.3123359580052498, 0.3313648293963368, 6.889763779527558]];
        double[] bx = Turn(1, -3.3508640216608995E-15, 0), by = Turn(3.3508640216608995E-15, 1, 0), origin = Turn(30.97746560839118, 4.063011881293229, 0);
        double[] transform = [bx[0], by[0], 0, bx[1], by[1], 0, 0, 0, 1, origin[0], origin[1], origin[2]];
        var locationStart = Turn(32.8147, 4.063, 0);
        var locationEnd = Turn(22.8377, 4.063, 0);
        var elements = new Dictionary<int, Element>
        {
            [WallElement] = new() { Id = WallElement, Location = new LocationCurve { CurveType = "Line", StartPoint = locationStart, EndPoint = locationEnd } },
            [DoorElement] = new()
            {
                Id = DoorElement, TypeId = 1,
                OwnSingleFamilyCutout = (SymbolElement, WallElement, transform, new FamilyCutLoopData(loop, [1.864073392952492E-15, -1, 0], 0)),
            },
            [SymbolElement] = new() { Id = SymbolElement, Geometry = new GeometryData { BoundingBox = [0, 0, 0, 0, 0, 0], RootBoundingBox = symbolBox } },
        };
        return (f, opening, elements, new Dictionary<int, int> { [wall] = WallElement, [door] = DoorElement });
    }

    private static (string[] Placement, string[] Solid, string[] Profile) Read(IfcExporterCurrentWallExtrusionTests.Fixture f, int opening)
    {
        var attributes = f.Attributes(opening);
        var placement = f.Attributes(int.Parse(attributes[5][1..]));
        var shape = f.Attributes(int.Parse(attributes[6][1..]));
        var representation = f.Attributes(int.Parse(shape[2].Trim('(', ')')[1..]));
        var solid = f.Attributes(int.Parse(representation[3].Trim('(', ')')[1..]));
        return (placement, solid, f.Attributes(int.Parse(solid[0][1..])));
    }

    [Fact]
    public void Door_IsPlacedInTheFrameOfTheWallAndExtrudedAcrossItsWholeWidth()
    {
        // the box of the symbol reaches 2.96 ft to the side the cut plane is on: the hinge side
        var (f, opening, elements, products) = Scene([-1.5748031496062993, -2.9564656610201734, 0, 1.5748031496062993, 0.4297900262467311, 7.152230971128607]);

        Assert.Equal(1, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        var (placement, solid, profile) = Read(f, opening);
        // relative to the placement of the wall, at the lowest corner of the loop
        var origin = f.Numbers(f.Attributes(int.Parse(placement[1][1..]))[0]);
        Assert.Equal(0.059, origin[0], 4);
        Assert.Equal(-0.101, origin[1], 6);
        Assert.Equal(0, origin[2], 9);
        // the plane of the loop faces the wall (Z = the direction of the cut), X vertical; the extrusion across the wall
        var position = f.Attributes(int.Parse(solid[1][1..]));
        Assert.Equal(new double[] { 0, 0, 0 }, f.Numbers(position[0]));
        Assert.Equal(new double[] { 0, 1, 0 }, f.Numbers(position[1]).Select(v => Math.Round(v, 9)).ToArray());
        Assert.Equal(new double[] { 0, 0, 1 }, f.Numbers(position[2]));
        Assert.Equal(new double[] { 0, 0, 1 }, f.Numbers(solid[2]));
        Assert.Equal(0.202, double.Parse(solid[3], System.Globalization.CultureInfo.InvariantCulture), 9);
        // the loop of the symbol starts at the bottom: XDim is the height, YDim the width
        Assert.Equal("IFCRECTANGLEPROFILEDEF", f.Find(int.Parse(solid[0][1..])).Type);
        Assert.Equal(".AREA.,''", $"{profile[0]},{profile[1]}");
        Assert.Equal(2.1, double.Parse(profile[3], System.Globalization.CultureInfo.InvariantCulture), 4);
        Assert.Equal(0.8, double.Parse(profile[4], System.Globalization.CultureInfo.InvariantCulture), 4);
        var profilePlacement = f.Attributes(int.Parse(profile[2][1..]));
        Assert.Equal(1.05, f.Numbers(profilePlacement[0])[0], 4);
        Assert.Equal(0.4, f.Numbers(profilePlacement[0])[1], 4);
        Assert.Equal(new double[] { -1, 0 }, f.Numbers(profilePlacement[1]).Select(v => Math.Round(v, 9)).ToArray());
    }

    [Fact]
    public void Door_OnTheOtherSideOfTheAxis_IsExtrudedFromTheOtherFaceOfTheWall()
    {
        // the box of the symbol mirrored: the hinge is on the other side, the plane on the other face, the direction turned
        var (f, opening, elements, products) = Scene([-1.5748031496062993, -0.4297900262467311, 0, 1.5748031496062993, 2.9564656610201734, 7.152230971128607]);

        Assert.Equal(1, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        var (placement, solid, _) = Read(f, opening);
        Assert.Equal(0.101, f.Numbers(f.Attributes(int.Parse(placement[1][1..]))[0])[1], 6);
        Assert.Equal(new double[] { 0, -1, 0 }, f.Numbers(f.Attributes(int.Parse(solid[1][1..]))[1]).Select(v => Math.Round(v, 9)).ToArray());
    }

    [Fact]
    public void OpeningWithoutACutLoopOrASymbolBox_IsLeftAsItWas()
    {
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 1]);
        elements[DoorElement].OwnSingleFamilyCutout = null;
        string before = f.Find(opening).Args;
        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));
        Assert.Equal(before, f.Find(opening).Args);

        var (g, other, otherElements, otherProducts) = Scene([-1, -1, 0, 1, 1, 1]);
        otherElements[SymbolElement].Geometry = null;
        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(g.W, otherElements, otherProducts));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ArbitraryLineLoopKeepsItsFirstVertexAndTurnsCounterClockwise(bool reversed, bool quadrilateral)
    {
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        var own = elements[DoorElement].OwnSingleFamilyCutout!.Value;
        double y = own.Loop.Vertices[0][1];
        List<double[]> vertices = quadrilateral
            ? [[-1, y, 0], [1, y, 0], [0.5, y, 2], [-1, y, 2]]
            : [[-1, y, 0], [1, y, 0], [1, y, 2], [0, y, 3], [-1, y, 2]];
        if (reversed) vertices = [vertices[0], .. vertices.Skip(1).Reverse()];
        elements[DoorElement].OwnSingleFamilyCutout = (own.SymbolId, own.HostId, own.Transform, own.Loop with { Vertices = vertices });

        Assert.Equal(1, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        var (_, solid, profile) = Read(f, opening);
        Assert.Equal("IFCARBITRARYCLOSEDPROFILEDEF", f.Find(int.Parse(solid[0][1..])).Type);
        Assert.Equal(".AREA.,''", $"{profile[0]},{profile[1]}");
        var curve = f.Attributes(int.Parse(profile[2][1..]));
        var points = curve[0].Trim('(', ')').Split(',').Select(f.Numbers).ToList();
        Assert.Equal(vertices.Count + 1, points.Count);
        Assert.Equal(points[0], points[^1]);
        Assert.Equal(0, points[0][0], 12);
        Assert.Equal(0.6096, points[0][1], 12);
        double area = Enumerable.Range(0, points.Count - 1)
            .Sum(i => points[i][0] * points[i + 1][1] - points[i + 1][0] * points[i][1]) / 2;
        Assert.True(area > 0);
        Assert.Equal((quadrilateral ? 3.5 : 5) * 0.3048 * 0.3048, area, 12);
    }

    [Theory]
    [InlineData("few-vertices")]
    [InlineData("nonfinite")]
    [InlineData("collinear")]
    public void InvalidLineLoopsLeaveTheOpeningAndWriterUnchanged(string mode)
    {
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        var own = elements[DoorElement].OwnSingleFamilyCutout!.Value;
        List<double[]> vertices = mode switch
        {
            "few-vertices" => [[0, 0, 0], [1, 0, 0]],
            "nonfinite" => [[0, 0, 0], [1, 0, 0], [double.NaN, 0, 1]],
            _ => [[0, 0, 0], [1, 0, 0], [2, 0, 0]],
        };
        elements[DoorElement].OwnSingleFamilyCutout = (own.SymbolId, own.HostId, own.Transform, own.Loop with { Vertices = vertices });
        string before = f.Find(opening).Args;
        int count = f.W.EntityCount;

        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(before, f.Find(opening).Args);
        Assert.Equal(count, f.W.EntityCount);
    }

    [Fact]
    public void BaseQuantities_Legacy_UpdatedWithCutLoopModelFrameExtents()
    {
        var (f, opening, elements, products) = Scene([-1.5748031496062993, -2.9564656610201734, 0, 1.5748031496062993, 0.4297900262467311, 7.152230971128607]);
        int qDepth = f.W.Add("IFCQUANTITYLENGTH", "'Depth','',$,0.1");
        int qHeight = f.W.Add("IFCQUANTITYLENGTH", "'Height','',$,0.885");
        int qWidth = f.W.Add("IFCQUANTITYLENGTH", "'Width','',$,2.135");
        int qSet = f.W.Add("IFCELEMENTQUANTITY", $"'qset',#1,'BaseQuantities',$,$,(#{qDepth},#{qHeight},#{qWidth})");
        f.W.Add("IFCRELDEFINESBYPROPERTIES", $"'rel',#1,$,$,(#{opening}),#{qSet}");

        Assert.Equal(1, new IfcExporter { OpeningQuantitiesSwapped = true }.ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        var depthArgs = f.Attributes(qDepth);
        var heightArgs = f.Attributes(qHeight);
        var widthArgs = f.Attributes(qWidth);

        Assert.Equal(0.202, double.Parse(depthArgs[3], System.Globalization.CultureInfo.InvariantCulture), 3);
        double h = double.Parse(heightArgs[3], System.Globalization.CultureInfo.InvariantCulture);
        double w = double.Parse(widthArgs[3], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0.8, h, 3);
        Assert.Equal(2.1, w, 3);
    }

    private static (double Depth, double Height, double Width) QuantitiesAfterTheRewrite(bool swapped, double turnDegrees, string fillerType,
        bool decodedCut = true)
    {
        var (f, opening, elements, products) = Scene([-1.5748031496062993, -2.9564656610201734, 0, 1.5748031496062993, 0.4297900262467311, 7.152230971128607], turnDegrees, fillerType);
        // (what the quantity pass wrote before the rewrite: a size that is not that of the cut loop, 0.885 by 2.135)
        int qDepth = f.W.Add("IFCQUANTITYLENGTH", "'Depth','',$,0.1");
        int qHeight = f.W.Add("IFCQUANTITYLENGTH", "'Height','',$,0.885");
        int qWidth = f.W.Add("IFCQUANTITYLENGTH", "'Width','',$,2.135");
        int qSet = f.W.Add("IFCELEMENTQUANTITY", $"'qset',#1,'BaseQuantities',$,$,(#{qDepth},#{qHeight},#{qWidth})");
        f.W.Add("IFCRELDEFINESBYPROPERTIES", $"'rel',#1,$,$,(#{opening}),#{qSet}");
        // a door whose cut loop is not decoded: no cut of its own, and the symbol holds none
        if (!decodedCut) elements[DoorElement].OwnSingleFamilyCutout = null;

        Assert.Equal(decodedCut ? 1 : 0, new IfcExporter { OpeningQuantitiesSwapped = swapped }.ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        double Value(int quantity) => double.Parse(f.Attributes(quantity)[3], System.Globalization.CultureInfo.InvariantCulture);
        return (Value(qDepth), Value(qHeight), Value(qWidth));
    }

    [Theory]
    // Legacy: the loop measured in the model frame: X along the placement for 0 degrees, and the horizontal projections when turned.
    [InlineData(0, 0.8, 2.1)]
    [InlineData(37, 0.639, 0.481)]
    public void BaseQuantities_Legacy_NamesTheSidesOfTheLoopByTheFrame(double turnDegrees, double height, double width)
    {
        var (depth, h, w) = QuantitiesAfterTheRewrite(true, turnDegrees, "IFCDOOR");

        Assert.Equal(0.202, depth, 3);
        Assert.Equal(height, h, 3);
        Assert.Equal(width, w, 3);
    }

    [Theory]
    // By default (along the host axes) the opening that the cut loop rebuilds is measured by the loop, in a wall of any direction: Height the vertical
    // side (2.1), Width the side along the wall (0.8), whatever size the quantity pass had taken from the door (here 0.885 by 2.135, a cut that is not the nominal size).
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(90)]
    [InlineData(180)]
    public void BaseQuantities_Default_ADecodedCutLoopIsMeasuredByTheLoopAndNotByTheNominalSize(double turnDegrees)
    {
        var (depth, h, w) = QuantitiesAfterTheRewrite(false, turnDegrees, "IFCDOOR");

        Assert.Equal(0.202, depth, 3);
        Assert.Equal(2.1, h, 3);
        Assert.Equal(0.8, w, 3);
    }

    [Theory]
    // An opening whose cut loop is not decoded is not rebuilt and keeps what the quantity pass wrote (the size of the door).
    [InlineData(false)]
    [InlineData(true)]
    public void BaseQuantities_AnUndecodedCutLoop_KeepsTheQuantitiesOfTheQuantityPass(bool swapped)
    {
        var (depth, h, w) = QuantitiesAfterTheRewrite(swapped, 0, "IFCDOOR", decodedCut: false);

        Assert.Equal(0.1, depth, 3);
        Assert.Equal(0.885, h, 3);
        Assert.Equal(2.135, w, 3);
    }

    /// <summary>Both passes in the order of the export: the quantity pass writes the nominal size of the door (0.885 by 2.135) on the
    /// opening that was written as a generic box (6 by 0.202 by 3), then the opening pass rebuilds it from the cut loop (0.8 by 2.1) or,
    /// with no decoded cut, leaves it.</summary>
    private static (double Height, double Width) QuantitiesOfTheOpeningAfterBothPasses(bool decodedCut, double nominalHeight = 2.135, double nominalWidth = 0.885, string fillerType = "IFCDOOR")
    {
        var (f, opening, elements, products) = Scene([-1.5748031496062993, -2.9564656610201734, 0, 1.5748031496062993, 0.4297900262467311, 7.152230971128607], fillerType: fillerType);
        const double FeetPerMetre = 1 / 0.3048;
        Parameter Dimension(int id, double metres) => new()
        {
            Id = id, Value = metres * FeetPerMetre, StorageType = StorageType.Double, Definition = new Definition { Id = id, Name = "P" + id },
        };
        elements[DoorElement].Parameters = [Dimension(-1001300, nominalHeight), Dimension(-1001301, nominalWidth)];
        if (!decodedCut) elements[DoorElement].OwnSingleFamilyCutout = null;

        // the generic box as the body of the opening, across the wall like the box of an undecoded opening
        int origin = f.Point3D(0, 0, 0);
        int profile = f.W.Add("IFCRECTANGLEPROFILEDEF", $".AREA.,$,#{f.W.Add("IFCAXIS2PLACEMENT2D", $"#{f.Point2D(0, 0)},$")},6.,3.");
        int position = f.W.Add("IFCAXIS2PLACEMENT3D", $"#{origin},#{f.Direction3D(0, 1, 0)},#{f.Direction3D(1, 0, 0)}");
        int solid = f.W.Add("IFCEXTRUDEDAREASOLID", $"#{profile},#{position},#{f.Direction3D(0, 0, 1)},0.202");
        var shape = f.Attributes(int.Parse(f.Attributes(opening)[6][1..]));
        int representation = int.Parse(shape[2].Trim('(', ')')[1..]);
        for (int i = 0; i < f.W.EntityCount; i++)
            if (f.W.EntityAt(i).Id == representation) f.W.ReplaceArgs(i, $"#{f.BodyContext},'Body','SweptSolid',(#{solid})");

        var exporter = new IfcExporter();
        var byEntity = new Dictionary<int, int>(products);
        exporter.ApplyCurrentBaseQuantities(f.W, elements, byEntity, new Dictionary<int, (int, int, string)>(), 1, null);
        exporter.ApplyCurrentDoorWindowOpenings(f.W, elements, products);

        var openingSet = Enumerable.Range(0, f.W.EntityCount).Select(f.W.EntityAt)
            .First(e => e.Type == "IFCRELDEFINESBYPROPERTIES" && e.Args.Contains($"(#{opening})"));
        int set = int.Parse(IfcExporter.SplitStepArguments(openingSet.Args)[5][1..]);
        var values = IfcExporter.SplitStepArguments(f.Attributes(set)[5].Trim('(', ')'))
            .Select(reference => f.Attributes(int.Parse(reference[1..])))
            .ToDictionary(q => q[0].Trim('\''), q => double.Parse(q[3], System.Globalization.CultureInfo.InvariantCulture));
        return (values["Height"], values["Width"]);
    }

    [Theory]
    [InlineData("IFCDOOR")]
    [InlineData("IFCWINDOW")]
    public void BothPasses_ADecodedCutLoop_ReplacesTheNominalSizeOfInstance(string fillerType)
    {
        var (height, width) = QuantitiesOfTheOpeningAfterBothPasses(decodedCut: true, fillerType: fillerType);

        Assert.Equal(2.1, height, 3);
        Assert.Equal(0.8, width, 3);
    }

    [Fact]
    public void BothPasses_ACutOfTheNominalSizeUpToRoundingNoise_KeepsTheNominalValueAsItWas()
    {
        // (the nominal size of the door differs from the cut loop, 2.1 by 0.8, by a relative 1e-12 only)
        double nominalHeight = 2.1 * (1 + 1e-12), nominalWidth = 0.8 * (1 + 1e-12);
        var written = QuantitiesOfTheOpeningAfterBothPasses(decodedCut: false, nominalHeight, nominalWidth);

        var (height, width) = QuantitiesOfTheOpeningAfterBothPasses(decodedCut: true, nominalHeight, nominalWidth);

        Assert.Equal(written.Height, height);
        Assert.Equal(written.Width, width);
        Assert.NotEqual(2.1, height);
        Assert.NotEqual(0.8, width);
    }

    [Fact]
    public void BothPasses_ACutThatDiffersFromTheNominalSizeByAPartInAMillion_IsMeasuredByTheLoop()
    {
        var (height, width) = QuantitiesOfTheOpeningAfterBothPasses(decodedCut: true, 2.1 * (1 + 1e-6), 0.8 * (1 + 1e-6));

        Assert.Equal(2.1, height, 9);
        Assert.Equal(0.8, width, 9);
    }

    [Fact]
    public void BothPasses_NoDecodedCutLoop_LeavesTheNominalSizeOfTheDoorInsteadOfTheGenericBox()
    {
        var (height, width) = QuantitiesOfTheOpeningAfterBothPasses(decodedCut: false);

        Assert.Equal(2.135, height, 3);
        Assert.Equal(0.885, width, 3);
    }

    private static string[] OverallSizeAfterTheRewrite(bool decodedCut, string fillerType, string written)
    {
        var (f, _, elements, products) = Scene([-1.5748031496062993, -2.9564656610201734, 0, 1.5748031496062993, 0.4297900262467311, 7.152230971128607],
            fillerType: fillerType);
        if (!decodedCut) elements[DoorElement].OwnSingleFamilyCutout = null;
        int index = Enumerable.Range(0, f.W.EntityCount).First(i => f.W.EntityAt(i).Type == fillerType);
        f.W.ReplaceArgs(index, f.W.EntityAt(index).Args + "," + written);

        new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products);

        // (the pass removes the entities it detaches, so the index is looked up again)
        var attributes = IfcExporter.SplitStepArguments(
            Enumerable.Range(0, f.W.EntityCount).Select(f.W.EntityAt).Single(e => e.Type == fillerType).Args);
        return [attributes[8], attributes[9]];
    }

    [Theory]
    [InlineData("IFCDOOR")]
    [InlineData("IFCWINDOW")]
    public void OverallSize_ADecodedCutLoop_ReplacesTheNominalSizeInMillimetres(string fillerType)
    {
        var overall = OverallSizeAfterTheRewrite(true, fillerType, "2135.,885.");

        Assert.Equal(2100, double.Parse(overall[0], System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(800, double.Parse(overall[1], System.Globalization.CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void OverallSize_ACutOfTheNominalSizeUpToRoundingNoise_KeepsTheWrittenText()
    {
        var overall = OverallSizeAfterTheRewrite(true, "IFCDOOR", "2100.0000000000005,800.0000000000001");

        Assert.Equal(["2100.0000000000005", "800.0000000000001"], overall);
    }

    [Fact]
    public void OverallSize_NoDecodedCutLoop_KeepsTheNominalSize()
    {
        Assert.Equal(["2135.", "885."], OverallSizeAfterTheRewrite(false, "IFCDOOR", "2135.,885."));
    }

    [Theory]
    [InlineData("IFCWINDOW", 2.1, 0.8)]
    [InlineData("IFCBUILDINGELEMENTPROXY", 0.8, 2.1)]
    public void BaseQuantities_Default_ChangesOnlyTheOpeningOfADoorOrWindow(string fillerType, double height, double width)
    {
        var (depth, h, w) = QuantitiesAfterTheRewrite(false, 0, fillerType);

        Assert.Equal(0.202, depth, 3);
        Assert.Equal(height, h, 3);
        Assert.Equal(width, w, 3);
    }

    [Fact]
    public void TheTwoProperties_AreTheOppositeOfEachOther_AndTheRevitMeaningIsTheDefault()
    {
        Assert.False(new IfcExporter().OpeningQuantitiesSwapped);
        Assert.True(new IfcExporter().OpeningQuantitiesInHostAxes);
        Assert.True(new IfcExporter { OpeningQuantitiesSwapped = true }.OpeningQuantitiesSwapped);
        Assert.False(new IfcExporter { OpeningQuantitiesSwapped = true }.OpeningQuantitiesInHostAxes);
        Assert.True(new IfcExporter { OpeningQuantitiesInHostAxes = false }.OpeningQuantitiesSwapped);
        Assert.False(new IfcExporter { OpeningQuantitiesInHostAxes = true }.OpeningQuantitiesSwapped);
    }

    private static Parameter WallWidth(double metres) =>
        new() { Id = -1001000, StorageType = StorageType.Double, Value = metres / 0.3048 };

    [Fact]
    public void WallWithAWidthParameterThatAgreesWithItsBody_IsRewritten()
    {
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        // within the agreement of 0.1 mm
        elements[WallElement].Parameters.Add(WallWidth(0.202 + 5e-5));

        Assert.Equal(1, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(0.202, double.Parse(Read(f, opening).Solid[3], System.Globalization.CultureInfo.InvariantCulture), 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WallWhoseWidthParameterDisagreesWithItsBody_IsRewrittenWithTheParameter(bool parameterOfTheType)
    {
        // a stale copy of the wall (a file saved in several increments): the layers give 0.202 m, the width parameter 0.303 m
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        if (parameterOfTheType)
        {
            elements[WallElement].Parameters.Add(new Parameter { Id = -1, StorageType = StorageType.ElementId, Value = 70, Definition = new Definition { Name = "ELEM_TYPE_PARAM" } });
            elements[70] = new Element { Id = 70, Parameters = [WallWidth(0.303)] };
        }
        else
            elements[WallElement].Parameters.Add(WallWidth(0.303));
        var diagnostics = new StringWriter();

        Assert.Equal(1, new IfcExporter { DiagnosticOutput = diagnostics }.ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(0.303, double.Parse(Read(f, opening).Solid[3], System.Globalization.CultureInfo.InvariantCulture), 9);
        Assert.Contains("1 rewritten (1 with the wall width parameter", diagnostics.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.303)]
    public void WallWhoseWidthParameterIsNotPositive_IsNoWidthAndTheBodyIsUsed(double parameter)
    {
        // the parameter is read only when it is positive: no declared width, so no conflict
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        elements[WallElement].Parameters.Add(WallWidth(parameter));

        Assert.Equal(1, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(0.202, double.Parse(Read(f, opening).Solid[3], System.Globalization.CultureInfo.InvariantCulture), 9);
    }

    [Fact]
    public void WallWhoseWidthParameterIsNotFiniteAndDisagreesWithItsBody_KeepsTheOpeningAsItWas()
    {
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        elements[WallElement].Parameters.Add(new Parameter { Id = -1001000, StorageType = StorageType.Double, Value = double.PositiveInfinity });
        string before = f.Find(opening).Args;
        var diagnostics = new StringWriter();

        Assert.Equal(0, new IfcExporter { DiagnosticOutput = diagnostics }.ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(before, f.Find(opening).Args);
        Assert.Contains("wall-width-conflict=1", diagnostics.ToString());
    }

    [Fact]
    public void WallWithAValidatedTypeThickness_IsRewrittenWhateverItsWidthParameterSays()
    {
        // the type root and its compound structure were read once: that thickness is confirmed, the parameter is not consulted
        var (f, opening, elements, products) = Scene([-1, -1, 0, 1, 1, 3]);
        elements[WallElement].Parameters.Add(WallWidth(0.303));
        elements[WallElement].ValidatedWallTypeThicknessFeet = 0.202 / 0.3048;

        Assert.Equal(1, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(0.202, double.Parse(Read(f, opening).Solid[3], System.Globalization.CultureInfo.InvariantCulture), 9);
    }
}
