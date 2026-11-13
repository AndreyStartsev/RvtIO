using System.Diagnostics;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>CI-runnable corruption and fuzz tests over synthetic <c>.rvt</c> compound files and inner streams, enforcing bounded
/// execution time and bounded memory allocations without private fixtures.</summary>
[Collection("ElemTableStaticMaps")]
public sealed class CorruptionFuzzTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rvtio-fuzz-" + Guid.NewGuid().ToString("N"));

    public CorruptionFuzzTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SyntheticContainerTruncations_AreRejectedByVerifyAndReaderWithinBounds()
    {
        byte[] validFile = BuildValidSyntheticRvtBytes();
        Assert.True(validFile.Length > 4096);

        int[] cutPoints =
        [
            0, 1, 7, 8, 64, 511, 512, 513, 1024, 1536, 2048,
            validFile.Length / 4, validFile.Length / 2, (validFile.Length * 3) / 4,
            validFile.Length - 512, validFile.Length - 1,
        ];

        string targetPath = Path.Combine(_directory, "truncated.rvt");
        var totalWatch = Stopwatch.StartNew();
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);

        foreach (int cut in cutPoints.Where(c => c >= 0 && c < validFile.Length).Distinct())
        {
            File.WriteAllBytes(targetPath, validFile[..cut]);

            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var iterWatch = Stopwatch.StartNew();

            Exception? verifyEx = Record.Exception(() => RvtContainerCheck.Verify(targetPath));
            if (verifyEx != null)
            {
                Assert.True(verifyEx is RvtInputException or RvtLimitException,
                    $"Unexpected exception from Verify at cut={cut}: {verifyEx.GetType().FullName}");
            }
            else
            {
                // OpenMcdf V3 allocates a 128-entry trailing FAT sector at the end of a small file, so only cuts in
                // trailing unreferenced FAT entries can still satisfy every declared stream length.
                Assert.True(cut > validFile.Length / 2, $"Expected Verify to reject cut={cut} (length={validFile.Length}).");
            }

            try
            {
                _ = new RvtFileReader().Read(targetPath);
            }
            catch (Exception readEx)
            {
                Assert.True(readEx is RvtInputException or RvtLimitException,
                    $"Unexpected exception from Read at cut={cut}: {readEx.GetType().FullName}");
            }

            long allocDelta = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
            Assert.True(iterWatch.ElapsedMilliseconds < 5000, $"Truncation cut={cut} took {iterWatch.ElapsedMilliseconds} ms.");
            Assert.True(allocDelta < 64L * 1024 * 1024, $"Truncation cut={cut} allocated {allocDelta} bytes.");
        }

        long retainedGrowth = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
        Assert.True(retainedGrowth < 64L * 1024 * 1024, $"Retained heap grew by {retainedGrowth} bytes.");
        Assert.True(totalWatch.ElapsedMilliseconds < 20_000, $"Container truncation suite took {totalWatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void SyntheticContainerBitFlipsZeroRangesAndMaxUintOverwrites_StayWithinTimeAndMemoryBounds()
    {
        byte[] validFile = BuildValidSyntheticRvtBytes();
        string targetPath = Path.Combine(_directory, "mutated-container.rvt");
        var random = new Random(39_2026);
        var totalWatch = Stopwatch.StartNew();
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 120; i++)
        {
            byte[] mutated = (byte[])validFile.Clone();
            switch (i % 4)
            {
                case 0:
                    // Corrupt CFB header / DIFAT / first sector
                    int headerPos = random.Next(0, Math.Min(512, mutated.Length));
                    mutated[headerPos] ^= (byte)(1 + random.Next(255));
                    break;
                case 1:
                    // Zero out a contiguous range anywhere in the container
                    int start = random.Next(0, mutated.Length);
                    int len = random.Next(1, Math.Min(1024, mutated.Length - start + 1));
                    Array.Clear(mutated, start, len);
                    break;
                case 2:
                    // Overwrite 4 bytes with 0xFFFFFFFF (uint.MaxValue / CFB ENDOFCHAIN / runaway count)
                    int maxPos = random.Next(0, mutated.Length - 4);
                    BitConverter.GetBytes(uint.MaxValue).CopyTo(mutated, maxPos);
                    break;
                default:
                    // Multi-byte random bit flips across FAT, directory, and stream sectors
                    for (int k = 0; k < 6; k++)
                        mutated[random.Next(mutated.Length)] ^= (byte)(1 + random.Next(255));
                    break;
            }

            File.WriteAllBytes(targetPath, mutated);
            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var iterWatch = Stopwatch.StartNew();

            var diagnostics = new RvtReadDiagnostics();
            try
            {
                _ = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics }).Read(targetPath);
            }
            catch (Exception ex)
            {
                Assert.False(ex is OutOfMemoryException, $"Iteration {i} threw OutOfMemoryException.");
                Assert.True(ex is RvtInputException or RvtLimitException,
                    $"Iteration {i} threw unexpected {ex.GetType().FullName}: {ex.Message}");
            }

            long allocDelta = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
            Assert.True(iterWatch.ElapsedMilliseconds < 5000, $"Iteration {i} took {iterWatch.ElapsedMilliseconds} ms.");
            Assert.True(allocDelta < 128L * 1024 * 1024, $"Iteration {i} allocated {allocDelta} bytes.");
        }

        long retainedGrowth = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
        Assert.True(retainedGrowth < 64L * 1024 * 1024, $"Retained heap grew by {retainedGrowth} bytes.");
        Assert.True(totalWatch.ElapsedMilliseconds < 30_000, $"Container fuzz suite took {totalWatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void SyntheticInnerStreamMutationsAndRunawayLengths_StayWithinTimeAndMemoryBounds()
    {
        var (elemTableStream, historyStream, formatsStream, partition1Stream, partition2Stream) = BuildValidStreams();
        string targetPath = Path.Combine(_directory, "mutated-streams.rvt");
        var random = new Random(9001);
        var totalWatch = Stopwatch.StartNew();
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 80; i++)
        {
            byte[] et = (byte[])elemTableStream.Clone();
            byte[] hist = (byte[])historyStream.Clone();
            byte[] fmt = (byte[])formatsStream.Clone();
            byte[] p1 = (byte[])partition1Stream.Clone();
            byte[] p2 = (byte[])partition2Stream.Clone();

            byte[][] targets = [et, hist, fmt, p1, p2];
            int slot = i % targets.Length;
            targets[slot] = MutateStream(targets[slot], i / targets.Length, random);

            CoverageCensusTests.WriteFile(targetPath,
                ("Global/ElemTable", targets[0]),
                ("Global/History", targets[1]),
                ("Formats/Latest", targets[2]),
                ("Partitions/1", targets[3]),
                ("Partitions/2", targets[4]));

            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            var iterWatch = Stopwatch.StartNew();
            var diagnostics = new RvtReadDiagnostics();

            try
            {
                var info = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics }).Read(targetPath);
                Assert.NotNull(info);
            }
            catch (Exception ex)
            {
                Assert.False(ex is OutOfMemoryException, $"Inner-stream iteration {i} threw OutOfMemoryException.");
                Assert.True(ex is RvtInputException or RvtLimitException,
                    $"Inner-stream iteration {i} threw unexpected {ex.GetType().FullName}: {ex.Message}");
            }

            long allocDelta = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
            Assert.True(iterWatch.ElapsedMilliseconds < 5000, $"Inner-stream iteration {i} took {iterWatch.ElapsedMilliseconds} ms.");
            Assert.True(allocDelta < 128L * 1024 * 1024, $"Inner-stream iteration {i} allocated {allocDelta} bytes.");
        }

        long retainedGrowth = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
        Assert.True(retainedGrowth < 64L * 1024 * 1024, $"Retained heap grew by {retainedGrowth} bytes.");
        Assert.True(totalWatch.ElapsedMilliseconds < 30_000, $"Inner-stream fuzz suite took {totalWatch.ElapsedMilliseconds} ms.");
    }

    private static byte[] MutateStream(byte[] source, int mode, Random random)
    {
        if (source.Length == 0) return source;
        byte[] copy = (byte[])source.Clone();
        switch (mode % 5)
        {
            case 0:
                return copy[..random.Next(0, copy.Length)];
            case 1:
                for (int k = 0; k < 5; k++)
                    copy[random.Next(copy.Length)] ^= (byte)(1 + random.Next(255));
                return copy;
            case 2:
                if (copy.Length >= 4)
                    BitConverter.GetBytes(uint.MaxValue).CopyTo(copy, random.Next(0, copy.Length - 3));
                return copy;
            case 3:
                int clearPos = random.Next(0, copy.Length);
                Array.Clear(copy, clearPos, random.Next(1, copy.Length - clearPos + 1));
                return copy;
            default:
                // Corrupt payload inside a valid PageCodec + Gzip wrapper with runaway 0xFF counts
                byte[] inner = new byte[128];
                random.NextBytes(inner);
                if (inner.Length >= 8)
                {
                    BitConverter.GetBytes((ushort)1312).CopyTo(inner, 0);
                    BitConverter.GetBytes(int.MaxValue).CopyTo(inner, 2);
                }
                return PageCodec.EncodePadded(GzipMember.Write(inner));
        }
    }

    private byte[] BuildValidSyntheticRvtBytes()
    {
        var (et, hist, fmt, p1, p2) = BuildValidStreams();
        string path = Path.Combine(_directory, "seed.rvt");
        CoverageCensusTests.WriteFile(path,
            ("Global/ElemTable", et),
            ("Global/History", hist),
            ("Formats/Latest", fmt),
            ("Partitions/1", p1),
            ("Partitions/2", p2));
        return File.ReadAllBytes(path);
    }

    private static (byte[] ElemTable, byte[] History, byte[] Formats, byte[] Partition1, byte[] Partition2) BuildValidStreams()
    {
        using var elemTable = new MemoryStream();
        using (var writer = new BinaryWriter(elemTable))
        {
            writer.Write((ushort)1312);
            writer.Write(3);
            for (int i = 0; i < 3; i++)
            {
                writer.Write(1000 + i);
                writer.Write(100 + i);
                writer.Write(64 + i);
                writer.Write(523);
                writer.Write(999);
                writer.Write(7 + i);
                writer.Write(42 + i);
            }
        }

        byte[] p1 = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(false,
                CoverageCensusTests.R(1000, CoverageCensusTests.Body("Plain", 5, 0, 0, 0)),
                CoverageCensusTests.R(1001, CoverageCensusTests.Body("Store", 9, 0, 0, 0))));
        byte[] p2 = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(true,
                CoverageCensusTests.R(1002, CoverageCensusTests.Body("Listed", 2, 0, 0, 0, 7, 0, 0, 0, 8, 0, 0, 0))));

        return (
            PageCodec.EncodePadded(GzipMember.Write(elemTable.ToArray())),
            PageCodec.EncodePadded(GzipMember.Write(new byte[64])),
            PageCodec.EncodePadded(GzipMember.Write(CoverageCensusTests.TableBytes)),
            p1,
            p2);
    }
}
