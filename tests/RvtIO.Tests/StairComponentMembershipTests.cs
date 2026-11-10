using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class StairComponentMembershipTests
{
    // Named spans independently traced in the exact existing Test_2022 fixture.
    private const int CellListStart = 345, HelperStart = 738, HelperEnd = 760;

    [Fact]
    public void OwnCellHelperSuppliesMemberListDespiteEmptyRootContainers()
    {
        var ids = Decode(Block());
        Assert.NotNull(ids);
        Assert.Equal(513254, ids.OwnerId);
        Assert.Equal(new[] { 513309 }, ids.RunsAndLandings);
        Assert.Empty(ids.Supports); Assert.Empty(ids.RegisteredRailings);
        Assert.Equal(-1, ids.MultistoryUpToLevelId);
    }

    [Fact]
    public void LaterUnrelatedDeferredGraphIsOutsideMembershipFieldValidation()
    {
        byte[] block = Block();
        Array.Fill(block, (byte)0xAB, HelperEnd, block.Length - HelperEnd - 4);
        Assert.Equal(new[] { 513309 }, Decode(block)!.RunsAndLandings);
    }

    [Fact]
    public void BareNativeIdsAreNotInterpretedAsPointersOrApiOrder()
    {
        byte[] block = Block();
        BitConverter.GetBytes(int.MinValue).CopyTo(block, HelperStart + 14);
        Assert.Equal(new[] { int.MinValue }, Decode(block)!.RunsAndLandings);
    }

    [Fact]
    public void EmptyMaxCountIsRetainedAsAnEmptyMemberList()
    {
        byte[] block = Block();
        // Remove the one member so the following support count remains at its named boundary.
        block = block[..(HelperStart + 14)].Concat(block[(HelperStart + 18)..]).ToArray();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(block, HelperStart + 10);
        Frame(block);
        Assert.Empty(Decode(block)!.RunsAndLandings);
    }

    [Fact]
    public void EveryPrefixTruncationRejectsWithoutPartialMembership()
    {
        byte[] original = Block();
        for (int end = 14; end < HelperEnd; end++)
        {
            byte[] block = original[..end].Concat(new byte[4]).ToArray(); Frame(block);
            Assert.Null(Decode(block));
        }
    }

    [Theory]
    [InlineData("header")] [InlineData("footer")] [InlineData("owner")]
    [InlineData("root_class")] [InlineData("cell_pointer")] [InlineData("helper_class")]
    [InlineData("duplicate_helper")] [InlineData("cell_count")]
    [InlineData("member_count")] [InlineData("support_count")]
    [InlineData("root_members_nonempty")]
    public void MalformedOrAmbiguousOwnChainFailsClosed(string mode)
    {
        byte[] block = Block();
        switch (mode)
        {
            case "header": block[8] ^= 1; break;
            case "footer": block[^1] ^= 1; break;
            case "owner": block[0] ^= 1; break;
            case "root_class": block[12] ^= 1; break;
            case "cell_pointer": block[50] ^= 1; break;
            case "helper_class": BitConverter.GetBytes((ushort)3684).CopyTo(block, CellListStart + 8); break;
            case "duplicate_helper": BitConverter.GetBytes((ushort)3682).CopyTo(block, CellListStart + 14); break;
            case "cell_count": BitConverter.GetBytes(100001u).CopyTo(block, CellListStart); break;
            case "member_count": BitConverter.GetBytes(100001u).CopyTo(block, HelperStart + 10); break;
            case "support_count": BitConverter.GetBytes(100001u).CopyTo(block, HelperStart + 18); break;
            case "root_members_nonempty": BitConverter.GetBytes(1u).CopyTo(block, 95); break;
        }
        Assert.Null(Decode(block));
    }

    [Fact]
    public void AStepThatCannotBeReadAfterTheHelperKeepsTheMembership()
    {
        byte[] block = Block();
        var plain = Decode(block)!;
        // The cells after the helper become a history step, whose bytes are not one, and a multistory data cell.
        BitConverter.GetBytes((ushort)3678).CopyTo(block, CellListStart + 14);
        BitConverter.GetBytes((ushort)2536).CopyTo(block, CellListStart + 20);
        var read = Decode(block);
        Assert.NotNull(read);
        Assert.Equal(plain.RunsAndLandings, read.RunsAndLandings);
        Assert.Equal(plain.Supports, read.Supports);
        Assert.Null(read.MultistoryCopyLevelIds);
    }

    [Theory]
    [InlineData(-1)] [InlineData(int.MaxValue)]
    public void InvalidStartFailsClosed(int start)
        => Assert.Null(TfBlockDeserializer.ExtractStairComponentIds(Block(), start, 2231, 3681));

    private static byte[] Block() => Convert.FromBase64String(StairElementSerializationTests.ExactStairBlockBase64);
    private static TfBlockDeserializer.StairComponentIds? Decode(byte[] block)
        => TfBlockDeserializer.ExtractStairComponentIds(block, 0, BitConverter.ToUInt32(block, 8), 3681);
    private static void Frame(byte[] block)
    {
        uint size = (uint)(block.Length - 16);
        BitConverter.GetBytes(size).CopyTo(block, 8); BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
    }
}
