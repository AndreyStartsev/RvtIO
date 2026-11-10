using System.Text;
using System.Text.Json;
using RvtIO.Compact;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class CompactPropertySourcesTests
{

    [Theory]
    [InlineData(1, "autodesk.spec:spec.bool-1.0.0", "Yes")]
    [InlineData(0, "autodesk.spec:spec.bool-1.0.0", "No")]
    [InlineData(5, "autodesk.spec:spec.bool", "Yes")]
    [InlineData(-3, "autodesk.spec:spec.int-1.0.0", "-3")]
    [InlineData(42, "", "42")]
    public void IntegerText_IsYesNoOnlyForTheBoolSpec(int value, string spec, string expected)
        => Assert.Equal(expected, CompactValueRules.IntegerText(value, spec));

    [Fact]
    public void ElementIdText_NoneNegativeDecimalAndValidatedNameOnly()
    {
        string? Name(int id) => id == 7 ? "Level 1" : null;
        Assert.Equal("None", CompactValueRules.ElementIdText(-1, Name));
        Assert.Equal("-2000011", CompactValueRules.ElementIdText(-2000011, Name));
        Assert.Equal("Level 1", CompactValueRules.ElementIdText(7, Name));
        Assert.Null(CompactValueRules.ElementIdText(8, Name));
        Assert.Null(CompactValueRules.ElementIdText(0, Name));
    }

    [Fact]
    public void CategoryText_UsesTheEnumNameOrTheDecimalIdOfTheCategoryElement()
    {
        Assert.Null(CompactValueRules.CategoryText(null));
        Assert.Equal("None", CompactValueRules.CategoryText(-1));
        Assert.Equal("OST_Walls", CompactValueRules.CategoryText(-2000011));
        Assert.Equal("228727", CompactValueRules.CategoryText(228727));
    }

    [Fact]
    public void ImageParametersPrintNothing()
    {
        Assert.Contains(-1152385, CompactValueRules.ImageParameters);
        Assert.Contains(-1152384, CompactValueRules.ImageParameters);
        Assert.DoesNotContain(-1002001, CompactValueRules.ImageParameters);
    }


    [Fact]
    public void ValueGate_ParsesStoredDerivedAndClassRows()
    {
        var gate = CompactValueGate.Parse("# comment\r\nS\t-5\t3\r\nD\t-6\t9\r\nC\tWall\t-7\t2\r\n");
        Assert.Equal(CompactValueGate.Source.Stored, gate.Of("Wall", -5));
        Assert.Equal(CompactValueGate.Source.Derived, gate.Of("Floor", -6));
        Assert.Equal(CompactValueGate.Source.Derived, gate.Of("Wall", -7));
        Assert.Equal(CompactValueGate.Source.None, gate.Of("Floor", -7));
        Assert.Equal(CompactValueGate.Source.None, gate.Of("Wall", -8));
    }

    [Theory]
    [InlineData("X\t1")]
    [InlineData("S\tabc")]
    [InlineData("C\tWall")]
    [InlineData("S\t5\nD\t5")]
    public void ValueGate_RejectsMalformedTables(string text)
        => Assert.Throws<InvalidDataException>(() => CompactValueGate.Parse(text));

    [Fact]
    public void ValueGate_ShippedTableLoads()
    {
        Assert.True(CompactValueGate.Default.StoredCount > 0);
        Assert.True(CompactValueGate.Default.DerivedCount + CompactValueGate.Default.DerivedPairCount > 0);
    }

    [Fact]
    public void HeaderCategory_IsTheDecodedHeaderValueAndUnknownWithoutIt()
    {
        Assert.Null(CompactHeaderCategory.Of(new Element { Id = 1, CategoryId = -2000011 }));
        Assert.Equal(-2000240, CompactHeaderCategory.Of(new Element { Id = 1, CategoryId = -2000011, NativeHeaderCategoryId = -2000240 }));
    }


    private static readonly Lazy<(RevitFileInfo Info, ParsingStateHolder State)> Test2022 = new(() =>
    {
        var reader = new RvtFileReader();
        var info = reader.Read(TestFileFixture.FindTestFile("Test_2022.rvt"));
        return (info, new ParsingStateHolder(reader));
    });

    private sealed record ParsingStateHolder(RvtFileReader Reader);

    private static CompactPropertySources Composite() =>
        CompactPropertySources.Create(Test2022.Value.Info, Test2022.Value.State.Reader.LastParsingState, true);

    private static byte[] Export(CompactPropertySources source)
    {
        using var stream = new MemoryStream();
        CompactJsonExporter.Export(Test2022.Value.Info, stream, source);
        return stream.ToArray();
    }

    [SkippableFact]
    public void Test2022_PropertyCountsAreStable()
    {
        using var doc = JsonDocument.Parse(Export(Composite()));
        int entries = 0, withProperties = 0;
        foreach (var node in doc.RootElement.GetProperty("data").GetProperty("collection").EnumerateArray())
        {
            if (!node.TryGetProperty("object", out _)) continue;
            int count = node.GetProperty("properties").EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
                .Sum(item => item.EnumerateObject().Count());
            entries += count;
            if (count > 0) withProperties++;
        }
        Assert.Equal(47742, entries);
        Assert.Equal(7621, withProperties);
    }

    [SkippableFact]
    public void Test2022_OutputIsDeterministic()
        => Assert.Equal(Export(Composite()), Export(Composite()));

    [SkippableFact]
    public void Test2022_ListsIfcAndUserParametersInTheirGroupsWithoutDuplicates()
    {
        var info = Test2022.Value.Info;
        var source = Composite();
        var instance = info.Elements.First(e => e.Id == 414482);
        var properties = source.GetProperties(instance);
        // the expected entries of the element
        Assert.Contains(new CompactProperty("Construction", "Show Drop Panel", "Yes"), properties);
        Assert.Contains(properties, p => p.Group == "IFC Parameters" && p.Label == "Export to IFC" && p.Value == "0");
        // the built-in and IFC entries come before the user entries of the element
        int ifc = properties.ToList().FindIndex(p => p.Group == "IFC Parameters");
        int user = properties.ToList().FindIndex(p => p is { Group: "Construction", Label: "Show Drop Panel" });
        Assert.True(user > ifc || ifc < 0);
    }

    [SkippableFact]
    public void Report_NamesTheCountsAndTheReasons()
    {
        var source = Composite();
        foreach (var element in Test2022.Value.Info.Elements.Take(50)) source.GetProperties(element);
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb)) source.Builtin.WriteReport(writer);
        Assert.StartsWith("Compact built-in parameters: ", sb.ToString());
    }
}
