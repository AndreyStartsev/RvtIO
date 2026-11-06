using RvtIO.Ifc;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class StairNativeNameTests
{
    [Theory]
    [InlineData("RUS", "Лестница")] [InlineData("ENU", "Stair")]
    public void InstanceNameComesFromOriginalSavedLocaleResource(string locale, string expected)
    {
        Assert.Equal(expected, RevitSystemFamilyNames.Load("2022", locale)!.StairOccurrenceName);
    }

    [Fact]
    public void OwnNativeInstanceNameIsIndependentOfTypeNameAndPublicJson()
    {
        var (parent, type) = Elements();
        var names = RevitSystemFamilyNames.ForTests(new Dictionary<SystemFamilyKind, string>(), "Лестница");
        ElementBuilder.ApplyValidatedStairOccurrenceNames([parent, type], names);
        Assert.Equal("Лестница", parent.ValidatedStairOccurrenceName);
        Assert.Equal("old occurrence", parent.Name); Assert.Equal("authored type", type.Name);
        Assert.DoesNotContain("ValidatedStairOccurrenceName", System.Text.Json.JsonSerializer.Serialize(parent));
        Assert.Equal("Монолитная лестница:Лестница:100", Resolve(parent, [parent, type]));
    }

    [Theory]
    [InlineData("no_resource")] [InlineData("no_name")] [InlineData("parent_duplicate")]
    [InlineData("type_duplicate")] [InlineData("parent_block")] [InlineData("type_block")]
    [InlineData("parent_class")] [InlineData("type_class")] [InlineData("parent_category")]
    [InlineData("type_category")] [InlineData("type_missing")] [InlineData("type_self")]
    [InlineData("link_unresolved")] [InlineData("no_core")] [InlineData("no_family")]
    public void InstanceNameAdmissionRejectsMissingOrAmbiguousAuthorityAndClearsOldValues(string mode)
    {
        var (parent, type) = Elements(); var elements = new List<Element> { parent, type };
        RevitSystemFamilyNames? names = RevitSystemFamilyNames.ForTests(new Dictionary<SystemFamilyKind, string>(), "Лестница");
        ElementBuilder.ApplyValidatedStairOccurrenceNames(elements, names); Assert.NotNull(parent.ValidatedStairOccurrenceName);
        switch (mode)
        {
            case "no_resource": names = null; break;
            case "no_name": names = RevitSystemFamilyNames.ForTests(new Dictionary<SystemFamilyKind, string>()); break;
            case "parent_duplicate": elements.Add(new() { Id = 100 }); break;
            case "type_duplicate": elements.Add(new() { Id = 101 }); break;
            case "parent_block": parent.IsBlockOnly = true; break;
            case "type_block": type.IsBlockOnly = true; break;
            case "parent_class": parent.SerializedOwnElementClassId = 3742; break;
            case "type_class": type.SerializedStairsTypeClassId = 3681; break;
            case "parent_category": parent.CategoryId = -2000126; break;
            case "type_category": type.CategoryId = -2000126; break;
            case "type_missing": elements.Remove(type); break;
            case "type_self": parent.SerializedStairTypeId = 100; break;
            case "link_unresolved": parent.SerializedStairTypeLinkIsResolved = false; break;
            case "no_core": parent.SerializedStairCore = null; break;
            case "no_family": type.ValidatedSystemFamilyName = null; break;
        }
        ElementBuilder.ApplyValidatedStairOccurrenceNames(elements, names);
        Assert.Null(parent.ValidatedStairOccurrenceName);
    }

    [Theory]
    [InlineData("IfcName", "new", "new")]
    [InlineData("Ifc Name", "new", "new")]
    [InlineData("ifcname", "new", "new")]
    [InlineData("IfcName", "original", "legacy")]
    [InlineData("IfcName", "", "legacy")]
    [InlineData("IfcName", null, "legacy")]
    [InlineData("IfcName[Type]", "ignored", "legacy")]
    public void InstanceOverridePrecedenceRetainsEqualToOriginalAndLegacyFallback(string key, string? value, string expected)
    {
        var element = new Element(); Parameter(element, key, value); Parameter(element, "NameOverride", "legacy");
        Assert.Equal(expected, IfcExporter.ResolveNativeStairNameOverride(element, "original"));
    }

    [Theory]
    [InlineData("expression")] [InlineData("numeric")] [InlineData("duplicate")]
    [InlineData("empty_duplicate")] [InlineData("legacy_expression")]
    public void UnvalidatedDisplayOrExpressionDoesNotFallBack(string mode)
    {
        var element = new Element(); Parameter(element, "NameOverride", "legacy");
        switch (mode)
        {
            case "expression": Parameter(element, "IfcName", "{Name}"); break;
            case "numeric": element.SerializedStairNameParameters!.Add(new() { Definition = new() { Name = "IfcName" }, StorageType = StorageType.Double, Value = 12.3 }); break;
            case "duplicate": Parameter(element, "IfcName", "one"); Parameter(element, "IfcName", "two"); break;
            case "empty_duplicate": Parameter(element, "IfcName", "one"); Parameter(element, "IfcName", ""); break;
            case "legacy_expression": element.SerializedStairNameParameters!.Clear(); Parameter(element, "NameOverride", "{Name}"); break;
        }
        Assert.Null(IfcExporter.ResolveNativeStairNameOverride(element, "original"));
    }

    [Fact]
    public void LowerPriorityBlockedOverrideIsNotReadAfterAnActualIfcNameOverride()
    {
        var element = new Element(); Parameter(element, "IfcName", "actual"); Parameter(element, "NameOverride", "{unknown}");
        Assert.Equal("actual", IfcExporter.ResolveNativeStairNameOverride(element, "original"));
    }

    [Theory]
    [InlineData("first", "legacy", "legacy")]
    [InlineData("Монолитная лестница:Лестница:100", "legacy", "Монолитная лестница:Лестница:100")]
    [InlineData("", "legacy", "legacy")]
    [InlineData("first", null, "first")]
    [InlineData("first", "{unknown}", null)]
    public void ParentUsesOriginalNameThenRootOverrideSequence(string? ifcName, string? legacy, string? expected)
    {
        var (parent, type) = Elements(); parent.ValidatedStairOccurrenceName = "Лестница";
        Parameter(parent, "IfcName", ifcName); Parameter(parent, "NameOverride", legacy);
        Assert.Equal(expected, Resolve(parent, [parent, type]));
    }

    [Theory]
    [InlineData("conflicting_type")] [InlineData("parent_override")]
    [InlineData("type_override")] [InlineData("missing_name")] [InlineData("missing_type")]
    public void WriterRechecksSourceAndTypeGuards(string mode)
    {
        var (parent, type) = Elements(); parent.ValidatedStairOccurrenceName = "Лестница";
        var elements = new List<Element> { parent, type };
        switch (mode)
        {
            case "conflicting_type": parent.Parameters.Add(new() { Definition = new() { Name = "ELEM_TYPE_PARAM" }, Value = 102 }); break;
            case "parent_override": Parameter(parent, "IfcExportAs", "IfcStair"); break;
            case "type_override": Parameter(type, "IfcExportAs", "IfcStair"); break;
            case "missing_name": parent.ValidatedStairOccurrenceName = null; break;
            case "missing_type": elements.Remove(type); break;
        }
        Assert.Null(Resolve(parent, elements));
    }

    [Fact]
    public void NativeOrdinalsCountFilteredSiblingsAndResetForLandings()
    {
        var (parent, type) = Elements(); parent.ValidatedStairOccurrenceName = "Лестница";
        parent.SerializedStairCore = [new(300, 3708), new(200, 3708), new(400, 3686)];
        var missing = new Element { Id = 200, SerializedOwnElementClassId = 3708, SerializedOwningElementId = 100 };
        var run = new Element { Id = 300, SerializedOwnElementClassId = 3708, SerializedOwningElementId = 100, SerializedStairNameParameters = [] };
        var landing = new Element { Id = 400, SerializedOwnElementClassId = 3686, SerializedOwningElementId = 100, SerializedStairNameParameters = [] };
        var writer = new StepWriter();
        writer.Add("IFCEXTRUDEDAREASOLID", "$,$,$,$");
        writer.Add("IFCSHAPEREPRESENTATION", "$,'Body','SweptSolid',(#1)");
        writer.Add("IFCPRODUCTDEFINITIONSHAPE", "$,$,(#2)");
        string nativeName = Resolve(parent, [parent, type])!;
        writer.Add("IFCSTAIR", $"'{IfcGuid.FromElementId(100)}',$,{StepWriter.Str(nativeName)},$,'ObjectType',#99,#3,'100',.STRAIGHT_RUN_STAIR.");
        writer.Add("IFCSTAIRFLIGHT", "'run',$,'old run',$,'Unchanged',#99,#3,'300',$,$,$,$");
        writer.Add("IFCSLAB", "'landing',$,'old landing',$,'Unchanged',#99,#3,'400',.LANDING.");
        var products = new Dictionary<int, (int entityId, int placementId, string ifcType)>
            { [100] = (4, 99, "IFCSTAIR"), [300] = (5, 99, "IFCSTAIRFLIGHT"), [400] = (6, 99, "IFCSLAB") };
        var contents = new Dictionary<int, List<int>> { [1] = [4, 5, 6] };
        string[] oldRun = IfcExporter.SplitStepArguments(writer.EntityAt(4).Args);
        StairAggregation.Apply(writer, [parent, type, missing, run, landing], products, contents, 88);
        var namedRun = IfcExporter.SplitStepArguments(writer.EntityAt(4).Args);
        Assert.Equal(StepWriter.Str(nativeName + " Run 2"), namedRun[2]);
        Assert.Equal(StepWriter.Str(nativeName + " Landing 1"), IfcExporter.SplitStepArguments(writer.EntityAt(5).Args)[2]);
        oldRun[2] = namedRun[2]; Assert.Equal(oldRun, namedRun);
        Assert.Equal(new[] { 4 }, contents[1]);
    }

    private static (Element Parent, Element Type) Elements() =>
        (new() { Id = 100, Name = "old occurrence", CategoryId = -2000120, SerializedOwnElementClassId = 3681,
            SerializedStairCore = [new(200, 3708)], SerializedStairTypeId = 101, SerializedStairTypeLinkIsResolved = true, SerializedStairNameParameters = [] },
         new() { Id = 101, Name = "authored type", CategoryId = -2000120, SerializedStairsTypeClassId = 3742, ValidatedSystemFamilyName = "Монолитная лестница" });
    private static string? Resolve(Element parent, IReadOnlyList<Element> elements)
        => IfcExporter.ResolveNativeStairName(parent, elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.First()),
            elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.Count()));
    private static void Parameter(Element element, string name, string? value)
    {
        var parameter = new Parameter { Definition = new() { Name = name }, StorageType = StorageType.String, Value = value };
        element.Parameters.Add(parameter);
        (element.SerializedStairNameParameters ??= []).Add(parameter);
    }

    [Fact]
    public void PublicOrInheritedOverridesCannotConfirmOwnAbsenceOrReplaceOwnValues()
    {
        var element = new Element();
        element.Parameters.Add(new() { Definition = new() { Name = "IfcName" }, StorageType = StorageType.String, Value = "inherited" });
        Assert.Null(IfcExporter.ResolveNativeStairNameOverride(element, "original"));
        element.SerializedStairNameParameters = [];
        Assert.Equal("original", IfcExporter.ResolveNativeStairNameOverride(element, "original"));
        element.SerializedStairNameParameters.Add(new() { Definition = new() { Name = "IfcName" }, StorageType = StorageType.String, Value = "own" });
        Assert.Equal("own", IfcExporter.ResolveNativeStairNameOverride(element, "original"));
        Assert.DoesNotContain("SerializedStairNameParameters", System.Text.Json.JsonSerializer.Serialize(element));
    }
}
