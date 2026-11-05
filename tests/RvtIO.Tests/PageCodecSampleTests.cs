using ICSharpCode.SharpZipLib.Zip.Compression;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>Runs the page codec over the paged streams of the Revit project files found in the private fixture folder
/// (<see cref="TestFiles"/>): every file directly in the folder or in one level of subfolders. Skipped when there is none. A stream
/// over 128 MB is left out to keep the memory use of the test low.</summary>
public class PageCodecSampleTests(ITestOutputHelper output)
{
    private static readonly string[] PagedStorages = ["Formats", "Global", "Partitions"];
    private const long MaxStreamBytes = 128L * 1024 * 1024;

    private sealed record PagedStream(string File, string Name, byte[] Raw);

    internal static List<string> ProjectFiles()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string root in TestFiles.Roots())
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.rvt")) files.Add(file);
            foreach (string folder in Directory.EnumerateDirectories(root))
                foreach (string file in Directory.EnumerateFiles(folder, "*.rvt")) files.Add(file);
        }
        if (files.Count == 0) throw TestFiles.Missing("*.rvt project files");
        return [.. files];
    }

    private static IEnumerable<PagedStream> PagedStreams(string path)
    {
        using var root = RootStorage.OpenRead(path);
        foreach (var storageEntry in root.EnumerateEntries().Where(e => e.Type == EntryType.Storage && PagedStorages.Contains(e.Name)))
        {
            var storage = root.OpenStorage(storageEntry.Name);
            foreach (var entry in storage.EnumerateEntries().Where(e => e.Type == EntryType.Stream && e.Length <= MaxStreamBytes))
            {
                using var stream = storage.OpenStream(entry.Name);
                var raw = new byte[stream.Length];
                stream.ReadExactly(raw);
                yield return new PagedStream(Path.GetFileName(path), $"{storageEntry.Name}/{entry.Name}", raw);
            }
        }
    }

    /// <summary>Where the single gzip member of a Formats or Global stream ends in the decoded body, or -1.</summary>
    private static int MemberEnd(byte[] body)
    {
        int start = body.Length > 18 && body[0] == 0x1F ? 0 : PagedGzipStream.PrefixLength;
        if (body.Length < start + 18 || body[start] != 0x1F || body[start + 1] != 0x8B || body[start + 2] != 0x08 || body[start + 3] != 0) return -1;
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(body, start + GzipMember.HeaderLength, body.Length - start - GzipMember.HeaderLength);
        var buffer = new byte[81_920];
        while (!inflater.IsFinished)
            if (inflater.Inflate(buffer) == 0 && (inflater.IsNeedingInput || inflater.IsNeedingDictionary)) return -1;
        return start + GzipMember.HeaderLength + (int)inflater.TotalIn + 8;
    }

    [SkippableFact]
    public void FullPages_OfRealStreams_AreReproducedByteForByte()
    {
        int files = 0, streams = 0, pages = 0, reproduced = 0, markedPages = 0, writtenForm = 0, otherForm = 0;
        foreach (string path in ProjectFiles())
        {
            files++;
            foreach (var stream in PagedStreams(path))
            {
                streams++;
                byte[] raw = stream.Raw;
                int fullPages = raw.Length / PagedGzipStream.PageStride;
                for (int page = 0; page < fullPages; page++)
                {
                    int at = page * PagedGzipStream.PageStride;
                    pages++;
                    byte[] expected = PageCodec.FullPageTrailer(raw.AsSpan(at, PagedGzipStream.PageDataLength));
                    if (expected.AsSpan().SequenceEqual(raw.AsSpan(at + PagedGzipStream.PageDataLength, PagedGzipStream.SegmentLength)))
                    {
                        reproduced++;
                        continue;
                    }

                    // The one kind of page that is not reproduced from its payload: the page that ends a stream exactly and has set bits
                    // in the 12 bits between its payload and its parity. The codec writes those bits as zero. The page is still a codeword
                    // of the same code (Verify takes the 12 bits as data), so only the premise of the writer differs, not the code.
                    string where = $"{stream.File} {stream.Name} page {page} of {fullPages}";
                    Assert.True(page == fullPages - 1 && raw.Length == fullPages * PagedGzipStream.PageStride, where + " is not the last page");
                    Assert.True(raw[at + PagedGzipStream.PageDataLength] != 0 || (raw[at + PagedGzipStream.PageDataLength + 1] & 0x0F) != 0,
                        where + " differs although its 12 bits after the payload are zero");
                    Assert.Equal(new PageCheck(PageCheckStatus.Valid, 1), PageCodec.Verify(raw.AsSpan(at, PagedGzipStream.PageStride)));
                    markedPages++;
                }

                int tailStart = fullPages * PagedGzipStream.PageStride;
                if (raw.Length == tailStart) continue;
                var tail = PageCodec.Verify(raw.AsSpan(tailStart));
                Assert.True(tail.Status is PageCheckStatus.Valid or PageCheckStatus.UnsupportedTail,
                    $"{stream.File} {stream.Name}: the last page has a length this codec writes, with other check bytes");
                if (tail.Status == PageCheckStatus.Valid) writtenForm++; else otherForm++;
            }
        }
        output.WriteLine($"files={files} streams={streams} full pages={pages} reproduced from the payload={reproduced} " +
            $"accepted with set bits after the payload={markedPages}; last pages in the written form={writtenForm}, in another form (not checked)={otherForm}");
        Assert.Equal(pages, reproduced + markedPages);
        Assert.True(reproduced > 0 && writtenForm > 0);
    }

    [SkippableFact]
    public void SingleMemberStreams_WithALastPageOfTheWrittenForm_KeepTheirLengthAndFullPages_WhenRewritten()
    {
        int rewritten = 0, notWritten = 0;
        foreach (string path in ProjectFiles())
        {
            foreach (var stream in PagedStreams(path).Where(s => !s.Name.StartsWith("Partitions/", StringComparison.Ordinal)))
            {
                byte[] raw = stream.Raw;
                int tailStart = raw.Length / PagedGzipStream.PageStride * PagedGzipStream.PageStride;
                if (raw.Length == tailStart || PageCodec.Verify(raw.AsSpan(tailStart)).Status != PageCheckStatus.Valid)
                {
                    notWritten++; // the last page is stored in a form this codec does not write
                    continue;
                }

                byte[]? body = PagedGzipStream.RemoveSegments(raw);
                int end = body is null ? -1 : MemberEnd(body);
                Assert.True(end > 0, $"{stream.File} {stream.Name}: no single gzip member");
                byte[] stored = PageCodec.EncodePadded(body.AsSpan(0, end));

                // The same pages and the same length; the last page differs in the bits that the original leaves at the end of its data
                // region (the codec leaves them zero) and in the parity that depends on them.
                Assert.Equal(raw.Length, stored.Length);
                Assert.True(raw.AsSpan(0, tailStart).SequenceEqual(stored.AsSpan(0, tailStart)), $"{stream.File} {stream.Name}: full pages differ");
                Assert.Equal(PageCheckStatus.Valid, PageCodec.Verify(stored).Status);
                Assert.Equal(PagedGzipStream.Decompress(raw), PagedGzipStream.Decompress(stored));
                rewritten++;
            }
        }
        output.WriteLine($"rewritten to the same length={rewritten}; last page in another form (not rewritten)={notWritten}");
        Assert.True(rewritten > 0);
    }
}
