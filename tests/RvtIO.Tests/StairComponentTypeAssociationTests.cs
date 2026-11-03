using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// The type of a stair run or landing is the one its serialized TypeId names, and the class of such an element is counted once per stored
/// block. Both are checked on synthetic elements: no sample file, no element name, nothing taken from the exporter's output.
/// </summary>
public class StairComponentTypeAssociationTests
{
    private const int RunsCategory = -2000919, LandingsCategory = -2000920;

    private static (Element Run, Element Type, Dictionary<int, Element> ById, Dictionary<int, int> Counts) RunWithType()
    {
        var type = new Element { Id = 171672, CategoryId = RunsCategory, Name = "190mm Depth",
            ValidatedStairComponentTypeName = "190mm Depth", ValidatedSystemFamilyName = "Monolithic Run" };
        // a run has no ELEM_TYPE_PARAM: only the serialized TypeId names its type
        var run = new Element { Id = 513309, CategoryId = RunsCategory, Category = "OST_StairsRuns", TypeId = type.Id };
        var byId = new Dictionary<int, Element> { [run.Id] = run, [type.Id] = type };
        return (run, type, byId, byId.Keys.ToDictionary(id => id, _ => 1));
    }

    [Fact]
    public void ARunNamesItsTypeThroughTheSerializedTypeId()
    {
        var (run, type, byId, counts) = RunWithType();
        Assert.Same(type, IfcExporter.ResolveStairComponentType(run, "IFCSTAIRFLIGHT", byId, counts));
        Assert.Equal("Monolithic Run:190mm Depth", IfcExporter.ResolveProductFamilyAndTypeName(run, "IFCSTAIRFLIGHT", byId, counts, currentProfile: true));
    }

    [Fact]
    public void WithoutAFamilyNameTheTypeNameStillReplacesTheElementPlaceholder()
    {
        var (run, type, byId, counts) = RunWithType();
        type.ValidatedSystemFamilyName = null;
        Assert.Equal("OST_StairsRuns:190mm Depth", IfcExporter.ResolveProductFamilyAndTypeName(run, "IFCSTAIRFLIGHT", byId, counts, currentProfile: true));
    }

    [Fact]
    public void TheOlderDialectKeepsTheCategoryPlaceholder()
    {
        var (run, _, byId, counts) = RunWithType();
        Assert.Equal("OST_StairsRuns:Element 513309", IfcExporter.ResolveProductFamilyAndTypeName(run, "IFCSTAIRFLIGHT", byId, counts));
    }

    private static Parameter StringParameter(string name, string value, int id) => new()
    {
        Id = id,
        Definition = new Definition { Id = id, Name = name },
        StorageType = StorageType.String,
        Value = value,
    };

    // The Reference of a run reads the explicit parameter of the type its TypeId names, ahead of the type name.
    [Theory]
    [InlineData("AUTHORED-REFERENCE", "AUTHORED-REFERENCE")]
    [InlineData("{Expr}", null)]   // an unsupported expression blocks the substitution instead of being replaced by the name
    [InlineData(null, "190mm Depth")]
    public void TheReferenceOfARunReadsTheExplicitParameterOfItsResolvedType(string? explicitValue, string? expected)
    {
        var (run, type, byId, counts) = RunWithType();
        if (explicitValue != null) type.Parameters.Add(StringParameter("Reference[Type]", explicitValue, 20));
        Element? componentType = IfcExporter.ResolveStairComponentType(run, "IFCSTAIRFLIGHT", byId, counts);
        Assert.Same(type, componentType);
        Assert.Equal(expected, IfcExporter.ResolveIfcReferenceValue(run, byId, type.ValidatedStairComponentTypeName, componentType));
    }

