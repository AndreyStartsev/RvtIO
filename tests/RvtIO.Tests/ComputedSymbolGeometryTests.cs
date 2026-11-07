using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>An instance whose own faces are only a void helper (no fill bit) but whose computed symbol has filled ones is a
/// product with the body of that symbol: louvers with an own box of unfilled faces and a computed symbol of filled lamellas are exported;
/// the void family of a floor opening, whose symbol has no filled face, is not.</summary>
public class ComputedSymbolGeometryTests
{
    private const uint Unfilled = 0x00088004u, Filled = 0x00088204u;
    private static uint handle;

    private static GRepFace Face(double x, uint flags, int node = 1) => new()
    {
        Vertices = [[x, 0, 0], [x + 1, 0, 0], [x, 1, 0]],
        Origin = [x, 0, 0], XVector = [0, 0, 1], GeometryNode = node, SourceFace = ++handle, GInfoFlags = flags,
    };

    private static GRepData Symbol(uint flags, int node = 1) => new()
    {
        BoundingBox = [0, 0, 0, 4, 1, 1],
        Faces = [Face(0, flags, node), Face(1, flags, node), Face(2, flags, node), Face(3, flags, node)],
    };

    private static Element Instance(int id, uint flags)
    {
        var faces = Enumerable.Range(0, 6).Select(index => new GeoFace
        {
            OuterVertices = [[index, 0, 0], [index + 1, 0, 0], [index, 1, 0]], GInfoFlags = flags,
        }).ToList();
        return new Element
        {
            Id = id, CategoryId = -2000151, BoundingBox = [-1, -2, -3, 4, 5, 6],
            Geometry = new GeometryData { Faces = faces, BoundingBox = [0, 0, 0, 1, 1, 1] },
        };
    }

    // The instance sits at (10, 20, 30), turned a quarter around z: local x runs along world y.
    private static readonly double[] Transform = [0, 1, 0, -1, 0, 0, 0, 0, 1, 10, 20, 30];

    private static ParsingState State(int instanceId = 7, int symbolId = 70, uint symbolFlags = Filled,
        int blocks = 1, ushort symbolClass = 1829, double[]? transform = null, int symbolNode = 1)
    {
        var state = new ParsingState();
        state.ComputedFamilySymbols[(uint)instanceId] = (blocks,
            new TfBlockDeserializer.ComputedFamilySymbol(symbolId, 0, 0, 0) { Transform = transform ?? Transform });
        state.ComputedSymbolTargetClasses[(uint)symbolId] = (1, symbolClass);
        state.GrepBlocks[(uint)symbolId] = Symbol(symbolFlags, symbolNode);
        return state;
    }

    [Fact]
    public void VoidHelperWithAFilledComputedSymbol_GetsTheSymbolPlacedByItsTransform()
    {
        var element = Instance(7, Unfilled);
        ElementBuilder.AttachComputedSymbolGeometry(element, 7, State());

        var geometry = element.SerializedComputedSymbolGeometry!;
        Assert.NotNull(geometry);
        Assert.Equal(4, geometry.Faces.Count);
        var mapped = Assert.Single(geometry.ExactMappedInstances!);
        Assert.Equal(70, mapped.SymbolId);
        Assert.Equal(Transform, mapped.Transform);
        // local (1, 0, 0) of the first face is world (10 + 0, 20 - 1, 30): the local x axis runs along world -y
        Assert.Contains(geometry.Faces.SelectMany(face => face.OuterVertices!),
            vertex => vertex[0] == 10 && vertex[1] == 19 && vertex[2] == 30);
        // The placed symbol keeps the tight box of the instance's decoded geometry.
        Assert.Equal([0, 0, 0, 1, 1, 1], geometry.BoundingBox!);
        // the geometry of the instance itself is kept for the other versions
        Assert.Equal(6, element.Geometry!.Faces.Count);
        // faces placed from a symbol count as filled
        element.Geometry = geometry;
        Assert.False(IfcExporter.HasOnlyUnfilledFacesCurrent(element));
    }

    [Fact]
    public void InstanceWithAFilledFace_KeepsItsOwnGeometry()
    {
        var element = Instance(7, Filled);
        ElementBuilder.AttachComputedSymbolGeometry(element, 7, State());
        Assert.Null(element.SerializedComputedSymbolGeometry);

        var mixed = Instance(7, Unfilled);
        mixed.Geometry!.Faces[3].GInfoFlags = Filled;
        ElementBuilder.AttachComputedSymbolGeometry(mixed, 7, State());
        Assert.Null(mixed.SerializedComputedSymbolGeometry);
    }

    [Fact]
    public void VoidFamily_WhoseSymbolHasNoFilledFace_StaysAVoid()
    {
        var element = Instance(7, Unfilled);
        ElementBuilder.AttachComputedSymbolGeometry(element, 7, State(symbolFlags: Unfilled));
        Assert.Null(element.SerializedComputedSymbolGeometry);
        Assert.True(IfcExporter.HasOnlyUnfilledFacesCurrent(element));
    }

