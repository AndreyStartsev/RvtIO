using System.Globalization;
using System.Security.Cryptography;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>
/// Exercises <see cref="RvtWriter"/> and <see cref="RvtDocument.SaveAs"/> across real Revit project (<c>.rvt</c>) and family (<c>.rfa</c>)
/// files located in the private fixture folder (<see cref="TestFiles"/>). Skipped cleanly when no private fixtures are present.
/// </summary>
public sealed class RvtWriterSampleTests : IDisposable
{
    private const long MaxTargetedRewriteBytes = 32L * 1024 * 1024;
    private const int MaxTargetedRewriteFiles = 6;
    private readonly ITestOutputHelper _output;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-writer-samples-" + Guid.NewGuid().ToString("N"));

    public RvtWriterSampleTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static List<string> SampleRevitFiles()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string root in TestFiles.Roots())
        {
            foreach (string ext in new[] { "*.rvt", "*.rfa" })
            {
                foreach (string file in Directory.EnumerateFiles(root, ext))
                    files.Add(file);
                foreach (string folder in Directory.EnumerateDirectories(root))
                    foreach (string file in Directory.EnumerateFiles(folder, ext))
                        files.Add(file);
            }
        }
        if (files.Count == 0)
            throw TestFiles.Missing("*.rvt / *.rfa Revit sample files");
        return [.. files];
    }

    private static SortedDictionary<string, (long Length, string Sha256)> StreamHashes(string path)
    {
        var hashes = new SortedDictionary<string, (long, string)>(StringComparer.Ordinal);
        using var root = RootStorage.OpenRead(path);
        byte[] buffer = new byte[1 << 20];
        void Walk(Storage storage, string prefix)
        {
            foreach (var entry in storage.EnumerateEntries())
            {
                if (entry.Type == EntryType.Storage)
                {
                    Walk(storage.OpenStorage(entry.Name), prefix + entry.Name + "/");
                    continue;
                }
                using var stream = storage.OpenStream(entry.Name);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                    length += read;
                }
                hashes.Add(prefix + entry.Name, (length, Convert.ToHexString(hash.GetHashAndReset())));
            }
        }
        Walk(root, "");
        return hashes;
    }

    private string Fresh(string name)
    {
        string path = Path.Combine(_folder, name);
        File.Delete(path);
        return path;
    }

    [SkippableFact]
    public void SaveAs_OnSupportedSampleFiles_PreservesStreamsIdentitiesRelationshipsGeometryAndExternalRefs()
    {
        int supportedCount = 0;
        int refusedCount = 0;

        foreach (string source in SampleRevitFiles())
        {
            var caps = RvtWriter.InspectCapabilities(source);
            string ext = Path.GetExtension(source);
            string copy = Fresh("rvtwriter-saveas" + ext);
            string? stagedSource = null;
            try
            {
                if (!caps.Supports(RvtWriteOperation.SaveAs))
                {
                    refusedCount++;
                    Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(source, copy));
                    Assert.False(File.Exists(copy));
                    continue;
                }

                supportedCount++;
                RvtWriteResult writeResult;
                try
                {
                    writeResult = RvtWriter.SaveAs(source, copy);
                }
                catch (RvtWriteRefusedException ex) when (ex.Kind == "relative-reference-relocation")
                {
                    Assert.False(File.Exists(copy));
                    stagedSource = Fresh("rvtwriter-staged-saveas" + ext);
                    File.Copy(source, stagedSource);
                    writeResult = RvtWriter.SaveAs(stagedSource, copy);
                }

                Assert.Equal(caps.FileKind, writeResult.FileKind);
                Assert.Equal(caps.EffectiveRelease, writeResult.EffectiveRelease);

                var expectedStreams = StreamHashes(stagedSource ?? source);
                var actualStreams = StreamHashes(copy);
                Assert.Equal(
                    expectedStreams.Select(s => (s.Key, s.Value.Length, s.Value.Sha256)),
                    actualStreams.Select(s => (s.Key, s.Value.Length, s.Value.Sha256)));

                var srcDoc = RvtDocument.Open(stagedSource ?? source);
                var dstDoc = RvtDocument.Open(copy);

                Assert.Equal(srcDoc.Elements.Count, dstDoc.Elements.Count);
                Assert.Equal(
                    srcDoc.Elements.Select(e => (e.Id, e.UniqueId, e.CategoryId, e.FamilyTypeId, e.HostId, e.References?.Count ?? 0, e.Geometry?.Solids?.Count ?? -1, e.Geometry?.Faces?.Count ?? -1)),
                    dstDoc.Elements.Select(e => (e.Id, e.UniqueId, e.CategoryId, e.FamilyTypeId, e.HostId, e.References?.Count ?? 0, e.Geometry?.Solids?.Count ?? -1, e.Geometry?.Faces?.Count ?? -1)));

                var srcRefs = srcDoc.TransmissionData?.ExternalReferences.Select(r => (r.ElementId, r.ReferenceType, r.Path, r.PathType, r.LoadState)).ToArray() ?? [];
                var dstRefs = dstDoc.TransmissionData?.ExternalReferences.Select(r => (r.ElementId, r.ReferenceType, r.Path, r.PathType, r.LoadState)).ToArray() ?? [];
                Assert.Equal(srcRefs, dstRefs);
            }
            finally
            {
                File.Delete(copy);
                if (stagedSource is not null) File.Delete(stagedSource);
            }
        }

        _output.WriteLine($"supported={supportedCount}, refused={refusedCount}");
        Assert.True(supportedCount + refusedCount > 0);
    }

    [SkippableFact]
    public void TargetedRewrite_OnSampleFiles_VerifiesChecksumsAndPreservesModelGraph()
    {
        var candidates = SampleRevitFiles()
            .Where(p => new FileInfo(p).Length <= MaxTargetedRewriteBytes)
            .Where(p => RvtWriter.InspectCapabilities(p).Supports(RvtWriteOperation.PartitionRecordRewrite))
            .OrderBy(p => new FileInfo(p).Length)
            .Take(MaxTargetedRewriteFiles)
            .ToList();

        if (candidates.Count == 0)
            throw new SkipException("No supported sample file <= 32 MB with partition streams found.");

        int exercised = 0;
        foreach (string source in candidates)
        {
            int? targetPart = null;
            ulong targetOwner = 0;
            byte[]? targetBody = null;
            byte[]? globalPtPayload;

            using (var root = RootStorage.OpenRead(source))
            {
                var schema = FormatsSchema.TryRead(root)!;
                var tags = PartitionFraming.TagsOf(schema)!.Value;
                int idWidth = schema.Release >= 2024 ? 8 : 4;
                ulong terminator = idWidth == 8 ? ulong.MaxValue : uint.MaxValue;

                globalPtPayload = PagedGzipStream.Read(root, "Global", "PartitionTable");

                var partStorage = root.OpenStorage("Partitions");
                foreach (var entry in partStorage.EnumerateEntries().Where(e => e.Type == EntryType.Stream && int.TryParse(e.Name, out _)))
                {
                    int partNo = int.Parse(entry.Name, CultureInfo.InvariantCulture);
                    using var s = partStorage.OpenStream(entry.Name);
                    byte[] raw = new byte[s.Length];
                    s.ReadExactly(raw);
                    bool walked = PartitionFraming.Walk(
                        raw,
                        tags,
                        idWidth,
                        r =>
                        {
                            if (targetPart is null && r.Block == 0 && r.Channel == 102 && r.Owner != terminator && r.Body.Length >= 2)
                            {
                                targetPart = partNo;
                                targetOwner = r.Owner;
                                targetBody = r.Body.ToArray();
                            }
                        },
                        out _,
                        out _);
                    Assert.True(walked);
                    if (targetPart is not null) break;
                }
            }

            if (targetPart is null || targetBody is null)
                continue;

            string ext = Path.GetExtension(source);
            string rewritten = Fresh("rvtwriter-rewrite" + ext);
            string? stagedSource = null;
            try
            {
                var pagedEdits = globalPtPayload is not null
                    ? new[] { new RvtPagedStreamEdit("Global/PartitionTable", globalPtPayload) }
                    : Array.Empty<RvtPagedStreamEdit>();

                var request = new RvtWriteRequest
                {
                    PagedStreamEdits = pagedEdits,
                    PartitionRecordEdits =
                    [
                        new RvtPartitionRecordEdit(targetPart.Value, 102, targetOwner, targetBody),
                    ],
                };

                RvtWriteResult res;
                try
                {
                    res = RvtWriter.Rewrite(source, rewritten, request);
                }
                catch (RvtWriteRefusedException ex) when (ex.Kind == "relative-reference-relocation")
                {
                    Assert.False(File.Exists(rewritten));
                    stagedSource = Fresh("rvtwriter-staged-rewrite" + ext);
                    File.Copy(source, stagedSource);
                    res = RvtWriter.Rewrite(stagedSource, rewritten, request);
                }

                Assert.Equal(1, res.RewrittenRecordCount);

                var srcCensus = CoverageCensus.Read(stagedSource ?? source);
                var dstCensus = CoverageCensus.Read(rewritten);
                Assert.Equal(0, dstCensus.Totals.ChecksumMismatches);
                Assert.Equal(srcCensus.Totals.Items, dstCensus.Totals.Items);

                var srcDoc = RvtDocument.Open(stagedSource ?? source);
                var dstDoc = RvtDocument.Open(rewritten);
                Assert.Equal(srcDoc.Elements.Count, dstDoc.Elements.Count);
                Assert.Equal(
                    srcDoc.Elements.Select(e => (e.Id, e.UniqueId)),
                    dstDoc.Elements.Select(e => (e.Id, e.UniqueId)));
                exercised++;
            }
            finally
            {
                File.Delete(rewritten);
                if (stagedSource is not null) File.Delete(stagedSource);
            }
        }

        _output.WriteLine($"exercised={exercised} of {candidates.Count}");
        Assert.True(exercised > 0);
    }
}
