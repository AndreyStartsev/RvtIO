using System.Globalization;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The opening of a door or window in a straight wall reaches through the whole wall (the total layer width of the wall)
/// also where it is not rebuilt from a cut loop and keeps the box of the family cutter.</summary>
public class IfcExporterCurrentDoorWindowThroughDepthTests
{
    private const int WallElement = 30, DoorElement = 31;
    private const double WallWidth = 0.202;

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>A wall along +X, 0.202 thick, with a door whose cutter box is 0.9 along the wall, <paramref name="across"/> across it
    /// (its centre <paramref name="offset"/> from the axis) and 2.1 high; BaseQuantities Depth is <paramref name="across"/>.</summary>
    private static (IfcExporterCurrentWallExtrusionTests.Fixture F, int Opening, int Solid, int Depth, Dictionary<int, Element> Elements, Dictionary<int, int> Products)
        Scene(double across, double offset, bool extrudedAcross = false)
    {
        var f = new IfcExporterCurrentWallExtrusionTests.Fixture();
        int wall = f.StandardCaseWall(0, 0, 0, 1, 0, 3, WallWidth, 2.7);

        int boxPoint = f.Point3D(0.4, offset, 0);
        // (extruded across: the profile stands in the plane of the wall and the extrusion goes along the wall normal)
        int boxFrame = extrudedAcross
            ? f.W.Add("IFCAXIS2PLACEMENT3D", $"#{boxPoint},#{f.Direction3D(0, 1, 0)},#{f.Direction3D(1, 0, 0)}")
            : f.W.Add("IFCAXIS2PLACEMENT3D", $"#{boxPoint},$,$");
        int placement = f.W.Add("IFCLOCALPLACEMENT", $"#{f.StoreyPlacement},#{boxFrame}");
        int centre = f.Point2D(0, 0);
        int profilePlacement = f.W.Add("IFCAXIS2PLACEMENT2D", $"#{centre},$");
        int profile = f.W.Add("IFCRECTANGLEPROFILEDEF", extrudedAcross
            ? $".AREA.,$,#{profilePlacement},0.9,2.1"
            : $".AREA.,$,#{profilePlacement},0.9,{StepWriter.Real(across)}");
        int origin = f.Point3D(0, 0, 0);
        int solidFrame = f.W.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int up = f.Direction3D(0, 0, 1);
        int solid = f.W.Add("IFCEXTRUDEDAREASOLID", extrudedAcross
            ? $"#{profile},#{solidFrame},#{up},{StepWriter.Real(across)}"
            : $"#{profile},#{solidFrame},#{up},2.1");
        int body = f.W.Add("IFCSHAPEREPRESENTATION", $"#{f.BodyContext},'Body','SweptSolid',(#{solid})");
        int shape = f.W.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{body})");
        int opening = f.W.Add("IFCOPENINGELEMENT", $"'g',#1,'Single-Flush:31',$,'Opening',#{placement},#{shape},'31'");
        int door = f.W.Add("IFCDOOR", $"'d',#1,'Single-Flush',$,$,#{placement},$,'31'");
        f.W.Add("IFCRELVOIDSELEMENT", $"'v',#1,$,$,#{wall},#{opening}");
        f.W.Add("IFCRELFILLSELEMENT", $"'f',#1,$,$,#{opening},#{door}");
        int depth = f.W.Add("IFCQUANTITYLENGTH", $"'Depth','',$,{StepWriter.Real(across)}");
        int quantities = f.W.Add("IFCELEMENTQUANTITY", $"'qset',#1,'BaseQuantities',$,$,(#{depth})");
        f.W.Add("IFCRELDEFINESBYPROPERTIES", $"'rel',#1,$,$,(#{opening}),#{quantities}");

