using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

/// <summary>The count of elements per class on small synthetic files: a class table whose ContentMarker has the class id of release 2022
/// (so the element id is 4 bytes wide) and partition streams written by a small writer of the saved framing.</summary>
public sealed class ElementCensusTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-census-" + Guid.NewGuid().ToString("N"));

    public ElementCensusTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Cls C(string name) => new(name, new NoRef(), 1, [], []);

    // ContentKey 816 and ContentMarker 817 (the release 2022 marker), then the classes of the test and the framing classes.
    private static SchemaDocument Table()
    {
        var writer = new SchemaStreamEditorTests.Writer();
        for (int id = 12; id < 816; id++) writer.Top(C("Filler" + id));
        foreach (string name in new[] { "ContentKey", "ContentMarker", "Alpha", "Beta", "SegmentMarker", "SegmentCheckback", "SignatureMarker" }) writer.Top(C(name));
        return TfSchemaReader.Parse(writer.Build());
    }

    private static readonly SchemaDocument Schema = Table();

    // a class name, or #number for a tag the table does not define
    private static ushort Tag(string name) => name.StartsWith('#') ? ushort.Parse(name[1..]) : Id(name);

    private static ushort Id(string name) => (ushort)Schema.Classes.Single(c => c.Name == name).Id;

    /// <summary>One block: <paramref name="keyed"/> for a loaded family document, and its elements as (owner id, class name).</summary>
    private sealed record Block(bool Keyed, (uint Owner, string Class)[] Elements, bool Tagless = false);

    /// <summary>A record whose body is the 2-byte class tag and one more byte, or, when <paramref name="tagless"/>, one byte only.</summary>
    private static byte[] Record(uint owner, uint channel, ushort kind, bool tagless = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(owner);
        if (channel != 101) writer.Write(77u);
        writer.Write(tagless ? 1u : 3u);
        if (!tagless) writer.Write(kind);
        writer.Write((byte)17);
        writer.Write(tagless ? 1u : 3u);
        return stream.ToArray();
    }

    private static void Segment(BinaryWriter writer, uint channel, byte[] records, uint count, uint recordSize = 3)
    {
        byte[] payload = GzipMember.Write(records);
        uint size = (uint)payload.Length + 8;
        writer.Write(Id("SegmentMarker"));
        writer.Write(0u);
        writer.Write(count);
        writer.Write(size);
        writer.Write(count * recordSize);
        writer.Write(channel);
        writer.Write(19u);
        writer.Write(payload);
        writer.Write(Id("SegmentCheckback"));
        writer.Write(size);
    }

    private static byte[] Partition(params Block[] blocks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(9ul);
        foreach (var block in blocks)
        {
            writer.Write(Id("ContentMarker"));
            writer.Write(block.Keyed ? uint.MaxValue : 0u);
            if (block.Keyed) writer.Write(Id("ContentKey"));
            writer.Write((uint)block.Elements.Length);
            if (block.Keyed) writer.Write(new byte[16]);
            for (uint channel = 101; channel <= 103; channel++)
            {
                byte[] records = [.. block.Elements.SelectMany(e => Record(e.Owner, channel, channel == 102 ? Tag(e.Class) : (ushort)1, block.Tagless && channel == 102))];
                Segment(writer, channel, records, (uint)block.Elements.Length, block.Tagless && channel == 102 ? 1u : 3u);
            }
            writer.Write(Id("SegmentMarker"));
            writer.Write(new byte[16]);
            writer.Write(Id("SignatureMarker"));
            writer.Write(0u);
            writer.Write(0u);
        }
        writer.Write(Id("ContentMarker"));
        writer.Write(0u);
        writer.Write(uint.MaxValue);
        return stream.ToArray();
    }

    private (Dictionary<string, int>? Counts, string? Failure) Count(params byte[][] partitions)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".rvt");
        using (var create = RootStorage.Create(path, OpenMcdf.Version.V3))
        {
            var storage = create.CreateStorage("Partitions");
            for (int i = 0; i < partitions.Length; i++)
            {
                using var stream = storage.CreateStream((i + 1).ToString());
                stream.Write(partitions[i]);
            }
        }
        using var root = RootStorage.OpenRead(path);
        var counts = ElementCensus.CountByClass(root, Schema, out var failure);
        return (counts, failure);
    }

    [Fact]
    public void EveryOwnerOfTheProjectBlockIsOneElement_OfTheClassOfItsRecord()
    {
        var (counts, failure) = Count(Partition(new Block(false, [(1, "Alpha"), (2, "Beta"), (3, "Alpha")])));

        Assert.Null(failure);
        Assert.Equal(new Dictionary<string, int> { ["Alpha"] = 2, ["Beta"] = 1 }, counts);
    }

    [Fact]
    public void TheRecordsOfALoadedFamilyDocument_AreNotProjectElements()
    {
        // the keyed block repeats owner 2 with another class and adds owner 99: neither changes the project's count
        var (counts, failure) = Count(Partition(
            new Block(false, [(1, "Alpha"), (2, "Beta")]),
            new Block(true, [(2, "Alpha"), (99, "Alpha")])));

        Assert.Null(failure);
        Assert.Equal(new Dictionary<string, int> { ["Alpha"] = 1, ["Beta"] = 1 }, counts);
    }

    [Fact]
    public void AnElementSeveralPartitionStreamsHold_IsCountedOnce_InTheClassOfItsLastCopy()
    {
        var (counts, failure) = Count(
            Partition(new Block(false, [(1, "Alpha"), (2, "Alpha")])),
            Partition(new Block(false, [(4, "Alpha"), (1, "Beta")])));

        Assert.Null(failure);
        Assert.Equal(new Dictionary<string, int> { ["Alpha"] = 2, ["Beta"] = 1 }, counts);
    }

    [Fact]
    public void AClassTagThatTheTableDoesNotDefine_IsCountedUnderItsNumber()
    {
        var (counts, failure) = Count(Partition(new Block(false, [(1, "Alpha"), (2, "#5000")])));

        Assert.Null(failure);
        Assert.Equal(new Dictionary<string, int> { ["Alpha"] = 1, ["(class 5000)"] = 1 }, counts);
    }

    [Fact]
    public void AFramingThatDoesNotHold_IsDamage_AndSaysWhichStream()
    {
        byte[] partition = Partition(new Block(false, [(1, "Alpha")]));
        partition[^1] = 0; // the terminal marker no longer ends the stream

        var exception = Assert.Throws<RvtInputException>(() => Count(partition));

        Assert.Equal("truncated-or-damaged", exception.Kind);
        Assert.Contains("Partition stream 1", exception.Message);
    }

    [Fact]
    public void ARecordWithoutAClassTag_IsDamage_AndSaysWhichStream()
    {
        byte[] partition = Partition(new Block(false, [(1, "Alpha")], Tagless: true));

        var exception = Assert.Throws<RvtInputException>(() => Count(partition));

        Assert.Equal("truncated-or-damaged", exception.Kind);
        Assert.Contains("without a class tag", exception.Message);
    }

    [Fact]
    public void AFileWithoutPartitions_GivesNoCounts()
    {
        string path = Path.Combine(_folder, "none.rvt");
        using (RootStorage.Create(path, OpenMcdf.Version.V3)) { }
        using var root = RootStorage.OpenRead(path);

        Assert.Null(ElementCensus.CountByClass(root, Schema, out var failure));
        Assert.Contains("Partitions", failure);
    }
}
