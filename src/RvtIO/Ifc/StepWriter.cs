using System.Globalization;
using System.Text;

namespace RvtIO.Ifc;

/// <summary>
/// Low-level IFC STEP file writer (ISO 10303-21).
/// Manages entity IDs, serialization to text, and file structure.
/// </summary>
internal class StepWriter
{
    internal readonly record struct Checkpoint(int NextId, int EntityCount);

    private int _nextId = 1;
    private readonly List<(int id, string type, string args)> _entities = new();

    /// <summary>The circular arcs a written IfcPolyline stands for, by the id of the polyline: the chords each arc was written as.
    /// A polyline without an entry has straight sides only, as far as its writer knew.</summary>
    internal Dictionary<int, IReadOnlyList<IfcExporter.ArcChordsCurrent>> PolylineArcs { get; } = new();

    /// <summary>Captures the append-only writer state so a caller can inspect the exact
    /// representation it produced and then discard that whole attempted product.</summary>
    internal Checkpoint CreateCheckpoint() => new(_nextId, _entities.Count);

    internal void Rollback(Checkpoint checkpoint)
    {
        if (checkpoint.NextId < 1 || checkpoint.NextId > _nextId
            || checkpoint.EntityCount < 0 || checkpoint.EntityCount > _entities.Count)
            throw new ArgumentOutOfRangeException(nameof(checkpoint));

        _entities.RemoveRange(checkpoint.EntityCount,
            _entities.Count - checkpoint.EntityCount);
        _nextId = checkpoint.NextId;
        // the ids from here on are given out again
        foreach (int id in PolylineArcs.Keys.Where(id => id >= checkpoint.NextId).ToList()) PolylineArcs.Remove(id);
    }

    public int NextId() => _nextId++;

    /// <summary>Rewrites the attribute text of every entity written so far.</summary>
    internal void RewriteArguments(Func<string, string, string> rewrite)
    {
        for (int i = 0; i < _entities.Count; i++)
        {
            var (id, type, args) = _entities[i];
            _entities[i] = (id, type, rewrite(type, args));
        }
    }

    /// <summary>Add an entity and return its #ID.</summary>
    public int Add(string type, string args)
    {
        int id = _nextId++;
        _entities.Add((id, type, args));
        return id;
    }

    internal int EntityCount => _entities.Count;

    internal (int Id, string Type, string Args) EntityAt(int index) => _entities[index];

    /// <summary>Replaces the attribute list of an already written entity.</summary>
    internal void ReplaceArgs(int index, string args)
    {
        var (id, type, _) = _entities[index];
        _entities[index] = (id, type, args);
    }

    /// <summary>Replaces the class and the attribute list of an already written entity.</summary>
    internal void ReplaceEntity(int index, string type, string args)
    {
        var (id, _, _) = _entities[index];
        _entities[index] = (id, type, args);
    }

    /// <summary>Permutes entities among the given places: the entity now at <c>sources[k]</c> is written at <c>places[k]</c>.
    /// The ids stay with their entities, so every reference remains valid (STEP allows forward references).</summary>
    internal void PlaceEntities(IReadOnlyList<int> places, IReadOnlyList<int> sources)
    {
        if (places.Count != sources.Count || !places.Order().SequenceEqual(sources.Order()))
            throw new ArgumentException("sources must be a permutation of places", nameof(sources));
        var moved = sources.Select(source => _entities[source]).ToArray();
        for (int k = 0; k < places.Count; k++) _entities[places[k]] = moved[k];
    }

    /// <summary>Removes the entities with the given ids; nothing may reference them any more.</summary>
    internal void RemoveEntities(IReadOnlySet<int> ids) => _entities.RemoveAll(entity => ids.Contains(entity.id));

    /// <summary>Add an entity with a pre-assigned ID.</summary>
    public void AddWithId(int id, string type, string args)
    {
        _entities.Add((id, type, args));
    }

    /// <summary>Write complete IFC STEP file to stream.</summary>
    public void WriteTo(TextWriter w, string fileName, string schema, string description, string application,
        string organization = "")
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        w.WriteLine("ISO-10303-21;");
        w.WriteLine("HEADER;");
        w.WriteLine($"FILE_DESCRIPTION(('{Escape(description)}'),'2;1');");
        w.WriteLine($"FILE_NAME('{Escape(fileName)}','{now}',(''),('{Escape(organization)}'),'{Escape(application)}','RvtIO','');");
        w.WriteLine($"FILE_SCHEMA(('{schema}'));");
        w.WriteLine("ENDSEC;");
        w.WriteLine("DATA;");

        foreach (var (id, type, args) in _entities)
            w.WriteLine($"#{id}={type}({args});");

