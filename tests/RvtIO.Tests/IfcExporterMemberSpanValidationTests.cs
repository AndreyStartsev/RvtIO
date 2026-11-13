using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterMemberSpanValidationTests
{
    private const int MullionCategoryId = -2000171;
    private const double WriterLatticeMetres = 1.0e-5;
    private const double FeetToMetres = 0.3048;

    [Fact]
    public void ExactZeroSpanMarkerWithoutLength_AuthorizesClosedBrepScaledLength()
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 10);
        mullion.Parameters.RemoveAll(parameter => parameter.Id == -1004005);
        mullion.Parameters.Add(SpanParameter(0));

        Assert.True(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            mullion, out double validatedSpanMillimetres));
        Assert.Equal(9.2 * FeetToMetres * 1000,
            validatedSpanMillimetres, precision: 9);

        string path = TempIfcPath("member-brep-span");
        var diagnostic = new StringWriter();
        try
        {
            var info = new RevitFileInfo();
            info.Elements.Add(mullion);
            new IfcExporter(diagnostic) { Profile = IfcProfile.Legacy }.Export(info, path);

            Assert.Equal(validatedSpanMillimetres,
                ExportedSpanMillimetres(File.ReadAllText(path)), precision: 9);
            Assert.Contains(
                "IFC Pset_MemberCommon.Span provenance: " +
                "runtime(INSTANCE_LENGTH_PARAM via BRep)=0 " +
                "calculator(explicit zero Span via BRep)=1 " +
                "authored(parameter)=0 total=1",
                diagnostic.ToString());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ExactSyntheticRuntimeLengthMarker_UsesBrepAndNeverBoundingBoxValue()
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 999);
        mullion.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        mullion.BoundingBox = null;

        string path = TempIfcPath("member-open-span-fallback");
        var diagnostic = new StringWriter();
        try
        {
            var info = new RevitFileInfo();
            info.Elements.Add(mullion);
            new IfcExporter(diagnostic) { Profile = IfcProfile.Legacy }.Export(info, path);

            string ifc = File.ReadAllText(path);
            Assert.Equal(9.2 * FeetToMetres * 1000,
                ExportedSpanMillimetres(ifc), precision: 9);
            Assert.Contains(
                "IFC Pset_MemberCommon.Span provenance: " +
                "runtime(INSTANCE_LENGTH_PARAM via BRep)=1 " +
                "calculator(explicit zero Span via BRep)=0 " +
                "authored(parameter)=0 total=1",
                diagnostic.ToString());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void ExactLengthDirectBranch_WinsBeforeBrepAndNeedsNoCanonicalMarker()
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 10);

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, elementType: null, out double spanMillimetres,
            out IfcExporter.MemberSpanSource source));
        Assert.Equal(10 * FeetToMetres * 1000, spanMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.DirectLength, source);
    }

    [Fact]
    public void PositiveCanonicalSpan_WinsBeforeDifferentExactLengthAndBrep()
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 10);
        mullion.Parameters.Add(SpanParameter(4));

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, elementType: null, out double spanMillimetres,
            out IfcExporter.MemberSpanSource source));
        Assert.Equal(4 * FeetToMetres * 1000, spanMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.DirectCanonical, source);
    }

    [Fact]
    public void NumberTypedCanonicalSpan_IsNotLengthScaledOnDirectBranch()
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 10);
        mullion.Parameters.Add(SpanParameter(
            1234.5, "autodesk.spec.aec:number-2.0.0"));

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, elementType: null, out double spanMillimetres,
            out IfcExporter.MemberSpanSource source));
        Assert.Equal(1234.5, spanMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.DirectCanonical, source);
    }

    [Theory]
    [InlineData("Span", "Pset_MemberCommon.Span")]
    [InlineData("Span[Type]", "Pset_MemberCommon.Span[Type]")]
    public void TypeSetQualifiedSpan_WinsOverThePropertyName(string plain, string qualified)
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 10);
        mullion.Parameters.RemoveAll(parameter => parameter.Id == -1004005);
        var type = new Element();
        Parameter Span(int id, string name, double feet) => new()
        {
            Id = id, Definition = new Definition { Id = id, Name = name },
            StorageType = StorageType.Double, Value = feet, SpecType = "autodesk.spec.aec:length-2.0.0",
        };
        type.Parameters.Add(Span(9101, plain, 6));
        type.Parameters.Add(Span(9102, qualified, 4));

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, type, out double spanMillimetres, out _));
        Assert.Equal(4 * FeetToMetres * 1000, spanMillimetres, precision: 9);
    }

    [Fact]
    public void TypeCanonicalSpan_UsesCalculatorScalingWhenDirectSourcesAreAbsent()
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: 10);
        mullion.Parameters.RemoveAll(parameter => parameter.Id == -1004005);
        var type = new Element();
        type.Parameters.Add(SpanParameter(
            4, "autodesk.spec.aec:number-2.0.0"));

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, type, out double spanMillimetres,
            out IfcExporter.MemberSpanSource source));
        Assert.Equal(4 * FeetToMetres * 1000, spanMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.CalculatorCanonical, source);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(999.0)]
    public void RuntimeLengthMarkerValue_IsValidationOnly(double markerValue)
    {
        var mullion = ExactMullion(bodySpanFeet: 9.2, curveLengthFeet: markerValue);
        mullion.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        mullion.BoundingBox = [-500, -400, -300, 500, 400, 300];

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, elementType: null, out double spanMillimetres,
            out IfcExporter.MemberSpanSource source));
        Assert.Equal(9.2 * FeetToMetres * 1000, spanMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.RuntimeInstanceLength, source);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("wrong-storage")]
    [InlineData("wrong-definition-id")]
    [InlineData("wrong-name")]
    [InlineData("wrong-built-in")]
    [InlineData("duplicate")]
    public void MalformedSyntheticRuntimeLengthMarker_FailsClosed(string mutation)
    {
        var mullion = ExactMullion();
        Parameter marker = mullion.Parameters.Single(parameter => parameter.Id == -1004005);
        marker.IsApproximate = true;
        switch (mutation)
        {
            case "zero":
                marker.Value = 0.0;
                break;
            case "nan":
                marker.Value = double.NaN;
                break;
            case "infinity":
                marker.Value = double.PositiveInfinity;
                break;
            case "wrong-storage":
                marker.StorageType = StorageType.Integer;
                marker.Value = 10;
                break;
            case "wrong-definition-id":
                marker.Definition.Id++;
                break;
            case "wrong-name":
                marker.Definition.Name = "Curve Elem Length";
                break;
            case "wrong-built-in":
                marker.Definition.BuiltInParameter = "INSTANCE_LENGTH_PARAM";
                break;
            case "duplicate":
                mullion.Parameters.Add(new Parameter
                {
                    Id = -1004005,
                    Definition = new Definition
                    {
                        Id = -1004005,
                        Name = "CURVE_ELEM_LENGTH",
                        BuiltInParameter = "CURVE_ELEM_LENGTH",
                    },
                    StorageType = StorageType.Double,
                    Value = 10.0,
                    IsApproximate = true,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.False(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, elementType: null, out _, out _));
    }

    [Fact]
    public void RuntimeLengthBridge_NeverOverridesAuthoredInstanceOrTypeSpan()
    {
        var instanceWinner = ExactMullion();
        instanceWinner.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        instanceWinner.Parameters.Add(SpanParameter(4));
        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            instanceWinner, elementType: null, out double instanceMillimetres,
            out IfcExporter.MemberSpanSource instanceSource));
        Assert.Equal(4 * FeetToMetres * 1000, instanceMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.DirectCanonical, instanceSource);

        var typeWinner = ExactMullion();
        typeWinner.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        var type = new Element();
        type.Parameters.Add(SpanParameter(5));
        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            typeWinner, type, out double typeMillimetres,
            out IfcExporter.MemberSpanSource typeSource));
        Assert.Equal(5 * FeetToMetres * 1000, typeMillimetres, precision: 9);
        Assert.Equal(IfcExporter.MemberSpanSource.CalculatorCanonical, typeSource);
    }

    [Fact]
    public void AuthoredNullOrDuplicateSpan_DisablesRuntimeLengthBridge()
    {
        var nullSpan = ExactMullion();
        nullSpan.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        Parameter authoredNull = SpanParameter(4);
        authoredNull.Value = null;
        nullSpan.Parameters.Add(authoredNull);
        Assert.False(IfcExporter.TryResolveExactMemberSpanMillimetres(
            nullSpan, elementType: null, out _, out _));

        var duplicateSpan = ExactMullion();
        duplicateSpan.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        duplicateSpan.Parameters.Add(SpanParameter(4));
        duplicateSpan.Parameters.Add(SpanParameter(4));
        Assert.False(IfcExporter.TryResolveExactMemberSpanMillimetres(
            duplicateSpan, elementType: null, out _, out _));
    }

    [Fact]
    public void RuntimeLengthBridge_RequiresMarkerAndStrictBrepValidation()
    {
        var noMarker = ExactMullion();
        noMarker.Parameters.RemoveAll(parameter => parameter.Id == -1004005);
        Assert.False(IfcExporter.TryResolveExactMemberSpanMillimetres(
            noMarker, elementType: null, out _, out _));

        var openValidation = ExactMullion();
        openValidation.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        openValidation.Geometry!.BrepSolids![0].IsClosed = false;
        Assert.False(IfcExporter.TryResolveExactMemberSpanMillimetres(
            openValidation, elementType: null, out _, out _));
    }

    [Fact]
    public void ExactZeroSpanWithSyntheticMarker_RemainsCalculatorBrepSource()
    {
        var mullion = ExactMullion();
        mullion.Parameters.Single(parameter => parameter.Id == -1004005)
            .IsApproximate = true;
        mullion.Parameters.Add(SpanParameter(0));

        Assert.True(IfcExporter.TryResolveExactMemberSpanMillimetres(
            mullion, elementType: null, out _,
            out IfcExporter.MemberSpanSource source));
        Assert.Equal(IfcExporter.MemberSpanSource.BrepScaledLength, source);
    }

    [Theory]
    [InlineData("wrong-category")]
    [InlineData("missing-transform")]
    [InlineData("invalid-symbol")]
    [InlineData("missing-host")]
    [InlineData("nonfinite-origin")]
    [InlineData("zero-basis-x")]
    [InlineData("zero-basis-z")]
    [InlineData("missing-geometry")]
    [InlineData("zero-solids")]
    [InlineData("two-solids")]
    [InlineData("open-solid")]
    [InlineData("incomplete-face-indices")]
    [InlineData("duplicate-face-indices")]
    [InlineData("out-of-range-face-index")]
    [InlineData("alternative-outer-cycle")]
    [InlineData("nonfinite-vertex")]
    [InlineData("lattice-overflow")]
    [InlineData("collapsed-loop")]
    [InlineData("ambiguous-terminal-face")]
    public void ExactMullionSpanValidation_FailsClosedOnAuthorityMutations(string mutation)
    {
        var mullion = ExactMullion();
        switch (mutation)
        {
            case "wrong-category":
                mullion.CategoryId = -2000170;
                break;
            case "missing-transform":
                mullion.Transform = null;
                break;
            case "invalid-symbol":
                mullion.Transform!.SymbolId = 0;
                break;
            case "missing-host":
                mullion.SerializedHostId = null;
                break;
            case "nonfinite-origin":
                mullion.Transform!.Origin[0] = double.NaN;
                break;
            case "zero-basis-x":
                mullion.Transform!.BasisX = [0, 0, 0];
                break;
            case "zero-basis-z":
                mullion.Transform!.BasisZ = [0, 0, 0];
                break;
            case "missing-geometry":
                mullion.Geometry = null;
                break;
            case "zero-solids":
                mullion.Geometry!.BrepSolids = [];
                break;
            case "two-solids":
                mullion.Geometry!.BrepSolids!.Add(new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                });
                break;
            case "open-solid":
                mullion.Geometry!.BrepSolids![0].IsClosed = false;
                break;
            case "incomplete-face-indices":
                mullion.Geometry!.BrepSolids![0].FaceIndices.RemoveAt(5);
                break;
            case "duplicate-face-indices":
                mullion.Geometry!.BrepSolids![0].FaceIndices[5] = 4;
                break;
            case "out-of-range-face-index":
                mullion.Geometry!.BrepSolids![0].FaceIndices[5] = 99;
                break;
            case "alternative-outer-cycle":
                mullion.Geometry!.Faces[0].AlternativeOuterVertices =
                    mullion.Geometry.Faces[0].OuterVertices!
                        .Select(ClonePoint).ToList();
                break;
            case "nonfinite-vertex":
                mullion.Geometry!.Faces[0].OuterVertices![0][0] = double.NaN;
                break;
            case "lattice-overflow":
                mullion.Geometry!.Faces[0].OuterVertices![0][0] = double.MaxValue;
                break;
            case "collapsed-loop":
                mullion.Geometry!.Faces[0].OuterVertices =
                [
                    [0, 0, 0],
                    [1e-12, 0, 0],
                    [2e-12, 0, 0],
                ];
                break;
            case "ambiguous-terminal-face":
                mullion.Geometry!.Faces.Add(CloneFace(mullion.Geometry.Faces[0]));
                mullion.Geometry.BrepSolids![0].FaceIndices.Add(6);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.False(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            mullion, out double spanMillimetres));
        Assert.Equal(0, spanMillimetres);
    }

    [Fact]
    public void ExactMullionSpanValidation_UsesMidpointToEvenWriterLattice()
    {
        double latticeFeet = WriterLatticeMetres / FeetToMetres;
        var halfBin = ExactMullion();
        ShiftUpperCapAcrossAxis(halfBin.Geometry!, 0.5 * latticeFeet);
        Assert.True(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            halfBin, out _));

        var beyondHalfBin = ExactMullion();
        ShiftUpperCapAcrossAxis(beyondHalfBin.Geometry!, 1.5 * latticeFeet);
        Assert.False(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            beyondHalfBin, out _));
    }

    [Fact]
    public void ExactMullionSpanValidation_RequiresEdgeLengthBinToEqualAxialSpanBin()
    {
        double latticeFeet = WriterLatticeMetres / FeetToMetres;
        var baseline = ExactMullion(bodySpanFeet: 2.49 * latticeFeet);
        Assert.True(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            baseline, out _));

        var mismatch = ExactMullion(bodySpanFeet: 2.49 * latticeFeet);
        ShiftUpperCapAcrossAxis(mismatch.Geometry!, 0.49 * latticeFeet);
        Assert.False(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            mismatch, out _));
    }

    [Fact]
    public void ExactMullionSpanValidation_DeduplicatesReversedEdgeUsesAndRequiresTwoEdges()
    {
        var ordinaryClosedPrism = ExactMullion();
        Assert.True(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            ordinaryClosedPrism, out _));

        var repeatedSingleEdge = ExactMullion();
        double[] a = [0, 0, 0];
        double[] b = [1, 0, 0];
        double[] c = [0, 0, 1];
        double[] d = [0, 1, 0];
        double[] e = [1, 1, 0];
        double[] f = [0, 1, 1];
        repeatedSingleEdge.Geometry = new GeometryData
        {
            Faces =
            [
                Face(a, b, c),
                Face(d, f, e),
                Face(a, d, b),
                Face(a, d, b),
                Face(a, d, b),
                Face(a, d, b),
            ],
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                },
            ],
        };

        Assert.False(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            repeatedSingleEdge, out _));
    }

    [Fact]
    public void ExactMullionSpanValidation_RejectsNonparallelTerminalEdges()
    {
        var mullion = ExactMullion();
        ShiftUpperCapAcrossAxis(mullion.Geometry!,
            2 * WriterLatticeMetres / FeetToMetres);

        Assert.False(IfcExporter.TryGetExactMullionBrepSpanMillimetres(
            mullion, out _));
    }

    private static Element ExactMullion(
        double bodySpanFeet = 9.2, double curveLengthFeet = 10)
    {
        double[] axis = [0, 1, 0];
        double[] profileX = [1, 0, 0];
        // One profile side lies exactly on x=0. This makes the half-lattice test exercise
        // the midpoint itself, without cancellation from subtracting two large coordinates.
        double[] centre = [0.05, bodySpanFeet / 2, 4];
        var geometry = RectangularPrism(
            centre, axis, profileX, bodySpanFeet, width: 0.1, depth: 0.1);
        return new Element
        {
            Id = 300,
            TypeId = 301,
            IsDiscovered = true,
            CategoryId = MullionCategoryId,
            Category = "OST_CurtainWallMullions",
            Name = "Mullion_50x100",
            References = [3],
            SerializedHostId = 900,
            Transform = new InstanceTransform
            {
                SymbolId = 301,
                Origin = [0, 0, 0],
                BasisX = [1, 0, 0],
                BasisY = [0, 0, 1],
                BasisZ = axis,
            },
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
                new Parameter
                {
                    Id = -1004005,
                    Definition = new Definition
                    {
                        Id = -1004005,
                        Name = "CURVE_ELEM_LENGTH",
                        BuiltInParameter = "CURVE_ELEM_LENGTH",
                    },
                    StorageType = StorageType.Double,
                    Value = curveLengthFeet,
                },
            ],
            BoundingBox = geometry.BoundingBox,
            Geometry = geometry,
        };
    }

    private static GeometryData RectangularPrism(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        double[][] points = PrismPoints(
            centre, axis, profileX, profileY, length, width, depth);
        int[][] faceIndices =
        [
            [0, 1, 3, 2],
            [4, 6, 7, 5],
            [0, 4, 5, 1],
            [2, 3, 7, 6],
            [0, 2, 6, 4],
            [1, 5, 7, 3],
        ];
        var geometry = new GeometryData
        {
            Faces = faceIndices.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => ClonePoint(points[index])).ToList(),
            }).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                },
            ],
        };
        geometry.BoundingBox = Bounds(
            geometry.Faces.SelectMany(face => face.OuterVertices!));
        return geometry;
    }

    private static double[][] PrismPoints(
        double[] centre, double[] axis, double[] profileX, double[] profileY,
        double length, double width, double depth)
    {
        var points = new double[8][];
        int index = 0;
        foreach (int alongSign in new[] { -1, 1 })
            foreach (int widthSign in new[] { -1, 1 })
                foreach (int depthSign in new[] { -1, 1 })
                    points[index++] =
                    [
                        centre[0] + axis[0] * length / 2 * alongSign
                            + profileX[0] * width / 2 * widthSign
                            + profileY[0] * depth / 2 * depthSign,
                        centre[1] + axis[1] * length / 2 * alongSign
                            + profileX[1] * width / 2 * widthSign
                            + profileY[1] * depth / 2 * depthSign,
                        centre[2] + axis[2] * length / 2 * alongSign
                            + profileX[2] * width / 2 * widthSign
                            + profileY[2] * depth / 2 * depthSign,
                    ];
        return points;
    }

    private static void ShiftUpperCapAcrossAxis(GeometryData geometry, double shiftFeet)
    {
        double maximumY = geometry.Faces.SelectMany(face => face.OuterVertices!)
            .Max(point => point[1]);
        foreach (double[] point in geometry.Faces.SelectMany(face => face.OuterVertices!))
            if (point[1] == maximumY)
                point[0] += shiftFeet;
    }

    private static GeoFace Face(params double[][] points) => new()
    {
        OuterVertices = points.Select(ClonePoint).ToList(),
    };

    private static GeoFace CloneFace(GeoFace source) => new()
    {
        OuterVertices = source.OuterVertices?.Select(ClonePoint).ToList(),
        InnerVertices = source.InnerVertices?.Select(ring =>
            ring.Select(ClonePoint).ToList()).ToList(),
        AlternativeOuterVertices = source.AlternativeOuterVertices?
            .Select(ClonePoint).ToList(),
    };

    private static double[] ClonePoint(double[] point) => [point[0], point[1], point[2]];

    private static double[] Bounds(IEnumerable<double[]> points)
    {
        var values = points.ToList();
        return
        [
            values.Min(point => point[0]),
            values.Min(point => point[1]),
            values.Min(point => point[2]),
            values.Max(point => point[0]),
            values.Max(point => point[1]),
            values.Max(point => point[2]),
        ];
    }

    private static double[] Cross(double[] first, double[] second) =>
    [
        first[1] * second[2] - first[2] * second[1],
        first[2] * second[0] - first[0] * second[2],
        first[0] * second[1] - first[1] * second[0],
    ];

    private static double ExportedSpanMillimetres(string ifc)
    {
        var match = Assert.Single(Regex.Matches(ifc,
            @"IFCPROPERTYSINGLEVALUE\('Span',(?:\$|''),IFCPOSITIVELENGTHMEASURE\(([^)]+)\),\$\)"));
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static Parameter SpanParameter(
        double value,
        string specType = "autodesk.spec.aec:length-2.0.0") => new()
    {
        Id = 9001,
        Definition = new Definition { Id = 9001, Name = "Span" },
        StorageType = StorageType.Double,
        Value = value,
        SpecType = specType,
    };

    private static string TempIfcPath(string tag) => Path.Combine(
        Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
