using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class OwnWallQuantityHeightTests
{
    [Fact]
    public void SimpleWallHeightComesFromCollectedSourceSolidsBeforeGrossReconstruction()
    {
        var wall = Wall();
        wall.BoundingBox = [0,0,0,1,1,99];
        wall.Parameters.Add(new() { Id = -1001105, Value = 99.0 });
        Assert.Equal(1.6 * 0.3048, IfcExporter.OwnSimpleWallHeightCurrent(wall)!.Value, 12);
    }

    [Fact]
    public void InactiveTiltKeepsTheBodyExporterHeightInsteadOfTheAxisRouteHeight()
    {
        var wall = Wall();
        wall.Geometry = wall.OwnCurrentCutPairBodyCurrent;
        foreach (var face in wall.Geometry!.Faces) face.GeometryNode = 7;
        wall.Geometry.InactiveFaces = [new() { GeometryNode = 7, Normal = [1, 1, 1] }];

        Assert.Null(IfcExporter.OwnSimpleWallHeightCurrent(wall));
    }

    [Theory]
    [InlineData("no-source")] [InlineData("open-source")]
    [InlineData("incomplete-coverage")] [InlineData("face-in-no-solid")]
    [InlineData("nonrectangular-helper")] [InlineData("nonfinite")]
    public void UnsupportedOrUnconfirmedSourceKeepsTheWrittenExtrusionHeight(string mutation)
    {
        var wall = Wall();
        switch (mutation)
        {
            case "no-source": wall.OwnCurrentCutPairBodyCurrent = null; wall.Geometry = null; break;
            case "open-source": wall.OwnCurrentCutPairBodyCurrent!.BrepSolids![0].IsClosed = false; break;
            case "incomplete-coverage": wall.OwnCurrentCutPairBodyCurrent!.BrepSolids![0].FaceIndices.RemoveAt(0); break;
            // A triangle that belongs to no solid stands for a mesh: the wall is then measured by the box in its header, which
            // is not modelled, so the written height stays.
            case "face-in-no-solid":
                wall.OwnCurrentCutPairBodyCurrent!.Faces.Add(new() { OuterVertices = [[0, 0, 9], [1, 0, 9], [0, 1, 9]], Normal = [0, 0, 1] });
                break;
            case "nonrectangular-helper": wall.OwnWallHelper = new(3524, true, true, [8]); break;
            case "nonfinite": wall.OwnCurrentCutPairBodyCurrent!.Faces[0].OuterVertices![0][2] = double.NaN; break;
        }
        Assert.Null(IfcExporter.OwnSimpleWallHeightCurrent(wall));
    }

    [Theory]
    [InlineData("no-helper")]
    [InlineData("cross-section-not-vertical")]
    [InlineData("cross-section-missing")]
    public void WallHeightIsMeasuredWithoutTheProfileHelperOrTheCrossSection(string mutation)
    {
        // The height is taken from the stored solids before the class of the wall is decided: a wall without a profile helper
        // and a wall whose cross-section attribute is missing or not vertical are measured as well, although their stored
        // height parameter says otherwise.
        var wall = Wall();
        switch (mutation)
        {
            case "no-helper": wall.OwnWallHelper = null; break;
            case "cross-section-not-vertical": wall.OwnWallCrossSection = 0; break;
            case "cross-section-missing": wall.OwnWallCrossSection = null; break;
        }
        Assert.Equal(1.6 * 0.3048, IfcExporter.OwnSimpleWallHeightCurrent(wall)!.Value, 12);
    }

    [Fact]
    public void HeightIsMeasuredFromTheWallGeometryWhenNoCutPairBodyIsHeld()
    {
        // Fails on a build that needs OwnCurrentCutPairBodyCurrent, the helper and the cross-section attribute.
        var wall = Wall();
        wall.Geometry = wall.OwnCurrentCutPairBodyCurrent;
        wall.OwnCurrentCutPairBodyCurrent = null;
        wall.OwnWallHelper = null;
        wall.OwnWallCrossSection = 0;
        wall.BoundingBox = [0, 0, 0, 1, 1, 99];
        wall.Parameters.Add(new() { Id = -1001105, Value = 99.0 });
        Assert.Equal(1.6 * 0.3048, IfcExporter.OwnSimpleWallHeightCurrent(wall)!.Value, 12);
    }

    [Fact]
    public void SeveralSolidsAreMeasuredByTheZRangeOfTheFirstOne()
    {
        // The first solid decides the class of the wall and is the one that is extruded and measured. The second solid here is a
        // taller post beside the box: 2.1 .. 3.7 ft for the first, 2.1 .. 4.1 ft for both.
        var wall = Wall();
        AddSolid(wall.OwnCurrentCutPairBodyCurrent!, zFrom: 2.1, zTo: 4.1);
        Assert.Equal(1.6 * 0.3048, IfcExporter.OwnSimpleWallHeightCurrent(wall)!.Value, 12);
    }

    [Theory]
    [InlineData("open-second-solid")] [InlineData("empty-second-solid")] [InlineData("face-in-two-solids")]
    public void TheOtherSolidsOfSeveralAreNotLookedAt_WhateverTheyAre(string mutation)
    {
        // The first solid decides, for the depth of the extrusion as for the Height: a second solid that is open, empty or shares a
        // face with the first one does not make the wall keep the depth that was written.
        var wall = Wall();
        AddSolid(wall.OwnCurrentCutPairBodyCurrent!, zFrom: 2.1, zTo: 4.1);
        var solids = wall.OwnCurrentCutPairBodyCurrent!.BrepSolids!;
        switch (mutation)
        {
            case "open-second-solid": solids[1].IsClosed = false; break;
            case "empty-second-solid": solids[1].FaceIndices.Clear(); break;
            case "face-in-two-solids": solids[1].FaceIndices.Add(0); break;
        }
        Assert.Equal(1.6 * 0.3048, IfcExporter.OwnSimpleWallHeightCurrent(wall)!.Value, 12);
    }

    [Fact]
    public void AnOpenFirstSolidOfSeveralKeepsTheWrittenHeight()
    {
        var wall = Wall();
        AddSolid(wall.OwnCurrentCutPairBodyCurrent!, zFrom: 2.1, zTo: 4.1);
        wall.OwnCurrentCutPairBodyCurrent!.BrepSolids![0].IsClosed = false;
        Assert.Null(IfcExporter.OwnSimpleWallHeightCurrent(wall));
    }

    [Fact]
    public void AFaceOfTheFirstSolidOutsideTheFacesKeepsTheWrittenHeight()
    {
        var wall = Wall();
        AddSolid(wall.OwnCurrentCutPairBodyCurrent!, zFrom: 2.1, zTo: 4.1);
        wall.OwnCurrentCutPairBodyCurrent!.BrepSolids![0].FaceIndices.Add(999);
        Assert.Null(IfcExporter.OwnSimpleWallHeightCurrent(wall));
    }

    [Fact]
    public void AWallWithLeaningSidesIsNotMeasuredAsASimpleWall()
    {
        // A truly slanted wall: the top of the box is shifted along X, so the side faces lean (normals with a Z part), which
        // isSimpleRectangularWall refuses whatever the cross-section attribute says. The body exporter keeps its extrusion depth.
        var wall = Wall(lean: 0.5);
        Assert.Equal(1, wall.OwnWallCrossSection);
        Assert.Null(IfcExporter.OwnSimpleWallHeightCurrent(wall));
    }

    /// <summary>The polygon normal (Newell) in XVector, where the decoder of a block stashes it.</summary>
    private static void SetNormals(GRepData body)
    {
        foreach (var face in body.Faces)
        {
            double[] n = [0, 0, 0];
            var ring = face.Vertices!;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                n[0] += (a[1] - b[1]) * (a[2] + b[2]);
                n[1] += (a[2] - b[2]) * (a[0] + b[0]);
                n[2] += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            face.XVector = [n[0] / length, n[1] / length, n[2] / length];
        }
    }

    private static void AddSolid(GeometryData body, double zFrom, double zTo)
    {
        var box = OwnWallPlanarBodyTests.Cube();
        foreach (var face in box.Faces)
            face.Vertices = face.Vertices!.Select(p => new[] { p[0] + 2, p[1], zFrom + p[2] * (zTo - zFrom) }).ToList();
        SetNormals(box);
        var extra = ElementBuilder.BuildGeometryData(box);
        int first = body.Faces.Count;
        body.Faces.AddRange(extra.Faces);
        body.BrepSolids!.Add(new() { IsClosed = true, FaceIndices = Enumerable.Range(first, extra.Faces.Count).ToList() });
    }

    [Fact]
    public void StandardCaseWallExtrusionDepthIsUpdatedFromSourceSolids()
    {
        Assert.Equal(StepWriter.Real(1.6 * 0.3048), WrittenExtrusionDepth(Wall()));
    }

    [Fact]
    public void StandardCaseWallExtrusionDepthIsUpdatedFromTheWallGeometryWithoutHelperOrCrossSection()
    {
        // The written extrusion of 3.7 ft is replaced by the Z range of the stored solid (1.6 ft) although the wall holds no
        // cut-pair body, no profile helper and no cross-section attribute: on a build that needs them the depth stays 3.7 ft.
        var wall = Wall();
        wall.Geometry = wall.OwnCurrentCutPairBodyCurrent;
        wall.OwnCurrentCutPairBodyCurrent = null;
        wall.OwnWallHelper = null;
        wall.OwnWallCrossSection = null;
        Assert.Equal(StepWriter.Real(1.6 * 0.3048), WrittenExtrusionDepth(wall));
    }

    private static string WrittenExtrusionDepth(Element wall)
    {
        wall.Id = 42;
        var f = new IfcExporterCurrentWallExtrusionTests.Fixture();
        int wallEntity = f.StandardCaseWall(1, 2, 0.3, 0, 1, 3.2, 0.2, 3.7);
        var elements = new Dictionary<int, Element> { [42] = wall };
        var productToElement = new Dictionary<int, int> { [wallEntity] = 42 };

        Assert.Equal(1, new IfcExporter().ApplyCurrentWallExtrusions(f.W, elements, productToElement));

        var attributes = f.Attributes(wallEntity);
        var representations = f.Attributes(int.Parse(attributes[6][1..]));
        var ids = representations[2].Trim('(', ')').Split(',').Select(token => int.Parse(token[1..])).ToList();
        var body = f.Attributes(ids[1]);
        var solid = f.Attributes(int.Parse(body[3].Trim('(', ')')[1..]));
        return solid[3];
    }

    private static Element Wall(double lean = 0)
    {
        var source = OwnWallPlanarBodyTests.Cube();
        foreach (var face in source.Faces)
            face.Vertices = face.Vertices!.Select(p => new[] { p[0] + lean * p[2], p[1], 2.1 + p[2] * 1.6 }).ToList();
        SetNormals(source);
        return new() { OwnWallCrossSection = 1, OwnWallHelper = new(3524, true, true, [4]),
            OwnCurrentCutPairBodyCurrent = ElementBuilder.BuildGeometryData(source) };
    }
}
