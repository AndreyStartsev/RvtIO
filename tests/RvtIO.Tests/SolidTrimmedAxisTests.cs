using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The trimmed axis of a straight wall without a face-history trim: the location line bounded by the extreme
/// parameters of the vertices of the vertical end faces of its first solid.</summary>
public class SolidTrimmedAxisTests
{
    // Curve driver: origin (10,0,0), direction (2,0,0) (not unit), so parameter t is at x = 10 + 2t.
    private static readonly double[] Origin = [10, 0, 0];
    private static readonly double[] Direction = [2, 0, 0];

    private static GeoFace Face(double[] normal, params double[][] ring) => new() { Normal = normal, OuterVertices = ring.ToList() };

    private static GeoFace EndFace(double x0, double x1, double y = 0) =>
        Face([1, 0, 0], [x0, y, 0], [x1, y, 0], [x1, y, 9], [x0, y, 9]);

    private static GeoFace Side(double x0, double x1, double y) =>
        Face([0, 1, 0], [x0, y, 0], [x1, y, 0], [x1, y, 9], [x0, y, 9]);

    private static Element Wall(IEnumerable<GeoFace> faces, double endParam0, double endParam1, List<GeoBrepSolid>? solids = null) => new()
    {
        CategoryId = -2000011,
        Location = new LocationCurve { CurveType = "Line", DriverOrigin = Origin, DriverDirection = Direction, DriverEndParams = [endParam0, endParam1] },
        Geometry = new GeometryData { Faces = faces.ToList(), BrepSolids = solids },
    };

    private static void Apply(Element wall) => ElementBuilder.ApplySolidTrimmedAxis([wall]);

    [Fact]
    public void EndFaceVertices_BoundTheAxis()
    {
        // axis from t = 0 (x = 10) to t = 5 (x = 20); the end faces reach x = 9 and x = 21
        var wall = Wall([EndFace(9, 9.5), EndFace(20.5, 21), Side(9, 21, 0.1)], 0, 5);

        Apply(wall);

        Assert.Equal([[9.0, 0, 0], [21.0, 0, 0]], wall.OwnWallTrimmedAxis);
    }

    [Fact]
    public void OriginBeyondTheEnd_TakesTheUpperEndParameter()
    {
        // every vertex lies at a negative parameter (x < 10): the upper end stands in
        var wall = Wall([EndFace(2, 3), EndFace(6, 8)], -4, -1);

        Apply(wall);

        Assert.Equal(2, wall.OwnWallTrimmedAxis![0][0], 12);
        Assert.Equal(10 + 2 * -1, wall.OwnWallTrimmedAxis[1][0], 12);
    }

    [Fact]
    public void NegativeEndParameters_WithAnEndFaceAboveTheOrigin_KeepTheVertexMaximum()
    {
        var wall = Wall([EndFace(6, 7), EndFace(11, 12)], -4, -0.5);

        Apply(wall);

        Assert.Equal(6, wall.OwnWallTrimmedAxis![0][0], 12);
        Assert.Equal(12, wall.OwnWallTrimmedAxis[1][0], 12);
    }

    [Fact]
    public void TiltedEndFaces_AreMeasuredByTheirVertices()
    {
        // end faces cut at an angle: vertices of one face at x = 8 and x = 9, of the other at x = 21 and x = 22
        var wall = Wall([Face([0.7, 0.7, 0], [8, 0, 0], [9, 0.3, 0], [9, 0.3, 9], [8, 0, 9]),
            Face([0.7, 0.7, 0], [21, 0, 0], [22, 0.3, 0], [22, 0.3, 9], [21, 0, 9])], 0, 5);

        Apply(wall);

        Assert.Equal(8, wall.OwnWallTrimmedAxis![0][0], 12);
        Assert.Equal(22, wall.OwnWallTrimmedAxis[1][0], 12);
    }

    [Fact]
    public void OnlyTheFirstSolidCounts()
    {
        var faces = new[] { EndFace(9, 9.5), EndFace(20.5, 21), EndFace(0, 1) };
        var wall = Wall(faces, 0, 5, [new GeoBrepSolid { FaceIndices = [0, 1] }, new GeoBrepSolid { FaceIndices = [2] }]);

        Apply(wall);

        Assert.Equal(9, wall.OwnWallTrimmedAxis![0][0], 12);
        Assert.Equal(21, wall.OwnWallTrimmedAxis[1][0], 12);
    }

    [Fact]
    public void WithoutUsableEndFaces_TheAxisStaysUntrimmed()
    {
        // only faces parallel to the axis, and a horizontal one
        var wall = Wall([Side(9, 21, 0.1), Side(9, 21, -0.1), Face([0, 0, 1], [9, 0, 0], [21, 0, 0], [21, 1, 0])], 0, 5);

        Apply(wall);

        Assert.Null(wall.OwnWallTrimmedAxis);
    }

    [Fact]
    public void HistoryTrim_Wins()
    {
        var wall = Wall([EndFace(9, 9.5), EndFace(20.5, 21)], 0, 5);
        double[][] history = [[11, 0, 0], [19, 0, 0]];
        wall.OwnWallTrimmedAxis = history;

        Apply(wall);

        Assert.Same(history, wall.OwnWallTrimmedAxis);
    }

    [Fact]
    public void ArcAndNonWallElements_AreLeftAlone()
    {
        var arc = Wall([EndFace(9, 9.5), EndFace(20.5, 21)], 0, 5);
        arc.Location!.CurveType = "Arc";
        var column = Wall([EndFace(9, 9.5), EndFace(20.5, 21)], 0, 5);
        column.CategoryId = -2000100;

        Apply(arc);
        Apply(column);

        Assert.Null(arc.OwnWallTrimmedAxis);
        Assert.Null(column.OwnWallTrimmedAxis);
    }
}
