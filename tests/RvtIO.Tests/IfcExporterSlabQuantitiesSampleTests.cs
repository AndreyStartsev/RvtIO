using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The base quantities of foundation slabs on the Revit sample projects, against the values of the reference output. The samples
/// are not part of the repository: a test is skipped when its file is not in the test file folder (see <see cref="TestFiles"/>).</summary>
public class IfcExporterSlabQuantitiesSampleTests
{
    /// <summary>Exports the project as the command line tool does by default and returns the IFC text.</summary>
    private static string ExportIfc(string fileName)
    {
        string path = TestFiles.Find(fileName);
        var options = new RvtReaderOptions { R22Layout = true, SeparateFacesWithDistinctOpenings = true, SystemFamilyNameLocale = "ENU" };
        var info = new RvtFileReader(options).Read(path);
        string output = TestFiles.Scratch($"{Path.GetFileNameWithoutExtension(fileName)}.slab-quantities.ifc");
        try
        {
            new IfcExporter(TextWriter.Null) { BaseQuantities = true }.Export(info, output);
            return File.ReadAllText(output);
        }
        finally { File.Delete(output); }
    }

    /// <summary>The BaseQuantities, by quantity name, of every IfcSlab whose ObjectType starts with <paramref name="objectTypePrefix"/>.</summary>
    private static List<Dictionary<string, double>> SlabQuantities(string ifc, string objectTypePrefix)
    {
        var slabs = new List<Dictionary<string, double>>();
        foreach (Match slab in Regex.Matches(ifc, @"^#(\d+)=IFCSLAB\('[^']*',#\d+,'(?:[^']|'')*','(?:[^']|'')*','((?:[^']|'')*)',", RegexOptions.Multiline))
        {
            if (!slab.Groups[2].Value.Replace("''", "'").StartsWith(objectTypePrefix, StringComparison.Ordinal)) continue;
            Match quantitySet = Match.Empty;
            foreach (Match relation in Regex.Matches(ifc, @"^#\d+=IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,[^;]*,\(#" + slab.Groups[1].Value + @"\),#(\d+)\);", RegexOptions.Multiline))
            {
                quantitySet = Regex.Match(ifc, @"^#" + relation.Groups[1].Value + @"=IFCELEMENTQUANTITY\('[^']*',#\d+,'BaseQuantities',[^;]*,\(([^)]*)\)\);", RegexOptions.Multiline);
                if (quantitySet.Success) break;
            }
            var result = new Dictionary<string, double>();
            if (quantitySet.Success)
                foreach (Match item in Regex.Matches(quantitySet.Groups[1].Value, @"#(\d+)"))
                {
                    var quantity = Regex.Match(ifc, @"^#" + item.Groups[1].Value + @"=IFCQUANTITY\w+\('(\w+)','[^']*',\$,([-0-9.E+]+)\);", RegexOptions.Multiline);
                    Assert.True(quantity.Success);
                    result[quantity.Groups[1].Value] = double.Parse(quantity.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            slabs.Add(result);
        }
        return slabs;
    }

    [SkippableFact]
    public void RstBasicSample_PileCapWidth_IsTheFirstSideOfThe400By400By900Cap()
    {
        string ifc = ExportIfc("rstbasicsampleproject.rvt");

        // the reference output and 0.1.0 give 400 (mm); the vertical side is 900
        var caps = SlabQuantities(ifc, "Pile Cap-Rectangular:400 x 400 x 900mm");
        Assert.NotEmpty(caps);
        Assert.All(caps, cap => Assert.Equal(400, cap["Width"], 6));
    }

    [SkippableFact]
    public void Struct25Sample_PileCapWidth_AndRoundPileVolume_AreThoseOfTheReferenceOutput()
    {
        string ifc = ExportIfc("struct25.rvt");

        // pile caps 36 x 36 x 24 inch: 3 feet in the reference output (2 feet is the vertical side)
        var caps = SlabQuantities(ifc, "SFD_Pile_Cap_Rectangle:");
        Assert.NotEmpty(caps);
        Assert.All(caps, cap => Assert.Equal(3, cap["Width"], 6));
        // round piles 24 inch in diameter whose solids are open shells: 73.8 or 76.0 cubic feet in the reference output
        // (0.1.0 summed the open shells to several times that, the first version of the foundation volume wrote none)
        var piles = SlabQuantities(ifc, "SFD_Round:24x24x25'");
        Assert.NotEmpty(piles);
        Assert.All(piles, pile => Assert.InRange(pile["NetVolume"], 73.8, 76.2));
    }

    [SkippableFact]
    public void Struct25Sample_RoundPileWidth_IsTheDiameterItsTypeStores()
    {
        string ifc = ExportIfc("struct25.rvt");

        // a round pile has no extrusion to measure, so the reference output gives it the Width parameter its type stores,
        // the diameter of its round profile: 2 feet for the 24 inch families, 2.5 feet for the 30 inch one
        var piles25 = SlabQuantities(ifc, "SFD_Round:24x24x25'");
        Assert.NotEmpty(piles25);
        Assert.All(piles25, pile => Assert.Equal(2, pile["Width"], 6));
        var piles30 = SlabQuantities(ifc, "SFD_Round:30x30x25'");
        Assert.NotEmpty(piles30);
        Assert.All(piles30, pile => Assert.Equal(2.5, pile["Width"], 6));
        var piles20 = SlabQuantities(ifc, "SFD_Round:24x24x20'");
        Assert.NotEmpty(piles20);
        Assert.All(piles20, pile => Assert.Equal(2, pile["Width"], 6));
    }
}
