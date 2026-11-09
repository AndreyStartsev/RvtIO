using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>A building-services element whose level is a Level that is not a project storey is contained in the highest storey at or below that level, and in the building where no storey is that low. An element on a project storey keeps that storey, and an element of another kind on a Level that is not a project storey keeps the storey of its box.</summary>
public class IfcExporterBuildingServicesContainerTests
{
    private const int PhaseId = 3;

    [Fact]
    public void ServicesElementOnNonStoreyLevel_IsContainedInHighestStoreyAtOrBelowIt()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Hung ceiling", elevation: 3.5, buildingStory: false));
        info.Elements.Add(Level(id: 8, name: "Pit", elevation: -1, buildingStory: false));
        info.Elements.Add(Fitting(id: 100, levelId: 7, bottom: 7));    // level 3.5: First, not the storey of the box bottom
        info.Elements.Add(Fitting(id: 101, levelId: 8, bottom: 0));    // below every storey: the building
        info.Elements.Add(Fitting(id: 102, levelId: 3, bottom: 0));    // a project storey: unchanged
        info.Elements.Add(Fitting(id: 103, levelId: 7, bottom: 7, categoryId: -2001100));   // not building services: the storey of its box
        string path = TempIfcPath("services-container");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("IFCBUILDING", ContainerOf(ifc, "101"));
            Assert.Equal("Second", ContainerOf(ifc, "102"));
            Assert.Equal("Second", ContainerOf(ifc, "103"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void NestedServicesElement_TakesTheNonStoreyLevelOfItsTopLevelInstance()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Hung ceiling", elevation: 3.5, buildingStory: false));
        var parent = Fitting(id: 100, levelId: 7, bottom: 7);                                // the top-level instance: First
        parent.Parameters.Add(new Parameter { Id = -1001350, StorageType = StorageType.ElementId, Value = 7 });
        info.Elements.Add(parent);
        var nested = Fitting(id: 101, levelId: 0, bottom: 7);                                // no level of its own: the geometry would give Second
        nested.Parameters = nested.Parameters.Where(parameter => parameter.Id != -1001952).ToList();
        nested.SuperInstanceId = 100;
        nested.NativeMasterSymbolId = 5;
        info.Elements.Add(nested);
        string path = TempIfcPath("services-container-nested");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("First", ContainerOf(ifc, "101"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>The name of the storey, or the class of the building, that contains the product with this Tag.</summary>
    private static string ContainerOf(string ifc, string tag)
    {
        var product = Regex.Match(ifc, @"#(\d+)=IFC[A-Z]+\([^\n]*'" + tag + @"'\);");
        Assert.True(product.Success, "element " + tag + " is not a product");
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

    private static Element Fitting(int id, int levelId, double bottom, int categoryId = -2008049) => new()
    {
        Id = id,
        TypeId = 300,
        CategoryId = categoryId,
        Category = categoryId == -2008049 ? "OST_PipeFitting" : "OST_FurnitureSystems",
        Name = "Fitting",
        References = [PhaseId],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = PhaseId },
            new Parameter { Id = -1001952, StorageType = StorageType.ElementId, Value = levelId },
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
