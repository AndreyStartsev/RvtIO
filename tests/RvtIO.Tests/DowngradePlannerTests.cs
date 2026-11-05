using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

/// <summary>The downgrade planner on synthetic class tables, built by the independent writer of <see cref="SchemaStreamEditorTests"/>:
/// the version rule, the two upgrade-identifier rules, classes that only one table defines, the matching of fields by name, the
/// element counts and the refusals of a table that cannot be planned.</summary>
public class DowngradePlannerTests
{
    private static string G(int n) => SchemaStreamEditorTests.Guid(n);

    private static string[] Gs(params int[] numbers) => [.. numbers.Select(G)];

    private static Fld Int(string name) => SchemaStreamEditorTests.Int(name);

    private static Cls C(string name, uint version, string[]? guids = null, params Fld[] fields) =>
        new(name, new NoRef(), version, fields, guids ?? []);

    private static SchemaDocument Doc(params Cls[] classes) => TfSchemaReader.Parse(SchemaStreamEditorTests.Write([.. classes]).Data);

    private static DowngradePlan Plan(Cls[] source, Cls[] target, GuidRule rule = GuidRule.Exact, Dictionary<string, int>? counts = null) =>
        DowngradePlanner.Compute(Doc(source), Doc(target), rule, counts);

    private static SchemaEdit EditOf(DowngradePlan plan, string name) => plan.Edits.Single(e => e.ClassName == name);

    // ---- versions ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Version_IsLoweredToTheTargetVersion_OnlyWhenTheSourceIsHigher()
    {
        var plan = Plan(
            [C("High", 9), C("Same", 4), C("Low", 2)],
            [C("High", 7), C("Same", 4), C("Low", 5)]);

        var edit = Assert.Single(plan.Edits);
        Assert.Equal(new SchemaEdit("High", 7, null), edit);
        Assert.Equal([new VersionChange("High", 9, 7)], plan.HigherVersions);
        Assert.Equal(1, plan.LowerVersions);
        Assert.Equal(3, plan.CommonClasses);
    }

    [Fact]
    public void NothingToDo_GivesAnEmptyPlan()
    {
        var plan = Plan([C("A", 3, Gs(1, 2)), C("B", 1)], [C("A", 3, Gs(1, 2)), C("B", 1)]);

        Assert.Empty(plan.Edits);
        Assert.Empty(plan.HigherVersions);
        Assert.Empty(plan.GuidLists);
        Assert.Empty(plan.FieldDifferences);
    }

    // ---- upgrade-identifier lists --------------------------------------------------------------------------------------------

    [Fact]
    public void ExactRule_TheListBecomesTheTargetList_InTheOrderOfTheTarget()
    {
        var plan = Plan([C("A", 1, Gs(1, 2, 3))], [C("A", 1, Gs(3, 4, 1))]);

        Assert.Equal(Gs(3, 4, 1), EditOf(plan, "A").Guids);
        Assert.Null(EditOf(plan, "A").Version);
        Assert.Equal(new GuidListChange("A", 3, 3, 3, Added: 1, Removed: 1), Assert.Single(plan.GuidLists));
    }

    [Fact]
    public void ExactRule_AddsTheTargetIdentifiersTheSourceLacks_AndGivesAClassTheTargetListsNothingForAnEmptyList()
    {
        var plan = Plan([C("Short", 1, Gs(1)), C("Empty", 1, Gs(5)), C("None", 1)], [C("Short", 1, Gs(1, 2, 3)), C("Empty", 1), C("None", 1, Gs(7))]);

        Assert.Equal(Gs(1, 2, 3), EditOf(plan, "Short").Guids);
        Assert.Equal(Gs(7), EditOf(plan, "None").Guids);
        Assert.Empty(EditOf(plan, "Empty").Guids!);
        Assert.Equal(2, plan.GuidLists.Single(l => l.Name == "Short").Added);
        Assert.Equal(1, plan.GuidLists.Single(l => l.Name == "Empty").Removed);
    }

    [Fact]
    public void MinimalRule_OnlyRemovesWhatTheTargetListLacks_AndNeverAdds_InTheOrderOfTheSource()
    {
        var plan = Plan([C("A", 1, Gs(1, 2, 3, 4)), C("Kept", 1, Gs(1, 2))], [C("A", 1, Gs(4, 9, 2)), C("Kept", 1, Gs(2, 1, 8))], GuidRule.Minimal);

        Assert.Equal(Gs(2, 4), EditOf(plan, "A").Guids);
        Assert.Equal(new GuidListChange("A", 4, 3, 2, Added: 0, Removed: 2), plan.GuidLists.Single());
        Assert.DoesNotContain(plan.Edits, e => e.ClassName == "Kept");
    }

