using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class SpatialPhaseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Decoder_PreservesAuthoredOrder(int prefix)
    {
        byte[] block = Block([90, 7, 31]);
        byte[] wrapped = [..new byte[prefix], ..block, 0, 0];
        Assert.True(TfBlockDeserializer.TryExtractExactProjectPhaseOrder(
            wrapped, prefix, Size(block), 187, out var phases));
        Assert.Equal(new[] { 90, 7, 31 }, phases);
    }

    [Fact]
    public void Decoder_AcceptsPhaseIdZero()
    {
        // Older projects keep phases with ids 0 and 2 (advanced structural sample project; the Revit API reports Phase Created = 0).
        byte[] block = Block([2, 0]);
        Assert.True(TfBlockDeserializer.TryExtractExactProjectPhaseOrder(block, 0, Size(block), 187, out var phases));
        Assert.Equal(new[] { 2, 0 }, phases);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("owner")]
    [InlineData("class")]
    [InlineData("footer")]
    [InlineData("size")]
    [InlineData("truncated")]
    [InlineData("tail")]
    [InlineData("unknown-tail")]
    [InlineData("count")]
    public void Decoder_RejectsIncompleteOrAmbiguousSource(string mutation)
    {
        byte[] block = Block(mutation switch
        {
            "empty" => [], "duplicate" => [3, 3], "invalid" => [3, -1], _ => [90, 7, 31],
        }, mutation);
        uint size = Size(block);
        switch (mutation)
        {
            case "owner": BitConverter.GetBytes(2).CopyTo(block, 0); break;
            case "class": BitConverter.GetBytes((ushort)3065).CopyTo(block, 12); break;
            case "footer": block[^1] ^= 1; break;
            case "size": size++; break;
            case "truncated": block = block[..^1]; break;
        }
        Assert.False(TfBlockDeserializer.TryExtractExactProjectPhaseOrder(block, 0, size, 187, out var phases));
        Assert.Null(phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recorder_DuplicatesNeverRestoreOrder(bool malformedFirst)
    {
        byte[] block = Block([90, 7, 31]);
        var state = new ParsingState();
        RvtFileReader.RecordProjectPhaseOrderSource(state, malformedFirst ? block[..^1] : block,
            0, Size(block), 187, 1);
        RvtFileReader.RecordProjectPhaseOrderSource(state, block, 0, Size(block), 187, 1);
        RvtFileReader.RecordProjectPhaseOrderSource(state, block, 0, Size(block), 187, 1);
        Assert.Equal(3, state.ProjectPhaseOrderSource.BlockCount);
        Assert.Null(state.ProjectPhaseOrderSource.PhaseIds);
    }

    [Theory]
    [InlineData("valid", 31)]
    [InlineData("missing", null)]
    [InlineData("duplicate", null)]
    [InlineData("wrong-class", null)]
    [InlineData("duplicate-id", null)]
    [InlineData("empty", null)]
    public void Builder_UsesLastResolvedSourcePhase(string mutation, int? expected)
    {
        var state = new ParsingState { ProjectPhaseOrderSource = (1, [90, 7, 31]) };
        foreach (uint id in new uint[] { 90, 7, 31 }) state.ElemClassIdx[id] = 3065;
        switch (mutation)
        {
            case "missing": state.ElemClassIdx.Remove(7); break;
            case "duplicate": state.ProjectPhaseOrderSource = (2, [90, 7, 31]); break;
            case "wrong-class": state.ElemClassIdx[31] = 3471; break;
            case "duplicate-id": state.ProjectPhaseOrderSource = (1, [90, 31, 31]); break;
            case "empty": state.ProjectPhaseOrderSource = (1, []); break;
        }
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var area = new Element { Id = 101, CategoryId = -2003200 };
        var duplicate = new Element { Id = 102, CategoryId = -2000160 };
        var info = new RevitFileInfo { Elements = [room, area, duplicate], SerializedDefaultExportPhaseId = 999 };
        foreach (uint id in new uint[] { 100, 101, 102 })
        {
            state.RoomNameSource[id] = (id == 102 ? 2 : 1, "Room");
            state.ExactRoomPhaseIds[id] = 7;
        }
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        Assert.Equal(expected, info.SerializedDefaultExportPhaseId);
        Assert.Equal(7, room.SerializedRoomPhaseId);
        Assert.Null(area.SerializedRoomPhaseId);
        Assert.Null(duplicate.SerializedRoomPhaseId);
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 7, 7)]
    [InlineData(false, 0, null)]
    public void Builder_ReadsPhaseZeroOfARoomOnlyInAWideIdFile(bool wide, int phase, int? expected)
    {
        var state = new ParsingState { WideSchema = wide ? new FileSchema(new SchemaDocument()) : null };
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var info = new RevitFileInfo { Elements = [room] };
        state.RoomNameSource[100] = (1, "Room");
        state.ExactRoomPhaseIds[100] = phase;
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        Assert.Equal(expected, room.SerializedRoomPhaseId);
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    private static byte[] Block(int[] phases, string? mutation = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)187);
        foreach (var field in TfBlockDeserializer.SerializationFields(187)!)
        {
            if (field.Name == "m_phaseIds")
            {
                writer.Write(mutation == "count" ? uint.MaxValue : (uint)phases.Length);
                foreach (int phase in phases) writer.Write(phase);
            }
            else if (field.Name == "m_cellList" && mutation == "unknown-tail")
            {
                writer.Write(uint.MaxValue); writer.Write(ushort.MaxValue);
            }
            else if (field.Name == "m_id") writer.Write(1);
            else if (field.Type == 1) writer.Write((byte)0);
            else
            {
                Assert.Equal((byte)14, field.Type);
                for (int i = 0; i < ((field.Mods >> 4) == 1 ? field.Cnt!.Value : 1); i++) writer.Write(0u);
            }
        }
        if (mutation == "tail") writer.Write(1234u);
        writer.Write(0u);
        byte[] block = stream.ToArray();
        uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8);
        BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;
    }
}
