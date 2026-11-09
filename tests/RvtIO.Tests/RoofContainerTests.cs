using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

public class RoofContainerTests
{
    private static Element Roof(int id) => new() { Id = id, CategoryId = -2000035 };
    private static Element Panel(int id, int hostId, int category = -2000170) =>
        new() { Id = id, CategoryId = category, SerializedHostId = hostId };

    private static (Dictionary<int, Element> ById, Dictionary<int, int> Counts) Index(params Element[] elements) =>
        (elements.ToDictionary(e => e.Id), elements.ToDictionary(e => e.Id, _ => 1));

    [Fact]
    public void PanelsAndMullionsOfAGeometrylessRoof_AreItsChildren()
    {
        var elements = new[] { Roof(1), Panel(2, 1), Panel(3, 1, -2000171) };
        var (byId, counts) = Index(elements);
        var result = IfcExporter.ResolveRoofContainerChildren(elements, byId, counts);
        Assert.Equal([2, 3], result[1]);
    }

    [Fact]
    public void RoofWithABody_IsNoContainer()
    {
        var roof = Roof(1);
        roof.BoundingBox = [0, 0, 0, 1, 1, 1];
        var elements = new[] { roof, Panel(2, 1) };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveRoofContainerChildren(elements, byId, counts));
    }

    [Fact]
    public void NonCurtainChildrenOrNonRoofHosts_AreIgnored()
    {
        var wall = new Element { Id = 1, CategoryId = -2000011 };
        var elements = new[] { wall, Panel(2, 1), Roof(3), new Element { Id = 4, CategoryId = -2000023, SerializedHostId = 3 } };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveRoofContainerChildren(elements, byId, counts));
    }
    private static Element CurtainWall(int id, int typeId) => new()
        {
            Id = id, CategoryId = -2000011,
            Parameters = [new Parameter { Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" }, StorageType = StorageType.ElementId, Value = typeId }],
        };
    private static Element CurtainWallType(int id, int function) =>
        new() { Id = id, CategoryId = -2000011, SerializedCurtainWallFunction = function };

    [Fact]
    public void MullionWithoutCurtainHostId_IsAChildOfItsPlainHostCurtainWall()
    {
        var elements = new[] { CurtainWall(1, 2), CurtainWallType(2, 1), Panel(3, 1, -2000171) };
        var (byId, counts) = Index(elements);
        Assert.Equal([3], IfcExporter.ResolveExactCurtainChildren(elements, byId, counts)[1]);
    }

    [Fact]
    public void MullionOnABasicWall_OrDoorOnACurtainWallByPlainHost_IsNoChild()
    {
        var basicType = CurtainWallType(2, 1);
        basicType.SerializedBasicWallTypeFunction = 0;
        var elements = new[]
        {
            CurtainWall(1, 2), basicType, Panel(3, 1, -2000171),
            CurtainWall(4, 5), CurtainWallType(5, 1), new Element { Id = 6, CategoryId = -2000023, SerializedHostId = 4 },
        };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveExactCurtainChildren(elements, byId, counts));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoofWithoutBounds_AggregatesItsMullions_InAnyGeometryLayout(bool geometryUndecoded)
    {
        var info = new RevitFileInfo { GeometryUndecoded = geometryUndecoded };
        var roof = Roof(1);
        roof.TypeId = 1001;
        roof.SerializedCreatedPhaseId = 3;
        roof.Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }];
        info.Elements.Add(roof);
        var mullion = Panel(2, 1, -2000171);
        double[] axis = [0, 0, 1], profileX = [1, 0, 0], centre = [0, 0, 5];
        mullion.BoundingBox = IfcExporterBeamGeometryTests.PrismBounds(centre, axis, profileX, 10, 0.1, 0.1);
        mullion.Geometry = IfcExporterBeamGeometryTests.RectangularPrism(centre, axis, profileX, 9.2, 0.1, 0.1);
        mullion.TypeId = 1002;
        mullion.IsDiscovered = true;
        mullion.SerializedCreatedPhaseId = 3;
        mullion.Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }];
        info.Elements.Add(mullion);
        string path = Path.Combine(Path.GetTempPath(), $"roof-container-{Guid.NewGuid():N}.ifc");
        string ifc;
        try
        {
            new IfcExporter().Export(info, path);
            ifc = File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        var roofEntity = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCROOF\("));
        var member = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCMEMBER\("));
        Assert.Matches($@"IFCRELAGGREGATES\('[^']*',#\d+,[^,]*,[^,]*,#{roofEntity.Groups[1].Value},\(#{member.Groups[1].Value}\)\)", ifc);
        Assert.DoesNotMatch($@"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^;]*\(([^)]*,)?#{member.Groups[1].Value}[,)]", ifc);
    }
}
