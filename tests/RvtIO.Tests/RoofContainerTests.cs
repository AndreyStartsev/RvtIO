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

    [Theory]
    [InlineData(-2000170)]
    [InlineData(-2000171)]
    public void PanelOrMullionWithoutCurtainHostId_IsAChildOfItsPlainHostCurtainWall(int category)
    {
        var elements = new[] { CurtainWall(1, 2), CurtainWallType(2, 1), Panel(3, 1, category) };
        var (byId, counts) = Index(elements);
        Assert.Equal([3], IfcExporter.ResolveExactCurtainChildren(elements, byId, counts)[1]);
    }

    [Fact]
    public void CurtainHostId_WinsOverThePlainHostId()
    {
        var mullion = Panel(5, 4, -2000171);
        mullion.SerializedCurtainHostId = 1;
        var elements = new[] { CurtainWall(1, 2), CurtainWallType(2, 1), CurtainWall(4, 2), mullion };
        var (byId, counts) = Index(elements);
        var result = IfcExporter.ResolveExactCurtainChildren(elements, byId, counts);
        Assert.Equal([5], result[1]);
        Assert.False(result.ContainsKey(4));
    }

    [Fact]
    public void MullionByPlainHost_OnABasicWallType_IsNoChild()
    {
        var basicType = CurtainWallType(2, 1);
        basicType.SerializedBasicWallTypeFunction = 0;
        var elements = new[] { CurtainWall(1, 2), basicType, Panel(3, 1, -2000171) };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveExactCurtainChildren(elements, byId, counts));
    }

    [Fact]
    public void MullionByPlainHost_OnAStackedWallType_IsNoChild()
    {
        var stackedType = CurtainWallType(2, 1);
        stackedType.IsSerializedStackedWallType = true;
        var elements = new[] { CurtainWall(1, 2), stackedType, Panel(3, 1, -2000171) };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveExactCurtainChildren(elements, byId, counts));
    }

    [Fact]
    public void MullionByPlainHost_OnAWallWithAnIfcOverride_IsNoChild()
    {
        var wall = CurtainWall(1, 2);
        wall.Parameters.Add(new Parameter { Id = -1019012, StorageType = StorageType.String, Value = "IfcWall" });
        var elements = new[] { wall, CurtainWallType(2, 1), Panel(3, 1, -2000171) };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveExactCurtainChildren(elements, byId, counts));
    }

    [Fact]
    public void MullionByPlainHost_WhoseIdOccursTwice_IsNoChild()
    {
        var elements = new[] { CurtainWall(1, 2), CurtainWallType(2, 1), Panel(3, 1, -2000171) };
        var (byId, counts) = Index(elements);
        counts[3] = 2;
        Assert.Empty(IfcExporter.ResolveExactCurtainChildren(elements, byId, counts));
    }

    [Fact]
    public void DoorByPlainHost_OnACurtainWall_IsNoChild()
    {
        var elements = new[] { CurtainWall(1, 2), CurtainWallType(2, 1), new Element { Id = 3, CategoryId = -2000023, SerializedHostId = 1 } };
        var (byId, counts) = Index(elements);
        Assert.Empty(IfcExporter.ResolveExactCurtainChildren(elements, byId, counts));
    }

    private const int RoofTypeId = 1001, MullionTypeId = 1002;

    private static Element Mullion(int id, int hostId, bool withGeometry)
    {
        var mullion = Panel(id, hostId, -2000171);
        mullion.TypeId = MullionTypeId;
        mullion.SerializedCreatedPhaseId = 3;
        mullion.Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }];
        if (!withGeometry) return mullion;
        double[] axis = [0, 0, 1], profileX = [1, 0, 0], centre = [0, 0, 5];
        mullion.BoundingBox = IfcExporterBeamGeometryTests.PrismBounds(centre, axis, profileX, 10, 0.1, 0.1);
        mullion.Geometry = IfcExporterBeamGeometryTests.RectangularPrism(centre, axis, profileX, 9.2, 0.1, 0.1);
        mullion.IsDiscovered = true;
        return mullion;
    }

    private static string ExportIfc(bool geometryUndecoded, params Element[] elements)
    {
        var info = new RevitFileInfo { GeometryUndecoded = geometryUndecoded };
        info.Elements.Add(new Element { Id = RoofTypeId, CategoryId = -2000035 });
        info.Elements.Add(new Element { Id = MullionTypeId, CategoryId = -2000171 });
        foreach (var element in elements) info.Elements.Add(element);
        string path = Path.Combine(Path.GetTempPath(), $"roof-container-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter().Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Element PhasedRoof()
    {
        var roof = Roof(1);
        roof.TypeId = RoofTypeId;
        roof.SerializedCreatedPhaseId = 3;
        roof.Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }];
        return roof;
    }

    private static void AssertRoofAggregatesMember(string ifc)
    {
        var roofEntity = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCROOF\("));
        var member = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCMEMBER\("));
        Assert.Matches($@"IFCRELAGGREGATES\('[^']*',#\d+,[^,]*,[^,]*,#{roofEntity.Groups[1].Value},\(#{member.Groups[1].Value}\)\)", ifc);
        Assert.DoesNotMatch($@"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^;]*\(([^)]*,)?#{member.Groups[1].Value}[,)]", ifc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoofWithoutBounds_AggregatesItsMullions_InAnyGeometryLayout(bool geometryUndecoded) =>
        AssertRoofAggregatesMember(ExportIfc(geometryUndecoded, PhasedRoof(), Mullion(2, 1, withGeometry: true)));

    [Fact]
    public void RoofWithoutBounds_AggregatesItsMullionsWithoutGeometry_InAFileWithUndecodedGeometry() =>
        AssertRoofAggregatesMember(ExportIfc(true, PhasedRoof(), Mullion(2, 1, withGeometry: false)));

    [Fact]
    public void RoofWithoutBounds_WhoseMullionsAreAllSkipped_IsStillWrittenAsARoof_InAFileWithUndecodedGeometry()
    {
        // A mullion that names no project phase is not exported, so there is nothing for the roof to aggregate.
        var mullion = Mullion(2, 1, withGeometry: false);
        mullion.SerializedCreatedPhaseId = null;
        mullion.Parameters = [];
        string ifc = ExportIfc(true, PhasedRoof(), mullion);
        var roofEntity = Assert.Single(Regex.Matches(ifc, @"#(\d+)=IFCROOF\("));
        Assert.DoesNotContain("IFCMEMBER(", ifc);
        Assert.DoesNotMatch($@"IFCRELAGGREGATES\('[^']*',#\d+,[^,]*,[^,]*,#{roofEntity.Groups[1].Value},", ifc);
    }
}
