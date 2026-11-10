using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>An element whose level is a Level that is not a project storey is contained in the highest storey at or below that level, and in the building where no storey is that low: a building-services element, and an element of any other kind by the level parameter of its kind. An element on a project storey keeps that storey. The assembly of a beam system stays with its first beam; where that beam is on a level that is not a project storey, the assembly is contained by the bottom of its own outline.</summary>
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
        info.Elements.Add(Fitting(id: 103, levelId: 7, bottom: 7, categoryId: -2001100));   // not building services: First as well
        string path = TempIfcPath("services-container");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("IFCBUILDING", ContainerOf(ifc, "101"));
            Assert.Equal("Second", ContainerOf(ifc, "102"));
            Assert.Equal("First", ContainerOf(ifc, "103"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(-2000011, "OST_Walls", -1001107)]               // a wall by its base constraint
    [InlineData(-2000032, "OST_Floors", -1001952)]              // a floor by its level
    [InlineData(-2000151, "OST_GenericModel", -1001952)]        // a generic model by its level
    [InlineData(-2001320, "OST_StructuralFraming", -1001383)]   // a beam by its reference level
    [InlineData(-2000080, "OST_Furniture", -1002062)]           // a piece of furniture by its schedule level
    public void ElementOfAnotherKindOnNonStoreyLevel_IsContainedInHighestStoreyAtOrBelowIt(
        int categoryId, string category, int levelParameterId)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Rough floor", elevation: 3.5, buildingStory: false));
        info.Elements.Add(Level(id: 8, name: "Pit", elevation: -1, buildingStory: false));
        Element OnLevel(int id, int levelId, double bottom)
        {
            var element = Fitting(id, levelId: 0, bottom, categoryId);
            element.Category = category;
            element.Parameters = element.Parameters.Where(parameter => parameter.Id != -1001952).ToList();
            element.Parameters.Add(new Parameter { Id = levelParameterId, StorageType = StorageType.ElementId, Value = levelId });
            return element;
        }
        info.Elements.Add(OnLevel(id: 100, levelId: 7, bottom: 7));    // level 3.5: First, not the storey of the box bottom
        info.Elements.Add(OnLevel(id: 101, levelId: 8, bottom: 0));    // below every storey: the building
        info.Elements.Add(OnLevel(id: 102, levelId: 3, bottom: 0));    // a project storey: unchanged
        string path = TempIfcPath("any-kind-container");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("IFCBUILDING", ContainerOf(ifc, "101"));
            Assert.Equal("Second", ContainerOf(ifc, "102"));
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

    [Theory]
    [InlineData(-1001952)]   // its own level says Second
    [InlineData(-1002062)]   // its own schedule level says Second
    public void NestedServicesElement_PrefersTheNonStoreyLevelOfItsTopLevelInstanceToItsOwnLevel(int ownLevelParameter)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Hung ceiling", elevation: 3.5, buildingStory: false));
        var parent = Fitting(id: 100, levelId: 7, bottom: 7);                                // the top-level instance: First
        parent.Parameters.Add(new Parameter { Id = -1001350, StorageType = StorageType.ElementId, Value = 7 });
        info.Elements.Add(parent);
        info.Elements.Add(NestedFitting(id: 101, parentId: 100, ownLevelParameter, ownLevelId: 3));
        string path = TempIfcPath("services-container-nested-own-level");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("First", ContainerOf(ifc, "101"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void NestedServicesElement_KeepsTheStoreyOfItsTopLevelInstanceAgainstItsOwnLevel()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        var parent = Fitting(id: 100, levelId: 2, bottom: 7);                                // the top-level instance: First
        parent.Parameters.Add(new Parameter { Id = -1001350, StorageType = StorageType.ElementId, Value = 2 });
        info.Elements.Add(parent);
        info.Elements.Add(NestedFitting(id: 101, parentId: 100, ownLevelParameter: -1001952, ownLevelId: 3));
        string path = TempIfcPath("services-container-nested-parent-storey");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "100"));
            Assert.Equal("First", ContainerOf(ifc, "101"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ServicesFamilyInstance_PrefersItsNonStoreyBaseLevelToItsOwnLevel()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Hung ceiling", elevation: 3.5, buildingStory: false));
        var instance = Fitting(id: 100, levelId: 3, bottom: 7);                              // not nested; its own level says Second
        instance.Parameters.Add(new Parameter { Id = -1001350, StorageType = StorageType.ElementId, Value = 7 });
        instance.NativeMasterSymbolId = 5;
        info.Elements.Add(instance);
        string path = TempIfcPath("services-container-own-base-level");

        try
        {
            new IfcExporter().Export(info, path);

            Assert.Equal("First", ContainerOf(File.ReadAllText(path), "100"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BeamSystemAssembly_WhoseFirstBeamIsOnANonStoreyLevel_IsContainedByTheBottomOfItsOwnOutline()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        info.Elements.Add(Level(id: 7, name: "Rafter line", elevation: 6.5, buildingStory: false));
        Element Beam(int id) => SystemBeam(id, levelId: 7, bottom: 7);                        // level 6.5: Second
        info.Elements.AddRange([Beam(100), Beam(101), Beam(110), Beam(111)]);
        info.Elements.Add(BeamSystem(id: 200, firstBeamId: 100, outline: [0, 0, 4, 1, 1, 10]));   // its outline starts in First
        info.Elements.Add(BeamSystem(id: 201, firstBeamId: 110, outline: null));                  // no outline: with its first beam
        string path = TempIfcPath("beam-system-container");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal("First", ContainerOf(ifc, "200"));
            Assert.Equal("Second", ContainerOf(ifc, "201"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(2)]   // the beams are on First and hang below it
    [InlineData(0)]   // the beams have no level: the bottom of the first one is in First
    public void BeamSystemAssembly_WhoseFirstBeamIsNotOnANonStoreyLevel_StaysWithItsFirstBeam(int beamLevelId)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level(id: 1, name: "Ground", elevation: 0, buildingStory: true));
        info.Elements.Add(Level(id: 2, name: "First", elevation: 3, buildingStory: true));
        info.Elements.Add(Level(id: 3, name: "Second", elevation: 6, buildingStory: true));
        double firstBottom = beamLevelId == 0 ? 3.5 : 2;
        info.Elements.AddRange([SystemBeam(100, beamLevelId, firstBottom), SystemBeam(101, beamLevelId, bottom: 2)]);
        info.Elements.Add(BeamSystem(id: 200, firstBeamId: 100, outline: [0, 0, 2, 1, 1, 6.5]));   // its outline starts in Ground
        string path = TempIfcPath("beam-system-first-beam");

        try
        {
            new IfcExporter().Export(info, path);

            Assert.Equal("First", ContainerOf(File.ReadAllText(path), "200"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>A beam whose only level parameter is its reference level (none for level 0).</summary>
    private static Element SystemBeam(int id, int levelId, double bottom)
    {
        var beam = Fitting(id, levelId: 0, bottom, categoryId: -2001320);
        beam.Category = "OST_StructuralFraming";
        beam.Parameters = beam.Parameters.Where(parameter => parameter.Id != -1001952).ToList();
        if (levelId != 0) beam.Parameters.Add(new Parameter { Id = -1001383, StorageType = StorageType.ElementId, Value = levelId });
        return beam;
    }

    private static Element BeamSystem(int id, int firstBeamId, double[]? outline) => new()
    {
        Id = id, CategoryId = -2001327, Category = "OST_StructuralFramingSystem", BoundingBox = outline,
        JoistSystem = new RvtIO.Parsing.JoistSystemData(1, "Joist type", [firstBeamId, firstBeamId + 1]),
    };

    /// <summary>A nested fitting whose only level parameter is the given one.</summary>
    private static Element NestedFitting(int id, int parentId, int ownLevelParameter, int ownLevelId)
    {
        var nested = Fitting(id, levelId: 0, bottom: 7);
        nested.Parameters = nested.Parameters.Where(parameter => parameter.Id != -1001952).ToList();
        nested.Parameters.Add(new Parameter { Id = ownLevelParameter, StorageType = StorageType.ElementId, Value = ownLevelId });
        nested.SuperInstanceId = parentId;
        nested.NativeMasterSymbolId = 5;
        return nested;
    }

    /// <summary>The name of the storey, or the class of the building, that contains the product with this Tag.</summary>
    private static string ContainerOf(string ifc, string tag)
    {
        var product = Regex.Match(ifc, @"#(\d+)=IFC[A-Z]+\([^\n]*'" + tag + @"'(,[^,\n]*){0,2}\);");
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
