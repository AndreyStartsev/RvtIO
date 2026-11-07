using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class WallWithoutSolidAndTypeElementTests
{
    private static readonly double[][] Points = [[0,0,0],[4,0,0],[4,3,0],[0,3,0],[0,0,2],[4,0,2],[4,3,2],[0,3,2]];
    private static readonly int[][] Quads = [[0,3,2,1],[4,5,6,7],[0,1,5,4],[1,2,6,5],[2,3,7,6],[3,0,4,7]];

    private static Element Wall(int id, bool solid, int typeId = 0, bool box = true, bool phased = true)
    {
        var wall = new Element { Id = id, CategoryId = -2000011, TypeId = 900, References = phased ? [3] : null, SerializedCreatedPhaseId = phased ? 3 : null, Category = "OST_Walls", IsDiscovered = true,
            Name = $"Wall {id}", BoundingBox = box ? [0,0,0,4,3,2] : null, SerializedSystemTypeId = typeId > 0 ? typeId : null,
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }] };
        if (typeId > 0) wall.Parameters.Add(new Parameter { Id = -1002050, StorageType = StorageType.ElementId, Value = typeId });
        wall.Geometry = new GeometryData { BoundingBox = box ? wall.BoundingBox : null,
            Faces = solid ? Quads.Select(q => new GeoFace { OuterVertices = q.Select(i => Points[i]).ToList() }).ToList() : [],
            BrepSolids = solid ? [new GeoBrepSolid { IsClosed = true, FaceIndices = [0,1,2,3,4,5] }] : [] };
        return wall;
    }

    private static string Export(params Element[] elements) => Export(false, elements);

    private static string Export(bool undecoded, params Element[] elements) => Export(IfcProfile.Current, undecoded, elements);

    private static string Export(IfcProfile profile, bool undecoded, params Element[] elements)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rr-wall-solid-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = profile }.Export(new RevitFileInfo { Elements = [.. elements], GeometryUndecoded = undecoded }, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static int Walls(string ifc) => Regex.Matches(ifc, @"#\d+=IFCWALL(?:STANDARDCASE)?\(").Count;

    [Fact]
    public void WallWithASolid_IsExported() => Assert.Equal(1, Walls(Export(Wall(100, solid: true))));

    // The filter is a finding only where the geometry of the file was decoded: it holds a body somewhere.
    [Fact]
    public void WallWithoutASolidAndWithoutBounds_InAFileWithDecodedGeometry_IsNotExported() =>
        Assert.Equal(1, Walls(Export(true, Wall(100, solid: false, box: false), Wall(101, solid: true, box: false))));

    [Fact]
    public void WallWithoutASolidAndWithoutBounds_InAFileWhoseGeometryWasNotDecodedAtAll_IsExported() =>
        Assert.Equal(2, Walls(Export(true, Wall(100, solid: false, box: false), Wall(101, solid: false, box: false))));

    /// <summary>A wall as a file read without the R22 layout gives it: the outline of its header and no geometry at all.</summary>
    private static Element OutlineOnlyWall(int id)
    {
        var wall = Wall(id, solid: false, box: false);
        wall.BoundingBox = [0, 0, 0, 4, 3, 2];
        wall.BoundingBoxIsHeaderOutline = true;
        wall.Geometry = null;
        return wall;
    }

    private static string DataOf(string ifc) => ifc[ifc.IndexOf("DATA;", StringComparison.Ordinal)..];

    [Fact]
    public void TheHeaderOutlineOfTheElementsOfAFileWithoutDecodedGeometry_DoesNotChangeTheExport()
    {
        // The products of such a file are written without a representation, with or without the box of the header on their elements.
        string withoutOutline = DataOf(Export(true, Wall(100, solid: false, box: false), Wall(101, solid: false, box: false)));
        string withOutline = DataOf(Export(true, OutlineOnlyWall(100), OutlineOnlyWall(101)));
        Assert.Equal(2, Walls(withOutline));
        Assert.Equal(withoutOutline, withOutline);
    }

    [Fact]
    public void TheHeaderOutlinesAreStillOnTheElementsAfterTheExport()
    {
        var walls = new[] { OutlineOnlyWall(100), OutlineOnlyWall(101) };
        Export(true, walls);
        Assert.All(walls, wall => Assert.Equal([0, 0, 0, 4, 3, 2], wall.BoundingBox));
    }

    [SkippableFact]
    public void WallsOfAFileReadWithoutTheR22Layout_AreExported()
    {
        // A 64-bit-id file read by the default reader decodes no geometry: no element has a body, so a wall without one is the rule.
        var info = new RvtFileReader().Read(TestFiles.FindRelative(Path.Combine("2025", "Test_2025.rvt")));
        Assert.True(info.GeometryUndecoded);
        Assert.DoesNotContain(info.Elements, element => element.Geometry is { } geometry && (geometry.BrepSolids is { Count: > 0 } || geometry.Faces is { Count: > 0 }));
        string path = Path.Combine(Path.GetTempPath(), $"rr-wall-undecoded-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter(TextWriter.Null) { Profile = IfcProfile.Current }.Export(info, path);
            string exported = File.ReadAllText(path);
            Assert.True(Walls(exported) > 0);

            // The elements carry the outline of their header: the export is the one of the same elements without a box (a read that gave none).
            var outlines = info.Elements.Where(element => element.BoundingBox != null).ToList();
            Assert.NotEmpty(outlines);
            var boxes = outlines.Select(element => element.BoundingBox).ToList();
            foreach (var element in outlines) element.BoundingBox = null;
            new IfcExporter(TextWriter.Null) { Profile = IfcProfile.Current }.Export(info, path);
            Assert.Equal(DataOf(File.ReadAllText(path)), DataOf(exported));
            for (int i = 0; i < outlines.Count; i++) outlines[i].BoundingBox = boxes[i];
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WallWithASolidAndWithoutBounds_InAFileWithUndecodedGeometry_IsExported() =>
        Assert.Equal(1, Walls(Export(true, Wall(100, solid: true, box: false))));

    [Fact]
    public void WallWithFacesButNoSolid_InAFileWithUndecodedGeometry_IsExported()
    {
        var wall = Wall(100, solid: true, box: false);
        wall.Geometry!.BrepSolids = [];
        Assert.Equal(1, Walls(Export(true, wall)));
    }

    [Fact]
    public void WallTypeElement_IsNotExportedAsAWall()
    {
        // The type record is referenced by the system type parameter of the instance, names no phase and carries geometry of its own.
        Assert.Equal(1, Walls(Export(Wall(100, solid: true, typeId: 500), Wall(500, solid: true, phased: false))));
    }

    [Fact]
    public void LegacyProfile_WallTypeElement_IsStillExportedAsAWall() =>
        Assert.Equal(2, Walls(Export(IfcProfile.Legacy, false, Wall(100, solid: true, typeId: 500), Wall(500, solid: true, phased: false))));

    [Fact]
    public void LegacyProfile_WallWithoutASolid_InAFileWithUndecodedGeometry_IsStillExported() =>
        Assert.Equal(1, Walls(Export(IfcProfile.Legacy, true, Wall(100, solid: false, box: false))));
}
