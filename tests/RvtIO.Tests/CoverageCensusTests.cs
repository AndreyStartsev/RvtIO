using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;
using Rec = RvtIO.Tests.ElementCensusTests.Rec;

namespace RvtIO.Tests;

/// <summary>The coverage census on small synthetic files: a class table whose ContentMarker has the class id of release 2022 (so the
/// element id is 4 bytes wide) with a few classes that have fields, and partition streams written by the writer of
/// <see cref="ElementCensusTests"/>. No private file is needed.</summary>
public sealed class CoverageCensusTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-coverage-" + Guid.NewGuid().ToString("N"));

    public CoverageCensusTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Cls C(string name, params Fld[] fields) => new(name, new NoRef(), 1, fields, []);

    /// <summary>The decoded class table: ContentKey 816 and ContentMarker 817 (the release 2022 marker), then Plain (one int), Store (one
    /// int), Holder (an owned pointer), Listed (a counted array of ints), Empty (no field) and the framing classes.</summary>
    internal static readonly byte[] TableBytes = BuildTable();

    internal static readonly SchemaDocument Schema = TfSchemaReader.Parse(TableBytes);

    private static byte[] BuildTable()
    {
        var writer = new SchemaStreamEditorTests.Writer();
        for (int id = 12; id < 816; id++) writer.Top(C("Filler" + id));
        writer.Top(C("ContentKey")).Top(C("ContentMarker"));
        writer.Top(C("Plain", new Fld("m_value", 4))).Top(C("Store", new Fld("m_value", 4))).Top(C("Holder", new Fld("m_pStore", 14, 1)));
        writer.Top(C("Listed", new Fld("m_items", 4, 0x50))).Top(C("Empty"));
        foreach (string name in new[] { "SegmentMarker", "SegmentCheckback", "SignatureMarker" }) writer.Top(C(name));
        return writer.Build();
    }

    internal static ushort Id(string name) => (ushort)Schema.Classes.Single(c => c.Name == name).Id;

    /// <summary>The decoded class table of a file with an extensible storage entity: Carrier (an entity and an int), the classes of the
    /// document that lists the entity schemas, and the framing classes. Its ContentMarker names no release: the tests give one.</summary>
    internal static readonly byte[] EntityTableBytes = BuildEntityTable();

    internal static readonly SchemaDocument EntitySchema = TfSchemaReader.Parse(EntityTableBytes);

    private static byte[] BuildEntityTable()
    {
        static Fld Of(string name, int classId, byte mods = 0) => new(name, 14, mods, Target: new SchemaStreamEditorTests.Back(classId));
        var writer = new SchemaStreamEditorTests.Writer();
        // the writer numbers the classes from 12 on
        writer.Top(C("GUIDvalue", new Fld("m_guid", 9))).Top(C("ESEntity", new Fld("m_blob", 14, 1)));
        writer.Top(C("Carrier", Of("m_entity", 13), new Fld("m_value", 4)));
        writer.Top(C("ESField", new Fld("m_fieldName", 8, 0x60), new Fld("m_fieldTypeName", 8, 0x60), new Fld("m_containerType", 4), new Fld("m_entryIndex", 4)));
        writer.Top(C("ESSchema", Of("m_fields", 15, 0x50), Of("m_guid", 12))).Top(C("std::pair< GUIDvalue, ESSchema >", Of("first", 12), Of("second", 16)));
        writer.Top(C("ESSchemaStorage", Of("m_storedSchemas", 17, 0x50))).Top(C("ADocument", new Fld("m_pStorage", 14, 1)));
        foreach (string name in new[] { "ContentKey", "ContentMarker", "SegmentMarker", "SegmentCheckback", "SignatureMarker" }) writer.Top(C(name));
        return writer.Build();
    }

    internal static ushort EntityId(string name) => (ushort)EntitySchema.Classes.Single(c => c.Name == name).Id;

    private static readonly byte[] EntityGuid = [.. Enumerable.Range(0x21, 16).Select(i => (byte)i)];

    private static byte[] Utf16(string text) => [.. BitConverter.GetBytes(text.Length), .. System.Text.Encoding.Unicode.GetBytes(text)];

    /// <summary>The body of a Carrier: its entity names a schema, m_value, and behind the fields the values of the entity, a text and an int.</summary>
    internal static readonly byte[] CarrierBody =
        [.. BitConverter.GetBytes(EntityId("Carrier")), 0xFF, 0xFF, 0xFF, 0xFF, .. EntityGuid, 7, 0, 0, 0, .. Utf16("ab"), 3, 0, 0, 0];

    /// <summary>The decoded Global/Latest: an ADocument whose storage holds the schema of that entity, the fields listed by name
    /// (Number, Text) with the entry indexes 1 and 0, and the four bytes behind the last object.</summary>
    internal static readonly byte[] EntityLatest =
    [
        .. BitConverter.GetBytes(EntityId("ADocument")), 0xFF, 0xFF, 0xFF, 0xFF, .. BitConverter.GetBytes(EntityId("ESSchemaStorage")),
        1, 0, 0, 0, .. EntityGuid,
        2, 0, 0, 0, .. Utf16("Number"), .. Utf16("int"), 0, 0, 0, 0, 1, 0, 0, 0, .. Utf16("Text"), .. Utf16("TCHAR"), 0, 0, 0, 0, 0, 0, 0, 0,
        .. EntityGuid, 0, 0, 0, 0,
    ];

    internal static byte[] EntityPartition(params Rec[] records) => ElementCensusTests.Partition(EntityId, Block(false, records));

    /// <summary>A record body: the tag of the class and the bytes behind it.</summary>
    internal static byte[] Body(string className, params byte[] rest) => [.. BitConverter.GetBytes(Id(className)), .. rest];

    // written out here, so that the checksum the census computes is not checked against itself
    private static uint Adler(byte[] bytes)
    {
        uint a = 1, b = 0;
        foreach (byte value in bytes)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return b << 16 | a;
    }

    /// <summary>A record with the checksum of its body.</summary>
    internal static Rec R(uint owner, byte[] body) => new(owner, body, Adler(body));

    /// <summary>A block that holds the same records on its three channels.</summary>
    internal static (bool Keyed, Rec[][] Channels) Block(bool keyed, params Rec[] records) => (keyed, [records, records, records]);

    internal static byte[] Partition(params (bool Keyed, Rec[][] Channels)[] blocks) => ElementCensusTests.Partition(Id, blocks);

    /// <summary>Writes a compound file with the given streams; a path with a slash names a stream of a storage.</summary>
    internal static void WriteFile(string path, params (string Path, byte[] Bytes)[] streams)
    {
        using var root = RootStorage.Create(path, OpenMcdf.Version.V3);
        var storages = new Dictionary<string, Storage>();
        foreach (var (name, bytes) in streams)
        {
            int slash = name.IndexOf('/');
            Storage parent = root;
            if (slash >= 0 && !storages.TryGetValue(name[..slash], out parent!)) storages[name[..slash]] = parent = root.CreateStorage(name[..slash]);
            using var stream = parent.CreateStream(name[(slash + 1)..]);
            stream.Write(bytes);
        }
    }

    private CoverageCensus.Result Run(params (string Path, byte[] Bytes)[] streams)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".rvt");
        WriteFile(path, streams);
        using var root = RootStorage.OpenRead(path);
        return CoverageCensus.Run(root, Schema, null);
    }

    private CoverageCensus.Result RunProject(params Rec[] records) => Run(("Partitions/1", Partition(Block(false, records))));

    private CoverageCensus.Result RunCarrier(params (string Path, byte[] Bytes)[] streams)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".rvt");
        WriteFile(path, [.. streams, ("Partitions/1", EntityPartition(R(1, CarrierBody)))]);
        using var root = RootStorage.OpenRead(path);
        return CoverageCensus.Run(root, EntitySchema, 2022);
    }

    private static CoverageCensus.ClassRow Row(CoverageCensus.Result result, bool family, int channel, string className) =>
        result.Classes.Single(c => c.Family == family && c.Channel == channel && c.Class == className);

    private static (long, long, long, long, long, long) Values(CoverageCensus.Sums sums) =>
        (sums.Items, sums.Exact, sums.PayloadBytes, sums.ExactBytes, sums.ChecksumMismatches, sums.Terminators);

    /// <summary>The class rows add up to their group, the groups to the totals, the stops of a row to its records that are not exact, and
    /// the records of the framed streams to the total.</summary>
    internal static void AssertRowsAddUp(CoverageCensus.Result result)
    {
        Assert.Equal(result.Classes.Count, result.Classes.Select(c => (c.Family, c.Channel, c.Class)).Distinct().Count());
        foreach (var group in result.Groups)
        {
            var sum = new CoverageCensus.Sums();
            foreach (var row in result.Classes.Where(c => c.Family == group.Family && c.Channel == group.Channel)) sum.Add(row.Sums);
            // the terminators of a group are no record of a class
            Assert.Equal(0, sum.Terminators);
            sum.Terminators = group.Sums.Terminators;
            Assert.Equal(Values(sum), Values(group.Sums));
        }
        var total = new CoverageCensus.Sums();
        foreach (var group in result.Groups) total.Add(group.Sums);
        Assert.Equal(Values(total), Values(result.Totals));
        Assert.All(result.Classes, c => Assert.Equal(c.Sums.Items - c.Sums.Exact, c.Stops.Sum(s => s.Count)));
        Assert.Equal(result.Streams.Sum(s => s.Records ?? 0), result.Totals.Items);
    }

    [Fact]
    public void ARecordWalkedToItsLastByte_IsExact()
    {
        var result = RunProject(R(1, Body("Plain", 5, 0, 0, 0)));

        Assert.Null(result.RecordsFailure);
        Assert.Equal((2022, 4), (result.SchemaRelease, result.IdWidth));
        Assert.Equal(Schema.Classes.Count, result.ClassCount);
        foreach (int channel in new[] { 101, 102, 103 })
        {
            var row = Row(result, false, channel, "Plain");
            Assert.Equal((1, 1, 6, 6, 0, 0), Values(row.Sums));
            Assert.Empty(row.Stops);
        }
        Assert.Equal((3, 3, 18, 18, 0, 0), Values(result.Totals));
    }

    [Fact]
    public void ARecordThatEndsShort_SaysByHowManyBytes_UpTo64()
    {
        var result = RunProject(R(1, Body("Plain", 5, 0, 0, 0, 9, 9, 9)), R(2, Body("Plain", [.. new byte[4 + 65]])), R(3, Body("Empty")),
            R(4, Body("Plain", [.. new byte[4 + 64]])));

        var row = Row(result, false, 102, "Plain");
        Assert.Equal((3, 0, 9 + 71 + 70, 0), (row.Sums.Items, row.Sums.Exact, row.Sums.PayloadBytes, row.Sums.ExactBytes));
        Assert.Equal(["short by 3 bytes", "short by 64 bytes", "short by more than 64 bytes"], row.Stops.Select(s => s.Reason));
        Assert.Equal([1ul], row.Stops[0].Owners);
        // a class without fields is exact when the body is its tag alone
        Assert.Equal((1, 1), (Row(result, false, 102, "Empty").Sums.Items, Row(result, false, 102, "Empty").Sums.Exact));
    }

    [Fact]
    public void AnError_IsNamedWithItsField_AndWithTheClassOfAQueuedObject()
    {
        var result = RunProject(
            R(1, Body("Plain", 5, 0)),
            R(2, Body("Holder", [0xFF, 0xFF, 0xFF, 0xFF, .. BitConverter.GetBytes(Id("Store")), 5, 0])),
            R(3, Body("Listed", 0xFF, 0xFF, 0xFF, 0x00)),
            R(4, Body("Holder", [0xFF, 0xFF, 0xFF, 0xFF, .. BitConverter.GetBytes(Id("Store")), 5, 0, 0, 0])));

        Assert.Equal("end of payload at Plain.m_value", Assert.Single(Row(result, false, 102, "Plain").Stops).Reason);
        var holder = Row(result, false, 102, "Holder");
        Assert.Equal((2, 1), (holder.Sums.Items, holder.Sums.Exact));
        Assert.Equal("end of payload at Store.m_value, in Store", Assert.Single(holder.Stops).Reason);
        // the count itself is left out of the reason, so that every such record falls under one reason
        Assert.Equal("count at Listed.m_items", Assert.Single(Row(result, false, 102, "Listed").Stops).Reason);
    }

    [Fact]
    public void TheValuesOfAnEntity_AreReadByTheEntitySchemasOfGlobalLatest()
    {
        var result = RunCarrier(("Global/Latest", GzipMember.Write(EntityLatest)));

        var row = Row(result, false, 102, "Carrier");
        Assert.Equal((1, 1, CarrierBody.Length), (row.Sums.Items, row.Sums.Exact, row.Sums.ExactBytes));
        Assert.Empty(row.Stops);
    }

    [Fact]
    public void WhenTheEntitySchemasCannotBeRead_AnEntityWithValuesStopsItsRecord()
    {
        // no Global/Latest, one that does not decode, and one whose walk stops (cut inside the GUID of the schema)
        foreach (var streams in new (string, byte[])[][] { [], [("Global/Latest", [1, 2, 3, 4])], [("Global/Latest", GzipMember.Write(EntityLatest[..^8]))] })
        {
            var row = Row(RunCarrier(streams), false, 102, "Carrier");
            Assert.Equal("entity schema not stored at ESEntity.m_blob", Assert.Single(row.Stops).Reason);
        }
    }

    /// <summary>The decoded Global/ContentDocuments of one family document with the given key: its ContentMarker object, the length,
    /// the document's ADocument (one that stores the entity schema of the Carrier), the length again, and the closing ContentMarker.</summary>
    private static byte[] ContentDocuments(byte[] key, bool closed = true) =>
    [
        .. BitConverter.GetBytes(EntityId("ContentMarker")), 0xFF, 0xFF, 0xFF, 0xFF, .. BitConverter.GetBytes(EntityId("ContentKey")),
        0xFF, 0xFF, 0xFF, 0xFF, .. key, .. BitConverter.GetBytes(EntityLatest.Length), .. EntityLatest, .. BitConverter.GetBytes(EntityLatest.Length),
        .. closed ? [.. BitConverter.GetBytes(EntityId("ContentMarker")), 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0] : Array.Empty<byte>(),
    ];

    // the partition writer gives the keyed block of index 1 the key 1 and fifteen zero bytes
    private static readonly byte[] FamilyKey = [1, .. new byte[15]];

    /// <summary>A Carrier in the project's block and one in a family block (index 1), with the given Global streams.</summary>
    private CoverageCensus.Result RunCarrierInBothBlocks(params (string Path, byte[] Bytes)[] streams)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".rvt");
        WriteFile(path, [.. streams, ("Partitions/1", ElementCensusTests.Partition(EntityId, Block(false, R(1, CarrierBody)), Block(true, R(1, CarrierBody))))]);
        using var root = RootStorage.OpenRead(path);
        return CoverageCensus.Run(root, EntitySchema, 2022);
    }

    [Fact]
    public void AnEntityOfAFamilyBlock_IsReadByTheSchemasOfItsOwnDocumentInGlobalContentDocuments()
    {
        var result = RunCarrierInBothBlocks(("Global/ContentDocuments", GzipMember.Write(ContentDocuments(FamilyKey))));

        var family = Row(result, true, 102, "Carrier");
        Assert.Equal((1, 1, CarrierBody.Length), (family.Sums.Items, family.Sums.Exact, family.Sums.ExactBytes));
        // the schemas of a family document are not the project's
        Assert.Equal("entity schema not stored at ESEntity.m_blob", Assert.Single(Row(result, false, 102, "Carrier").Stops).Reason);
        AssertRowsAddUp(result);
    }

    [Fact]
    public void AnEntityOfAFamilyBlock_IsReadByTheSchemasOfGlobalLatest_AsFromRevit2025()
    {
        var result = RunCarrierInBothBlocks(("Global/Latest", GzipMember.Write(EntityLatest)),
            ("Global/ContentDocuments", GzipMember.Write(ContentDocuments([2, .. new byte[15]]))));

        Assert.Equal((1, 1), (Row(result, true, 102, "Carrier").Sums.Items, Row(result, true, 102, "Carrier").Sums.Exact));
        Assert.Equal((1, 1), (Row(result, false, 102, "Carrier").Sums.Items, Row(result, false, 102, "Carrier").Sums.Exact));
    }

    [Fact]
    public void AnEntityOfAFamilyBlock_StopsWhenNoDocumentOfItsKeyStoresTheSchema()
    {
        // a document of another key, one that is not closed, and one whose stream does not decode
        foreach (var bytes in new[] { GzipMember.Write(ContentDocuments([2, .. new byte[15]])), GzipMember.Write(ContentDocuments(FamilyKey, closed: false)), [1, 2, 3, 4] })
        {
            var family = Row(RunCarrierInBothBlocks(("Global/ContentDocuments", bytes)), true, 102, "Carrier");
            Assert.Equal("entity schema not stored at ESEntity.m_blob", Assert.Single(family.Stops).Reason);
        }
    }

    [Fact]
    public void AClassTagOutsideTheSchema_AndABodyWithoutATag_HaveRowsOfTheirOwn()
    {
        var result = RunProject(R(1, [0x88, 0x13, 0]), R(2, [7]), R(3, []), R(4, [0, 0]));

        var unknown = Row(result, false, 102, "(class 5000)");
        Assert.Equal((1, 0), (unknown.Sums.Items, unknown.Sums.Exact));
        Assert.Equal("class 5000 has no schema", Assert.Single(unknown.Stops).Reason);
        // tag 0 names no class either, although a walk of it reads nothing and so ends at the last byte of a 2-byte body
        var zero = Row(result, false, 102, "(class 0)");
        Assert.Equal((1, 0), (zero.Sums.Items, zero.Sums.Exact));
        Assert.Equal("class 0 has no schema", Assert.Single(zero.Stops).Reason);
        var tagless = Row(result, false, 102, "(no class tag)");
        Assert.Equal((2, 0, 1), (tagless.Sums.Items, tagless.Sums.Exact, tagless.Sums.PayloadBytes));
        var stop = Assert.Single(tagless.Stops);
        Assert.Equal(("no class tag", 2), (stop.Reason, stop.Count));
        Assert.Equal([2ul, 3ul], stop.Owners);
    }

    [Fact]
    public void AFamilyBlock_IsKeptApartFromTheProjectBlock_AndTheTerminatorIsCounted()
    {
        var result = Run(("Partitions/1", Partition(
            Block(false, R(1, Body("Plain", 5, 0, 0, 0)), R(uint.MaxValue, [])),
            Block(true, R(1, Body("Plain", 5, 0)), R(2, Body("Plain", 5, 0, 0, 0))))));

        Assert.Equal([(false, 101), (false, 102), (false, 103), (true, 101), (true, 102), (true, 103)], result.Groups.Select(g => (g.Family, g.Channel)));
        Assert.Equal((1, 1), (Row(result, false, 103, "Plain").Sums.Items, Row(result, false, 103, "Plain").Sums.Exact));
        Assert.Equal((2, 1), (Row(result, true, 103, "Plain").Sums.Items, Row(result, true, 103, "Plain").Sums.Exact));
        Assert.All(result.Groups, g => Assert.Equal(g.Family ? 0 : 1, g.Sums.Terminators));
        // the terminator has no body: it is not a record, of no class and of no stream count
        Assert.Equal((1, 1, 1), (result.Groups[0].Sums.Items, result.Groups[0].Sums.Exact, result.Groups[0].Sums.Terminators));
        Assert.DoesNotContain(result.Classes, c => c.Class is "" or "(no class tag)");
        Assert.Equal(3, result.Totals.Terminators);
        var stream = Assert.Single(result.Streams);
        Assert.Equal((2, 1, 6, 9L), (stream.Blocks, stream.FamilyBlocks, stream.Segments, stream.Records));
        AssertRowsAddUp(result);
    }

    [Fact]
    public void AChecksumThatDoesNotMatch_IsCounted_OnChannels102And103Only()
    {
        byte[] body = Body("Plain", 5, 0, 0, 0);
        Rec good = R(1, body), bad = good with { Extra = good.Extra + 1 };

        var result = Run(("Partitions/1", Partition((false, [[bad], [bad], [good]]))));

        Assert.Equal([0, 1, 0], result.Groups.Select(g => (int)g.Sums.ChecksumMismatches));
        Assert.Equal(1, result.Totals.ChecksumMismatches);
        // the checksum does not decide whether the record is exact
        Assert.Equal(3, result.Totals.Exact);
    }

    [Fact]
    public void APartitionWhoseFramingFails_LeavesNoRecords_AndTheOtherPartitionStillCounts()
    {
        byte[] damaged = Partition(Block(false, R(1, Body("Plain", 5, 0, 0, 0)), R(2, Body("Plain", 5, 0))));
        damaged[^1] = 0; // the terminal marker no longer ends the stream, behind records the walk has already seen

        var result = Run(("Partitions/1", damaged), ("Partitions/2", Partition(Block(false, R(7, Body("Store", 5, 0, 0, 0))))));

        Assert.Null(result.RecordsFailure);
        var failed = result.Streams[0];
        Assert.Equal(("Partitions/1", "partition"), (failed.Path, failed.Kind));
        Assert.False(string.IsNullOrEmpty(failed.Framing));
        Assert.NotEqual("ok", failed.Framing);
        Assert.Null(failed.Blocks);
        Assert.Null(failed.Records);
        Assert.Null(failed.InflatedBytes);
        Assert.Equal(("ok", 3L), (result.Streams[1].Framing, result.Streams[1].Records));
        Assert.Equal(["Store", "Store", "Store"], result.Classes.Select(c => c.Class));
        Assert.Equal((3, 3), (result.Totals.Items, result.Totals.Exact));
        AssertRowsAddUp(result);
    }

    [Fact]
    public void AFileWithoutPartitions_GivesTheReason_AndStillListsItsStreams()
    {
        var result = Run(("Other", [1, 2, 3]));

        Assert.Contains("Partitions", result.RecordsFailure);
        Assert.Empty(result.Classes);
        Assert.Empty(result.Groups);
        Assert.Equal(0, result.Totals.Items);
        Assert.Equal(("Other", "plain", 3L), (result.Streams.Single().Path, result.Streams.Single().Kind, result.Streams.Single().RawBytes));
    }

    [Fact]
    public void AClassTableWithoutFramingClasses_GivesTheReason_AndListsThePartitionUnread()
    {
        string path = Path.Combine(_folder, "noframing.rvt");
        byte[] partition = Partition(Block(false, R(1, Body("Plain", 5, 0, 0, 0))));
        WriteFile(path, ("Partitions/1", partition));
        using var root = RootStorage.OpenRead(path);

        var result = CoverageCensus.Run(root, new SchemaDocument { Classes = [new(12, "Plain", 0, 1, [], [])] }, 2022);

        Assert.Contains("framing", result.RecordsFailure);
        var stream = Assert.Single(result.Streams);
        Assert.Equal(("partition", (long)partition.Length), (stream.Kind, stream.RawBytes));
        Assert.Null(stream.Framing);
        Assert.Null(stream.Records);
        Assert.Empty(result.Classes);
    }

    [Fact]
    public void EveryStreamIsListed_WithItsKind_InPathOrderWithThePartitionsByNumber()
    {
        byte[] partition = Partition(Block(false, R(1, Body("Plain", 5, 0, 0, 0))));
        byte[] member = GzipMember.Write(new byte[100]);

        var result = Run(("Partitions/10", partition), ("Partitions/2", partition), ("Partitions/Notes", [1]), ("Other", [1, 2, 3]),
            ("Global/Good", member), ("Global/Bad", [1, 2, 3, 4]), ("Formats/Latest", member));

        Assert.Equal(["Formats/Latest", "Global/Bad", "Global/Good", "Other", "Partitions/2", "Partitions/10", "Partitions/Notes"], result.Streams.Select(s => s.Path));
        Assert.Equal(["paged", "paged", "paged", "plain", "partition", "partition", "plain"], result.Streams.Select(s => s.Kind));
        Assert.Equal([true, false, true, null, null, null, null], result.Streams.Select(s => s.Decoded));
        Assert.Equal([100L, null, 100L, null, 62L, 62L, null], result.Streams.Select(s => s.InflatedBytes));
        Assert.Equal([null, null, null, null, "ok", "ok", null], result.Streams.Select(s => s.Framing));
        var framed = result.Streams[4];
        Assert.Equal(((long)partition.Length, 1, 0, 3, 3L), (framed.RawBytes, framed.Blocks, framed.FamilyBlocks, framed.Segments, framed.Records));
        Assert.Equal(6, result.Totals.Items);
        AssertRowsAddUp(result);
    }

    [Fact]
    public void Rows_AreOrderedByBlockChannelAndRecordsNotExact_AndStopsByCount()
    {
        Rec[] project =
        [
            R(1, Body("Store", 5, 0, 0, 0)),
            R(9, Body("Plain", 5, 0)), R(3, Body("Plain", 5, 0)), R(7, Body("Plain", 5, 0)), R(5, Body("Plain", 5, 0)),
            R(4, Body("Plain", 5, 0, 0, 0, 1)),
            R(2, Body("Listed", 0)), R(6, Body("Holder", 0)),
        ];

        var result = Run(("Partitions/1", Partition(Block(false, project), Block(true, R(1, Body("Plain", 5, 0))))),
            ("Partitions/2", Partition(Block(false, R(8, Body("Plain", 5, 0))))));

        // per block and channel: Plain (6 not exact), then Holder and Listed (1 each, by name), then Store (none)
        string[] names = ["Plain", "Holder", "Listed", "Store"];
        Assert.Equal(
            [.. new[] { 101, 102, 103 }.SelectMany(channel => names.Select(name => (false, channel, name))),
             .. new[] { 101, 102, 103 }.Select(channel => (true, channel, "Plain"))],
            result.Classes.Select(c => (c.Family, c.Channel, c.Class)));
        var plain = Row(result, false, 102, "Plain");
        Assert.Equal([("end of payload at Plain.m_value", 5L), ("short by 1 bytes", 1L)], plain.Stops.Select(s => (s.Reason, s.Count)));
        // the lowest three owners, over both partition streams
        Assert.Equal([3ul, 5ul, 7ul], plain.Stops[0].Owners);
        AssertRowsAddUp(result);
    }
}