    // The exported flight carries the same Reference in its property sets; an unsupported expression leaves it out.
    [Theory]
    [InlineData("AUTHORED-REFERENCE", "AUTHORED-REFERENCE")]
    [InlineData("{Expr}", null)]
    [InlineData(null, "190mm Depth")]
    public void TheExportedFlightReferenceFollowsTheExplicitTypeParameter(string? explicitValue, string? expected)
    {
        var (_, type, _, _) = RunWithType();
        if (explicitValue != null) type.Parameters.Add(StringParameter("Reference[Type]", explicitValue, 20));
        Element run = IfcExporterStairOccurrenceCommonPropertySetTests.Product(513309, type.Id, RunsCategory, "OST_StairsRuns", null);
        var info = new RevitFileInfo();
        info.Elements.AddRange([run, type]);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-stair-ref-{Guid.NewGuid():N}.ifc");
        string ifc;
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            ifc = File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        Assert.Contains("IFCSTAIRFLIGHT(", ifc);
        var references = Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('((?:''|[^'])*)'\)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(expected == null ? [] : [expected], references);
    }

    // The Length surrogate of a run is decided by the family the run had before its type was resolved through TypeId, and by the class root
    // count it had before: resolving the type must not move it.
    [Theory]
    [InlineData("category_only", true, true)]
    [InlineData("resolved_assembled_type", true, true)]
    [InlineData("resolved_monolithic_type", true, true)]
    [InlineData("legacy_assembled_family", true, false)]
    [InlineData("legacy_monolithic_family", true, true)]
    [InlineData("category_only", false, false)]
    public void TheLengthSurrogateOfARunDoesNotFollowTheResolvedType(string mode, bool rootSeededOnce, bool expected)
    {
        var (run, type, byId, counts) = RunWithType();
        run.OwnClassRootSeededOnce = rootSeededOnce;
        switch (mode)
        {
            case "category_only": type.ValidatedSystemFamilyName = null; break;
            case "resolved_assembled_type": type.ValidatedSystemFamilyName = "Non-Monolithic Run"; break;
            case "resolved_monolithic_type": break;
            case "legacy_assembled_family": run.FamilyName = "Non-Monolithic Run"; break;
            case "legacy_monolithic_family": run.FamilyName = "Monolithic Run"; break;
        }
        Assert.Equal(expected, IfcExporter.IsRunLengthRuleCurrent(run, "IFCSTAIRFLIGHT", byId, counts));
    }

    [Fact]
    public void OnlyARunTakesTheLengthSurrogate()
    {
        var (run, _, byId, counts) = RunWithType();
        run.OwnClassRootSeededOnce = true;
        Assert.False(IfcExporter.IsRunLengthRuleCurrent(run, "IFCSLAB", byId, counts));
    }

    [Fact]
    public void ALandingNamesItsTypeAloneAndIgnoresASiblingInTheTypeParameter()
    {
        var type = new Element { Id = 171671, CategoryId = LandingsCategory, ValidatedStairComponentTypeName = "220mm Thickness" };
        var sibling = new Element { Id = 725395, CategoryId = LandingsCategory, TypeId = 725394 };
        var landing = new Element { Id = 725396, CategoryId = LandingsCategory, Category = "OST_StairsLandings", TypeId = type.Id,
            Parameters = [new Parameter { Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = sibling.Id }] };
        var byId = new[] { type, sibling, landing }.ToDictionary(e => e.Id);
        var counts = byId.Keys.ToDictionary(id => id, _ => 1);
        Assert.Same(type, IfcExporter.ResolveStairComponentType(landing, "IFCSLAB", byId, counts));
        Assert.Equal("220mm Thickness", IfcExporter.ResolveProductFamilyAndTypeName(landing, "IFCSLAB", byId, counts, currentProfile: true));
    }

    [Theory]
    [InlineData("no_type_id")] [InlineData("own_id")] [InlineData("other_category")] [InlineData("type_has_a_type")]
    [InlineData("unproven_name")] [InlineData("duplicate_type")] [InlineData("duplicate_run")] [InlineData("block_only_type")]
    [InlineData("block_only_run")] [InlineData("type_not_materialized")] [InlineData("not_a_run_product")] [InlineData("not_a_stair_category")]
    public void AnythingElseIsNotATypeAssociation(string mode)
    {
        var (run, type, byId, counts) = RunWithType();
        string ifcType = "IFCSTAIRFLIGHT";
        switch (mode)
        {
            case "no_type_id": run.TypeId = -1; break;
            case "own_id": run.TypeId = run.Id; break;
            case "other_category": type.CategoryId = LandingsCategory; break;
            case "type_has_a_type": type.TypeId = 99; break;
            case "unproven_name": type.ValidatedStairComponentTypeName = null; break;
            case "duplicate_type": counts[type.Id] = 2; break;
            case "duplicate_run": counts[run.Id] = 2; break;
            case "block_only_type": type.IsBlockOnly = true; break;
            case "block_only_run": run.IsBlockOnly = true; break;
            case "type_not_materialized": byId.Remove(type.Id); break;
            case "not_a_run_product": ifcType = "IFCSLAB"; break;
            case "not_a_stair_category": run.CategoryId = -2000011; type.CategoryId = -2000011; break;
        }
        Assert.Null(IfcExporter.ResolveStairComponentType(run, ifcType, byId, counts));
    }

    // ---- the class of an element, counted once per stored block ----

    private static (List<Element>, ParsingState) SeededStair(int walkedBlocks)
    {
        var state = new ParsingState { ElemRecOwningElementIds = new() { [100] = -1, [200] = 100, [300] = 100 } };
        state.StairMembershipRoots[100] = new(1, new(100, [200, 300], [], [], -1));
        // a file with its own class schema: the schema index seeded each root once and the stream walk counted the same block again
        foreach (var (id, cls) in new[] { (100u, (ushort)3681), (200u, (ushort)3708), (300u, (ushort)3686) })
        {
            state.OwnedElementClassRoots[id] = new(2, null);
            state.WalkedElementClassRoots[id] = new(walkedBlocks, walkedBlocks == 1 ? cls : null);
        }
        return ([new() { Id = 100, CategoryId = -2000120 }, new() { Id = 200, CategoryId = RunsCategory },
            new() { Id = 300, CategoryId = LandingsCategory }], state);
    }

    [Fact]
    public void ABlockTheSchemaIndexAlreadyCountedIsNotADuplicate()
    {
        var (elements, state) = SeededStair(walkedBlocks: 1);
        StairComponentSource.Apply(elements, state);
        Assert.Equal((ushort)3681, elements[0].SerializedOwnElementClassId);
        Assert.Equal((ushort)3708, elements[1].SerializedOwnElementClassId);
        Assert.Equal([new StairComponentSource.CoreComponent(200, 3708), new(300, 3686)], elements[0].SerializedStairCore);
        // the seeded table counts these roots twice: the run is not one the Length surrogate was calibrated on
        Assert.All(elements, element => Assert.False(element.OwnClassRootSeededOnce));
    }

    [Fact]
    public void TwoStoredBlocksOfOneElementStayAmbiguous()
    {
        var (elements, state) = SeededStair(walkedBlocks: 2);
        StairComponentSource.Apply(elements, state);
        Assert.All(elements, element => Assert.Null(element.SerializedOwnElementClassId));
        Assert.Null(elements[0].SerializedStairCore);
    }

    [Fact]
    public void TheSeededRootStandsWhereTheStreamWalkSawNothing()
    {
        // no rewrite into the Revit 2022 layout: the walk never runs and the schema index is the only evidence
        var (elements, state) = SeededStair(walkedBlocks: 1);
        state.WalkedElementClassRoots.Clear();
        foreach (var (id, cls) in new[] { (100u, (ushort)3681), (200u, (ushort)3708), (300u, (ushort)3686) })
            state.OwnedElementClassRoots[id] = new(1, cls);
        StairComponentSource.Apply(elements, state);
        Assert.Equal((ushort)3708, elements[1].SerializedOwnElementClassId);
        Assert.NotNull(elements[0].SerializedStairCore);
        Assert.All(elements, element => Assert.True(element.OwnClassRootSeededOnce));
    }

    [Fact]
    public void RecordCountsTheWalkedBlocksApartFromTheSeededOnes()
    {
        byte[] block = StairRunInputTests.Build().Bytes;
        uint size = BitConverter.ToUInt32(block, 8);
        var state = new ParsingState { ElemRecOwningElementIds = new() { [42] = 43 } };
        state.OwnedElementClassRoots[42] = new(1, 3708);   // seeded by the schema index
        StairComponentSource.Record(state, block, 0, size, 3708, 42);
        Assert.Equal(2, state.OwnedElementClassRoots[42].BlockCount);   // the established count, which other readers keep using
        Assert.Equal(1, state.WalkedElementClassRoots[42].BlockCount);
        Assert.Equal((ushort)3708, state.StairClassRoot(42)!.ClassId);
        block[^1] ^= 1;
        StairComponentSource.Record(state, block, 0, size, 3708, 42);
        Assert.Equal(2, state.WalkedElementClassRoots[42].BlockCount);
        Assert.Null(state.StairClassRoot(42)!.ClassId);
    }
}
