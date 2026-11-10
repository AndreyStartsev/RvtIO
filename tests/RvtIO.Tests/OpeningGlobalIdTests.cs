using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// IfcOpeningElement identities: the expected GlobalIds follow the documented Revit export derivation.
/// </summary>
public class OpeningGlobalIdTests
{
    private static Guid Id(string globalId) => IfcGuid.Parse(globalId)!.Value;

    [Theory]
    [InlineData("2pfAHb2EL46hq_sMVbImEN")]
    [InlineData("3lLx0gNe59vvExhby0Bfew")]
    [InlineData("0zHsnpxmbKKMuNHbtDcZ62")]
    public void Parse_IsTheInverseOfToIfcGuid(string globalId)
    {
        Assert.Equal(globalId, IfcGuid.ToIfcGuid(Id(globalId)));
        Assert.Null(IfcGuid.Parse("too short"));
    }

    [Fact]
    public void DoorWindowOpeningGlobalId_HashesTheDoorAndItsLevel()
    {
        // Door 906937 (symbol with a cut loop) on level 311: the cutout extrusion.
        Assert.Equal("3Db1Rnh_JEBxCc0rriLNGP", IfcExporter.DoorWindowOpeningGlobalId(
            Id("2pfAHb2EL46hq_sMVbImEN"), 311, IfcExporter.DoorWindowOpeningKind.Extrusion));
        // Door 505974 (symbol without one) on level 245423: the host wall's opening solid.
        Assert.Equal("0zHsnpxmbKKMuNHbtDcZ62", IfcExporter.DoorWindowOpeningGlobalId(
            Id("2MWqvKy9535viQk_YsG9Xh"), 245423, IfcExporter.DoorWindowOpeningKind.Solid));
    }

    [Fact]
    public void DoorWindowOpeningGlobalId_CurrentSpellsTheSolidWithTheSpace()
    {
        // The current profile: door 1v6PmD2qD6DxSd7NJuTy3c on level 410356 fills the host's opening solid 1T8mqUQ$5zmHvfe8KjP9iA.
        Assert.Equal("1T8mqUQ$5zmHvfe8KjP9iA", IfcExporter.DoorWindowOpeningGlobalId(
            Id("1v6PmD2qD6DxSd7NJuTy3c"), 410356, IfcExporter.DoorWindowOpeningKind.Solid, currentProfile: true));
        // The legacy profile wrote no space there; the extrusion has it in both versions.
        Assert.NotEqual("1T8mqUQ$5zmHvfe8KjP9iA", IfcExporter.DoorWindowOpeningGlobalId(
            Id("1v6PmD2qD6DxSd7NJuTy3c"), 410356, IfcExporter.DoorWindowOpeningKind.Solid));
        Assert.Equal("3Db1Rnh_JEBxCc0rriLNGP", IfcExporter.DoorWindowOpeningGlobalId(
            Id("2pfAHb2EL46hq_sMVbImEN"), 311, IfcExporter.DoorWindowOpeningKind.Extrusion, currentProfile: true));
    }

    [Fact]
    public void CutOpeningGlobalId_HashesIndexesHostAndCutter()
    {
        // Door 906937 also cuts curtain-adjacent wall 422243, its second opening data.
        Assert.Equal("3pDJfvjtEPkISoZPxayrQo", IfcExporter.CutOpeningGlobalId(
            Id("1PDnLIM013wvkZO9Lb4$wc"), Id("2pfAHb2EL46hq_sMVbImEN"), 2, 1));
    }

    [Fact]
    public void SplitStepArguments_KeepsStringsAndListsWhole()
    {
        Assert.Equal(new[] { "'a,b'", "#1", "(#2,#3)", "$", "'it''s'" },
            IfcExporter.SplitStepArguments("'a,b',#1,(#2,#3),$,'it''s'"));
    }


    private const int WallsCategoryId = -2000011, DoorsCategoryId = -2000023;

    private sealed record Model(StepWriter Writer, Dictionary<int, Element> Elements,
        Dictionary<int, int> ProductElements, Dictionary<int, int> Levels, HashSet<string> Exact,
        int Opening, int Voids, int? Fills);

    private static Element Typed(int id, int category, Guid exportId, int? typeId = null, int? hostId = null)
    {
        var element = new Element { Id = id, CategoryId = category, SerializedExportId = exportId, SerializedHostId = hostId };
        if (typeId is int type)
            element.Parameters.Add(new Parameter
            {
                Id = -1002050,
                Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId,
                Value = type,
            });
        return element;
    }

