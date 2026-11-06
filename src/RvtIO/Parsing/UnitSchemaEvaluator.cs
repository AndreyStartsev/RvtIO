using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RvtIO.Parsing;

/// <summary>Numeric component projection of the tested Revit2022 SDK corpus plus
/// own persisted additions. Returns SI factors, NOT FromInternalUnits(1), offsets,
/// DisplayUnitTypeInfo/legacy winners or complete native JSON registration.</summary>
internal static partial class UnitSchemaEvaluator
{
    private sealed record Power(string TypeId, int Exponent);
    private sealed record Node(string TypeId, string Kind, double? Factor,
        Power[] FactorParents, Power[] UnitParents, string? AbsoluteParent);
    private sealed record Catalog(HashSet<string> Known, Dictionary<string, Node> Nodes);
    private static readonly Lazy<Catalog> Builtins = new(LoadBuiltins);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed class Utf8Comparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            if (a == null || b == null) throw new InvalidDataException("Null schema identifier");
            return StrictUtf8.GetBytes(a).AsSpan().SequenceCompareTo(StrictUtf8.GetBytes(b));
        }
    }
    private static readonly Utf8Comparer KeyOrder = new();

    internal static IReadOnlyDictionary<string, double>? EvaluateOwn(
        TfBlockDeserializer.DocumentSchemaMap own, out string? failure)
    {
        failure = null;
        try
        {
            var nodes = CreateCatalog(own).Nodes;
            var cache = new Dictionary<(string Kind, string Id), double>();
            var active = new HashSet<(string Kind, string Id)>();
            double Evaluate(string id, string kind)
            {
                if (cache.TryGetValue((kind, id), out double cached)) return cached;
                if (active.Count >= 100 || !active.Add((kind, id)))
                    throw new InvalidDataException("Cyclic or over-depth unit dependency: " + id);
                try
                {
                    if (!nodes.TryGetValue(id, out var node))
                        throw new InvalidDataException("Missing full unit dependency: " + id);
                    bool isFactor = node.Kind is "factor" or "derived-factor";
                    if (isFactor != (kind == "factor"))
                        throw new InvalidDataException("Wrong typed unit dependency: " + id);
                    double value;
                    if (node.Kind == "absolute-unit")
                        value = Evaluate(node.AbsoluteParent ?? throw new InvalidDataException("Missing absolute parent"), "unit");
                    else if (node.Kind == "primitive-unit") value = 1.0;
                    else
                    {
                        // Component constructor values, not observed conversion coefficients.
                        value = node.Factor ?? 1.0;
                        foreach (var (parentKind, parents) in new[] { ("factor", node.FactorParents), ("unit", node.UnitParents) })
                        foreach (var parent in parents)
                        {
                            double p = Evaluate(parent.TypeId, parentKind);
                            // Power 2 uses a plain double multiplication.
                            double powered = parent.Exponent == 2 ? p * p : Math.Pow(p, parent.Exponent);
                            value *= powered;
                            if (!double.IsFinite(value)) throw new InvalidDataException("Nonfinite unit factor: " + id);
                        }
                    }
                    cache.Add((kind, id), value); return value;
                }
                finally { active.Remove((kind, id)); }
            }
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var node in nodes.Values.Where(n => n.Kind.EndsWith("unit", StringComparison.Ordinal)))
                result.Add(node.TypeId, Evaluate(node.TypeId, "unit"));
            return result;
        }
        catch (InvalidDataException e) { failure = e.Message; return null; }
        catch (JsonException e) { failure = "Malformed own schema JSON: " + e.Message; return null; }
        catch (FormatException e) { failure = "Invalid own numeric schema value: " + e.Message; return null; }
        catch (DecoderFallbackException e) { failure = "Invalid unit identifier Unicode: " + e.Message; return null; }
        catch (EncoderFallbackException e) { failure = "Invalid unit identifier Unicode: " + e.Message; return null; }
        catch (InvalidOperationException e) { failure = "Invalid own schema value shape: " + e.Message; return null; }
        catch (KeyNotFoundException e) { failure = "Missing own schema field: " + e.Message; return null; }
    }

    private static Catalog CreateCatalog(TfBlockDeserializer.DocumentSchemaMap own)
    {
        var seed = Builtins.Value;
        var known = new HashSet<string>(seed.Known, StringComparer.Ordinal);
        var nodes = new Dictionary<string, Node>(seed.Nodes, StringComparer.Ordinal);
        var inputKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in own.Entries)
        {
            if (!inputKeys.Add(entry.Key)) throw new InvalidDataException("Repeated own full schema key");
            // The persistence gate retains a known full key even when the own JSON differs or is malformed. No version/name stripping.
            if (known.Contains(entry.Key)) continue;
            using var json = JsonDocument.Parse(entry.Value, new() { MaxDepth = 40 });
            CheckUniqueProperties(json.RootElement);
            var node = ParseOwn(entry.Key, json.RootElement);
            if (node != null) nodes.Add(entry.Key, node);
            known.Add(entry.Key);
        }
        return new(known, nodes);
    }

    private static Catalog LoadBuiltins()
    {
        using var stream = typeof(UnitSchemaEvaluator).Assembly.GetManifestResourceStream("RvtIO.Resources.r22_unit_definitions.json")
            ?? throw new InvalidDataException("Missing source-bound Revit2022 unit corpus");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1
            || root.GetProperty("profile").GetString() != "Revit2022-unit-catalog")
            throw new InvalidDataException("Unsupported built-in unit corpus");
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in root.GetProperty("knownTypeIds").EnumerateArray())
            if (!known.Add(Identifier(key))) throw new InvalidDataException("Repeated built-in full key");
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var value in root.GetProperty("components").EnumerateArray())
        {
            string id = Identifier(value.GetProperty("typeId"));
            string kind = Identifier(value.GetProperty("kind"));
            var raw = value.GetProperty("scalarFactorBits");
            double? factor = raw.ValueKind == JsonValueKind.Null ? null
                : BitConverter.UInt64BitsToDouble(ulong.Parse(raw.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            Power[] ReadPowers(string name) => value.GetProperty(name).EnumerateArray()
                .Select(x => new Power(Identifier(x.GetProperty("typeId")), x.GetProperty("power").GetInt32())).ToArray();
            var parent = value.GetProperty("absoluteParent");
            var node = new Node(id, kind, factor, ReadPowers("factorParents"), ReadPowers("unitParents"),
                parent.ValueKind == JsonValueKind.Null ? null : Identifier(parent));
            if (!known.Contains(id) || factor.HasValue && !double.IsFinite(factor.Value)
                || kind is not ("factor" or "derived-factor" or "primitive-unit" or "derived-unit" or "absolute-unit"))
                throw new InvalidDataException("Invalid built-in numeric component");
            nodes.Add(id, node);
        }
        if (known.Count != 717 || nodes.Count != 324) throw new InvalidDataException("Incomplete built-in unit corpus");
        return new(known, nodes);
    }

    private static string Identifier(JsonElement value)
        => Identifier(value.GetString() ?? throw new InvalidDataException("Null full identifier"));

    private static string Identifier(string text)
    {
        if (text.Length == 0 || text.Contains('\0')) throw new InvalidDataException("Empty/NUL full identifier is outside this projection");
        StrictUtf8.GetByteCount(text); return text;
    }

    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new InvalidDataException("Repeated JSON property: " + p.Name);
                CheckUniqueProperties(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUniqueProperties(item);
    }

    private static Node? ParseOwn(string key, JsonElement record)
    {
        if (Identifier(record.GetProperty("typeid")) != key) throw new InvalidDataException("Own schema full key/typeid mismatch");
        var inherited = record.GetProperty("inherits");
        if (inherited.GetArrayLength() != 1) throw new InvalidDataException("Single own inherited family required");
        string family = Identifier(inherited[0]);
        string? kind = family switch
        {
            "autodesk.unit:factor-1.0.0" => "factor",
            "autodesk.unit:factor.derived-1.0.0" or "autodesk.unit:factor.derived-1.0.1" => "derived-factor",
            "autodesk.unit:unit.primitive-1.0.0" or "autodesk.unit:unit.primitive.dimensional-1.0.0" => "primitive-unit",
            "autodesk.unit:unit.derived-1.0.0" or "autodesk.unit:unit.derived-1.0.1" => "derived-unit",
            "autodesk.unit:unit.absolute-1.0.0" => "absolute-unit",
            "autodesk.unit:dimension-1.0.0" or "autodesk.unit:symbol-1.0.0" or "autodesk.unit:symbol.prefixOrSuffix-1.0.0"
                or "autodesk.unit:quantity-1.0.0" => null,
            _ => throw new InvalidDataException("Unsupported own inherited family: " + family),
        };
        var constants = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (record.TryGetProperty("constants", out var rows))
            foreach (var row in rows.EnumerateArray())
                if (!constants.TryAdd(Identifier(row.GetProperty("id")), row))
                    throw new InvalidDataException("Repeated own constant id");
        // Non-numeric family semantics/registration are not reproduced here.
        if (kind == null) return null;
        double? factor = null;
        if (kind is "factor" or "derived-factor" or "derived-unit" && constants.TryGetValue("factor", out var f))
        {
            factor = f.GetProperty("value").GetDouble();
            if (!double.IsFinite(factor.Value)) throw new InvalidDataException("Nonfinite own scalar factor");
        }
        Power[] Powers(string name)
        {
            if (!constants.TryGetValue(name, out var constant)) return [];
            var powers = new List<Power>();
            foreach (var p in constant.GetProperty("value").EnumerateObject())
            {
                var id = Identifier(p.Name);
                string raw = p.Value.GetRawText();
                if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || !p.Value.TryGetInt32(out int exponent))
                    throw new InvalidDataException("Own parent power must be signed int32");
                powers.Add(new(id, exponent));
            }
            return powers.OrderBy(p => p.TypeId, KeyOrder).ToArray();
        }
        var factorParents = kind is "derived-factor" or "derived-unit" ? Powers("parentFactors") : [];
        var unitParents = kind == "derived-unit" ? Powers("parentUnits") : [];
        if (kind == "derived-unit" && unitParents.Length == 0) throw new InvalidDataException("Missing/empty own parentUnits");
        string? absoluteParent = kind == "absolute-unit"
            ? Identifier(constants["parentUnit"].GetProperty("typedValue").GetProperty("typeid")) : null;
        return new(key, kind, factor, factorParents, unitParents, absoluteParent);
    }
}
