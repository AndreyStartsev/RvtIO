using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Wall sweeps are written as building element proxies; the reveal kind of wall sweep is not written at all.</summary>
public class IfcWallSweepProxyTests
{
    private static string Export(params Element[] elements)
    {
        var info = new RevitFileInfo();
        foreach (var element in elements) info.Elements.Add(element);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-sweep-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter().Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WallSweep_IsWrittenAsProxy_AndRevealIsNot()
    {
        var sweep = IfcExporterCoveringCommonPropertySetTests.Product(100, 200, -2000181, "OST_Cornices", "Sweep");
        var reveal = IfcExporterCoveringCommonPropertySetTests.Product(101, 201, -2000182, "OST_Reveals", "Reveal");
        string text = Export(sweep, reveal);
        var proxies = Regex.Matches(text, @"=IFCBUILDINGELEMENTPROXY\([^\r\n]*,'(\d+)',\$\);").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["100"], proxies);
    }

    [Fact]
    public void RevealOnly_WritesNoProxy()
    {
        var reveal = IfcExporterCoveringCommonPropertySetTests.Product(101, 201, -2000182, "OST_Reveals", "Reveal");
        Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", Export(reveal));
    }

    [Fact]
    public void WallSweep_ProxyCarriesFamilyTypeIdNameAndTag_AndNothingIsFilled()
    {
        var sweep = IfcExporterCoveringCommonPropertySetTests.Product(100, 200, -2000181, "OST_Cornices", "Sweep");
        string text = Export(sweep);
        string line = Regex.Match(text, @"=IFCBUILDINGELEMENTPROXY\([^\r\n]*\);").Value;
        Assert.Contains(",'Covering-common test family:Sweep:100','','Covering-common test family:Sweep',", line);
        Assert.EndsWith(",'100',$);", line);
        Assert.DoesNotContain("=IFCRELFILLSELEMENT(", text);
    }
}
