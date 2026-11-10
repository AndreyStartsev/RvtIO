using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ComputedFamilySymbolTests
{
    [Theory]
    [InlineData(600)] [InlineData(int.MaxValue)]
    public void OwnQueuedSymbol_IsNotTheDisplaySymbolOrAByteSearch(int symbol)
    {
        byte[] block = Block(symbol: symbol);
        var own = Assert.IsType<TfBlockDeserializer.ComputedFamilySymbol>(Read(block));
        Assert.Equal(symbol, own.SymbolId);
        Assert.Equal(1, own.QueueIndex);
        Assert.Equal(104, own.End - own.Start);
        Assert.Equal(symbol, BitConverter.ToInt32(block, own.Start + 96));
        Assert.Equal(500, TfBlockDeserializer.ExtractFamilyInstanceSymbolId(block, 0, Size(block), 1794));
    }

    [Theory]
    [InlineData("owner")] [InlineData("zero_owner")] [InlineData("size")]
    [InlineData("footer")] [InlineData("class")] [InlineData("short_root")]
    [InlineData("null_pointer")] [InlineData("external_pointer")] [InlineData("wrong_pointer")]
    [InlineData("handle_pointer")] [InlineData("prior_store")] [InlineData("short_info")]
    [InlineData("foreign_document")] [InlineData("null_document")]
    public void InvalidOwnPrefix_IsUnknown(string mutation) => Assert.Null(Read(Block(mutation)));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(100)]
    public void NonpositiveOrSelfSymbol_IsUnknown(int symbol) => Assert.Null(Read(Block(symbol: symbol)));

    [Fact]
    public void BoundsAreCheckedBeforeReading()
    {
        byte[] block = Block();
        Assert.Null(TfBlockDeserializer.ExtractComputedFamilySymbol(block, -1, Size(block), 1794));
        Assert.Null(TfBlockDeserializer.ExtractComputedFamilySymbol(block, int.MaxValue, Size(block), 1794));
        Assert.Null(TfBlockDeserializer.ExtractComputedFamilySymbol(block, 0, uint.MaxValue, 1794));
        Assert.Null(TfBlockDeserializer.ExtractComputedFamilySymbol(block[..^1], 0, Size(block), 1794));
    }

    [Theory]
    [InlineData("unknown_tail")] [InlineData("nonfinite_matrix")]
    public void IdentityValidation_DoesNotClaimLaterGeometryOrPlacement(string mutation)
        => Assert.Equal(600, Read(Block(mutation))?.SymbolId);

    [Fact]
    public void PlacementComesFromTheSameOwnedInfoAsSymbol_NotAnOrthonormalScan()
    {
        byte[] bytes = Block();
        int first = Read(bytes)!.Start;
        double[] expected = Enumerable.Range(0, 12).Select(index => index / 7.0 - 1).ToArray();
        for (int index = 0; index < expected.Length; index++)
            BitConverter.GetBytes(expected[index]).CopyTo(bytes, first + index * 8);
        var own = Read(bytes)!;
        Assert.Equal(600, own.SymbolId);
        Assert.Equal(expected, own.Transform);
        byte[] embedded = new byte[bytes.Length + 19]; bytes.CopyTo(embedded, 7);
        Assert.Equal(expected, TfBlockDeserializer.ExtractComputedFamilySymbol(
            embedded, 7, Size(bytes), 1794)!.Transform);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void NonfinitePlacementDoesNotInvalidateIndependentSymbolIdentity(double value)
    {
        for (int index = 0; index < 12; index++)
        {
            byte[] bytes = Block();
            BitConverter.GetBytes(value).CopyTo(bytes, Read(bytes)!.Start + index * 8);
            var own = Read(bytes)!;
            Assert.Equal(600, own.SymbolId);
            Assert.Null(own.Transform);
        }
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("malformed_first")] [InlineData("foreign_owner")]
    public void RecordingCannotRepairAmbiguousOrForeignRoots(string mutation)
    {
        var state = new ParsingState();
        if (mutation == "malformed_first") Record(state, Block("footer"));
        if (mutation == "duplicate") Record(state, Block(symbol: 601));
        Record(state, Block(), mutation == "foreign_owner" ? 999u : 100u);
        var own = state.ComputedFamilySymbols[mutation == "foreign_owner" ? 999u : 100u];
        Assert.Null(own.Symbol);
        Assert.Equal(mutation == "foreign_owner" ? 1 : 2, own.BlockCount);
    }

    [Fact]
    public void RepeatThatReadsTheSameConfirmsNothingDifferent()
    {
        byte[] block = Block(host: 300);
        var state = new ParsingState();
        Record(state, block);
        // the older copy of the instance in another partition: the same symbol, placement, type and host, but a field this reader
        // does not use differs (the stamp in the block header and the last field)
        byte[] older = new byte[block.Length + 11]; block.CopyTo(older, 5);
        older[5 + 4] ^= 0x55; older[5 + block.Length - 5] ^= 1;
        RvtFileReader.RecordFamilySymbolReferenceSource(state, older, 5, Size(block), 1794, 100);
        Record(state, (byte[])block.Clone());
        Assert.Equal(1, state.ComputedFamilySymbols[100].BlockCount);
        Assert.Equal(600, state.ComputedFamilySymbols[100].Symbol!.SymbolId);
        Assert.Equal(1, state.FamilyInstanceSymbolIds[100].BlockCount);
        Assert.Equal(500, state.FamilyInstanceSymbolIds[100].TypeId);
        Assert.Equal(1, state.ComputedSymbolTargetClasses[100].BlockCount);
        Assert.Equal((ushort?)1794, state.ComputedSymbolTargetClasses[100].ClassId);
        // and the instance still materializes its original symbol
        var instance = new Element { Id = 100, NativeMasterSymbolId = 500 };
        state.ComputedSymbolTargetClasses[600] = (1, 1829);
        ElementBuilder.ApplyValidatedOriginalFamilySymbols([instance, new Element { Id = 600 }], state);
        Assert.Equal(600, instance.ValidatedOriginalFamilySymbolId);
    }

    [Theory]
    [InlineData("symbol")] [InlineData("placement")] [InlineData("host")] [InlineData("orientation")]
    [InlineData("type")] [InlineData("malformed")]
    public void RepeatThatReadsDifferentlyStillMakesTheInstanceAmbiguous(string difference)
    {
        byte[] block = Block(host: 300);
        byte[] other = difference switch
        {
            "symbol" => Block(host: 300, symbol: 601),
            "host" => Block(host: 301),
            "orientation" => Block(host: 300, orientation: 1),
            "type" => Block(host: 300, master: 501),
            "malformed" => Block("footer", host: 300),
            _ => (byte[])block.Clone(),
        };
        if (difference == "placement") BitConverter.GetBytes(5d).CopyTo(other, Read(other)!.Start + 9 * 8);
        var state = new ParsingState();
        Record(state, block);
        Record(state, other);
        Record(state, (byte[])block.Clone()); // equal to the first again, but the instance is ambiguous already
        Assert.Equal(3, state.ComputedFamilySymbols[100].BlockCount);
        Assert.Null(state.ComputedFamilySymbols[100].Symbol);
        Assert.Equal(3, state.FamilyInstanceSymbolIds[100].BlockCount);
        Assert.Equal(3, state.ComputedSymbolTargetClasses[100].BlockCount);
        Assert.Null(state.ComputedSymbolTargetClasses[100].ClassId);
    }

    [Theory]
    [InlineData("same", true)] [InlineData("symbol", false)] [InlineData("host", false)] [InlineData("sub", false)]
    [InlineData("orientation", false)] [InlineData("transform", false)] [InlineData("type", false)]
    [InlineData("class", false)] [InlineData("no_symbol", false)] [InlineData("no_type", false)]
    [InlineData("first_ambiguous", false)] [InlineData("first_unreadable", false)]
    [InlineData("symbols_ambiguous", false)] [InlineData("kind_ambiguous", false)] [InlineData("neither_type_read", false)]
    public void RepeatComparisonCoversEveryFieldTheReaderUses(string change, bool repeats)
    {
        double[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 20, 30];
        TfBlockDeserializer.ComputedFamilySymbol Symbol(double[]? transform = null) => new(600, 1, 400, 504)
            { Transform = transform ?? [.. matrix], SubInstanceCount = 0, HostId = 300, InsertOrientation = 0 };
        var state = new ParsingState();
        state.FamilyInstanceSymbolIds[100] = (change == "first_ambiguous" ? 2 : 1, change == "neither_type_read" ? null : 500);
        state.ComputedFamilySymbols[100] = (change == "symbols_ambiguous" ? 2 : 1, change == "first_unreadable" ? null : Symbol());
        state.ComputedSymbolTargetClasses[100] = (change == "kind_ambiguous" ? 2 : 1, change == "class" ? (ushort)1795 : (ushort)1794);
        var again = change switch
        {
            "symbol" => Symbol() with { SymbolId = 601 },
            "host" => Symbol() with { HostId = 301 },
            "sub" => Symbol() with { SubInstanceCount = 1 },
            "orientation" => Symbol() with { InsertOrientation = 1 },
            "transform" => Symbol([.. matrix[..11], 31]),
            "no_symbol" => null,
            // the bytes of the walk differ (queue index, start, end) but what was read is the same
            _ => Symbol() with { QueueIndex = 7, Start = 11, End = 115 },
        };
        int? type = change == "type" ? 501 : change is "no_type" or "neither_type_read" ? null : 500;
        Assert.Equal(repeats, RvtFileReader.RepeatsInstanceRoot(state, 100, 1794, type, again));
    }

    [Theory]
    [InlineData("valid")] [InlineData("block_only_symbol")]
    [InlineData("missing_occurrence")] [InlineData("missing_symbol")]
    [InlineData("duplicate_occurrence")] [InlineData("duplicate_symbol")]
    [InlineData("missing_source")] [InlineData("duplicate_source")]
    [InlineData("missing_target_root")] [InlineData("duplicate_target_root")]
    [InlineData("wrong_target_class")] [InlineData("self_symbol")]
    public void MaterializationRequiresUniqueOwnInstanceAndFamilySymbol(string mutation)
    {
        var instance = new Element { Id = 100, NativeMasterSymbolId = 500, ValidatedOriginalFamilySymbolId = 999 };
        var symbol = new Element { Id = 600, IsBlockOnly = mutation == "block_only_symbol" };
        var elements = new List<Element> { instance, symbol };
        var state = new ParsingState(); Record(state, Block());
        state.ComputedSymbolTargetClasses[600] = (1, 1829);
        switch (mutation)
        {
            case "missing_occurrence": instance.Id = 101; break;
            case "missing_symbol": elements.Remove(symbol); break;
            case "duplicate_occurrence": elements.Add(new Element { Id = 100 }); break;
            case "duplicate_symbol": elements.Add(new Element { Id = 600 }); break;
            case "missing_source": state.ComputedFamilySymbols.Clear(); break;
            case "duplicate_source": Record(state, Block(symbol: 601)); break;
            case "missing_target_root": state.ComputedSymbolTargetClasses.Clear(); break;
            case "duplicate_target_root": state.ComputedSymbolTargetClasses[600] = (2, 1829); break;
            case "wrong_target_class": state.ComputedSymbolTargetClasses[600] = (1, 512); break;
            case "self_symbol": state.ComputedFamilySymbols[100] = (1, new(100, 1, 400, 504)); break;
        }
        ElementBuilder.ApplyValidatedOriginalFamilySymbols(elements, state);
        if (mutation is "valid" or "block_only_symbol") Assert.Equal(600, instance.ValidatedOriginalFamilySymbolId);
        else Assert.Null(instance.ValidatedOriginalFamilySymbolId);
        Assert.Equal(500, instance.NativeMasterSymbolId);
        Assert.Null(symbol.ValidatedOriginalFamilySymbolId);
    }

    [Theory]
    [InlineData(0, 0, false)] [InlineData(1, 0, true)] [InlineData(0, 1, true)] [InlineData(1, 1, false)]
    [InlineData(2, 0, null)] [InlineData(0, 255, null)]
    public void HandAndFacingFlipsComeFromTheSameOwnRoot(byte flippedX, byte flippedY, bool? mirrored)
    {
        byte[] block = Block(flippedX: flippedX, flippedY: flippedY);
        var own = Read(block)!;
        Assert.Equal(flippedX > 1 ? null : flippedX == 1, own.FlippedX);
        Assert.Equal(flippedY > 1 ? null : flippedY == 1, own.FlippedY);
        Assert.Equal(600, own.SymbolId);
        var instance = new Element { Id = 100 };
        var state = new ParsingState(); Record(state, block);
        ElementBuilder.ApplyValidatedOriginalFamilySymbols([instance], state);
        Assert.Equal(mirrored, instance.SerializedFlippedSymbol);
        // a second, different root of the same element leaves the flips unread
        Record(state, Block(symbol: 601, flippedX: flippedX, flippedY: flippedY));
        ElementBuilder.ApplyValidatedOriginalFamilySymbols([instance], state);
        Assert.Null(instance.SerializedFlippedSymbol);
        Assert.DoesNotContain("Flipped", JsonSerializer.Serialize(instance));
    }

    [Fact]
    public void OriginalSymbol_IsPrivateAndStaleValidationIsCleared()
    {
        var instance = new Element { Id = 100, ValidatedOriginalFamilySymbolId = 600 };
        string before = JsonSerializer.Serialize(instance);
        Assert.DoesNotContain("OriginalFamilySymbol", before);
        ElementBuilder.ApplyValidatedOriginalFamilySymbols([instance], new ParsingState());
        Assert.Null(instance.ValidatedOriginalFamilySymbolId);
        Assert.Equal(before, JsonSerializer.Serialize(instance));
    }

    [Theory]
    [InlineData("valid")] [InlineData("duplicate")]
    [InlineData("foreign_document")] [InlineData("foreign_owner")] [InlineData("mixed_class")]
    public void TargetKindUsesOwnSemanticPrefix_NotUnknownLaterSubtypeFields(string mutation)
    {
        byte[] target = Block();
        BitConverter.GetBytes((ushort)1829).CopyTo(target, 12);
        // The Element prefix gives only the semantic kind and identity;
        // the later FamilySymbol subtype cannot be read from this synthetic payload.
        var field = typeof(TfBlockDeserializer).GetMethod("TryExtractOwnElementClass", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(field);
        var state = new ParsingState();
        if (mutation == "foreign_document") target = Block("foreign_document");
        BitConverter.GetBytes((ushort)1829).CopyTo(target, 12);
        RvtFileReader.RecordFamilySymbolReferenceSource(state, target, 0, Size(target), 1829,
            mutation == "foreign_owner" ? 999u : 100u);
        if (mutation == "duplicate")
            RvtFileReader.RecordFamilySymbolReferenceSource(state, target, 0, Size(target), 1829, 100);
        if (mutation == "mixed_class") Record(state, Block());
        var own = state.ComputedSymbolTargetClasses[mutation == "foreign_owner" ? 999u : 100u];
        Assert.Equal(mutation is "duplicate" or "mixed_class" ? 2 : 1, own.BlockCount);
        if (mutation == "valid") Assert.Equal((ushort)1829, own.ClassId);
        else Assert.Null(own.ClassId);
    }

    [Theory]
    [InlineData("IFCDOOR", false)] [InlineData("IFCDOOR", true)]
    [InlineData("IFCWINDOW", false)] [InlineData("IFCWINDOW", true)]
    public void OwnOriginalSymbolsSplitStyles_WhileDisplayNameAndTagStayCanonical(string product, bool reverse)
    {
        string text = Write(product, 601, 602, reverse);
        string style = product == "IFCDOOR" ? "IFCDOORSTYLE" : "IFCWINDOWSTYLE";
        string[] lines = Lines(text, style);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Contains("'Own family:Own type',$,$,$,$,'500',", line));
        Assert.All(lines, line => Assert.EndsWith(".NOTDEFINED.,.NOTDEFINED.,.F.,.F.);", line.TrimEnd()));
        Assert.Contains($"'{IfcGuid.FromName("type:500:symbol:601")}'", text);
        Assert.Contains($"'{IfcGuid.FromName("type:500:symbol:602")}'", text);
        Assert.Contains($"(#1001),#{Id(lines[reverse ? 1 : 0])});", text);
        Assert.Contains($"(#1002),#{Id(lines[reverse ? 0 : 1])});", text);
        Assert.DoesNotContain("Unrelated original name", text);
    }

    [Theory]
    [InlineData("IFCDOOR")] [InlineData("IFCWINDOW")]
    public void SameOwnSymbolGroupsTogether_WithoutUsingGeometryCandidates(string product)
    {
        string text = Write(product, 601, 601);
        Assert.Single(Lines(text, product == "IFCDOOR" ? "IFCDOORSTYLE" : "IFCWINDOWSTYLE"));
        Assert.Contains("(#1001,#1002)", text);
    }

    [Theory]
    [InlineData("IFCDOOR")] [InlineData("IFCWINDOW")]
    public void NoValidationRetainsLegacyStyleIdentity(string product)
    {
        string text = Write(product, null, null);
        Assert.Single(Lines(text, product == "IFCDOOR" ? "IFCDOORSTYLE" : "IFCWINDOWSTYLE"));
        Assert.Contains($"'{IfcGuid.FromName("type:500")}'", text);
    }

    [Fact]
    public void OtherTypeWriterBranchesAreNotRegroupedByNewValidation()
    {
        Assert.Single(Lines(Write("IFCWALL", 601, 602), "IFCWALLTYPE"));
    }

    private static string Write(string product, int? first, int? second, bool reverse = false)
    {
        Element Instance(int id, int? own) => new() { Id = id, NativeMasterSymbolId = 500,
            ValidatedOriginalFamilySymbolId = own,
            Parameters = [new Parameter { Id = -1002050, StorageType = StorageType.ElementId,
                Definition = new Definition { Name = "ELEM_TYPE_PARAM" }, Value = 500 }],
            Geometry = new GeometryData { ExactMappedInstances = [new() { SymbolId = id == 1 ? 777 : 888 }] } };
        var elements = new Dictionary<int, Element> { [1] = Instance(1, first), [2] = Instance(2, second),
            [500] = new() { Id = 500, ValidatedIfcTypeObjectName = "Own family:Own type" },
            [601] = new() { Id = 601, Name = "Unrelated original name" }, [602] = new() { Id = 602 } };
        var products = new Dictionary<int, (int entityId, int placementId, string ifcType)>();
        foreach (int id in reverse ? new[] { 2, 1 } : new[] { 1, 2 }) products.Add(id, (1000 + id, 0, product));
        var writer = new StepWriter();
        typeof(IfcExporter).GetMethod("WriteTypeObjectsAndRelDefinesByType", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [writer, 900, elements, products,
                new Dictionary<(string PropertyName, string ValueExpr), int>(), new Dictionary<int, bool?>()]);
        using var output = new StringWriter(); writer.WriteTo(output, "test.ifc", "IFC2X3", "test", "test");
        return output.ToString();
    }
    private static string[] Lines(string text, string type) => text.Split('\n')
        .Where(line => Regex.IsMatch(line, $@"^#\d+={type}\(")).ToArray();
    private static int Id(string line) => int.Parse(line[1..line.IndexOf('=')]);
    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);
    private static TfBlockDeserializer.ComputedFamilySymbol? Read(byte[] block)
        => TfBlockDeserializer.ExtractComputedFamilySymbol(block, 0, Size(block), 1794);
    private static void Record(ParsingState state, byte[] block, uint owner = 100)
        => RvtFileReader.RecordFamilySymbolReferenceSource(state, block, 0, Size(block), 1794, owner);

    private static byte[] Block(string? mutation = null, int symbol = 600, int host = -1, int orientation = 0, int master = 500,
        byte flippedX = 0, byte flippedY = 0)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(100); writer.Write(0u); writer.Write(0u); writer.Write((ushort)1794);
        foreach (var field in TfBlockDeserializer.SerializationFields(1794)!)
        {
            if (field.Declaring == 37 && field.Name == "m_id") { writer.Write(100); continue; }
            if (field.Declaring == 37 && field.Name == "m_docAccess")
            { writer.Write(mutation == "foreign_document" ? 2u : mutation == "null_document" ? 0u : 1u); continue; }
            if (field.Name == "m_masterSymbolId") { writer.Write(master); continue; }
            if (field is { Declaring: 1795, Name: "m_hostId" }) { writer.Write(host); continue; }
            if (field is { Declaring: 1794, Name: "m_insertOrientation" }) { writer.Write(orientation); continue; }
            if (field is { Declaring: 1794, Name: "m_flippedX" }) { writer.Write(flippedX); continue; }
            if (field is { Declaring: 1794, Name: "m_flippedY" }) { writer.Write(flippedY); continue; }
            if (field.Name == "m_pParamValueSetAString") { writer.Write(uint.MaxValue); writer.Write((ushort)2886); continue; }
            if (field.Name == "m_pInstanceInfo")
            {
                if (mutation == "null_pointer") { writer.Write(0u); continue; }
                writer.Write(mutation == "handle_pointer" ? 1u : uint.MaxValue);
                writer.Write(mutation == "external_pointer" ? (ushort)0 : mutation == "wrong_pointer" ? (ushort)3905 : (ushort)2238);
                continue;
            }
            if (mutation == "short_root" && field.Name == "m_refs") break;
            Default(field);
        }
        writer.Write(mutation == "prior_store" ? uint.MaxValue : 1u);
        writer.Write(11070); byte[] decoy = Encoding.Unicode.GetBytes("A misleading symbol: 999999");
        writer.Write((uint)decoy.Length / 2); writer.Write(decoy);
        for (int index = 0; index < (mutation == "short_info" ? 4 : 12); index++)
            writer.Write(mutation == "nonfinite_matrix" ? double.NaN : index is 0 or 4 or 8 ? 1d : 0d);
        if (mutation != "short_info") { writer.Write(symbol); writer.Write(0u); }
        if (mutation == "unknown_tail") writer.Write(new byte[] { 0xff, 0xde, 0xad, 0xbe, 0xef });
        writer.Write(0u);
        byte[] bytes = stream.ToArray(); uint size = (uint)bytes.Length - 16;
        BitConverter.GetBytes(size).CopyTo(bytes, 8); BitConverter.GetBytes(size).CopyTo(bytes, bytes.Length - 4);
        if (mutation == "owner") BitConverter.GetBytes(101).CopyTo(bytes, 0);
        if (mutation == "zero_owner") BitConverter.GetBytes(0).CopyTo(bytes, 0);
        if (mutation == "size") BitConverter.GetBytes(size - 1).CopyTo(bytes, 8);
        if (mutation == "footer") bytes[^1] ^= 1;
        if (mutation == "class") BitConverter.GetBytes((ushort)1829).CopyTo(bytes, 12);
        return bytes;
        void Default(TfBlockDeserializer.TfField field)
        {
            if (field.Mods >> 4 == 5) { writer.Write(0u); return; }
            int count = field.Mods >> 4 == 1 ? field.Cnt!.Value : 1;
            for (int index = 0; index < count; index++) switch (field.Type)
            {
                case 1: case 2: case 3: writer.Write((byte)0); break;
                case 4: case 5: case 6: writer.Write(0); break;
                case 7: writer.Write(0d); break;
                case 8: writer.Write(0u); break;
                case 9: writer.Write(new byte[16]); break;
                case 10: writer.Write(0L); break;
                case 13: writer.Write(new byte[(field.Cnt ?? 1) * 8]); break;
                case 14 when (field.Mods & 15) == 0 && field.Ref is not null and not 20:
                    foreach (var nested in TfBlockDeserializer.SerializationFields(field.Ref.Value)!) Default(nested); break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
        }
    }
}
