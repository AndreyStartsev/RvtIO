using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The layers a host object takes from its public layer list when its own type is not bound exactly: layers of zero
/// thickness are left out of the layer set, but the first layer of the compound structure still gives the surface its material.</summary>
public class IfcLegacyLayerSetTests
{
    private const int Red = 0x0000FF, Blue = 0xFF0000;

    private static Element Material(int id, string name, int color) => new()
    {
        Id = id, TypeId = -1, IsBlockOnly = true, CategoryId = -2000700, Category = "OST_Materials", Name = name,
        SerializedMaterialName = name, HasShadingAppearance = true,
        Parameters = [new Parameter { Id = id * 10, Definition = new Definition { Name = "MATERIAL_PARAM_COLOR" }, Value = color, StorageType = StorageType.Integer }],
    };

    private static Element Wall(params (int Material, double ThicknessMm)[] layers)
    {
        double[][] points = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2]];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new Element
        {
            Id = 100, TypeId = 500, CategoryId = -2000011, Category = "OST_Walls", Name = "Wall", IsDiscovered = true,
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
            Layers = layers.Select((layer, index) => new MaterialLayer { LayerIndex = index, MaterialId = layer.Material, ThicknessMm = layer.ThicknessMm }).ToList(),
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Geometry = new GeometryData
            {
                BoundingBox = [0, 0, 0, 4, 3, 2],
                Faces = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(index => points[index]).ToList() }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
        };
    }

    private static string Export(IfcProfile profile, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-legacy-layers-{Guid.NewGuid():N}.ifc");
        try { new IfcExporter { Profile = profile }.Export(info, path); return File.ReadAllText(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static List<string> Entities(string ifc, string type) => Regex.Matches(ifc, $@"^#\d+={type}\((.*)\);\s*$", RegexOptions.Multiline)
        .Select(match => match.Value.TrimEnd()).ToList();

    private static string Id(string entity) => entity[..entity.IndexOf('=')];

    private static string[] Args(string entity) => IfcExporter.SplitStepArguments(entity[(entity.IndexOf('(') + 1)..entity.LastIndexOf(')')]);

    [Fact]
    public void LayerOfZeroThickness_IsLeftOutOfTheLayerSet()
    {
        string ifc = Export(IfcProfile.Legacy, Wall((700, 0), (701, 75)), Material(700, "Red membrane", Red), Material(701, "Blue structure", Blue));
        var layer = Assert.Single(Entities(ifc, "IFCMATERIALLAYER"));
        Assert.Contains("IFCMATERIAL('Blue structure')", ifc);
        Assert.Equal(75, double.Parse(Args(layer)[1], System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Single(Entities(ifc, "IFCMATERIALLAYERSET"));
    }

    [Fact]
    public void LayersOfZeroThicknessOnly_WriteNoLayerSetAndNoUsage()
    {
        string ifc = Export(IfcProfile.Legacy, Wall((700, 0)), Material(700, "Red membrane", Red));
        Assert.Contains("IFCWALL", ifc);
        Assert.Empty(Entities(ifc, "IFCMATERIALLAYER"));
        Assert.Empty(Entities(ifc, "IFCMATERIALLAYERSET"));
        Assert.Empty(Entities(ifc, "IFCMATERIALLAYERSETUSAGE"));
        Assert.DoesNotContain("IFCMATERIALLAYERSET((", ifc);
    }

    [Fact]
    public void SurfaceStyle_IsThatOfTheFirstLayerWhateverItsThickness()
    {
        string ifc = Export(IfcProfile.Current, Wall((700, 0), (701, 75)), Material(700, "Red membrane", Red), Material(701, "Blue structure", Blue));
        Assert.Equal("'Red membrane'", BodySurfaceStyleName(ifc));
        // The layers of the layer set still leave out the layer of zero thickness.
        Assert.Single(Entities(ifc, "IFCMATERIALLAYER"));
    }

    [Fact]
    public void LayersOfZeroThicknessOnly_KeepTheSurfaceStyleOfTheFirstLayer()
    {
        string ifc = Export(IfcProfile.Current, Wall((700, 0)), Material(700, "Red membrane", Red));
        Assert.Empty(Entities(ifc, "IFCMATERIALLAYERSET"));
        Assert.Empty(Entities(ifc, "IFCMATERIALLAYERSETUSAGE"));
        Assert.Equal("'Red membrane'", BodySurfaceStyleName(ifc));
    }

    private static string BodySurfaceStyleName(string ifc)
    {
        var bodyStyle = Assert.Single(Entities(ifc, "IFCSTYLEDITEM"), item => Args(item)[0] != "$");
        var assignment = Entities(ifc, "IFCPRESENTATIONSTYLEASSIGNMENT").Single(entity => Id(entity) == Args(bodyStyle)[1].Trim('(', ')'));
        var style = Entities(ifc, "IFCSURFACESTYLE").Single(entity => Id(entity) == Args(assignment)[0].Trim('(', ')'));
        return Args(style)[0];
    }
}
