using System.Collections.ObjectModel;
using System.Globalization;

namespace RvtIO.Parsing;

internal static partial class UnitSchemaEvaluator
{
    /// <summary>Numeric fields only, not the native display name/label/system object.</summary>
    internal sealed record NumericUnitInfo(string FullId, UnitCore Core);

    /// <summary>Normal-name and exact legacy lookup. Numeric conversion
    /// targets native system 0 to 1; formatting, current-option selection and IFC units are separate.</summary>
    internal sealed class UnitConversions
    {
        public IReadOnlyDictionary<string, NumericUnitInfo> NormalByName { get; }
        public IReadOnlyDictionary<string, NumericUnitInfo> LegacyExact { get; }

        internal UnitConversions(Dictionary<string, NumericUnitInfo> normal, Dictionary<string, NumericUnitInfo> legacy)
        {
            NormalByName = new ReadOnlyDictionary<string, NumericUnitInfo>(normal);
            LegacyExact = new ReadOnlyDictionary<string, NumericUnitInfo>(legacy);
        }

        public NumericUnitInfo? GetNormal(string id)
            => ValidLookupId(id) && NormalByName.TryGetValue(DisplayNameKey(id), out var info) ? info : null;
        public NumericUnitInfo? GetLegacy(string id)
            => ValidLookupId(id) && LegacyExact.TryGetValue(id, out var info) ? info : GetNormal(id);

        public bool TryFromInternal(double value, string id, out double result)
        {
            result = default;
            if (!double.IsFinite(value) || GetLegacy(id) is not { } info) return false;
            // Special tests use the requested ForgeTypeId name, before division and subtraction; this is not value*From(1).
            string name = DisplayNameKey(id);
            if (name == SlopeName) value = Math.Atan(value);
            if (name == RatioName && Math.Abs(value) > Epsilon) value = 1.0 / value;
            double converted = value / info.Core.InternalScale - info.Core.Offset;
            if (!double.IsFinite(converted)) return false;
            result = converted; return true;
        }

        public bool TryToInternal(double value, string id, out double result)
        {
            result = default;
            if (!double.IsFinite(value) || GetLegacy(id) is not { } info) return false;
            double converted = info.Core.InternalScale * (value + info.Core.Offset);
            if (!double.IsFinite(converted)) return false;
            string name = DisplayNameKey(id);
            if (name == SlopeName)
            {
                // Either near-vertical interval snaps to the FIRST lower bound, including the negative-angle case.
                // Use the exact native tan(0x3FF921445170E214) bits directly so ARM64 libm does not drift by 1 ULP.
                if (AngleBetween(Bits(0x3FF921445170E214), Bits(0x3FF922B25717780D), converted)
                    || AngleBetween(Bits(0x4012D94EBE7E4F0E), Bits(0x4012D9AA3FE7F48B), converted))
                    converted = Bits(0x40B66193F0CCDEE8);
                else
                    converted = Math.Tan(converted);
            }
            if (name == RatioName && Math.Abs(converted) > Epsilon) converted = 1.0 / converted;
            if (!double.IsFinite(converted)) return false;
            result = converted; return true;
        }
    }

    private const string SlopeName = "autodesk.unit.unit:slopeDegrees";
    private const string RatioName = "autodesk.unit.unit:1ToRatio";
    private static double Epsilon => Bits(0x3E112E0BE826D695);
    private static double Bits(ulong value) => BitConverter.UInt64BitsToDouble(value);
    private static bool ValidLookupId(string id) => !string.IsNullOrEmpty(id) && !id.Contains('\0');

    // Compares ordinal UTF16 up to the FIRST '-' character.
    // This identity is separate from the exact full-key SDK registration gate.
    internal static string DisplayNameKey(string id)
    {
        int at = id.IndexOf('-'); return at < 0 ? id : id[..at];
    }

    private static readonly Lazy<IReadOnlyDictionary<string, NumericUnitInfo>> BuiltinDisplay = new(LoadDisplay);
    private static readonly (string Id, string Parent)[] DisplayFormats =
    [
        ("feetFractionalInches-1.0.0", "feet-1.0.1"),
        ("fractionalInches-1.0.0", "inches-1.0.1"),
        ("metersCentimeters-1.0.0", "meters-1.0.0"),
        ("degreesMinutes-1.0.0", "degrees-1.0.1"),
        ("slopeDegrees-1.0.0", "degrees-1.0.1"),
        ("stationingFeet-1.0.0", "feet-1.0.1"),
        ("stationingMeters-1.0.0", "meters-1.0.0"),
        ("stationingSurveyFeet-1.0.0", "usSurveyFeet-1.0.0"),
    ]; // Clones the actual parent components, not measured coefficients.

    private static NumericUnitInfo DisplayInfo(string id, UnitCore core)
    {
        string name = DisplayNameKey(id);
        var dimensions = name == SlopeName ? Array.AsReadOnly(new int[8]) : core.Dimensions;
        bool absolute = name != SlopeName && core.Absolute;
        double si = name == "autodesk.unit.unit:usGallonsPerMinute"
            ? Bits(0x3F1089E961FEAEBA) : core.SiFactor;
        double scale = UniversalInternalScale(si, dimensions[0]);
        if (!double.IsFinite(scale) || scale == 0.0) throw new InvalidDataException("Invalid display numeric scale: " + id);
        return new(id, new(dimensions, absolute, si, core.Offset, scale));
    }

