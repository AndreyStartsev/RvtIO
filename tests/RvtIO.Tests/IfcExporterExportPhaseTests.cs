using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The elements that exist in the export phase: demolished at or before it or created after it leaves an element out,
/// the phase order is the authored one, the default is the last phase, rooms follow their own phase. Synthetic projects with three phases.
/// </summary>
public class IfcExporterExportPhaseTests
{
    private const int First = 7, Second = 31, Third = 99;
    private static readonly int[] Order = [First, Second, Third];

    // created, demolished -> exported in the default (last) phase
    [Theory]
    [InlineData(First, -1, true)]       // exists since the first phase, never demolished
    [InlineData(First, Second, false)]  // demolished in the second phase: gone in the last
    [InlineData(First, Third, false)]   // demolished in the last phase itself: gone (demolished phase is at or before the export phase)
    [InlineData(Third, -1, true)]       // created in the last phase
    [InlineData(null, null, true)]      // no phase evidence: kept
    [InlineData(-1, -1, true)]          // the null id is never a phase
    [InlineData(12345, 54321, true)]    // ids that are no phase of the list never exclude
    public void DefaultPhase_IsTheLastOne(int? created, int? demolished, bool exported) =>
        Assert.Equal(exported, Floors(Export(Info(Floor(100, created, demolished)))).Contains(100));

    [Theory]
    [InlineData(First, First, false, false, false)]  // created and demolished in the first phase: in no phase
    [InlineData(First, Second, true, false, false)]  // demolished in the second: still there in the first
    [InlineData(Second, -1, false, true, true)]      // created in the second: not there in the first
    [InlineData(Third, Third, false, false, false)]  // created and demolished in the last
    public void ExplicitPhase_SelectsTheStateOfThatPhase(int created, int demolished, bool inFirst, bool inSecond, bool inThird)
    {
        var info = Info(Floor(100, created, demolished));
        Assert.Equal(inFirst, Floors(Export(info, new IfcExporter { ExportPhaseId = First })).Contains(100));
        Assert.Equal(inSecond, Floors(Export(info, new IfcExporter { ExportPhaseId = Second })).Contains(100));
        Assert.Equal(inThird, Floors(Export(info, new IfcExporter { ExportPhaseId = Third })).Contains(100));
    }

    [Fact]
    public void PhaseOrder_IsTheAuthoredOneNotTheNumericOne()
    {
        // authored order 99, 7: the id 99 is the earlier phase although it is the larger number
        var info = Info(order: [Third, First], elements: [Floor(100, Third, First), Floor(101, First, -1)]);
        // the last phase is 7: 100 is demolished in it and gone, 101 stays
        Assert.Equal([101], Floors(Export(info)));
        // in phase 99 (the first of the order) 100 exists and 101 is not yet created
        Assert.Equal([100], Floors(Export(info, new IfcExporter { ExportPhaseId = Third })));
    }

    [Fact]
    public void AllPhases_KeepsTheDemolishedElements()
    {
        var info = Info(Floor(100, First, Second), Floor(101, First, -1));
        Assert.Equal([101], Floors(Export(info)));
        Assert.Equal([100, 101], Floors(Export(info, new IfcExporter { ExportAllPhases = true })).Order());
    }

    [Fact]
    public void LegacyProfile_KeepsEveryElementByDefault_AndFiltersWithAnExplicitPhase()
    {
        var info = Info(Floor(100, First, Second), Floor(101, First, -1));
        Assert.Equal([100, 101], Floors(Export(info, new IfcExporter { Profile = IfcProfile.Legacy })).Order());
        Assert.Equal([101], Floors(Export(info, new IfcExporter { Profile = IfcProfile.Legacy, ExportPhaseId = Third })));
    }

    [Fact]
    public void WithoutAProvenPhaseOrder_TheDefaultLeavesOutTheElementsDemolishedInAnyPhase()
    {
        // only the set of phases is known: whatever the order, a demolished phase is at or before the last one
        var info = Info(order: null, elements: [Floor(100, First, Second), Floor(101, First, -1), Floor(102, Second, 12345)]);
        Assert.Equal([101, 102], Floors(Export(info)).Order());
        Assert.Equal([100, 101, 102], Floors(Export(info, new IfcExporter { ExportAllPhases = true })).Order());
        Assert.Equal([100, 101, 102], Floors(Export(info, new IfcExporter { Profile = IfcProfile.Legacy })).Order());
    }

