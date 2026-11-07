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

    private static Parameter Flag(int id, int value) => new() { Id = id, StorageType = StorageType.Integer, Value = value };

    private static string ExportText(IfcProfile profile, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-flag-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = profile }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // The writers outside the product loop (spaces, grids, the assembly of a beam system) leave out the elements the loop leaves out.

    // instance flag, type flag, spaces written
    [Theory]
    [InlineData(null, null, 1)]
    [InlineData(2, null, 0)]
    [InlineData(null, 2, 0)]
    [InlineData(1, 2, 1)]
    public void ARoomWhoseExportFlagIsNo_GetsNoSpace(int? instanceFlag, int? typeFlag, int expected)
    {
        var room = IfcExporterSpacesTests.Room(100, "Room", "1", 900);
        var elements = new List<Element> { IfcExporterSpacesTests.Level(900, "Level", 0), room };
        if (instanceFlag is int own) room.Parameters.Add(Flag(InstanceFlag, own));
        if (typeFlag is int type)
        {
            room.TypeId = 901;
            elements.Add(new Element { Id = 901, Name = "Room type", Parameters = [Flag(TypeFlag, type)] });
        }

        string ifc = ExportText(IfcProfile.Legacy, elements.ToArray());

        Assert.Equal(expected, Regex.Matches(ifc, @"=IFCSPACE\(").Count);
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void AGridWhoseExportFlagIsNo_IsLeftOutOfTheGrids(IfcProfile profile)
    {
        Element[] Model(int? flaggedGridId)
        {
            var elements = new Element[]
            {
                IfcExporterGridTests.Level(1, "Ground", 0.0), IfcExporterGridTests.Level(2, "Upper", 10.0),
                IfcExporterGridTests.Grid(10, "A", 0, 0, 10, 0), IfcExporterGridTests.Grid(11, "B", 0, 5, 10, 5),
                IfcExporterGridTests.Grid(12, "1", 0, 0, 0, 5), IfcExporterGridTests.Grid(13, "2", 5, 0, 5, 5),
            };
            foreach (var grid in elements.Where(element => element.Id == flaggedGridId)) grid.Parameters.Add(Flag(InstanceFlag, 2));
            return elements;
        }
        int Axes(string ifc) => Regex.Matches(ifc, @"IFCGRIDAXIS\(").Count;
        int control = Axes(ExportText(profile, Model(null)));
        Assert.True(control > 0);

        // one of four lines left out: the others are still written, on each storey
        Assert.True(Axes(ExportText(profile, Model(11))) is var some && some > 0 && some < control);

        // every grid left out: no grid at all
        var all = Model(null);
        foreach (var grid in all.Where(element => element.Category == "OST_Grids")) grid.Parameters.Add(Flag(InstanceFlag, 2));
        string none = ExportText(profile, all);
        Assert.DoesNotContain("IFCGRID(", none);
        Assert.Equal(0, Axes(none));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(2, 0)]
    public void ABeamSystemWhoseExportFlagIsNo_GetsNoAssembly_AndItsBeamsStay(int? systemFlag, int assemblies)
    {
        var system = new Element
        {
            Id = 5200, CategoryId = -2001327, Category = "OST_StructuralFramingSystem",
            JoistSystem = new RvtIO.Parsing.JoistSystemData(1, "Joist type", [5100, 5101]),
        };
        if (systemFlag is int own) system.Parameters.Add(Flag(InstanceFlag, own));

        string ifc = ExportText(IfcProfile.Current,
            IfcExporterBeamCommonPropertySetTests.Product(5100, 5102, -2001320, "OST_StructuralFraming", "Joist", 0),
            IfcExporterBeamCommonPropertySetTests.Product(5101, 5102, -2001320, "OST_StructuralFraming", "Joist", 10), system);

        Assert.Equal(assemblies, Regex.Matches(ifc, @"IFCELEMENTASSEMBLY\(").Count);
        Assert.Equal(2, Regex.Matches(ifc, @"=IFCBEAM\(").Count);
    }

    [Theory]
    [InlineData(null, null, 1)]
    [InlineData(2, null, 0)]
    [InlineData(null, 2, 0)]
    [InlineData(1, 2, 1)]
    public void ATopographyWhoseExportFlagIsNo_FormsNoSiteBody(int? instanceFlag, int? typeFlag, int expected)
    {
        var topography = new Element
        {
            Id = 700, CategoryId = -2001340, Category = "OST_Topography", Name = "Site",
            BoundingBox = [0, 0, 0, 10, 10, 1],
            Geometry = new GeometryData
            {
                BoundingBox = [0, 0, 0, 10, 10, 1],
                Faces =
                [
                    new GeoFace { OuterVertices = [[0, 0, 0], [10, 0, 0], [10, 10, 1]] },
                    new GeoFace { OuterVertices = [[0, 0, 0], [10, 10, 1], [0, 10, 1]] },
                    new GeoFace { OuterVertices = [[0, 10, 1], [10, 10, 1], [5, 20, 2]] },
                ],
            },
        };
        var elements = new List<Element> { topography };
        if (instanceFlag is int own) topography.Parameters.Add(Flag(InstanceFlag, own));
        if (typeFlag is int type)
        {
            topography.TypeId = 701;
            elements.Add(new Element { Id = 701, Name = "Site type", Parameters = [Flag(TypeFlag, type)] });
        }

        string ifc = ExportText(IfcProfile.Current, elements.ToArray());

        Assert.Equal(expected, Regex.Matches(ifc, @"=IFCFACEBASEDSURFACEMODEL\(").Count);
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
