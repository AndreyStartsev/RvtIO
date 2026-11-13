using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Pset_SlabCommon: attached to every successfully tagged IFCSLAB product that already emitted Pset_QuantityTakeOff, reusing the entity id behind the QTO Reference property. LoadBearing comes from FLOOR_PARAM_IS_STRUCTURAL (id -1001954), gated on CategoryId == OST_Floors (-2000032). IsExternal comes from the shared IsElementExternal replay. PitchAngle is never emitted: it would need a geometry-derived top-face slope, so it is asserted absent.</summary>
public class IfcExporterSlabCommonPropertySetTests
{
    private const int PhaseId = 3;
    private const int FloorParamIsStructuralId = -1001954;
    private const int FloorCategoryId = -2000032;
    private const int StructuralFoundationCategoryId = -2001300;

    [Fact]
    public void StructuralFloor_GetsPsetSlabCommonWithReferenceAndLoadBearingTrue()
    {
        var slab = Slab(id: 300, typeId: 301, name: "Floor_Concrete_200",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: 1);
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("slabcommon-structural");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_SlabCommon'"));

            int slabId = SoleEntityId(ifc, "IFCSLAB");
            var (psetId, valueIds) = SolePsetOfName(ifc, "Pset_SlabCommon");
            Assert.Equal(slabId, SoleRelDefinesByPropertiesTarget(ifc, psetId));

            var props = ResolveProperties(ifc, valueIds);
            // Property list per Pset: exactly IsExternal, Reference, LoadBearing - never
            // PitchAngle (see class remarks / ResolveExactFloorParamIsStructural).
            Assert.Equal(
                new[] { "IsExternal", "LoadBearing", "Reference" },
                props.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("IFCIDENTIFIER('Floor_Concrete_200')", props["Reference"]);
            Assert.Equal("IFCBOOLEAN(.T.)", props["LoadBearing"]);
            Assert.Equal("IFCBOOLEAN(.F.)", props["IsExternal"]);

            Assert.DoesNotContain("PitchAngle", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NonStructuralFloor_GetsLoadBearingFalse()
    {
        var slab = Slab(id: 310, typeId: 311, name: "Floor_Concrete_100",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: 0);
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("slabcommon-nonstructural");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueIds) = SolePsetOfName(ifc, "Pset_SlabCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal("IFCBOOLEAN(.F.)", props["LoadBearing"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FloorWithNoStructuralParameter_OmitsOnlyLoadBearing_KeepsReference()
    {
        // Absent source must never default to a constant: the property list shrinks, the
        // Pset itself and its Reference survive intact.
        var slab = Slab(id: 320, typeId: 321, name: "Floor_Concrete_300",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: null);
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("slabcommon-no-structural-param");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueIds) = SolePsetOfName(ifc, "Pset_SlabCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(new[] { "IsExternal", "Reference" }, props.Keys);
            Assert.Equal("IFCIDENTIFIER('Floor_Concrete_300')", props["Reference"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FloorWithApproximateStructuralParameter_OmitsLoadBearing()
    {
        // An IsApproximate bit is a sign that the direct lookup existed but cannot be reproduced
        // exactly - distinct from genuine absence, and must also omit rather than guess.
        var slab = Slab(id: 330, typeId: 331, name: "Floor_Concrete_400",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: 1);
        slab.Parameters.Single(p => p.Definition?.Id == FloorParamIsStructuralId)
            .IsApproximate = true;
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("slabcommon-approximate-structural");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueIds) = SolePsetOfName(ifc, "Pset_SlabCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(new[] { "IsExternal", "Reference" }, props.Keys);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void StructuralFoundationSlab_NeverGetsLoadBearing_EvenWithRawParameterPresent()
    {
        var slab = Slab(id: 340, typeId: 341, name: "Foundation_Concrete_500",
            categoryId: StructuralFoundationCategoryId,
            category: "OST_StructuralFoundation", isStructural: 1);
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("slabcommon-foundation");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueIds) = SolePsetOfName(ifc, "Pset_SlabCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(new[] { "IsExternal", "Reference" }, props.Keys);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NamelessSlab_GetsNoSlabCommon_NoPlaceholder()
    {
        // Wired inside the same QTO-gated block as Pset_WallCommon/Pset_ColumnCommon: no QTO
        // Reference means no Pset_SlabCommon at all, not a Pset with Reference individually
        // omitted.
        var slab = Slab(id: 350, typeId: 351, name: null,
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: 1);
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("slabcommon-nameless");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain("Pset_SlabCommon", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TwoSlabsSharingLoadBearingValue_ReuseSamePooledPropertyEntity()
    {
        // LoadBearing is pooled like every other boolean property in this exporter - two
        // slabs with the same Structural value must reference the identical
        // IfcPropertySingleValue entity, not two separate ones.
        var slabA = Slab(id: 360, typeId: 361, name: "Type A",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: 1);
        var slabB = Slab(id: 370, typeId: 371, name: "Type B",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: 1, offsetX: 20);
        var info = new RevitFileInfo();
        info.Elements.AddRange([slabA, slabB]);
        string path = TempIfcPath("slabcommon-pooled");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var psets = AllPsetsOfName(ifc, "Pset_SlabCommon");
            Assert.Equal(2, psets.Count);
            var propsA = ResolveProperties(ifc, psets[0].valueIds);
            var propsB = ResolveProperties(ifc, psets[1].valueIds);

            int loadBearingIdA = psets[0].valueIds[
                Array.IndexOf([.. propsA.Keys], "LoadBearing")];
            int loadBearingIdB = psets[1].valueIds[
                Array.IndexOf([.. propsB.Keys], "LoadBearing")];
            Assert.Equal(loadBearingIdA, loadBearingIdB);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("type_function_exterior", "IFCBOOLEAN(.T.)")]
    [InlineData("type_function_interior", "IFCBOOLEAN(.F.)")]
    [InlineData("occurrence_parameter", "IFCBOOLEAN(.T.)")]
    [InlineData("qualified_occurrence_parameter", "IFCBOOLEAN(.F.)")]
    [InlineData("no_source", "IFCBOOLEAN(.F.)")]
    public void IsExternal_FollowsCategoryUtilPrecedence(string mode, string expected)
    {
        var slab = Slab(id: 380, typeId: 381, name: "Floor_Type",
            categoryId: FloorCategoryId, category: "OST_Floors", isStructural: null);
        slab.Parameters.Add(new Parameter
        {
            Id = -1002050,
            Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId,
            Value = 381,
        });
        var type = new Element { Id = 381, CategoryId = slab.CategoryId, Name = "Floor_Type" };
        if (mode.StartsWith("type_function"))
            type.Parameters.Add(new Parameter
            {
                Id = -1001006,
                StorageType = StorageType.Integer,
                Value = mode == "type_function_exterior" ? 1 : 0,
            });
        if (mode == "qualified_occurrence_parameter")
        {
            // Pset_SlabCommon.IsExternal 0 wins over IsExternal 1.
            slab.Parameters.Add(new Parameter
            {
                Id = 29381, Definition = new Definition { Id = 29381, Name = "IsExternal" },
                StorageType = StorageType.Integer, Value = 1,
            });
            slab.Parameters.Add(new Parameter
            {
                Id = 29382, Definition = new Definition { Id = 29382, Name = "Pset_SlabCommon.IsExternal" },
                StorageType = StorageType.Integer, Value = 0,
            });
            type.Parameters.Add(new Parameter
                { Id = -1001006, StorageType = StorageType.Integer, Value = 0 });
        }
        if (mode == "occurrence_parameter")
        {
            slab.Parameters.Add(new Parameter
            {
                Id = 29380,
                Definition = new Definition { Id = 29380, Name = "IsExternal" },
                StorageType = StorageType.Integer,
                Value = 1,
            });
            type.Parameters.Add(new Parameter
                { Id = -1001006, StorageType = StorageType.Integer, Value = 0 });
        }
        var info = new RevitFileInfo();
        info.Elements.AddRange([slab, type]);
        string path = TempIfcPath("slabcommon-isexternal");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (occurrencePsetId, valueIds) = SolePsetOfName(ifc, "Pset_SlabCommon");
            Assert.Equal(expected, ResolveProperties(ifc, valueIds)["IsExternal"]);
            var allPsets = AllPsetsOfName(ifc, "Pset_SlabCommon");
            Assert.Equal(2, allPsets.Count);
            var typePset = Assert.Single(allPsets, set => set.psetId != occurrencePsetId);
            var typeProps = ResolveProperties(ifc, typePset.valueIds);
            Assert.Equal(new[] { "IsExternal" }, typeProps.Keys);
            Assert.Equal(mode == "type_function_exterior" ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)",
                typeProps["IsExternal"]);
            Assert.False(IsOccurrencePset(ifc, typePset.psetId));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("flat", true)]
    [InlineData("sloped_face", false)]
    [InlineData("downward_side_face", true)]
    [InlineData("noisy_side_face", true)]
    [InlineData("rotated_transform", false)]
    [InlineData("basisless_without_vertices", false)]
    [InlineData("translated_transform", true)]
    [InlineData("open_solid", true)]
    [InlineData("family_instances", true)]
    [InlineData("no_upward_face", false)]
    [InlineData("sloped_largest_upward_face", false)]
    [InlineData("level_largest_upward_face", true)]
    [InlineData("ambiguous_largest_upward_face", false)]
    [InlineData("basisless_level_face", true)]
    [InlineData("basisless_sloped_face", false)]
    [InlineData("no_type", false)]
    [InlineData("slope_type_parameter", false)]
    [InlineData("slope_parameter", false)]
    [InlineData("pitch_parameter", false)]
    [InlineData("qualified_pitch_parameter", false)]
    [InlineData("ramp_parameter", false)]
    public void PitchAngle_IsZeroOnlyForAValidatedHorizontalSlab(string mode, bool expected)
    {
        var (slab, type) = TypedSlab(390, 391, 0);
        GeometryData geometry = slab.Geometry!;
        switch (mode)
        {
            case "sloped_face":
                geometry.Faces[0].YVector = [0, 0.995, 0.0998];
                geometry.Faces[1].YVector = [0, 0.995, 0.0998];
                break;
            case "downward_side_face": geometry.Faces[2].YVector = [0, -0.1, 0.995]; break;
            case "no_upward_face":
                geometry.Faces[0].YVector = [0, -1, 0];
                geometry.Faces[1].YVector = [0, -1, 0];
                break;
            case "sloped_largest_upward_face":
                geometry.Faces = [LevelTriangle(), SlopedSquare()];
                geometry.BrepSolids![0].FaceIndices = [0, 1];
                break;
            case "ambiguous_largest_upward_face":
                geometry.Faces = [LevelSquare(), SlopedSquare()];
                geometry.BrepSolids![0].FaceIndices = [0, 1];
                break;
            case "level_largest_upward_face":
                geometry.Faces = [SlopedTriangle(), LevelSquare()];
                geometry.BrepSolids![0].FaceIndices = [0, 1];
                break;
            case "noisy_side_face": geometry.Faces[2].XVector = [1, 1e-17, 0]; geometry.Faces[2].YVector = [1e-17, 0, 1]; break;
            case "basisless_without_vertices":
                foreach (var face in geometry.Faces.Take(2))
                {
                    face.XVector = [0, 0, 0];
                    face.Normal = [0, 0, 1];
                    face.OuterVertices = null;
                }
                break;
            case "rotated_transform": geometry.Transform = [1, 0, 0, 0, 0, 0.999, 0.04, 0, 0, -0.04, 0.999, 0]; break;
            case "translated_transform": geometry.Transform = [1, 0, 0, 5, 0, 1, 0, 6, 0, 0, 1, 7]; break;
            case "open_solid": geometry.BrepSolids![0].IsClosed = false; break;
            case "family_instances": geometry.Instances = [new GeoInstance { SymbolId = 7 }]; break;
            case "basisless_level_face":
                geometry.Faces[0].XVector = [0, 0, 0];
                geometry.Faces[0].YVector = [0, 0, 0];
                geometry.Faces[0].Normal = [1e-18, 0, 1];
                geometry.Faces[0].OuterVertices = [[0, 0, 4.25], [1, 0, 4.25], [1, 1, 4.25]];
                break;
            case "basisless_sloped_face":
                foreach (var face in geometry.Faces.Take(2))
                {
                    face.XVector = [0, 0, 0];
                    face.YVector = [0, 0, 0];
                    face.Normal = [0, -0.05, 0.9987];
                    face.OuterVertices = [[0, 0, 4.25], [1, 0, 4.25], [1, 1, 4.30]];
                }
                break;
            case "slope_type_parameter": type.Parameters.Add(Named("Slope", 0.0)); break;
            case "slope_parameter": slab.Parameters.Add(Named("Slope", 0.0)); break;
            case "pitch_parameter": slab.Parameters.Add(Named("PitchAngle", 0.0)); break;
            case "qualified_pitch_parameter": slab.Parameters.Add(Named("Pset_SlabCommon.PitchAngle", 0.0)); break;
            case "ramp_parameter":
                slab.Parameters.Add(new Parameter
                    { Id = -1008301, StorageType = StorageType.Double, Value = 0.0 });
                break;
        }

        Assert.Equal(expected, IfcExporter.IsValidatedHorizontalSlab(slab, mode == "no_type" ? null : type));
    }

    [Fact]
    public void HorizontalSlabs_SharePooledPitchAngleZero()
    {
        var (first, type) = TypedSlab(392, 394, 0);
        var (second, _) = TypedSlab(393, 394, 20);
        var info = new RevitFileInfo();
        info.Elements.AddRange([first, second, type]);
        string path = TempIfcPath("slabcommon-pitch");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var allPsets = AllPsetsOfName(ifc, "Pset_SlabCommon");
            Assert.Equal(3, allPsets.Count);
            var psets = allPsets.Where(set => IsOccurrencePset(ifc, set.psetId)).ToList();
            Assert.Equal(2, psets.Count);
            foreach (var (_, valueIds) in psets)
                Assert.Equal("IFCPLANEANGLEMEASURE(0.)", ResolveProperties(ifc, valueIds)["PitchAngle"]);
            var typePset = Assert.Single(allPsets, set => !IsOccurrencePset(ifc, set.psetId));
            Assert.Equal(new[] { "IsExternal" }, ResolveProperties(ifc, typePset.valueIds).Keys);
            Assert.Single(Regex.Matches(ifc,
                @"IFCPROPERTYSINGLEVALUE\('PitchAngle',(?:\$|''),IFCPLANEANGLEMEASURE\(0\.\),\$\)"));
        }
        finally { Cleanup(path); }
    }

    private static GeoFace UpwardFace(double[] y, params double[][] vertices) => new()
    {
        XVector = [1, 0, 0],
        YVector = y,
        OuterVertices = vertices.ToList(),
    };

    private static GeoFace LevelTriangle() => UpwardFace([0, 1, 0], [0, 0, 4], [1, 0, 4], [1, 1, 4]);
    private static GeoFace SlopedTriangle() => UpwardFace([0, 0.995, 0.0998], [0, 0, 4], [1, 0, 4], [1, 1, 4.1]);
    private static GeoFace LevelSquare() => UpwardFace([0, 1, 0], [0, 0, 4], [3, 0, 4], [3, 3, 4], [0, 3, 4]);
    private static GeoFace SlopedSquare() => UpwardFace([0, 0.995, 0.0998], [0, 0, 4], [3, 0, 4], [3, 3, 4.3], [0, 3, 4.3]);

    private static (Element Slab, Element Type) TypedSlab(int id, int typeId, double offsetX)
    {
        var slab = Slab(id, typeId, "Floor_Type", FloorCategoryId, "OST_Floors", null, offsetX);
        slab.Parameters.Add(new Parameter
        {
            Id = -1002050,
            Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId,
            Value = typeId,
        });
        // Top (up) and bottom (down) horizontal; the four sides vertical with one exactly vertical direction.
        double[][] xs = [[1, 0, 0], [1, 0, 0], [1, 0, 0], [1, 0, 0], [0, 1, 0], [0, 1, 0]];
        double[][] ys = [[0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, 1], [0, 0, 1], [0, 0, 1]];
        for (int index = 0; index < slab.Geometry!.Faces.Count; index++)
        {
            slab.Geometry.Faces[index].XVector = xs[index];
            slab.Geometry.Faces[index].YVector = ys[index];
        }
        var type = new Element { Id = typeId, CategoryId = FloorCategoryId, Name = "Floor_Type" };
        return (slab, type);
    }

    private static Parameter Named(string name, double value) => new()
    {
        Definition = new Definition { Name = name },
        StorageType = StorageType.Double,
        Value = value,
    };

    internal static Element Slab(
        int id, int typeId, string? name, int categoryId, string category,
        int? isStructural, double offsetX = 0) => new()
    {
        Id = id,
        TypeId = typeId,
        IsDiscovered = true,
        CategoryId = categoryId,
        Category = category,
        Name = name,
        References = [PhaseId],
        Parameters =
        [
            PhaseParameter(),
            .. isStructural is int structural
                ?
                [
                    new Parameter
                    {
                        Id = FloorParamIsStructuralId,
                        Definition = new Definition
                        {
                            Id = FloorParamIsStructuralId,
                            Name = "FLOOR_PARAM_IS_STRUCTURAL",
                        },
                        StorageType = StorageType.Integer,
                        Value = structural,
                    },
                ]
                : Array.Empty<Parameter>(),
        ],
        BoundingBox = PrismBounds(
            [offsetX, 5, 4], [0, 1, 0], [1, 0, 0], length: 10, width: 0.4, depth: 0.6),
        Geometry = RectangularPrism(
            [offsetX, 5, 4], [0, 1, 0], [1, 0, 0], length: 9.2, width: 0.4, depth: 0.6),
    };

    private static Parameter PhaseParameter() => new()
    {
        Id = -1012100,
        StorageType = StorageType.Integer,
        Value = PhaseId,
    };

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static int SoleEntityId(string ifc, string ifcType)
    {
        var match = Assert.Single(Regex.Matches(ifc, $@"#(\d+)={ifcType}\("));
        return int.Parse(match.Groups[1].Value);
    }

    private static (int psetId, List<int> valueIds) SolePsetOfName(string ifc, string psetName) =>
        Assert.Single(AllPsetsOfName(ifc, psetName), set => IsOccurrencePset(ifc, set.psetId));

    private static bool IsOccurrencePset(string ifc, int psetId) => Regex.IsMatch(ifc,
        $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#[^)]*\),#{psetId}\);");

    private static List<(int psetId, List<int> valueIds)> AllPsetsOfName(string ifc, string psetName)
    {
        var pattern = new Regex(
            $@"#(\d+)=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\(([^)]*)\)\);");
        var matches = pattern.Matches(ifc);
        Assert.True(matches.Count > 0, $"expected at least one {psetName} IFCPROPERTYSET");
        return matches.Select(m => (
            int.Parse(m.Groups[1].Value),
            Regex.Matches(m.Groups[2].Value, @"#(\d+)")
                .Select(vm => int.Parse(vm.Groups[1].Value)).ToList()
        )).ToList();
    }

    private static int SoleRelDefinesByPropertiesTarget(string ifc, int psetId)
    {
        var pattern = new Regex(
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#(\d+)\),#{psetId}\);");
        var match = Assert.Single(pattern.Matches(ifc));
        return int.Parse(match.Groups[1].Value);
    }

    private static Dictionary<string, string> ResolveProperties(string ifc, List<int> valueIds)
    {
        var result = new Dictionary<string, string>();
        foreach (int id in valueIds)
        {
            var pattern = new Regex($@"#{id}=IFCPROPERTYSINGLEVALUE\('([^']*)',(?:\$|''),([^,]+(?:\([^)]*\))?),\$\);");
            var match = Assert.Single(pattern.Matches(ifc));
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return result;
    }

    private static GeometryData RectangularPrism(
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

    private static double[] PrismBounds(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        return Bounds(PrismPoints(centre, axis, profileX, profileY, length, width, depth));
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
}
