using RvtIO.Meshes;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The public triangle meshes (<see cref="ElementMeshes"/>) on private project files: a placement is exactly the
/// element's own geometry, triangles follow their normals, and the result does not change between two runs.</summary>
public class ElementMeshesSampleTests
{
    private static readonly Lazy<RvtDocument> Sample2022 = new(() => RvtDocument.Open(TestFiles.Find("Test_2022.rvt")));

    private static RvtDocument Open2022()
    {
        TestFiles.Find("Test_2022.rvt");   // skips when the file is missing
        return Sample2022.Value;
    }

    private static (double[] Min, double[] Max, double Area) Measure(IEnumerable<TriangleMesh> meshes, MeshTransform? transform = null)
    {
        double[] min = [double.MaxValue, double.MaxValue, double.MaxValue], max = [double.MinValue, double.MinValue, double.MinValue];
        double area = 0;
        foreach (var mesh in meshes)
        {
            var points = new MeshVector[mesh.Positions.Length / 3];
            for (int vertex = 0; vertex < points.Length; vertex++)
            {
                var point = new MeshVector(mesh.Positions[3 * vertex], mesh.Positions[3 * vertex + 1], mesh.Positions[3 * vertex + 2]);
                points[vertex] = transform is { } placed ? placed.Apply(point) : point;
            }
            for (int index = 0; index < mesh.Indices.Length; index += 3)
            {
                var (a, b, c) = (points[mesh.Indices[index]], points[mesh.Indices[index + 1]], points[mesh.Indices[index + 2]]);
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                area += Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
                foreach (var p in new[] { a, b, c })
                {
                    min[0] = Math.Min(min[0], p.X); min[1] = Math.Min(min[1], p.Y); min[2] = Math.Min(min[2], p.Z);
                    max[0] = Math.Max(max[0], p.X); max[1] = Math.Max(max[1], p.Y); max[2] = Math.Max(max[2], p.Z);
                }
            }
        }
        return (min, max, area);
    }

    private static (double[] Min, double[] Max, double Area) MeasurePlacements(MeshSet set, ElementMesh element)
    {
        var parts = element.Placements.Select(placement => Measure(set.Symbols[placement.SymbolId], placement.Transform)).ToList();
        return (Enumerable.Range(0, 3).Select(axis => parts.Min(part => part.Min[axis])).ToArray(),
            Enumerable.Range(0, 3).Select(axis => parts.Max(part => part.Max[axis])).ToArray(), parts.Sum(part => part.Area));
    }

    /// <summary>The placed symbols cover what the element's own faces cover: the same box and the same area.</summary>
    private static void AssertPlacementsAreTheElement(RvtDocument document, MeshSet set, ElementMesh element)
    {
        var placed = MeasurePlacements(set, element);
        var own = Measure(ElementMeshes.Triangulate(document.GetElement(element.ElementId)!.Geometry!.Faces, _ => 0).Meshes);
        for (int axis = 0; axis < 3; axis++)
        {
            Assert.Equal(own.Min[axis], placed.Min[axis], 6);
            Assert.Equal(own.Max[axis], placed.Max[axis], 6);
        }
        Assert.Equal(own.Area, placed.Area, 6);
    }

    private static void AssertWindingFollowsNormals(TriangleMesh mesh)
    {
        for (int index = 0; index < mesh.Indices.Length; index += 3)
        {
            int a = 3 * mesh.Indices[index], b = 3 * mesh.Indices[index + 1], c = 3 * mesh.Indices[index + 2];
            double ux = mesh.Positions[b] - mesh.Positions[a], uy = mesh.Positions[b + 1] - mesh.Positions[a + 1], uz = mesh.Positions[b + 2] - mesh.Positions[a + 2];
            double vx = mesh.Positions[c] - mesh.Positions[a], vy = mesh.Positions[c + 1] - mesh.Positions[a + 1], vz = mesh.Positions[c + 2] - mesh.Positions[a + 2];
            double dot = (uy * vz - uz * vy) * mesh.Normals[a] + (uz * vx - ux * vz) * mesh.Normals[a + 1] + (ux * vy - uy * vx) * mesh.Normals[a + 2];
            // A triangle of no area (three corners on a line, up to rounding) has no orientation.
            double scale = Math.Sqrt((ux * ux + uy * uy + uz * uz) * (vx * vx + vy * vy + vz * vz));
            Assert.True(dot >= -1e-9 * scale, $"triangle {index / 3} runs against its normal");
        }
    }