        var elements = new Dictionary<int, Element>
        {
            [WallElement] = new() { Id = WallElement, Location = new LocationCurve { CurveType = "Line", StartPoint = [-1.5, 0, 0], EndPoint = [1.5, 0, 0] } },
            // (no cut loop of its own and none on the symbol: the box of the cutter is what the opening keeps)
            [DoorElement] = new() { Id = DoorElement, TypeId = 1 },
        };
        return (f, opening, solid, depth, elements, new Dictionary<int, int> { [wall] = WallElement, [door] = DoorElement });
    }

    /// <summary>The across side of the box and the centre of its profile in the plane of the profile.</summary>
    private static (double Along, double Across, double[] Centre) Box(IfcExporterCurrentWallExtrusionTests.Fixture f, int solid)
    {
        var solidArguments = f.Attributes(solid);
        var profile = f.Attributes(int.Parse(solidArguments[0][1..]));
        var placement = f.Attributes(int.Parse(profile[2][1..]));
        return (Number(profile[3]), Number(profile[4]), f.Numbers(placement[0]));
    }

    [Theory]
    [InlineData(0.07, 0)]       // a shallower cutter box than the wall
    [InlineData(0.5, 0)]        // a deeper box than the wall
    [InlineData(0.202, 0.03)]   // as deep as the wall but off its axis
    [InlineData(0.07, -0.05)]
    public void CutterBox_IsGivenTheWidthOfTheWallAndCentredOnItsAxis(double across, double offset)
    {
        var (f, opening, solid, depth, elements, products) = Scene(across, offset);

        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        var (along, newAcross, centre) = Box(f, solid);
        Assert.Equal(0.9, along, 12);
        Assert.Equal(WallWidth, newAcross, 12);
        Assert.Equal(0, centre[0], 12);
        // the box stood at `offset` from the axis, its profile centre stays there in the placement: the profile is moved back to the axis
        Assert.Equal(-offset, centre[1], 12);
        Assert.Equal(WallWidth, Number(f.Attributes(depth)[3]), 12);
        // the height and the placement are as they were
        Assert.Equal(2.1, Number(f.Attributes(solid)[3]), 12);
        Assert.Equal(offset, f.Numbers(f.Attributes(int.Parse(f.Attributes(int.Parse(f.Attributes(opening)[5][1..]))[1][1..]))[0])[1], 12);
    }

    [Fact]
    public void Depth_IsTheTotalWidthOfTheLayers_AndNotTheNarrowerBodyOfTheWall()
    {
        // a wall with banding: the extrusion of its body is 0.202 across, its layers add up to 0.303
        var (f, _, solid, depth, elements, products) = Scene(0.07, 0);
        elements[WallElement].Layers = [new MaterialLayer { ThicknessMm = 150 }, new MaterialLayer { ThicknessMm = 153 }];

        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(0.303, Box(f, solid).Across, 12);
        Assert.Equal(0.303, Number(f.Attributes(depth)[3]), 12);
    }

    [Fact]
    public void CutterBox_ThatIsAlreadyAsDeepAsTheWallAndCentred_IsLeftAsItWas()
    {
        var (f, _, solid, depth, elements, products) = Scene(WallWidth, 0);
        string solidBefore = f.Find(solid).Args, depthBefore = f.Find(depth).Args;
        int count = f.W.EntityCount;

        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(solidBefore, f.Find(solid).Args);
        Assert.Equal(depthBefore, f.Find(depth).Args);
        Assert.Equal(count, f.W.EntityCount);
    }

    [Fact]
    public void CutterBox_ExtrudedAcrossTheWall_IsLeftAsItWas()
    {
        var (f, _, solid, depth, elements, products) = Scene(0.07, 0, extrudedAcross: true);
        string solidBefore = f.Find(solid).Args, depthBefore = f.Find(depth).Args;

        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(solidBefore, f.Find(solid).Args);
        Assert.Equal(depthBefore, f.Find(depth).Args);
    }

    [Fact]
    public void CutterBox_InACurvedWall_IsLeftAsItWas()
    {
        var (f, _, solid, depth, elements, products) = Scene(0.07, 0);
        elements[WallElement].Location = new LocationCurve { CurveType = "Arc", ArcRadius = 5, StartPoint = [-1.5, 0, 0], EndPoint = [1.5, 0, 0] };
        string solidBefore = f.Find(solid).Args, depthBefore = f.Find(depth).Args;

        Assert.Equal(0, new IfcExporter().ApplyCurrentDoorWindowOpenings(f.W, elements, products));

        Assert.Equal(solidBefore, f.Find(solid).Args);
        Assert.Equal(depthBefore, f.Find(depth).Args);
    }

    [Fact]
    public void CutterBox_IsCountedInTheDiagnostics_AndTheOldProfileIsRemoved()
    {
        var (f, _, solid, _, elements, products) = Scene(0.07, 0);
        int oldProfile = int.Parse(f.Attributes(solid)[0][1..]);
        var diagnostics = new StringWriter();

        new IfcExporter { DiagnosticOutput = diagnostics }.ApplyCurrentDoorWindowOpenings(f.W, elements, products);

        Assert.Contains("1 of the others given the depth of the wall", diagnostics.ToString());
        Assert.False(f.Exists(oldProfile));
    }
}
