using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using ICSharpCode.SharpZipLib.Checksum;
using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO;

/// <summary>
/// Counts, for one file, how many records of its partition streams the file's own class schema walks to their last byte
/// (<see cref="WideBlockWalker"/>), and lists every stream of the compound file. A record is one item of channel 101, 102 or 103 of a
/// content block (<see cref="PartitionFraming"/>); its body opens with the class tag the walk starts from. The values of an extensible
/// storage entity are read by the entity schemas of the file (<see cref="EntitySchemaReader"/>). The count is strict: no byte
/// scan, no resynchronisation, no recovery. A partition stream whose framing does not hold adds no record; the reason stands in its row.
/// </summary>
internal static class CoverageCensus
{
    /// <summary>One stream of the compound file. <see cref="Kind"/> is <c>partition</c>, <c>paged</c> or <c>plain</c>.
    /// <see cref="Framing"/> (partition) is <c>ok</c> or the reason the framing does not hold, and null when the records
    /// of the file cannot be counted; the counts behind it are set only when it is <c>ok</c>.</summary>
    internal sealed record StreamRow(string Path, string Kind, long RawBytes, string? Framing = null, bool? Decoded = null, int? Blocks = null,
        int? FamilyBlocks = null, int? Segments = null, long? InflatedBytes = null, long? Records = null);

    /// <summary>The counts of a set of records. <see cref="Terminators"/> are the chain terminators (owner all ones, no body) of a group:
    /// they carry no payload and are not records of a class.</summary>
    internal class Sums
    {
        public long Items, Exact, PayloadBytes, ExactBytes, ChecksumMismatches, Terminators;

        internal void Add(Sums other)
        {
            Items += other.Items;
            Exact += other.Exact;
            PayloadBytes += other.PayloadBytes;
            ExactBytes += other.ExactBytes;
            ChecksumMismatches += other.ChecksumMismatches;
            Terminators += other.Terminators;
        }
    }

    /// <summary>Why records are not exact: the reason, how many, and the lowest owner ids (at most three).</summary>
    internal sealed record Stop(string Reason, long Count, IReadOnlyList<ulong> Owners);

    /// <summary>The records of one class on one channel of the project block (<paramref name="Family"/> false) or of the family blocks.</summary>
    internal sealed record ClassRow(bool Family, int Channel, string Class, Sums Sums, IReadOnlyList<Stop> Stops);

    internal sealed record GroupRow(bool Family, int Channel, Sums Sums);

    /// <param name="RecordsFailure">Why the records cannot be counted at all; null when they are counted.</param>
    internal sealed record Result(int? SavedRelease, int? SchemaRelease, int? IdWidth, int ClassCount, IReadOnlyList<StreamRow> Streams,
        string? RecordsFailure, Sums Totals, IReadOnlyList<GroupRow> Groups, IReadOnlyList<ClassRow> Classes);

    private sealed class Tally : Sums
    {
        internal readonly Dictionary<string, (long Count, SortedSet<ulong> Owners)> Stops = new(StringComparer.Ordinal);

        internal void AddStop(string reason, long count, IEnumerable<ulong> owners)
        {
            if (!Stops.TryGetValue(reason, out var stop)) stop = (0, new SortedSet<ulong>());
            foreach (ulong owner in owners)
                if (stop.Owners.Add(owner) && stop.Owners.Count > 3) stop.Owners.Remove(stop.Owners.Max);
            Stops[reason] = (stop.Count + count, stop.Owners);
        }
    }

