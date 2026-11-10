using RvtIO.Ifc;
using RvtIO.Internal;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The circular edges of the profile of an extrusion are sampled in ceil(|span| / (pi / 16)) equal
/// steps over the stored span of the arc, whatever tessellation the cache holds. The origin of the frame is the mean of the corners.</summary>
public class ExtrusionArcResampleCurrentTests
{
    private static readonly Arc Quarter = new([0, 0, 0], [1, 0, 0], [0, 1, 0], 4, 0, Math.PI / 2);

    private static double[] OnCircle(double angle) => [4 * Math.Cos(angle), 4 * Math.Sin(angle), 0];

    [Fact]
    public void ACoarseArc_GetsTheSamplesOfReference_AndTheCornersStayTheOnesOfTheLoop()
    {
        // the quarter circle from (4, 0) to (0, 4) held as three chords, the corner (0, 0) first
        double[][] ring = [[0, 0, 0], [4, 0, 0], OnCircle(Math.PI / 6), OnCircle(Math.PI / 3), [0, 4, 0]];

        Assert.True(IfcExporter.TryResampleArcsCurrent(ring, [], [Quarter], out var outer, out _, out var corners));

        Assert.Equal(10, outer.Count);                       // (0, 0), (4, 0), seven samples, (0, 4)
        Assert.Equal(ring[0], outer[0]);
        Assert.Equal(ring[1], outer[1]);
        for (int step = 1; step < 8; step++)
        {
            var expected = OnCircle(Math.PI / 16 * step);
            Assert.All(Enumerable.Range(0, 3), axis => Assert.Equal(expected[axis], outer[1 + step][axis], 9));
        }
        Assert.Equal(ring[4], outer[9]);
        Assert.Equal([ring[0], ring[1], ring[4]], corners);
    }

    [Fact]
    public void AnArcRunningAgainstTheRing_IsSampledFromTheRingEnd()
    {
        double[][] ring = [[0, 0, 0], [0, 4, 0], OnCircle(Math.PI / 3), OnCircle(Math.PI / 6), [4, 0, 0]];

        Assert.True(IfcExporter.TryResampleArcsCurrent(ring, [], [Quarter], out var outer, out _, out _));

        Assert.Equal(10, outer.Count);
        var expected = OnCircle(Math.PI / 2 - Math.PI / 16);
        Assert.All(Enumerable.Range(0, 3), axis => Assert.Equal(expected[axis], outer[2][axis], 9));
    }

    [Fact]
    public void ARingThatDoesNotCarryTheArc_IsLeftAlone()
    {
        double[][] ring = [[0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0]];

        Assert.False(IfcExporter.TryResampleArcsCurrent(ring, [], [Quarter], out var outer, out _, out var corners));

        Assert.Equal(ring, outer);
        Assert.Equal(ring, corners);
    }

    [Fact]
    public void AFaceWithoutCircularEdges_IsLeftAlone()
    {
        double[][] ring = [[0, 0, 0], [4, 0, 0], [4, 4, 0]];
        Assert.False(IfcExporter.TryResampleArcsCurrent(ring, [], null, out var outer, out _, out _));
        Assert.Equal(ring, outer);
    }

    [Fact]
    public void TheArcsOfTheOuterRing_ArePassedOnWithTheirPlaceAndTheSideTheyBulgeTo()
    {
        // the quarter disc, counter-clockwise and clockwise: the arc bulges out of the ring whichever way the ring runs
        double[][] disc = [[0, 0, 0], [4, 0, 0], OnCircle(Math.PI / 4), [0, 4, 0]];
        Assert.True(IfcExporter.TryResampleArcsCurrent(disc, [], [Quarter], out _, out _, out _, out var arcs));
        Assert.Equal([new IfcExporter.ArcChordsCurrent(1, 8, Math.PI / 2)], arcs);
        double[][] turned = [[0, 0, 0], [0, 4, 0], OnCircle(Math.PI / 4), [4, 0, 0]];
        Assert.True(IfcExporter.TryResampleArcsCurrent(turned, [], [Quarter], out _, out _, out _, out arcs));
        Assert.Equal([new IfcExporter.ArcChordsCurrent(1, 8, Math.PI / 2)], arcs);

        // the square with the quarter disc cut out of it: the same arc bulges into the ring
        double[][] cut = [[4, 4, 0], [0, 4, 0], OnCircle(Math.PI / 4), [4, 0, 0]];
        Assert.True(IfcExporter.TryResampleArcsCurrent(cut, [], [Quarter], out _, out _, out _, out arcs));
        Assert.Equal([new IfcExporter.ArcChordsCurrent(1, 8, -Math.PI / 2)], arcs);

        // a ring that does not carry the arc passes none on
        double[][] square = [[0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0]];
        Assert.False(IfcExporter.TryResampleArcsCurrent(square, [], [Quarter], out _, out _, out _, out arcs));
        Assert.Empty(arcs);
    }

    [Fact]
    public void AnArcOfAHole_IsResampledInTheHoleRing()
    {
        // a hole bounded by two half circles of radius 1 about (5, 5); the outer ring is a square
        var upper = new Arc([5, 5, 0], [1, 0, 0], [0, 1, 0], 1, 0, Math.PI);
        var lower = new Arc([5, 5, 0], [1, 0, 0], [0, 1, 0], 1, Math.PI, 2 * Math.PI);
        double[] At(double angle) => [5 + Math.Cos(angle), 5 + Math.Sin(angle), 0];
        double[][] outerRing = [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]];
        var hole = new List<double[]> { At(0), At(Math.PI / 2), At(Math.PI), At(3 * Math.PI / 2) };

        Assert.True(IfcExporter.TryResampleArcsCurrent(outerRing, [hole], [upper, lower], out var outer, out var inner, out var corners));

        Assert.Equal(outerRing, outer);
        Assert.Equal(outerRing, corners);
        Assert.Equal(32, Assert.Single(inner).Count);         // two arcs of sixteen steps
    }
}
