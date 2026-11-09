using RvtIO.Parsing;
using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

public class IfcCeilingOccurrencePredefinedTypeTests
{
    [Theory]
    [InlineData(-2000038, "unrelated authored name", "CEILING")]
    [InlineData(-2000038, "Потолок", "CEILING")]
    [InlineData(-2000032, "OST_Ceilings", "NOTDEFINED")]
    [InlineData(-2000035, "Ceiling", "NOTDEFINED")]
    [InlineData(null, "Ceiling", "NOTDEFINED")]
    public void DefaultUsesOnlyTheIntrinsicCeilingCategory(int? category, string name, string expected)
    {
        var covering = new Element { Id = 100, CategoryId = category, Name = name, Category = "OST_Ceilings" };
        Assert.Equal(expected, IfcExporter.ResolveCoveringOccurrencePredefinedType(covering, null, Counts()));
    }

    [Theory]
    [InlineData("block_only")] [InlineData("duplicate_occurrence")] [InlineData("missing_occurrence")]
    [InlineData("nonpositive_id")] [InlineData("duplicate_type")] [InlineData("wrong_type_category")]
    [InlineData("wrong_type_id")]
    public void AmbiguityDoesNotBecomeACategoryDefault(string mutation)
    {
        var covering = Ceiling(); var type = new Element { Id = 200, CategoryId = -2000038 };
        var counts = Counts();
        switch (mutation)
        {
            case "block_only": covering.IsBlockOnly = true; break;
            case "duplicate_occurrence": counts[100] = 2; break;
            case "missing_occurrence": counts.Remove(100); break;
            case "nonpositive_id": covering.Id = 0; break;
            case "duplicate_type": counts[200] = 2; break;
            case "wrong_type_category": type.CategoryId = -2000032; break;
            case "wrong_type_id": type.Id = 201; counts[201] = 1; break;
        }
        Assert.Equal("NOTDEFINED", IfcExporter.ResolveCoveringOccurrencePredefinedType(covering, type, counts));
    }

    [Theory]
    [InlineData(false, "IfcExportAs")] [InlineData(true, "IfcExportType")]
    [InlineData(false, "IfcType[Type]")] [InlineData(true, "IfcExportAs[Type]")]
    public void InstanceAndTypeOverridesAreNotReplacedByThisDefault(bool onType, string name)
    {
        var covering = Ceiling(); var type = new Element { Id = 200, CategoryId = -2000038 };
        (onType ? type : covering).Parameters.Add(new Parameter
        {
            Definition = new Definition { Name = name }, StorageType = StorageType.String, Value = "IfcCovering.USERDEFINED",
        });
        Assert.Equal("NOTDEFINED", IfcExporter.ResolveCoveringOccurrencePredefinedType(covering, type, Counts()));
    }

