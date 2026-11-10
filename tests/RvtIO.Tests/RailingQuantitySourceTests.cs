using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class RailingQuantitySourceTests
{
    private static byte[] Read(string kind)
    {
        using var stream = TestFiles.OpenBlock($"sample-a-railing-7583306-{kind}.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    private static TfBlockDeserializer.OwnRailingQuantityInput Decode(string kind, ushort cls)
    {
        byte[] data = Read(kind);
        return TfBlockDeserializer.ExtractOwnRailingQuantityInput(data, 0, BitConverter.ToUInt32(data, 8), cls)!;
    }

    [SkippableFact]
    public void OwnOccurrenceAndSymbolSupplyTheNativeStoredLength()
    {
        var root = Decode("root", 485); var symbol = Decode("symbol", 492);
        Assert.Equal(7583307, root.SymbolId); Assert.Equal(7583306, symbol.RailingId);
        Assert.Equal(219.99999999997976, symbol.LengthFeet!.Value * 304.8, 10);
    }

    [SkippableFact]
    public void InlineParamsAndIdRecordsKeepTheFollowingFieldsAligned()
    {
        using var stream = TestFiles.OpenBlock("sample-a-railing-2653815-symbol.bin");
        using var memory = new MemoryStream(); stream.CopyTo(memory); byte[] data = memory.ToArray();
        var input = TfBlockDeserializer.ExtractOwnRailingQuantityInput(data, 0, BitConverter.ToUInt32(data, 8), 492)!;
        Assert.Equal(2653815, input.RailingId);
        Assert.Equal(10831.502025378875, input.LengthFeet!.Value * 304.8, 9);
        // First class493 record's deleted byte, after four doubles and three ElementIds.
        data[389] = 2;
        Assert.Null(TfBlockDeserializer.ExtractOwnRailingQuantityInput(data, 0, BitConverter.ToUInt32(data, 8), 492));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(data, 341);
        Assert.Null(TfBlockDeserializer.ExtractOwnRailingQuantityInput(data, 0, BitConverter.ToUInt32(data, 8), 492));
    }

    [SkippableTheory]
    [InlineData("owner")] [InlineData("footer")] [InlineData("size")] [InlineData("class")]
    [InlineData("custom_params")]
    public void DamagedFramesAreRejected(string mutation)
    {
        byte[] data = Read("symbol");
        int index = mutation switch { "owner" => 0, "footer" => data.Length - 1, "size" => 8, "custom_params" => 125, _ => 12 };
        data[index] ^= 1;
        Assert.Null(TfBlockDeserializer.ExtractOwnRailingQuantityInput(data, 0, BitConverter.ToUInt32(data, 8), 492));
    }

    [SkippableTheory]
    [InlineData("valid")] [InlineData("duplicate_element")] [InlineData("duplicate_root")]
    [InlineData("duplicate_symbol")] [InlineData("wrong_symbol_class")] [InlineData("wrong_backlink")]
    [InlineData("negative")] [InlineData("zero")] [InlineData("nan")] [InlineData("infinity")]
    [InlineData("block_only")]
    public void OnlyUniqueOwnPositiveLengthReceivesValidation(string mutation)
    {
        var state = new ParsingState();
        var root = Decode("root", 485); var symbol = Decode("symbol", 492);
        symbol = mutation switch
        {
            "wrong_backlink" => symbol with { RailingId = 1 },
            "negative" => symbol with { LengthFeet = -1 },
            "zero" => symbol with { LengthFeet = 0 },
            "nan" => symbol with { LengthFeet = double.NaN },
            "infinity" => symbol with { LengthFeet = double.PositiveInfinity },
            _ => symbol,
        };
        state.OwnedElementClassRoots[7583306] = new(mutation == "duplicate_root" ? 2 : 1, 485);
        state.OwnedElementClassRoots[7583307] = new(1, mutation == "wrong_symbol_class" ? (ushort)1829 : (ushort)492);
        state.RailingQuantityInputs[7583306] = (1, root);
        state.RailingQuantityInputs[7583307] = (mutation == "duplicate_symbol" ? 2 : 1, symbol);
        var element = new Element { Id = 7583306, CategoryId = -2000126,
            IsBlockOnly = mutation == "block_only", ValidatedRailingLengthFeet = 999 };
        var elements = new List<Element> { element };
        if (mutation == "duplicate_element") elements.Add(element);
        ElementBuilder.ApplyValidatedRailingLengths(elements, state);
        Assert.Equal(mutation == "valid" ? symbol.LengthFeet : null, element.ValidatedRailingLengthFeet);
    }

    // A file with its own class schema: the schema index seeds the class root and the stream walk counts the same block again.
    [Theory]
    [InlineData(1, true)] [InlineData(2, false)]
    public void SchemaSeededRootCountedAgainByTheWalkIsReadByTheWalkCount(int walkedBlocks, bool validated)
    {
        var state = new ParsingState();
        foreach (var (id, cls) in new[] { (100u, (ushort)485), (101u, (ushort)492) })
        {
            state.OwnedElementClassRoots[id] = new(2, null);
            state.WalkedElementClassRoots[id] = new(walkedBlocks, walkedBlocks == 1 ? cls : (ushort?)null);
        }
        state.RailingQuantityInputs[100] = (1, new(485, 100, 101, null, null));
        state.RailingQuantityInputs[101] = (1, new(492, 101, null, 100, 12.5));
        var element = new Element { Id = 100, CategoryId = -2000126 };
        ElementBuilder.ApplyValidatedRailingLengths([element], state);
        Assert.Equal(validated ? 12.5 : null, element.ValidatedRailingLengthFeet);
    }
}
