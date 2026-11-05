using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>One change to a class of the embedded schema, addressed by the class name: a new version, a new GUID list, or both.
/// <see cref="Guids"/> holds 32-digit hex strings (either case); an empty list is allowed and writes a list of zero entries.</summary>
internal sealed record SchemaEdit(string ClassName, int? Version = null, IReadOnlyList<string>? Guids = null);

/// <summary>
/// Editor of the decoded Formats/Latest stream (the bytes inside the gzip member, see <see cref="PagedGzipStream"/>). It changes two
/// things of a class definition, nothing else: the version dword and the GUID list that closes the definition (u32 count, then 16 bytes
/// per entry). The positions come from the strict parser (<see cref="TfSchemaReader"/>, <see cref="SchemaClass.Spans"/>), never from a
/// byte search. A list of another length moves everything behind it, so the result is built by copying the bytes between the changed
/// spans; every other byte, including the zero padding at the end of the stream, is copied as it is.
///
/// The edit is refused, with an <see cref="ArgumentException"/> and no output, when a class name is not in the schema or names more than
/// one class, when a class is edited twice, when a version is negative or higher than the stored one (unless the caller allows
/// raising), when a GUID is not 32 hex digits, and when a list is edited in the older stream layout that has no lists. The result is
/// parsed again with the strict parser and compared with the input: the class count, the layout, and for every class the id, name,
/// base, fields and type references must be unchanged, and version and list must be the requested ones for an edited class and the
/// stored ones for every other class. A difference throws <see cref="InvalidDataException"/>.
/// </summary>
internal static class SchemaStreamEditor
{
    /// <summary>The stream with the edits applied. The input array is not changed. Without edits the result is a copy of the input.
    /// A version is never raised unless <paramref name="allowVersionRaise"/> is set: a stored version below the one a reader
    /// knows is the normal state of an older file, a higher one is not. Throws <see cref="FormatException"/> when the input does not
    /// parse with the strict grammar.</summary>
    internal static byte[] Apply(byte[] data, IReadOnlyList<SchemaEdit> edits, bool allowVersionRaise = false)
    {
        var before = TfSchemaReader.Parse(data);
        var patches = Plan(before, edits, allowVersionRaise);

        var result = new MemoryStream(data.Length + patches.Sum(p => p.Replacement.Length - p.Length));
        int at = 0;
        foreach (var patch in patches)
        {
            result.Write(data, at, patch.Offset - at);
            result.Write(patch.Replacement);
            at = patch.Offset + patch.Length;
        }
        result.Write(data, at, data.Length - at);
        byte[] edited = result.ToArray();

        Verify(before, TfSchemaReader.Parse(edited), edits);
        return edited;
    }

    private readonly record struct Patch(int Offset, int Length, byte[] Replacement);

    private static SchemaClass Resolve(SchemaDocument document, string name)
    {
        var found = document.Classes.Where(c => c.Name == name).Take(2).ToList();
        if (found.Count == 0) throw new ArgumentException($"The schema has no class named {name}.", nameof(name));
        if (found.Count > 1) throw new ArgumentException($"More than one class is named {name}.", nameof(name));
        return found[0];
    }

    private static byte[] ParseGuid(string text, string className)
    {
        if (text.Length != 32) throw new ArgumentException($"GUID '{text}' of {className} is not 32 hex digits.");
        try { return Convert.FromHexString(text); }
        catch (FormatException e) { throw new ArgumentException($"GUID '{text}' of {className} is not 32 hex digits.", e); }
    }

    /// <summary>The byte patches (ordered, not overlapping) for the edits; refuses the edits that cannot be applied.</summary>
    private static List<Patch> Plan(SchemaDocument document, IReadOnlyList<SchemaEdit> edits, bool allowVersionRaise)
    {
        var patches = new List<Patch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!seen.Add(edit.ClassName)) throw new ArgumentException($"Class {edit.ClassName} is edited twice.", nameof(edits));
            if (edit.Version is null && edit.Guids is null) throw new ArgumentException($"The edit of {edit.ClassName} changes nothing.", nameof(edits));
            var cls = Resolve(document, edit.ClassName);
            var spans = cls.Spans ?? throw new InvalidOperationException($"Class {cls.Name} has no recorded spans.");

            if (edit.Version is int version)
            {
                if (version < 0) throw new ArgumentException($"Version {version} of {cls.Name} is negative.", nameof(edits));
                if (version > cls.Version && !allowVersionRaise)
                    throw new ArgumentException($"Version {version} of {cls.Name} is higher than the stored {cls.Version}.", nameof(edits));
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)version);
                patches.Add(new Patch(spans.Version.Offset, spans.Version.Length, bytes));
            }

            if (edit.Guids is { } guids)
            {
                var list = spans.GuidList ?? throw new ArgumentException($"The stream layout has no GUID lists ({cls.Name}).", nameof(edits));
                var bytes = new byte[4 + 16 * guids.Count];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)guids.Count);
                for (int i = 0; i < guids.Count; i++) ParseGuid(guids[i], cls.Name).CopyTo(bytes, 4 + 16 * i);
                patches.Add(new Patch(list.Offset, list.Length, bytes));
            }
        }

        patches.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        for (int i = 1; i < patches.Count; i++)
            if (patches[i].Offset < patches[i - 1].Offset + patches[i - 1].Length)
                throw new InvalidOperationException("Two edits cover the same bytes.");
        return patches;
    }

    /// <summary>Compares the schema read back from the edited stream with the input schema: only the requested edits may differ.
    /// Throws <see cref="InvalidDataException"/> on the first difference.</summary>
    internal static void Verify(SchemaDocument before, SchemaDocument after, IReadOnlyList<SchemaEdit> edits)
    {
        var edited = edits.ToDictionary(e => Resolve(before, e.ClassName).Id);
        if (after.GuidLists != before.GuidLists) throw new InvalidDataException("The stream layout changed.");
        if (after.Classes.Count != before.Classes.Count)
            throw new InvalidDataException($"The class count changed from {before.Classes.Count} to {after.Classes.Count}.");

        for (int i = 0; i < before.Classes.Count; i++)
        {
            SchemaClass was = before.Classes[i], now = after.Classes[i];
            string where = $"class {was.Id} {was.Name}";
            if (now.Id != was.Id || now.Name != was.Name) throw new InvalidDataException($"{where}: id or name changed.");
            if (now.Base != was.Base) throw new InvalidDataException($"{where}: base changed.");
            if (!FieldsEqual(was.Fields, now.Fields)) throw new InvalidDataException($"{where}: fields changed.");

            edited.TryGetValue(was.Id, out var edit);
            int version = edit?.Version ?? was.Version;
            if (now.Version != version) throw new InvalidDataException($"{where}: version is {now.Version}, expected {version}.");
            var guids = edit?.Guids?.Select(g => g.ToLowerInvariant()).ToList() ?? was.Guids;
            if (!now.Guids.SequenceEqual(guids)) throw new InvalidDataException($"{where}: GUID list differs from the expected one.");
        }
    }

    private static bool FieldsEqual(List<SchemaField> a, List<SchemaField> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            SchemaField x = a[i], y = b[i];
            if (x.Name != y.Name || x.Type != y.Type || x.Mods != y.Mods || x.Extra != y.Extra || x.Fixed != y.Fixed || x.Ref != y.Ref ||
                x.Ref2 != y.Ref2 || (x.Args is null) != (y.Args is null) || (x.Args is not null && !FieldsEqual(x.Args, y.Args!)))
                return false;
        }
        return true;
    }
}
