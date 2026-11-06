using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RvtIO.Parsing;

internal static partial class UnitSchemaEvaluator
{
    /// <summary>Native dimension/SI/absolute-offset/universal-scale component
    /// projection. Linear formula is NOT the public UnitUtils route: display
    /// lookup, legacy overrides, 1ToRatio and special globals remain separate.</summary>
    internal sealed record UnitCore(ReadOnlyCollection<int> Dimensions, bool Absolute,
        double SiFactor, double Offset, double InternalScale)
    {
        public double LinearFromInternal(double value) => value / InternalScale - Offset;
        public double LinearToInternal(double value) => (value + Offset) * InternalScale;
    }

    private sealed record Detail(string? DimensionTypeId, double Offset);
    private static readonly Lazy<Dictionary<string, Detail>> BuiltinDetails = new(LoadDetails);
    // Full dimension literals map to BaseUnit enums; no dimensional classification
    // from names, length-only guesses or model ids.
    private static readonly Dictionary<string,int> BaseDimensions = new(StringComparer.Ordinal)
    {
        ["autodesk.unit.dimension:length-1.0.0"] = 0,
        ["autodesk.unit.dimension:mass-1.0.0"] = 2,
        ["autodesk.unit.dimension:time-1.0.0"] = 3,
        ["autodesk.unit.dimension:electricCurrent-1.0.0"] = 4,
        ["autodesk.unit.dimension:temperature-1.0.0"] = 5,
        ["autodesk.unit.dimension:luminousIntensity-1.0.0"] = 6,
    };

