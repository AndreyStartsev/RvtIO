using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RvtIO.Cli;
using RvtIO.Writing;
using Xunit;
using static RvtIO.Tests.CoverageCensusTests;

namespace RvtIO.Tests;

/// <summary>The coverage command (<c>rvtio coverage</c>) on a small synthetic project file with the class table and the partition
/// writer of <see cref="CoverageCensusTests"/>, so that no private file is needed: exit codes, the printed lines, the shape of the JSON
/// report, and that nothing but the report is written.</summary>
public sealed class CoverageCommandTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-coverage-" + Guid.NewGuid().ToString("N"));

    public CoverageCommandTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string P(string name) => Path.Combine(_folder, name);

    private static readonly byte[] StoredTable = PageCodec.EncodePadded(GzipMember.Write(TableBytes));

    private static readonly byte[] SavedBy2022 = [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(4), .. Encoding.Unicode.GetBytes("2022")];

    // owner 1 is walked to its last byte on every channel, owner 2 has one byte more than its class reads
    private static readonly byte[] OnePartition = Partition(Block(false, R(1, Body("Plain", 5, 0, 0, 0)), R(2, Body("Plain", 5, 0, 0, 0, 9))));

    private string Sample(bool withPartition = true)
    {
        string path = P("sample.rvt");
        (string, byte[])[] streams = [("Formats/Latest", StoredTable), ("BasicFileInfo", SavedBy2022)];
        WriteFile(path, withPartition ? [.. streams, ("Partitions/1", OnePartition)] : streams);
        return path;
    }

    private static (int Exit, string[] Out, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = CoverageCommand.Run(args, output, error);
        return (exit, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray(), error.ToString());
    }

    private string[] Listing() => [.. Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void ASyntheticFile_IsMeasured_AndThePrintedLinesSayWhatWasCounted()
    {
        string source = Sample();
        string hash = Hash(source);
        string[] before = Listing();

        var (exit, output, error) = Run(source);

        Assert.Equal(0, exit);
        Assert.Equal("", error);
        long raw = StoredTable.Length + SavedBy2022.Length + OnePartition.Length;
        Assert.Equal(
        [
            $"File: sample.rvt (saved by release 2022, schema of release 2022, {Schema.Classes.Count} classes, element id of 4 bytes)",
            $"Streams: 3, {raw} raw bytes; partition streams framed 1 of 1; paged streams decoded 1 of 1",
            "project 101: 2 records, 1 exact (50.00%); 13 payload bytes, 6 exact (46.15%)",
            "project 102: 2 records, 1 exact (50.00%); 13 payload bytes, 6 exact (46.15%)",
            "project 103: 2 records, 1 exact (50.00%); 13 payload bytes, 6 exact (46.15%)",
            "Not exact, top 15:",
            "  101 project Plain: 1 of 2; short by 1 bytes (1)",
            "  102 project Plain: 1 of 2; short by 1 bytes (1)",
            "  103 project Plain: 1 of 2; short by 1 bytes (1)",
        ], output);
        Assert.Equal(before, Listing());
        Assert.Equal(hash, Hash(source));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    public void Top_LimitsTheClassLines_AndZeroPrintsNone(string top, int lines)
    {
        var (exit, output, _) = Run(Sample(), "--top", top);

        Assert.Equal(0, exit);
        Assert.Equal(lines, output.Count(l => l.StartsWith("  ", StringComparison.Ordinal)));
        Assert.Equal(lines > 0, output.Contains($"Not exact, top {top}:"));
    }

    [Fact]
    public void Report_HasTheDocumentedShape()
    {
        string source = Sample(), report = P("report.json");
        string[] before = Listing();

        var (exit, _, error) = Run(source, "--report", report);

        Assert.Equal(0, exit);
        Assert.Contains("Report written to", error);
        Assert.Equal([.. before.Append("report.json").Order(StringComparer.Ordinal)], Listing());
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        var root = json.RootElement;
        Assert.Equal("rvtio-coverage/1", root.GetProperty("schema").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("sample.rvt", root.GetProperty("file").GetString());
        Assert.Equal((2022, 2022, 4, Schema.Classes.Count), (root.GetProperty("savedRelease").GetInt32(), root.GetProperty("schemaRelease").GetInt32(),
            root.GetProperty("idWidth").GetInt32(), root.GetProperty("classCount").GetInt32()));
        Assert.False(root.TryGetProperty("recordsFailure", out _));

        var streams = root.GetProperty("streams").EnumerateArray().ToList();
        Assert.Equal([("BasicFileInfo", "plain"), ("Formats/Latest", "paged"), ("Partitions/1", "partition")],
            streams.Select(s => (s.GetProperty("path").GetString()!, s.GetProperty("kind").GetString()!)));
        Assert.Equal(SavedBy2022.Length, streams[0].GetProperty("rawBytes").GetInt32());
        Assert.False(streams[0].TryGetProperty("decoded", out _));
        Assert.False(streams[0].TryGetProperty("framing", out _));
        Assert.True(streams[1].GetProperty("decoded").GetBoolean());
        Assert.True(streams[1].GetProperty("inflatedBytes").GetInt32() >= TableBytes.Length);
        var partition = streams[2];
        Assert.Equal(("ok", 1, 0, 3, 6), (partition.GetProperty("framing").GetString(), partition.GetProperty("blocks").GetInt32(),
            partition.GetProperty("familyBlocks").GetInt32(), partition.GetProperty("segments").GetInt32(), partition.GetProperty("records").GetInt32()));
        Assert.True(partition.GetProperty("inflatedBytes").GetInt32() > 0);

        var records = root.GetProperty("records");
        var totals = records.GetProperty("totals");
        Assert.Equal((6, 3, 39, 18, 0, 0), (totals.GetProperty("items").GetInt32(), totals.GetProperty("exact").GetInt32(), totals.GetProperty("payloadBytes").GetInt32(),
            totals.GetProperty("exactBytes").GetInt32(), totals.GetProperty("checksumMismatches").GetInt32(), totals.GetProperty("terminators").GetInt32()));
        var groups = records.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal([("project", 101), ("project", 102), ("project", 103)], groups.Select(g => (g.GetProperty("block").GetString()!, g.GetProperty("channel").GetInt32())));
        Assert.All(groups, g => Assert.Equal((2, 1, 13, 6, 0, 0), (g.GetProperty("items").GetInt32(), g.GetProperty("exact").GetInt32(), g.GetProperty("payloadBytes").GetInt32(),
            g.GetProperty("exactBytes").GetInt32(), g.GetProperty("checksumMismatches").GetInt32(), g.GetProperty("terminators").GetInt32())));
        var classes = records.GetProperty("classes").EnumerateArray().ToList();
        Assert.Equal(3, classes.Count);
        Assert.All(classes, c =>
        {
            Assert.Equal(("project", "Plain", 2, 1, 13, 6, 0, 0), (c.GetProperty("block").GetString(), c.GetProperty("class").GetString(), c.GetProperty("items").GetInt32(),
                c.GetProperty("exact").GetInt32(), c.GetProperty("payloadBytes").GetInt32(), c.GetProperty("exactBytes").GetInt32(),
                c.GetProperty("checksumMismatches").GetInt32(), c.GetProperty("otherStops").GetInt32()));
            var stop = Assert.Single(c.GetProperty("stops").EnumerateArray());
            Assert.Equal(("short by 1 bytes", 1), (stop.GetProperty("reason").GetString(), stop.GetProperty("count").GetInt32()));
            Assert.Equal([2], stop.GetProperty("owners").EnumerateArray().Select(o => o.GetInt32()));
        });
        // names only: no path of the machine reaches the report (the folder name, which JSON does not escape)
        Assert.DoesNotContain(Path.GetFileName(_folder), File.ReadAllText(report));
    }

    [Fact]
    public void MoreThanFiveReasons_AreSummedAsOther_AndAChecksumMismatchEndsTheGroupLine()
    {
        // not exact for seven reasons: short by 1 byte twice, then by 2 to 7 bytes
        var records = new[] { 1, 1, 2, 3, 4, 5, 6, 7 }.Select((extra, i) => R((uint)i + 1, Body("Plain", [.. new byte[4 + extra]]))).ToArray();
        string source = P("many.rvt"), report = P("report.json");
        WriteFile(source, ("Formats/Latest", StoredTable), ("BasicFileInfo", SavedBy2022),
            ("Partitions/1", Partition((false, [records, [records[0] with { Extra = records[0].Extra + 1 }, .. records[1..]], records]))));

        var (exit, output, _) = Run(source, "--report", report);

        Assert.Equal(0, exit);
        Assert.EndsWith("; 1 checksum mismatches", output.Single(l => l.StartsWith("project 102:", StringComparison.Ordinal)));
        Assert.DoesNotContain("checksum", output.Single(l => l.StartsWith("project 103:", StringComparison.Ordinal)));
        Assert.Equal("  102 project Plain: 8 of 8; short by 1 bytes (2); short by 2 bytes (1); short by 3 bytes (1); short by 4 bytes (1); short by 5 bytes (1); other (2)",
            output.Single(l => l.StartsWith("  102 ", StringComparison.Ordinal)));
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        var row = json.RootElement.GetProperty("records").GetProperty("classes").EnumerateArray().Single(c => c.GetProperty("channel").GetInt32() == 102);
        Assert.Equal((5, 2), (row.GetProperty("stops").GetArrayLength(), row.GetProperty("otherStops").GetInt32()));
        Assert.Equal(8, row.GetProperty("stops").EnumerateArray().Sum(s => s.GetProperty("count").GetInt32()) + row.GetProperty("otherStops").GetInt32());
    }

    [Fact]
    public void AFileWhoseRecordsCannotBeCounted_IsStillMeasured_AndTheReasonIsGiven()
    {
        string report = P("report.json");

        var (exit, output, _) = Run(Sample(withPartition: false), "--report", report);

        Assert.Equal(0, exit);
        Assert.Contains("Streams: 2, ", output[1]);
        Assert.Equal("Records not counted: the file has no Partitions storage.", output[2]);
        Assert.Equal(3, output.Length);
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Contains("Partitions", json.RootElement.GetProperty("recordsFailure").GetString());
        Assert.False(json.RootElement.TryGetProperty("records", out _));
        Assert.Equal(2, json.RootElement.GetProperty("streams").GetArrayLength());
    }

    [Fact]
    public void AFileThatCannotBeRead_IsAnInputError_WithExitCode2_AndAnUnreadableReport()
    {
        string garbage = P("garbage.rvt");
        File.WriteAllText(garbage, "not a compound file at all");
        string notable = P("notable.rvt");
        WriteFile(notable, ("Partitions/1", OnePartition));
        // a gzip member whose dynamic deflate header gives all nineteen code length codes the length 1: the inflater fails on its own tables
        string badtable = P("badtable.rvt");
        WriteFile(badtable, ("Formats/Latest", [0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 0x05, 0xE0, 0x93, 0x24, 0x49, 0x92, 0x24, 0x49, 0x92, .. new byte[64]]));

        var cases = new (string Source, string Kind)[]
        {
            (garbage, "not-a-compound-file"),
            (P("missing.rvt"), "cannot-open"),
            (notable, "truncated-or-damaged"),
            (badtable, "truncated-or-damaged"),
        };
        foreach (var (source, kind) in cases)
        {
            string report = P("report-" + Path.GetFileNameWithoutExtension(source) + ".json");
            var (exit, output, error) = Run(source, "--report", report);

            Assert.True(exit == 2, $"{Path.GetFileName(source)}: exit {exit}");
            Assert.Empty(output);
            Assert.StartsWith("error: ", error);
            Assert.Contains("(exit code 2).", error);
            Assert.DoesNotContain(Path.GetFileName(_folder), File.ReadAllText(report));
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            var root = json.RootElement;
            Assert.Equal(("rvtio-coverage/1", "unreadable", kind, Path.GetFileName(source)), (root.GetProperty("schema").GetString(),
                root.GetProperty("status").GetString(), root.GetProperty("kind").GetString(), root.GetProperty("file").GetString()));
            Assert.False(string.IsNullOrEmpty(root.GetProperty("message").GetString()));
            Assert.False(root.TryGetProperty("streams", out _));
        }
    }

    [Fact]
    public void UsageErrors_ExitWith1_AndWriteNothing()
    {
        string source = Sample();
        string hash = Hash(source);
        Directory.CreateDirectory(P("folder"));
        string[] before = Listing();
        string[][] bad =
        [
            [],
            [""],
            [P("a\u0000b.rvt")],
            ["--report", P("report.json")],
            [source, source],
            [source, "--unknown"],
            [source, "--report"],
            [source, "--report", "--top"],
            [source, "--report", P("a.json"), "--report", P("b.json")],
            [source, "--report", source],
            [source, "--report", P("folder")],
            [source, "--report", ""],
            [source, "--top"],
            [source, "--top", "many"],
            [source, "--top", "-1"],
            [source, "--top", "1.5"],
        ];
        foreach (var args in bad)
        {
            var (exit, output, error) = Run(args);

            Assert.True(exit == 1, $"{string.Join(' ', args)}: exit {exit}");
            Assert.Empty(output);
            Assert.Contains("Usage: rvtio coverage", error);
            Assert.DoesNotContain('\n', error.TrimEnd());
        }
        Assert.Equal(before, Listing());
        Assert.Equal(hash, Hash(source));
    }
}
