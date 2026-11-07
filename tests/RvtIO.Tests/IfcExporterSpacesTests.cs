using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Spaces: <c>IfcSpace</c> + <c>IfcRelAggregates</c> (storey decomposition, never <c>IfcRelContainedInSpatialStructure</c>),
/// a private, non-shared <c>IfcSpaceType</c>, <c>IfcRelDefinesByType</c> and the one-property <c>Pset_SpaceCommon</c> (<c>Reference</c> only).
/// The source population is OST_Rooms/OST_Areas, gated fail-closed on (resolved storey, non-empty ROOM_NUMBER, non-empty Name).
/// </summary>
public class IfcExporterSpacesTests
{
    private const int RoomCategoryId = -2000160;      // OST_Rooms
    private const int AreaCategoryId = -2003200;      // OST_Areas
    private const int OtherCategoryId = -2000011;      // OST_Walls
    private const int RoomLevelIdParam = -1006916;     // BuiltInParameter.ROOM_LEVEL_ID
    private const int LevelParamId = -1001952;         // BuiltInParameter.LEVEL_PARAM
    private const int RoomNumberParam = -1006901;      // BuiltInParameter.ROOM_NUMBER
    private const int LevelElevParam = -1007102;       // BuiltInParameter.LEVEL_ELEV
    private const int LevelIsBuildingStoryParam = -1007111; // BuiltInParameter.LEVEL_IS_BUILDING_STORY

    [Fact]
    public void ValidatedSpaceTypeGuid_RelationshipUsesAutodeskMd5()
    {
        var room = Room(100, "Room", "1", 900);
        room.SerializedExportId = Guid.Parse("1f34752d-926d-4ab8-8425-c6d81fe02dab");
        string ifc = ExportToText(Level(900, "Level", 0), room);
        string typeGuid = IfcGuid.ToIfcGuid(RvtIO.Parsing.ElementExportIds.SubElementId(
            room.SerializedExportId.Value, IfcExporter.InstanceAsTypeSubElementIndex));
        Assert.Equal($"'{typeGuid}'", EntityArgs(ifc, SoleEntityId(ifc, "IFCSPACETYPE"), "IFCSPACETYPE")[0]);
        Assert.Equal($"'{IfcGuid.FromMd5("IfcRelDefinesByType:" + typeGuid)}'",
            EntityArgs(ifc, SoleEntityId(ifc, "IFCRELDEFINESBYTYPE"), "IFCRELDEFINESBYTYPE")[0]);
    }

    [Theory]
    [InlineData(31, 7, RoomCategoryId, 0)]
    [InlineData(31, 31, RoomCategoryId, 1)]
    [InlineData(null, 7, RoomCategoryId, 1)]
    [InlineData(31, null, RoomCategoryId, 1)]
    [InlineData(31, -1, RoomCategoryId, 1)]
    [InlineData(31, 7, AreaCategoryId, 1)]
    public void PhasePolicy_FiltersOnlyRoomsWithBothExactPhases(int? documentPhase, int? roomPhase,
        int category, int expected)
    {
        var room = Room(100, "Room", "1", 900);
        room.CategoryId = category;
        room.SerializedRoomPhaseId = roomPhase;
        // Deliberately conflicting legacy fields must not be used as phase source.
        room.Parameters.Add(new Parameter { Id = -1012100, Value = 7, StorageType = StorageType.ElementId });
        room.Parameters.Add(new Parameter { Id = -1012112, Value = 31, StorageType = StorageType.ElementId });
        var info = new RevitFileInfo { Elements = [Level(900, "Level", 0), room],
            SerializedDefaultExportPhaseId = documentPhase, SerializedProjectPhaseIds = [7, 31] };
        string ifc = ExportInfoToText(info, documentPhase);
        Assert.Equal(expected, EntityIds(ifc, "IFCSPACE").Count);
        Assert.Equal(expected, EntityIds(ifc, "IFCSPACETYPE").Count);
        Assert.Equal(expected, EntityIds(ifc, "IFCRELDEFINESBYTYPE").Count);
        Assert.Equal(2, info.Elements.Count);
        Assert.DoesNotContain("SerializedRoomPhaseId", System.Text.Json.JsonSerializer.Serialize(info));
        Assert.DoesNotContain("SerializedDefaultExportPhaseId", System.Text.Json.JsonSerializer.Serialize(info));
    }

