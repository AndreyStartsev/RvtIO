using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class CompoundLayerFunctionTests
{
    // 0x1003, count, then 29-byte records [thickness f64][function i32][-1][material i32][-1][index i32][wrapping u8]
    private static byte[] Block(params (double Thickness, int Function)[] layers)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)0x1003));
        bytes.AddRange(BitConverter.GetBytes(layers.Length));
        for (int i = 0; i < layers.Length; i++)
        {
            bytes.AddRange(BitConverter.GetBytes(layers[i].Thickness));
            bytes.AddRange(BitConverter.GetBytes(layers[i].Function));
            bytes.AddRange(BitConverter.GetBytes(-1));
            bytes.AddRange(BitConverter.GetBytes(100 + i));
            bytes.AddRange(BitConverter.GetBytes(-1));
            bytes.AddRange(BitConverter.GetBytes(i));
            bytes.Add(0);
        }
        bytes.AddRange(new byte[64]);
        return bytes.ToArray();
    }

    private static List<CompoundLayer>? Scan(byte[] data)
    {
        var layers = new Dictionary<uint, List<CompoundLayer>>();
        BlockParser.ExtractCompoundLayers(data, 0, data.Length, 7, layers);
        return layers.GetValueOrDefault(7u);
    }

    [Fact]
    public void EveryStoredFunctionKeepsTheArrayAndItsRawValue()
    {
        int[] stored = [0, 1, 2, 3, 4, 5, 100, 200];
        var read = Scan(Block(stored.Select(f => (0.25, f)).ToArray()));
        Assert.NotNull(read);
        Assert.Equal(stored, read.Select(l => l.Function));
    }

    [Fact]
    public void MembraneAndDeckLayersDoNotDropTheOtherLayers()
    {
        var read = Scan(Block((0.3, 4), (0.25, 3), (0.0, 100), (0.06, 2), (0.5, 1), (0.0, 200), (0.04, 5)));
        Assert.NotNull(read);
        Assert.Equal(7, read.Count);
        Assert.Equal([0.3, 0.25, 0.0, 0.06, 0.5, 0.0, 0.04], read.Select(l => l.ThicknessFt));
    }

    [Theory]
    [InlineData(6)] [InlineData(7)] [InlineData(10)] [InlineData(99)] [InlineData(101)] [InlineData(199)] [InlineData(201)] [InlineData(-1)]
    public void AnUnstoredFunctionIsNoLayerArray(int function)
        => Assert.Null(Scan(Block((0.25, 1), (0.25, function))));

    [Theory]
    [InlineData(0, "None")] [InlineData(1, "Structure")] [InlineData(2, "Substrate")] [InlineData(3, "Insulation")]
    [InlineData(4, "Finish1")] [InlineData(5, "Finish2")] [InlineData(100, "Membrane")] [InlineData(200, "StructuralDeck")]
    [InlineData(7, "Function7")] [InlineData(99, "Function99")]
    public void FunctionNames(int function, string name) => Assert.Equal(name, CompoundLayer.FunctionName(function));

    [Fact]
    public void ReferencedLayeredTypeDoesNotLayerAnElementWithAnotherOwnType()
    {
        // A curtain wall refers to the basic wall type of one of its panels; its own type has no compound layers.
        var state = new ParsingState();
        state.CompoundLayers[200] = [new CompoundLayer { ThicknessFt = 0.5, Function = 100, MaterialId = 5, LayerIndex = 0 }];
        state.SystemOccurrenceTypeIds[10] = (1, 3524, 300);
        state.SystemOccurrenceTypeIds[11] = (1, 3524, 200);
        var info = new Model.RevitFileInfo();
        info.Elements.Add(new Model.Element { Id = 200, CategoryId = -2000011 });
        info.Elements.Add(new Model.Element { Id = 300, CategoryId = -2000011 });
        info.Elements.Add(new Model.Element { Id = 10, CategoryId = -2000011, Category = "OST_Walls", References = [300, 200] });
        info.Elements.Add(new Model.Element { Id = 11, CategoryId = -2000011, Category = "OST_Walls", References = [300, 200] });
        info.Elements.Add(new Model.Element { Id = 12, CategoryId = -2000011, Category = "OST_Walls", References = [300, 200] });
        ElementBuilder.PostProcess(info, state);
        Assert.Null(info.Elements.Single(e => e.Id == 10).Layers);
        Assert.Equal("Membrane", Assert.Single(info.Elements.Single(e => e.Id == 11).Layers!).Function);
        // no own type record: the first referenced layered type, as before
        Assert.Equal("Membrane", Assert.Single(info.Elements.Single(e => e.Id == 12).Layers!).Function);
    }

    [SkippableFact]
    public void SampleTypeWithMembraneLayersGetsItsLayers()
    {
        string path = TestFiles.Find("rac_basic_sample_project.rvt");
        var info = new RvtFileReader(new RvtReaderOptions { R22Layout = true, SystemFamilyNameLocale = "ENU" }).Read(path);
        // 'Exterior - Brick on Mtl. Stud': stored functions 4,3,100,2,1,100,5
        var layers = Assert.IsAssignableFrom<IReadOnlyList<Model.MaterialLayer>>(info.Elements.First(e => e.Id == 397).Layers);
        Assert.Equal(["Finish1", "Insulation", "Membrane", "Substrate", "Structure", "Membrane", "Finish2"], layers.Select(l => l.Function));
        Assert.Equal([90, 76, 0, 19, 152, 0, 13], layers.Select(l => Math.Round(l.ThicknessMm)));
    }
}
