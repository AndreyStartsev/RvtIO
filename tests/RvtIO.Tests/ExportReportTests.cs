using System.Text.Json;
using OpenMcdf;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The export report, the exit codes and the reader limits (docs/export-report.md).</summary>
public class ExportReportTests
{
    [Fact]
    public void SkipLogGroupsByClassAndReasonLargestFirst()
    {
        var log = new SkipLog();
        for (int id = 100; id < 108; id++) log.Add(id, "OST_Walls", "type-element");
        log.Add(7, "OST_Doors", "hosted-element-without-host");
        log.Add(8, "OST_Doors", "hosted-element-without-host");
        log.Add(9, "OST_Abc", "type-element");
        log.Remove(8);

        Assert.Equal(10, log.Total);
        var groups = log.Groups();
        Assert.Equal(["OST_Walls", "OST_Abc", "OST_Doors"], groups.Select(g => g.Class));
        Assert.Equal([8, 1, 1], groups.Select(g => g.Count));
        Assert.Equal([100, 101, 102, 103, 104], groups[0].Examples);
        Assert.Equal([7], groups[2].Examples);
    }

    [Fact]
    public void SkipLogRemoveOfAnUnknownElementChangesNothing()
    {
        var log = new SkipLog();
        log.Add(1, "A", "r");
        log.Remove(2);
        Assert.Equal(1, log.Total);
    }