    [Fact]
    public void PhasePolicy_IsOptInEvenWithValidatedLastPhase()
    {
        var room = Room(100, "Room", "1", 900);
        room.SerializedRoomPhaseId = 7;
        var info = new RevitFileInfo { Elements = [Level(900, "Level", 0), room],
            SerializedDefaultExportPhaseId = 31, SerializedProjectPhaseIds = [7, 31] };
        Assert.Single(EntityIds(ExportInfoToText(info), "IFCSPACE"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(31, false)]
    [InlineData(99, true)]
    public void PhasePolicy_RejectsInvalidSelectionBeforeWriting(int phase, bool knownOrder)
    {
        var info = new RevitFileInfo { SerializedProjectPhaseIds = knownOrder ? [7, 31] : null };
        var exporter = new IfcExporter { RoomExportPhaseId = phase, Profile = IfcProfile.Legacy };
        using var output = new StringWriter();
        Assert.ThrowsAny<Exception>(() => exporter.Export(info, output));
        Assert.Equal("", output.ToString());
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-phase-{Guid.NewGuid():N}.ifc");
        try
        {
            File.WriteAllText(path, "existing output");
            Assert.ThrowsAny<Exception>(() => exporter.Export(info, path));
            Assert.Equal("existing output", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidatedPlanarBoundary_EmitsClosedBrepInItsStoreyFrame()
    {
        var level = Level(900, "Upper", 10);
        var room = Room(100, "Room", "1", 900);
        room.SerializedSpaceBoundary = SpaceCube();
        // Source face orientation is not required to be consistent; topology is.
        room.SerializedSpaceBoundary.Faces[0].OuterVertices!.Reverse();
        string ifc = ExportToText(level, room);
        var args = EntityArgs(ifc, SoleEntityId(ifc, "IFCSPACE"), "IFCSPACE");
        Assert.NotEqual("$", args[6]);
        Assert.Single(EntityIds(ifc, "IFCFACETEDBREP"));
        Assert.Single(EntityIds(ifc, "IFCCLOSEDSHELL"));
        Assert.Empty(EntityIds(ifc, "IFCFACEBASEDSURFACEMODEL"));
        Assert.Empty(EntityIds(ifc, "IFCBUILDINGELEMENTPROXY"));
        var points = EntityIds(ifc, "IFCPOLYLOOP")
            .SelectMany(id => Regex.Matches(EntityArgs(ifc, id, "IFCPOLYLOOP")[0], @"#(\d+)"))
            .Select(match => int.Parse(match.Groups[1].Value)).Distinct()
            .Select(id => EntityArgs(ifc, id, "IFCCARTESIANPOINT")[0].Trim('(', ')')
                .Split(',').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .ToList();
        Assert.Equal(8, points.Count);
        Assert.Equal(0, points.Min(point => point[2]), 9);
        Assert.Equal(304.8, points.Max(point => point[2]), 9);
        // Export never moves or reverses the original project-coordinate boundary.
        Assert.Equal(10, room.SerializedSpaceBoundary.Faces[0].OuterVertices![0][2]);
    }

    [Theory]
    [InlineData("physical-only")]
    [InlineData("area")]
    [InlineData("open")]
    [InlineData("missing-face")]
    [InlineData("nonfinite")]
    public void UnsupportedSpatialBoundary_DoesNotBecomeAnApproximateBody(string mutation)
    {
        var level = Level(900, "Upper", 10);
        var room = Room(100, "Room", "1", 900);
        room.SerializedSpaceBoundary = SpaceCube();
        switch (mutation)
        {
            case "physical-only": room.Geometry = room.SerializedSpaceBoundary; room.SerializedSpaceBoundary = null; break;
            case "area": room.CategoryId = AreaCategoryId; break;
            case "open": room.SerializedSpaceBoundary.BrepSolids![0].IsClosed = false; break;
            case "missing-face": room.SerializedSpaceBoundary.Faces.RemoveAt(0); break;
            case "nonfinite": room.SerializedSpaceBoundary.Faces[0].OuterVertices![0][0] = double.NaN; break;
        }
        string ifc = ExportToText(level, room);
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCSPACE"), "IFCSPACE")[6]);
        Assert.Empty(EntityIds(ifc, "IFCFACETEDBREP"));
        Assert.Empty(EntityIds(ifc, "IFCFACEBASEDSURFACEMODEL"));
    }

    private static GeometryData SpaceCube()
    {
        double[][] vertices = [[0,0,10],[1,0,10],[1,1,10],[0,1,10],
            [0,0,11],[1,0,11],[1,1,11],[0,1,11]];
        int[][] rings = [[0,3,2,1],[4,5,6,7],[0,1,5,4],[1,2,6,5],[2,3,7,6],[3,0,4,7]];
        return new GeometryData
        {
            Faces = rings.Select(ring => new GeoFace
                { OuterVertices = ring.Select(index => (double[])vertices[index].Clone()).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0,1,2,3,4,5] }],
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisconnectedSpatialShells_AreNotAssumedToBeIndependentFilledSolids(bool nested)
    {
        var level = Level(900, "Upper", 10);
        var room = Room(100, "Room", "1", 900);
        var boundary = SpaceCube();
        var second = SpaceCube();
        foreach (var point in second.Faces.SelectMany(face => face.OuterVertices!))
        {
            point[0] = nested ? .25 + point[0] * .5 : point[0] + 2;
            point[1] = nested ? .25 + point[1] * .5 : point[1];
            point[2] = nested ? 10.25 + (point[2] - 10) * .5 : point[2];
        }
        boundary.Faces.AddRange(second.Faces);
        boundary.BrepSolids![0].FaceIndices = Enumerable.Range(0, 12).ToList();
        room.SerializedSpaceBoundary = boundary;
        string ifc = ExportToText(level, room);
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCSPACE"), "IFCSPACE")[6]);
        Assert.Empty(EntityIds(ifc, "IFCFACETEDBREP"));
        Assert.Empty(EntityIds(ifc, "IFCFACEBASEDSURFACEMODEL"));
    }

    [Theory]
    [InlineData(RoomCategoryId)]
    [InlineData(AreaCategoryId)]
    public void SpatialElement_WithLevelNumberAndName_EmitsSpaceTypeAndPsetAndAggregates(int categoryId)
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element room = Room(id: 100, longName: "Living Room", number: "1.20", levelId: 900, categoryId: categoryId);
        string ifc = ExportToText(level, room);

        int spaceId = SoleEntityId(ifc, "IFCSPACE");
        var spaceArgs = EntityArgs(ifc, spaceId, "IFCSPACE");
        // GlobalId, OwnerHistory, Name, Description, ObjectType, ObjectPlacement,
        // Representation, LongName, CompositionType, InteriorOrExteriorSpace,
        // ElevationWithFlooring.
        Assert.Equal(11, spaceArgs.Count);
        Assert.Equal($"'{IfcGuid.FromName("space:100")}'", spaceArgs[0]);
        Assert.Equal("'1.20'", spaceArgs[2]);          // Name = room Number
        Assert.Equal("$", spaceArgs[3]);
        Assert.Equal("$", spaceArgs[4]);
        Assert.Matches(@"^#\d+$", spaceArgs[5]);       // ObjectPlacement present
        Assert.Equal("$", spaceArgs[6]);                // Representation: no geometry
        Assert.Equal("'Living Room'", spaceArgs[7]);   // LongName = room's own Name (purpose text)
        Assert.Equal(".ELEMENT.", spaceArgs[8]);
        Assert.Equal(".INTERNAL.", spaceArgs[9]);
        Assert.Equal("$", spaceArgs[10]);

        int typeId = SoleEntityId(ifc, "IFCSPACETYPE");
        var typeArgs = EntityArgs(ifc, typeId, "IFCSPACETYPE");
        // GlobalId, OwnerHistory, Name, Description, ApplicableOccurrence, HasPropertySets,
        // RepresentationMaps, Tag, ElementType, PredefinedType.
        Assert.Equal(10, typeArgs.Count);
        Assert.Equal($"'{IfcGuid.FromName("space-type:100")}'", typeArgs[0]);
        Assert.Equal("'Living Room 1.20:100'", typeArgs[2]);
        Assert.Equal("$", typeArgs[5]);   // HasPropertySets always empty
        Assert.Equal("'100'", typeArgs[7]); // Tag = the room's own element id
        Assert.Equal(".NOTDEFINED.", typeArgs[9]);

        var relType = SoleRelDefinesByType(ifc);
        Assert.Equal($"(#{spaceId})", relType.RelatedObjects);
        Assert.Equal(typeId, relType.RelatingType);

        int psetId = SoleEntityIdOfPset(ifc, "Pset_SpaceCommon");
        var psetArgs = EntityArgs(ifc, psetId, "IFCPROPERTYSET");
        Assert.Equal("'Pset_SpaceCommon'", psetArgs[2]);
        int refValueId = int.Parse(Regex.Match(psetArgs[4], @"#(\d+)").Groups[1].Value);
        var refValueArgs = EntityArgs(ifc, refValueId, "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("'Reference'", refValueArgs[0]);
        Assert.Equal("IFCIDENTIFIER('Living Room 1.20')", refValueArgs[2]);

        // The exporter always emits an unrelated Pset_BuildingCommon IFCRELDEFINESBYPROPERTIES
        // too (building-level NumberOfStoreys), so find the one naming this pset specifically.
        int relDefinesByProps = EntityIds(ifc, "IFCRELDEFINESBYPROPERTIES")
            .Single(id => EntityArgs(ifc, id, "IFCRELDEFINESBYPROPERTIES")[5] == $"#{psetId}");
        var relPropsArgs = EntityArgs(ifc, relDefinesByProps, "IFCRELDEFINESBYPROPERTIES");
        Assert.Equal($"(#{spaceId})", relPropsArgs[4]);
        Assert.Equal($"#{psetId}", relPropsArgs[5]);

        var aggArgs = SoleRelAggregatesForSpaces(ifc);
        Assert.Contains($"#{spaceId}", aggArgs.RelatedObjects);
        Assert.DoesNotContain("IFCRELCONTAINEDINSPATIALSTRUCTURE", ExtractSpaceRelatedLines(ifc, spaceId));
    }

    [Theory]
    [InlineData(RoomCategoryId)]
    [InlineData(AreaCategoryId)]
    public void SpatialElement_WithoutResolvedLevel_StaysUnemitted(int categoryId)
    {
        // No level parameter set at all - fail-closed, no guessed storey.
        Element room = Room(id: 100, longName: "Кухня", number: "5", levelId: null, categoryId: categoryId);
        string ifc = ExportToText(room);

        Assert.DoesNotContain("IFCSPACE(", ifc);
        Assert.DoesNotContain("IFCSPACETYPE", ifc);
    }

    [Theory]
    [InlineData(RoomCategoryId)]
    [InlineData(AreaCategoryId)]
    public void SpatialElement_WithLevelNotAnAcceptedStorey_StaysUnemitted(int categoryId)
    {
        // References a Level-category element that fails GetBuildingStoreyLevels' own
        // acceptance test (missing LEVEL_IS_BUILDING_STORY=1) - not a real project storey.
        Element notAStorey = Level(id: 900, name: "Ref Plane", elevationFt: 0, isBuildingStory: false);
        Element room = Room(id: 100, longName: "Кухня", number: "5", levelId: 900, categoryId: categoryId);
        string ifc = ExportToText(notAStorey, room);

        Assert.DoesNotContain("IFCSPACE(", ifc);
    }

    [Theory]
    [InlineData(RoomCategoryId)]
    [InlineData(AreaCategoryId)]
    public void SpatialElement_WithoutNumber_StaysUnemitted(int categoryId)
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element room = Room(id: 100, longName: "Кухня", number: null, levelId: 900, categoryId: categoryId);
        string ifc = ExportToText(level, room);

        Assert.DoesNotContain("IFCSPACE(", ifc);
    }

    [Theory]
    [InlineData(RoomCategoryId)]
    [InlineData(AreaCategoryId)]
    public void SpatialElement_WithoutName_StaysUnemitted(int categoryId)
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element room = Room(id: 100, longName: null, number: "5", levelId: 900, categoryId: categoryId);
        string ifc = ExportToText(level, room);

        Assert.DoesNotContain("IFCSPACE(", ifc);
    }

    [Fact]
    public void NonSpatialCategory_NeverEmitsIfcSpace()
    {
        // Same level/number/name shape, but a different category - the gate must be
        // categorical (OST_Rooms/OST_Areas), never inferred from having those fields alone.
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element notARoom = Room(id: 100, longName: "Кухня", number: "5", levelId: 900,
            categoryId: OtherCategoryId);
        string ifc = ExportToText(level, notARoom);

        Assert.DoesNotContain("IFCSPACE(", ifc);
    }

    [Fact]
    public void TwoRoomsOnSameStorey_ShareOneIfcRelAggregates()
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element roomA = Room(id: 100, longName: "Кухня", number: "1", levelId: 900);
        Element roomB = Room(id: 200, longName: "Спальня", number: "2", levelId: 900);
        string ifc = ExportToText(level, roomA, roomB);

        int spaceAId = SoleEntityIdForName(ifc, "IFCSPACE", "1");
        int spaceBId = SoleEntityIdForName(ifc, "IFCSPACE", "2");

        var aggArgs = SoleRelAggregatesForSpaces(ifc);
        Assert.Contains($"#{spaceAId}", aggArgs.RelatedObjects);
        Assert.Contains($"#{spaceBId}", aggArgs.RelatedObjects);
        Assert.Equal(2, EntityIds(ifc, "IFCSPACE").Count);
    }

    [Theory]
    [InlineData(RoomCategoryId)]
    [InlineData(AreaCategoryId)]
    public void SpatialElement_LevelParamTakesPriorityOverRoomLevel(int categoryId)
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element element = Room(id: 100, longName: "Office", number: "1", levelId: -1, categoryId: categoryId);
        element.Parameters.Add(new Parameter { Id = LevelParamId, StorageType = StorageType.ElementId, Value = 900 });
        string ifc = ExportToText(level, element);

        int storeyId = SoleEntityIdForName(ifc, "IFCBUILDINGSTOREY", "Level 1");
        Assert.Equal(storeyId, SoleRelAggregatesForSpaces(ifc).RelatingObject);
        Assert.Single(EntityIds(ifc, "IFCSPACE"));
    }

    [Fact]
    public void RoomAndAreaWithSameNumber_RetainSeparateSpacesAndTypes()
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element room = Room(id: 100, longName: "Office", number: "1", levelId: 900);
        Element area = Room(id: 200, longName: "Office", number: "1", levelId: 900, categoryId: AreaCategoryId);
        string ifc = ExportToText(level, room, area);

        var spaces = EntityIds(ifc, "IFCSPACE");
        Assert.Equal(2, spaces.Count);
        Assert.Equal(2, spaces.Select(id => EntityArgs(ifc, id, "IFCSPACE")[0]).Distinct().Count());
        Assert.Equal(2, EntityIds(ifc, "IFCSPACETYPE").Count);
        Assert.Equal(2, EntityIds(ifc, "IFCRELDEFINESBYTYPE").Count);
        var aggregate = SoleRelAggregatesForSpaces(ifc);
        foreach (int id in spaces) Assert.Contains($"#{id}", aggregate.RelatedObjects);
    }

    [Fact]
    public void AreaWithGeometry_DoesNotAlsoEmitBuildingElementProxy()
    {
        Element level = Level(id: 900, name: "Level 1", elevationFt: 0);
        Element area = Room(id: 100, longName: "Office", number: "1", levelId: 900, categoryId: AreaCategoryId);
        area.Category = null; // Numeric category is sufficient; display text is optional.
        area.TypeId = 10;
        area.References = [7];
        area.Parameters.Add(new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 7 });
        double[][] vertices = [[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]];
        area.Geometry = new GeometryData
        {
            Faces = new[] { new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 2, 0, 3 } }
                .Select(indices => new GeoFace { OuterVertices = indices.Select(i => vertices[i]).ToList() }).ToList(),
        };
        string ifc = ExportToText(level, area);

        Assert.Single(EntityIds(ifc, "IFCSPACE"));
        Assert.DoesNotContain("IFCBUILDINGELEMENTPROXY(", ifc);
    }


