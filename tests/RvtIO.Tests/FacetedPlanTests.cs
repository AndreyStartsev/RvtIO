using System.Text;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Plans depend on a static switch that every export sets, so these tests must not overlap an export running in another test class.</summary>
[CollectionDefinition("FacetedPlans", DisableParallelization = true)]
public sealed class FacetedPlansCollection;

/// <summary>The faceted shell plans computed ahead of the writer: one thread and many threads give the same plans, bit for bit, in input
/// order, and an exception stays in the slot of the geometry that threw it.</summary>
[Collection("FacetedPlans")]
public class FacetedPlanTests
{
    private static GeoFace Face(params double[][] corners) => new() { OuterVertices = [.. corners], PlaneSurface = true };

    private static List<GeoFace> Box(double x, double y, double z, double a, double b, double c)
    {
        double[] p000 = [x, y, z], p100 = [x + a, y, z], p110 = [x + a, y + b, z], p010 = [x, y + b, z];
        double[] p001 = [x, y, z + c], p101 = [x + a, y, z + c], p111 = [x + a, y + b, z + c], p011 = [x, y + b, z + c];
        return
        [
            Face(p000, p010, p110, p100), Face(p001, p101, p111, p011), Face(p000, p100, p101, p001),
            Face(p010, p011, p111, p110), Face(p000, p001, p011, p010), Face(p100, p110, p111, p101),
        ];
    }

    private static GeometryData Solids(bool withSolids, bool closed, params List<GeoFace>[] boxes)
    {
        var geometry = new GeometryData();
        foreach (var box in boxes)
        {
            var solid = new GeoBrepSolid { IsClosed = closed };
            foreach (var face in box) { solid.FaceIndices.Add(geometry.Faces.Count); geometry.Faces.Add(face); }
            if (withSolids) (geometry.BrepSolids ??= []).Add(solid);
        }
        return geometry;
    }

    private static List<GeometryData> Geometries()
    {
        var random = new Random(5);
        var list = new List<GeometryData>();
        for (int i = 0; i < 60; i++)
        {
            double s = 1 + random.Next(1, 9) * 0.25;
            var boxes = Enumerable.Range(0, 1 + i % 3).Select(k => Box(k * 3.0, i * 0.5, 0, s, s * 0.5, s * 2)).ToArray();
            list.Add(Solids(i % 4 != 3, true, boxes));
        }
        // Edge records that do not close the solid: only the current profile judges such a solid by its polygons, so the flag changes the plan.
        for (int i = 0; i < 8; i++)
            list.Add(Solids(true, false, Box(0, i, 0, 1 + i * 0.25, 1, 2), Box(5, i, 0, 1, 1 + i * 0.25, 2)));
        // A corner pulled out of its plane: the three faces at it are not planar and are triangulated unless the plan keeps proven planar faces.
        for (int i = 0; i < 6; i++)
        {
            var box = Box(0, i, 0, 1 + i * 0.25, 1, 2);
            double[] pulled = [1 + i * 0.25, i + 1, 2.4];
            foreach (var face in box)
                for (int v = 0; v < face.OuterVertices!.Count; v++)
                    if (face.OuterVertices[v][0] == pulled[0] && face.OuterVertices[v][1] == pulled[1] && face.OuterVertices[v][2] == 2)
                        face.OuterVertices[v] = [.. pulled];
            list.Add(Solids(true, true, box));
        }
        list.Add(new GeometryData());
        return list;
    }

    private static string Fingerprint(List<IfcExporter.ClosedComponentCurrent> plan)
    {
        var sb = new StringBuilder();
        foreach (var c in plan)
        {
            sb.Append("C[").AppendJoin(',', c.SourceIndices).Append("][").AppendJoin(',', c.ConsumedIndices).Append("][");
            sb.Append(c.OrientedSources == null ? "-" : string.Join(',', c.OrientedSources)).Append(']');
            foreach (var f in c.OrientedFaces)
            {
                sb.Append('F');
                foreach (var v in f.OuterVertices!) foreach (var d in v) sb.Append(BitConverter.DoubleToInt64Bits(d)).Append(' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_thread_and_many_threads_give_identical_plans_in_input_order(bool currentProfile)
    {
        var geometries = Geometries();
        var one = IfcExporter.ComputePlans(geometries, currentProfile, 1);
        var many = IfcExporter.ComputePlans(geometries, currentProfile, 16);
        Assert.Equal(geometries.Count, many.Length);
        int nonEmpty = 0;
        for (int i = 0; i < geometries.Count; i++)
        {
            Assert.Null(one[i].Error);
            Assert.Null(many[i].Error);
            var inline = IfcExporter.ComputeClosedComponents(geometries[i], false, currentProfile);
            Assert.Equal(Fingerprint(inline), Fingerprint(one[i].Result!));
            Assert.Equal(Fingerprint(inline), Fingerprint(many[i].Result!));
            if (inline.Count > 0) nonEmpty++;
        }
        Assert.True(nonEmpty > (currentProfile ? 40 : 30));
    }

    [Fact]
    public void The_current_profile_flag_reaches_the_computation()
    {
        var geometries = Geometries();
        var on = IfcExporter.ComputePlans(geometries, true, 4);
        var off = IfcExporter.ComputePlans(geometries, false, 4);
        int differing = 0;
        for (int i = 0; i < geometries.Count; i++)
            if (Fingerprint(on[i].Result!) != Fingerprint(off[i].Result!)) differing++;
        Assert.True(differing >= 8);
        // The geometries 60..67 are solids whose edge records do not close them: only the current profile accepts them by their polygons.
        for (int i = 60; i < 68; i++)
        {
            Assert.NotEmpty(on[i].Result!);
            Assert.Empty(off[i].Result!);
        }
    }

    [Fact]
    public void An_exception_stays_in_the_slot_of_its_geometry()
    {
        var geometries = Geometries();
        var slots = IfcExporter.ComputePlans(geometries, false, 8, (geo, preserve, currentProfile) =>
            ReferenceEquals(geo, geometries[7]) ? throw new InvalidOperationException("boom") : IfcExporter.ComputeClosedComponents(geo, preserve, currentProfile));
        for (int i = 0; i < slots.Length; i++)
        {
            if (i == 7) { Assert.Null(slots[i].Result); Assert.Throws<InvalidOperationException>(() => slots[i].Error!.Throw()); }
            else { Assert.Null(slots[i].Error); Assert.NotNull(slots[i].Result); }
        }
    }
}
