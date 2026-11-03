using RvtIO.Cli;
using Xunit;

namespace RvtIO.Tests;

public class OpeningAxesOptionTests
{
    [Fact]
    public void WithoutTheOption_TheOpeningHasTheSidesOfTheDoorOrWindow()
        => Assert.False(OpeningAxesOption.Legacy(["input.rvt", "output.ifc", "--ifc-phase", "last"]));

    [Fact]
    public void TheLegacyOption_AsksForTheSwappedSides_WhereverItStands()
    {
        Assert.True(OpeningAxesOption.Legacy(["input.rvt", "output.ifc", "--ifc-opening-legacy-axes"]));
        Assert.True(OpeningAxesOption.Legacy(["input.rvt", "--ifc-opening-legacy-axes", "output.ifc"]));
    }

    [Fact]
    public void TheEarlierHostAxesOption_IsAcceptedAndChangesNothing()
    {
        Assert.Contains("--ifc-opening-host-axes", OpeningAxesOption.Names);
        Assert.False(OpeningAxesOption.Legacy(["input.rvt", "output.ifc", "--ifc-opening-host-axes"]));
    }

    [Fact]
    public void TheLegacyOption_IsAcceptedAsASwitch() => Assert.Contains("--ifc-opening-legacy-axes", OpeningAxesOption.Names);
}
