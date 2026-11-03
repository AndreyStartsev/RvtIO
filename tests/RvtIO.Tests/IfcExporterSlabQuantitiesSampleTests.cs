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

    /// <summary>The BaseQuantities of the IfcSlab with this Tag, by quantity name.</summary>
    private static Dictionary<string, double> SlabQuantities(string ifc, string tag)
    {
        var slab = Regex.Match(ifc, @"^#(\d+)=IFCSLAB\('[^']*',#\d+,'(?:[^']|'')*',[^;]*,'" + tag + @"',", RegexOptions.Multiline);
        Assert.True(slab.Success, $"no IfcSlab with the tag {tag}");
        Match quantitySet = Match.Empty;
        foreach (Match relation in Regex.Matches(ifc, @"^#\d+=IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,[^;]*,\(#" + slab.Groups[1].Value + @"\),#(\d+)\);", RegexOptions.Multiline))
        {
            quantitySet = Regex.Match(ifc, @"^#" + relation.Groups[1].Value + @"=IFCELEMENTQUANTITY\('[^']*',#\d+,'BaseQuantities',[^;]*,\(([^)]*)\)\);", RegexOptions.Multiline);
            if (quantitySet.Success) break;
        }
        Assert.True(quantitySet.Success, $"the slab {tag} has no BaseQuantities");
        var result = new Dictionary<string, double>();
        foreach (Match item in Regex.Matches(quantitySet.Groups[1].Value, @"#(\d+)"))
        {
            var quantity = Regex.Match(ifc, @"^#" + item.Groups[1].Value + @"=IFCQUANTITY\w+\('(\w+)','[^']*',\$,([-0-9.E+]+)\);", RegexOptions.Multiline);
            Assert.True(quantity.Success);
            result[quantity.Groups[1].Value] = double.Parse(quantity.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        return result;
    }

    [SkippableFact]
    public void RstBasicSample_PileCapWidth_IsTheFirstSideOfThe400By400By900Cap()
    {
        string ifc = ExportIfc("rstbasicsampleproject.rvt");

        // the reference output and 0.1.0 give 400 (mm); the vertical side is 900
        Assert.Equal(400, SlabQuantities(ifc, "1381287")["Width"], 6);
    }

    [SkippableFact]
    public void Struct25Sample_PileCapWidth_AndRoundPileVolume_AreThoseOfTheReferenceOutput()
    {
        string ifc = ExportIfc("struct25.rvt");

        // a pile cap 36 x 36 x 24 inch: 3 feet in the reference output (2 feet is the vertical side)
        Assert.Equal(3, SlabQuantities(ifc, "682504")["Width"], 6);
        // a round pile 24 inch in diameter whose solids are open shells: the reference output has 76.02 cubic feet
        // (0.1.0 summed the open shells to 48.1, the first version of the foundation volume wrote none)
        Assert.Equal(76.0207, SlabQuantities(ifc, "683092")["NetVolume"], 0.5);
    }
}
