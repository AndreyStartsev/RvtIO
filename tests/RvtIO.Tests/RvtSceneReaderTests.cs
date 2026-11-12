using System.Globalization;
using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The scene of <see cref="RvtSceneReader"/> on synthetic meshes and elements: instancing, mirrored placements, local
/// origins, feet to metres, element facts, IFC GlobalIds, property sets and their display text, materials and the diagnostics of
/// incomplete geometry.</summary>
public class RvtSceneReaderTests
{
    private const double Foot = 0.3048;
    private const int Wall = 100, Door = 101, Empty = 102, Broken = 103, Type = 200, Symbol = 300, Concrete = 500;

    private static Parameter Bip(BuiltInParameter bip, object value, StorageType? storage = null) => new()
    {
        Id = (int)bip, Value = value, StorageType = storage ?? StorageOf(value),
        Definition = new Definition { Id = (int)bip, BuiltInParameter = bip.ToString(), Name = bip.ToString() },
    };

    private static Parameter User(int id, string name, object value, StorageType? storage = null) => new()
    {
        Id = id, Value = value, StorageType = storage ?? StorageOf(value), Definition = new Definition { Id = id, Name = name },
    };

    private static StorageType StorageOf(object value) => value switch
    {
        double => StorageType.Double, string => StorageType.String, _ => StorageType.Integer,
    };

    /// <summary>A length in feet, displayed in millimetres as the reader resolves a length parameter.</summary>
    private static Parameter Length(BuiltInParameter bip, double feet)
    {
        var parameter = Bip(bip, feet);
        parameter.UnitSymbol = "mm";
        parameter.ConversionFactor = 304.8;
        return parameter;
    }

    private static Element Instance(int id, int categoryId, string category, params Parameter[] parameters) => new()
    {
        Id = id, UniqueId = $"doc-{id:x8}", CategoryId = categoryId, Category = category,
        Parameters = [Bip(BuiltInParameter.ELEM_TYPE_PARAM, Type, StorageType.ElementId), .. parameters],
    };

    private static RvtDocument Document() => RvtDocument.FromInfo(new RevitFileInfo
    {
        Elements =
        [
            Instance(Wall, (int)BuiltInCategory.OST_Walls, "OST_Walls", Length(BuiltInParameter.WALL_USER_HEIGHT_PARAM, 10.5),
                User(900, "Fire rating", "EI 60")),
            Instance(Door, (int)BuiltInCategory.OST_Doors, "OST_Doors"),
            Instance(Empty, (int)BuiltInCategory.OST_Walls, "OST_Walls"),
            Instance(Broken, (int)BuiltInCategory.OST_Walls, "OST_Walls"),
            new Element { Id = Type, Name = "Generic 200", Parameters = [User(901, "Count", 3)] },
            new Element
            {
                Id = Concrete, Name = "Concrete", CategoryId = (int)BuiltInCategory.OST_Materials, Category = "OST_Materials",
                Parameters = [Bip(BuiltInParameter.MATERIAL_PARAM_COLOR, 0x30_20_10), Bip(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY, 20)],
            },
        ],
    });