    [Fact]
    public void WithoutAnyPhaseElement_NothingIsFiltered()
    {
        var info = Info(order: null, elements: [Floor(100, First, Second)]);
        info.Elements.RemoveAll(element => element.CategoryId == -2000112);
        Assert.Equal([100], Floors(Export(info)));
    }

    [Fact]
    public void ExportPhase_MustBeInTheProvenOrder()
    {
        var info = Info(Floor(100, First, -1));
        Assert.Throws<InvalidOperationException>(() => Export(info, new IfcExporter { ExportPhaseId = 5 }));
        Assert.Throws<InvalidOperationException>(() => Export(Info(order: null, elements: [Floor(100, First, -1)]), new IfcExporter { ExportPhaseId = First }));
    }

    [Fact]
    public void Export_DoesNotChangeTheElementsOfTheDocument()
    {
        var info = Info(Floor(100, First, Second), Floor(101, First, -1));
        Export(info);
        Assert.Equal([100, 101], info.Elements.Where(element => element.Id is 100 or 101).Select(element => element.Id).Order());
    }

    [Fact]
    public void Rooms_FollowTheirOwnPhaseNotTheirCreatedAndDemolishedOnes()
    {
        // the room is demolished in the second phase but its ROOM_PHASE is the last: it stays (the spatial path decides)
        var room = Room(300, roomPhase: Third);
        room.SerializedCreatedPhaseId = First;
        room.SerializedDemolishedPhaseId = Second;
        var info = Info(room);
        Assert.Single(Ids(Export(info), "IFCSPACE"));
    }

    [Fact]
    public void LastPhaseWithoutAResolvedCategory_IsStillTheLastPhase()
    {
        // a 64-bit-id file whose second phase element has no resolved category (id 0): the elements created in it are in the default export
        var info = Info(Floor(100, Third, -1), Floor(101, First, Second));
        info.Elements.Single(element => element.Id == Third).CategoryId = -1;
        Assert.Equal([100], Floors(Export(info)));
    }

