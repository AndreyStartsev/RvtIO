using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO;

/// <summary>
/// Prints how the field walk of single records goes (<see cref="CoverageCensus"/> counts them): the objects the walk queued with the
/// position each one starts at, the field it stopped at, the fields of the class it stopped in, and the bytes around the stop.
/// A developer aid for finding the rule a stopped walk is missing; positions are offsets in the body of the record.
/// </summary>
internal static class CoverageTrace
{
    /// <summary>Which records to print: those of one class (by the class tag of the body), or those of the listed owners; of the first
    /// kind only records that are not exact, and of those only the ones whose reason contains <paramref name="Reason"/> when it is given.</summary>
    internal sealed record Selection(string? Class, IReadOnlySet<ulong>? Owners, string? Reason, int Channel, bool Family, int Max, int Bytes);

    internal static void Write(string path, Selection selection, TextWriter output)
    {
        RvtContainerCheck.Verify(path);
        using var root = RootStorage.OpenRead(path);
        var document = TfSchemaReader.Read(root) ?? throw new RvtInputException("truncated-or-damaged", "The class table (Formats/Latest) is missing or cannot be read.");
        var schema = new FormatsSchema(document);
        int? saved = SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion"));
        if (PartitionFraming.TagsOf(schema) is not { } tags || SchemaRelease.IdWidth(SchemaRelease.Effective(saved, SchemaRelease.FromSchema(schema))) is not int width
            || !root.TryOpenStorage("Partitions", out var storage))
            throw new RvtInputException("truncated-or-damaged", "The records of this file cannot be counted.");
        var walker = new WideBlockWalker(document, (_, _) => null, []);
        var entitySchemas = new CoverageCensus.BlockEntitySchemas(root, document, walker);
        var classes = document.ById;
        int printed = 0;
        foreach (var entry in storage.EnumerateEntries().Where(e => e.Type == EntryType.Stream).OrderBy(e => e.Name.Length).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            if (printed >= selection.Max) break;
            var raw = new byte[entry.Length];
            using (var stream = storage.OpenStream(entry.Name)) stream.ReadExactly(raw);
            PartitionFraming.Walk(raw, tags, width, record =>
            {
                if (printed >= selection.Max || record.Channel != selection.Channel || (record.Key.Length != 0) != selection.Family || record.Body.Length < 2) return;
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
                if (walker.Class(tag) is not { } rootClass) return;
                entitySchemas.Use(record.Key);
                if (selection.Owners != null ? !selection.Owners.Contains(record.Owner) : rootClass.Name != selection.Class) return;
                if (!MemoryMarshal.TryGetArray(record.Body, out var bytes)) bytes = record.Body.ToArray();
                walker.StepBudget = 16L * bytes.Count + 65536;
                var walk = walker.Run(bytes.Array!, bytes.Offset, bytes.Count, tag);
                string reason = walk.Exact ? "exact" : CoverageCensus.Reason(walk, bytes.Offset + bytes.Count);
                if (selection.Owners == null && (walk.Exact || (selection.Reason != null && !reason.Contains(selection.Reason, StringComparison.Ordinal)))) return;
                printed++;
                Print(output, entry.Name, record, rootClass, walk, reason, bytes, walker, classes, selection.Bytes);
            }, out _, out _);
        }
        if (printed == 0) output.WriteLine("No record matches.");
    }

    private static void Print(TextWriter output, string stream, PartitionFraming.Record record, SchemaClass rootClass, WideBlockWalker.Walk walk, string reason,
        ArraySegment<byte> body, WideBlockWalker walker, Dictionary<int, SchemaClass> classes, int window)
    {
        var inv = CultureInfo.InvariantCulture;
        int origin = body.Offset, length = body.Count, stop = walk.Position - origin;
        window = Math.Min(window, length);
        output.WriteLine($"=== Partitions/{stream} channel {record.Channel} owner {record.Owner.ToString(inv)} class {rootClass.Name} ({rootClass.Id}) body {length} bytes: {reason}");
        output.WriteLine($"walk position {stop}, {length - stop} bytes left; root fields {(walk.RootRead ? "read" : "not read")}; queue {walk.Queue.Count} objects, {walk.QueueStart.Count} started");
        for (int i = 0; i < walk.Queue.Count; i++)
        {
            string start = i < walk.QueueStart.Count ? (walk.QueueStart[i] - origin).ToString(inv) : "-";
            string size = i + 1 < walk.QueueStart.Count ? (walk.QueueStart[i + 1] - walk.QueueStart[i]).ToString(inv) : i < walk.QueueStart.Count ? "(" + (walk.Position - walk.QueueStart[i]).ToString(inv) + " read)" : "";
            output.WriteLine($"  queue {i}: {walker.Class(walk.Queue[i])?.Name} ({walk.Queue[i].ToString(inv)}) at {start} {size}");
        }
        int objectStart = walk.StopQueueIndex >= 0 && walk.StopQueueIndex < walk.QueueStart.Count ? walk.QueueStart[walk.StopQueueIndex] - origin
            : walk.Error != null ? 2 : walk.QueueStart.Count > 0 ? walk.QueueStart[^1] - origin : 2;
        int stopClass = walk.Error == null ? (walk.Queue.Count > 0 ? walk.Queue[^1] : rootClass.Id) : walk.StopQueueIndex >= 0 ? walk.Queue[walk.StopQueueIndex] : rootClass.Id;
        output.WriteLine($"fields of {walker.Class(stopClass)?.Name} (the object {(walk.Error == null ? "read last" : "the walk stopped in")}, it starts at {objectStart}):");
        var chain = new List<SchemaClass>();
        for (int id = stopClass, guard = 0; id != 0 && guard < 64 && classes.TryGetValue(id, out var c); id = c.Base, guard++) chain.Add(c);
        chain.Reverse();
        foreach (var c in chain)
            foreach (var f in c.Fields)
                output.WriteLine($"  {c.Name}.{f.Name}: type {f.Type} mods 0x{f.Mods:X2}{(f.Fixed is int n ? " fixed " + n : "")}{(f.Ref is int r ? " ref " + (classes.GetValueOrDefault(r)?.Name ?? r.ToString(inv)) : "")}"
                    + (f.Args is { Count: > 0 } args ? " args " + string.Join(",", args.Select(a => $"{a.Type}/0x{a.Mods:X2}{(a.Ref is int ar ? "/" + (classes.GetValueOrDefault(ar)?.Name ?? ar.ToString(inv)) : "")}")) : "")
                    + (f.Stored ? "" : " (not stored)"));
        Hex(output, "object", body, objectStart, Math.Min(length, Math.Max(stop, objectStart) + window), stop);
        if (length - stop > window) Hex(output, "end of the body", body, Math.Max(stop, length - window), length, -1);
        output.WriteLine();
    }

    private static void Hex(TextWriter output, string title, ArraySegment<byte> body, int from, int to, int mark)
    {
        output.WriteLine($"bytes of the {title}, {from}..{to}{(mark >= 0 ? ", '>' marks the walk position " + mark : "")}:");
        var line = new StringBuilder();
        for (int at = from; at < to; at += 16)
        {
            line.Clear().Append(at.ToString("D7", CultureInfo.InvariantCulture)).Append(' ');
            var text = new StringBuilder();
            for (int i = at; i < Math.Min(to, at + 16); i++)
            {
                byte b = body.Array![body.Offset + i];
                line.Append(i == mark ? '>' : ' ').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                text.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            output.WriteLine(line.Append("  ").Append(text));
        }
    }
}
