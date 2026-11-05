using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>Runs the schema stream editor over the Formats/Latest stream of the Revit project files found in the private fixture folder
/// (<see cref="TestFiles"/>): every file directly in the folder or in one level of subfolders, one stream per distinct schema (the
/// streams of one release are identical). Skipped when there is no file.</summary>
public class SchemaStreamEditorSampleTests(ITestOutputHelper output)
{
    private sealed record Sample(string File, int? Release, byte[] Stream, SchemaDocument Document);

    private static List<Sample> Samples()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string root in TestFiles.Roots())
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.rvt")) files.Add(file);
            foreach (string folder in Directory.EnumerateDirectories(root))
                foreach (string file in Directory.EnumerateFiles(folder, "*.rvt")) files.Add(file);
        }
        if (files.Count == 0) throw TestFiles.Missing("*.rvt project files");

        var bySchema = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (string path in files)
        {
            using var root = RootStorage.OpenRead(path);
            byte[]? stream = PagedGzipStream.Read(root, "Formats", "Latest");
            Assert.True(stream is not null, $"{Path.GetFileName(path)}: Formats/Latest does not decode");
            var document = TfSchemaReader.Parse(stream);
            if (bySchema.ContainsKey(document.StreamSha256!)) continue;
            int? marker = document.Classes.FirstOrDefault(c => c.Name == "ContentMarker")?.Id;
            bySchema[document.StreamSha256!] = new Sample(Path.GetFileName(path), SchemaRelease.FromEvidence(marker, document.Classes.Count), stream, document);
        }
        return [.. bySchema.Values.OrderBy(s => s.Release).ThenBy(s => s.File, StringComparer.Ordinal)];
    }

    private static string Fresh(int n) => string.Concat(Enumerable.Range(0, 16).Select(i => ((n * 17 + i * 5 + 1) & 0xFF).ToString("x2")));

    [SkippableFact]
    public void EveryClassHasSpans_AndTheIdentityRewriteIsByteEqual()
    {
        foreach (var s in Samples())
        {
            int withVersion = s.Document.Classes.Count(c => c.Spans is not null);
            int withList = s.Document.Classes.Count(c => c.Spans?.GuidList is not null);
            output.WriteLine($"release {s.Release}: {s.Stream.Length} bytes, {s.Document.Classes.Count} classes, version spans {withVersion}, list spans {withList}");
            Assert.Equal(s.Document.Classes.Count, withVersion);
            Assert.Equal(s.Document.GuidLists ? s.Document.Classes.Count : 0, withList);

            // Spans lie inside the stream, hold the stored values, and do not overlap.
            var covered = new List<ByteSpan>();
            foreach (var c in s.Document.Classes)
            {
                var spans = c.Spans!.Value;
                Assert.Equal(c.Version, BitConverter.ToInt32(s.Stream, spans.Version.Offset));
                covered.Add(spans.Version);
                if (spans.GuidList is { } list)
                {
                    Assert.Equal(c.Guids.Count, BitConverter.ToInt32(s.Stream, list.Offset));
                    Assert.Equal(4 + 16 * c.Guids.Count, list.Length);
                    covered.Add(list);
                }
            }
            covered.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            Assert.True(covered[0].Offset >= 0 && covered[^1].Offset + covered[^1].Length <= s.Stream.Length);
            for (int i = 1; i < covered.Count; i++) Assert.True(covered[i].Offset >= covered[i - 1].Offset + covered[i - 1].Length);

            byte[] same = SchemaStreamEditor.Apply(s.Stream, []);
            Assert.Equal(s.Stream, same);
            Assert.NotSame(s.Stream, same);
        }
    }

    [SkippableFact]
    public void AVersionAndListEdit_RoundTrips_WithTheReparseCheck()
    {
        foreach (var s in Samples())
        {
            var unique = s.Document.Classes.GroupBy(c => c.Name).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
            var a = unique.FirstOrDefault(c => c.Version > 0 && c.Guids.Count > 0) ?? unique.First(c => c.Version > 0);
            var b = unique.First(c => c.Name != a.Name && c.Guids.Count == 0);
            var d = unique.First(c => c.Name != a.Name && c.Name != b.Name && c.Version > 0);

            var edits = new[]
            {
                new SchemaEdit(a.Name, Version: a.Version - 1, Guids: [.. a.Guids.Skip(1), Fresh(1), Fresh(2)]),  // lower, drop one, add two
                new SchemaEdit(b.Name, Guids: [Fresh(3)]),                                                          // from empty
                new SchemaEdit(d.Name, Version: 0),
            };
            byte[] edited = SchemaStreamEditor.Apply(s.Stream, edits);

            var after = TfSchemaReader.Parse(edited);
            Assert.Equal(s.Document.Classes.Count, after.Classes.Count);
            Assert.Equal(s.Document.FieldCount, after.FieldCount);
            Assert.Equal(a.Version - 1, after.Classes.Single(c => c.Id == a.Id).Version);
            Assert.Equal(0, after.Classes.Single(c => c.Id == d.Id).Version);
            Assert.Equal([Fresh(3)], after.Classes.Single(c => c.Id == b.Id).Guids);
            int newA = Math.Max(a.Guids.Count - 1, 0) + 2;
            Assert.Equal(s.Stream.Length + 16 * (newA - a.Guids.Count) + 16, edited.Length);

            // Back to the stored values: the original stream again.
            var back = new[]
            {
                new SchemaEdit(a.Name, Version: a.Version, Guids: a.Guids),
                new SchemaEdit(b.Name, Guids: []),
                new SchemaEdit(d.Name, Version: d.Version),
            };
            Assert.Equal(s.Stream, SchemaStreamEditor.Apply(edited, back, allowVersionRaise: true));

            // The edited stream is a valid payload for the writers of the page layer.
            byte[] stored = PageCodec.EncodePadded(GzipMember.Write(edited));
            Assert.Equal(PageCheckStatus.Valid, PageCodec.Verify(stored).Status);
            Assert.Equal(edited, PagedGzipStream.Decompress(stored)![..edited.Length]);

            output.WriteLine($"release {s.Release} ({s.Document.Classes.Count} classes): edited {a.Name} v{a.Version}->{a.Version - 1}, {b.Name}, {d.Name}; stream {s.Stream.Length} -> {edited.Length} bytes");
        }
    }
}