    internal static Element Level(int id, string name, double elevationFt, bool isBuildingStory = true)
    {
        var parameters = new List<Parameter>
        {
            new() { Id = LevelElevParam, Definition = new Definition { Name = "LEVEL_ELEV" },
                StorageType = StorageType.Double, Value = elevationFt },
        };
        if (isBuildingStory)
            parameters.Add(new Parameter
            {
                Id = LevelIsBuildingStoryParam, StorageType = StorageType.Integer, Value = 1,
            });
        return new Element
        {
            Id = id,
            Name = name,
            IsNativeLevel = true,
            SerializedFamilyId = -1,
            SerializedUnplacedOwnerId = -1,
            SerializedOwnerDbViewId = -1,
            Parameters = parameters,
        };
    }

    internal static Element Room(
        int id, string? longName, string? number, int? levelId, int categoryId = RoomCategoryId)
    {
        var parameters = new List<Parameter>();
        if (levelId is int lvl)
            parameters.Add(new Parameter
            {
                Id = RoomLevelIdParam, StorageType = StorageType.ElementId, Value = lvl,
            });
        if (number != null)
            parameters.Add(new Parameter
            {
                Id = RoomNumberParam, StorageType = StorageType.String, Value = number,
            });

        return new Element
        {
            Id = id,
            CategoryId = categoryId,
            Category = "OST_Rooms",
            Name = longName,
            Parameters = parameters,
        };
    }

