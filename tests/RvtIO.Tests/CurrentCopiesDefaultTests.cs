using Xunit;

namespace RvtIO.Tests;

/// <summary>The current copy of an element that several partition streams hold is read by default, and ResolveCurrentCopies = false reads every copy.</summary>
public class CurrentCopiesDefaultTests
{
    private static ParsedRead Read(string path, bool resolve)
    {
        var reader = new RvtFileReader(new RvtReaderOptions { ResolveCurrentCopies = resolve });
        var info = reader.Read(path);
        return new ParsedRead(info.Elements.Count, reader.LastParsingState!.StaleCopiesRemoved);
    }

    private sealed record ParsedRead(int Elements, bool StaleCopiesRemoved);

    // TESTFILE.rvt is saved in five increments (partition streams 0, 1, 3, 4, 5) and 68 element ids occur in more than one of them.
    [SkippableFact]
    public void AFileSavedInSeveralIncrements_DropsTheSupersededCopiesByDefault_AndKeepsThemOnRequest()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        var byDefault = Read(path, new RvtReaderOptions().ResolveCurrentCopies);
        var keepAll = Read(path, resolve: false);
        Assert.True(byDefault.StaleCopiesRemoved);
        Assert.False(keepAll.StaleCopiesRemoved);
        Assert.NotEqual(keepAll.Elements, byDefault.Elements);
    }

    [SkippableFact]
    public void AFileWithOnePartitionStream_ReadsTheSameEitherWay()
    {
        string path = TestFiles.Find("test_angled_wall.rvt");
        var byDefault = Read(path, new RvtReaderOptions().ResolveCurrentCopies);
        Assert.False(byDefault.StaleCopiesRemoved);
        Assert.Equal(Read(path, resolve: false), byDefault);
    }
}
