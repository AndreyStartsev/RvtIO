using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The current profile storey of a stair (STAIRS_BASE_LEVEL_PARAM is StairsElement.m_baseLevelId) and the room of a family
/// instance. The legacy profile keeps its earlier behaviour in every case below.
/// </summary>
public class IfcExporterCurrentSpacesContainmentTests
{
    private const int Stairs = -2000120;

    [Theory]
    [InlineData(IfcProfile.Current, 10, "Level A")]  // the stored base level wins over the bounding box and FAMILY_LEVEL_PARAM
    [InlineData(IfcProfile.Current, 999, "Level C")] // not a storey: the earlier rule
    [InlineData(IfcProfile.Legacy, 10, "Level C")]
    public void Stair_TakesItsStoredBaseLevelInCurrent(IfcProfile version, int baseLevel, string storey)
    {
        var stair = Product(100, Stairs, "OST_Stairs", 200);
        stair.SerializedStairBaseLevelId = baseLevel;
        stair.Parameters.Add(new Parameter
        {
            Id = -1001352, Definition = new Definition { Id = -1001352, Name = "FAMILY_LEVEL_PARAM" },
            StorageType = StorageType.ElementId, Value = 30,
        });
        string ifc = Export(version, Levels().Append(stair).ToArray());
        Assert.Equal(storey, StoreyOf(ifc, "IFCSTAIR", 100));
    }

    [Fact]
    public void Stair_WithoutAStoredBaseLevel_KeepsTheEarlierRule()
    {
        var stair = Product(100, Stairs, "OST_Stairs", 200);
        stair.Parameters.Add(new Parameter
        {
            Id = -1001352, Definition = new Definition { Id = -1001352, Name = "FAMILY_LEVEL_PARAM" },
            StorageType = StorageType.ElementId, Value = 30,
        });
        string ifc = Export(IfcProfile.Current, Levels().Append(stair).ToArray());
        Assert.Equal("Level C", StoreyOf(ifc, "IFCSTAIR", 100));
    }

