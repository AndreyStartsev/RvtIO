using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMcdf;

namespace RvtIO.Parsing;

/// <summary>One field of a Tf class (or one template argument of a field). <see cref="Ref"/> is the class id of a
/// type reference (only a type-14 field whose low modifier nibble is zero has one); inside template arguments the
/// stream writes that reference twice, the second copy is <see cref="Ref2"/>.</summary>
internal sealed record SchemaField(string Name, byte Type, byte Mods, ushort Extra, int? Fixed, int? Ref, int? Ref2,
    List<SchemaField>? Args)
{
    internal bool Stored => (Extra & 2) == 0;
}

/// <summary>A run of bytes of the decoded Formats/Latest stream.</summary>
internal readonly record struct ByteSpan(int Offset, int Length);

/// <summary>Where a class stores its version and its GUID list in the decoded stream. <see cref="Version"/> is the 4-byte version
/// dword. <see cref="GuidList"/> is the u32 count followed by the 16-byte values (4 + 16 * count bytes); it is null in the older
/// layout, which has no list.</summary>
internal readonly record struct ClassSpans(ByteSpan Version, ByteSpan? GuidList);

/// <summary>One class of the embedded schema. <see cref="Guids"/> is the trailing GUID list of the definition
/// (lower-case hex, 16 bytes each); the list does not exist in the older stream layout. <see cref="Spans"/> is set by the stream
/// parser only (null for a class read from JSON or from the shipped table).</summary>
internal sealed record SchemaClass(int Id, string Name, int Base, int Version, List<SchemaField> Fields, List<string> Guids,
    ClassSpans? Spans = null);

internal sealed class SchemaDocument
{
    public string Source = "";
    public int StreamBytes;
    public string? StreamSha256;
    /// <summary>True when every definition ends with a u32 GUID count (Revit 2020 and later); false for the older layout.</summary>
    public bool GuidLists = true;
    public List<SchemaClass> Classes = [];
    public int FieldCount => Classes.Sum(c => c.Fields.Count);
    public Dictionary<int, SchemaClass> ById => Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
}

/// <summary>
/// Reader of the class schema every Revit file embeds in the stream Formats/Latest (decoded by
/// <see cref="PagedGzipStream"/>). The grammar was established on Revit 2022 and holds,
/// without change except for the optional GUID lists, for the 2020, 2025 and 2027 files:
///   stream  := { 0x0000, name16, body }  zero padding            (implicit class id = counter++, counter starts at 12)
///   body    := typeref(base) u32(version) u32(fieldCount) field* [u32(n) n*GUID16]
///   typeref := u16 v ; v and 0x8000: new class id v and 0x7FFF (must equal the counter), 0x0000, name16, body
///              else a back reference to an already registered id (0 = none)
///   field   := u32 nameLen name core
///   core    := u8 type, u8 mods, u16 extra, [mods>>4 == 1: u32 fixedCount],
///              [type 14 and mods and 0xF == 0: typeref (twice inside template arguments)],
///              [type 13: u32 argCount, argCount * (0x20, core)]
/// The parse is strict (every id, back reference, length and type code is checked), so a wrong layout fails instead
/// of yielding wrong data; the GUID-list variant is tried first, the older layout second.
/// This is the ONE parser of the embedded schema: <see cref="FileSchema"/> and <see cref="FormatsSchema"/> are query views
/// over its <see cref="SchemaDocument"/>.
/// </summary>
internal static class TfSchemaReader
{
    private sealed class Failure(string message) : Exception(message);

    /// <summary>Parses a decoded Formats/Latest stream. Throws <see cref="FormatException"/> when neither layout fits.</summary>
    internal static SchemaDocument Parse(byte[] data) => Parse(data, [true, false]);

    private static SchemaDocument Parse(byte[] data, bool[] layouts)
    {
        string errors = "";
        foreach (bool guidLists in layouts)
        {
            try
            {
                var classes = new Reader(data, guidLists).Run();
                return new SchemaDocument
                {
                    StreamBytes = data.Length, GuidLists = guidLists, Classes = classes,
                    StreamSha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                };
            }
            catch (Failure failure) { errors += $"[guid lists {(guidLists ? "yes" : "no")}] {failure.Message}; "; }
        }
        throw new FormatException("Formats/Latest does not parse: " + errors.TrimEnd());
    }

