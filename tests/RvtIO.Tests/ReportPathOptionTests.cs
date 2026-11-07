using RvtIO.Cli;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The output paths the export refuses a --report path against: the refusal covered only the input and the
/// export output, while the dump options name further outputs the run would overwrite.</summary>
public class ReportPathOptionTests
{
    [Fact]
    public void OutputPaths_NameTheInputAndTheExportOutput()
    {
        string[] args = ["in.rvt", "out.ifc"];
        Assert.Equal(["in.rvt", "out.ifc"], ReportPathOption.OutputPaths(args, "in.rvt", "out.ifc", null));
    }

    [Fact]
    public void OutputPaths_NameEveryDumpPathTheCommandLineNames()
    {
        string[] args =
        [
            "in.rvt", "out.json", "--dev-external-dump", "dump.txt", "--dev-candidates", "candidates.tsv",
            "--dev-trace", "trace.tsv", "--dev-facts", "facts.tsv", "--dev-solids", "solids.tsv",
            "--dev-user-params", "parameters.tsv", "--dev-collisions", "collisions.tsv",
        ];
        Assert.Equal(
            ["in.rvt", "out.json", "dump.txt", "candidates.tsv", "trace.tsv", "facts.tsv", "solids.tsv", "parameters.tsv", "collisions.tsv"],
            ReportPathOption.OutputPaths(args, "in.rvt", "out.json", "dump.txt"));
    }

    [Fact]
    public void OutputPaths_OmitTheDumpsThatAreNotNamed()
    {
        // --dev-fact-names takes a list of names, not a path; the dump options that are absent name nothing.
        string[] args = ["in.rvt", "out.json", "--dev-fact-names", "a,b", "--dev-trace", "trace.tsv"];
        Assert.Equal(["in.rvt", "out.json", "trace.tsv"], ReportPathOption.OutputPaths(args, "in.rvt", "out.json", null));
    }
}
