using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterBeamGeometryTests
{
    [Fact]
    public void CentredNominalEnvelope_RecoversRectangularBeamExtrusion()
    {
        double inverseSqrtTwo = 1 / Math.Sqrt(2);
        double[] axis = [inverseSqrtTwo, inverseSqrtTwo, 0];
        double[] profileX = [0, 0, 1];
        double[] centre = [2, 3, 4];
        GeometryData geometry = RectangularPrism(
            centre, axis, profileX, length: 9.2, width: 0.4, depth: 0.6);
        var element = new Element
        {
            Id = 100,
            CategoryId = -2001320,
            BoundingBox = PrismBounds(
                centre, axis, profileX, length: 10, width: 0.4, depth: 0.6),
            Geometry = geometry,
        };

        Assert.True(IfcExporter.TryGetNominalRectangularBeamExtrusion(
            element, out var extrusion));
        Assert.Equal(10, extrusion.LengthFt, 8);
        Assert.Equal(1, Math.Abs(Dot(axis, extrusion.Axis)), 8);
        double[] recoveredProfile =
            [.. new[] { extrusion.ProfileWidthFt, extrusion.ProfileDepthFt }.Order()];
        Assert.Equal(0.4, recoveredProfile[0], 8);
        Assert.Equal(0.6, recoveredProfile[1], 8);
        double[] recoveredCentre =
        [
            extrusion.StartCentreFt[0] + extrusion.Axis[0] * extrusion.LengthFt / 2,
            extrusion.StartCentreFt[1] + extrusion.Axis[1] * extrusion.LengthFt / 2,
            extrusion.StartCentreFt[2] + extrusion.Axis[2] * extrusion.LengthFt / 2,
        ];
        Assert.All(recoveredCentre.Zip(centre), pair => Assert.Equal(pair.Second, pair.First, 8));
    }

    [Fact]
    public void OneSidedNominalEnvelope_RecoversAxialEndExtension()
    {
        double[] axis = [1, 0, 0];
        double[] profileX = [0, 1, 0];
        double[] solidCentre = [0, 0, 0];
        double[] envelopeCentre = [0.025, 0, 0];
        var element = new Element
        {
            Id = 101,
            CategoryId = -2001320,
            BoundingBox = PrismBounds(
                envelopeCentre, axis, profileX, length: 10, width: 0.4, depth: 0.6),
            Geometry = RectangularPrism(
                solidCentre, axis, profileX, length: 9.95, width: 0.4, depth: 0.6),
        };

        Assert.True(IfcExporter.TryGetNominalRectangularBeamExtrusion(
            element, out var extrusion));
        Assert.Equal(10, extrusion.LengthFt, 8);
        double[] recoveredCentre =
        [
            extrusion.StartCentreFt[0] + extrusion.Axis[0] * extrusion.LengthFt / 2,
            extrusion.StartCentreFt[1] + extrusion.Axis[1] * extrusion.LengthFt / 2,
            extrusion.StartCentreFt[2] + extrusion.Axis[2] * extrusion.LengthFt / 2,
        ];
        Assert.All(recoveredCentre.Zip(envelopeCentre),
            pair => Assert.Equal(pair.Second, pair.First, 8));
    }

    [Fact]
    public void TransverselyOffCentreEnvelope_DoesNotReplaceAuthoredBeamBrep()
    {
        double[] axis = [1, 0, 0];
        double[] profileX = [0, 1, 0];
        double[] centre = [0, 0, 0];
        var element = new Element
        {
            Id = 101,
            CategoryId = -2001320,
            BoundingBox = PrismBounds(
                [0, 0.1, 0], axis, profileX, length: 10, width: 0.4, depth: 0.6),
            Geometry = RectangularPrism(
                centre, axis, profileX, length: 8, width: 0.4, depth: 0.6),
        };

        Assert.False(IfcExporter.TryGetNominalRectangularBeamExtrusion(
            element, out _));
    }

    [Fact]
    public void ValidatedNominalBeam_ExportsSweptSolidAtEnvelopeLength()
    {
        double[] axis = [0, 1, 0];
        double[] profileX = [1, 0, 0];
        double[] centre = [2, 5, 4];
        var beam = new Element
        {
            Id = 102,
            TypeId = 103,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Nominal rectangular beam",
            References = [3],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
            ],
            BoundingBox = PrismBounds(
                centre, axis, profileX, length: 10, width: 0.4, depth: 0.6),
            Geometry = RectangularPrism(
                centre, axis, profileX, length: 9.2, width: 0.4, depth: 0.6),
        };
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = Path.Combine(Path.GetTempPath(),
            $"revitreader-nominal-beam-{Guid.NewGuid():N}.ifc");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Contains("=IFCBEAM(", ifc);
            Assert.Contains("=IFCEXTRUDEDAREASOLID(", ifc);
            Assert.Contains(",3048.);", ifc);
            Assert.DoesNotContain("=IFCFACETEDBREP(", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CoincidentDirectAndSymbolSolids_ExportOneBeamBody()
    {
        double[] axis = [0, 1, 0];
        double[] profileX = [1, 0, 0];
        double[] centre = [2, 5, 4];
        GeometryData geometry = RectangularPrism(
            centre, axis, profileX, length: 10, width: 0.4, depth: 0.6);
        AddCoincidentSolid(geometry);
        var beam = new Element
        {
            Id = 104,
            TypeId = 105,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Coincident direct and symbol beam",
            References = [3],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
            ],
            BoundingBox = PrismBounds(
                centre, axis, profileX, length: 10, width: 0.4, depth: 0.6),
            Geometry = geometry,
        };
        Assert.True(IfcExporter.TryGetNominalRectangularBeamExtrusion(
            beam, out var extrusion));
        Assert.Equal(10, extrusion.LengthFt, 8);
        string path = Path.Combine(Path.GetTempPath(),
            $"revitreader-coincident-beam-{Guid.NewGuid():N}.ifc");

        try
        {
            var info = new RevitFileInfo();
            info.Elements.Add(beam);
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCEXTRUDEDAREASOLID\\("));
            Assert.DoesNotContain("=IFCFACETEDBREP(", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ValidatedEndNotch_ExportsOneClosedEightFaceBeamBrep()
    {
        GeometryData geometry = EndNotchedBeamContracts();
        var beam = new Element
        {
            Id = 106,
            TypeId = 107,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "Rectangular beam with authored end notch",
            References = [3],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
            ],
            BoundingBox = geometry.BoundingBox,
            Geometry = geometry,
        };

        Assert.True(IfcExporter.TryBuildRectangularBeamEndNotchBoundary(
            beam, out GeometryData boundary));
        Assert.Equal(8, boundary.Faces.Count);
        Assert.Equal(2, boundary.Faces.Count(face => face.OuterVertices!.Count == 6));
        Assert.Equal(6, boundary.Faces.Count(face => face.OuterVertices!.Count == 4));
        Assert.True(Assert.Single(boundary.BrepSolids!).IsClosed);
        Assert.Equal([0, 0, 0, 10, 3, 2], boundary.BoundingBox!);

        string path = Path.Combine(Path.GetTempPath(),
            $"revitreader-notched-beam-{Guid.NewGuid():N}.ifc");
        try
        {
            var info = new RevitFileInfo();
            info.Elements.Add(beam);
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCFACETEDBREP\\("));
            Match closedShell = Regex.Match(ifc, "=IFCCLOSEDSHELL\\(([^)]*)\\)");
            Assert.True(closedShell.Success);
            Assert.Equal(8, Regex.Matches(closedShell.Groups[1].Value, "#").Count);
            Assert.DoesNotContain("=IFCSHELLBASEDSURFACEMODEL(", ifc);
            Assert.DoesNotContain("=IFCEXTRUDEDAREASOLID(", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InteriorOpenBox_DoesNotBecomeBeamEndNotch()
    {
        GeometryData geometry = EndNotchedBeamContracts();
        foreach (int faceIndex in geometry.BrepSolids!.Single(solid => !solid.IsClosed).FaceIndices)
            foreach (double[] point in geometry.Faces[faceIndex].OuterVertices!)
                point[1] -= 0.5;
        var beam = new Element
        {
            Id = 108,
            CategoryId = -2001320,
            BoundingBox = geometry.BoundingBox,
            Geometry = geometry,
        };

        Assert.False(IfcExporter.TryBuildRectangularBeamEndNotchBoundary(
            beam, out _));
    }

    internal static GeometryData RectangularPrism(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        double[][] points = PrismPoints(centre, axis, profileX, profileY, length, width, depth);
        int[][] faceIndices =
        [
            [0, 1, 3, 2],
            [4, 6, 7, 5],
            [0, 4, 5, 1],
            [2, 3, 7, 6],
            [0, 2, 6, 4],
            [1, 5, 7, 3],
        ];
        return new GeometryData
        {
            BoundingBox = Bounds(points),
            Faces = faceIndices.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
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
    }

    private static GeometryData EndNotchedBeamContracts()
    {
        GeometryData geometry = RectangularPrism(
            centre: [5.25, 1.5, 1], axis: [1, 0, 0], profileX: [0, 1, 0],
            length: 9.5, width: 3, depth: 2);
        int cutterOffset = geometry.Faces.Count;
        geometry.Faces.AddRange(
        [
            Face([10, 2, 0], [10, 3, 0], [10, 3, 2], [10, 2, 2]),
            Face([9, 2, 0], [9, 2, 2], [9, 3, 2], [9, 3, 0]),
            Face([9, 2, 2], [10, 2, 2], [10, 3, 2], [9, 3, 2]),
            Face([9, 2, 0], [10, 2, 0], [10, 2, 2], [9, 2, 2]),
        ]);
        geometry.BrepSolids!.Add(new GeoBrepSolid
        {
            IsClosed = false,
            FaceIndices = Enumerable.Range(cutterOffset, 4).ToList(),
        });
        geometry.Faces.Add(Face([0, 0, 0], [0, 0, 2], [0, 3, 2], [0, 3, 0]));
        geometry.BoundingBox = [0, 0, 0, 10, 3, 2];
        return geometry;

        static GeoFace Face(params double[][] points) => new()
        {
            OuterVertices = points.ToList(),
        };
    }

    internal static double[] PrismBounds(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        return Bounds(PrismPoints(
            centre, axis, profileX, profileY, length, width, depth));
    }

    private static void AddCoincidentSolid(GeometryData geometry)
    {
        int faceOffset = geometry.Faces.Count;
        foreach (GeoFace face in geometry.Faces.Take(6).ToList())
            geometry.Faces.Add(new GeoFace
            {
                OuterVertices = face.OuterVertices!
                    .Select(point => point.ToArray()).ToList(),
            });
        geometry.BrepSolids!.Add(new GeoBrepSolid
        {
            IsClosed = true,
            FaceIndices = Enumerable.Range(faceOffset, 6).ToList(),
        });
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

    private static double[] Bounds(IEnumerable<double[]> points)
    {
        var list = points.ToList();
        return
        [
            list.Min(point => point[0]),
            list.Min(point => point[1]),
            list.Min(point => point[2]),
            list.Max(point => point[0]),
            list.Max(point => point[1]),
            list.Max(point => point[2]),
        ];
    }

    private static double[] Cross(double[] first, double[] second) =>
    [
        first[1] * second[2] - first[2] * second[1],
        first[2] * second[0] - first[0] * second[2],
        first[0] * second[1] - first[1] * second[0],
    ];

    private static double Dot(double[] first, double[] second) =>
        first[0] * second[0] + first[1] * second[1] + first[2] * second[2];
}