    /// <summary>Parses a decoded Formats/Latest stream of Revit 2014 or later (GUID lists only: a stream cut inside a list would
    /// otherwise also fit the older layout); null when it does not follow the grammar or defines no class.</summary>
    internal static SchemaDocument? TryParse(byte[] data)
    {
        try { return Parse(data, [true]) is { Classes.Count: > 0 } document ? document : null; }
        catch (FormatException) { return null; }
    }

    /// <summary>The schema of an open file, or null when Formats/Latest is missing or does not parse.</summary>
    internal static SchemaDocument? Read(RootStorage root)
        => PagedGzipStream.Read(root, "Formats", "Latest") is { } stream ? TryParse(stream) : null;

    /// <summary>The schema table of Revit 2022 shipped with the library (resource r22_schemas.json) as a <see cref="SchemaDocument"/>.
    /// It equals the exact parse of the 2022 stream, except that the <see cref="SchemaField.Ref"/> of this table is the LEGACY
    /// value the 2022 decoders were written against and differs from the stream's type reference on some fields, so the 2022
    /// path keeps this table.</summary>
    internal static SchemaDocument ReadShipped2022(Stream json)
    {
        using var j = JsonDocument.Parse(json);
        var doc = new SchemaDocument { Source = "r22_schemas.json", GuidLists = false };
        foreach (var prop in j.RootElement.EnumerateObject())
        {
            var o = prop.Value;
            int baseId = o.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            var fields = new List<SchemaField>();
            foreach (var f in o.GetProperty("fields").EnumerateArray())
            {
                byte t = f.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.Number ? tv.GetByte() : (byte)0;
                byte m = f.TryGetProperty("m", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetByte() : (byte)0;
                int? rf = f.TryGetProperty("ref", out var rv) && rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : null;
                int? cn = f.TryGetProperty("cnt", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : null;
                fields.Add(new SchemaField(f.GetProperty("n").GetString() ?? "", t, m, 0, cn, rf, null, null));
            }
            int version = o.TryGetProperty("ver", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetInt32() : 0;
            doc.Classes.Add(new SchemaClass(int.Parse(prop.Name), o.GetProperty("name").GetString() ?? "", baseId, version, fields, []));
        }
        return doc;
    }

    /// <summary>Reads the schema of a .rvt/.rfa/.rte file, or of a neutral JSON dump written by <see cref="WriteJson"/>.</summary>
    internal static SchemaDocument Load(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var fromJson = ReadJson(File.ReadAllText(path, Encoding.UTF8));
            fromJson.Source = Path.GetFileName(path);
            return fromJson;
        }
        using var root = RootStorage.OpenRead(path);
        byte[] stream = PagedGzipStream.Read(root, "Formats", "Latest")
            ?? throw new FormatException($"{path}: Formats/Latest is missing or does not inflate to its CRC-32.");
        var document = Parse(stream);
        document.Source = Path.GetFileName(path);
        return document;
    }

    private sealed class Reader(byte[] d, bool guidLists)
    {
        private int o;
        private int counter = 12;
        private readonly List<SchemaClass> order = [];

        private byte U8() => o < d.Length ? d[o++] : throw new Failure($"end of stream at {o:X}");
        private ushort U16() { if (o + 2 > d.Length) throw new Failure($"end of stream at {o:X}"); o += 2; return BitConverter.ToUInt16(d, o - 2); }
        private uint U32() { if (o + 4 > d.Length) throw new Failure($"end of stream at {o:X}"); o += 4; return BitConverter.ToUInt32(d, o - 4); }

        private string Name(bool wide)
        {
            uint n = wide ? U32() : U16();
            if (n is 0 or > 200 || o + n > d.Length) throw new Failure($"name length {n} at {o:X}");
            for (int i = 0; i < n; i++)
                if (d[o + i] is < 0x20 or > 0x7E) throw new Failure($"non-ascii name at {o:X}");
            string s = Encoding.ASCII.GetString(d, o, (int)n);
            o += (int)n;
            return s;
        }

        private int TypeRef()
        {
            int start = o;
            ushort v = U16();
            if ((v & 0x8000) == 0)
                return v < counter ? v : throw new Failure($"back reference {v} to an unregistered id at {start:X}");
            int id = v & 0x7FFF;
            if (id != counter) throw new Failure($"inline id {id} differs from counter {counter} at {start:X}");
            counter++;
            if (U16() != 0) throw new Failure($"inline definition marker at {o - 2:X}");
            Body(id, Name(false));
            return id;
        }

        private void Body(int id, string name)
        {
            // Registered before its body so a nested definition keeps stream (= id) order of appearance.
            int slot = order.Count;
            order.Add(null!);
            int baseId = TypeRef();
            int versionAt = o;
            int version = (int)U32();
            uint count = U32();
            if (count > 400) throw new Failure($"field count {count} of {name} at {o - 4:X}");
            var fields = new List<SchemaField>((int)count);
            for (uint i = 0; i < count; i++)
            {
                string fieldName = Name(true);
                fields.Add(Core(fieldName, false));
            }
            var guids = new List<string>();
            ByteSpan? guidSpan = null;
            if (guidLists)
            {
                int listAt = o;
                uint n = U32();
                if ((long)n * 16 > d.Length - o) throw new Failure($"GUID count {n} of {name} at {o - 4:X}");
                for (uint i = 0; i < n; i++, o += 16) guids.Add(Convert.ToHexString(d, o, 16).ToLowerInvariant());
                guidSpan = new ByteSpan(listAt, o - listAt);
            }
            order[slot] = new SchemaClass(id, name, baseId, version, fields, guids, new ClassSpans(new ByteSpan(versionAt, 4), guidSpan));
        }

        private SchemaField Core(string name, bool inTemplate)
        {
            int start = o;
            byte type = U8(), mods = U8();
            ushort extra = U16();
            int? fixedCount = null, reference = null, reference2 = null;
            List<SchemaField>? args = null;
            if (mods >> 4 == 1)
            {
                uint n = U32();
                if (n is 0 or > 100000) throw new Failure($"fixed count {n} at {o - 4:X}");
                fixedCount = (int)n;
            }
            if (type == 0x0E)
            {
                if ((mods & 0x0F) == 0)
                {
                    reference = TypeRef();
                    if (inTemplate) reference2 = TypeRef();
                }
            }
            else if (type == 0x0D)
            {
                uint n = U32();
                if (n > 8) throw new Failure($"template argument count {n} at {o - 4:X}");
                args = [];
                for (uint i = 0; i < n; i++)
                {
                    if (U8() != 0x20) throw new Failure($"template argument tag at {o - 1:X}");
                    args.Add(Core("", true));
                }
            }
            else if (type is 0 or > 0x0F)
                throw new Failure($"unknown type code {type:X} at {start:X}");
            return new SchemaField(name, type, mods, extra, fixedCount, reference, reference2, args);
        }

        public List<SchemaClass> Run()
        {
            while (o < d.Length)
            {
                if (d.AsSpan(o).IndexOfAnyExcept((byte)0) < 0) break;
                if (U16() != 0) throw new Failure($"definition marker at {o - 2:X}");
                string name = Name(false);
                Body(counter++, name);
            }
            return [.. order.OrderBy(c => c.Id)];
        }
    }

    // Neutral JSON: {"format":"tf-schema/1", ..., "classes":[ one class per line ]}

    internal const string JsonFormat = "tf-schema/1";

    private static void Quote(StringBuilder b, string s) => b.Append(JsonSerializer.Serialize(s));

    private static void WriteField(StringBuilder b, SchemaField f)
    {
        b.Append('{');
        if (f.Name.Length > 0) { b.Append("\"name\":"); Quote(b, f.Name); b.Append(','); }
        b.Append(CultureInfo.InvariantCulture, $"\"type\":{f.Type},\"mods\":{f.Mods}");
        if (f.Extra != 0) b.Append(CultureInfo.InvariantCulture, $",\"extra\":{f.Extra}");
        if (f.Fixed is { } c) b.Append(CultureInfo.InvariantCulture, $",\"fixed\":{c}");
        if (f.Ref is { } r) b.Append(CultureInfo.InvariantCulture, $",\"ref\":{r}");
        if (f.Ref2 is { } r2) b.Append(CultureInfo.InvariantCulture, $",\"ref2\":{r2}");
        if (f.Args is { } args)
        {
            b.Append(",\"args\":[");
            for (int i = 0; i < args.Count; i++) { if (i > 0) b.Append(','); WriteField(b, args[i]); }
            b.Append(']');
        }
        b.Append('}');
    }

    internal static string WriteJson(SchemaDocument doc)
    {
        var b = new StringBuilder();
        b.Append("{\"format\":\"").Append(JsonFormat).Append("\",\"source\":"); Quote(b, doc.Source);
        b.Append(CultureInfo.InvariantCulture, $",\"streamBytes\":{doc.StreamBytes},\"streamSha256\":");
        Quote(b, doc.StreamSha256 ?? "");
        b.Append(CultureInfo.InvariantCulture,
            $",\"guidLists\":{(doc.GuidLists ? "true" : "false")},\"classCount\":{doc.Classes.Count},\"fieldCount\":{doc.FieldCount},\"classes\":[\n");
        for (int i = 0; i < doc.Classes.Count; i++)
        {
            var c = doc.Classes[i];
            b.Append(CultureInfo.InvariantCulture, $"{{\"id\":{c.Id},\"name\":"); Quote(b, c.Name);
            b.Append(CultureInfo.InvariantCulture, $",\"base\":{c.Base},\"version\":{c.Version},\"fields\":[");
            for (int k = 0; k < c.Fields.Count; k++) { if (k > 0) b.Append(','); WriteField(b, c.Fields[k]); }
            b.Append(']');
            if (c.Guids.Count > 0) b.Append(",\"guids\":[").Append(string.Join(',', c.Guids.Select(g => $"\"{g}\""))).Append(']');
            b.Append('}').Append(i + 1 < doc.Classes.Count ? ",\n" : "\n");
        }
        return b.Append("]}\n").ToString();
    }

    private static SchemaField ReadField(JsonElement e) => new(
        e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        e.GetProperty("type").GetByte(), e.GetProperty("mods").GetByte(),
        e.TryGetProperty("extra", out var x) ? x.GetUInt16() : (ushort)0,
        e.TryGetProperty("fixed", out var c) ? c.GetInt32() : null,
        e.TryGetProperty("ref", out var r) ? r.GetInt32() : null,
        e.TryGetProperty("ref2", out var r2) ? r2.GetInt32() : null,
        e.TryGetProperty("args", out var a) ? [.. a.EnumerateArray().Select(ReadField)] : null);

    internal static SchemaDocument ReadJson(string json)
    {
        using var j = JsonDocument.Parse(json);
        var root = j.RootElement;
        if (root.GetProperty("format").GetString() != JsonFormat)
            throw new FormatException($"not a {JsonFormat} document");
        var doc = new SchemaDocument
        {
            Source = root.GetProperty("source").GetString() ?? "",
            StreamBytes = root.GetProperty("streamBytes").GetInt32(),
            StreamSha256 = root.GetProperty("streamSha256").GetString(),
            GuidLists = root.GetProperty("guidLists").GetBoolean(),
        };
        foreach (var c in root.GetProperty("classes").EnumerateArray())
            doc.Classes.Add(new SchemaClass(c.GetProperty("id").GetInt32(), c.GetProperty("name").GetString() ?? "",
                c.GetProperty("base").GetInt32(), c.GetProperty("version").GetInt32(),
                [.. c.GetProperty("fields").EnumerateArray().Select(ReadField)],
                c.TryGetProperty("guids", out var g) ? [.. g.EnumerateArray().Select(v => v.GetString() ?? "")] : []));
        return doc;
    }
}
