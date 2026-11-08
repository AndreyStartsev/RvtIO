using System.Globalization;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Beams of the current profile that share one mapped body: only the first one has extrusion data (the full quantity set and
/// no Slope); every later one has NetVolume and NetSurfaceArea and the Slope of its drive curve.</summary>
public class IfcExporterBeamSharedBodyTests
{
    private static GeoFace Face(params double[][] ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return new GeoFace { OuterVertices = ring.ToList(), Origin = ring[0], Normal = [nx / length, ny / length, nz / length] };
    }

    private static GeometryData Box(double a, double b, double c) => new()
    {
        Faces =
        [
            Face([0, 0, 0], [0, b, 0], [a, b, 0], [a, 0, 0]),
            Face([0, 0, c], [a, 0, c], [a, b, c], [0, b, c]),
            Face([0, 0, 0], [a, 0, 0], [a, 0, c], [0, 0, c]),
            Face([0, b, 0], [0, b, c], [a, b, c], [a, b, 0]),
            Face([0, 0, 0], [0, 0, c], [0, b, c], [0, b, 0]),
            Face([a, 0, 0], [a, b, 0], [a, b, c], [a, 0, c]),
        ],
        BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
    };

    private const string FirstGuid = "HZGD7y6S5209$mGLi_sPlj";
    private const string SecondGuid = "IZGD7y6S5209$mGLi_sPlj";

    /// <summary>A beam whose Body is a mapped item of the given representation map (the box of the element stands for the geometry).</summary>
    private static int AddMappedBeam(StepWriter w, int owner, int placement, string guid, int map)
    {
        int mapped = w.Add("IFCMAPPEDITEM", $"#{map},#{w.Add("IFCCARTESIANTRANSFORMATIONOPERATOR3D", "$,$,$,1.,$")}");
        int context = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", "$,'Model',3,1.E-05,$,$");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#{context},'Body','MappedRepresentation',(#{mapped})");
        int shape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{representation})");
        return w.Add("IFCBEAM", $"'{guid}',#{owner},'P',$,$,#{placement},#{shape},'1'");
    }

    private static int AddMap(StepWriter w)
    {
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int axis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int context = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", "$,'Model',3,1.E-05,$,$");
        int brep = w.Add("IFCFACETEDBREP", "$");
        int representation = w.Add("IFCSHAPEREPRESENTATION", $"#{context},'Body','Brep',(#{brep})");
        return w.Add("IFCREPRESENTATIONMAP", $"#{axis},#{representation}");
    }

    private static void AddBeamCommonPropertySet(StepWriter w, int owner, int beam, string guid)
    {
        int reference = w.Add("IFCPROPERTYSINGLEVALUE", "'Reference',$,IFCIDENTIFIER('B'),$");
        int pset = w.Add("IFCPROPERTYSET", $"'{guid}',#{owner},'Pset_BeamCommon',$,(#{reference})");
        w.Add("IFCRELDEFINESBYPROPERTIES", $"'{guid}',#{owner},$,$,(#{beam}),#{pset}");
    }

    private static (StepWriter W, int Owner, int Placement) NewWriter()
    {
        var w = new StepWriter();
        int owner = w.Add("IFCOWNERHISTORY", "$,$,$,.NOCHANGE.,$,$,$,0");
        int origin = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int axis = w.Add("IFCAXIS2PLACEMENT3D", $"#{origin},$,$");
        int placement = w.Add("IFCLOCALPLACEMENT", $"$,#{axis}");
        return (w, owner, placement);
    }

    private static List<string> QuantityNames(StepWriter w) =>
        Enumerable.Range(0, w.EntityCount).Select(w.EntityAt)
            .Where(e => e.Type is "IFCQUANTITYLENGTH" or "IFCQUANTITYAREA" or "IFCQUANTITYVOLUME")
            .Select(e => IfcExporter.SplitStepArguments(e.Args)[0].Trim('\'')).ToList();

    private static List<double> SlopesWritten(StepWriter w) =>
        Enumerable.Range(0, w.EntityCount).Select(w.EntityAt)
            .Where(e => e.Type == "IFCPROPERTYSINGLEVALUE" && e.Args.StartsWith("'Slope'", StringComparison.Ordinal))
            .Select(e => double.Parse(e.Args.Split("IFCPLANEANGLEMEASURE(")[1].Split(')')[0], CultureInfo.InvariantCulture)).ToList();

    private static void Apply(StepWriter w, int owner, int first, int second, Func<int, Element> beam, bool withPool = false) =>
        new IfcExporter().ApplyCurrentBaseQuantities(
            w, new Dictionary<int, Element> { [16] = beam(16), [17] = beam(17) }, new Dictionary<int, int> { [first] = 16, [second] = 17 },
            new Dictionary<int, (int, int, string)>(), owner, propertyValuePool: withPool ? new() : null);

    private static Element BoxBeam(int id) => new() { Id = id, CategoryId = -2001320, Geometry = Box(1, 2, 5) };

    [Fact]
    public void LaterBeamOfASharedBody_HasOnlyTheNetQuantities_AndTheFirstOneTheFullSet()
    {
        var (w, owner, placement) = NewWriter();
        int map = AddMap(w);
        int first = AddMappedBeam(w, owner, placement, FirstGuid, map);
        int second = AddMappedBeam(w, owner, placement, SecondGuid, map);

        Apply(w, owner, first, second, BoxBeam);

        var names = QuantityNames(w);
        Assert.Equal(1, names.Count(name => name == "Length"));
        Assert.Equal(1, names.Count(name => name == "GrossVolume"));
        Assert.Equal(1, names.Count(name => name == "GrossSurfaceArea"));
        Assert.Equal(1, names.Count(name => name == "OuterSurfaceArea"));
        Assert.Equal(2, names.Count(name => name == "NetVolume"));
        Assert.Equal(2, names.Count(name => name == "NetSurfaceArea"));
    }

    [Fact]
    public void BeamsOfDifferentBodies_EachHaveTheFullSet()
    {
        var (w, owner, placement) = NewWriter();
        int first = AddMappedBeam(w, owner, placement, FirstGuid, AddMap(w));
        int second = AddMappedBeam(w, owner, placement, SecondGuid, AddMap(w));

        Apply(w, owner, first, second, BoxBeam);

        Assert.Equal(2, QuantityNames(w).Count(name => name == "GrossVolume"));
    }

    [Fact]
    public void Slope_IsWrittenForTheLaterBeamOnly_AsTheAngleOfItsDriveCurve()
    {
        var (w, owner, placement) = NewWriter();
        int map = AddMap(w);
        int first = AddMappedBeam(w, owner, placement, FirstGuid, map);
        int second = AddMappedBeam(w, owner, placement, SecondGuid, map);
        AddBeamCommonPropertySet(w, owner, first, FirstGuid);
        AddBeamCommonPropertySet(w, owner, second, SecondGuid);

        Apply(w, owner, first, second, id => new Element
        {
            Id = id, CategoryId = -2001320, Geometry = Box(1, 2, 5), CurveDriverLineLength = 10, CurveDriverLineZ = (3, -2),
        }, withPool: true);

        Assert.Equal(30.0, Assert.Single(SlopesWritten(w)), 9);
    }

    [Theory]
    [InlineData(10.0, 0.0, 5.0, 30.0)]
    [InlineData(10.0, 5.0, 0.0, 30.0)]
    [InlineData(10.0, 2.0, 2.0, 0.0)]
    [InlineData(10.0, 0.0, 10.0, 90.0)]
    public void SlopeDegrees_IsTheUnsignedAngleOfTheDriveCurveToTheHorizontal(double length, double startZ, double endZ, double degrees) =>
        Assert.Equal(degrees, IfcExporter.ResolveBeamSlopeDegreesCurrent(
            new Element { Id = 1, CurveDriverLineLength = length, CurveDriverLineZ = (startZ, endZ) })!.Value, 9);

    [Fact]
    public void SlopeDegrees_WithoutADriveCurve_IsUnknown() =>
        Assert.Null(IfcExporter.ResolveBeamSlopeDegreesCurrent(new Element { Id = 1 }));
}
