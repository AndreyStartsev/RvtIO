using RvtIO.Cli;
using Xunit;

namespace RvtIO.Tests;

public class CurrentCopiesOptionTests
{
    [Fact]
    public void WithoutTheOption_OnlyTheCurrentCopyIsRead()
        => Assert.True(CurrentCopiesOption.Resolve(["input.rvt", "output.ifc", "--ifc-phase", "last"]));

    [Fact]
    public void TheKeepAllOption_ReadsEveryCopy_WhereverItStands()
    {
        Assert.False(CurrentCopiesOption.Resolve(["input.rvt", "output.ifc", "--keep-all-copies"]));
        Assert.False(CurrentCopiesOption.Resolve(["input.rvt", "--keep-all-copies", "output.ifc"]));
    }

    [Fact]
    public void TheEarlierResolveOption_IsAcceptedAndChangesNothing()
    {
        Assert.Contains("--resolve-current-copies", CurrentCopiesOption.Names);
        Assert.True(CurrentCopiesOption.Resolve(["input.rvt", "output.ifc", "--resolve-current-copies"]));
        Assert.False(CurrentCopiesOption.Resolve(["input.rvt", "--resolve-current-copies", "--keep-all-copies"]));
    }

    [Fact]
    public void TheKeepAllOption_IsAcceptedAsASwitch() => Assert.Contains("--keep-all-copies", CurrentCopiesOption.Names);

    [Fact]
    public void TheLibraryResolvesTheCurrentCopyByDefault() => Assert.True(new RvtReaderOptions().ResolveCurrentCopies);
}