    internal static IReadOnlyDictionary<string, UnitCore>? EvaluateCore(
        TfBlockDeserializer.DocumentSchemaMap own, out string? failure)
    {
        var si = EvaluateOwn(own, out failure);
        if (si == null) return null;
        try
        {
            var catalog = CreateCatalog(own);
            var details = new Dictionary<string, Detail>(BuiltinDetails.Value, StringComparer.Ordinal);
            foreach (var entry in own.Entries)
            {
                if (Builtins.Value.Known.Contains(entry.Key)
                    || !catalog.Nodes.TryGetValue(entry.Key, out var node)
                    || !node.Kind.EndsWith("unit", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(entry.Value, new() { MaxDepth = 40 });
                details.Add(entry.Key, OwnDetail(json.RootElement, node.Kind));
            }
            var cache = new Dictionary<string, (int[] Vector, bool Absolute)>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            (int[] Vector, bool Absolute) Dimension(string id)
            {
                if (cache.TryGetValue(id, out var existing)) return existing;
                if (active.Count >= 100 || !active.Add(id)) throw new InvalidDataException("Cyclic/over-depth dimension graph: " + id);
                try
                {
                    var node = catalog.Nodes[id];
                    var detail = details[id];
                    var vector = new int[8];bool absolute = false;
                    if (node.Kind == "absolute-unit")
                    {
                        vector = (int[])Dimension(node.AbsoluteParent!).Vector.Clone();
                        absolute = true; // Sets absolute on the parent copy.
                    }
                    else if (node.Kind == "primitive-unit")
                    {
                        if (detail.DimensionTypeId is string dimension)
                        {
                            if (BaseDimensions.TryGetValue(dimension, out int index)) vector[index] = 1;
                            // The currency dimension has no native BaseUnit; the default UnitDimension is all zero.
                            else if (dimension != "autodesk.unit.dimension:currency-1.0.0")
                                throw new InvalidDataException("Unverified primitive dimension mapping: " + dimension);
                        }
                        else if (id == "autodesk.unit.unit:radians-1.0.0") vector[1] = 1;
                        else if (id == "autodesk.unit.unit:steradians-1.0.0") vector[7] = 1;
                    }
                    else if (node.Kind == "derived-unit")
                    {
                        foreach (var parent in node.UnitParents)
                        {
                            var child = Dimension(parent.TypeId).Vector;
                            // The result wraps as signed int32.
                            for (int i = 0; i < 8; i++) vector[i] = unchecked(vector[i] + child[i] * parent.Exponent);
                        }
                        // Native derived aggregation does not propagate absolute flags.
                    }
                    else throw new InvalidDataException("Non-unit dimension dependency: " + id);
                    var result = (vector, absolute);cache.Add(id,result);return result;
                }
                finally { active.Remove(id); }
            }
            var cores = new Dictionary<string, UnitCore>(StringComparer.Ordinal);
            foreach (var (id, factor) in si)
            {
                var d = Dimension(id);double offset = details[id].Offset;
                double scale = UniversalInternalScale(factor, d.Vector[0]);
                if (!double.IsFinite(offset) || !double.IsFinite(scale) || scale == 0.0)
                    throw new InvalidDataException("Nonfinite/zero unit core scale or offset: " + id);
                cores.Add(id,new(Array.AsReadOnly(d.Vector),d.Absolute,factor,offset,scale));
            }
            return cores;
        }
        catch (InvalidDataException e) { failure = e.Message; return null; }
        catch (JsonException e) { failure = "Malformed unit detail JSON: " + e.Message; return null; }
        catch (FormatException e) { failure = "Invalid numeric unit detail: " + e.Message; return null; }
        catch (InvalidOperationException e) { failure = "Invalid unit detail shape: " + e.Message; return null; }
        catch (KeyNotFoundException e) { failure = "Missing unit detail/dependency: " + e.Message; return null; }
        catch (EncoderFallbackException e) { failure = "Invalid unit detail Unicode: " + e.Message; return null; }
    }

    internal static double UniversalInternalScale(double si, int lengthExponent)
    {
        // Conversion from system 0 to 1: repeated multiplication in the native order, not pow or a stored
        // From(1) coefficient. The negated count wraps for int32.MinValue, whose native loop executes zero multiplications.
        int count = lengthExponent < 0 ? unchecked(-lengthExponent) : lengthExponent;
        if (count > 10_000) throw new InvalidDataException("Universal scale exponent outside bounded projection");
        double power = 1.0;
        double baseScale = BitConverter.UInt64BitsToDouble(0x3FD381D7DBF487FDul);
        for (int i = 0; i < count; i++) power *= baseScale;
        return lengthExponent < 0 ? power * si : si / power;
    }

    private static Dictionary<string, Detail> LoadDetails()
    {
        using var stream = typeof(UnitSchemaEvaluator).Assembly.GetManifestResourceStream("RvtIO.Resources.r22_unit_details.json")
            ?? throw new InvalidDataException("Missing source-bound unit details");
        using var json = JsonDocument.Parse(stream);var root = json.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 1
            || root.GetProperty("profile").GetString() != "Revit2022-unit-details")
            throw new InvalidDataException("Unsupported unit detail corpus");
        var result = new Dictionary<string, Detail>(StringComparer.Ordinal);
        foreach (var row in root.GetProperty("units").EnumerateArray())
        {
            string id = Identifier(row.GetProperty("typeId"));var dimension = row.GetProperty("dimensionTypeId");
            var rawOffset = row.GetProperty("absoluteOffsetBits");
            double offset = rawOffset.ValueKind == JsonValueKind.Null ? 0.0
                : BitConverter.UInt64BitsToDouble(ulong.Parse(rawOffset.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (!double.IsFinite(offset)) throw new InvalidDataException("Nonfinite built-in offset");
            result.Add(id,new(dimension.ValueKind == JsonValueKind.Null ? null : Identifier(dimension),offset));
        }
        if (result.Count != 309 || !result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
            Builtins.Value.Nodes.Values.Where(n => n.Kind.EndsWith("unit", StringComparison.Ordinal)).Select(n => n.TypeId)))
            throw new InvalidDataException("Incomplete numeric unit details");
        return result;
    }

    private static Detail OwnDetail(JsonElement record, string kind)
    {
        var constants = new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        if (record.TryGetProperty("constants", out var rows))
            foreach (var row in rows.EnumerateArray()) constants.Add(Identifier(row.GetProperty("id")),row);
        string? dimension = record.GetProperty("inherits")[0].GetString() == "autodesk.unit:unit.primitive.dimensional-1.0.0"
            ? Identifier(constants["dimension"].GetProperty("typedValue").GetProperty("typeid")) : null;
        double offset = kind == "absolute-unit" && constants.TryGetValue("offset", out var value)
            ? value.GetProperty("value").GetDouble() : 0.0; // Constructor default is zero.
        if (!double.IsFinite(offset)) throw new InvalidDataException("Nonfinite own absolute offset");
        return new(dimension,offset);
    }
}