    [SkippableFact]
    public void SameFileComparesTheFullPathsAndTellsDifferentFilesApart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rvt-samefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "a.rvt"), other = Path.Combine(folder, "b.json");
            File.WriteAllText(input, "x");
            string viaDots = Path.Combine(folder, "sub", "..", "a.rvt");

            Assert.True(ExportReport.SameFile(input, input));
            Assert.True(ExportReport.SameFile(input, viaDots));
            Assert.False(ExportReport.SameFile(input, other));
            if (OperatingSystem.IsWindows())
                Assert.True(ExportReport.SameFile(input, input.ToUpperInvariant()));

            string link = Path.Combine(folder, "link.rvt");
            try { File.CreateSymbolicLink(link, input); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SkipException("symbolic links cannot be created here"); }
            Assert.True(ExportReport.SameFile(link, input));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FailedReportDropsSourcePathsFromTheStackTrace()
    {
        var report = ExportReport.Failed("a.rvt", 1, "decode-failure", "m", RvtExitCodes.DecodeError, null, false, false,
            "System.Exception: x\n   at A.B() in /build/work dir/A.cs:line 12\n   at C.D()");
        Assert.Equal("System.Exception: x\n   at A.B()\n   at C.D()", report.Error!.Detail);
    }

    [Fact]
    public void FailedReportSerializesTheDocumentedFields()
    {
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));
        var report = ExportReport.Failed("a.rvt", 12, "truncated-or-damaged", "it is cut", RvtExitCodes.InputError, diagnostics, raised: false, partialAllowed: true);

        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Assert.Equal("rvtio-report/1", root.GetProperty("schema").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("a.rvt", root.GetProperty("input").GetProperty("file").GetString());
        Assert.Equal("truncated-or-damaged", root.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("it is cut", root.GetProperty("error").GetProperty("message").GetString());
        Assert.True(root.GetProperty("limits").GetProperty("partialAllowed").GetBoolean());
        var hit = root.GetProperty("limits").GetProperty("hit")[0];
        Assert.Equal("elem-table-recovery-bytes", hit.GetProperty("limit").GetString());
        Assert.Equal(1000, hit.GetProperty("value").GetInt64());
        Assert.False(root.TryGetProperty("export", out _));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void ALimitHitExitsWithTheLimitCodeUnlessPartialOutputIsAllowed(bool allowPartial, int expected)
    {
        var diagnostics = new RvtReadDiagnostics();
        Assert.Equal(0, RvtExitCodes.ForRead(diagnostics, allowPartial));
        diagnostics.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));
        Assert.Equal(expected, RvtExitCodes.ForRead(diagnostics, allowPartial));
    }

    [Fact]
    public void ExportPartComputesFractionsAndKeepsEverySkippedClass()
    {
        var report = ExportReport.Failed("a.rvt", 1, "x", "x", 2, null, false, false);
        var skipped = new SkipLog();
        skipped.Add(1, "OST_Walls", "type-element");
        skipped.Add(2, "OST_Doors", "hosted-element-without-host");
        skipped.Add(3, "OST_Doors", "type-element");
        report.SetExport("ifc", 1, skipped);

        Assert.Equal(4, report.Export!.Considered);
        Assert.Equal(0.25, report.Export.ExportedFraction);
        Assert.Equal(3, report.Export.SkippedByClass.Count);
        Assert.Equal(3, report.Export.SkippedByClass.Sum(g => g.Count));
        Assert.All(report.Export.SkippedByClass, g => Assert.False(string.IsNullOrEmpty(g.Reason)));
    }

    [Fact]
    public void ExportOfNothingIsFullyExported()
    {
        var report = ExportReport.Failed("a.rvt", 1, "x", "x", 2, null, false, false);
        report.SetExport("json", 0, new SkipLog());
        Assert.Equal(1.0, report.Export!.ExportedFraction);
    }

    [Fact]
    public void LimitsFollowTheDocumentedDefaultsAndRaiseToThePlatformLength()
    {
        Assert.Equal((256 * 1024 * 1024, 4096), ElemTableRecover.Limits(false));
        Assert.Equal((Array.MaxLength, int.MaxValue), ElemTableRecover.Limits(true));
    }

    [Fact]
    public void AMissingFileIsAnInputError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "rr-missing-" + Guid.NewGuid() + ".rvt");
        var error = Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(missing));
        Assert.Equal("cannot-open", error.Kind);
        // the message goes into the report: the file name, never the directory
        Assert.DoesNotContain(Path.GetTempPath(), error.Message);
        Assert.Contains(Path.GetFileName(missing), error.Message);
    }

    [Fact]
    public void AnEmptyFileIsAnInputError()
    {
        using var file = new TempFile([]);
        Assert.Equal("empty-file", Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path)).Kind);
    }

    [Fact]
    public void ATextFileIsNotACompoundFile()
    {
        using var file = new TempFile("hello"u8.ToArray());
        Assert.Equal("not-a-compound-file", Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path)).Kind);
        Assert.Throws<RvtInputException>(() => new RvtFileReader().Read(file.Path));
    }

    [SkippableTheory]
    [InlineData(2)] [InlineData(3)] [InlineData(10)]
    public void ASampleCutToAFractionIsATruncatedFile(int divisor)
    {
        byte[] whole = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        using var file = new TempFile(whole[..(whole.Length / divisor)]);
        var error = Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path));
        Assert.Contains(error.Kind, new[] { "truncated-or-damaged", "not-a-compound-file" });
        Assert.DoesNotContain("   at ", error.Message);
        // the reader raises the same error and does not start a decoder
        Assert.IsType<RvtInputException>(Record.Exception(() => new RvtFileReader().Read(file.Path)));
    }

    [SkippableTheory]
    [InlineData(4096)] [InlineData(65536)] [InlineData(1_000_000)]
    public void ASampleWithItsTailCutOffDoesNotPassTheCheck(int cut)
    {
        byte[] whole = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        using var file = new TempFile(whole[..(whole.Length - cut)]);
        Assert.IsType<RvtInputException>(Record.Exception(() => RvtContainerCheck.Verify(file.Path)));
    }

    [SkippableFact]
    public void ASampleWithADamagedHeaderIsNotACompoundFile()
    {
        byte[] bytes = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        Array.Clear(bytes, 0, 512);
        using var file = new TempFile(bytes);
        Assert.Equal("not-a-compound-file", Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(file.Path)).Kind);
    }

    [SkippableFact]
    public void ASampleWithAZeroedMiddleDoesNotEndInAnUnhandledException()
    {
        byte[] bytes = File.ReadAllBytes(TestFiles.Find("TESTFILE.rvt"));
        Array.Clear(bytes, bytes.Length / 2, 4096);
        using var file = new TempFile(bytes);
        // zeroed payload is the decoders' business (recovery); the container itself stays readable
        var failure = Record.Exception(() => RvtContainerCheck.Verify(file.Path));
        Assert.True(failure == null || failure is RvtInputException, failure?.ToString());
    }

    [SkippableFact]
    public void AnIntactSampleReportsEveryPartitionStreamDecodedInFull()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        var diagnostics = new RvtReadDiagnostics();
        var reader = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics });
        var info = reader.Read(path);

        Assert.NotEmpty(diagnostics.Streams);
        Assert.All(diagnostics.Streams, s =>
        {
            Assert.StartsWith("Partitions/", s.Name);
            Assert.True(s.Members > 0);
            Assert.Equal(s.Members, s.Decoded);
            Assert.Equal(0, s.Failed);
        });
        Assert.Empty(diagnostics.LimitHits);
        var report = ExportReport.ForRead("TESTFILE.rvt", new FileInfo(path).Length, diagnostics, false, false,
            reader.LastParsingState!.ElemTable.Keys.ToList(), info);
        Assert.Equal("complete", report.Status);
        Assert.Equal(0, report.ExitCode);
        Assert.Empty(report.Issues);
        Assert.Equal(1.0, report.Elements!.DecodedFraction);
        report.SetJsonExport(info, full: false);
        Assert.Equal(info.Elements.Count, report.Export!.Considered);
        Assert.Equal(report.Export.Skipped, report.Export.SkippedByClass.Sum(g => g.Count));
    }

    [SkippableTheory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void AReadCutByALimitIsPartialAndExitsByTheOption(bool allowPartial, int expectedExit)
    {
        var diagnostics = new RvtReadDiagnostics();
        var reader = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics });
        var info = reader.Read(TestFiles.Find("TESTFILE.rvt"));
        diagnostics.LimitHits.Add(new ReaderLimitHit("Global/ElemTable", "elem-table-recovery-bytes", 1000, true, "cut"));

        var report = ExportReport.ForRead("TESTFILE.rvt", 1, diagnostics, false, allowPartial, reader.LastParsingState!.ElemTable.Keys.ToList(), info);

        Assert.Equal("partial", report.Status);
        Assert.Equal(expectedExit, report.ExitCode);
        Assert.Single(report.Limits.Hit);
        Assert.Equal(allowPartial, report.Limits.PartialAllowed);
    }

    [SkippableFact]
    public void AnElementTableWithoutRowsIsAPartialRead()
    {
        var diagnostics = new RvtReadDiagnostics();
        var info = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics }).Read(TestFiles.Find("TESTFILE.rvt"));

        var report = ExportReport.ForRead("TESTFILE.rvt", 1, diagnostics, false, false, [], info);

        Assert.Equal("partial", report.Status);
        Assert.Equal(0, report.ExitCode);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(("Global/ElemTable", "no-rows"), (issue.Stream, issue.Code));
        Assert.Equal(1.0, report.Elements!.DecodedFraction);
    }

    [SkippableFact]
    public void TheElemTableRecoveryStopsAtItsLimitsAndSaysWhich()
    {
        byte[] data;
        using (var source = RootStorage.OpenRead(TestFiles.Find("Test_2020.rvt")))
        using (var stream = source.OpenStorage("Global").OpenStream("ElemTable"))
        {
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        int gzStart = Array.FindIndex(data, i => i == 0x1F);
        while (data[gzStart + 1] != 0x8B) gzStart = Array.FindIndex(data, gzStart + 1, i => i == 0x1F);

        Assert.Null(ElemTableRecover.Recover(data, gzStart, out var bytesCut, (1000, 4096)));
        Assert.Equal(("elem-table-recovery-bytes", 1000L), bytesCut.LimitHit);

        ElemTableRecover.Recover(data, gzStart, out var iterationsCut, (ElemTableRecover.DefaultMaxRecoveredBytes, 0));
        Assert.Equal(("elem-table-recovery-iterations", 0L), iterationsCut.LimitHit);

        ElemTableRecover.Recover(data, gzStart, out var inLimits);
        Assert.Null(inLimits.LimitHit);

        // A container whose ElemTable fails the exact paged decode (one flipped byte in the middle) is read through the recovery tier.
        byte[] damaged = (byte[])data.Clone();
        damaged[damaged.Length / 2] ^= 0xFF;
        if (PagedGzipStream.Decompress(damaged) != null) throw new SkipException("the damaged copy still decodes exactly");
        var container = new MemoryStream();
        using (var created = RootStorage.Create(container, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            using (var table = created.CreateStorage("Global").CreateStream("ElemTable")) table.Write(damaged);
        }
        container.Position = 0;
        using var root = RootStorage.Open(container, StorageModeFlags.LeaveOpen);
        var diagnostics = new RvtReadDiagnostics();
        ElemTableReader.Read(root, out _, out _, out _, (1000, 4096), diagnostics);
        var hit = Assert.Single(diagnostics.LimitHits);
        Assert.Equal(("Global/ElemTable", "elem-table-recovery-bytes", 1000L, true), (hit.Stream, hit.Limit, hit.Value, hit.Raisable));

        var inLimitsDiagnostics = new RvtReadDiagnostics();
        ElemTableReader.Read(root, out _, out _, out _, null, inLimitsDiagnostics);
        Assert.Empty(inLimitsDiagnostics.LimitHits);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rr-report-" + Guid.NewGuid() + ".rvt");

        public TempFile(byte[] content) => File.WriteAllBytes(Path, content);

        public void Dispose() => File.Delete(Path);
    }
}