    [Theory]
    [InlineData(false, "CEILING")] [InlineData(true, "NOTDEFINED")]
    public void WholeIfcCarriesTheGuardedDefaultAlongsideUnchangedBodyAndPsets(bool overridden, string expected)
    {
        var info = new RevitFileInfo();
        var covering = IfcExporterCoveringCommonPropertySetTests.Product(100, 200, -2000038, "OST_Ceilings", "Native covering");
        if (overridden) covering.Parameters.Add(new Parameter
        {
            Definition = new Definition { Name = "IfcExportType" }, StorageType = StorageType.String, Value = "USERDEFINED",
        });
        info.Elements.Add(covering);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-ceiling-enum-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);
            var product = Assert.Single(Regex.Matches(ifc, @"#\d+=IFCCOVERING\(([^\r\n]*)\);"));
            Assert.EndsWith($".{expected}.", product.Groups[1].Value);
            Assert.Matches(@",#\d+,#\d+,'100',\.[A-Z]+\.$", product.Groups[1].Value);
            Assert.Contains("'Pset_CoveringCommon'", ifc);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(IfcProfile.Current, "IFCCOVERING", "$")]
    [InlineData(IfcProfile.Legacy, "IFCBUILDINGELEMENTPROXY", "$")]
    public void RoofSoffitIsACoveringWithoutAPredefinedTypeInTheCurrentProfile(IfcProfile profile, string productClass, string lastArgument)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(WithTypeLink(IfcExporterCoveringCommonPropertySetTests.Product(100, 200, -2001393, "OST_RoofSoffit", "Soffit")));
        info.Elements.Add(new Element { Id = 200, TypeId = -1, CategoryId = -2001393, Category = "OST_RoofSoffit", Name = "Soffit 100" });
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-soffit-{Guid.NewGuid():N}.ifc");
        try
        {
            var skipped = new SkipLog();
            new IfcExporter { Profile = profile, SkipLog = skipped }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.True(Regex.Matches(ifc, $@"#\d+={productClass}\(").Count == 1, Describe(skipped, ifc));
            var product = Assert.Single(Regex.Matches(ifc, $@"#\d+={productClass}\(([^\r\n]*)\);"));
            Assert.EndsWith($",'100',{lastArgument}", product.Groups[1].Value);
            if (profile == IfcProfile.Current)
            {
                var type = Assert.Single(Regex.Matches(ifc, @"#\d+=IFCCOVERINGTYPE\(([^\r\n]*)\);"));
                Assert.EndsWith(",'200',$,$", type.Groups[1].Value);
            }
            else Assert.DoesNotContain("IFCCOVERING", ifc);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void GutterIsAFlowSegmentWithAGutterTypeOfItsOwnInTheCurrentProfile(IfcProfile profile)
    {
        var info = new RevitFileInfo();
        var gutter = WithTypeLink(IfcExporterCoveringCommonPropertySetTests.Product(100, 200, -2001391, "OST_Gutter", "Gutter"));
        gutter.SerializedExportId = Guid.Parse("1551350c-45e5-41b8-bdf1-bcfa6794dc6a");
        info.Elements.Add(gutter);
        info.Elements.Add(new Element { Id = 200, TypeId = -1, CategoryId = -2001391, Category = "OST_Gutter", Name = "Gutter 150" });
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-gutter-{Guid.NewGuid():N}.ifc");
        try
        {
            var skipped = new SkipLog();
            new IfcExporter { Profile = profile, SkipLog = skipped }.Export(info, path);
            string ifc = File.ReadAllText(path);
            if (profile == IfcProfile.Legacy)
            {
                Assert.Contains("=IFCBUILDINGELEMENTPROXY(", ifc);
                Assert.DoesNotContain("IFCFLOWSEGMENT", ifc);
                return;
            }
            Assert.True(Regex.Matches(ifc, @"#\d+=IFCFLOWSEGMENT\(").Count == 1, Describe(skipped, ifc));
            Assert.EndsWith(",'100'", Assert.Single(Regex.Matches(ifc, @"#\d+=IFCFLOWSEGMENT\(([^\r\n]*)\);")).Groups[1].Value);
            string type = Assert.Single(Regex.Matches(ifc, @"#\d+=IFCPIPESEGMENTTYPE\(([^\r\n]*)\);")).Groups[1].Value;
            string typeGuid = IfcGuid.ToIfcGuid(ElementExportIds.SubElementId(gutter.SerializedExportId.Value, 1));
            Assert.StartsWith($"'{typeGuid}',", type);
            Assert.EndsWith(",'100','Gutter:Gutter 150:200',.GUTTER.", type);
            Assert.Contains(",'Gutter:Gutter 150:200',", type);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Element WithTypeLink(Element product)
    {
        product.Parameters.Add(new Parameter
        {
            Id = -1002050, Value = 200, StorageType = StorageType.ElementId,
            Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
        });
        return product;
    }

    private static string Describe(SkipLog skipped, string ifc) =>
        string.Join(";", skipped.Groups().Select(group => $"{group.Class}/{group.Reason}/{group.Count}"))
        + " | " + string.Join(" ", ifc.Split('\n').Where(line => line.Contains("'100'")).Select(line => line.Trim()));

    private static Element Ceiling() => new() { Id = 100, TypeId = 200, CategoryId = -2000038 };
    private static Dictionary<int, int> Counts() => new() { [100] = 1, [200] = 1 };
}