    [Fact]
    public void VoidFamily_WhoseFilledSymbolFacesAreInANodeReferenceLeavesOut_StaysAVoid()
    {
        // sample project A 7862859, the computed symbol of the six floor opening tasks: five faces with the fill bit in the node -70
        var element = Instance(7, Unfilled);
        ElementBuilder.AttachComputedSymbolGeometry(element, 7, State(symbolNode: -70));
        Assert.Null(element.SerializedComputedSymbolGeometry);
    }

    [Theory]
    [InlineData("two roots")]
    [InlineData("not a family symbol")]
    [InlineData("the instance itself")]
    [InlineData("no symbol block")]
    [InlineData("no transform")]
    [InlineData("short transform")]
    [InlineData("no computed symbol")]
    public void ComputedSymbol_MustBeTheUniqueFamilySymbolWithAGeometryBlockAndATransform(string broken)
    {
        var element = Instance(7, Unfilled);
        var state = broken switch
        {
            "two roots" => State(blocks: 2),
            "not a family symbol" => State(symbolClass: 1794),
            "the instance itself" => State(symbolId: 7),
            "short transform" => State(transform: [1, 0, 0]),
            _ => State(),
        };
        if (broken == "no symbol block") state.GrepBlocks.Clear();
        if (broken == "no transform")
            state.ComputedFamilySymbols[7] = (1, new TfBlockDeserializer.ComputedFamilySymbol(70, 0, 0, 0));
        if (broken == "no computed symbol") state.ComputedFamilySymbols.Clear();

        ElementBuilder.AttachComputedSymbolGeometry(element, 7, state);

        Assert.Null(element.SerializedComputedSymbolGeometry);
    }


    private static string Export(IfcProfile version, Element element)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(element);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-computed-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Element Product(uint flags, bool withComputed)
    {
        double[][] corners = [[0, 0], [2, 0], [2, 1], [0, 1]];
        GeometryData Cube(uint f)
        {
            var faces = new List<GeoFace>
            {
                new() { Normal = [0, 0, -1], Origin = [0, 0, 0], OuterVertices = corners.AsEnumerable().Reverse().Select(p => new[] { p[0], p[1], 0.0 }).ToList(), GInfoFlags = f },
                new() { Normal = [0, 0, 1], Origin = [0, 0, 1], OuterVertices = corners.Select(p => new[] { p[0], p[1], 1.0 }).ToList(), GInfoFlags = f },
            };
            for (int i = 0; i < 4; i++)
            {
                var p = corners[i]; var q = corners[(i + 1) % 4];
                double sx = q[0] - p[0], sy = q[1] - p[1], length = Math.Sqrt(sx * sx + sy * sy);
                faces.Add(new GeoFace
                {
                    Normal = [sy / length, -sx / length, 0], GInfoFlags = f,
                    OuterVertices = [[p[0], p[1], 0], [q[0], q[1], 0], [q[0], q[1], 1], [p[0], p[1], 1]],
                });
            }
            return new GeometryData
            {
                Faces = faces, BoundingBox = [0, 0, 0, 2, 1, 1],
                BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, 6).ToList(), IsClosed = true }],
            };
        }
        return new Element
        {
            Id = 1, TypeId = 102, Name = "Louver 1", CategoryId = -2000151, Category = "OST_GenericModel", References = [99],
            Geometry = Cube(flags), BoundingBox = [0, 0, 0, 2, 1, 1], SerializedExportId = Guid.NewGuid(),
            SerializedComputedSymbolGeometry = withComputed ? Cube(0xFFFFFFFFu & ~0u) : null,
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = 99 }],
        };
    }

    [Fact]
    public void Current_WritesTheVoidHelperWithItsComputedSymbolBody_AndGivesTheGeometryBack()
    {
        var withSymbol = Product(Unfilled, withComputed: true);
        var own = withSymbol.Geometry;
        Assert.Contains("=IFCBUILDINGELEMENTPROXY(", Export(IfcProfile.Current, withSymbol));
        Assert.Same(own, withSymbol.Geometry);

        Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", Export(IfcProfile.Current, Product(Unfilled, withComputed: false)));
    }

    [Fact]
    public void OlderRelease_NeverTakesTheComputedSymbolGeometry()
    {
        var element = Product(Unfilled, withComputed: true);
        var own = element.Geometry;
        Export(IfcProfile.Legacy, element);
        Assert.Same(own, element.Geometry);
    }

    [Fact]
    public void Swap_PutsTheSymbolGeometryInAndReturnsTheOriginal()
    {
        var swapped = Product(Unfilled, withComputed: true);
        var plain = Product(Unfilled, withComputed: false);
        var own = swapped.Geometry;
        var symbol = swapped.SerializedComputedSymbolGeometry;

        var back = IfcExporter.UseComputedSymbolGeometryCurrent([swapped, plain]);

        Assert.Same(symbol, swapped.Geometry);
        Assert.Single(back);
        Assert.Same(own, back[0].Original);
        Assert.NotNull(plain.Geometry);
    }
}
