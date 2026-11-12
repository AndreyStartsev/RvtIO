using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class OwnElementClassTests
{
    [Theory]
    [InlineData(3708)] // Run
    [InlineData(3686)] // Landing
    [InlineData(3727)] // Support
    [InlineData(3739)] // Service symbol: class identity does not make it an IFC component
    [InlineData(157)]
    public void OwnElementPrefix_IsNotAFullSubtypeOrDeferredGeometryValidation(ushort cls)
    {
        byte[] bytes = Frame(cls);
        Assert.True(TfBlockDeserializer.TryExtractOwnElementClass(bytes, 0, Size(bytes), cls, out int owner));
        Assert.Equal(123, owner);
    }

    [Theory]
    [InlineData(2007)] // GElement, independent geometry frame
    [InlineData(0)]
    [InlineData(65000)]
    public void NonElementOrUnknownClass_Rejects(ushort cls)
        => Reject(Frame(cls), cls);

    [Theory]
    [InlineData(0)] // header owner
    [InlineData(8)] // size
    [InlineData(12)] // class tag
    [InlineData(46)] // project document reference
    [InlineData(50)] // named Element.m_id
    public void ConflictingFrameOrIdentity_Rejects(int offset)
    {
        byte[] bytes = Frame(3708);
        bytes[offset] ^= 1;
        Reject(bytes, 3708);
    }

    [Fact]
    public void WrongFooter_Rejects()
    {
        byte[] bytes = Frame(3708);
        bytes[^1] ^= 1;
        Reject(bytes, 3708);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80000000u)]
    public void NonPositiveSignedHeaderOwner_Rejects(uint owner)
    {
        byte[] bytes = Frame(3708);
        BitConverter.GetBytes(owner).CopyTo(bytes, 0);
        Reject(bytes, 3708);
    }

    [Fact]
    public void EveryTruncatedFrame_Rejects()
    {
        byte[] bytes = Frame(3708);
        for (int end = 0; end < bytes.Length; end++)
        {
            Assert.False(TfBlockDeserializer.TryExtractOwnElementClass(bytes[..end], 0, Size(bytes), 3708, out int owner));
            Assert.Equal(-1, owner);
        }
    }

    [Fact]
    public void OversizedConstraintArray_Rejects()
    {
        byte[] bytes = Frame(3708);
        BitConverter.GetBytes(uint.MaxValue - 1).CopyTo(bytes, 38);
        Reject(bytes, 3708);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void InvalidStart_Rejects(int start)
    {
        byte[] bytes = Frame(3708);
        Assert.False(TfBlockDeserializer.TryExtractOwnElementClass(bytes, start, Size(bytes), 3708, out int owner));
        Assert.Equal(-1, owner);
    }

    [Fact]
    public void ValidSubspan_DoesNotRequireTheOuterBufferEnd()
    {
        byte[] bytes = Frame(3708);
        byte[] outer = [11, 12, 13, .. bytes, 99];
        Assert.True(TfBlockDeserializer.TryExtractOwnElementClass(outer, 3, Size(bytes), 3708, out int owner));
        Assert.Equal(123, owner);
    }

    [Fact]
    public void TheRootOfAnElement_IsItsRecordInTheProjectBlock_NotACopyOfItsIdInAFamilyDocument()
    {
        // the stream holds the element's root and, behind it, a block of the same id in the document of a loaded family, which the
        // framing does not list as a record of the project
        byte[] root = Frame(3708), copy = Frame(157);
        byte[] stream = [.. root, .. copy];
        var state = new ParsingState
        {
            ElemRecOwningElementIds = new() { [123] = -1 },
            FramedPartitions = [new PartitionFraming.Framed([stream[..10], stream[10..]], [new(102, 123, 0, root.Length)], 4)],
        };

        RvtFileReader.RecordFramedRoots(state);

        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 3708), state.OwnedElementClassRoots[123]);
        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 3708), state.WalkedElementClassRoots[123]);
        // counted per block met, as the stream walk does, the copy makes the root a duplicate and its class unknown
        var walked = new ParsingState { ElemRecOwningElementIds = new() { [123] = -1 } };
        StairComponentSource.Record(walked, stream, 0, Size(root), 3708, 123);
        StairComponentSource.Record(walked, stream, root.Length, Size(copy), 157, 123);
        Assert.Equal(new StairComponentSource.ElementClassRoot(2, null), walked.OwnedElementClassRoots[123]);
    }

    // TESTFILE.rvt is saved in five increments; reading it with and without the removal of the superseded copies changes the records around
    // the elements that one partition holds, but not their roots.
    [SkippableFact]
    public void TheClassOfAnElementHeldOnce_DoesNotDependOnTheRemovalOfOtherCopies()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        var (_, resolved) = new RvtFileReader(new RvtReaderOptions { ResolveCurrentCopies = true }).ReadWithState(path);
        var (_, everyCopy) = new RvtFileReader(new RvtReaderOptions { ResolveCurrentCopies = false }).ReadWithState(path);
        Assert.True(resolved.StaleCopiesRemoved);
        var heldOnce = everyCopy.WalkedElementClassRoots.Where(r => r.Value.BlockCount == 1).ToList();
        Assert.True(heldOnce.Count > 8000);
        Assert.All(heldOnce, r => Assert.Equal(r.Value, resolved.WalkedElementClassRoots[r.Key]));
        // the root of this arc wall is a record of the project's block that the stream walk does not reach
        Assert.Equal(new StairComponentSource.ElementClassRoot(1, 361), resolved.WalkedElementClassRoots[290112]);
    }

    private static uint Size(byte[] bytes) => BitConverter.ToUInt32(bytes, 8);

    private static void Reject(byte[] bytes, ushort cls)
    {
        Assert.False(TfBlockDeserializer.TryExtractOwnElementClass(bytes, 0, Size(bytes), cls, out int owner));
        Assert.Equal(-1, owner);
    }

    private static byte[] Frame(ushort cls)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(123u); writer.Write(0u); writer.Write(0u); writer.Write(cls);
        for (int index = 0; index < 6; index++) writer.Write(0u);
        writer.Write(0u); // constraints
        writer.Write(0u); // cell list
        writer.Write(1u); // project document
        writer.Write(123);
        for (int index = 1; index < 8; index++) writer.Write(-1);
        writer.Write(new byte[3]);
        uint size = (uint)stream.Length - 12;
        writer.Write(size);
        stream.Position = 8; writer.Write(size);
        return stream.ToArray();
    }
}
