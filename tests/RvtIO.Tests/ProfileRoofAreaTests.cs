using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ProfileRoofAreaTests
{
    private static byte[] Read(string kind)
    {
        using var stream = TestFiles.OpenBlock($"sample-a-roof-7311849-{kind}.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    [SkippableFact]
    public void OwnHistorySelectsTheConsumedTopAndTrimmedBottom()
    {
        byte[] root = Read("root"), grep = Read("grep");
        var tags = TfBlockDeserializer.ExtractOwnProfileRoofAreaFaceTags(root, 0, BitConverter.ToUInt32(root, 8))!;
        Assert.Equal(new[] { 9 }, tags.Top); Assert.Equal(new[] { 0 }, tags.Bottom);
        var graph = TfBlockDeserializer.ExtractOwnGElementGeometryGraph(grep, 0, BitConverter.ToUInt32(grep, 8), 2007,
            out var failure, includeAnnotationNodes: true)!;
        Assert.Null(failure); Assert.Equal(155, graph.Nodes.Count);
        Assert.Equal(12.870562689188338, GRepParserV2.ComputeOwnProfileRoofArea(graph, tags)!.Value * .3048 * .3048, 12);
        Assert.Null(GRepParserV2.ComputeOwnProfileRoofArea(graph, tags with { OwnerId = 1 }));
        Assert.Null(GRepParserV2.ComputeOwnProfileRoofArea(graph, tags with { Bottom = [999999] }));
        // Annotation support is opt-in; existing wall-graph consumers keep their admission rules.
        Assert.Null(TfBlockDeserializer.ExtractOwnGElementGeometryGraph(grep, 0, BitConverter.ToUInt32(grep, 8), 2007, out _));
        int flagsOffset = graph.Nodes.First(n => n.ClassId == 1243).Fields
            .Single(f => f.Declaring == 1244 && f.Name == "m_flags").Start;
        grep[flagsOffset] |= 8;
        var curved = TfBlockDeserializer.ExtractOwnGElementGeometryGraph(grep, 0, BitConverter.ToUInt32(grep, 8), 2007,
            out _, includeAnnotationNodes: true)!;
        Assert.Null(GRepParserV2.ComputeOwnProfileRoofArea(curved, tags));
    }

    [SkippableTheory]
    [InlineData("owner")] [InlineData("size")] [InlineData("footer")] [InlineData("class")]
    [InlineData("version")] [InlineData("step_owner")] [InlineData("duplicate_tag")]
    public void CorruptOrUnsupportedRootFailsClosed(string mutation)
    {
        byte[] root = Read("root");
        switch (mutation)
        {
            case "owner": root[0] ^= 1; break;
            case "size": root[8] ^= 1; break;
            case "footer": root[^1] ^= 1; break;
            case "class": root[12] ^= 1; break;
            // Exact offsets in this fixture's owned ProfileRoofBaseGStep prefix.
            case "version": BitConverter.GetBytes(32).CopyTo(root, 3462); break;
            case "step_owner": BitConverter.GetBytes(3).CopyTo(root, 3470); break;
            case "duplicate_tag": BitConverter.GetBytes(0).CopyTo(root, 1702); break;
        }
        Assert.Null(TfBlockDeserializer.ExtractOwnProfileRoofAreaFaceTags(root, 0, BitConverter.ToUInt32(root, 8)));
    }

    [SkippableTheory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void AnchoredReaderRequiresOneRootAndOneGrep(bool duplicateRoot, bool duplicateGrep)
    {
        byte[] root = Read("root"), grep = Read("grep");
        var state = new ParsingState(); state.AllKnownIds.Add(7311849);
        state.OwnedElementClassRoots[7311849] = new(1, 3054);
        state.SystemOccurrenceTypeIds[7311849] = (1, 3054, 7221455);
        state.SystemTypeRoots[7221455] = (1, 3452, null);
        state.Chunks.Add(root); state.Chunks.Add(grep);
        if (duplicateRoot) state.Chunks.Add(root);
        if (duplicateGrep) state.Chunks.Add(grep);
        RvtFileReader.ReadOwnProfileRoofAreas(state);
        Assert.Equal(!duplicateRoot && !duplicateGrep, state.OwnProfileRoofAreas.ContainsKey(7311849));
        state.SystemTypeRoots[7221455] = (1, 908, null);
        RvtFileReader.ReadOwnProfileRoofAreas(state);
        Assert.Empty(state.OwnProfileRoofAreas);
    }

    [Theory]
    [InlineData("valid")] [InlineData("duplicate")] [InlineData("wrong_category")]
    [InlineData("block_only")] [InlineData("wrong_class")] [InlineData("duplicate_root")]
    public void OnlyUniqueMaterializedRoofReceivesTheValidation(string mutation)
    {
        var state = new ParsingState(); state.OwnProfileRoofAreas[10] = 100;
        state.OwnedElementClassRoots[10] = new(mutation == "duplicate_root" ? 2 : 1,
            mutation == "wrong_class" ? (ushort)3451 : (ushort)3054);
        var roof = new Element { Id = 10, CategoryId = mutation == "wrong_category" ? -2000032 : -2000035,
            IsBlockOnly = mutation == "block_only", ValidatedProfileRoofAreaFeet = 999 };
        var elements = new List<Element> { roof };
        if (mutation == "duplicate") elements.Add(roof);
        ElementBuilder.ApplyValidatedProfileRoofAreas(elements, state);
        Assert.Equal(mutation == "valid" ? 100d : (double?)null, roof.ValidatedProfileRoofAreaFeet);
    }

    [Fact]
    public void ProjectedAreaIsTheStoredAreaNotTheValidatedProfileArea()
    {
        // Revit's ProjectedArea is the stored HOST_AREA_COMPUTED; the own-profile area was the ODA 27.7 TotalArea.
        var writer = new StepWriter();
        var roof = new Element { Id = 10, CategoryId = -2000035,
            ValidatedProfileRoofAreaFeet = 12.870562689188338 / (.3048 * .3048) };
        roof.Parameters.Add(new Parameter { Id = -1012805, StorageType = StorageType.Double,
            Value = 14.375562689188321 / (.3048 * .3048) });
        IfcExporter.ExportRoofCommonPropertySet(writer, roof, 100, 1, new(), null);
        var area = Assert.Single(Enumerable.Range(0, writer.EntityCount).Select(writer.EntityAt),
            entity => entity.Args.Contains("IFCAREAMEASURE(")).Args;
        Assert.Equal(14.375562689188321, double.Parse(Regex.Match(area, @"IFCAREAMEASURE\(([^)]+)\)").Groups[1].Value,
            CultureInfo.InvariantCulture), 12);
    }
}