    private static string ExportToText(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        return ExportInfoToText(info);
    }

    private static string ExportInfoToText(RevitFileInfo info, int? phase = null)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"revitreader-spaces-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { RoomExportPhaseId = phase, Profile = IfcProfile.Legacy }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static List<int> EntityIds(string ifc, string ifcType) =>
        Regex.Matches(ifc, $@"#(\d+)={ifcType}\(")
            .Select(match => int.Parse(match.Groups[1].Value)).ToList();

    private static int SoleEntityId(string ifc, string ifcType) =>
        Assert.Single(EntityIds(ifc, ifcType));

    private static int SoleEntityIdForName(string ifc, string ifcType, string name)
    {
        foreach (int id in EntityIds(ifc, ifcType))
        {
            var args = EntityArgs(ifc, id, ifcType);
            if (args.Count > 2 && args[2] == $"'{name}'") return id;
        }
        throw new Xunit.Sdk.XunitException($"no {ifcType} with Name '{name}' found");
    }

    private static int SoleEntityIdOfPset(string ifc, string psetName)
    {
        foreach (int id in EntityIds(ifc, "IFCPROPERTYSET"))
        {
            var args = EntityArgs(ifc, id, "IFCPROPERTYSET");
            if (args.Count > 2 && args[2] == $"'{psetName}'") return id;
        }
        throw new Xunit.Sdk.XunitException($"no IFCPROPERTYSET named '{psetName}' found");
    }

