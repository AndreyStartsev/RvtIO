using System.Globalization;
using System.Text;

namespace RvtIO.Parsing;

/// <summary>A finding of <see cref="TfSchemaTools.Validate"/>. Severity: "error" (the schema is inconsistent),
/// "unknown" (valid for the grammar but outside what the decoder implements), "info".</summary>
internal sealed record SchemaIssue(string Severity, string Code, string Class, string Field, string Message);

/// <summary>One difference between two schemas; classes are matched by NAME because ids are assigned by order of
/// appearance in the stream and move between versions.</summary>
internal sealed record SchemaChange(string Kind, string Class, string Field, string From, string To);

internal static class TfSchemaTools
{
    /// <summary>Type codes ReadScalar of <see cref="TfBlockDeserializer"/> has a case for (1-10, 13 template, 14 class).</summary>
    internal static readonly HashSet<int> DecoderTypeCodes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14];

    /// <summary>Modifier high nibbles of the grammar: 0 scalar, 1 fixed array, 5 dynamic container, 6 string storage.</summary>
    private static readonly HashSet<int> KnownModsHigh = [0, 1, 5, 6];


    /// <summary>Self-consistency of one schema plus everything the decoder cannot handle. <paramref name="knownPairs"/>
    /// are the (type, mods) pairs of top-level fields the decoder was built against (the Revit 2022 table).</summary>
    internal static List<SchemaIssue> Validate(SchemaDocument doc, ISet<(byte, byte)>? knownPairs = null)
    {
        var issues = new List<SchemaIssue>();
        void Add(string sev, string code, string cls, string field, string msg) => issues.Add(new(sev, code, cls, field, msg));
        var ids = new Dictionary<int, SchemaClass>();
        var names = new Dictionary<string, int>();
        int expected = 12;
        foreach (var c in doc.Classes)
        {
            if (!ids.TryAdd(c.Id, c)) Add("error", "duplicate-id", c.Name, "", $"id {c.Id} is used twice");
            if (c.Id != expected) Add("error", "id-sequence", c.Name, "", $"id {c.Id}, the registration counter is {expected}");
            expected = c.Id + 1;
            if (names.TryGetValue(c.Name, out int other)) Add("error", "duplicate-name", c.Name, "", $"ids {other} and {c.Id}");
            else names[c.Name] = c.Id;
        }
        var unknownPairs = new Dictionary<(byte, byte), int>();
        foreach (var c in ids.Values)
        {
            if (c.Base != 0 && !ids.ContainsKey(c.Base)) Add("error", "base-unresolved", c.Name, "", $"base id {c.Base}");
            var seen = new HashSet<int> { c.Id };
            int depth = 0;
            for (int b = c.Base; b != 0 && ids.TryGetValue(b, out var bc); b = bc.Base, depth++)
                if (!seen.Add(b)) { Add("error", "base-cycle", c.Name, "", "the base chain returns to a class it already passed"); break; }
            if (depth > 32) Add("unknown", "base-depth", c.Name, "", $"base chain of {depth} classes; the decoder follows at most 32");
            var fieldNames = new HashSet<string>();
            foreach (var f in c.Fields)
            {
                if (!fieldNames.Add(f.Name)) Add("error", "duplicate-field", c.Name, f.Name, "field name repeats inside the class");
                CheckField(f, c, f.Name, true);
            }
        }
        foreach (var ((t, m), n) in unknownPairs.OrderBy(p => p.Key))
            Add("unknown", "type-mods-pair", "", "", $"(type {t}, mods 0x{m:X2}) occurs on {n} top-level fields and in none of the decoder's reference table");
        return issues;

        void CheckField(SchemaField f, SchemaClass c, string path, bool top)
        {
            if (!DecoderTypeCodes.Contains(f.Type))
                Add("unknown", "type-code", c.Name, path, $"type code {f.Type} has no case in the decoder");
            if (!KnownModsHigh.Contains(f.Mods >> 4))
                Add("unknown", "mods-high-nibble", c.Name, path, $"modifier 0x{f.Mods:X2}: high nibble {f.Mods >> 4} is not in the grammar");
            if ((f.Mods >> 4 == 1) != f.Fixed.HasValue)
                Add("error", "fixed-count", c.Name, path, $"modifier 0x{f.Mods:X2} and fixed count {f.Fixed?.ToString() ?? "none"} disagree");
            if (f.Fixed is <= 0)
                Add("error", "fixed-count", c.Name, path, $"fixed count {f.Fixed}");
            bool classRef = f.Type == 14 && (f.Mods & 0x0F) == 0;
            if (classRef != f.Ref.HasValue)
                Add("error", "ref-presence", c.Name, path, $"type {f.Type} modifier 0x{f.Mods:X2} with ref {f.Ref?.ToString() ?? "none"}");
            if (f.Ref is { } r && !ids.ContainsKey(r) && r != 0)
                Add("error", "ref-unresolved", c.Name, path, $"ref id {r}");
            if (f.Ref2 is { } r2 && r2 != f.Ref)
                Add("info", "ref-copies-differ", c.Name, path, $"template reference copies {f.Ref} and {r2}");
            if ((f.Type == 13) != (f.Args != null))
                Add("error", "template-args", c.Name, path, "template arguments and type 13 disagree");
            if (top && !(knownPairs?.Contains((f.Type, f.Mods)) ?? true))
                unknownPairs[(f.Type, f.Mods)] = unknownPairs.GetValueOrDefault((f.Type, f.Mods)) + 1;
            if (f.Args != null) foreach (var a in f.Args) CheckField(a, c, path + "<>", false);
        }
    }


    /// <summary>Byte widths of the scalar type codes whose width the file layout confirms (checked on the ElemRec rows of
    /// Global/ElemTable, Revit 2020/2022/2025): bool/u8 1, i32/u32/f32 4, f64/i64 8, GUID 16; a class reference (type 10) is its
    /// 2-byte class tag, as the element header records store it.
    /// Type 3 (1 or 2 bytes), strings, containers and pointers have none.</summary>
    private static readonly Dictionary<int, int> ScalarWidth = new() { [1] = 1, [2] = 1, [4] = 4, [5] = 4, [6] = 4, [7] = 8, [9] = 16, [10] = 2, [11] = 8 };

    /// <summary>Serialized size of an instance of the class (base fields first), or null when any field has no fixed width.</summary>
    internal static int? FixedSize(Dictionary<int, SchemaClass> ids, int classId, int depth = 0)
    {
        if (depth > 32 || !ids.TryGetValue(classId, out var c)) return null;
        int total = 0;
        if (c.Base != 0) { if (FixedSize(ids, c.Base, depth + 1) is not { } b) return null; total += b; }
        foreach (var f in c.Fields.Where(f => f.Stored))
        {
            int count = f.Fixed ?? 1;
            if (f.Mods >> 4 is not (0 or 1) || (f.Mods & 0x0F) != 0) return null;
            int? one = f.Type == 14 && f.Ref is { } r ? FixedSize(ids, r, depth + 1)
                : ScalarWidth.TryGetValue(f.Type, out int w) ? w : null;
            if (one is null) return null;
            total += one.Value * count;
        }
        return total;
    }


    private static string Sig(SchemaField f, Dictionary<int, SchemaClass> ids)
    {
        var s = new StringBuilder();
        s.Append(CultureInfo.InvariantCulture, $"t{f.Type}/m0x{f.Mods:X2}");
        if (f.Fixed is { } c) s.Append(CultureInfo.InvariantCulture, $"/x{c}");
        if (f.Ref is { } r) s.Append('/').Append(r != 0 && ids.TryGetValue(r, out var rc) ? rc.Name : "#" + r);
        if (f.Args != null) s.Append('<').Append(string.Join(",", f.Args.Select(a => Sig(a, ids)))).Append('>');
        return s.ToString();
    }

    private static string BaseName(SchemaClass c, Dictionary<int, SchemaClass> ids) =>
        c.Base != 0 && ids.TryGetValue(c.Base, out var b) ? b.Name : c.Base == 0 ? "" : "#" + c.Base;

    /// <summary>Structure without the class's own name or id: base name, version, field list.</summary>
    private static string Shape(SchemaClass c, Dictionary<int, SchemaClass> ids) =>
        BaseName(c, ids) + "|" + c.Version + "|" + string.Join(";", c.Fields.Select(f => f.Name + ":" + Sig(f, ids)));

    private static Dictionary<string, SchemaClass> ByName(List<SchemaClass> l) => l.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());

    internal static List<SchemaChange> Diff(SchemaDocument a, SchemaDocument b)
    {
        var changes = new List<SchemaChange>();
        var ia = a.ById; var ib = b.ById;
        var na = ByName(a.Classes); var nb = ByName(b.Classes);
        var removed = a.Classes.Where(c => !nb.ContainsKey(c.Name)).ToList();
        var added = b.Classes.Where(c => !na.ContainsKey(c.Name)).ToList();
        // A removed and an added class whose shapes are equal, each shape being unique on its side, are one renamed class.
        var removedByShape = removed.ToLookup(c => Shape(c, ia)); var addedByShape = added.ToLookup(c => Shape(c, ib));
        var renamed = new HashSet<string>();
        foreach (var r in removed)
        {
            string shape = Shape(r, ia);
            if (removedByShape[shape].Count() != 1 || addedByShape[shape].Count() != 1) continue;
            var n = addedByShape[shape].Single();
            changes.Add(new("class-rename-candidate", r.Name, "", r.Name + " #" + r.Id, n.Name + " #" + n.Id));
            renamed.Add(r.Name); renamed.Add(n.Name);
        }
        foreach (var r in removed.Where(c => !renamed.Contains(c.Name)))
            changes.Add(new("class-removed", r.Name, "", "#" + r.Id, ""));
        foreach (var n in added.Where(c => !renamed.Contains(c.Name)))
            changes.Add(new("class-added", n.Name, "", "", "#" + n.Id));
        foreach (var ca in a.Classes)
        {
            if (!nb.TryGetValue(ca.Name, out var cb)) continue;
            if (ca.Id != cb.Id) changes.Add(new("class-id-moved", ca.Name, "", ca.Id.ToString(CultureInfo.InvariantCulture), cb.Id.ToString(CultureInfo.InvariantCulture)));
            string ba = BaseName(ca, ia), bb = BaseName(cb, ib);
            if (ba != bb) changes.Add(new("base-changed", ca.Name, "", ba, bb));
            if (ca.Version != cb.Version) changes.Add(new("version-changed", ca.Name, "", ca.Version.ToString(CultureInfo.InvariantCulture), cb.Version.ToString(CultureInfo.InvariantCulture)));
            var fa = ca.Fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First()); var fb = cb.Fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (var f in ca.Fields.Where(f => !fb.ContainsKey(f.Name)))
                changes.Add(new("field-removed", ca.Name, f.Name, Sig(f, ia), ""));
            foreach (var f in cb.Fields.Where(f => !fa.ContainsKey(f.Name)))
                changes.Add(new("field-added", ca.Name, f.Name, "", Sig(f, ib)));
            foreach (var f in ca.Fields.Where(f => fb.ContainsKey(f.Name)))
                if (Sig(f, ia) != Sig(fb[f.Name], ib))
                    changes.Add(new("field-retyped", ca.Name, f.Name, Sig(f, ia), Sig(fb[f.Name], ib)));
            var common = ca.Fields.Where(f => fb.ContainsKey(f.Name)).Select(f => f.Name).ToList();
            var commonB = cb.Fields.Where(f => fa.ContainsKey(f.Name)).Select(f => f.Name).ToList();
            if (!common.SequenceEqual(commonB))
                changes.Add(new("fields-reordered", ca.Name, "", string.Join(",", common), string.Join(",", commonB)));
        }
        return changes;
    }


    internal static string FormatValidation(SchemaDocument doc, List<SchemaIssue> issues)
    {
        var s = new StringBuilder();
        s.AppendLine($"schema: {doc.Source}  {doc.Classes.Count} classes, {doc.FieldCount} fields, stream {doc.StreamBytes} bytes, GUID lists {(doc.GuidLists ? "yes" : "no")}");
        foreach (var g in issues.GroupBy(i => (i.Severity, i.Code)).OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Code))
            s.AppendLine($"  {g.Key.Severity,-8} {g.Key.Code,-18} {g.Count()}");
        var ids = doc.ById;
        int fixedClasses = doc.Classes.Count(c => FixedSize(ids, c.Id) != null);
        s.AppendLine($"  classes with a schema-derived fixed serialized size: {fixedClasses} of {doc.Classes.Count}");
        if (issues.Count == 0) s.AppendLine("  no findings");
        foreach (var i in issues.Take(200))
            s.AppendLine($"{i.Severity}: {i.Code}: {i.Class}{(i.Field.Length > 0 ? "." + i.Field : "")}: {i.Message}");
        if (issues.Count > 200) s.AppendLine($"... {issues.Count - 200} more");
        return s.ToString();
    }

    internal static string FormatDiff(SchemaDocument a, SchemaDocument b, List<SchemaChange> changes)
    {
        var s = new StringBuilder();
        var nb = b.Classes.Select(c => c.Name).ToHashSet();
        int common = a.Classes.Count(c => nb.Contains(c.Name));
        s.AppendLine($"A: {a.Source}  {a.Classes.Count} classes, {a.FieldCount} fields");
        s.AppendLine($"B: {b.Source}  {b.Classes.Count} classes, {b.FieldCount} fields");
        s.AppendLine($"classes with the same name in both: {common}");
        var touched = changes.Where(c => c.Kind != "class-id-moved" && c.Kind != "class-added" && c.Kind != "class-removed" && c.Kind != "class-rename-candidate")
            .Select(c => c.Class).ToHashSet();
        s.AppendLine($"classes with the same name and an identical definition (base, version, fields): {common - touched.Count}");
        s.AppendLine();
        s.AppendLine("kind                 count");
        foreach (var g in changes.GroupBy(c => c.Kind).OrderBy(g => g.Key)) s.AppendLine($"{g.Key,-20} {g.Count(),6}");
        foreach (var g in changes.Where(c => c.Kind != "class-id-moved").GroupBy(c => c.Kind).OrderBy(g => g.Key))
        {
            s.AppendLine().AppendLine($"== {g.Key} ==");
            foreach (var c in g.Take(400))
                s.AppendLine($"{c.Class}{(c.Field.Length > 0 ? "." + c.Field : "")}: {c.From} -> {c.To}");
            if (g.Count() > 400) s.AppendLine($"... {g.Count() - 400} more (see the JSON list)");
        }
        return s.ToString();
    }

    internal static string DiffJson(SchemaDocument a, SchemaDocument b, List<SchemaChange> changes)
    {
        var s = new StringBuilder();
        s.Append("{\"format\":\"tf-schema-diff/1\",\"a\":").Append(System.Text.Json.JsonSerializer.Serialize(a.Source))
            .Append(",\"b\":").Append(System.Text.Json.JsonSerializer.Serialize(b.Source)).Append(",\"changes\":[\n");
        for (int i = 0; i < changes.Count; i++)
            s.Append(System.Text.Json.JsonSerializer.Serialize(changes[i])).Append(i + 1 < changes.Count ? ",\n" : "\n");
        return s.Append("]}\n").ToString();
    }


    /// <summary>--schema-dump &lt;file&gt; &lt;out.json&gt; | --schema-check &lt;file|json&gt; [out.txt] | --schema-diff &lt;a&gt; &lt;b&gt; [out.txt]</summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            switch (args[0])
            {
                case "--schema-dump" when args.Length == 3:
                {
                    var doc = TfSchemaReader.Load(args[1]);
                    File.WriteAllText(args[2], TfSchemaReader.WriteJson(doc), new UTF8Encoding(false));
                    output.WriteLine($"{doc.Source}: {doc.Classes.Count} classes, {doc.FieldCount} fields, GUID lists {(doc.GuidLists ? "yes" : "no")} -> {args[2]}");
                    return 0;
                }
                case "--schema-check" when args.Length is 2 or 3:
                {
                    var doc = TfSchemaReader.Load(args[1]);
                    var known = TfBlockDeserializer.Schemas.Values.SelectMany(c => c.Fields).Select(f => (f.Type, f.Mods)).ToHashSet();
                    var issues = Validate(doc, known);
                    string text = FormatValidation(doc, issues);
                    if (args.Length == 3) File.WriteAllText(args[2], text, new UTF8Encoding(false)); else output.Write(text);
                    return issues.Any(i => i.Severity == "error") ? 2 : 0;
                }
                case "--schema-diff" when args.Length is 3 or 4:
                {
                    var a = TfSchemaReader.Load(args[1]); var b = TfSchemaReader.Load(args[2]);
                    var changes = Diff(a, b);
                    string text = FormatDiff(a, b, changes);
                    if (args.Length == 4)
                    {
                        File.WriteAllText(args[3], text, new UTF8Encoding(false));
                        File.WriteAllText(args[3] + ".json", DiffJson(a, b, changes), new UTF8Encoding(false));
                    }
                    else output.Write(text);
                    return 0;
                }
            }
            error.WriteLine("Usage: --schema-dump <file.rvt|rfa|rte> <out.json> | --schema-check <file|json> [out.txt] | --schema-diff <a> <b> [out.txt]");
            return 1;
        }
        catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            error.WriteLine(e.Message);
            return 1;
        }
    }
}
