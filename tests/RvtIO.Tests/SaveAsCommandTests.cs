using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RvtIO.Cli;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Tests <c>rvtio save-as</c> (<see cref="SaveAsCommand"/> and <c>Program.Main</c> routing) across supported and refused
/// synthetic Revit files, JSON report generation, and usage/input error handling without requiring private fixtures.
/// </summary>
public sealed class SaveAsCommandTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-saveas-cli-" + Guid.NewGuid().ToString("N"));

    public SaveAsCommandTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string P(string name) => Path.Combine(_folder, name);

    private static string FileSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static (int Exit, string Out, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = SaveAsCommand.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static (int Exit, string Out, string Error) RunCliProcess(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The CLI process did not finish within two minutes.");
        Task.WaitAll(output, error);
        return (process.ExitCode, output.Result, error.Result);
    }

    [Fact]
    public void Check_OnSupportedProjectAndFamily_ReturnsZero_WritesCheckReport_AndLeavesSourceIntact()
    {
        string source = RvtWriterTests.CreateSyntheticRevitFile(
            P("project_2025.rvt"),
            new RvtWriterTests.SyntheticFileOptions
            {
                FileName = "project_2025.rvt",
                Kind = RvtFileKind.Project,
                SchemaRelease = 2025,
                SavedRelease = 2025,
            });
        string sourceHash = FileSha256(source);
        string report = P("check_report.json");

        var (exit, output, error) = Run(source, "--check", "--report", report);

        Assert.Equal(RvtExitCodes.Success, exit);
        Assert.Contains("Report written to", error);
        Assert.Contains("Source: project_2025.rvt (project, release 2025, 8-byte ids", output);
        Assert.Contains("save-as: supported", output);
        Assert.Contains("paged-stream-rewrite: supported", output);
        Assert.Contains("partition-record-rewrite: supported", output);
        Assert.Equal(sourceHash, FileSha256(source));

        using var json = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(SaveAsCommand.ReportSchema, json.RootElement.GetProperty("schema").GetString());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("check", json.RootElement.GetProperty("mode").GetString());
        var srcNode = json.RootElement.GetProperty("source");
        Assert.Equal("project", srcNode.GetProperty("fileKind").GetString());
        Assert.Equal(2025, srcNode.GetProperty("effectiveRelease").GetInt32());
        Assert.Equal(8, srcNode.GetProperty("elementIdWidth").GetInt32());
        Assert.Equal(3, srcNode.GetProperty("operations").GetArrayLength());
    }

    [Fact]
    public void Check_AndWrite_OnRefusedFile_ReturnExitFive_WriteNoOutput_AndRecordRefusalReason()
    {
        string workshared = RvtWriterTests.CreateSyntheticRevitFile(
            P("workshared_2026.rvt"),
            new RvtWriterTests.SyntheticFileOptions
            {
                FileName = "workshared_2026.rvt",
                SchemaRelease = 2026,
                SavedRelease = 2026,
                Worksharing = "Central",
            });
        string sourceHash = FileSha256(workshared);
        string checkReport = P("refused_check.json");

        var (checkExit, checkOut, checkErr) = Run(workshared, "--check", "--report", checkReport);
        Assert.Equal(SaveAsCommand.Refused, checkExit);
        Assert.Contains("Report written to", checkErr);
        Assert.Contains("save-as: refused (workshared-model:", checkOut);

        using (var json = JsonDocument.Parse(File.ReadAllText(checkReport)))
        {
            Assert.Equal("refused", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("check", json.RootElement.GetProperty("mode").GetString());
        }

        string blockedOut = P("should_not_be_created.rvt");
        string writeReport = P("refused_write.json");
        var (writeExit, writeOut, writeErr) = Run(workshared, blockedOut, "--report", writeReport);

        Assert.Equal(SaveAsCommand.Refused, writeExit);
        Assert.Equal("", writeOut);
        Assert.Contains("Nothing was written (exit code 5)", writeErr);
        Assert.False(File.Exists(blockedOut));
        Assert.Equal(sourceHash, FileSha256(workshared));

        using (var json = JsonDocument.Parse(File.ReadAllText(writeReport)))
        {
            Assert.Equal(SaveAsCommand.ReportSchema, json.RootElement.GetProperty("schema").GetString());
            Assert.Equal("refused", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("workshared-model", json.RootElement.GetProperty("kind").GetString());
        }
    }

    [Fact]
    public void Write_ViaCommandAndCliEntryPoint_CreatesVerifiedLosslessCopy_AndWritesReport()
    {
        string source = RvtWriterTests.CreateSyntheticRevitFile(
            P("family_2024.rfa"),
            new RvtWriterTests.SyntheticFileOptions
            {
                FileName = "family_2024.rfa",
                Kind = RvtFileKind.Family,
                SchemaRelease = 2024,
                SavedRelease = 2024,
            });
        string sourceHash = FileSha256(source);
        string copy1 = P("copy1.rfa");
        string report1 = P("write_report.json");

        var (exit, output, error) = Run(source, copy1, "--report", report1);

        Assert.Equal(RvtExitCodes.Success, exit);
        Assert.Contains("Report written to", error);
        Assert.Contains("Saved: copy1.rfa (family, release 2024,", output);
        Assert.True(File.Exists(copy1));
        Assert.Equal(sourceHash, FileSha256(source));

        using (var json = JsonDocument.Parse(File.ReadAllText(report1)))
        {
            Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("write", json.RootElement.GetProperty("mode").GetString());
            var outNode = json.RootElement.GetProperty("output");
            Assert.Equal("copy1.rfa", outNode.GetProperty("file").GetString());
            Assert.Equal(0, outNode.GetProperty("replacedStreams").GetInt32());
            Assert.Equal(0, outNode.GetProperty("rewrittenRecords").GetInt32());
        }

        // Also exercise the compiled CLI process entry point (`rvtio save-as ...` and `rvtio --help`)
        var (helpExit, helpOut, _) = RunCliProcess("--help");
        Assert.Equal(RvtExitCodes.Success, helpExit);
        Assert.Contains("save-as <source.rvt|source.rfa>", helpOut);

        string copy2 = P("copy2.rfa");
        var (procExit, procOut, procErr) = RunCliProcess("save-as", copy1, copy2);
        Assert.Equal(RvtExitCodes.Success, procExit);
        Assert.Equal("", procErr);
        Assert.Contains("Saved: copy2.rfa", procOut);

        var reopened = RvtDocument.Open(copy2);
        Assert.NotNull(reopened.TransmissionData);
    }

    [Fact]
    public void InputAndIoFailures_ReturnExitTwo_AndPreserveExistingFiles()
    {
        // Missing input file -> exit 2
        var (missingExit, _, missingErr) = Run(P("does_not_exist.rvt"), P("out.rvt"));
        Assert.Equal(RvtExitCodes.InputError, missingExit);
        Assert.Contains("exit code 2", missingErr);

        // Non-CFB file -> exit 2
        string garbage = P("not_cfb.rvt");
        File.WriteAllText(garbage, "plain text file");
        var (badExit, _, _) = Run(garbage, P("out2.rvt"));
        Assert.Equal(RvtExitCodes.InputError, badExit);
        Assert.False(File.Exists(P("out2.rvt")));

        // Existing destination file -> exit 2 and destination untouched
        string source = RvtWriterTests.CreateSyntheticRevitFile(P("valid.rvt"));
        string existing = P("existing_target.rvt");
        File.WriteAllBytes(existing, [9, 8, 7, 6]);
        var (overExit, _, _) = Run(source, existing);
        Assert.Equal(RvtExitCodes.InputError, overExit);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(existing));
    }

    [Fact]
    public void UsageErrors_ReturnExitOne_BeforeReadingOrWriting()
    {
        string source = P("nonexistent_for_usage.rvt");
        string dest = P("dest.rvt");

        Assert.Equal(RvtExitCodes.Usage, Run().Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source).Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--check").Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, P("extra.rvt")).Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--unknown-flag").Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--report").Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--report", "--check").Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--report", P("r1.json"), "--report", P("r2.json")).Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--report", source).Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--report", dest).Exit);
        Assert.Equal(RvtExitCodes.Usage, Run(source, dest, "--report", _folder).Exit);
    }
}