    /// <summary>Splits one entity's raw argument list on TOP-LEVEL commas only.</summary>
    private static List<string> EntityArgs(string ifc, int entityId, string ifcType)
    {
        var match = Assert.Single(Regex.Matches(
            ifc, $@"#{entityId}={ifcType}\((.*)\);\r?\n"));
        string raw = match.Groups[1].Value;
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(raw[start..i]);
                start = i + 1;
            }
        }
        parts.Add(raw[start..]);
        return parts;
    }

    private static (string GlobalId, int RelatingType, string RelatedObjects) SoleRelDefinesByType(string ifc)
    {
        int relId = SoleEntityId(ifc, "IFCRELDEFINESBYTYPE");
        var args = EntityArgs(ifc, relId, "IFCRELDEFINESBYTYPE");
        Assert.Equal(6, args.Count);
        string globalId = args[0].Trim('\'');
        string relatedObjects = args[4];
        int relatingType = int.Parse(args[5].TrimStart('#'));
        return (globalId, relatingType, relatedObjects);
    }

    /// <summary>Finds the IFCRELAGGREGATES entity whose RelatedObjects (last argument) is a
    /// list of IFCSPACE ids - distinguishes it from the fixed project/site/building/storeys
    /// aggregates the exporter always emits, without depending on entity id ordering.</summary>
    private static (int RelatingObject, string RelatedObjects) SoleRelAggregatesForSpaces(string ifc)
    {
        var spaceIds = new HashSet<int>(EntityIds(ifc, "IFCSPACE"));
        foreach (int id in EntityIds(ifc, "IFCRELAGGREGATES"))
        {
            var args = EntityArgs(ifc, id, "IFCRELAGGREGATES");
            var related = Regex.Matches(args[5], @"#(\d+)")
                .Select(m => int.Parse(m.Groups[1].Value)).ToList();
            if (related.Count > 0 && related.All(spaceIds.Contains))
                return (int.Parse(args[4].TrimStart('#')), args[5]);
        }
        throw new Xunit.Sdk.XunitException("no space-only IFCRELAGGREGATES found");
    }

    private static string ExtractSpaceRelatedLines(string ifc, int spaceId) =>
        string.Join("\n", ifc.Split('\n').Where(line => line.Contains($"#{spaceId})")));
}