    [Fact]
    public void MinimalRule_ALongerTargetListChangesNothing()
    {
        var plan = Plan([C("A", 1, Gs(1))], [C("A", 1, Gs(1, 2, 3))], GuidRule.Minimal);

        Assert.Empty(plan.Edits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryEdit_AppliesToTheSourceStream_AndLeavesAStreamThatPassesTheTargetPredicate(bool minimal)
    {
        var rule = minimal ? GuidRule.Minimal : GuidRule.Exact;
        var source = new[] { C("A", 5, Gs(1, 2, 3)), C("B", 8, Gs(4)), C("OnlyHere", 2, Gs(6)), C("Same", 1, Gs(7)) };
        var target = new[] { C("A", 4, Gs(1, 3, 9)), C("B", 8, []), C("OnlyThere", 9), C("Same", 1, Gs(7)) };
        byte[] stream = SchemaStreamEditorTests.Write([.. source]).Data;
        var plan = DowngradePlanner.Compute(TfSchemaReader.Parse(stream), Doc(target), rule);

        var edited = TfSchemaReader.Parse(SchemaStreamEditor.Apply(stream, plan.Edits));

        var targetByName = Doc(target).Classes.ToDictionary(c => c.Name);
        foreach (var c in edited.Classes.Where(c => targetByName.ContainsKey(c.Name)))
        {
            var t = targetByName[c.Name];
            Assert.True(c.Version <= t.Version, $"{c.Name}: version {c.Version} above {t.Version}");
            Assert.All(c.Guids, g => Assert.Contains(g, t.Guids));
        }
        // a class only one table defines is not touched
        var untouched = edited.Classes.Single(c => c.Name == "OnlyHere");
        Assert.Equal(2, untouched.Version);
        Assert.Equal(Gs(6), untouched.Guids);
    }

    // ---- classes only in one table -------------------------------------------------------------------------------------------

    [Fact]
    public void ClassesOnlyInOneTable_AreListedAndNeverEdited_AndTheSourceOnesAreCounted()
    {
        var plan = Plan(
            [C("Both", 2), C("Gone", 9, Gs(1)), C("Gone2", 3)],
            [C("Both", 2), C("New", 1)],
            counts: new() { ["Gone"] = 61, ["Both"] = 5000 });

        Assert.Equal([new SourceOnlyClass("Gone", 61), new SourceOnlyClass("Gone2", 0)], plan.ClassesOnlyInSource);
        Assert.Equal(["New"], plan.ClassesOnlyInTarget);
        Assert.Equal(61, plan.ElementsInSourceOnlyClasses);
        Assert.Empty(plan.Edits);
        Assert.Equal((3, 2, 1), (plan.SourceClasses, plan.TargetClasses, plan.CommonClasses));
    }

    [Fact]
    public void WithoutCounts_TheElementsOfSourceOnlyClassesAreUnknown_NotZero()
    {
        var plan = Plan([C("Gone", 1)], [C("Other", 1)]);

        Assert.Null(Assert.Single(plan.ClassesOnlyInSource).Elements);
        Assert.Null(plan.ElementsInSourceOnlyClasses);
    }

    // ---- fields --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Fields_AreMatchedByName_AndTheDifferencesListedPerClass()
    {
        var plan = Plan(
            [C("A", 1, null, Int("m_kept"), Int("m_sourceOnly"), new Fld("m_type", 0x04)),
             C("B", 1, null, Int("m_x"))],
            [C("A", 1, null, new Fld("m_kept", 0x07), Int("m_targetOnly"), new Fld("m_type", 0x07)),
             C("B", 1, null, Int("m_x"))]);

        var difference = Assert.Single(plan.FieldDifferences);
        Assert.Equal("A", difference.Name);
        Assert.Equal(["m_sourceOnly"], difference.OnlyInSource);
        Assert.Equal(["m_targetOnly"], difference.OnlyInTarget);
    }

    [Fact]
    public void Fields_AreComparedByName_NotByPosition()
    {
        var plan = Plan([C("A", 1, null, Int("m_a"), Int("m_b"))], [C("A", 1, null, Int("m_b"), Int("m_a"))]);

        Assert.Empty(plan.FieldDifferences);
    }

    [Fact]
    public void Fields_ThatAreNotStored_AreIgnored()
    {
        var plan = Plan(
            [C("A", 1, null, Int("m_a"), new Fld("m_runtime", 0x04, 0, 2))],
            [C("A", 1, null, Int("m_a"), new Fld("m_other", 0x04, 0, 2))]);

        Assert.Empty(plan.FieldDifferences);
    }

    // ---- tables that cannot be planned ---------------------------------------------------------------------------------------

    [Fact]
    public void ADuplicateClassName_IsRefused_ForBothTables()
    {
        var duplicate = new[] { C("Twice", 1), C("Twice", 2) };
        var single = new[] { C("Twice", 1) };

        Assert.Equal("ambiguous-class", Assert.Throws<DowngradeRefusedException>(() => Plan(duplicate, single)).Kind);
        Assert.Equal("ambiguous-class", Assert.Throws<DowngradeRefusedException>(() => Plan(single, duplicate)).Kind);
    }

    [Fact]
    public void ATableWithoutLists_IsRefused()
    {
        var withLists = Doc(C("A", 1));
        var oldLayout = TfSchemaReader.Parse(SchemaStreamEditorTests.Write([C("A", 1, null, Int("m_a")), C("B", 1, null, Int("m_b"))], guidLists: false).Data);
        Assert.False(oldLayout.GuidLists);

        Assert.Equal("source-layout", Assert.Throws<DowngradeRefusedException>(() => DowngradePlanner.Compute(oldLayout, withLists, GuidRule.Exact)).Kind);
        Assert.Equal("profile-layout", Assert.Throws<DowngradeRefusedException>(() => DowngradePlanner.Compute(withLists, oldLayout, GuidRule.Exact)).Kind);
    }
}
