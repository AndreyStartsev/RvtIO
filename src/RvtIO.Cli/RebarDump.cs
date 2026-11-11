using System.Globalization;
using OpenMcdf;
using RvtIO.Parsing;

namespace RvtIO;

/// <summary>--rebar-dump &lt;file.rvt&gt; &lt;ClassName&gt; [limit]: the field walk of the root blocks of one class (every field by class and field name), for reading a layout off the file.</summary>
internal static class RebarDump
{
    private sealed class All : IReadOnlySet<uint>
    {
        public int Count => int.MaxValue;
        public bool Contains(uint item) => true;
        public IEnumerator<uint> GetEnumerator() => Enumerable.Empty<uint>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<uint> other) => false;
        public bool IsProperSupersetOf(IEnumerable<uint> other) => false;
        public bool IsSubsetOf(IEnumerable<uint> other) => false;
        public bool IsSupersetOf(IEnumerable<uint> other) => false;
        public bool Overlaps(IEnumerable<uint> other) => false;
        public bool SetEquals(IEnumerable<uint> other) => false;
    }

    internal static int Run(string[] args)
    {
        using var root = RootStorage.OpenRead(args[0]);
        var doc = TfSchemaReader.Parse(PagedGzipStream.Read(root, "Formats", "Latest")!);
        var schema = FileSchema.Read(root)!;
        string target = args[1];
        if (target.StartsWith('?'))
        {
            // --rebar-dump <file> ?Class: the stored fields of the class chain (class.field type mods ref), base class first
            var byId = doc.Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            var chain = new List<SchemaClass>();
            for (var c = doc.Classes.First(c => c.Name == target[1..]); ; c = byId[c.Base]) { chain.Add(c); if (c.Base == 0 || !byId.ContainsKey(c.Base)) break; }
            chain.Reverse();
            foreach (var c in chain)
                foreach (var f in c.Fields.Where(f => f.Stored))
                    Console.WriteLine($"{c.Name}.{f.Name} type={f.Type} mods={f.Mods} ref={(f.Ref is int r && byId.TryGetValue(r, out var rc) ? rc.Name : "-")}");
            return 0;
        }
        int limit = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3;
        string only = args.Length > 3 ? args[3] : "";
        var walker = new WideBlockWalker(doc, (d, f) => ((f.Name.StartsWith("m_included", StringComparison.Ordinal) || f.Name is "m_doubles" or "m_ints") ? WideBlockWalker.AllItemsKeyPrefix : "") + d.Name + "." + f.Name, doc.Classes.Select(c => c.Name));
        // the walk of the rebar reader: the values of an entity read by its schema, an entity that cannot be read passed
        walker.UseEntitySchemas(EntitySchemaReader.Read(root, doc), passUnknown: true);
        var partStor = root.OpenStorage("Partitions");
        var decompressor = new ChunkDecompressor();
        var streams = new List<IReadOnlyList<byte[]>>();
        foreach (var e in partStor.EnumerateEntries().Where(e => e.Type == EntryType.Stream))
        {
            using var s = partStor.OpenStream(e.Name);
            var raw = new byte[s.Length];
            s.ReadExactly(raw);
            streams.Add(decompressor.DecompressAll(raw));
        }
        int shown = 0, seen = 0;
        var counts = new SortedDictionary<string, int>();
        WideElementRecords.BuildIndex(streams, schema, new All(), (id, data, offset, size, cls) =>
        {
            string name = walker.Class(cls)!.Name;
            counts[name] = counts.GetValueOrDefault(name) + 1;
            if (name != target) return;
            seen++;
            if (shown >= limit || (only != "" && only != id.ToString(CultureInfo.InvariantCulture))) return;
            shown++;
            var w = walker.Run(data, offset, size, cls);
            Console.WriteLine($"=== {name} id={id} size={size} exact={w.Exact} pos={w.Position} err={w.Error} queue={w.Queue.Count}");
            if (w.Error != null && Environment.GetEnvironmentVariable("RVTIO_HEX") == "1")
            {
                int from = Math.Max(offset, w.Position - 48), to = Math.Min(offset + size, w.Position + 160);
                Console.WriteLine("hex@" + (from - offset) + " (fail at " + (w.Position - offset) + "): " + Convert.ToHexString(data, from, to - from));
            }
            int lastQ = -2;
            foreach (var c in w.Captures)
            {
                if (c.QueueIndex != lastQ)
                {
                    lastQ = c.QueueIndex;
                    Console.WriteLine(c.QueueIndex < 0 ? "-- root" : $"-- q{c.QueueIndex} {walker.Class(w.Queue[c.QueueIndex])!.Name}");
                }
                string v = c.Kind switch
                {
                    WideBlockWalker.CaptureKind.Real => c.Real.ToString("R", CultureInfo.InvariantCulture),
                    WideBlockWalker.CaptureKind.Text => "\"" + c.Text + "\"",
                    WideBlockWalker.CaptureKind.PointerClass => "ptr:" + (c.Integer > 0 ? walker.Class((int)c.Integer)!.Name : c.Integer.ToString(CultureInfo.InvariantCulture)),
                    _ => c.Integer.ToString(CultureInfo.InvariantCulture),
                };
                Console.WriteLine($"   {c.Key} = {v}");
            }
        });
        Console.WriteLine("seen " + seen);
        if (args.Length > 2 && args[2] == "0") foreach (var (n, c) in counts) Console.WriteLine($"{n} {c}");
        return 0;
    }
}
