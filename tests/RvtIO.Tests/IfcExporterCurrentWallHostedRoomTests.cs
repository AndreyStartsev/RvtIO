using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The test point the current profile looks for the room of a family instance at: beside a side face of the host wall
/// for an instance of a hosted family, one foot along the instance's Z for a work-plane-based instance, one foot against it
/// for an instance of a hosted family on a floor, else the placed centre of the box of the instance's symbol, moved towards
/// the associated level.</summary>
public class IfcExporterCurrentWallHostedRoomTests
{
    private const int RoomPhase = 5;
    private const int Wall = 400, Symbol = 200, Family = 250;

    private static int Tag(string name) => TfBlockDeserializer.Schemas.Single(pair => pair.Value.Name == name).Key;

    /// <summary>A box whose faces carry the handles given for bottom, top, the y0 side, the x1 end, the y1 side and the x0 end.</summary>
    private static GeometryData Box(double x0, double x1, double y0, double y1, double z0, double z1, uint[]? handles = null)
    {
        double[][] p =
        [
            [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
            [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [x0, y0, z0, x1, y1, z1],
            Faces = faces.Select((indices, index) => new GeoFace
            {
                OuterVertices = indices.Select(vertex => p[vertex]).ToList(), PlaneSurface = true, SourceFace = handles?[index] ?? 0,
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    /// <summary>The room is 4 x 3 x 2 ft from the origin; the wall (y from 3 to 3.5) is on its north side.</summary>
    private static Element Room() => new()
    {
        Id = 300, CategoryId = -2000160, Category = "OST_Rooms", Name = "Room",
        Parameters =
        [
            new Parameter { Id = -1006916, StorageType = StorageType.ElementId, Value = 10 },
            new Parameter { Id = -1006901, StorageType = StorageType.String, Value = "1" },
        ],
        SerializedSpaceBoundary = Box(0, 4, 0, 3, 0, 2),
        SerializedRoomPhaseId = RoomPhase,
    };

    /// <summary>The side of the wall that looks into the room (y = 3) has handle 12, the other side (y = 3.5) handle 13.</summary>
    private static Element WallElement(int categoryId = -2000011, uint roomSide = 12, uint otherSide = 13) => new()
    {
        Id = Wall, CategoryId = categoryId, Category = "OST_Walls", Name = "Wall",
        BoundingBox = [0, 3, 0, 4, 3.5, 2], Geometry = Box(0, 4, 3, 3.5, 0, 2, [10, 11, roomSide, 14, otherSide, 15]),
    };

    /// <summary>A generic model hosted by the wall, its origin in the wall's thickness, its box centre inside the wall.</summary>
    private static Element Instance(bool flippedY = false, int hostId = Wall, bool workPlaneBased = false, int associatedLevel = -1)
    {
        var instance = Product(100, -2000151, "OST_GenericModel", Symbol);
        instance.SerializedOwnElementClassId = 1794;
        instance.NativeRecordClass = Tag("FamilyInstance");
        instance.NativeListFields = new()
        {
            ["1794:m_masterSymbolId"] = Symbol, ["1794:m_workPlaneBased"] = workPlaneBased ? 1 : 0, ["37:m_assocLevelId"] = associatedLevel,
        };
        instance.NativeRoomFields = new() { ["1794:m_flippedY"] = flippedY ? 1 : 0 };
        instance.SerializedCreatedPhaseId = RoomPhase;
        instance.SerializedRoomId = -1;
        instance.SerializedHostId = hostId;
        instance.BoundingBox = [1.5, 3.1, 0.5, 2.5, 3.4, 1.5];
        instance.Geometry!.BoundingBox = (double[])instance.BoundingBox.Clone();
        instance.Transform = new InstanceTransform
        {
            Origin = [2, 3.25, 1], BasisX = [1, 0, 0], BasisY = [0, 1, 0], BasisZ = [0, 0, 1], SymbolId = Symbol,
        };
        return instance;
    }

    /// <summary>The symbol the instance stands on: its stored box and origin, in symbol coordinates.</summary>
    private static Element SymbolElement(double[]? origin = null, double[]? box = null) => new()
    {
        Id = Symbol, NativeRecordClass = Tag("FamilySymbol"), NativeListFields = new() { ["1829:m_familyId"] = Family },
        NativeRoomFields = origin == null ? null : new() { ["1829:m_origin@0"] = origin[0], ["1829:m_origin@1"] = origin[1], ["1829:m_origin@2"] = origin[2] },
        BoundingBox = box, BoundingBoxIsHeaderOutline = box != null,
    };

    /// <summary>The family of the symbol: its host type (0 or 7 none, 1 wall, 2 floor) and category.</summary>
    private static Element FamilyElement(int hostType, int category = -2000151) => new()
    {
        Id = Family, NativeRecordClass = Tag("Family"),
        NativeListFields = new()
        {
            ["1772:m_refTypeIds@0"] = hostType, ["1773:m_categoryId"] = category, ["1773:m_isCurtainPanel"] = 0, ["1773:m_isCrvDriven"] = 0,
        },
    };

    private static Dictionary<int, Element> Elements(params Element[] elements) => elements.ToDictionary(element => element.Id);

    private static double[] Rounded(double[]? point) => point!.Select(value => Math.Round(value, 9)).ToArray();

    [Fact]
    public void WallSideTestPoint_IsOneFootBeyondTheSideFaceWithTheHigherHandle_TheLowerOneWhenFlippedInY()
    {
        var elements = Elements(WallElement(), SymbolElement(), FamilyElement(hostType: 1));

        Assert.Equal([2.0, 4.5, 1.0], Rounded(IfcExporter.WallSideTestPointCurrent(Instance(), elements)));               // the face y = 3.5, one foot out
        Assert.Equal([2.0, 2.0, 1.0], Rounded(IfcExporter.WallSideTestPointCurrent(Instance(flippedY: true), elements))); // the face y = 3, one foot into the room

        // The handles decide, not the direction the instance looks in.
        elements[Wall] = WallElement(roomSide: 13, otherSide: 12);
        Assert.Equal([2.0, 2.0, 1.0], Rounded(IfcExporter.WallSideTestPointCurrent(Instance(), elements)));
    }

    [Fact]
    public void WallSideTestPoint_StartsFromThePlacedOriginOfTheSymbol()
    {
        var elements = Elements(WallElement(), SymbolElement(origin: [1.5, 0, 0.25]), FamilyElement(hostType: 1));
        Assert.Equal([3.5, 4.5, 1.25], Rounded(IfcExporter.WallSideTestPointCurrent(Instance(), elements)));
    }

    [Fact]
    public void WallSideTestPoint_NeedsAHostedFamilyInAWallWithAPairOfSideFaces()
    {
        var elements = Elements(WallElement(), SymbolElement(), FamilyElement(hostType: 1));

        Assert.Null(IfcExporter.WallSideTestPointCurrent(Instance(hostId: 999), elements));   // host unknown
        elements[Wall] = WallElement(categoryId: -2000151);
        Assert.Null(IfcExporter.WallSideTestPointCurrent(Instance(), elements));              // host is no wall
        elements[Wall] = WallElement();
        elements[Wall].Geometry = null;
        Assert.Null(IfcExporter.WallSideTestPointCurrent(Instance(), elements));              // no faces
        elements[Wall] = WallElement(roomSide: 12, otherSide: 20);
        Assert.Null(IfcExporter.WallSideTestPointCurrent(Instance(), elements));              // the handles of the two faces are not consecutive
        elements[Wall] = WallElement();
        var noPlacement = Instance();
        noPlacement.Transform = null;
        Assert.Null(IfcExporter.WallSideTestPointCurrent(noPlacement, elements));
        elements[Family] = FamilyElement(hostType: 0);
        Assert.Null(IfcExporter.WallSideTestPointCurrent(Instance(), elements));              // the family takes no host
        elements[Family] = FamilyElement(hostType: 7);
        Assert.Null(IfcExporter.WallSideTestPointCurrent(Instance(), elements));
    }

    [Fact]
    public void WorkPlaneBasedInstance_IsTestedOneFootAlongItsZFromThePlacedOriginOfItsSymbol()
    {
        var elements = Elements(SymbolElement(origin: [0.5, 0, 0], box: [-1, -1, -1, 1, 1, 9]), FamilyElement(hostType: 0));
        var instance = Instance(hostId: -1, workPlaneBased: true);
        instance.Transform!.BasisZ = [0, -1, 0];
        instance.Transform.BasisY = [0, 0, 1];
        Assert.Equal([2.5, 2.25, 1.0], Rounded(IfcExporter.RoomTestPointCurrent(instance, new Dictionary<int, double>(), elements)));
    }

    /// <summary>A floor under the room: its top is the bottom of the room.</summary>
    private static Element FloorElement() => new()
    {
        Id = Wall, CategoryId = -2000032, Category = "OST_Floors", Name = "Floor",
        BoundingBox = [0, 0, -0.5, 4, 3, 0], Geometry = Box(0, 4, 0, 3, -0.5, 0),
    };

    [Fact]
    public void InstanceOfAHostedFamilyOnAFloor_IsTestedOneFootAgainstItsZFromThePlacedOriginOfItsSymbol()
    {
        var elements = Elements(FloorElement(), SymbolElement(origin: [0.5, 0, 0.25], box: [-1, -1, 0, 1, 1, 2]), FamilyElement(hostType: 2));
        var levels = new Dictionary<int, double>();

        Assert.Equal([2.5, 3.25, 0.25], Rounded(IfcExporter.RoomTestPointCurrent(Instance(), levels, elements)));
        var tilted = Instance();
        tilted.Transform!.BasisZ = [0, -2, 0];   // the length of the axis does not count; the origin of the symbol turns with it
        tilted.Transform.BasisY = [0, 0, 1];
        Assert.Equal([2.5, 3.75, 1.0], Rounded(IfcExporter.RoomTestPointCurrent(tilted, levels, elements)));
        var flat = Instance();
        flat.Transform!.BasisZ = [0, 0, 0];      // no direction to go against: the box of the symbol decides
        Assert.Equal([2.0, 3.25, 1.0], Rounded(IfcExporter.RoomTestPointCurrent(flat, levels, elements)));

        // A work-plane-based instance on a face of the floor goes along its Z, as on any work plane.
        Assert.Equal([2.5, 3.25, 2.25], Rounded(IfcExporter.RoomTestPointCurrent(Instance(workPlaneBased: true), levels, elements)));
        // A family that takes no host hides the floor stored with its instance: the box of the symbol decides.
        foreach (int free in new[] { 0, 7 })
        {
            elements[Family] = FamilyElement(hostType: free);
            Assert.Equal([2.0, 3.25, 2.0], Rounded(IfcExporter.RoomTestPointCurrent(Instance(), levels, elements)));
        }
        // So does an instance whose family is not decoded: it is not known to take a host.
        elements.Remove(Family);
        Assert.Equal([2.0, 3.25, 2.0], Rounded(IfcExporter.RoomTestPointCurrent(Instance(), levels, elements)));
        // The host is not a floor.
        elements[Family] = FamilyElement(hostType: 2);
        elements[Wall] = WallElement(categoryId: -2000038);
        Assert.Equal([2.0, 3.25, 2.0], Rounded(IfcExporter.RoomTestPointCurrent(Instance(), levels, elements)));
    }

    [Theory]
    [InlineData(2, "IFCBUILDINGSTOREY")]  // hosted by the floor: tested one foot under its origin, below the room
    [InlineData(0, "IFCSPACE")]           // the same instance of a family without a host: the centre of its box, in the room
    public void InstanceStandingOnTheFloorOfARoom_IsInTheRoomOnlyWhenItsFamilyTakesNoHost(int hostType, string container)
    {
        var instance = Instance();
        instance.Transform!.Origin = [2, 1.5, 0];
        string ifc = Export(IfcProfile.Current, Levels().Concat(
            [Room(), FloorElement(), SymbolElement(box: [-0.5, -0.5, 0, 0.5, 0.5, 1]), FamilyElement(hostType), instance]).ToArray());
        Assert.Equal(container, ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Theory]
    [InlineData(2, 301)]  // hosted by the floor: the room under the floor holds the point one foot under its origin
    [InlineData(0, 300)]  // a family without a host: the room it stands in
    public void InstanceOnAFloorBetweenTwoRooms_TakesTheRoomItsTestPointIsIn(int hostType, int expected)
    {
        var below = Room();
        below.Id = 301;
        below.SerializedSpaceBoundary = Box(0, 4, 0, 3, -3, -0.5);
        var instance = Instance();
        instance.Transform!.Origin = [2, 1.5, 0];
        var elements = Elements(Room(), below, FloorElement(), SymbolElement(box: [-0.5, -0.5, 0, 0.5, 0.5, 1]), FamilyElement(hostType), instance);

        var rooms = IfcExporter.ResolveInstanceRoomsCurrent(elements.Values, elements,
            [new KeyValuePair<int, string>(100, "IFCBUILDINGELEMENTPROXY")], new Dictionary<int, double>());
        Assert.Equal(expected, rooms.GetValueOrDefault(100));
    }

    [Fact]
    public void Instance_TakesThePlacedCentreOfTheBoxOfItsSymbol_MovedTowardsItsStoredLevelByAtMostHalfAFoot()
    {
        // The box of the symbol is not the box of the instance: centre (0, -1, 0.75) in symbol coordinates.
        var elements = Elements(SymbolElement(box: [-1, -2, 0.5, 1, 0, 1]), FamilyElement(hostType: 0));
        var levels = new Dictionary<int, double> { [10] = 0.0, [30] = 2.0 };

        Assert.Equal([2.0, 2.25, 1.75], Rounded(IfcExporter.RoomTestPointCurrent(Instance(hostId: -1), levels, elements)));                        // no level stored
        Assert.Equal([2.0, 2.25, 1.25], Rounded(IfcExporter.RoomTestPointCurrent(Instance(hostId: -1, associatedLevel: 10), levels, elements)));   // half a foot down
        Assert.Equal([2.0, 2.25, 2.0], Rounded(IfcExporter.RoomTestPointCurrent(Instance(hostId: -1, associatedLevel: 30), levels, elements)));    // up to the level
    }

    [Theory]
    [InlineData(IfcProfile.Current, true, "IFCSPACE")]            // flipped in Y: the side face that looks into the room
    [InlineData(IfcProfile.Current, false, "IFCBUILDINGSTOREY")]  // the other side face
    [InlineData(IfcProfile.Legacy, true, "IFCBUILDINGSTOREY")]    // the legacy profile reads only the stored room
    public void WallHostedInstance_IsInTheRoomBesideTheSideFaceItIsTestedAt(IfcProfile version, bool flippedY, string container)
    {
        string ifc = Export(version, Levels().Concat([Room(), WallElement(), SymbolElement(), FamilyElement(hostType: 1), Instance(flippedY)]).ToArray());
        Assert.Equal(container, ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void InstanceOfAFamilyWithoutAHost_KeepsTheBoxCentre()
    {
        // Same wall and instance, but the family takes no host: the centre of the symbol's box (inside the wall) decides.
        string ifc = Export(IfcProfile.Current, Levels().Concat(
            [Room(), WallElement(), SymbolElement(box: [-0.5, -0.15, -0.5, 0.5, 0.15, 0.5]), FamilyElement(hostType: 0), Instance(flippedY: true)]).ToArray());
        Assert.Equal("IFCBUILDINGSTOREY", ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Theory]
    [InlineData(-2000151, "IFCSPACE")]           // generic models are listed
    [InlineData(-2000011, "IFCBUILDINGSTOREY")]  // walls are not: the category of the family decides, not the instance's
    public void TheCategoryOfTheFamilyDecidesWhetherARoomIsLookedFor(int familyCategory, string container)
    {
        var instance = Instance(hostId: -1);
        instance.Transform!.Origin = [2, 1.5, 1];
        string ifc = Export(IfcProfile.Current, Levels().Concat(
            [Room(), SymbolElement(box: [-0.5, -0.5, -0.5, 0.5, 0.5, 0.5]), FamilyElement(hostType: 0, category: familyCategory), instance]).ToArray());
        Assert.Equal(container, ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Theory]
    [InlineData(0.5, 1.0, true)]      // lower offset 0.5: the extent starts half a foot above the level
    [InlineData(0.5, 0.25, false)]
    [InlineData(0.0, 2.0, true)]      // the top: level 30 (2 ft) + upper offset 0 + computation offset 0
    [InlineData(0.0, 2.05, false)]
    public void RoomVolume_HoldsOnlyTheElevationsOfItsVerticalExtent(double lowerOffset, double z, bool expected)
    {
        var room = Room();
        room.NativeRoomFields = new() { ["3471:m_levelId"] = 10, ["3471:m_lowerOffset"] = lowerOffset, ["3471:m_upperLevelId"] = 30, ["3471:m_upperOffset"] = 0 };
        var upper = Level(30, "Level C", 2.0);
        upper.NativeRoomFields = new() { ["2299:m_roomComputationElevationOffset"] = 0 };
        var volume = IfcExporter.RoomVolumeCurrent.Create(room, Elements(upper), new Dictionary<int, double> { [10] = 0.0, [30] = 2.0 });
        Assert.NotNull(volume);
        Assert.Equal(expected, volume!.HoldsElevation(z));
        // A room whose level fields are not decoded has no such limit.
        Assert.True(IfcExporter.RoomVolumeCurrent.Create(Room())!.HoldsElevation(99));
    }

    [Theory]
    [InlineData(RoomPhase, null, 0.0, 300)]   // the validated phase of the room
    [InlineData(null, 0, 0.0, 300)]           // no validated phase: the phase id as the room's record stores it, 0 included
    [InlineData(null, null, 0.0, 0)]          // a room without any phase takes no part
    [InlineData(RoomPhase, null, 1.5, 0)]     // the test point (z = 1) is below the vertical extent of the room
    public void Lookup_TakesTheRoomOfTheInstancesPhaseWhoseVerticalExtentHoldsThePoint(int? validatedPhase, int? storedPhase,
        double lowerOffset, int expected)
    {
        var room = Room();
        room.SerializedRoomPhaseId = validatedPhase;
        room.NativeRoomFields = new() { ["3471:m_levelId"] = 10, ["3471:m_lowerOffset"] = lowerOffset };
        if (storedPhase is int stored) room.NativeRoomFields["3471:m_phaseId"] = stored;
        var instance = Instance(hostId: -1);
        instance.Transform!.Origin = [2, 1.5, 1];
        instance.SerializedCreatedPhaseId = validatedPhase ?? storedPhase ?? RoomPhase;
        var elements = Elements(room, SymbolElement(box: [-0.5, -0.5, -0.5, 0.5, 0.5, 0.5]), FamilyElement(hostType: 0), instance);

        var rooms = IfcExporter.ResolveInstanceRoomsCurrent(elements.Values, elements,
            [new KeyValuePair<int, string>(100, "IFCBUILDINGELEMENTPROXY")], new Dictionary<int, double> { [10] = 0.0 });
        Assert.Equal(expected, rooms.GetValueOrDefault(100));
    }

    private static IEnumerable<Element> Levels() => [Level(10, "Level A", 0.0), Level(30, "Level C", 10.0)];

    private static Element Level(int id, string name, double elevationFeet) => new()
    {
        Id = id, Name = name, CategoryId = -2000240, Category = "OST_Levels", IsNativeLevel = true,
        SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = elevationFeet },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static Element Product(int id, int categoryId, string category, int typeId) => new()
    {
        Id = id, TypeId = typeId, IsDiscovered = true, CategoryId = categoryId, Category = category, Name = $"Product {id}",
        References = [3],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
            new Parameter
            {
                Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = typeId,
            },
        ],
        BoundingBox = [0, 0, 10, 4, 3, 12],
        Geometry = Box(0, 4, 0, 3, 10, 12),
    };

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-currentProfile-wallroom-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string? ContainerOf(string ifc, string ifcType, int tag)
    {
        var product = Regex.Match(ifc, $@"#(\d+)={ifcType}\([^\r\n]*,'{tag}'[,)]");
        if (!product.Success) return null;
        foreach (Match relation in Regex.Matches(ifc, @"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^\r\n]*,\(([^)]*)\),#(\d+)\);"))
            if (relation.Groups[1].Value.Split(',').Contains($"#{product.Groups[1].Value}"))
                return Regex.Match(ifc, $@"#{relation.Groups[2].Value}=(\w+)\(").Groups[1].Value;
        return null;
    }
}