    private static IReadOnlyDictionary<string, NumericUnitInfo> LoadDisplay()
    {
        var cores = EvaluateCore(new(0, 0, []), out string? failure)
            ?? throw new InvalidDataException("SDK display components unavailable: " + failure);
        var normal = new Dictionary<string, NumericUnitInfo>(StringComparer.Ordinal);
        foreach (var (id, core) in cores.OrderBy(x => x.Key, KeyOrder))
            normal.Add(DisplayNameKey(id), DisplayInfo(id, core));
        foreach (var (id, parent) in DisplayFormats)
        {
            string full = "autodesk.unit.unit:" + id;
            normal.Add(DisplayNameKey(full), DisplayInfo(full, cores["autodesk.unit.unit:" + parent]));
        }
        return new ReadOnlyDictionary<string, NumericUnitInfo>(normal);
    }

    internal static UnitConversions? EvaluateDisplay(TfBlockDeserializer.DocumentSchemaMap own, out string? failure)
    {
        var cores = EvaluateCore(own, out failure);
        if (cores == null) return null;
        try
        {
            var normal = new Dictionary<string, NumericUnitInfo>(BuiltinDisplay.Value, StringComparer.Ordinal);
            var legacy = new Dictionary<string, NumericUnitInfo>(StringComparer.Ordinal);
            foreach (var (id, core) in cores.OrderBy(x => x.Key, KeyOrder))
            {
                string name = DisplayNameKey(id);
                // The predicate applies only to update, NOT SDK seeding.
                if (name is "autodesk.unit.unit:grams" or "autodesk.unit.unit:teslas") continue;
                if (normal.TryGetValue(name, out var current))
                {
                    if (current.FullId == id || legacy.ContainsKey(id)) continue;
                    var candidate = DisplayInfo(id, core);
                    if (NewerDisplayVersion(id, current.FullId))
                    {
                        legacy.Add(current.FullId, current);
                        // Assigns ALL fields, including UnitDimension (array AND absolute flag).
                        normal[name] = candidate;
                    }
                    else legacy.Add(id, candidate);
                }
                else normal.Add(name, DisplayInfo(id, core));
            }
            return new(normal, legacy);
        }
        catch (InvalidDataException e) { failure = e.Message; return null; }
    }

    // Version tokens: first dash, dot tokens, base-10 strtoul and unsigned comparisons. Only the unsigned-decimal
    // one-to-three-component subset is accepted; unsupported collisions reject the projection,
    // rather than inventing .NET Version/semver behavior.
    internal static bool NewerDisplayVersion(string candidate, string current)
    {
        static uint[] Parts(string id)
        {
            int dash = id.IndexOf('-');
            if (dash < 0) throw new InvalidDataException("Display version collision without a version: " + id);
            var tokens = id[(dash + 1)..].Split('.');
            if (tokens.Length is < 1 or > 3) throw new InvalidDataException("Unverified display version grammar: " + id);
            var result = new uint[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                if (tokens[i].Length == 0 || tokens[i].Any(c => c < '0' || c > '9')
                    || !uint.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out result[i]))
                    throw new InvalidDataException("Unverified display version token: " + id);
            return result;
        }
        var a = Parts(candidate); var b = Parts(current);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i] > b[i];
        return a.Length > b.Length;
    }

    private static bool NearAngle(double a, double b)
    {
        double sum = Math.Abs(a) + Math.Abs(b);
        return sum < Epsilon || Math.Abs(a - b) <= sum * Epsilon;
    }

    private static bool AngleBetween(double first, double last, double value)
    {
        double period = Bits(0x401921FB54442D11); // Not Math.PI*2.
        if (NearAngle(first, 0.0) && NearAngle(last, period)) return true;
        double bound = PutInAngleRange(last - first);
        double normalized = PutInAngleRange(value - first);
        double tail = PutInAngleRange(normalized + Epsilon);
        return normalized >= -Epsilon && normalized <= bound + Epsilon || tail >= 0.0 && bound >= tail;
    }

    private static double PutInAngleRange(double value)
    {
        // Null optional output pointers. Preserve floor/operation order and the native PI bits (different from Math.PI).
        double center = Bits(0x400921FB54442D11), period = Bits(0x401921FB54442D11);
        double half = period * Bits(0x3FE0000000000000);
        double minimum = center - half, maximum = half + center, turns = 0.0;
        if (minimum > value) turns = (Math.Floor((minimum - value) / period) + 1.0) + turns;
        if (value >= maximum) turns -= Math.Floor((value - maximum) / period) + 1.0;
        double result = value + turns * period;
        if (!double.IsFinite(result)) return double.NaN; // Outside bounded numeric projection.
        // Warning path returns the supplied center.
        return result > maximum + Epsilon || result < minimum - Epsilon ? center : result;
    }
}