    [SkippableFact]
    public void DoorInAWall_IsAPlacementOfItsSymbol()
    {
        var document = RvtDocument.Open(TestFiles.Find("test_door_in_wall.rvt"));

        var set = ElementMeshes.Extract(document);

        Assert.Equal(2, set.Elements.Count);
        Assert.All(set.Elements, element => Assert.Equal(MeshCoverage.Complete, element.Coverage));
        var door = Assert.Single(set.Elements, element => document.GetElement(element.ElementId)!.CategoryId == -2000023);
        var wall = Assert.Single(set.Elements, element => document.GetElement(element.ElementId)!.CategoryId == -2000011);
        Assert.Empty(door.Meshes);
        var placement = Assert.Single(door.Placements);
        Assert.Equal(1, Math.Abs(placement.Transform.Determinant), 6);
        Assert.Equal([placement.SymbolId], set.Symbols.Keys);
        AssertPlacementsAreTheElement(document, set, door);
        Assert.Empty(wall.Placements);
        Assert.NotEmpty(wall.Meshes);
    }

    [SkippableFact]
    public void Sample_PlacementsAreTheirElementsAndTrianglesFollowTheirNormals()
    {
        var document = Open2022();

        var set = ElementMeshes.Extract(document);

        Assert.False(set.GeometryUndecoded);
        Assert.Equal(set.Elements.Count, set.Elements.Select(element => element.ElementId).Distinct().Count());
        Assert.DoesNotContain(set.Elements, element => element.Coverage == MeshCoverage.Undecoded);
        var placed = set.Elements.Where(element => element.Placements.Count > 0).ToList();
        Assert.NotEmpty(placed);
        Assert.Equal(placed.SelectMany(element => element.Placements).Select(placement => placement.SymbolId).ToHashSet(),
            set.Symbols.Keys.ToHashSet());
        foreach (var element in placed)
        {
            Assert.Empty(element.Meshes);
            AssertPlacementsAreTheElement(document, set, element);
        }
        foreach (var mesh in set.Elements.SelectMany(element => element.Meshes).Concat(set.Symbols.Values.SelectMany(meshes => meshes)))
        {
            Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
            Assert.Equal(0, mesh.Indices.Length % 3);
            AssertWindingFollowsNormals(mesh);
        }
        // Every door and window of the sample is its symbol placed.
        foreach (int category in new[] { -2000023, -2000014 })
        {
            var inserts = set.Elements.Where(element => document.GetElement(element.ElementId)!.CategoryId == category).ToList();
            Assert.NotEmpty(inserts);
            Assert.All(inserts, element => Assert.NotEmpty(element.Placements));
        }
    }

    [SkippableFact]
    public void Sample_GivesTheSameMeshesTwice()
    {
        var document = Open2022();

        var first = ElementMeshes.Extract(document);
        var second = ElementMeshes.Extract(document);

        Assert.Equal(first.Elements.Select(element => (element.ElementId, element.Coverage, element.Placements.Count, element.UntriangulatedFaces,
                element.Meshes.Sum(mesh => mesh.Indices.Length))),
            second.Elements.Select(element => (element.ElementId, element.Coverage, element.Placements.Count, element.UntriangulatedFaces,
                element.Meshes.Sum(mesh => mesh.Indices.Length))));
        Assert.Equal(first.Symbols.Keys.Order(), second.Symbols.Keys.Order());
    }

    [SkippableFact]
    public void FileOfALaterRelease_ReadWithoutTheRewrite_IsUndecoded()
    {
        var document = RvtDocument.Open(TestFiles.Find("Test_2025.rvt"));

        var set = ElementMeshes.Extract(document);

        Assert.True(set.GeometryUndecoded);
        Assert.NotEmpty(set.Elements);
        Assert.All(set.Elements, element =>
        {
            Assert.Equal(MeshCoverage.Undecoded, element.Coverage);
            Assert.Empty(element.Meshes);
            Assert.Empty(element.Placements);
        });
    }

    [SkippableFact]
    public void FileOfALaterRelease_ReadWithTheRewrite_IsDecoded()
    {
        var document = RvtDocument.Open(TestFiles.Find("Test_2025.rvt"), new RvtReaderOptions { R22Layout = true });

        var set = ElementMeshes.Extract(document);

        Assert.False(set.GeometryUndecoded);
        Assert.Contains(set.Elements, element => element.Placements.Count > 0);
        Assert.Contains(set.Elements, element => element.Meshes.Count > 0);
    }
}
