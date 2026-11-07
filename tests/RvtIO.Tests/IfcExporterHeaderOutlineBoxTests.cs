using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The box of an element read from a file is the outline its header stores (the box the application reports), and the tight box of
/// its geometry is another one. The IFC export places a wall by the outline, as the reference does, and every other product by the box of its
/// geometry, so that an outline never stands for a body that is not there.</summary>
public class IfcExporterHeaderOutlineBoxTests
{
    private static readonly double[][] Points = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2]];
    private static readonly int[][] Quads = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
    private static readonly double[] Tight = [0, 0, 0, 4, 3, 2];
    private static readonly double[] WiderOutline = [-0.5, -0.5, 0, 4.5, 3.5, 2];

    private static GeometryData Cube() => new()
    {
        BoundingBox = (double[])Tight.Clone(),
        Faces = Quads.Select(quad => new GeoFace { OuterVertices = quad.Select(index => Points[index]).ToList() }).ToList(),
        BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
    };

    private static Element Wall(bool withOutline) => new()
    {
        Id = 100, CategoryId = -2000011, Category = "OST_Walls", TypeId = 900, Name = "Wall", IsDiscovered = true,
        Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 1.5, 0], EndPoint = [4, 1.5, 0] },
        BoundingBox = withOutline ? (double[])WiderOutline.Clone() : (double[])Tight.Clone(),
        BoundingBoxIsHeaderOutline = withOutline,
        Geometry = Cube(),
    };

    private static string Data(params Element[] elements)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rr-header-outline-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(new RevitFileInfo { Elements = [.. elements] }, path);
            string ifc = File.ReadAllText(path);
            return ifc[ifc.IndexOf("DATA;", StringComparison.Ordinal)..];
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string Rectangle(string ifc) => Regex.Match(ifc, @"=IFCRECTANGLEPROFILEDEF\(\.AREA\.,'',#\d+,([^,]+),([^)]+)\)").Value;

    [Fact]
    public void AWall_IsWrittenFromTheOutlineOfItsHeader()
    {
        string fromGeometry = Data(Wall(withOutline: false));
        string fromOutline = Data(Wall(withOutline: true));

        Assert.Contains(",1219.2,914.4)", Rectangle(fromGeometry));    // the 4 by 3 feet of the geometry
        Assert.Contains(",1524.,1219.2)", Rectangle(fromOutline));     // the 5 by 4 feet of the outline
    }

    private static string BeamIfc(bool withOutline)
    {
        double[] axis = [0, 1, 0], profileX = [1, 0, 0], centre = [2, 5, 4];
        var beam = IfcExporterBeamCommonPropertySetTests.Product(102, 103, -2001320, "OST_StructuralFraming", "Nominal rectangular beam");
        // the box of the element is the envelope of the beam, 10 feet long, and its geometry is 9.2 feet long
        beam.BoundingBox = IfcExporterBeamGeometryTests.PrismBounds(centre, axis, profileX, length: 10, width: 0.4, depth: 0.6);
        beam.BoundingBoxIsHeaderOutline = withOutline;
        beam.Geometry = IfcExporterBeamGeometryTests.RectangularPrism(centre, axis, profileX, length: 9.2, width: 0.4, depth: 0.6);
        return Data(beam);
    }

    [Fact]
    public void ABeam_IsWrittenFromTheBoxOfItsGeometry_NotFromTheOutlineOfItsHeader()
    {
        // A box that is the element's own is the envelope the beam is written to (3048 mm, 10 feet)...
        Assert.Contains(",3048.);", BeamIfc(withOutline: false));
        // ...and the outline of a header is not: the beam is written from its geometry, as it was before the outline was read.
        string fromOutline = BeamIfc(withOutline: true);
        Assert.DoesNotContain(",3048.);", fromOutline);
        Assert.Contains("=IFCBEAM(", fromOutline);
    }
}