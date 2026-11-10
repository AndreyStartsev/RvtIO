using System.Diagnostics;
using System.Text.Json;
using OpenMcdf;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The exit code and the report of the command line tool itself for an input it cannot read (docs/export-report.md).
/// The tool is started as a process of its own, from the copy the test project builds next to the tests.</summary>
public class CliExitCodeTests
{
    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        // both pipes are drained, or a full one would block the tool
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    private static void RunOn(byte[] input, params string[] kinds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "input.rvt"), ifc = Path.Combine(directory, "output.ifc"), report = Path.Combine(directory, "report.json");
            File.WriteAllBytes(rvt, input);

            Assert.Equal(RvtExitCodes.InputError, Run(rvt, ifc, "--report", report));

            Assert.False(File.Exists(ifc));
            using var json = JsonDocument.Parse(File.ReadAllText(report));
            Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(RvtExitCodes.InputError, json.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(json.RootElement.GetProperty("error").GetProperty("kind").GetString(), kinds);
            // without a report the exit code is the same
            Assert.Equal(RvtExitCodes.InputError, Run(rvt, ifc));
            Assert.False(File.Exists(ifc));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>A compound file with one stream of the given size, as bytes.</summary>
    private static byte[] CompoundFile(int streamBytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-cli-" + Guid.NewGuid().ToString("N") + ".cfb");
        try
        {
            using (var root = RootStorage.Create(path, OpenMcdf.Version.V3))
            using (var stream = root.CreateStream("Contents"))
                stream.Write(new byte[streamBytes]);
            return File.ReadAllBytes(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AnInputThatIsNoCompoundFile_EndsWithTheInputErrorCodeAndAFailedReport() =>
        RunOn("this is not a Revit file"u8.ToArray(), "not-a-compound-file");

    [Fact]
    public void AnEmptyInput_EndsWithTheInputErrorCode() => RunOn([], "empty-file");

    [Fact]
    public void ACompoundFileCutInHalf_EndsWithTheInputErrorCodeAndAFailedReport()
    {
        byte[] whole = CompoundFile(400_000);
        RunOn(whole[..(whole.Length / 2)], "truncated-or-damaged", "not-a-compound-file");
    }
}