    /// <summary>One triangle at (x, y, z) + (0..1, 0..2, 0), normal +Z.</summary>
    private static TriangleMesh Triangle(double x, double y, double z, int material = 0) => new()
    {
        Positions = [x, y, z, x + 1, y, z, x, y + 2, z],
        Indices = [0, 1, 2],
        Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1],
        MaterialId = material,
    };

    private static readonly MeshTransform Mirrored = new(new(10, 20, 0), new(-1, 0, 0), new(0, 1, 0), new(0, 0, 1));
    private static readonly MeshTransform Moved = new(new(0, 4, 0), new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1));

    private static MeshSet Meshes() => new()
    {
        Symbols = new Dictionary<int, IReadOnlyList<TriangleMesh>> { [Symbol] = [Triangle(0, 0, 0, Concrete)] },
        Elements =
        [
            new ElementMesh { ElementId = Wall, Meshes = [Triangle(1000, 2000, 30), Triangle(1001, 2000, 31, Concrete)], Coverage = MeshCoverage.Complete },
            new ElementMesh { ElementId = Door, Placements = [new(Symbol, Mirrored), new(Symbol, Moved)], Coverage = MeshCoverage.Complete },
            new ElementMesh { ElementId = Empty, Coverage = MeshCoverage.NoGeometry },
            new ElementMesh { ElementId = Broken, Meshes = [Triangle(0, 0, 0)], Coverage = MeshCoverage.Partial, UntriangulatedFaces = 2 },
        ],
    };

    private static RvtSceneResult Map() => RvtSceneReader.Read(Document(), Meshes(), new RvtSceneOptions { ModelKey = "model.rvt" });

    /// <summary>The scene of one drawable element.</summary>
    private static SceneDocument MapOne(Element element) => RvtSceneReader.Read(RvtDocument.FromInfo(new RevitFileInfo { Elements = [element] }),
        new MeshSet { Elements = [new ElementMesh { ElementId = element.Id, Meshes = [Triangle(0, 0, 0)] }] }, new RvtSceneOptions { ModelKey = "m" }).Document;

    /// <summary>The text of each property of the scene, by property name.</summary>
    private static Dictionary<string, string> Texts(SceneDocument document) =>
        document.PropertySets.SelectMany(set => set.Properties).ToDictionary(property => property.Name, property => property.Value.Text);

    private static void AssertClose(Vector3d expected, Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Z, actual.Z, 9);
    }

    [Fact]
    public void ScenePassesValidation()
    {
        var result = Map();
        Assert.Empty(result.Document.Validate());
        Assert.Empty(SceneJson.Read(SceneJson.Write(result.Document)).Validate());
    }

    [Fact]
    public void ModelCarriesKeyFormatUnitAndProducer()
    {
        var model = Assert.Single(Map().Document.Models);
        Assert.Equal("model.rvt", model.Key);
        Assert.Equal("rvt", model.SourceFormat);
        Assert.Equal("ft", model.SourceLengthUnit);
        Assert.NotNull(model.Producer);
        Assert.StartsWith("RvtIO.Scene ", model.Producer);
        Assert.DoesNotContain('+', model.Producer);
    }

    [Fact]
    public void SymbolIsOneGeometryPlacedOncePerPlacement()
    {
        var result = Map();
        var door = result.Document.Instances.Where(instance => instance.ElementId == Door).ToList();
        Assert.Equal(2, door.Count);
        Assert.Single(door.Select(instance => instance.GeometryId).Distinct());
        Assert.Equal(2, result.PlacementCount);
        // the symbol (1) and the three own meshes (two of the wall, one of the broken element)
        Assert.Equal(4, result.GeometryCount);
        Assert.Equal(5, result.InstanceCount);
        Assert.All(door, instance => Assert.Equal(Concrete, instance.MaterialId));
    }

    [Fact]
    public void MirroredPlacementKeepsItsTransformAndTheWinding()
    {
        var document = Map().Document;
        var mirrored = document.Instances.First(instance => instance.ElementId == Door);
        Assert.Equal(new Transform3d(new(-1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(10 * Foot, 20 * Foot, 0)), mirrored.Transform);
        var symbol = document.Geometries.Single(geometry => geometry.Id == mirrored.GeometryId);
        Assert.Equal([0, 1, 2], symbol.Indices);
        Assert.Equal([new(0, 0, 0), new(Foot, 0, 0), new(0, 2 * Foot, 0)], symbol.Vertices);
        Assert.Equal([Vector3d.UnitZ, Vector3d.UnitZ, Vector3d.UnitZ], symbol.Normals!);
    }

    [Fact]
    public void PlacementTransformsSymbolPointsInMetres()
    {
        var document = Map().Document;
        var moved = document.Instances.Where(instance => instance.ElementId == Door).ElementAt(1);
        var symbol = document.Geometries.Single(geometry => geometry.Id == moved.GeometryId);
        // (1, 0, 0) ft of the family maps to the origin (0, 4, 0) + BasisX (0, 1, 0) = (0, 5, 0) ft
        AssertClose(new(0, 5 * Foot, 0), moved.Transform!.Value.Apply(symbol.Vertices[1]));
    }

    [Fact]
    public void OwnMeshesAreLocalToTheirLowestCornerInMetres()
    {
        var document = Map().Document;
        var wall = document.Instances.Where(instance => instance.ElementId == Wall).ToList();
        Assert.Equal(2, wall.Count);
        Assert.All(wall, instance => Assert.Equal(Transform3d.Translate(new(1000 * Foot, 2000 * Foot, 30 * Foot)), instance.Transform));
        var first = document.Geometries.Single(geometry => geometry.Id == wall[0].GeometryId);
        Assert.Equal([new(0, 0, 0), new(Foot, 0, 0), new(0, 2 * Foot, 0)], first.Vertices);
        var second = document.Geometries.Single(geometry => geometry.Id == wall[1].GeometryId);
        AssertClose(new(1002 * Foot, 2000 * Foot, 31 * Foot), wall[1].Transform!.Value.Apply(second.Vertices[1]));
        Assert.Null(wall[0].MaterialId);
        Assert.Equal(Concrete, wall[1].MaterialId);
    }

    [Fact]
    public void ElementsCarryIdentityNamesAndCategories()
    {
        var elements = Map().Document.Elements;
        Assert.Equal([Wall, Door, Empty, Broken], elements.Select(element => element.Id));
        var wall = elements[0];
        Assert.Equal("doc-00000064", wall.SourceId);
        Assert.Equal(1, wall.ModelId);
        Assert.Equal("Walls", wall.Category);
        Assert.Equal("Doors", elements[1].Category);
        Assert.Equal("Generic 200", wall.TypeName);
        Assert.Null(wall.LevelId);
        Assert.Null(wall.IfcGuid);
    }

    [Fact]
    public void ParametersAreTextPropertySetsWithRevitKeys()
    {
        var document = Map().Document;
        var wall = document.Elements[0];
        var door = document.Elements[1];
        Assert.Equal(2, wall.PropertySetIds.Count);
        var instance = document.PropertySets.Single(set => set.Id == wall.PropertySetIds[0]);
        Assert.Equal("revit:instance", instance.Key);
        var height = instance.Properties.Single(property => property.Name == "WALL_USER_HEIGHT_PARAM");
        Assert.Equal("revit:bip:-1001105", height.Key);
        Assert.Equal(SceneValue.FromText("3200.4 mm"), height.Value);
        var user = instance.Properties.Single(property => property.Name == "Fire rating");
        Assert.Null(user.Key);
        Assert.Equal("EI 60", user.Value.Text);
        // one type set, shared by every instance of the type; the door's only instance parameter is its type, an element id
        Assert.Equal(wall.PropertySetIds[1], Assert.Single(door.PropertySetIds));
        var type = document.PropertySets.Single(set => set.Id == wall.PropertySetIds[1]);
        Assert.Equal("revit:type", type.Key);
        Assert.Equal("3", type.Properties.Single(property => property.Name == "Count").Value.Text);
        Assert.Single(document.PropertySets, set => set.Key == "revit:type");
    }

    [Fact]
    public void ApproximateParametersAreLeftOut()
    {
        var estimate = Bip(BuiltInParameter.ANALYTICAL_THERMAL_MASS, 453330.0);
        estimate.IsApproximate = true;
        var info = new RevitFileInfo
        {
            Elements = [new Element { Id = Wall, UniqueId = "w", Parameters = [Length(BuiltInParameter.WALL_USER_HEIGHT_PARAM, 10.5), estimate] }],
        };
        var meshes = new MeshSet { Elements = [new ElementMesh { ElementId = Wall, Meshes = [Triangle(0, 0, 0)] }] };
        var document = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" }).Document;
        var set = Assert.Single(document.PropertySets);
        Assert.Equal("WALL_USER_HEIGHT_PARAM", Assert.Single(set.Properties).Name);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void LengthShowsTheDisplayStringOfThePublicApiWithItsUnit()
    {
        var height = Length(BuiltInParameter.WALL_USER_HEIGHT_PARAM, 10.5);
        // Without a resolved unit the public API only guesses by the parameter name: the value is left out.
        var guessed = Bip(BuiltInParameter.WALL_BASE_OFFSET, 1.25);
        var plain = User(902, "Factor", 0.5);
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [height, guessed, plain] });

        var texts = Texts(document);
        Assert.Equal("3200.4 mm", Assert.Single(texts).Value);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(height.AsValueString(), texts["WALL_USER_HEIGHT_PARAM"]);
            // The text does not follow the culture of the machine.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Assert.Equal("3200.4 mm", Texts(MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [height] }))["WALL_USER_HEIGHT_PARAM"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void UnitWhoseFactorIsNotRevitsConversionIsLeftOut()
    {
        var angle = User(907, "Slope angle", Math.PI / 4);
        angle.UnitSymbol = "\u00B0";
        angle.ConversionFactor = 180 / Math.PI;
        // Revit stores power in kg ft^2/s^3, not in watts: a factor of 1 does not give the watts Revit shows.
        var power = User(908, "Load", 120.0);
        power.UnitSymbol = "W";
        power.ConversionFactor = 1;
        var perLength = User(909, "Surface per length", 2.0);
        perLength.UnitSymbol = "m\u00B2";
        perLength.ConversionFactor = 0.09290304;
        perLength.SpecType = "autodesk.spec.aec:surfaceAreaPerUnitLength-1.0.0";
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [angle, power, perLength] });
        Assert.Equal("45 \u00B0", Assert.Single(Texts(document)).Value);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void ElementIdValueIsLeftOut()
    {
        var level = Bip(BuiltInParameter.WALL_BASE_CONSTRAINT, 311, StorageType.ElementId);
        var material = User(903, "Finish", Concrete, StorageType.ElementId);
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [level, material, User(900, "Fire rating", "EI 60")] });
        Assert.Equal(["Fire rating"], Texts(document).Keys);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void YesNoOfAUserParameterIsTheNumberTheApiShows()
    {
        var external = User(904, "Is external", 1);
        // A built-in integer may be an enumeration, which the public API cannot tell from a number or a Yes/No: left out.
        var roomBounding = Bip(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING, 1);
        var document = MapOne(new Element { Id = Wall, UniqueId = "w", Parameters = [external, roomBounding, User(905, "Count", -12)] });
        var texts = Texts(document);
        Assert.Equal(external.AsValueString(), texts["Is external"]);
        Assert.Equal("1", texts["Is external"]);
        Assert.Equal("-12", texts["Count"]);
        Assert.DoesNotContain("WALL_ATTR_ROOM_BOUNDING", texts.Keys);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void IfcGuidIsTheExportIdOfTheUniqueId()
    {
        // The UniqueId and GlobalId of an element of a fixture file, as Revit 2022 exported it to IFC.
        var element = MapOne(new Element { Id = 263584, UniqueId = "d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405a0" }).Elements[0];
        Assert.Equal("3KMnWMoynDnh2x07HdzyEW", element.IfcGuid);
        Assert.Null(MapOne(new Element { Id = 1, UniqueId = "d45b1816-cbcc-4dc6-b0bb-007467f3c600-0405a0" }).Elements[0].IfcGuid);
        Assert.Null(MapOne(new Element { Id = 1 }).Elements[0].IfcGuid);
    }

    [Fact]
    public void ValidIfcGuidParameterOverridesTheExportId()
    {
        const string UniqueId = "d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405a0";
        var builtIn = Bip(BuiltInParameter.IFC_GUID, "2abcdefghijklmnopqrs$_");
        var shared = User(906, "IfcGUID", "0123456789ABCDEFGHIJKL");
        Assert.Equal("2abcdefghijklmnopqrs$_", MapOne(new Element { Id = 1, UniqueId = UniqueId, Parameters = [shared, builtIn] }).Elements[0].IfcGuid);
        Assert.Equal("0123456789ABCDEFGHIJKL", MapOne(new Element { Id = 1, UniqueId = UniqueId, Parameters = [shared] }).Elements[0].IfcGuid);
        // Not a GlobalId: empty, too short, a character outside the alphabet, a first character above 3.
        foreach (string invalid in new[] { "", "2abcdefghijklmnopqrs$", "2abcdefghijklmnopqrs-_", "4abcdefghijklmnopqrs$_" })
            Assert.Equal("3KMnWMoynDnh2x07HdzyEW",
                MapOne(new Element { Id = 1, UniqueId = UniqueId, Parameters = [Bip(BuiltInParameter.IFC_GUID, invalid)] }).Elements[0].IfcGuid);
    }

    [Fact]
    public void MaterialHasItsNameAndColour()
    {
        var material = Assert.Single(Map().Document.Materials);
        Assert.Equal(Concrete, material.Id);
        Assert.Equal("Concrete", material.Name);
        Assert.Equal(new RgbaColor(0x10, 0x20, 0x30, 204), material.Color);
    }

    [Fact]
    public void MaterialWithoutColourIsGrey()
    {
        var info = new RevitFileInfo { Elements = [new Element { Id = Wall, UniqueId = "w" }, new Element { Id = Concrete, Name = "Plain" }] };
        var meshes = new MeshSet { Elements = [new ElementMesh { ElementId = Wall, Meshes = [Triangle(0, 0, 0, Concrete)] }] };
        var document = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" }).Document;
        var material = Assert.Single(document.Materials);
        Assert.Equal(RgbaColor.Gray, material.Color);
        Assert.Equal("Plain", material.Name);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void IncompleteGeometryIsReported()
    {
        var result = Map();
        Assert.DoesNotContain(result.Document.Instances, instance => instance.ElementId == Empty);
        Assert.Contains(result.Document.Elements, element => element.Id == Empty);
        var empty = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "no-geometry");
        Assert.Equal(SceneSeverity.Warning, empty.Severity);
        Assert.Equal("doc-00000066", empty.SourceId);
        Assert.Equal("doc-00000067", Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "partial-geometry").SourceId);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Equal(4, result.ElementCount);
        Assert.Equal(3, result.ElementsWithGeometry);
        Assert.Equal(new Dictionary<MeshCoverage, int> { [MeshCoverage.Complete] = 2, [MeshCoverage.NoGeometry] = 1, [MeshCoverage.Partial] = 1 }, result.Coverage);
    }

    [Fact]
    public void UndecodedDocumentKeepsItsElementsAndReportsAnError()
    {
        var meshes = new MeshSet
        {
            GeometryUndecoded = true,
            Elements = [new ElementMesh { ElementId = Wall, Coverage = MeshCoverage.Undecoded }, new ElementMesh { ElementId = Door, Coverage = MeshCoverage.Undecoded }],
        };
        var result = RvtSceneReader.Read(Document(), meshes, new RvtSceneOptions { ModelKey = "old.rvt" });
        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == SceneSeverity.Error);
        Assert.Equal("geometry-undecoded", error.Code);
        Assert.Contains("R22Layout", error.Message);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == "undecoded"));
        Assert.Equal(2, result.Document.Elements.Count);
        Assert.Empty(result.Document.Instances);
        Assert.Empty(result.Document.Geometries);
        Assert.Equal(0, result.ElementsWithGeometry);
        Assert.Empty(result.Document.Validate());
    }

    [Fact]
    public void ElementWithoutUniqueIdIsReportedOnce()
    {
        var info = new RevitFileInfo { Elements = [new Element { Id = 1 }, new Element { Id = 2 }] };
        var meshes = new MeshSet { Elements = [new ElementMesh { ElementId = 1, Meshes = [Triangle(0, 0, 0)] }, new ElementMesh { ElementId = 2, Meshes = [Triangle(0, 0, 0)] }] };
        var result = RvtSceneReader.Read(RvtDocument.FromInfo(info), meshes, new RvtSceneOptions { ModelKey = "m" });
        Assert.All(result.Document.Elements, element => Assert.Null(element.SourceId));
        Assert.Contains("2 elements", Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "no-source-id").Message);
        Assert.Empty(result.Document.Validate());
    }

    [Fact]
    public void DocumentNeedsAModelKey()
    {
        var document = Document();
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read(document, null));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read(document, new RvtSceneOptions { ModelKey = "" }));
        Assert.Throws<ArgumentNullException>(() => RvtSceneReader.Read((RvtDocument)null!, new RvtSceneOptions { ModelKey = "k" }));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read("model.rvt", new RvtSceneOptions { ModelKey = "" }));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.Read(document, Meshes(), null));
        Assert.Throws<ArgumentNullException>(() => RvtSceneReader.Read(document, null!, new RvtSceneOptions { ModelKey = "k" }));
        Assert.Throws<ArgumentNullException>(() => RvtSceneReader.Read(null!, Meshes(), new RvtSceneOptions { ModelKey = "k" }));
    }

    [Fact]
    public void DocumentIsReadWithTheGivenKey()
    {
        // None of these elements is drawable, so the scene has the model only.
        var result = RvtSceneReader.Read(Document(), new RvtSceneOptions { ModelKey = "site/A.rvt" });
        Assert.Equal("site/A.rvt", Assert.Single(result.Document.Models).Key);
        Assert.Empty(result.Document.Validate());
    }
}
