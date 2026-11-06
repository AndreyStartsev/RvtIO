using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcExportFlagTests
{
    private const int InstanceFlag = -1019012, TypeFlag = -1019013;

    private static Element Beam(int id, int typeId, double x, int? instanceFlag)
    {
        var beam = IfcExporterBeamCommonPropertySetTests.Product(id, typeId, -2001320, "OST_StructuralFraming", "Beam", x);
        if (instanceFlag is int flag)
            beam.Parameters.Add(new Parameter { Id = InstanceFlag, StorageType = StorageType.Integer, Value = flag });
        return beam;
    }

    private static Element TypeElement(int id, int? typeFlag)
    {
        var type = new Element { Id = id, Name = "Beam type", CategoryId = -2001320, Category = "OST_StructuralFraming" };
        if (typeFlag is int flag)
            type.Parameters.Add(new Parameter { Id = TypeFlag, StorageType = StorageType.Integer, Value = flag });
        return type;
    }

    private static (int Beams, string Ifc) Export(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-flag-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            string ifc = File.ReadAllText(path);
            return (Regex.Matches(ifc, @"=IFCBEAM\(").Count, ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // instance flag, type flag, exported
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0, 0, true)]
    [InlineData(2, null, false)]
    [InlineData(2, 1, false)]
    [InlineData(0, 2, false)]
    [InlineData(null, 2, false)]
    [InlineData(1, 2, true)]
    [InlineData(1, null, true)]
    public void InstanceFlagIsReadFirstAndTwoMeansNo(int? instanceFlag, int? typeFlag, bool exported)
    {
        var (beams, _) = Export(Beam(5100, 5102, 0, instanceFlag), TypeElement(5102, typeFlag));
        Assert.Equal(exported ? 1 : 0, beams);
    }

    [Fact]
    public void OnlyTheFlaggedInstancesAreDropped()
    {
        var (beams, ifc) = Export(Beam(5100, 5102, 0, 2), Beam(5101, 5102, 10, null), TypeElement(5102, null),
            Beam(5103, 5104, 20, 2), TypeElement(5104, 2));
        Assert.Equal(1, beams);
        Assert.Contains(":Beam:5101'", ifc);
        Assert.DoesNotContain(":Beam:5100'", ifc);
        Assert.DoesNotContain(":Beam:5103'", ifc);
    }
}
