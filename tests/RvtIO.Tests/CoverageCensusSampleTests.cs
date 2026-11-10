using RvtIO.Cli;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>The coverage census on the project files of three releases in the private fixture folder (<see cref="TestFiles"/>); skipped
/// without them. Only what holds for every file is asserted: the measured counts are printed, not pinned, because they grow as the
/// decoding does.</summary>
public class CoverageCensusSampleTests(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("Test_2020.rvt")]
    [InlineData("Test_2022.rvt")]
    [InlineData("Test_2025.rvt")]
    public void EveryPartitionFrames_AndTheRowsAddUp(string fileName)
    {
        var result = CoverageCensus.Read(TestFiles.Find(fileName));
        foreach (var group in result.Groups) output.WriteLine(CoverageCommand.GroupLine(group));

        Assert.Null(result.RecordsFailure);
        var partitions = result.Streams.Where(s => s.Kind == "partition").ToList();
        Assert.NotEmpty(partitions);
        Assert.All(partitions, s => Assert.Equal("ok", s.Framing));
        CoverageCensusTests.AssertRowsAddUp(result);
        Assert.All(result.Classes, c =>
        {
            Assert.InRange(c.Sums.Exact, 0, c.Sums.Items);
            Assert.InRange(c.Sums.ExactBytes, 0, c.Sums.PayloadBytes);
        });
        var project = result.Groups.Where(g => !g.Family).ToList();
        Assert.Equal([101, 102, 103], project.Select(g => g.Channel));
        Assert.Single(project.Select(g => g.Sums.Items).Distinct());
    }
}