    /// <summary>Wall 198749 hosting door 906937 of symbol 907609.</summary>
    private static Model DoorModel(int? cutLoopCount, bool writeDoor, string wallClass = "IFCWALLSTANDARDCASE")
    {
        Guid wallId = Id("3lLx0gNe59vvExhby0Bff1"), doorId = Id("2pfAHb2EL46hq_sMVbImEN");
        var wall = Typed(198749, WallsCategoryId, wallId);
        var door = Typed(906937, DoorsCategoryId, doorId, typeId: 907609, hostId: 198749);
        var symbol = new Element { Id = 907609, CategoryId = DoorsCategoryId, SerializedCutLoopCount = cutLoopCount };
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wallEntity = w.Add(wallClass, $"'{IfcGuid.ToIfcGuid(wallId)}',#{owner},'Basic Wall:Wall - Timber Clad:198749',$,$,$,$,'198749'");
        int? doorEntity = writeDoor
            ? w.Add("IFCDOOR", $"'{IfcGuid.ToIfcGuid(doorId)}',#{owner},'Door:Single:906937',$,$,$,$,'906937',$,$")
            : null;
        int opening = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName("o")}',#{owner},'Door',$,'Opening',$,$,'906937'");
        int voids = w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName("v")}',#{owner},$,$,#{wallEntity},#{opening}");
        int? fills = doorEntity is int d
            ? w.Add("IFCRELFILLSELEMENT", $"'{IfcGuid.FromName("f")}',#{owner},$,$,#{opening},#{d}")
            : null;
        var products = new Dictionary<int, int> { [wallEntity] = wall.Id };
        if (doorEntity is int written) products[written] = door.Id;
        return new Model(w, new[] { wall, door, symbol }.ToDictionary(e => e.Id), products,
            new Dictionary<int, int> { [wall.Id] = 245423, [door.Id] = 311 },
            new HashSet<string> { IfcGuid.ToIfcGuid(wallId), IfcGuid.ToIfcGuid(doorId) },
            opening, voids, fills);
    }

    private static string Args(StepWriter w, int id)
    {
        for (int i = 0; i < w.EntityCount; i++)
            if (w.EntityAt(i).Id == id) return w.EntityAt(i).Args;
        throw new KeyNotFoundException();
    }

    private static HashSet<string> Apply(Model model) => IfcExporter.ApplyDoorWindowOpeningIdentities(
        model.Writer, model.Elements, model.ProductElements, model.Levels, model.Exact);

    [Fact]
    public void WrittenDoorWithACutLoop_IsCutOutAsAnExtrusionOnItsOwnLevel()
    {
        Model model = DoorModel(cutLoopCount: 1, writeDoor: true);
        Assert.Single(Apply(model));
        Assert.Equal("'3Db1Rnh_JEBxCc0rriLNGP',#1,'Door:Single:906937:1',$,'Opening',$,$,'906937'",
            Args(model.Writer, model.Opening));
        // Voided by the door's sub-element 20; filled by the MD5 of opening and door.
        Assert.StartsWith($"'{IfcGuid.ToIfcGuid(Parsing.ElementExportIds.SubElementId(Id("2pfAHb2EL46hq_sMVbImEN"), 20))}'",
            Args(model.Writer, model.Voids));
        Assert.StartsWith($"'{IfcGuid.FromMd5("IfcRelFillsElement:3Db1Rnh_JEBxCc0rriLNGP2pfAHb2EL46hq_sMVbImEN")}'",
            Args(model.Writer, model.Fills!.Value));
    }

    [Fact]
    public void WrittenDoorWithoutACutLoop_TakesTheWallSolidAndName()
    {
        Model model = DoorModel(cutLoopCount: 0, writeDoor: true);
        Assert.Single(Apply(model));
        string expected = IfcExporter.DoorWindowOpeningGlobalId(
            Id("2pfAHb2EL46hq_sMVbImEN"), 311, IfcExporter.DoorWindowOpeningKind.Solid);
        Assert.Equal($"'{expected}',#1,'Basic Wall:Wall - Timber Clad:198749',$,'Opening',$,$,'906937'",
            Args(model.Writer, model.Opening));
        Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcRelVoidsElement:3lLx0gNe59vvExhby0Bff1{expected}")}'",
            Args(model.Writer, model.Voids));
    }

    [Fact]
    public void Current_WrittenDoorWithoutACutLoop_TakesTheSpacedSolidGlobalIdAndItsRelations()
    {
        Model model = DoorModel(cutLoopCount: 0, writeDoor: true);
        Assert.Single(IfcExporter.ApplyDoorWindowOpeningIdentities(
            model.Writer, model.Elements, model.ProductElements, model.Levels, model.Exact, currentProfile: true));
        string expected = IfcExporter.DoorWindowOpeningGlobalId(
            Id("2pfAHb2EL46hq_sMVbImEN"), 311, IfcExporter.DoorWindowOpeningKind.Solid, currentProfile: true);
        Assert.NotEqual(IfcExporter.DoorWindowOpeningGlobalId(
            Id("2pfAHb2EL46hq_sMVbImEN"), 311, IfcExporter.DoorWindowOpeningKind.Solid), expected);
        // Named after the host, voided and filled by the MD5 relations.
        Assert.Equal($"'{expected}',#1,'Basic Wall:Wall - Timber Clad:198749',$,'Opening',$,$,'906937'",
            Args(model.Writer, model.Opening));
        Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcRelVoidsElement:3lLx0gNe59vvExhby0Bff1{expected}")}'",
            Args(model.Writer, model.Voids));
        Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcRelFillsElement:{expected}2pfAHb2EL46hq_sMVbImEN")}'",
            Args(model.Writer, model.Fills!.Value));
    }

    [Theory]
    [InlineData(true, 777, true, 777)]
    [InlineData(false, 777, true, 311)]
    // No level of its own: the level of the storey.
    [InlineData(true, -1, true, 311)]
    // In no storey: the opening is left as it is, whatever the own level.
    [InlineData(true, 777, false, null)]
    public void WrittenDoor_IsIdentifiedByItsOwnLevelInTheCurrentProfileOnly(
        bool currentProfile, int ownLevel, bool inStorey, int? level)
    {
        // The door is contained in the storey of level 311; 777 is a level that is no storey.
        Model model = DoorModel(cutLoopCount: 1, writeDoor: true);
        model.Elements[906937].Parameters.Add(new Parameter
        {
            Id = -1001952, Definition = new Definition { Id = -1001952, Name = "LEVEL_PARAM" },
            StorageType = StorageType.ElementId, Value = ownLevel,
        });
        if (!inStorey) model.Levels.Remove(906937);
        string before = Args(model.Writer, model.Opening);
        var assigned = IfcExporter.ApplyDoorWindowOpeningIdentities(
            model.Writer, model.Elements, model.ProductElements, model.Levels, model.Exact, currentProfile);
        if (level is not int expected)
        {
            Assert.Empty(assigned);
            Assert.Equal(before, Args(model.Writer, model.Opening));
            return;
        }
        Assert.Single(assigned);
        Assert.StartsWith($"'{IfcExporter.DoorWindowOpeningGlobalId(Id("2pfAHb2EL46hq_sMVbImEN"), expected, IfcExporter.DoorWindowOpeningKind.Extrusion)}'",
            Args(model.Writer, model.Opening));
    }

    [Theory]
    [InlineData(StorageType.Integer, -1, 1)]
    [InlineData(StorageType.String, "IfcWall", 0)]
    public void IfcExportParameterOnTheDoor_BlocksTheDoorPathOnlyAsARealOverride(StorageType storage, object value, int assigned)
    {
        Model model = DoorModel(cutLoopCount: 1, writeDoor: true);
        model.Elements[906937].Parameters.Add(new Parameter
        {
            Id = -1019014, Definition = new Definition { Id = -1019014, Name = "IFC_EXPORT_ELEMENT_AS" },
            StorageType = storage, Value = value,
        });
        Assert.Equal(assigned, IfcExporter.ApplyDoorWindowOpeningIdentities(
            model.Writer, model.Elements, model.ProductElements, model.Levels, model.Exact, currentProfile: true).Count);
    }

    [Fact]
    public void UnwrittenDoor_TakesTheWallSolidOnTheWallLevel()
    {
        // A doorway written as no product: the host's level, never an extrusion.
        Model model = DoorModel(cutLoopCount: 1, writeDoor: false);
        Assert.Single(Apply(model));
        Assert.StartsWith($"'{IfcExporter.DoorWindowOpeningGlobalId(Id("2pfAHb2EL46hq_sMVbImEN"), 245423, IfcExporter.DoorWindowOpeningKind.Solid)}'",
            Args(model.Writer, model.Opening));
    }

    [Fact]
    public void UnknownCutLoopsOrACurtainHost_LeaveTheOpeningAsItIs()
    {
        foreach (Model model in new[] { DoorModel(cutLoopCount: null, writeDoor: true), DoorModel(1, true, "IFCCURTAINWALL") })
        {
            string before = Args(model.Writer, model.Opening);
            Assert.Empty(Apply(model));
            Assert.Equal(before, Args(model.Writer, model.Opening));
        }
    }

    [Fact]
    public void CutOpenings_AreNumberedByTheHostStepsThatOpenIt()
    {
        // Host 10 is cut by 20 (no opening here), 30 and 40, in that step order: 30 is
        // opening data 1 and 40 is 2.
        Guid hostId = Guid.NewGuid();
        var host = new Element { Id = 10, CategoryId = -2000032, SerializedExportId = hostId, CuttingElementIds = [20, 30, 40] };
        var elements = new[] { host }.Concat(new[] { 20, 30, 40 }.Select(id =>
            new Element { Id = id, CategoryId = -2000151, SerializedExportId = Guid.NewGuid() })).ToDictionary(e => e.Id);
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int slab = w.Add("IFCSLAB", $"'{IfcGuid.ToIfcGuid(hostId)}',#{owner},'Floor:Generic:10',$,$,$,$,'10',.FLOOR.");
        var openings = new Dictionary<int, int>();
        foreach (int tag in new[] { 40, 30 })
        {
            openings[tag] = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{slab},#{openings[tag]}");
        }
        var assigned = IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [slab] = 10 },
            new HashSet<string> { IfcGuid.ToIfcGuid(hostId) }, new HashSet<string>());
        Assert.Equal(2, assigned.Count);
        Assert.Equal($"'{IfcExporter.CutOpeningGlobalId(hostId, elements[30].SerializedExportId!.Value, 1, 1)}',#1,'Floor:Generic:10',$,'Opening',$,$,'30'",
            Args(w, openings[30]));
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, elements[40].SerializedExportId!.Value, 2, 1)}'",
            Args(w, openings[40]));
    }

    [Fact]
    public void SketchOpeningName_PutsTheHostTypeIdInPlaceOfTheHostId()
    {
        // Wall 198694 of type 428955 ("SIP 202mm Wall - conc clad").
        Assert.Equal("'Basic Wall:SIP 202mm Wall - conc clad:428955'",
            IfcExporter.SketchOpeningName("'Basic Wall:SIP 202mm Wall - conc clad:198694'", 198694, 428955));
        Assert.Null(IfcExporter.SketchOpeningName("'Basic Wall:SIP 202mm Wall - conc clad'", 198694, 428955));
    }

    [Fact]
    public void CutOpenings_TakeTheHostNameOrItsTypeNameForASketchLine()
    {
        Guid hostId = Guid.NewGuid();
        var host = new Element { Id = 10, CategoryId = -2000032, SerializedExportId = hostId, CuttingElementIds = [] };
        host.Parameters.Add(new Parameter
        {
            Id = -1002050,
            Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId,
            Value = 77,
        });
        var line = new Element { Id = 20, CategoryId = -2000045, SerializedExportId = Guid.NewGuid() };
        var family = new Element { Id = 30, CategoryId = -2000151, SerializedExportId = Guid.NewGuid() };
        var elements = new[] { host, line, family }.ToDictionary(e => e.Id);
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int slab = w.Add("IFCSLAB", $"'{IfcGuid.ToIfcGuid(hostId)}',#{owner},'Floor:Generic:10',$,$,$,$,'10',.FLOOR.");
        var openings = new Dictionary<int, int>();
        foreach (int tag in new[] { 20, 30 })
        {
            openings[tag] = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{slab},#{openings[tag]}");
        }
        // Neither opening has an index (the host's steps list no cutter); both are renamed.
        Assert.Empty(IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [slab] = 10 },
            new HashSet<string> { IfcGuid.ToIfcGuid(hostId) }, new HashSet<string>()));
        Assert.Contains(",'Floor:Generic:77',", Args(w, openings[20]));
        Assert.Contains(",'Floor:Generic:10',", Args(w, openings[30]));
    }

    private static Element Line(int id, double x0, double y0, double x1, double y1) => new()
    {
        Id = id, CategoryId = -2000045, SerializedExportId = Guid.NewGuid(),
        Location = new LocationCurve { CurveType = "Line", StartPoint = [x0, y0, 0], EndPoint = [x1, y1, 0] },
    };

    [Fact]
    public void SketchLoopKeys_KeyOnlySimpleClosedLoopsByTheirSmallestLine()
    {
        Element[] square = [Line(14, 0, 0, 1, 0), Line(11, 1, 0, 1, 1), Line(13, 1, 1, 0, 1), Line(12, 0, 1, 0, 0)];
        // Two triangles sharing a vertex: four line ends meet there, so neither is keyed.
        Element[] bowtie = [Line(21, 5, 0, 6, 0), Line(22, 6, 0, 5, 1), Line(23, 5, 1, 5, 0),
            Line(24, 5, 1, 4, 2), Line(25, 4, 2, 4, 1), Line(26, 4, 1, 5, 1)];
        Element[] open = [Line(31, 9, 0, 9, 1), Line(32, 9, 1, 9, 2)];
        var byId = square.Concat(bowtie).Concat(open).ToDictionary(e => e.Id);
        var keys = RvtIO.Parsing.ElementBuilder.SketchLoopKeys(byId.Keys, byId);
        Assert.Equal(new[] { 11, 12, 13, 14 }, keys.Keys.Order());
        Assert.All(keys.Values, key => Assert.Equal(11, key));
    }

    [Fact]
    public void SketchOpenings_FollowEveryOtherOpeningInLoopKeyOrder()
    {
        // Host 10 is cut by family 20 (opening data 1); its sketch openings follow by the
        // smallest line of their loop, not by Tag: Tag 33 (loop key 31) is 2, Tag 32 (key 35) is 3.
        Guid hostId = Guid.NewGuid();
        var host = new Element { Id = 10, CategoryId = -2000011, SerializedExportId = hostId, CuttingElementIds = [20] };
        var family = new Element { Id = 20, CategoryId = -2000151, SerializedExportId = Guid.NewGuid() };
        Element first = Line(33, 0, 0, 1, 0), second = Line(32, 2, 0, 3, 0);
        first.SketchLoopKey = 31;
        second.SketchLoopKey = 35;
        var elements = new[] { host, family, first, second }.ToDictionary(e => e.Id);
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wall = w.Add("IFCWALLSTANDARDCASE", $"'{IfcGuid.ToIfcGuid(hostId)}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
        var openings = new Dictionary<int, int>();
        foreach (int tag in new[] { 32, 20, 33 })
        {
            openings[tag] = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{wall},#{openings[tag]}");
        }
        var assigned = IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [wall] = 10 },
            new HashSet<string> { IfcGuid.ToIfcGuid(hostId) }, new HashSet<string>());
        Assert.Equal(3, assigned.Count);
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, family.SerializedExportId!.Value, 1, 1)}'", Args(w, openings[20]));
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, first.SerializedExportId!.Value, 2, 1)}'", Args(w, openings[33]));
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, second.SerializedExportId!.Value, 3, 1)}'", Args(w, openings[32]));

        // A sketch opening without a loop key leaves every sketch opening of the host alone.
        second.SketchLoopKey = null;
        var w2 = new StepWriter();
        owner = w2.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        wall = w2.Add("IFCWALLSTANDARDCASE", $"'{IfcGuid.ToIfcGuid(hostId)}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
        foreach (int tag in new[] { 32, 33 })
        {
            int opening = w2.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            w2.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{wall},#{opening}");
        }
        Assert.Empty(IfcExporter.ApplyCutOpeningIdentities(w2, elements, new Dictionary<int, int> { [wall] = 10 },
            new HashSet<string> { IfcGuid.ToIfcGuid(hostId) }, new HashSet<string>()));
    }

    [Fact]
    public void SketchOpenings_CurrentAreTheHostExtrusionOpeningsNumberedFromTheLastLoop()
    {
        // Host 10 has two sketch loops: Tag 33 (loop key 31) comes first, Tag 32 (key 35) last. The current profile cuts them from the extrusion
        // and numbers from the last one: Tag 32 is 1, Tag 33 is 2.
        Guid hostId = Guid.NewGuid();
        string hostGlobalId = IfcGuid.ToIfcGuid(hostId);
        var host = new Element { Id = 10, CategoryId = -2000011, SerializedExportId = hostId };
        Element first = Line(33, 0, 0, 1, 0), second = Line(32, 2, 0, 3, 0);
        first.SketchLoopKey = 31;
        second.SketchLoopKey = 35;
        var elements = new[] { host, first, second }.ToDictionary(e => e.Id);
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wall = w.Add("IFCWALLSTANDARDCASE", $"'{hostGlobalId}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
        var openings = new Dictionary<int, int>();
        var voids = new Dictionary<int, int>();
        foreach (int tag in new[] { 32, 33 })
        {
            openings[tag] = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            voids[tag] = w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{wall},#{openings[tag]}");
        }
        var assigned = IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [wall] = 10 },
            new HashSet<string> { hostGlobalId }, new HashSet<string>(), currentProfile: true);
        Assert.Equal(2, assigned.Count);
        foreach (var (tag, number) in new[] { (32, 1), (33, 2) })
        {
            string expected = IfcGuid.FromMd5($"IfcOpeningElement:{number}:{hostGlobalId}");
            Assert.Equal($"'{expected}',#{owner},'Basic Wall:W:10:{number}',$,'Opening',$,$,'{tag}'", Args(w, openings[tag]));
            Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcRelVoidsElement:{hostGlobalId}{expected}")}'", Args(w, voids[tag]));
        }
    }

    [Fact]
    public void CutOpeningGlobalId_CurrentSlabCuttingARetainingWall()
    {
        // Foundation slab 766598 cuts retaining wall 599951 through a CutPairGStep.
        Assert.Equal("1z3iaN59Ss5O3qn$GTfZao", IfcExporter.CutOpeningGlobalId(
            Id("3_T6_Ndub6XO_ef59N4DOF"), Id("38BWtPCUX0VgAKmmGnoKyb"), 1, 1));
    }

    [Fact]
    public void WallOpening_CurrentCountsItsSolidsFromZero_ButTheOtherCuttersFromOne()
    {
        // The first loop (wall openings) numbers solids from i = 0, 1, ...; the second loop (opening solids) uses an index incremented before use.
        Guid hostId = Guid.NewGuid();
        string hostGlobalId = IfcGuid.ToIfcGuid(hostId);
        var host = new Element
        {
            Id = 10, CategoryId = -2000011, SerializedExportId = hostId, CuttingElementIds = [30],
            CutOutSteps = [(4137, 30, true)],
        };
        var wallOpening = new Element
        {
            Id = 20, CategoryId = -2000011, Category = "OST_SWallRectOpening", SerializedExportId = Guid.NewGuid(),
        };
        var family = new Element { Id = 30, CategoryId = -2000151, SerializedExportId = Guid.NewGuid() };
        var elements = new[] { host, wallOpening, family }.ToDictionary(e => e.Id);
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wall = w.Add("IFCWALLSTANDARDCASE", $"'{hostGlobalId}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
        var openings = new Dictionary<int, int>();
        foreach (int tag in new[] { 20, 30 })
        {
            openings[tag] = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{wall},#{openings[tag]}");
        }

        IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [wall] = 10 },
            new HashSet<string> { hostGlobalId }, new HashSet<string>(), currentProfile: true);

        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, wallOpening.SerializedExportId!.Value, 1, 0)}'", Args(w, openings[20]));
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, family.SerializedExportId!.Value, 2, 1)}'", Args(w, openings[30]));
    }

    [Fact]
    public void CutPairOpenings_CurrentAreNumberedWithTheCutOutStepsInStepOrder()
    {
        // Host 10's steps: a door (20) cuts it by an inserted-element step, then wall 30 by a host-to-host pair
        // step. The current profile numbers both in step order; the pair cutter is not among CuttingElementIds, so the legacy profile would
        // leave it unnumbered.
        Guid hostId = Guid.NewGuid();
        string hostGlobalId = IfcGuid.ToIfcGuid(hostId);
        var host = new Element
        {
            Id = 10, CategoryId = -2000011, SerializedExportId = hostId, CuttingElementIds = [20],
            CutOutSteps = [(4137, 20, true), (977, 30, false)],
        };
        var door = new Element { Id = 20, CategoryId = -2000151, SerializedExportId = Guid.NewGuid() };
        var plaster = new Element { Id = 30, CategoryId = -2000011, SerializedExportId = Guid.NewGuid() };
        var elements = new[] { host, door, plaster }.ToDictionary(e => e.Id);

        StepWriter Build(out int doorOpening, out int plasterOpening, out int voidOfPlaster)
        {
            var w = new StepWriter();
            int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
            int wall = w.Add("IFCWALLSTANDARDCASE", $"'{hostGlobalId}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
            doorOpening = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName("od")}',#{owner},'x',$,'Opening',$,$,'20'");
            plasterOpening = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName("op")}',#{owner},'x',$,'Opening',$,$,'30'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName("vd")}',#{owner},$,$,#{wall},#{doorOpening}");
            voidOfPlaster = w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName("vp")}',#{owner},$,$,#{wall},#{plasterOpening}");
            return w;
        }

        var wCurrent = Build(out int doorOpening, out int plasterOpening, out int voidOfPlaster);
        var wallEntity = wCurrent.EntityAt(1).Id;
        var assigned = IfcExporter.ApplyCutOpeningIdentities(wCurrent, elements, new Dictionary<int, int> { [wallEntity] = 10 },
            new HashSet<string> { hostGlobalId }, new HashSet<string>(), currentProfile: true);
        Assert.Equal(2, assigned.Count);
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, door.SerializedExportId!.Value, 1, 1)}'", Args(wCurrent, doorOpening));
        string plasterGlobalId = IfcExporter.CutOpeningGlobalId(hostId, plaster.SerializedExportId!.Value, 2, 1);
        Assert.StartsWith($"'{plasterGlobalId}'", Args(wCurrent, plasterOpening));
        Assert.StartsWith($"'{IfcGuid.FromMd5($"IfcRelVoidsElement:{hostGlobalId}{plasterGlobalId}")}'", Args(wCurrent, voidOfPlaster));

        // The legacy profile numbers only the cutters of the inserted-element steps: the pair cutter keeps its opening as written.
        var wLegacy = Build(out doorOpening, out plasterOpening, out _);
        var assignedLegacy = IfcExporter.ApplyCutOpeningIdentities(wLegacy, elements, new Dictionary<int, int> { [wLegacy.EntityAt(1).Id] = 10 },
            new HashSet<string> { hostGlobalId }, new HashSet<string>());
        Assert.Single(assignedLegacy);
        Assert.StartsWith($"'{IfcGuid.FromName("op")}'", Args(wLegacy, plasterOpening));
    }

    [Fact]
    public void CutPairOpenings_CurrentNumberACutterItCouldNotWrite()
    {
        // Host 10's first step is a curtain wall (20) with no decoded extent: it is opened as number 1, so the pair cutter
        // (30) after it is number 2 although this export wrote only its opening.
        Guid hostId = Guid.NewGuid();
        string hostGlobalId = IfcGuid.ToIfcGuid(hostId);
        var host = new Element
        {
            Id = 10, CategoryId = -2000011, SerializedExportId = hostId, CutOutSteps = [(4155, 20, true), (977, 30, false)],
        };
        var curtain = new Element { Id = 20, CategoryId = -2000011, SerializedExportId = Guid.NewGuid() };
        var plaster = new Element { Id = 30, CategoryId = -2000011, SerializedExportId = Guid.NewGuid() };
        var elements = new[] { host, curtain, plaster }.ToDictionary(e => e.Id);

        int Number(IReadOnlyDictionary<int, HashSet<int>>? unbuilt)
        {
            var w = new StepWriter();
            int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
            int wall = w.Add("IFCWALLSTANDARDCASE", $"'{hostGlobalId}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
            int opening = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName("op")}',#{owner},'x',$,'Opening',$,$,'30'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName("vp")}',#{owner},$,$,#{wall},#{opening}");
            IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [wall] = 10 },
                new HashSet<string> { hostGlobalId }, new HashSet<string>(), currentProfile: true, unbuilt);
            for (int number = 1; number <= 3; number++)
                if (Args(w, opening).StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, plaster.SerializedExportId!.Value, number, 1)}'"))
                    return number;
            return 0;
        }

        Assert.Equal(1, Number(null));
        Assert.Equal(2, Number(new Dictionary<int, HashSet<int>> { [10] = [20] }));
    }

    [Fact]
    public void CutPairOpenings_CurrentOfAHostWithOnlyCutOutSteps_AreNumberedToo()
    {
        // A host whose steps were read by the layout search has CutOutSteps but no CuttingElementIds (the legacy profile data).
        Guid hostId = Guid.NewGuid();
        string hostGlobalId = IfcGuid.ToIfcGuid(hostId);
        var host = new Element
        {
            Id = 10, CategoryId = -2000011, SerializedExportId = hostId, CuttingElementIds = null,
            CutOutSteps = [(4155, 20, true), (977, 30, false)],
        };
        var shadow = new Element { Id = 20, CategoryId = -2000151, SerializedExportId = Guid.NewGuid() };
        var plaster = new Element { Id = 30, CategoryId = -2000011, SerializedExportId = Guid.NewGuid() };
        var elements = new[] { host, shadow, plaster }.ToDictionary(e => e.Id);
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.ADDED.,$,$,$,0");
        int wall = w.Add("IFCWALLSTANDARDCASE", $"'{hostGlobalId}',#{owner},'Basic Wall:W:10',$,$,$,$,'10'");
        var openings = new Dictionary<int, int>();
        foreach (int tag in new[] { 20, 30 })
        {
            openings[tag] = w.Add("IFCOPENINGELEMENT", $"'{IfcGuid.FromName($"o{tag}")}',#{owner},'x',$,'Opening',$,$,'{tag}'");
            w.Add("IFCRELVOIDSELEMENT", $"'{IfcGuid.FromName($"v{tag}")}',#{owner},$,$,#{wall},#{openings[tag]}");
        }

        IfcExporter.ApplyCutOpeningIdentities(w, elements, new Dictionary<int, int> { [wall] = 10 },
            new HashSet<string> { hostGlobalId }, new HashSet<string>(), currentProfile: true);

        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, shadow.SerializedExportId!.Value, 1, 1)}'", Args(w, openings[20]));
        Assert.StartsWith($"'{IfcExporter.CutOpeningGlobalId(hostId, plaster.SerializedExportId!.Value, 2, 1)}'", Args(w, openings[30]));
    }

    [Fact]
    public void DoorWindowCutoutExtrusion_NeedsAWrittenHostedInstanceWhoseSymbolHasACutLoop()
    {
        var symbol = new Element { Id = 907609, CategoryId = DoorsCategoryId, SerializedCutLoopCount = 1 };
        var door = Typed(906937, DoorsCategoryId, Guid.NewGuid(), typeId: 907609, hostId: 198749);
        var generic = Typed(906938, -2000151, Guid.NewGuid(), typeId: 907609, hostId: 198749);
        var elements = new[] { symbol, door, generic }.ToDictionary(e => e.Id);

        Assert.True(IfcExporter.IsDoorWindowCutoutExtrusionCurrent(door, 5, 198749, elements));
        // Not written as a product, hosted by another wall, cut by a generic model, symbol without a loop or unknown.
        Assert.False(IfcExporter.IsDoorWindowCutoutExtrusionCurrent(door, 0, 198749, elements));
        Assert.False(IfcExporter.IsDoorWindowCutoutExtrusionCurrent(door, 5, 198750, elements));
        Assert.False(IfcExporter.IsDoorWindowCutoutExtrusionCurrent(generic, 5, 198749, elements));
        symbol.SerializedCutLoopCount = 0;
        Assert.False(IfcExporter.IsDoorWindowCutoutExtrusionCurrent(door, 5, 198749, elements));
        symbol.SerializedCutLoopCount = null;
        Assert.False(IfcExporter.IsDoorWindowCutoutExtrusionCurrent(door, 5, 198749, elements));
    }

    [Fact]
    public void SketchLoopKeys_ComeFromTheLinesOwnSketchWhenABroaderOwnerBranches()
    {
        // Two sketches with the same square profile (walls on two levels): an element that
        // references both makes every corner branch; each sketch's own reference set closes.
        Element[] first = [Line(14, 0, 0, 1, 0), Line(11, 1, 0, 1, 1), Line(13, 1, 1, 0, 1), Line(12, 0, 1, 0, 0)];
        Element[] second = [Line(24, 0, 0, 1, 0), Line(21, 1, 0, 1, 1), Line(23, 1, 1, 0, 1), Line(22, 0, 1, 0, 0)];
        var firstSketch = new Element { Id = 100, References = first.Select(e => e.Id).ToList() };
        var secondSketch = new Element { Id = 200, References = second.Select(e => e.Id).ToList() };
        var broad = new Element { Id = 50, References = first.Concat(second).Select(e => e.Id).ToList() };
        var info = new RevitFileInfo();
        info.Elements.AddRange(first.Concat(second).Append(firstSketch).Append(secondSketch).Append(broad));
        var state = new RvtIO.Internal.ParsingState();
        foreach (Element line in first.Concat(second))
            state.SketchCurves[(uint)line.Id] = line.Location!;
        RvtIO.Parsing.ElementBuilder.BuildSketchTopologyForTests(info, state);
        Assert.All(first, line => Assert.Equal(11, line.SketchLoopKey));
        Assert.All(second, line => Assert.Equal(21, line.SketchLoopKey));
    }
}
