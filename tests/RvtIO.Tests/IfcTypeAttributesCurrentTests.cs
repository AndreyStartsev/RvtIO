using System.Reflection;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Attributes of type objects in the current profile: the Tag of a window style (the original family symbol), the PredefinedType
/// of a foundation slab type (BASESLAB) and of a stair flight type (STRAIGHT, FREEFORM), and the split of the flight types by run style.</summary>
public class IfcTypeAttributesCurrentTests
{
    [Theory]
    [InlineData(true, "'601'")]    // the current profile: createWindowStyle takes the original family symbol
    [InlineData(false, "'500'")]
    public void WindowStyle_IsTaggedByTheOriginalFamilySymbolInTheNewerRelease(bool currentProfile, string expectedTag)
    {
        var elements = new Dictionary<int, Element>
        {
            [500] = new() { Id = 500, Name = "Style", CategoryId = -2000014 },
            [601] = new() { Id = 601, Name = "Symbol", CategoryId = -2000014 },
            [1] = new()
            {
                Id = 1, CategoryId = -2000014, ValidatedOriginalFamilySymbolId = 601,
                Parameters = [TypeParameter(500)],
            },
        };
        string text = Write(elements, [("IFCWINDOW", 1)], currentProfile);
        Assert.Equal(expectedTag, Args(Assert.Single(Lines(text, "IFCWINDOWSTYLE")))[7]);
    }

    [Fact]
    public void WindowStyleWithoutAnOriginalSymbol_IsTaggedByTheTypeElement()
    {
        var elements = new Dictionary<int, Element>
        {
            [500] = new() { Id = 500, Name = "Style", CategoryId = -2000014 },
            [1] = new() { Id = 1, CategoryId = -2000014, Parameters = [TypeParameter(500)] },
        };
        Assert.Equal("'500'", Args(Assert.Single(Lines(Write(elements, [("IFCWINDOW", 1)], currentProfile: true), "IFCWINDOWSTYLE")))[7]);
    }

    [Theory]
    [InlineData(-2001300, -2001300, true, "BASESLAB")]   // a foundation slab of a foundation slab type
    [InlineData(-2000032, -2001300, true, "BASESLAB")]   // a floor of a foundation slab type
    [InlineData(-2000032, -2001300, false, "NOTDEFINED")]
    [InlineData(-2000032, -2000032, true, "FLOOR")]
    public void SlabType_OfAFoundationSlabTypeIsBaseSlabInTheNewerRelease(
        int occurrenceCategory, int typeCategory, bool currentProfile, string expected)
    {
        var elements = new Dictionary<int, Element>
        {
            [500] = new() { Id = 500, Name = "Foundation Slab", CategoryId = typeCategory },
            [1] = new() { Id = 1, CategoryId = occurrenceCategory, Parameters = [TypeParameter(500)] },
        };
        string text = Write(elements, [("IFCSLAB", 1)], currentProfile);
        Assert.EndsWith($".{expected}.", Args(Assert.Single(Lines(text, "IFCSLABTYPE")))[^1]);
    }

    [Fact]
    public void FlightType_TakesTheStyleOfTheRun_AndSplitsByIt()
    {
        var elements = Runs(TfBlockDeserializer.StairRunStyle.Straight, TfBlockDeserializer.StairRunStyle.Sketched,
            TfBlockDeserializer.StairRunStyle.Straight);
        string text = Write(elements, [("IFCSTAIRFLIGHT", 1), ("IFCSTAIRFLIGHT", 2), ("IFCSTAIRFLIGHT", 3)], currentProfile: true);
        var types = Lines(text, "IFCSTAIRFLIGHTTYPE");
        Assert.Equal(2, types.Length);
        Assert.Single(types, line => line.EndsWith(".STRAIGHT.);"));
        Assert.Single(types, line => line.EndsWith(".FREEFORM.);"));
        Assert.Contains("(#1001,#1003),#", text);
        Assert.Contains("(#1002),#", text);
    }

    [Fact]
    public void FlightType_OfASpiralRun_IsSpiral_AndSplitsFromTheOtherStyles()
    {
        var elements = Runs(TfBlockDeserializer.StairRunStyle.Straight, TfBlockDeserializer.StairRunStyle.Spiral);
        string text = Write(elements, [("IFCSTAIRFLIGHT", 1), ("IFCSTAIRFLIGHT", 2)], currentProfile: true);
        var types = Lines(text, "IFCSTAIRFLIGHTTYPE");
        Assert.Equal(2, types.Length);
        Assert.Single(types, line => line.EndsWith(".SPIRAL.);"));
        Assert.Single(types, line => line.EndsWith(".STRAIGHT.);"));
    }

    [Fact]
    public void FlightType_OfARunWithoutADecodedStyle_IsNotDefined_AndOlderReleaseHasNoRunTypes()
    {
        var elements = Runs(TfBlockDeserializer.StairRunStyle.Straight, TfBlockDeserializer.StairRunStyle.Sketched);
        elements[1].SerializedStairRunInput = null;
        string newer = Write(elements, [("IFCSTAIRFLIGHT", 1), ("IFCSTAIRFLIGHT", 2)], currentProfile: true);
        Assert.Contains(Lines(newer, "IFCSTAIRFLIGHTTYPE"), line => line.EndsWith(".NOTDEFINED.);"));
        string older = Write(elements, [("IFCSTAIRFLIGHT", 1), ("IFCSTAIRFLIGHT", 2)], currentProfile: false);
        Assert.Empty(Lines(older, "IFCSTAIRFLIGHTTYPE"));
    }

    private static Dictionary<int, Element> Runs(params TfBlockDeserializer.StairRunStyle[] styles)
    {
        var elements = new Dictionary<int, Element> { [500] = new() { Id = 500, Name = "Run type", CategoryId = -2000919 } };
        for (int i = 0; i < styles.Length; i++)
            elements[i + 1] = new Element
            {
                Id = i + 1, CategoryId = -2000919, TypeId = 500,
                SerializedStairRunInput = new TfBlockDeserializer.StairRunInput(i + 1, 100, styles[i], [new(1, 0, 0)]),
            };
        return elements;
    }

    private static Parameter TypeParameter(int typeId) => new()
    {
        Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId, Value = typeId,
    };

    private static string Write(Dictionary<int, Element> elements, (string Class, int Id)[] products, bool currentProfile)
    {
        var map = new Dictionary<int, (int entityId, int placementId, string ifcType)>();
        foreach (var (ifcType, id) in products) map.Add(id, (1000 + id, 0, ifcType));
        var writer = new StepWriter();
        var method = typeof(IfcExporter).GetMethod("WriteTypeObjectsAndRelDefinesByTypeCore",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [writer, 900, elements, map,
            new Dictionary<(string PropertyName, string ValueExpr), int>(), new Dictionary<int, bool?>(), currentProfile]);
        using var text = new StringWriter();
        writer.WriteTo(text, "test.ifc", "IFC2X3", "test", "test");
        return text.ToString();
    }

    private static string[] Lines(string text, string type) => text.Split('\n')
        .Select(line => line.TrimEnd('\r')).Where(line => Regex.IsMatch(line, $@"^#\d+={type}\(")).ToArray();

    // Attributes of a whole entity line ("#12=IFCX(a,b);"), split outside strings and lists.
    private static string[] Args(string line)
    {
        string args = line[(line.IndexOf('(') + 1)..^2];
        var result = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { result.Add(args[start..i]); start = i + 1; }
        }
        result.Add(args[start..]);
        return [.. result];
    }
}