    [Fact]
    public void ExportPhase_AlsoSelectsTheRoomsUnlessTheRoomPhaseIsGiven()
    {
        var room = Room(300, roomPhase: First);
        var info = Info(room);
        Assert.Empty(Ids(Export(info), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { ExportPhaseId = First }), "IFCSPACE"));
        Assert.Empty(Ids(Export(info, new IfcExporter { ExportPhaseId = First, RoomExportPhaseId = Second }), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { ExportAllPhases = true }), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { ExportRoomsOfAllPhases = true }), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { ExportPhaseId = Second, ExportRoomsOfAllPhases = true }), "IFCSPACE"));   // the explicit room choice wins
    }

    [Fact]
    public void RoomsOfAllPhases_DoesNotRestoreDemolishedElements()
    {
        var info = Info(Floor(100, First, Second), Room(300, roomPhase: First));
        var exporter = new IfcExporter { ExportRoomsOfAllPhases = true };
        Assert.Empty(Floors(Export(info, exporter)));
        Assert.Single(Ids(Export(info, exporter), "IFCSPACE"));
    }

    // a 64-bit-id file can have a phase element with id 0: it is a phase like any other, "no phase" is a null id
    private static RevitFileInfo ZeroPhaseInfo(params Element[] elements)
    {
        var info = Info([0, First], elements);
        info.Elements.RemoveAll(element => element.CategoryId == -2000112);
        foreach (int phase in new[] { 0, First })
            info.Elements.Add(new Element { Id = phase, CategoryId = -2000112, Name = $"Phase {phase}", NativeRootClassName = "ProjectPhase" });
        return info;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(First)]
    public void ExplicitPhase_KeepsOnlyTheRoomsOfThatPhase_AlsoWhenItsIdIsZero(int phase)
    {
        var info = ZeroPhaseInfo(Room(300, roomPhase: First), Room(301, roomPhase: 0));
        Assert.Single(Ids(Export(info, new IfcExporter { ExportPhaseId = phase }), "IFCSPACE"));
        Assert.Single(Ids(Export(info, new IfcExporter { RoomExportPhaseId = phase }), "IFCSPACE"));
        // the one kept is the room of the selected phase
        int other = phase == 0 ? First : 0;
        Assert.Empty(Ids(Export(ZeroPhaseInfo(Room(300, roomPhase: other)), new IfcExporter { ExportPhaseId = phase }), "IFCSPACE"));
        Assert.Single(Ids(Export(ZeroPhaseInfo(Room(300, roomPhase: phase)), new IfcExporter { ExportPhaseId = phase }), "IFCSPACE"));
    }

    [Fact]
    public void ZeroExportPhase_AlsoSelectsTheElementsOfThatPhase()
    {
        var info = ZeroPhaseInfo(Floor(100, 0, -1), Floor(101, First, -1), Floor(102, 0, 0));
        Assert.Equal([100], Floors(Export(info, new IfcExporter { ExportPhaseId = 0 })));
        Assert.Equal([100, 101], Floors(Export(info, new IfcExporter { ExportPhaseId = First })).Order());
    }

    [Fact]
    public void ZeroRoomPhase_DefaultAndAllPhasesAreUnchanged()
    {
        var info = ZeroPhaseInfo(Room(300, roomPhase: First), Room(301, roomPhase: 0));
        Assert.Single(Ids(Export(info), "IFCSPACE"));                                                      // the last phase only
        Assert.Equal(2, Ids(Export(info, new IfcExporter { ExportAllPhases = true }), "IFCSPACE").Count);
        Assert.Equal(2, Ids(Export(info, new IfcExporter { ExportRoomsOfAllPhases = true }), "IFCSPACE").Count);
        Assert.Equal(2, Ids(Export(info, new IfcExporter { Profile = IfcProfile.Legacy }), "IFCSPACE").Count);
    }

    [Fact]
    public void NegativeRoomPhase_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Export(ZeroPhaseInfo(Room(300, roomPhase: First)), new IfcExporter { RoomExportPhaseId = -1 }));

    private static List<int> Floors(string ifc) =>
        Ids(ifc, "IFCSLAB").Select(id => Regex.Match(ifc, $@"#{id}=IFCSLAB\('[^']*',#\d+,'[^']*',[^;]*,'(\d+)'", RegexOptions.None))
            .Where(match => match.Success).Select(match => int.Parse(match.Groups[1].Value)).ToList();

    private static List<int> Ids(string ifc, string type) =>
        Regex.Matches(ifc, $@"#(\d+)={type}\(").Select(match => int.Parse(match.Groups[1].Value)).ToList();

    private static RevitFileInfo Info(params Element[] elements) => Info(Order, elements);

    private static RevitFileInfo Info(int[]? order, Element[] elements)
    {
        var info = new RevitFileInfo { SerializedProjectPhaseIds = order };
        info.Elements.AddRange(elements);
        info.Elements.Add(Level(900));
        info.Elements.Add(new Element { Id = 500, TypeId = -1, CategoryId = -2000032, Name = "Floor type" });
        foreach (int phase in Order) info.Elements.Add(new Element { Id = phase, CategoryId = -2000112, Name = $"Phase {phase}", NativeRootClassName = "ProjectPhase" });
        return info;
    }

    private static Element Level(int id) => new()
    {
        Id = id, Name = "Level 1", IsNativeLevel = true, SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static Element Room(int id, int roomPhase) => new()
    {
        Id = id, CategoryId = -2000160, Name = "Room", SerializedRoomPhaseId = roomPhase,
        Category = "OST_Rooms",
        Parameters =
        [
            new Parameter { Id = -1006916, StorageType = StorageType.ElementId, Value = 900 },
            new Parameter { Id = -1006901, StorageType = StorageType.String, Value = "1" },
        ],
    };

    private static Element Floor(int id, int? created, int? demolished)
    {
        double[][] points = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 1], [4, 0, 1], [4, 3, 1], [0, 3, 1]];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new Element
        {
            Id = id, TypeId = 500, CategoryId = -2000032, Category = "OST_Floors", Name = "Floor",
            References = [First], SerializedCreatedPhaseId = created, SerializedDemolishedPhaseId = demolished,
            Parameters = [new Parameter { Id = -1002050, Value = 500, StorageType = StorageType.ElementId,
                    Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
                new Parameter { Id = -1012100, Value = First, StorageType = StorageType.ElementId }],
            BoundingBox = [0, 0, 0, 4, 3, 1],
            Geometry = new GeometryData { BoundingBox = [0, 0, 0, 4, 3, 1],
                Faces = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(index => points[index]).ToList() }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }] },
        };
    }

    private static string Export(RevitFileInfo info, IfcExporter? exporter = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-export-phase-{Guid.NewGuid():N}.ifc");
        try { (exporter ?? new IfcExporter()).Export(info, path); return File.ReadAllText(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
