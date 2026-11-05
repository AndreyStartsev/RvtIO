using System.Diagnostics;
using System.Security.Cryptography;
using OpenMcdf;
using RvtIO.Compact;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>Runs the container rewriter over the Revit project files found in the private fixture folder (<see cref="TestFiles"/>):
/// every file directly in the folder or in one level of subfolders. Skipped when there is none.</summary>
public class ContainerRewriterSampleTests(ITestOutputHelper output)
{
    // The compact JSON export, run twice per file, is the slow part (about 6 seconds each on a small file), so only the largest few files
    // of up to 8 MB are compared; the copy itself is checked for every file.
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const int MaxJsonFiles = 10;

    /// <summary>Length and SHA-256 of every stream by path, read with a loop of the test's own.</summary>
    private static SortedDictionary<string, (long Length, string Sha256)> StreamHashes(string path)
    {
        var hashes = new SortedDictionary<string, (long, string)>(StringComparer.Ordinal);
        using var root = RootStorage.OpenRead(path);
        var buffer = new byte[1 << 20];
        void Walk(Storage storage, string prefix)
        {
            foreach (var entry in storage.EnumerateEntries())
            {
                if (entry.Type == EntryType.Storage) { Walk(storage.OpenStorage(entry.Name), prefix + entry.Name + "/"); continue; }
                using var stream = storage.OpenStream(entry.Name);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { hash.AppendData(buffer, 0, read); length += read; }
                hashes.Add(prefix + entry.Name, (length, Convert.ToHexString(hash.GetHashAndReset())));
            }
        }
        Walk(root, "");
        return hashes;
    }

    private static string Fresh(string name)
    {
        string path = TestFiles.Scratch(name);
        File.Delete(path);
        return path;
    }

    [SkippableFact]
    public void CopyThrough_OfEveryProjectFile_KeepsEveryStreamByteForByte()
    {
        string copy = Fresh("container-copy.rvt");
        int files = 0, streams = 0;
        long bytes = 0, biggestFile = 0, biggestStream = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (string path in PageCodecSampleTests.ProjectFiles())
            {
                var result = ContainerRewriter.Rewrite(path, copy, new Dictionary<string, byte[]>());

                var expected = StreamHashes(path);
                var actual = StreamHashes(copy);
                Assert.Equal(expected.Select(s => (s.Key, s.Value.Length, s.Value.Sha256)), actual.Select(s => (s.Key, s.Value.Length, s.Value.Sha256)));
                Assert.Equal(expected.Count, result.Entries.Values.Count(e => e.IsStream));
                foreach (var (name, hash) in expected)
                    Assert.Equal(new ContainerEntry(true, hash.Length, hash.Sha256), result.Entries[name]);

                files++;
                streams += expected.Count;
                bytes += expected.Values.Sum(s => s.Length);
                biggestFile = Math.Max(biggestFile, new FileInfo(path).Length);
                biggestStream = Math.Max(biggestStream, expected.Values.Max(s => s.Length));
                File.Delete(copy);
            }
        }
        finally { File.Delete(copy); }
        output.WriteLine($"files={files} streams={streams} stream bytes={bytes:N0} biggest file={biggestFile:N0} biggest stream={biggestStream:N0} seconds={clock.Elapsed.TotalSeconds:F1}");
        Assert.True(files > 0);
    }

    [SkippableFact]
    public void CopyThrough_WithTheStoredBytesOfAStreamAsReplacement_GivesTheSameEntries()
    {
        string copy = Fresh("container-replace.rvt");
        string path = PageCodecSampleTests.ProjectFiles().OrderBy(file => new FileInfo(file).Length).First();
        byte[] latest;
        using (var root = RootStorage.OpenRead(path))
        using (var stream = root.OpenStorage("Formats").OpenStream("Latest"))
        {
            latest = new byte[stream.Length];
            stream.ReadExactly(latest);
        }
        try
        {
            var plain = ContainerRewriter.Rewrite(path, copy, new Dictionary<string, byte[]>());
            File.Delete(copy);
            var replaced = ContainerRewriter.Rewrite(path, copy, new Dictionary<string, byte[]> { ["Formats/Latest"] = latest });

            Assert.Equal(1, replaced.Replaced);
            Assert.Equal(plain.Entries.OrderBy(e => e.Key, StringComparer.Ordinal), replaced.Entries.OrderBy(e => e.Key, StringComparer.Ordinal));
        }
        finally { File.Delete(copy); }
    }

    [SkippableFact]
    public void CompactJson_OfTheCopy_EqualsTheJsonOfTheSource()
    {
        var all = PageCodecSampleTests.ProjectFiles();
        var compared = all.Where(path => new FileInfo(path).Length <= MaxJsonBytes)
            .OrderByDescending(path => new FileInfo(path).Length).ThenBy(path => path, StringComparer.Ordinal).Take(MaxJsonFiles).ToList();
        if (compared.Count == 0) throw new SkipException($"no project file of up to {MaxJsonBytes:N0} bytes");
        string copy = Fresh("container-json.rvt");
        string jsonSource = Fresh("container-json-source.json");
        string jsonCopy = Fresh("container-json-copy.json");
        try
        {
            foreach (string path in compared)
            {
                ContainerRewriter.Rewrite(path, copy, new Dictionary<string, byte[]>());

                CompactJsonExporter.Export(path, jsonSource);
                CompactJsonExporter.Export(copy, jsonCopy);

                Assert.True(File.ReadAllBytes(jsonSource).AsSpan().SequenceEqual(File.ReadAllBytes(jsonCopy)), $"the JSON of the copy of {Path.GetFileName(path)} differs");
                File.Delete(copy);
            }
        }
        finally
        {
            File.Delete(copy);
            File.Delete(jsonSource);
            File.Delete(jsonCopy);
        }
        output.WriteLine($"files compared={compared.Count} of {all.Count}, largest {new FileInfo(compared[0]).Length:N0} bytes, smallest {new FileInfo(compared[^1]).Length:N0} bytes");
    }
}
