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

    [Fact]
    public void WallWithoutASolidAndWithoutBounds_InAFileWithUndecodedGeometry_IsNotExported() =>
        Assert.Equal(0, Walls(Export(true, Wall(100, solid: false, box: false))));

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
