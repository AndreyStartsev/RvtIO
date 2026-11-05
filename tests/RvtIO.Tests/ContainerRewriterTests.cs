using System.Security.Cryptography;
using OpenMcdf;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Container rewriter on compound files built here with OpenMcdf: no private file is needed.</summary>
public sealed class ContainerRewriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-container-" + Guid.NewGuid().ToString("N"));

    public ContainerRewriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string P(string name) => Path.Combine(_folder, name);

    /// <summary>Deterministic bytes that do not repeat inside a sector.</summary>
    private static byte[] Content(string seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed.Sum(c => c) + length).NextBytes(bytes);
        return bytes;
    }

    // Sizes on both sides of the 4,096 byte limit of the mini stream, an empty stream, and streams longer than the 1 MB copy buffer.
    private static readonly (string Path, int Length)[] SourceStreams =
    [
        ("Empty", 0), ("One", 1), ("Small", 10), ("Mini4095", 4095), ("Mini4096", 4096), ("Mini4097", 4097), ("Big", 1_500_000),
        ("Global/Latest", 5000), ("Global/Inner/Deep", 70_000), ("Global/Inner/DeepEmpty", 0), ("Partitions/1", 2_300_000),
    ];

    private string CreateSource(string name = "source.rvt", OpenMcdf.Version version = OpenMcdf.Version.V4)
    {
        string path = P(name);
        using var root = RootStorage.Create(path, version);
        root.CreateStorage("EmptyStorage");
        foreach (var (streamPath, length) in SourceStreams)
        {
            Storage storage = root;
            string[] parts = streamPath.Split('/');
            foreach (string part in parts[..^1]) storage = storage.TryOpenStorage(part, out var existing) ? existing : storage.CreateStorage(part);
            using var stream = storage.CreateStream(parts[^1]);
            stream.Write(Content(streamPath, length));
        }
        return path;
    }

    /// <summary>Every storage (null) and stream of the file by path.</summary>
    private static Dictionary<string, byte[]?> ReadTree(string path)
    {
        var tree = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        using var root = RootStorage.OpenRead(path);
        void Walk(Storage storage, string prefix)
        {
            foreach (var entry in storage.EnumerateEntries())
            {
                if (entry.Type == EntryType.Storage)
                {
                    tree.Add(prefix + entry.Name, null);
                    Walk(storage.OpenStorage(entry.Name), prefix + entry.Name + "/");
                    continue;
                }
                using var stream = storage.OpenStream(entry.Name);
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                tree.Add(prefix + entry.Name, bytes);
            }
        }
        Walk(root, "");
        return tree;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static readonly IReadOnlyDictionary<string, byte[]> NoReplacements = new Dictionary<string, byte[]>();

    private static ushort MajorVersion(string path)
    {
        using var file = File.OpenRead(path);
        var header = new byte[28];
        file.ReadExactly(header);
        return BitConverter.ToUInt16(header, 26);
    }

    private void AssertOnlyFiles(params string[] names) =>
        Assert.Equal(names.Select(P).Order(StringComparer.Ordinal), Directory.EnumerateFileSystemEntries(_folder).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData(OpenMcdf.Version.V3, 3)]
    [InlineData(OpenMcdf.Version.V4, 4)]
    public void Copy_WithoutReplacements_KeepsTheTreeTheStreamsAndTheSectorSize(OpenMcdf.Version version, int major)
    {
        string source = CreateSource(version: version);
        string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));

        var result = ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements);

        var expected = ReadTree(source);
        var copy = ReadTree(P("out.rvt"));
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), copy.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
            Assert.True(bytes is null ? copy[path] is null : bytes.AsSpan().SequenceEqual(copy[path]), $"'{path}' differs");
        Assert.Equal(SourceStreams.Length, copy.Values.Count(bytes => bytes is not null));
        Assert.Contains("EmptyStorage", copy.Keys);
        Assert.Contains("Global/Inner", copy.Keys);

        Assert.Equal((ushort)major, MajorVersion(P("out.rvt")));
        Assert.Equal(0, result.Replaced);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), result.Entries.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
            Assert.Equal(bytes is null ? new ContainerEntry(false, 0, "") : new ContainerEntry(true, bytes.Length, Sha(bytes)), result.Entries[path]);

        Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        AssertOnlyFiles("source.rvt", "out.rvt");
    }

    [Fact]
    public void Rewrite_ReplacesTheNamedStreams_AndCopiesTheOthers()
    {
        string source = CreateSource();
        var replacements = new Dictionary<string, byte[]>
        {
            ["Small"] = Content("new small", 200_000),       // a mini stream that becomes a regular one
            ["Big"] = [],                                    // a long stream that becomes empty
            ["Global/Inner/Deep"] = Content("new deep", 100), // a stream in a nested storage, shorter
            ["Mini4096"] = Content("new 4096", 4097),
            ["Empty"] = Content("new empty", 3),
        };

        var result = ContainerRewriter.Rewrite(source, P("out.rvt"), replacements);

        var expected = ReadTree(source);
        foreach (var (path, bytes) in replacements) expected[path] = bytes;
        var copy = ReadTree(P("out.rvt"));
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), copy.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in expected)
            Assert.True(bytes is null ? copy[path] is null : bytes.AsSpan().SequenceEqual(copy[path]), $"'{path}' differs");
        Assert.Equal(5, result.Replaced);
        Assert.Equal(new ContainerEntry(true, 0, Sha([])), result.Entries["Big"]);
        Assert.Equal(new ContainerEntry(true, 200_000, Sha(replacements["Small"])), result.Entries["Small"]);
        AssertOnlyFiles("source.rvt", "out.rvt");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(8192)]
    [InlineData(1_048_575)]
    [InlineData(1_048_576)]
    [InlineData(1_048_577)]
    [InlineData(3_000_000)]
    public void Rewrite_StoresReplacementsOfAnyLength(int length)
    {
        string source = CreateSource();
        byte[] bytes = Content("replacement", length);

        ContainerRewriter.Rewrite(source, P("out.rvt"), new Dictionary<string, byte[]> { ["Mini4095"] = bytes });

        Assert.True(bytes.AsSpan().SequenceEqual(ReadTree(P("out.rvt"))["Mini4095"]));
    }

    [Fact]
    public void Rewrite_MatchesStreamNamesWithoutRegardToCase()
    {
        string source = CreateSource();
        byte[] bytes = Content("case", 77);

        var result = ContainerRewriter.Rewrite(source, P("out.rvt"), new Dictionary<string, byte[]> { ["global/INNER/deep"] = bytes });

        Assert.Equal(1, result.Replaced);
        Assert.True(bytes.AsSpan().SequenceEqual(ReadTree(P("out.rvt"))["Global/Inner/Deep"]));
    }

    [Fact]
    public void Rewrite_Refuses_TwoKeysForOneStream()
    {
        string source = CreateSource();
        var replacements = new Dictionary<string, byte[]> { ["Small"] = [1], ["SMALL"] = [2] };

        var error = Assert.Throws<ArgumentException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), replacements));

        Assert.Contains("twice", error.Message);
        AssertOnlyFiles("source.rvt");
    }

    [Theory]
    [InlineData("NoSuchStream")]
    [InlineData("Global/NoSuchStream")]
    [InlineData("NoSuchStorage/Latest")]
    [InlineData("Global")]            // a storage
    [InlineData("Global/Inner")]
    [InlineData("EmptyStorage")]
    [InlineData("")]
    public void Rewrite_Refuses_AKeyThatIsNotAStreamOfTheSource_AndLeavesNoFile(string key)
    {
        string source = CreateSource();
        var replacements = new Dictionary<string, byte[]> { ["Small"] = [1, 2, 3], [key] = [9] };

        var error = Assert.Throws<ArgumentException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), replacements));

        Assert.Contains("not a stream of the source", error.Message);
        // The file was written completely before the key was found missing: the temporary file must be gone.
        AssertOnlyFiles("source.rvt");
    }

    [Fact]
    public void Rewrite_Refuses_AnExistingOutput_AndKeepsIt()
    {
        string source = CreateSource();
        File.WriteAllBytes(P("out.rvt"), [1, 2, 3]);

        var error = Assert.Throws<IOException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements));

        Assert.Contains("already exists", error.Message);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(P("out.rvt")));
        AssertOnlyFiles("source.rvt", "out.rvt");
    }

    [Fact]
    public void Rewrite_Refuses_TheSourceAsOutput_AndKeepsIt()
    {
        string source = CreateSource();
        byte[] before = File.ReadAllBytes(source);

        var error = Assert.Throws<IOException>(() => ContainerRewriter.Rewrite(source, Path.Combine(_folder, ".", "source.rvt"), NoReplacements));

        Assert.Contains("already exists", error.Message);
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(source)));
        AssertOnlyFiles("source.rvt");
    }

    [Fact]
    public void Rewrite_Refuses_AnOutputThatIsAFolder()
    {
        string source = CreateSource();
        Directory.CreateDirectory(P("out.rvt"));

        var error = Assert.Throws<IOException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements));

        Assert.Contains("already exists", error.Message);
        AssertOnlyFiles("source.rvt", "out.rvt");
    }

    [Fact]
    public void Rewrite_Throws_WhenTheOutputFolderIsMissing()
    {
        string source = CreateSource();

        Assert.Throws<DirectoryNotFoundException>(() => ContainerRewriter.Rewrite(source, P("missing/out.rvt"), NoReplacements));

        AssertOnlyFiles("source.rvt");
    }

    [Fact]
    public void Rewrite_Throws_WhenTheSourceIsMissing_AndLeavesNoFile()
    {
        Assert.Throws<FileNotFoundException>(() => ContainerRewriter.Rewrite(P("none.rvt"), P("out.rvt"), NoReplacements));

        AssertOnlyFiles();
    }

    [Fact]
    public void Rewrite_Throws_ForAFileThatIsNotACompoundFile_AndLeavesNoFile()
    {
        File.WriteAllBytes(P("text.rvt"), Content("not a container", 5000));

        Assert.Throws<FileFormatException>(() => ContainerRewriter.Rewrite(P("text.rvt"), P("out.rvt"), NoReplacements));

        AssertOnlyFiles("text.rvt");
    }

    [Fact]
    public void Rewrite_Throws_ForACutSource_AndLeavesNoFile()
    {
        string source = CreateSource();
        byte[] whole = File.ReadAllBytes(source);
        File.WriteAllBytes(P("cut.rvt"), whole.AsSpan(0, whole.Length / 2).ToArray());

        Assert.Throws<InvalidDataException>(() => ContainerRewriter.Rewrite(P("cut.rvt"), P("out.rvt"), NoReplacements));

        AssertOnlyFiles("source.rvt", "cut.rvt");
    }

    [Fact]
    public void Rewrite_Throws_ForStoragesNestedTooDeep_AndLeavesNoFile()
    {
        string source = P("deep.rvt");
        using (var root = RootStorage.Create(source, OpenMcdf.Version.V3))
        {
            Storage storage = root;
            for (int level = 0; level < 65; level++) storage = storage.CreateStorage("S");
            using var stream = storage.CreateStream("Leaf");
            stream.Write([1]);
        }

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements));

        Assert.Contains("deeper than", error.Message);
        AssertOnlyFiles("deep.rvt");
    }

    [Fact]
    public void Rewrite_AcceptsTheLongestNestingItCopies()
    {
        string source = P("deep.rvt");
        using (var root = RootStorage.Create(source, OpenMcdf.Version.V3))
        {
            Storage storage = root;
            for (int level = 0; level < 64; level++) storage = storage.CreateStorage("S");
            using var stream = storage.CreateStream("Leaf");
            stream.Write([1]);
        }

        var result = ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements);

        Assert.Equal(65, result.Entries.Count);
    }

    [Fact]
    public void Rewrite_RemovesTheTemporaryFile_AndWritesNoOutput_WhenTheWrittenFileDiffers()
    {
        string source = CreateSource();
        string? seen = null;
        void Damage(string temporary)
        {
            seen = temporary;
            using var root = RootStorage.Open(temporary, FileMode.Open);
            using var stream = root.OpenStream("Big");
            stream.Position = 1_234_567;
            stream.WriteByte((byte)(stream.ReadByte() ^ 0xFF));
        }

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements, Damage));

        Assert.Contains("'Big'", error.Message);
        Assert.NotNull(seen);
        Assert.Equal(_folder, Path.GetDirectoryName(seen));
        AssertOnlyFiles("source.rvt");
    }

    [SkippableFact]
    public void Rewrite_Refuses_ASourceThatIsOpenForWriting_AndLeavesNoFile()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("File sharing is enforced by the system on Windows only.");
        string source = CreateSource();

        using (var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Assert.Throws<IOException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements));

        AssertOnlyFiles("source.rvt");
        ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements);
    }

    [Fact]
    public void Rewrite_Throws_WhenTheWrittenFileDeclaresAnotherLengthThanItHolds()
    {
        string source = CreateSource();
        // An empty stream whose directory entry declares 8,192 bytes and a first sector past the end of the file reads as empty,
        // so only the declared length shows the damage.
        void Damage(string temporary)
        {
            using var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite);
            var header = new byte[512];
            file.ReadExactly(header);
            int sectorSize = 1 << BitConverter.ToUInt16(header, 30);
            long directory = (BitConverter.ToUInt32(header, 48) + 1L) * sectorSize;
            var sector = new byte[sectorSize];
            file.Position = directory;
            file.ReadExactly(sector);
            bool found = false;
            for (int offset = 0; offset < sectorSize; offset += 128)
            {
                int nameBytes = BitConverter.ToUInt16(sector, offset + 64);
                if (System.Text.Encoding.Unicode.GetString(sector, offset, Math.Max(nameBytes - 2, 0)) != "Empty") continue;
                BitConverter.TryWriteBytes(sector.AsSpan(offset + 116), 0x0010_0000u);
                BitConverter.TryWriteBytes(sector.AsSpan(offset + 120), 8192UL);
                found = true;
            }
            Assert.True(found);
            file.Position = directory;
            file.Write(sector);
        }

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements, Damage));

        Assert.Contains("'Empty'", error.Message);
        Assert.Contains("8,192", error.Message);
        AssertOnlyFiles("source.rvt");
    }

    [Fact]
    public void Rewrite_WritesTheOutput_OnlyWhenTheWrittenFileIsRead_AndTheCheckSeesTheTemporaryFile()
    {
        string source = CreateSource();
        bool outputExistedBeforeTheCheck = true;

        ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements, _ => outputExistedBeforeTheCheck = File.Exists(P("out.rvt")));

        Assert.False(outputExistedBeforeTheCheck);
        Assert.True(File.Exists(P("out.rvt")));
    }

    /// <summary>A written file that is changed in one way; the check must name the difference.</summary>
    private (string Path, ContainerRewriteResult Result) Written()
    {
        string source = CreateSource();
        var result = ContainerRewriter.Rewrite(source, P("out.rvt"), NoReplacements);
        ContainerRewriter.CheckWritten(P("out.rvt"), result.Entries);
        return (P("out.rvt"), result);
    }

    [Fact]
    public void CheckWritten_Finds_OneChangedBitInAStream()
    {
        var (path, result) = Written();
        using (var root = RootStorage.Open(path, FileMode.Open))
        using (var stream = root.OpenStorage("Global").OpenStorage("Inner").OpenStream("Deep"))
        {
            stream.Position = 33_333;
            int value = stream.ReadByte();
            stream.Position = 33_333;
            stream.WriteByte((byte)(value ^ 0x01));
        }

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));

        Assert.Contains("'Global/Inner/Deep'", error.Message);
    }

    [Fact]
    public void CheckWritten_Finds_AChangedLength()
    {
        var (path, result) = Written();
        using (var root = RootStorage.Open(path, FileMode.Open))
        using (var stream = root.OpenStream("Mini4096"))
            stream.SetLength(4095);

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));

        Assert.Contains("'Mini4096'", error.Message);
        Assert.Contains("4,095", error.Message);
    }

    [Fact]
    public void CheckWritten_Finds_AMissingStream()
    {
        var (path, result) = Written();
        using (var root = RootStorage.Open(path, FileMode.Open))
            root.OpenStorage("Partitions").Delete("1");

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));

        Assert.Contains("lacks 'Partitions/1'", error.Message);
    }

    [Fact]
    public void CheckWritten_Finds_AMissingStorage()
    {
        var (path, result) = Written();
        using (var root = RootStorage.Open(path, FileMode.Open))
            root.Delete("EmptyStorage");

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));

        Assert.Contains("lacks 'EmptyStorage'", error.Message);
    }

    [Fact]
    public void CheckWritten_Finds_AnExtraStream()
    {
        var (path, result) = Written();
        using (var root = RootStorage.Open(path, FileMode.Open))
        using (var extra = root.OpenStorage("Global").CreateStream("Extra"))
            extra.Write([1]);

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));

        Assert.Contains("holds 'Global/Extra', which was not written", error.Message);
    }

    [Fact]
    public void CheckWritten_Finds_AStreamWhereAStorageWasWritten_AndTheOtherWayRound()
    {
        var (path, result) = Written();
        using (var root = RootStorage.Open(path, FileMode.Open))
        {
            root.Delete("EmptyStorage");
            using var stream = root.CreateStream("EmptyStorage");
            stream.Write([1]);
        }
        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));
        Assert.Contains("'EmptyStorage' was written as a storage", error.Message);

        using (var root = RootStorage.Open(path, FileMode.Open))
        {
            root.Delete("EmptyStorage");
            root.Delete("One");
            root.CreateStorage("One");
            root.CreateStorage("EmptyStorage");
        }
        error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(path, result.Entries));
        Assert.Contains("'One' was written as a stream", error.Message);
    }

    [Fact]
    public void CheckWritten_Accepts_TheExpectedFile_AndRejectsAnotherFileWithTheSameShape()
    {
        var (path, result) = Written();
        ContainerRewriter.CheckWritten(path, result.Entries);

        // Same names and lengths, other bytes in the stream with the replacement.
        string other = P("other.rvt");
        ContainerRewriter.Rewrite(P("source.rvt"), other, new Dictionary<string, byte[]> { ["Small"] = Content("another", 10) });

        var error = Assert.Throws<InvalidDataException>(() => ContainerRewriter.CheckWritten(other, result.Entries));
        Assert.Contains("'Small'", error.Message);
    }
}