    [Theory]
    [InlineData(0.0, "Level B")]     // exactly on the lower bound
    [InlineData(1e-12, "Level B")]   // rounding noise above it
    [InlineData(-1e-6, "Level B")]
    [InlineData(1e-6, "Level C")]
    [InlineData(0.5, "Level C")]
    public void ElementWithoutALevel_BottomOnARangeBoundary_StaysInTheRangeBelow(double aboveBoundary, string storey)
    {
        var proxy = Product(100, -2000151, "OST_GenericModel", 200);
        double bottom = 10.0 - 10.0 / (12.0 * 2.54) + aboveBoundary;
        proxy.BoundingBox = [0, 0, bottom, 4, 3, bottom + 2];
        proxy.Geometry!.BoundingBox = [0, 0, bottom, 4, 3, bottom + 2];
        string ifc = Export(IfcProfile.Current, Levels().Append(Level(20, "Level B", 5.0)).Append(proxy).ToArray());
        Assert.Equal(storey, StoreyOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Theory]
    [InlineData(2.0, 1.5, 1.0, 0.0, true)]    // inside
    [InlineData(4.0, 1.5, 1.0, 0.0, true)]    // on the wall line
    [InlineData(4.05, 1.5, 1.0, 0.0, false)]  // just outside
    [InlineData(4.05, 1.5, 1.0, 0.1, true)]   // outside within the tolerance
    [InlineData(2.0, 1.5, 2.05, 0.0, false)]  // above the ceiling
    [InlineData(2.0, 1.5, -0.05, 0.1, true)]  // below the floor within the tolerance
    [InlineData(-3.0, 1.5, 1.0, 0.1, false)]  // far away
    public void RoomVolume_ContainsPointsOfItsPrismWithinTheTolerance(double x, double y, double z, double tolerance, bool expected)
    {
        var room = new Element { Id = 300, SerializedSpaceBoundary = Prism(0) };
        var volume = IfcExporter.RoomVolumeCurrent.Create(room);
        Assert.NotNull(volume);
        Assert.Equal(expected, volume!.Contains(x, y, z, tolerance));
    }

    [Fact]
    public void RoomVolume_NeedsADecodedBoundary()
    {
        Assert.Null(IfcExporter.RoomVolumeCurrent.Create(new Element { Id = 300 }));
        Assert.Null(IfcExporter.RoomVolumeCurrent.Create(new Element { Id = 300, SerializedSpaceBoundary = new GeometryData() }));
    }

    private const int RoomPhase = 5;

    /// <summary>A generic model (category -2000151) whose bounding box is a 1 ft cube around (x, y, z), created in the room
    /// phase, with no room stored.</summary>
    private static Element Instance(double x, double y, double z, int? createdPhase = RoomPhase)
    {
        var instance = Product(100, -2000151, "OST_GenericModel", 200);
        instance.SerializedOwnElementClassId = 1794;
        instance.SerializedCreatedPhaseId = createdPhase;
        instance.SerializedRoomId = -1;
        instance.BoundingBox = [x - 0.5, y - 0.5, z - 0.5, x + 0.5, y + 0.5, z + 0.5];
        instance.Geometry!.BoundingBox = (double[])instance.BoundingBox.Clone();
        return instance;
    }

    private static Element Room(int phase = RoomPhase)
    {
        var room = new Element
        {
            Id = 300, CategoryId = -2000160, Category = "OST_Rooms", Name = "Room",
            Parameters =
            [
                new Parameter { Id = -1006916, StorageType = StorageType.ElementId, Value = 10 },
                new Parameter { Id = -1006901, StorageType = StorageType.String, Value = "1" },
            ],
            SerializedSpaceBoundary = Prism(0),
            SerializedRoomPhaseId = phase,
        };
        room.SerializedSpaceBoundary.BoundingBox = [0, 0, 0, 4, 3, 2];
        return room;
    }

    /// <summary>The kind of spatial structure element that holds the product with this tag.</summary>
    private static string? ContainerOf(string ifc, string ifcType, int tag)
    {
        var product = Regex.Match(ifc, $@"#(\d+)={ifcType}\([^\r\n]*,'{tag}'[,)]");
        if (!product.Success) return null;
        foreach (Match relation in Regex.Matches(ifc, @"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^\r\n]*,\(([^)]*)\),#(\d+)\);"))
            if (relation.Groups[1].Value.Split(',').Contains($"#{product.Groups[1].Value}"))
                return Regex.Match(ifc, $@"#{relation.Groups[2].Value}=(\w+)\(").Groups[1].Value;
        return null;
    }

    [Theory]
    [InlineData(IfcProfile.Current, 2.0, 1.5, 0.5, RoomPhase, "IFCSPACE")]       // the centre is in the room
    [InlineData(IfcProfile.Current, 4.5, 1.5, 0.5, RoomPhase, "IFCBUILDINGSTOREY")] // outside the room
    [InlineData(IfcProfile.Current, 2.0, 1.5, 0.5, 7, "IFCBUILDINGSTOREY")]      // created in another phase
    [InlineData(IfcProfile.Current, 4.0, 1.5, 0.5, RoomPhase, "IFCSPACE")]       // on the wall line: the boundary counts
    [InlineData(IfcProfile.Legacy, 2.0, 1.5, 0.5, RoomPhase, "IFCBUILDINGSTOREY")] // the legacy profile reads only the stored room
    public void FamilyInstance_WithoutAStoredRoom_IsInTheRoomHoldingItsBoxCentre(IfcProfile version, double x, double y, double z, int phase, string container)
    {
        string ifc = Export(version, Levels().Concat([Room(), Instance(x, y, z, phase == RoomPhase ? RoomPhase : 7)]).ToArray());
        Assert.Equal(container, ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void FamilyInstance_CentreIsMovedTowardsItsLevelByAtMostHalfAFoot()
    {
        // Centre at z = 2.3 is above the 2 ft ceiling; the level (z = 0) pulls it down by 0.5 ft to 1.8 ft: inside.
        var instance = Instance(2, 1.5, 2.3);
        instance.Parameters.Add(new Parameter { Id = -1002062, StorageType = StorageType.ElementId, Value = 10 });
        string ifc = Export(IfcProfile.Current, Levels().Concat([Room(), instance]).ToArray());
        Assert.Equal("IFCSPACE", ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
        // Without an associated level the centre stays where it is.
        ifc = Export(IfcProfile.Current, Levels().Concat([Room(), Instance(2, 1.5, 2.3)]).ToArray());
        Assert.Equal("IFCBUILDINGSTOREY", ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void FamilyInstance_OfAnotherCategory_IsNotAskedForARoom()
    {
        var instance = Instance(2, 1.5, 0.5);
        instance.CategoryId = -2000011; // walls are not in the list
        string ifc = Export(IfcProfile.Current, Levels().Concat([Room(), instance]).ToArray());
        Assert.NotEqual("IFCSPACE", ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void FamilyInstance_WithAStoredRoom_KeepsIt(IfcProfile version)
    {
        // The box centre is outside the room; the stored room decides.
        var instance = Instance(9, 1.5, 0.5);
        instance.SerializedRoomId = 300;
        string ifc = Export(version, Levels().Concat([Room(), instance]).ToArray());
        Assert.Equal("IFCSPACE", ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    [Fact]
    public void FamilyInstance_WithoutACreatedPhase_IsLeftInTheStorey()
    {
        var instance = Instance(2, 1.5, 0.5, createdPhase: null);
        string ifc = Export(IfcProfile.Current, Levels().Concat([Room(), instance]).ToArray());
        Assert.Equal("IFCBUILDINGSTOREY", ContainerOf(ifc, "IFCBUILDINGELEMENTPROXY", 100));
    }

    private static IEnumerable<Element> Levels() =>
    [
        Level(10, "Level A", 0.0),
        Level(30, "Level C", 10.0),
    ];

    private static Element Level(int id, string name, double elevationFeet) => new()
    {
        Id = id,
        Name = name,
        CategoryId = -2000240,
        Category = "OST_Levels",
        IsNativeLevel = true,
        SerializedFamilyId = -1,
        SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter
            {
                Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" },
                StorageType = StorageType.Double, Value = elevationFeet,
            },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static Element Product(int id, int categoryId, string category, int typeId) => new()
    {
        Id = id,
        TypeId = typeId,
        IsDiscovered = true,
        CategoryId = categoryId,
        Category = category,
        Name = $"Product {id}",
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
        Geometry = Prism(10),
    };

    [Theory]
    [InlineData(IfcProfile.Current, 1)]
    [InlineData(IfcProfile.Legacy, 0)]
    public void MepSpace_IsAnIfcSpaceInCurrent(IfcProfile version, int expected)
    {
        var space = Room();
        space.CategoryId = -2003600;
        space.Category = "OST_MEPSpaces";
        space.SerializedRoomPhaseId = null;
        string ifc = Export(version, Levels().Append(space).ToArray());
        Assert.Equal(expected, Regex.Matches(ifc, "=IFCSPACE\\(").Count);
    }

    private static GeometryData Prism(double bottom)
    {
        double[][] points =
        [
            [0, 0, bottom], [4, 0, bottom], [4, 3, bottom], [0, 3, bottom],
            [0, 0, bottom + 2], [4, 0, bottom + 2], [4, 3, bottom + 2], [0, 3, bottom + 2],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [0, 0, bottom, 4, 3, bottom + 2],
            Faces = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(index => points[index]).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-currentProfile-spaces-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The name of the storey that IfcRelContainedInSpatialStructure gives the product with this tag.</summary>
    private static string? StoreyOf(string ifc, string ifcType, int tag)
    {
        var product = Regex.Match(ifc, $@"#(\d+)={ifcType}\([^\r\n]*,'{tag}'[,)]");
        if (!product.Success) return null;
        foreach (Match relation in Regex.Matches(ifc, @"IFCRELCONTAINEDINSPATIALSTRUCTURE\([^\r\n]*,\(([^)]*)\),#(\d+)\);"))
            if (relation.Groups[1].Value.Split(',').Contains($"#{product.Groups[1].Value}"))
                return Regex.Match(ifc, $@"#{relation.Groups[2].Value}=IFCBUILDINGSTOREY\('[^']*',#\d+,'([^']*)'").Groups[1].Value;
        return null;
    }
}