        w.WriteLine("ENDSEC;");
        w.WriteLine("END-ISO-10303-21;");
    }

    public static string Ref(int id) => $"#{id}";

    public static string Str(string? s) => s == null ? "$" : $"'{Escape(s)}'";

    /// <summary>A length given in millimetres, written in metres by moving the decimal point of its 17
    /// significant digits three places. The millimetre value is computed as the double feet * 304.8 and printed with 17
    /// digits; this reproduces those digits exactly instead of re-rounding the value in metres.</summary>
    public static string RealFromMillimetres(double millimetres)
    {
        decimal metres = decimal.Parse(millimetres.ToString("G17", CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture) / 1000m;
        string text = metres.ToString("0.############################", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : text + ".";
    }

    public static string Real(double v) => Real(v, "G15");

    /// <summary>A real written with the 17 significant digits that identify the double, trailing zeros dropped (the
    /// printf %.17g digits), a three-digit signed exponent when the value needs one (1.0000000000000001E-005) and a
    /// decimal point after an integer part (100.). Used for quantities and property values, which are not rounded.</summary>
    public static string RealExact(double v)
    {
        string value = v.ToString("G17", CultureInfo.InvariantCulture);
        int exponentIndex = value.IndexOf('E');
        if (exponentIndex < 0) return value.Contains('.') ? value : value + ".";
        string mantissa = value[..exponentIndex];
        if (!mantissa.Contains('.')) mantissa += ".0";
        string digits = value[(exponentIndex + 2)..].PadLeft(3, '0');
        return mantissa + "E" + value[exponentIndex + 1] + digits;
    }

    internal static string Real(double v, string format)
    {
        string value = v.ToString(format, CultureInfo.InvariantCulture);
        int exponentIndex = value.IndexOfAny(['E', 'e']);
        if (exponentIndex >= 0)
        {
            string mantissa = value[..exponentIndex];
            if (!mantissa.Contains('.')) mantissa += ".0";
            return mantissa + value[exponentIndex..];
        }
        return value.Contains('.') ? value : value + ".";
    }

    public static string Enum(string v) => $".{v}.";

    public static string List(params string[] items) => $"({string.Join(",", items)})";

    public static string List(IEnumerable<string> items) => $"({string.Join(",", items)})";

    public static string Null => "$";

    public static string Star => "*";

    /// <summary>The inverse of <see cref="Str"/> for the text between the quotes: '' and \\
    /// become one character, \X\hh a Latin-1 character and \X2\hhhh...\X0\ runs become UTF-16 code units. Null for text
    /// <see cref="Str"/> never writes.</summary>
    internal static string? Unescape(string escaped)
    {
        var sb = new StringBuilder(escaped.Length);
        for (int i = 0; i < escaped.Length; i++)
        {
            char c = escaped[i];
            if (c == '\'')
            {
                if (i + 1 >= escaped.Length || escaped[i + 1] != '\'') return null;
                sb.Append('\''); i++;
            }
            else if (c != '\\') sb.Append(c);
            else if (escaped.AsSpan(i).StartsWith("\\\\")) { sb.Append('\\'); i++; }
            else if (escaped.AsSpan(i).StartsWith("\\X\\"))
            {
                if (i + 5 > escaped.Length
                    || !byte.TryParse(escaped.AsSpan(i + 3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte latin1))
                    return null;
                sb.Append((char)latin1);
                i += 4;
            }
            else if (escaped.AsSpan(i).StartsWith("\\X2\\"))
            {
                int end = escaped.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                if (end < 0 || (end - i - 4) % 4 != 0) return null;
                for (int k = i + 4; k < end; k += 4)
                {
                    if (!ushort.TryParse(escaped.AsSpan(k, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort unit))
                        return null;
                    sb.Append((char)unit);
                }
                i = end + 3;
            }
            else return null;
        }
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inUnicode = false;
        foreach (char c in s)
        {
            if (c < 0x100 && (c > 0x7E || c < 0x20))
            {
                // A control or ISO 8859-1 character is one byte, \X\HH (the Revit exporter writes it so); it also ends a run of characters above it
                if (inUnicode) { sb.Append("\\X0\\"); inUnicode = false; }
                sb.Append("\\X\\").Append(((int)c).ToString("X2"));
            }
            else if (c > 0x7E || c < 0x20)
            {
                // Beyond Latin-1: use IFC extended encoding \X2\XXXX\X0\
                if (!inUnicode) { sb.Append("\\X2\\"); inUnicode = true; }
                sb.Append(((int)c).ToString("X4"));
            }
            else
            {
                if (inUnicode) { sb.Append("\\X0\\"); inUnicode = false; }
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\'') sb.Append("''");
                else sb.Append(c);
            }
        }
        if (inUnicode) sb.Append("\\X0\\");
        return sb.ToString();
    }
}
