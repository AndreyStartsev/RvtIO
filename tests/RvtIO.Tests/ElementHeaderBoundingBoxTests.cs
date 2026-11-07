using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The axis-aligned outline the ElementHeader stores: the element box of every release, written
/// behind a tag pair of the embedded format schema, absent where the application reports none, and
/// never taken from a counter.</summary>
public class ElementHeaderBoundingBoxTests
{
    private const ushort OutlineTag = 297, ParentsTag = 1352;

    /// <summary>An ElementHeader record (class 1340, no regeneration entries) whose tail carries the
    /// category, family, owner view and design option ids and then the given body.</summary>
    private static byte[] Record(byte[] body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0x053C);
        writer.Write(0u);
        writer.Write(-2000112);             // m_categroryId (OST_Phases)
        writer.Write(-1);                   // m_familyId
        writer.Write(-1);                   // m_ownerViewId
        writer.Write(-1);                   // m_designOptionId
        writer.Write(body);
        byte[] record = stream.ToArray();
        BitConverter.GetBytes((uint)(record.Length - 12)).CopyTo(record, 4);
        return record;
    }

    private static byte[] OutlineBody(double[] box) => WritePattern(OutlineTag, [.. box]);

    private static byte[] AbsentBody() => WritePattern(0, []);

    private static byte[] WritePattern(ushort tag, double[] box)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(tag);
        writer.Write(0xFFFFFFFFu);
        writer.Write(ParentsTag);
        foreach (double value in box) writer.Write(value);
        return stream.ToArray();
    }

    [Fact]
    public void Reader_ReadsTheStoredOutline()
    {
        double[] box = [-1.5, -2.25, 0.0, 3.75, 4.5, 12.0];
        var verdict = ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box)), 0);
        Assert.NotNull(verdict);
        Assert.True(verdict.Present);
        Assert.Equal(box, verdict.Box);
    }

    [Fact]
    public void Reader_AcceptsTheStoredAbsenceOfAnOutline()
    {
        var verdict = ElementHeaderCategory.ReadBoundingBox(Record(AbsentBody()), 0);
        Assert.NotNull(verdict);
        Assert.False(verdict.Present);
        Assert.Null(verdict.Box);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Reader_TakesNoCounterForABox(int mutation)
    {
        // A pattern whose doubles do not form a box: a NaN one, or a max below its min.
        double[] box = mutation == 0
            ? [double.NaN, -1.0, 0.0, 1.0, 2.0, 3.0]
            : [3.0, 2.0, 1.0, 0.0, -1.0, -2.0];
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box)), 0));
    }

    [Fact]
    public void Reader_LeavesARecordWithoutAPatternUnknown()
    {
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record([1, 2, 3, 4]), 0));
        // A header the reader cannot place (a wrong class tag) is unknown as well.
        byte[] record = Record(OutlineBody([0, 0, 0, 1, 1, 1]));
        record[8] ^= 0xFF;
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(record, 0));
    }

    [Fact]
    public void Recorder_FollowsTheRecordsAndStaysUnknownOnDisagreement()
    {
        double[] box = [0, 0, 0, 1, 1, 1];
        var state = new ParsingState();
        ElementHeaderCategory.Record(state, Record(OutlineBody(box)), 0, 100);
        var verdict = Assert.IsType<ElementHeaderBox>(state.HeaderBoundingBoxes[100]);
        Assert.True(verdict.Present);
        Assert.Equal(box, verdict.Box);
        // A second record of the same element that agrees keeps the verdict.
        ElementHeaderCategory.Record(state, Record(OutlineBody(box)), 0, 100);
        Assert.Same(verdict, state.HeaderBoundingBoxes[100]);
        // One that stores no outline instead turns the verdict unknown.
        ElementHeaderCategory.Record(state, Record(AbsentBody()), 0, 100);
        Assert.Null(state.HeaderBoundingBoxes[100]);
        // A record of another element starts fresh.
        ElementHeaderCategory.Record(state, Record(AbsentBody()), 0, 101);
        Assert.False(Assert.IsType<ElementHeaderBox>(state.HeaderBoundingBoxes[101]).Present);
    }

    [Fact]
    public void Applier_PutsTheStoredOutlineOnTheElementAndClearsTheUnstoredOne()
    {
        double[] box = [-1.0, -1.0, 0.0, 1.0, 1.0, 2.0];
        var state = new ParsingState();
        state.HeaderBoundingBoxes[100] = new ElementHeaderBox(true, box);
        state.HeaderBoundingBoxes[101] = new ElementHeaderBox(false, null);
        state.HeaderBoundingBoxes[102] = null;                                   // the records disagree
        state.HeaderBoundingBoxes[103] = new ElementHeaderBox(true, box);
        var boxed = new Element { Id = 100, BoundingBox = [9, 9, 9, 9, 9, 9] };
        var unstored = new Element { Id = 101, BoundingBox = [9, 9, 9, 9, 9, 9] };
        var disagreeing = new Element { Id = 102, BoundingBox = [9, 9, 9, 9, 9, 9] };
        var unrecorded = new Element { Id = 104, BoundingBox = [9, 9, 9, 9, 9, 9] };
        ElementBuilder.ApplyHeaderBoundingBoxes(
            new RevitFileInfo { Elements = [boxed, unstored, disagreeing, unrecorded] }, state);
        Assert.Equal(box, boxed.BoundingBox);
        Assert.Null(unstored.BoundingBox);
        Assert.Equal([9, 9, 9, 9, 9, 9], disagreeing.BoundingBox);
        Assert.Equal([9, 9, 9, 9, 9, 9], unrecorded.BoundingBox);
        Assert.Same(box, state.HeaderBoundingBoxes[103]!.Box);
    }

    [SkippableTheory]
    [InlineData(2020)]
    [InlineData(2021)]
    [InlineData(2022)]
    [InlineData(2023)]
    [InlineData(2024)]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void StoredOutline_IsTheElementBoxOfEveryRelease(int release)
    {
        string path = FirstOfRelease(release) ?? throw TestFiles.Missing($"a project of release {release}");
        // The anchored walk that reads the outline anchors the records of the Revit 2022 layout:
        // the reads of the later releases take it through the R22 layout (the IFC export does too).
        var info = new RvtFileReader(new RvtReaderOptions { R22Layout = true }).Read(path);

        var boxed = info.Elements.Where(e => e.BoundingBox is { Length: 6 }).ToList();
        Assert.NotEmpty(boxed);
        // The outline bounds the body: every geometry box the element decoded lies inside it (the
        // tessellation of a curved body may pass the stored envelope by a hair).
        foreach (var element in boxed)
            if (element.Geometry?.BoundingBox is { Length: 6 } tight)
            {
                double[] box = element.BoundingBox!;
                Assert.True(box[0] <= tight[0] + 1e-3 && box[1] <= tight[1] + 1e-3 && box[2] <= tight[2] + 1e-3
                    && box[3] >= tight[3] - 1e-3 && box[4] >= tight[4] - 1e-3 && box[5] >= tight[5] - 1e-3,
                    $"element {element.Id} of release {release}: [{string.Join(", ", box)}] does not contain [{string.Join(", ", tight)}]");
            }
        // A grid or a level has no stored outline in any release.
        Assert.All(info.Elements.Where(e => e.CategoryId is (int)BuiltInCategory.OST_Grids or (int)BuiltInCategory.OST_Levels),
            e => Assert.Null(e.BoundingBox));
    }

    private static string? FirstOfRelease(int release)
    {
        foreach (string path in PageCodecSampleTests.ProjectFiles())
        {
            using var root = OpenMcdf.RootStorage.OpenRead(path);
            if (TfSchemaReader.Read(root) is { } document && SchemaRelease.FromSchema(new FormatsSchema(document)) == release) return path;
        }
        return null;
    }
}
