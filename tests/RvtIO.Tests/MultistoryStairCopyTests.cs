using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class MultistoryStairCopyTests
{
    // GlobalIds of a multistory stair of a Revit 2022 sample and of its components, each with the one of its first level copy.
    private const string Stair = "2GoXeF051238RZ7lkh7z96", StairCopy = "0trU_u6UK033dUbpMYYcau";
    private const string Run = "2GoXeF051238RZ7lkh7z8p", RunCopy = "0jK_Dy$ByysSoBgGt8T2dD";
    private const string Landing = "2GoXeF051238RZ7lkh7zBj", LandingCopy = "3MsehpiL89Ww6QIBjPtL1B";
    private const string Stringer = "2GoXeF051238RZ7lkh7z8z", StringerCopy = "24BGX7qGwXp3JVlvmlxxFY";
    private const string Railing = "2GoXeF051238RZ7lkh7zB7", RailingCopy = "2ivv1EMV4NfvqFCEDV2ieU";

    [Fact]
    public void EachCopyLevelGetsAStairWithCopiesOfTheComponents()
    {
        var (writer, elements, products, contents, storeys, placements) = Fixture();
        int before = writer.EntityCount;
        StairAggregation.Apply(writer, elements, products, contents, 99, true, storeys, placements);
        var added = Enumerable.Range(before, writer.EntityCount - before).Select(writer.EntityAt).ToList();
        var stairs = added.Where(entity => entity.Type == "IFCSTAIR").ToList();
        Assert.Equal(2, stairs.Count);
        var first = IfcExporter.SplitStepArguments(stairs[0].Args);
        var second = IfcExporter.SplitStepArguments(stairs[1].Args);
        Assert.Equal(($"'{StairCopy}'", "'stair : 2'", "$", "'100'"), (first[0], first[2], first[6], first[7]));
        Assert.Equal((StepWriter.Str(IfcGuid.FromMd5(Stair + "Sub-element:IfcStair Copy: 3")), "'stair : 3'"), (second[0], second[2]));
        // The stair stands on the placement the storeys hang from. Its copies keep its axis on a placement that is
        // as far below the storey of the copy level as that placement is below the storey of the stair.
        string firstOrigin = Placement(first[5]).Split(',')[0], secondOrigin = Placement(second[5]).Split(',')[0];
        Assert.Equal((firstOrigin + ",#903", secondOrigin + ",#903"), (Placement(first[5]), Placement(second[5])));
        Assert.Equal((("#7", "(0.,0.,-3800.)"), ("#10", "(0.,0.,-3800.)")), (Origin(firstOrigin), Origin(secondOrigin)));
        Assert.Equal([stairs[0].Id], contents[21]);
        Assert.Equal([stairs[1].Id], contents[22]);
        Assert.Equal([15], contents[20]);

        var copies = added.Where(entity => entity.Type is "IFCSTAIRFLIGHT" or "IFCSLAB" or "IFCMEMBER" or "IFCRAILING")
            .Select(entity => (entity.Id, entity.Type, Args: IfcExporter.SplitStepArguments(entity.Args))).ToList();
        Assert.Equal(8, copies.Count);
        Assert.Equal([$"'{RunCopy}'", $"'{LandingCopy}'", $"'{StringerCopy}'", $"'{RailingCopy}'"], copies.Take(4).Select(copy => copy.Args[0]));
        Assert.Equal(["'stair Run 1 : 2'", "'stair Landing 1 : 2'", "'stair Stringer 1 : 2'", "'railing'"], copies.Take(4).Select(copy => copy.Args[2]));
        Assert.Equal(["'stair Run 1 : 2 : 3'", "'stair Landing 1 : 2 : 3'", "'stair Stringer 1 : 2 : 3'", "'railing'"], copies.Skip(4).Select(copy => copy.Args[2]));
        Assert.Equal([StepWriter.Str(IfcGuid.FromMd5(Run + "Sub-element:IfcStairFlight: 1")), StepWriter.Str(IfcGuid.FromMd5(Landing + "Sub-element:IfcSlab: 1")),
            StepWriter.Str(IfcGuid.FromMd5(Stringer + "Sub-element:IfcMember: 1")), StepWriter.Str(IfcGuid.FromMd5("IfcRailing:2:" + Railing))],
            copies.Skip(4).Select(copy => copy.Args[0]));
        Assert.Equal(["'200'", "'250'", "'300'", "'400'"], copies.Take(4).Select(copy => copy.Args[7]));
        // A component placed beside the stair follows the placement of the copy, one placed under the stair follows the stair copy.
        Assert.Equal([firstOrigin + ",#904", firstOrigin + ",#907", $"{first[5]},#905", firstOrigin + ",#906"],
            copies.Take(4).Select(copy => Placement(copy.Args[5])));
        Assert.Equal([secondOrigin + ",#904", secondOrigin + ",#907", $"{second[5]},#905", secondOrigin + ",#906"],
            copies.Skip(4).Select(copy => Placement(copy.Args[5])));
        // Every copy has its own shape and representation over the items of the original.
        var shapes = copies.Select(copy => copy.Args[6]).ToList();
        Assert.Equal(8, shapes.Distinct().Count());
        Assert.DoesNotContain("#14", shapes);
        foreach (string shape in shapes)
        {
            var shapeArgs = IfcExporter.SplitStepArguments(added.Single(entity => "#" + entity.Id == shape).Args);
            var representation = added.Single(entity => $"(#{entity.Id})" == shapeArgs[2]);
            Assert.Equal(("IFCSHAPEREPRESENTATION", "#999,'Body','Brep',(#12)"), (representation.Type, representation.Args));
        }

        var relations = added.Where(entity => entity.Type == "IFCRELAGGREGATES").Select(entity => IfcExporter.SplitStepArguments(entity.Args)).ToList();
        Assert.Equal(3, relations.Count);
        Assert.Equal(("#15", "(#17,#19,#21,#23)"), (relations[0][4], relations[0][5]));
        Assert.Equal(("#" + stairs[0].Id, StepWriter.List(copies.Take(4).Select(copy => "#" + copy.Id))), (relations[1][4], relations[1][5]));
        Assert.Equal(("#" + stairs[1].Id, StepWriter.List(copies.Skip(4).Select(copy => "#" + copy.Id))), (relations[2][4], relations[2][5]));
        var ids = Enumerable.Range(0, writer.EntityCount).Select(writer.EntityAt)
            .Where(entity => entity.Type.StartsWith("IFCSTAIR") || entity.Type is "IFCSLAB" or "IFCMEMBER" or "IFCRAILING" or "IFCRELAGGREGATES")
            .Select(entity => IfcExporter.SplitStepArguments(entity.Args)[0]).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        string Placement(string reference) => added.Single(entity => "#" + entity.Id == reference && entity.Type == "IFCLOCALPLACEMENT").Args;
        (string, string) Origin(string reference)
        {
            var args = Placement(reference).Split(',');
            var axis = added.Single(entity => "#" + entity.Id == args[1] && entity.Type == "IFCAXIS2PLACEMENT3D").Args.Split(',');
            Assert.Equal(("$", "$"), (axis[1], axis[2]));
            return (args[0], added.Single(entity => "#" + entity.Id == axis[0] && entity.Type == "IFCCARTESIANPOINT").Args);
        }
    }

    [Theory]
    [InlineData("no_levels")] [InlineData("legacy_profile")] [InlineData("no_storeys")] [InlineData("level_is_not_a_storey")]
    [InlineData("stair_not_in_a_storey")] [InlineData("storey_is_turned")] [InlineData("storey_hangs_elsewhere")]
    public void NothingIsRepeatedWithoutAResolvedCopyLevel(string scenario)
    {
        var (writer, elements, products, contents, storeys, placements) = Fixture();
        switch (scenario)
        {
            case "no_levels": elements[0].SerializedStairCopyLevelIds = []; break;
            case "level_is_not_a_storey": elements[0].SerializedStairCopyLevelIds = [77]; break;
            case "stair_not_in_a_storey": contents[20].Remove(15); break;
            case "storey_is_turned": writer.ReplaceArgs(2, "#2,#901,$"); break;
            case "storey_hangs_elsewhere": writer.ReplaceArgs(3, "#10,#3"); break;
        }
        int before = writer.EntityCount;
        StairAggregation.Apply(writer, elements, products, contents, 99, scenario != "legacy_profile",
            scenario == "no_storeys" ? null : storeys, scenario == "no_storeys" ? null : placements);
        // Only the relation of the stair itself is written.
        Assert.Equal(before + 1, writer.EntityCount);
        Assert.Equal("IFCRELAGGREGATES", writer.EntityAt(before).Type);
        Assert.Empty(contents[21]); Assert.Empty(contents[22]);
    }

    [Fact]
    public void AComponentPlacedElsewhereIsNotRepeated()
    {
        var (writer, elements, products, contents, storeys, placements) = Fixture();
        writer.ReplaceArgs(15, "#10,#904");
        int before = writer.EntityCount;
        StairAggregation.Apply(writer, elements, products, contents, 99, true, storeys, placements);
        var added = Enumerable.Range(before, writer.EntityCount - before).Select(writer.EntityAt).ToList();
        Assert.Equal(2, added.Count(entity => entity.Type == "IFCSTAIR"));
        Assert.DoesNotContain(added, entity => entity.Type == "IFCSTAIRFLIGHT");
        Assert.Equal(2, added.Count(entity => entity.Type == "IFCMEMBER"));
    }

    [Fact]
    public void ASupportCopyTakesItsGlobalIdFromTheSupportAtItsPlaceInTheWholeSet()
    {
        var (writer, elements, products, contents, storeys, placements) = Fixture();
        // Supports 300, 305 and 310 of which 305 is not exported: the copy of the second exported one is named by 305.
        var notExported = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        string last = IfcGuid.FromMd5("310");
        writer.Add("IFCLOCALPLACEMENT", "#11,#908");                                              // 24
        writer.Add("IFCMEMBER", $"'{last}',#99,'stair Stringer 3',$,'type',#24,#14,'310'");       // 25
        elements[0].SerializedStairCore = [.. elements[0].SerializedStairCore!, new(305, 3727), new(310, 3727)];
        elements.Add(new() { Id = 305, SerializedOwnElementClassId = 3727, SerializedOwningElementId = 100, SerializedExportId = notExported });
        elements.Add(new() { Id = 310, SerializedOwnElementClassId = 3727, SerializedOwningElementId = 100 });
        products[310] = (25, 0, "IFCMEMBER");
        contents[20].Add(25);
        int before = writer.EntityCount;
        StairAggregation.Apply(writer, elements, products, contents, 99, true, storeys, placements);
        var copies = Enumerable.Range(before, writer.EntityCount - before).Select(writer.EntityAt)
            .Where(entity => entity.Type == "IFCMEMBER").Select(entity => IfcExporter.SplitStepArguments(entity.Args)).ToList();
        string seed = IfcGuid.ToIfcGuid(notExported);
        Assert.Equal([$"'{StringerCopy}'", StepWriter.Str(IfcGuid.FromMd5(seed + "Sub-element:IfcMember: 0")),
            StepWriter.Str(IfcGuid.FromMd5(Stringer + "Sub-element:IfcMember: 1")), StepWriter.Str(IfcGuid.FromMd5(seed + "Sub-element:IfcMember: 1"))],
            copies.Select(copy => copy[0]));
        // The name and the Tag stay those of the support the copy repeats.
        Assert.Equal(["'300'", "'310'", "'300'", "'310'"], copies.Select(copy => copy[7]));
        Assert.Equal(["'stair Stringer 1 : 2'", "'stair Stringer 3 : 2'", "'stair Stringer 1 : 2 : 3'", "'stair Stringer 3 : 2 : 3'"],
            copies.Select(copy => copy[2]));
    }

    [SkippableFact]
    public void SampleMultistoryStairsCarryTheirCopyLevel()
    {
        string path = TestFiles.Find("rac_advanced_sample_project.rvt");
        var info = new RvtFileReader(new RvtReaderOptions { R22Layout = true, SystemFamilyNameLocale = "ENU" }).Read(path);
        var level = Assert.Single(info.Elements, element => element.Id == 694);
        Assert.Equal("02 - Floor", level.Name);
        var repeated = info.Elements.Where(element => element.SerializedStairCopyLevelIds is { Length: > 0 }).OrderBy(element => element.Id).ToList();
        Assert.Equal([354339, 355798, 357340, 358313, 360203], repeated.Select(element => element.Id));
        Assert.All(repeated, element => Assert.Equal([694], element.SerializedStairCopyLevelIds));
    }

    private static (StepWriter, List<Element>, Dictionary<int, (int, int, string)>, Dictionary<int, List<int>>,
        Dictionary<int, int>, Dictionary<int, int>) Fixture()
    {
        var writer = new StepWriter();
        writer.Add("IFCLOCALPLACEMENT", "$,#900");                                               // 1: the storeys hang from it
        foreach (string height in new[] { "3800.", "7600.", "11400." })                           // 4: storey of the stair
        {                                                                                         // 7, 10: storeys of the copy levels
            int point = writer.Add("IFCCARTESIANPOINT", $"(0.,0.,{height})");
            writer.Add("IFCLOCALPLACEMENT", $"#1,#{writer.Add("IFCAXIS2PLACEMENT3D", $"#{point},$,$")}");
        }
        writer.Add("IFCLOCALPLACEMENT", "#1,#903");                                              // 11: stair
        writer.Add("IFCFACETEDBREP", "#999");
        writer.Add("IFCSHAPEREPRESENTATION", "#999,'Body','Brep',(#12)");
        writer.Add("IFCPRODUCTDEFINITIONSHAPE", "$,$,(#13)");
        writer.Add("IFCSTAIR", $"'{Stair}',#99,'stair',$,'type',#11,#14,'100',.NOTDEFINED.");     // 15
        writer.Add("IFCLOCALPLACEMENT", "#1,#904");
        writer.Add("IFCSTAIRFLIGHT", $"'{Run}',#99,'stair Run 1',$,'type',#16,#14,'200'");        // 17
        writer.Add("IFCLOCALPLACEMENT", "#1,#907");
        writer.Add("IFCSLAB", $"'{Landing}',#99,'stair Landing 1',$,'type',#18,#14,'250'");       // 19
        writer.Add("IFCLOCALPLACEMENT", "#11,#905");
        writer.Add("IFCMEMBER", $"'{Stringer}',#99,'stair Stringer 1',$,'type',#20,#14,'300'");   // 21
        writer.Add("IFCLOCALPLACEMENT", "#1,#906");
        writer.Add("IFCRAILING", $"'{Railing}',#99,'railing',$,'type',#22,#14,'400',$");          // 23
        List<Element> elements = [
            new() { Id = 100, CategoryId = -2000120, SerializedOwnElementClassId = 3681,
                SerializedStairCore = [new(200, 3708), new(250, 3686), new(300, 3727)], SerializedStairCopyLevelIds = [11, 12] },
            new() { Id = 200, SerializedOwnElementClassId = 3708, SerializedOwningElementId = 100 },
            new() { Id = 250, SerializedOwnElementClassId = 3686, SerializedOwningElementId = 100 },
            new() { Id = 300, SerializedOwnElementClassId = 3727, SerializedOwningElementId = 100 },
            new() { Id = 400, SerializedRailingStairHostId = 100, SerializedOwningElementId = -1, SerializedOwnElementClassId = 485, NativeRootClassName = "BaseRailing" }];
        Dictionary<int, (int, int, string)> products = new()
        {
            [100] = (15, 0, "IFCSTAIR"), [200] = (17, 0, "IFCSTAIRFLIGHT"), [250] = (19, 0, "IFCSLAB"),
            [300] = (21, 0, "IFCMEMBER"), [400] = (23, 0, "IFCRAILING"),
        };
        Dictionary<int, List<int>> contents = new() { [20] = [15, 17, 19, 21, 23], [21] = [], [22] = [] };
        Dictionary<int, int> storeys = new() { [10] = 20, [11] = 21, [12] = 22 };
        Dictionary<int, int> placements = new() { [20] = 4, [21] = 7, [22] = 10 };
        return (writer, elements, products, contents, storeys, placements);
    }
}
