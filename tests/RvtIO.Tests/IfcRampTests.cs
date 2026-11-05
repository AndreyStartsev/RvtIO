using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcRampTests
{
    // A box on the footprint [x0,x1] x [y0,y1] from z = 0 whose top is a plane rising from z0 at x0 to z1 at x1.
    private static List<GeoFace> Box(double x0, double x1, double y0, double y1, double z0, double z1)
    {
        double[][] p = [[x0, y0, 0], [x1, y0, 0], [x1, y1, 0], [x0, y1, 0], [x0, y0, z0], [x1, y0, z1], [x1, y1, z1], [x0, y1, z0]];
        int[][] f = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return f.Select(ix => Face(ix.Select(i => p[i]).ToList())).ToList();
    }

    private static GeoFace Face(List<double[]> ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            nx += (a[1] - b[1]) * (a[2] + b[2]); ny += (a[2] - b[2]) * (a[0] + b[0]); nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        double area = Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        return new GeoFace { OuterVertices = ring, Normal = [nx / (2 * area), ny / (2 * area), nz / (2 * area)], Area = area };
    }

    private static GeometryData Solids(params List<GeoFace>[] solids)
    {
        var shells = new List<GeoBrepSolid>();
        int next = 0;
        foreach (var solid in solids)
        {
            shells.Add(new GeoBrepSolid { IsClosed = true, FaceIndices = Enumerable.Range(next, solid.Count).ToList() });
            next += solid.Count;
        }
        return new GeometryData { BoundingBox = [0, 0, 0, 10, 2, 3], Faces = solids.SelectMany(s => s).ToList(), BrepSolids = shells };
    }

    // A solid of 9 faces: the 6 of the box and 3 loose triangles.
    private static List<GeoFace> BigSolid()
    {
        var faces = Box(0, 4, 0, 2, 0.5, 2.5);
        for (int i = 0; i < 3; i++) faces.Add(Face([[0, 0, 5 + i], [1, 0, 5 + i], [1, 1, 5 + i]]));
        return faces;
    }

    private static Element Ramp(GeometryData? geometry, string? rootClass = "Ramp") => new()
    {
        Id = 100, TypeId = 500, CategoryId = -2000180, Category = "OST_Ramps", Name = "Ramp", NativeRootClassName = rootClass,
        IsDiscovered = true, References = [3],
        Parameters =
        [
            new Parameter { Id = -1012100, Value = 3, StorageType = StorageType.ElementId },
            new Parameter { Id = -1002050, Value = 500, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
        ],
        BoundingBox = geometry?.BoundingBox, Geometry = geometry,
    };

    [Fact]
    public void Classify_SlopedTopIsFlightAndHorizontalTopIsLanding()
    {
        var parts = IfcExporter.ClassifyRampSolids(Solids(Box(0, 4, 0, 2, 0.5, 2.5), Box(4, 6, 0, 2, 2.5, 2.5)));
        Assert.NotNull(parts);
        Assert.Equal([false, true], parts!.Select(p => p.IsLanding));
        Assert.Equal([0, 1, 2, 3, 4, 5], parts[0].FaceIndices);
        Assert.Equal([6, 7, 8, 9, 10, 11], parts[1].FaceIndices);
    }

    [Fact]
    public void Classify_LargestUpwardFaceDecides()
    {
        // The top of this solid is inclined and large; a small horizontal upward face is added next to it.
        var faces = Box(0, 4, 0, 2, 0.5, 2.5);
        faces.Add(Face([[0, 0, 5], [0.1, 0, 5], [0.1, 0.1, 5], [0, 0.1, 5]]));
        Assert.False(Assert.Single(IfcExporter.ClassifyRampSolids(Solids(faces))!).IsLanding);
    }

    [Fact]
    public void Classify_MoreThanEightFaces_IsNotMadeOfParts()
    {
        var faces = BigSolid();
        Assert.Equal(9, faces.Count);
        Assert.Null(IfcExporter.ClassifyRampSolids(Solids(faces, Box(4, 6, 0, 2, 2.5, 2.5))));
        faces.RemoveAt(8);
        Assert.NotNull(IfcExporter.ClassifyRampSolids(Solids(faces)));
    }

    [Fact]
    public void Classify_NoSolidsOrOpenSolidOrNoUpwardFace_IsNotMadeOfParts()
    {
        Assert.Null(IfcExporter.ClassifyRampSolids(null));
        Assert.Null(IfcExporter.ClassifyRampSolids(new GeometryData { Faces = Box(0, 4, 0, 2, 0, 0) }));
        var open = Solids(Box(0, 4, 0, 2, 0.5, 2.5));
        open.BrepSolids![0].IsClosed = false;
        Assert.Null(IfcExporter.ClassifyRampSolids(open));
        Assert.Null(IfcExporter.ClassifyRampSolids(Solids([Face([[0, 0, 0], [0, 2, 0], [4, 2, 0], [4, 0, 0]])])));
    }

    [Fact]
    public void Layout_FollowsClassRailingsAndSolids()
    {
        var two = Solids(Box(0, 4, 0, 2, 0.5, 2.5), Box(4, 6, 0, 2, 2.5, 2.5));
        var one = Solids(Box(0, 4, 0, 2, 0.5, 2.5));
        Assert.Equal(IfcExporter.RampLayout.Components, IfcExporter.ClassifyRamp(Ramp(two), false));
        Assert.Equal(IfcExporter.RampLayout.Single, IfcExporter.ClassifyRamp(Ramp(one), false));
        Assert.Equal(IfcExporter.RampLayout.Monolithic, IfcExporter.ClassifyRamp(Ramp(one), true));
        Assert.Equal(IfcExporter.RampLayout.Monolithic, IfcExporter.ClassifyRamp(Ramp(Solids(BigSolid())), false));
        Assert.Equal(IfcExporter.RampLayout.Single, IfcExporter.ClassifyRamp(Ramp(Solids(BigSolid()), "FamilyInstance"), false));
        Assert.Equal(IfcExporter.RampLayout.Single, IfcExporter.ClassifyRamp(Ramp(null), false));
    }

    [Fact]
    public void TypeElement_IsTheLinkedRampTypeOrTheOneReferencedRampType()
    {
        var ramp = Ramp(null);
        var rampType = new Element { Id = 7, NativeRootClassName = "RampAttributes" };
        var other = new Element { Id = 500, NativeRootClassName = "ElementType" };
        var byId = new Dictionary<int, Element> { [7] = rampType, [500] = other, [3] = new Element { Id = 3 } };
        Assert.Equal(0, IfcExporter.RampTypeElementId(ramp, byId));
        ramp.References = [3, 7, 7];
        Assert.Equal(7, IfcExporter.RampTypeElementId(ramp, byId));
        byId[500] = new Element { Id = 500, NativeRootClassName = "RampAttributes" };
        Assert.Equal(500, IfcExporter.RampTypeElementId(ramp, byId));
        ramp.References = [7, 500];
        byId[500] = other;
        byId[3] = new Element { Id = 3, NativeRootClassName = "RampAttributes" };
        ramp.References = [3, 7];
        Assert.Equal(0, IfcExporter.RampTypeElementId(ramp, byId));
    }

    [Fact]
    public void Export_MonolithicRamp_IsAContainerAggregatingTheIfcRampWithTheBody()
    {
        string ifc = Export(IfcProfile.Current, Ramp(Solids(BigSolid())));
        var ramps = Entities(ifc, "IFCRAMP");
        Assert.Equal(2, ramps.Count);
        var container = ramps.Single(ramp => Args(ramp)[6] == "$");
        var body = ramps.Single(ramp => Args(ramp)[6] != "$");
        Assert.NotEqual(Args(container)[0], Args(body)[0]);
        foreach (int index in new[] { 2, 4, 7, 8 }) Assert.Equal(Args(container)[index], Args(body)[index]);
        Assert.EndsWith(":100'", Args(container)[2]);
        Assert.Equal("'100'", Args(container)[7]);
        Assert.Equal(".NOTDEFINED.", Args(container)[8]);
        Assert.Contains($"{Args(body)[6]}=IFCPRODUCTDEFINITIONSHAPE(", ifc);
        var relation = Args(Assert.Single(RampRelations(ifc, container)));
        Assert.Equal(Id(container), relation[4]);
        Assert.Equal($"({Id(body)})", relation[5]);
        // The body is placed under the placement of the container.
        var bodyPlacement = Assert.Single(Entities(ifc, "IFCLOCALPLACEMENT"), place => Id(place) == Args(body)[5]);
        Assert.Equal(Args(container)[5], Args(bodyPlacement)[0]);
        // Only the container is in the storey, carries the property sets and has the type.
        string contained = Assert.Single(Entities(ifc, "IFCRELCONTAINEDINSPATIALSTRUCTURE"));
        Assert.Contains(Id(container), contained);
        Assert.DoesNotContain(Id(body), contained);
        var common = Assert.Single(Entities(ifc, "IFCPROPERTYSET"), pset => pset.Contains("'Pset_RampCommon'"));
        var byProperties = Entities(ifc, "IFCRELDEFINESBYPROPERTIES");
        Assert.Contains(byProperties, rel => Args(rel)[4] == $"({Id(container)})" && Args(rel)[5] == Id(common));
        Assert.DoesNotContain(byProperties, rel => Args(rel)[4].Contains(Id(body)));
        var type = Assert.Single(Entities(ifc, "IFCTYPEPRODUCT"));
        var byType = Assert.Single(Entities(ifc, "IFCRELDEFINESBYTYPE"));
        Assert.Equal($"({Id(container)})", Args(byType)[4]);
        Assert.Equal(Id(type), Args(byType)[5]);
        Assert.Empty(Entities(ifc, "IFCBUILDINGELEMENTPROXY"));
    }

    [Fact]
    public void Export_FlightAndLanding_AreAggregatedByTheBodylessRamp()
    {
        string ifc = Export(IfcProfile.Current, Ramp(Solids(Box(0, 4, 0, 2, 0.5, 2.5), Box(4, 6, 0, 2, 2.5, 2.5))));
        var ramp = Assert.Single(Entities(ifc, "IFCRAMP"));
        Assert.Equal("$", Args(ramp)[6]);
        var flight = Assert.Single(Entities(ifc, "IFCRAMPFLIGHT"));
        var landing = Assert.Single(Entities(ifc, "IFCSLAB"));
        Assert.Equal(".LANDING.", Args(landing)[^1]);
        Assert.Equal(8, Args(flight).Length);
        Assert.Empty(Entities(ifc, "IFCBUILDINGELEMENTPROXY"));
        var relationArgs = Args(Assert.Single(RampRelations(ifc, ramp)));
        Assert.Equal(Id(ramp), relationArgs[4]);
        Assert.Equal($"({Id(flight)},{Id(landing)})", relationArgs[5]);
        foreach (var part in new[] { flight, landing })
            Assert.Contains($"{Args(part)[6]}=IFCPRODUCTDEFINITIONSHAPE(", ifc);
        string contained = Assert.Single(Entities(ifc, "IFCRELCONTAINEDINSPATIALSTRUCTURE"));
        Assert.Contains(Id(ramp), contained);
        Assert.DoesNotContain(Id(flight), contained);
        Assert.DoesNotContain(Id(landing), contained);
        Assert.DoesNotContain("Pset_RampFlightCommon", ifc);
    }

    [Fact]
    public void Export_SingleFlight_IsOneIfcRampWithTheBody()
    {
        string ifc = Export(IfcProfile.Current, Ramp(Solids(Box(0, 4, 0, 2, 0.5, 2.5))));
        var ramp = Assert.Single(Entities(ifc, "IFCRAMP"));
        Assert.StartsWith("#", Args(ramp)[6]);
        Assert.Empty(Entities(ifc, "IFCRAMPFLIGHT"));
        Assert.Empty(RampRelations(ifc, ramp));
    }

    [Fact]
    public void Export_RampOfALoadedFamily_IsOneIfcRampWithTheBody()
    {
        string ifc = Export(IfcProfile.Current, Ramp(Solids(BigSolid()), "FamilyInstance"));
        var ramp = Assert.Single(Entities(ifc, "IFCRAMP"));
        Assert.StartsWith("#", Args(ramp)[6]);
        Assert.Empty(RampRelations(ifc, ramp));
    }

    [Fact]
    public void Export_RampWithoutGeometry_WritesNoRamp()
    {
        string ifc = Export(IfcProfile.Current, Ramp(null));
        Assert.Empty(Entities(ifc, "IFCRAMP"));
    }

    [Fact]
    public void Export_LegacyProfile_IsUnchanged()
    {
        string ifc = Export(IfcProfile.Legacy, Ramp(Solids(BigSolid())));
        Assert.Empty(Entities(ifc, "IFCRAMP"));
        Assert.Empty(Entities(ifc, "IFCRAMPFLIGHT"));
    }

    private static List<string> RampRelations(string ifc, string ramp) =>
        Entities(ifc, "IFCRELAGGREGATES").Where(relation => Args(relation)[4] == Id(ramp)).ToList();

    private static string Export(IfcProfile profile, Element ramp)
    {
        var type = new Element { Id = 500, TypeId = -1, CategoryId = -2000180, Name = "Ramp type" };
        var info = new RevitFileInfo(); info.Elements.AddRange([ramp, type]);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-ramp-{Guid.NewGuid():N}.ifc");
        try { new IfcExporter { Profile = profile }.Export(info, path); return File.ReadAllText(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static List<string> Entities(string ifc, string type) => ifc.Split('\n')
        .Where(line => line.StartsWith('#') && line.Contains($"={type}(")).Select(line => line.TrimEnd('\r')).ToList();

    private static string Id(string entity) => entity[..entity.IndexOf('=')];

    private static string[] Args(string entity)
    {
        int open = entity.IndexOf('(');
        return IfcExporter.SplitStepArguments(entity[(open + 1)..entity.LastIndexOf(')')]);
    }
}
