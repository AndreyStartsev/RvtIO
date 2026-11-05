using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Counts the elements of a file per class from its partition streams: every owner id with a record in channel 102 of the project's own
/// block (the null-key map) is one element, and the class is the class tag that opens the record body, resolved through the file's own
/// class table. An element that several partition streams hold is counted once, in the class of its last copy. Records of the keyed
/// blocks (loaded family documents) are not project elements and are not counted.
/// </summary>
internal static class ElementCensus
{
    /// <summary>Elements per class name, or null with the reason in <paramref name="failure"/> when the table, the id width or the
    /// framing of a partition stream does not decode.</summary>
    internal static Dictionary<string, int>? CountByClass(RootStorage root, SchemaDocument document, out string? failure)
    {
        failure = null;
        var schema = new FormatsSchema(document);
        if (PartitionFraming.TagsOf(schema) is not { } tags) { failure = "the class table has no framing marker classes"; return null; }
        var saved = SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion"));
        if (SchemaRelease.IdWidth(SchemaRelease.Effective(saved, SchemaRelease.FromSchema(schema))) is not int width) { failure = "the width of the element id is not known"; return null; }
        if (!root.TryOpenStorage("Partitions", out var storage)) { failure = "the file has no Partitions storage"; return null; }

        var names = storage.EnumerateEntries().Where(e => e.Type == EntryType.Stream).Select(e => e.Name)
            .Where(n => int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            .OrderBy(n => int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (names.Count == 0) { failure = "the file has no partition stream"; return null; }

        ulong terminator = width == 8 ? ulong.MaxValue : uint.MaxValue;
        var tagOfOwner = new Dictionary<ulong, ushort>();
        foreach (string name in names)
        {
            using var stream = storage.OpenStream(name);
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            bool bad = false;
            bool ok = PartitionFraming.Walk(raw, tags, width, record =>
            {
                if (record.Block != 0 || record.Channel != 102 || record.Owner == terminator) return;
                if (record.Body.Length < 2) { bad = true; return; }
                tagOfOwner[record.Owner] = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
            }, out string? walkFailure, out _);
            if (!ok) { failure = $"the framing of partition stream {name} does not hold ({walkFailure})"; return null; }
            if (bad) { failure = $"partition stream {name} holds a record without a class tag"; return null; }
        }

        var nameOfTag = document.ById;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ushort tag in tagOfOwner.Values)
        {
            string className = nameOfTag.TryGetValue(tag, out var c) ? c.Name : $"(class {tag})";
            counts[className] = counts.GetValueOrDefault(className) + 1;
        }
        return counts;
    }
}