    /// <summary>Opens the file and counts it.</summary>
    /// <exception cref="RvtInputException">The file is missing, not a compound file, cut or damaged, or its Formats/Latest cannot be read.</exception>
    /// <exception cref="RvtLimitException">A stream is longer than the platform allows in one array.</exception>
    internal static Result Read(string path)
    {
        RvtContainerCheck.Verify(path);
        using var root = RootStorage.OpenRead(path);
        var document = TfSchemaReader.Read(root)
            ?? throw new RvtInputException("truncated-or-damaged", $"The class table (Formats/Latest) of '{Path.GetFileName(path)}' is missing or cannot be read.");
        return Run(root, document, SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion")));
    }

    internal static Result Run(RootStorage root, SchemaDocument document, int? savedRelease)
    {
        var schema = new FormatsSchema(document);
        int? schemaRelease = SchemaRelease.FromSchema(schema);
        int? width = SchemaRelease.IdWidth(SchemaRelease.Effective(savedRelease, schemaRelease));
        var tags = PartitionFraming.TagsOf(schema);
        string? failure = tags == null ? "the class table has no framing marker classes" : width == null ? "the width of the element id is not known" : null;

        var entries = new List<(string Path, Storage Storage, string Name, long Length)>();
        Collect(root, "", entries);
        var streams = new List<StreamRow>();
        var total = new Dictionary<(bool Family, int Channel, string Class), Tally>();
        var walker = failure == null ? new WideBlockWalker(document, (_, _) => null, []) : null;
        // the values of an extensible storage entity are read by the entity schemas of the file; when those cannot be read the set
        // is empty and an entity with values stops its record: the count never passes one by the bytes around it
        walker?.UseEntitySchemas(EntitySchemaReader.Read(root, document));
        var adler = new Adler32();
        ulong terminator = width == 8 ? ulong.MaxValue : uint.MaxValue;
        int partitions = 0;
        foreach (var (path, storage, name, length) in entries.OrderBy(e => SortKey(e.Path), StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal))
        {
            bool partition = PartitionNumber(path) != null;
            if (partition) partitions++;
            if (!partition && !path.StartsWith("Formats/", StringComparison.Ordinal) && !path.StartsWith("Global/", StringComparison.Ordinal))
            {
                streams.Add(new(path, "plain", length));
                continue;
            }
            string kind = partition ? "partition" : "paged";
            if (partition && failure != null)
            {
                streams.Add(new(path, kind, length));
                continue;
            }
            var raw = new byte[length];
            using (var stream = storage.OpenStream(name)) stream.ReadExactly(raw);
            if (!partition)
            {
                byte[]? decoded = PagedGzipStream.Decompress(raw);
                streams.Add(new(path, kind, length, Decoded: decoded != null, InflatedBytes: decoded?.Length));
                continue;
            }

            // the callback sees the records in front of a framing failure: they are merged only when the whole stream frames
            var local = new Dictionary<(bool Family, int Channel, string Class), Tally>();
            var segments = new List<int>();
            long records = 0;
            bool framed = PartitionFraming.Walk(raw, tags!.Value, width!.Value, record =>
            {
                if (Count(record, walker!, terminator, adler, local)) records++;
            }, out string? reason, out int blocks, segments);
            if (!framed)
            {
                streams.Add(new(path, kind, length, Framing: reason));
                continue;
            }
            streams.Add(new(path, kind, length, "ok", null, blocks, blocks - 1, segments.Count, segments.Sum(s => (long)s), records));
            foreach (var (key, tally) in local)
            {
                if (!total.TryGetValue(key, out var sum)) total[key] = sum = new Tally();
                sum.Add(tally);
                foreach (var (stop, (count, owners)) in tally.Stops) sum.AddStop(stop, count, owners);
            }
        }
        if (failure == null && partitions == 0)
            failure = root.TryOpenStorage("Partitions", out _) ? "the file has no partition stream" : "the file has no Partitions storage";

        var classes = total.Where(pair => pair.Value.Items > 0)
            .Select(pair => new ClassRow(pair.Key.Family, pair.Key.Channel, pair.Key.Class, pair.Value,
                [.. pair.Value.Stops.OrderByDescending(s => s.Value.Count).ThenBy(s => s.Key, StringComparer.Ordinal)
                    .Select(s => new Stop(s.Key, s.Value.Count, [.. s.Value.Owners]))]))
            .OrderBy(r => r.Family).ThenBy(r => r.Channel).ThenByDescending(r => r.Sums.Items - r.Sums.Exact).ThenBy(r => r.Class, StringComparer.Ordinal)
            .ToList();
        var groups = total.GroupBy(pair => (pair.Key.Family, pair.Key.Channel)).OrderBy(g => g.Key.Family).ThenBy(g => g.Key.Channel)
            .Select(g => new GroupRow(g.Key.Family, g.Key.Channel, Sum(g.Select(pair => pair.Value)))).ToList();
        return new Result(savedRelease, schemaRelease, width, document.Classes.Count, streams, failure, Sum(groups.Select(g => g.Sums)), groups, classes);
    }

    private static Sums Sum(IEnumerable<Sums> parts)
    {
        var sum = new Sums();
        foreach (var part in parts) sum.Add(part);
        return sum;
    }

    private static void Collect(Storage storage, string prefix, List<(string Path, Storage Storage, string Name, long Length)> entries)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Type == EntryType.Storage) Collect(storage.OpenStorage(entry.Name), prefix + entry.Name + "/", entries);
            else if (entry.Type == EntryType.Stream) entries.Add((prefix + entry.Name, storage, entry.Name, entry.Length));
        }
    }

    private static int? PartitionNumber(string path) =>
        path.StartsWith("Partitions/", StringComparison.Ordinal)
        && int.TryParse(path.AsSpan("Partitions/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;

    // the partition streams sort by number, every other stream by its path
    private static string SortKey(string path) =>
        PartitionNumber(path) is int number ? "Partitions/" + number.ToString("D10", CultureInfo.InvariantCulture) : path;

    /// <summary>Counts one record; false for the terminator of a chain, which is counted apart under the empty class name.</summary>
    private static bool Count(PartitionFraming.Record record, WideBlockWalker walker, ulong terminator, Adler32 adler,
        Dictionary<(bool Family, int Channel, string Class), Tally> tallies)
    {
        var body = record.Body;
        if (record.Owner == terminator && body.Length == 0)
        {
            var group = (record.Key.Length != 0, record.Channel, "");
            if (!tallies.TryGetValue(group, out var ends)) tallies[group] = ends = new Tally();
            ends.Terminators++;
            return false;
        }
        int tag = body.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body.Span) : -1;
        string name = tag < 0 ? "(no class tag)" : walker.Class(tag)?.Name ?? "(class " + tag.ToString(CultureInfo.InvariantCulture) + ")";
        var key = (record.Key.Length != 0, record.Channel, name);
        if (!tallies.TryGetValue(key, out var tally)) tallies[key] = tally = new Tally();
        tally.Items++;
        tally.PayloadBytes += body.Length;
        if (!MemoryMarshal.TryGetArray(body, out var bytes)) bytes = body.ToArray();
        if (record.Channel != 101)
        {
            adler.Reset();
            adler.Update(bytes);
            if ((uint)adler.Value != record.Extra) tally.ChecksumMismatches++;
        }

        string? reason = tag < 0 ? "no class tag" : "class " + tag.ToString(CultureInfo.InvariantCulture) + " has no schema";
        if (tag >= 0 && walker.Class(tag) != null)
        {
            walker.StepBudget = 16L * body.Length + 65536;
            var walk = walker.Run(bytes.Array!, bytes.Offset, bytes.Count, tag);
            reason = walk.Exact ? null : Reason(walk, bytes.Offset + bytes.Count);
        }
        if (reason != null) tally.AddStop(reason, 1, [record.Owner]);
        else
        {
            tally.Exact++;
            tally.ExactBytes += body.Length;
        }
        return true;
    }

    internal static string Reason(WideBlockWalker.Walk walk, int end)
    {
        if (walk.Error == null)
            return end - walk.Position <= 64 ? "short by " + (end - walk.Position).ToString(CultureInfo.InvariantCulture) + " bytes" : "short by more than 64 bytes";
        string reason = walk.Error.StartsWith("count ", StringComparison.Ordinal) ? "count" : walk.Error;
        if (walk.StopField != null) reason += " at " + walk.StopField;
        if (walk.StopQueueIndex >= 0) reason += ", in " + walk.StopClass;
        return reason;
    }
}
