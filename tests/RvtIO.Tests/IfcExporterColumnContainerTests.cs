using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>A column whose base level is a Level that is not a project storey is contained in the highest storey at or below that level, and in the building where no storey is that low. A column on a project storey keeps that storey.</summary>
public class IfcExporterColumnContainerTests
{
    private const int PhaseId = 3;

    [Fact]
    public void ColumnOnNonStoreyLevel_IsContainedInHighestStoreyAtOrBelowIt()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Rough floor", elevation: 3.5, buildingStory: false));
        info.Elements.Add(Level(id: 8, name: "Pit", elevation: -1, buildingStory: false));
        info.Elements.Add(Level(id: 9, name: "Twin", elevation: 6, buildingStory: true));   // a second storey at the height of "Second"
        info.Elements.Add(Column(id: 100, baseLevelId: 7, bottom: 7));   // level 3.5: First, not the storey of the box bottom
        info.Elements.Add(Column(id: 101, baseLevelId: 8, bottom: 0));   // below every storey: the building
        info.Elements.Add(Column(id: 102, baseLevelId: 3, bottom: 0));   // a project storey: unchanged
        info.Elements.Add(Column(id: 104, baseLevelId: 3, bottom: 0));   // the storey named by the base level wins over a storey of the same height with a larger id
        info.Elements.Add(Column(id: 103, baseLevelId: 7, bottom: 7, levelParameterId: 3));   // a stored level reference does not override the base level
        string path = TempIfcPath("column-container");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("IFCBUILDING", ContainerOf(ifc, "101"));
            Assert.Equal("Second", ContainerOf(ifc, "102"));
            Assert.Equal("First", ContainerOf(ifc, "103"));
            Assert.Equal("Second", ContainerOf(ifc, "104"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>The name of the storey, or the class of the building, that contains the product with this Tag.</summary>
    private static string ContainerOf(string ifc, string tag)
    {
        var product = Regex.Match(ifc, @"#(\d+)=IFCCOLUMN\([^\n]*'" + tag + @"'\);");
        Assert.True(product.Success, "column " + tag + " is not a product");
        var rel = Regex.Matches(ifc, @"=IFCRELCONTAINEDINSPATIALSTRUCTURE\([^\n]*\(([^)]*)\),#(\d+)\)")
            .Single(match => Regex.IsMatch(match.Groups[1].Value, @"(^|,)#" + product.Groups[1].Value + @"(,|$)"));
        var container = Regex.Match(ifc, @"#" + rel.Groups[2].Value + @"=(IFC[A-Z]+)\([^,]*,[^,]*,'([^']*)'");
        return container.Groups[1].Value == "IFCBUILDINGSTOREY" ? container.Groups[2].Value : container.Groups[1].Value;
    }

    private static Element Level(int id, string name, double elevation, bool buildingStory) =>
        new()
        {
            Id = id,
            TypeId = id + 1000,
            CategoryId = -2000240,
            Category = "OST_Levels",
            Name = name,
            IsNativeLevel = true,
            SerializedFamilyId = -1,
            SerializedUnplacedOwnerId = -1,
            SerializedOwnerDbViewId = -1,
            Parameters =
            [
                new Parameter
                {
                    Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = elevation,
                },
                new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = buildingStory ? 1 : 0 },
            ],
        };

    private static Element Column(int id, int baseLevelId, double bottom, int? levelParameterId = null) => new()
    {
        Id = id,
        TypeId = 300,
        CategoryId = -2001330,
        Category = "OST_StructuralColumns",
        Name = "Column",
        References = [PhaseId],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = PhaseId },
            new Parameter { Id = -1001350, StorageType = StorageType.ElementId, Value = baseLevelId },
            .. levelParameterId is int levelId
                ? [new Parameter { Id = -1001952, StorageType = StorageType.ElementId, Value = levelId }]
                : Array.Empty<Parameter>(),
        ],
        BoundingBox = [0, 0, bottom, 1, 1, bottom + 3],
        Geometry = Body(bottom),
    };

    private static GeometryData Body(double z)
    {
        double[][] points =
        [
            [0, 0, z], [1, 0, z], [1, 1, z], [0, 1, z],
            [0, 0, z + 3], [1, 0, z + 3], [1, 1, z + 3], [0, 1, z + 3],
        ];
        int[][] faces =
        [
            [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7], [0, 3, 2, 1], [4, 5, 6, 7],
        ];
        return new GeometryData
        {
            BoundingBox = [0, 0, z, 1, 1, z + 3],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");
}
