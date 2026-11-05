using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class StairRunInputTests
{
    [Theory]
    [InlineData(3725, 1, 3)] [InlineData(3721, 1, 2)] [InlineData(3721, 3, 2)]
    public void CompleteOwnGraphRetainsExactStyleAndCachedPath(int cellClass, int curves, int style)
    {
        var fixture = Build((ushort)cellClass, curves);
        var input = Decode(fixture.Bytes);
        Assert.NotNull(input); Assert.Equal(42, input.OwnerId); Assert.Equal(43, input.StairsId);
        Assert.Equal(style, (int)input.Style); Assert.Equal(curves, input.Directions.Length);
        Assert.All(input.Directions, direction => Assert.Equal(new(1, -0.0, 0), direction));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(input.Directions[0].Y));
    }

    [Fact]
    public void CellBaseLineIsNotTheApiCachedPath()
    {
        var fixture = Build(3725, 1, baseLine: true);
        var input = Decode(fixture.Bytes);
        Assert.NotNull(input);
        Assert.Equal(new(1, -0.0, 0), Assert.Single(input.Directions));
    }

    [Fact]
    public void EveryPayloadTruncationRejectsWithSelfConsistentFraming()
    {
        byte[] original = Build().Bytes;
        for (int length = 14; length < original.Length - 4; length++)
        {
            byte[] shortened = original[..length].Concat(new byte[4]).ToArray(); Frame(shortened);
            Assert.Null(Decode(shortened));
        }
    }

    [Fact]
    public void TrailingPayloadIsNotAcceptedByMatchingHeaderAndFooter()
    {
        byte[] original = Build().Bytes;
        byte[] extra = original[..^4].Concat(new byte[5]).ToArray(); Frame(extra);
        Assert.Null(Decode(extra));
    }

    [Theory]
    [InlineData("header")] [InlineData("footer")] [InlineData("owner")] [InlineData("document")]
    [InlineData("named_id")] [InlineData("parent_self")] [InlineData("parent_negative")]
    [InlineData("class")] [InlineData("layout_count")] [InlineData("joins_count")]
    [InlineData("cache_count")] [InlineData("cache_null")] [InlineData("cache_wrong_class")]
    [InlineData("cell_null")] [InlineData("cell_wrong_class")] [InlineData("cell_count")]
    [InlineData("open_bool")] [InlineData("curve_count")] [InlineData("curve_class")]
    [InlineData("direction_nan")] [InlineData("direction_inf")] [InlineData("direction_zero")]
    [InlineData("direction_vertical")] [InlineData("origin_nan")] [InlineData("params_inf")]
    [InlineData("params_empty")]
    public void InvalidAuthorityOrCachedCurveFailsClosed(string mutation)
    {
        var fixture = Build(); byte[] bytes = fixture.Bytes; var p = fixture.Positions;
        void U32(int position, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, position);
        void F64(int position, double value) => BitConverter.GetBytes(value).CopyTo(bytes, position);
        switch (mutation)
        {
            case "header": bytes[8] ^= 1; break;
            case "footer": bytes[^1] ^= 1; break;
            case "owner": bytes[0] ^= 1; break;
            case "document": U32(p["-1/m_docAccess"], 2); break;
            case "named_id": U32(p["-1/m_id"], 44); break;
            case "parent_self": U32(p["-1/m_stairsId"], 42); break;
            case "parent_negative": U32(p["-1/m_stairsId"], uint.MaxValue); break;
            case "class": bytes[12] ^= 1; break;
            case "layout_count": U32(p["-1/m_compatibleCrvInfo"], 100001); break;
            case "joins_count": U32(p["-1/m_logicJoinInfo"], 100001); break;
            case "cache_count": U32(p["-1/m_oRefCurvesArr"], 0); break;
            case "cache_null": U32(p["-1/m_oRefCurvesArr"] + 4, 0); break;
            case "cache_wrong_class": BitConverter.GetBytes((ushort)1736).CopyTo(bytes, p["-1/m_oRefCurvesArr"] + 8); break;
            case "cell_null": U32(p["-1/m_cellList"], 0); break;
            case "cell_wrong_class": BitConverter.GetBytes((ushort)938).CopyTo(bytes, p["-1/m_cellList"] + 4); break;
            case "cell_count": U32(p["0/m_cells"], 100001); break;
            case "open_bool": bytes[p["1/m_open"]] = 2; break;
            case "curve_count": U32(p["1/m_curves"], 100001); break;
            case "curve_class": BitConverter.GetBytes((ushort)938).CopyTo(bytes, p["1/m_curves"] + 8); break;
            case "direction_nan": F64(p["3/m_dirVec"], double.NaN); break;
            case "direction_inf": F64(p["3/m_dirVec"], double.PositiveInfinity); break;
            case "direction_zero": F64(p["3/m_dirVec"], 0); break;
            case "direction_vertical": F64(p["3/m_dirVec"] + 16, 1); break;
            case "origin_nan": F64(p["3/m_origin"], double.NaN); break;
            case "params_inf": F64(p["3/m_endParams"] + 8, double.PositiveInfinity); break;
            case "params_empty": F64(p["3/m_endParams"] + 8, 0); break;
        }
        Assert.Null(Decode(bytes));
    }

    [Theory]
    [InlineData(-1)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void InvalidStartFailsClosed(int start)
        => Assert.Null(TfBlockDeserializer.ExtractStairRunInput(Build().Bytes, start, 20, 3708));

    [Fact]
    public void DuplicateOwnRunCellsRejectInsteadOfSelectingOne()
        => Assert.Null(Decode(Build(duplicateCell: true).Bytes));

    [Fact]
    public void ASpiralCellWithArcsAsBaseLineIsDecodedWithoutAPath()
    {
        var input = Decode(Build(3723, 2, curveClass: 1974).Bytes);
        Assert.NotNull(input);
        Assert.Equal((42, 43, TfBlockDeserializer.StairRunStyle.Spiral), (input.OwnerId, input.StairsId, input.Style));
        Assert.Empty(input.Directions);
    }

    [Theory]
    [InlineData(3725)] [InlineData(3721)] [InlineData(3743)]
    public void OnlyASpiralCellMayHaveArcsAsBaseLine(int cellClass)
        => Assert.Null(Decode(Build((ushort)cellClass, 1, curveClass: 1974).Bytes));

    [Fact]
    public void AWinderCellRemainsUnsupported()
        => Assert.Null(Decode(Build(3743).Bytes));

    private static TfBlockDeserializer.StairRunInput? Decode(byte[] bytes)
        => TfBlockDeserializer.ExtractStairRunInput(bytes, 0, BitConverter.ToUInt32(bytes, 8), 3708);

    internal static (byte[] Bytes, Dictionary<string, int> Positions) Build(ushort cellClass = 3725,
        int curves = 1, bool baseLine = false, bool duplicateCell = false, ushort curveClass = 1736)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var positions = new Dictionary<string, int>(); var queue = new List<ushort>();
        writer.Write(42u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)3708);
        Class(3708, -1);
        for (int index = 0; index < queue.Count; index++) Class(queue[index], index);
        writer.Write(0u); byte[] bytes = stream.ToArray(); Frame(bytes);
        return (bytes, positions);

        void Pointer(ushort cls)
        {
            writer.Write(uint.MaxValue); writer.Write(cls); queue.Add(cls);
        }
        void Class(ushort cls, int index)
        {
            foreach (var field in TfBlockDeserializer.SerializationFields(cls)!)
            {
                positions[$"{index}/{field.Name}"] = checked((int)stream.Position);
                if (index == -1 && field.Name == "m_docAccess") { writer.Write(1u); continue; }
                if (index == -1 && field.Name == "m_id") { writer.Write(42); continue; }
                if (index == -1 && field.Name == "m_stairsId") { writer.Write(43); continue; }
                if (index == -1 && field.Name == "m_cellList") { Pointer(653); continue; }
                if (index == -1 && field.Name == "m_compatibleCrvInfo") { writer.Write(new byte[21]); continue; }
                if (index == -1 && field.Name == "m_oRefCurvesArr") { writer.Write(1u); Pointer(938); continue; }
                if (cls == 653 && field.Name == "m_cells")
                {
                    writer.Write(duplicateCell ? 2u : 1u); Pointer(cellClass);
                    if (duplicateCell) Pointer(cellClass);
                    continue;
                }
                if (cls == 938 && field.Name == "m_curves")
                {
                    writer.Write((uint)curves); for (int i = 0; i < curves; i++) Pointer(curveClass); continue;
                }
                if (cls == 3725 && baseLine && field.Name == "m_baseLines") { writer.Write(1u); Pointer(1736); continue; }
                if (cls == 1736 && field.Name == "m_endParams") { writer.Write(0.0); writer.Write(1.0); continue; }
                if (cls == 1736 && field.Name == "m_dirVec")
                {
                    bool ownPath = index >= 3 && index < 3 + curves;
                    writer.Write(ownPath ? 1.0 : 0.0); writer.Write(-0.0); writer.Write(ownPath ? 0.0 : 1.0); continue;
                }
                if (cls == 1736 && field.Name == "m_GInfo") { writer.Write(new byte[16]); continue; }
                Zero(field);
            }
        }
        void Zero(TfBlockDeserializer.TfField field)
        {
            if (field.Mods >> 4 is 5 or 6) { writer.Write(0u); return; }
            if (field.Type == 14)
            {
                if (field.Ref is not null && field.Ref != 20 && field.Mods == 0)
                    foreach (var child in TfBlockDeserializer.SerializationFields(field.Ref.Value)!) Zero(child);
                else writer.Write(0u);
                return;
            }
            int width = field.Type switch { 1 or 2 or 3 => 1, 4 or 5 or 6 => 4, 7 or 10 or 13 => 8, 9 => 16, _ => throw new InvalidOperationException() };
            writer.Write(new byte[width * (field.Mods == 16 ? field.Cnt!.Value : 1)]);
        }
    }
    private static void Frame(byte[] bytes)
    {
        uint size = checked((uint)(bytes.Length - 16));
        BitConverter.GetBytes(size).CopyTo(bytes, 8); BitConverter.GetBytes(size).CopyTo(bytes, bytes.Length - 4);
    }
}
