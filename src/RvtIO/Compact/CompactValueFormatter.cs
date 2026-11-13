using System.Globalization;
using System.Text;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>
/// Display string of a Double parameter value as the current profile label utilities write it: the value in Revit internal
/// units is converted to the unit of the project's FormatOptions of its spec, rounded to the accuracy, laid out with the
/// decimal symbol, grouping and unit symbol.
/// </summary>
internal static partial class CompactValueFormatter
{
    private const string UnitPrefix = "autodesk.unit.unit:";
    private const string Meters = UnitPrefix + "meters-1.0.0";
    private const string MetersCentimeters = UnitPrefix + "metersCentimeters-1.0.0";
    private const string FractionalInches = UnitPrefix + "fractionalInches-1.0.0";
    private const string FeetFractionalInches = UnitPrefix + "feetFractionalInches-1.0.0";
    private const string General = UnitPrefix + "general-1.0.1";
    private const string Degrees = UnitPrefix + "degrees-1.0.1";
    private const string SlopeDegrees = UnitPrefix + "slopeDegrees-1.0.0";
    private const string OneToRatio = UnitPrefix + "1ToRatio-1.0.1";
    private const string Fahrenheit = UnitPrefix + "fahrenheit-1.0.1";
    private const string Celsius = UnitPrefix + "celsius-1.0.1";
    private const string RiseDividedBy12Inches = UnitPrefix + "riseDividedBy12Inches-1.0.1";
    private const string RiseDividedBy120Inches = UnitPrefix + "riseDividedBy120Inches-1.0.1";
    private const string RiseDividedBy1Foot = UnitPrefix + "riseDividedBy1Foot-1.0.1";
    private const string RiseDividedBy10Feet = UnitPrefix + "riseDividedBy10Feet-1.0.1";
    private const string RiseDividedBy1000Millimeters = UnitPrefix + "riseDividedBy1000Millimeters-1.0.1";
    private const string Millimeters = UnitPrefix + "millimeters-1.0.1";

    private const double RoundTolerance = 1e-9;
    private const double SignThreshold = -1e-10;

    /// <summary>The state one layout routine works from.</summary>
    private sealed class Layout
    {
        public double Value, Accuracy;
        public string Unit = "", Symbol = "";
        public bool Grouping, SuppressSpaces, SuppressLeadingZeros, Editing, PlusPrefix, SuppressTrailingZeros;
        /// <summary>Set only by the rise / run layout for the run number of the 10 feet and 1000 millimeters forms.</summary>
        public bool MinimalDisplay;
        public char Decimal = '.', Group = ',';
        public int Rounding, GroupAmount;
    }

    /// <summary>Formats a value in Revit internal units with the options of <paramref name="spec"/>.</summary>
    internal static string? Format(double internalValue, string spec, CompactFormatSettings settings, bool editing = false)
        => settings.Find(spec) is { } option ? Format(internalValue, option, settings, editing) : null;

    internal static string? Format(double internalValue, TfBlockDeserializer.UnitFormatOption option,
        CompactFormatSettings settings, bool editing = false)
        => Format(internalValue, option, settings, editing, minimalDisplay: false);

    private static string? Format(double internalValue, TfBlockDeserializer.UnitFormatOption option,
        CompactFormatSettings settings, bool editing, bool minimalDisplay)
    {
        if (double.IsNaN(internalValue)) return null;
        double accuracy = option.Accuracy;
        if (accuracy < SignThreshold) return internalValue.ToString("F6", CultureInfo.InvariantCulture);
        string unit = option.Unit;
        if (unit == MetersCentimeters) unit = Meters; // the dimension-only form is never requested
        if (!TryConvertFromInternal(internalValue, unit, settings.Conversions, out double converted)) return null;
        if (double.IsNaN(converted)) return null;
        if (Math.Abs(converted) >= 1e30) return "???";

        var layout = new Layout
        {
            Value = converted, Accuracy = accuracy, Unit = unit, Symbol = option.Symbol,
            Grouping = option.UseGrouping != 0, SuppressSpaces = option.SuppressSpaces != 0,
            SuppressLeadingZeros = option.SuppressLeadingZeros != 0, Editing = editing,
            PlusPrefix = option.UsePlusPrefix != 0 && !editing,
            MinimalDisplay = minimalDisplay,
            Decimal = settings.Options.DecimalSymbol == 0 ? '.' : ',',
            Group = settings.Options.DigitGroupingSymbol switch { 1 => ',', 2 => ' ', 3 => '\'', _ => '.' },
            Rounding = option.RoundingMethod,
            GroupAmount = settings.Options.DigitGroupingAmount,
        };
        if (unit is FractionalInches or FeetFractionalInches) layout.SuppressTrailingZeros = true;
        else if (unit == General)
        {
            layout.Accuracy = 1e-6;
            layout.SuppressTrailingZeros = true;
        }
        else layout.SuppressTrailingZeros = minimalDisplay || option.SuppressTrailingZeros != 0 && !editing;

        int dash = unit.IndexOf('-');
        if (SpecialUnits.Contains(dash < 0 ? unit : unit.Substring(0, dash)))
        {
            return unit switch
            {
                FractionalInches or FeetFractionalInches => FormatFractional(layout, settings),
                RiseDividedBy12Inches or RiseDividedBy120Inches or RiseDividedBy1Foot or RiseDividedBy10Feet
                    or RiseDividedBy1000Millimeters => FormatRise(layout, settings),
                DegreesMinutes or StationingFeet or StationingMeters or StationingSurveyFeet => FormatSpecial(layout, settings),
                General or Fahrenheit or Celsius or SlopeDegrees or OneToRatio => FormatDecimal(layout, settings),
                _ => null,
            };
        }
        return FormatDecimal(layout, settings);
    }

    private static readonly HashSet<string> SpecialUnits = new string[]
    {
        FractionalInches, FeetFractionalInches, General, Fahrenheit, Celsius, SlopeDegrees, OneToRatio,
        RiseDividedBy12Inches, RiseDividedBy120Inches, RiseDividedBy1Foot, RiseDividedBy10Feet,
        RiseDividedBy1000Millimeters, DegreesMinutes, StationingFeet, StationingMeters, StationingSurveyFeet,
    }.Select(UnitSchemaEvaluator.DisplayNameKey).ToHashSet(StringComparer.Ordinal);

    private static bool TryConvertFromInternal(double value, string unit,
        UnitSchemaEvaluator.UnitConversions conversions, out double result)
    {
        result = 0;
        if (unit == Fahrenheit) { result = value * 9.0 / 5.0 - 459.67; return true; }
        if (unit == Celsius) { result = value - 273.15; return true; }
        if (unit == SlopeDegrees) { value = Math.Atan(value); unit = Degrees; }
        if (unit == OneToRatio)
        {
            result = Math.Abs(value) > 1e-10 ? 1.0 / value : 0.0;
            return true;
        }
        if (conversions.GetLegacy(unit) is not { } info || info.FullId != unit || info.Core.Offset != 0.0) return false;
        result = value / info.Core.InternalScale;
        return true;
    }

    private static bool TryConvertToInternal(double value, string unit,
        UnitSchemaEvaluator.UnitConversions conversions, out double result)
    {
        result = 0;
        if (conversions.GetLegacy(unit) is not { } info || info.FullId != unit || info.Core.Offset != 0.0
            || unit is Fahrenheit or Celsius or SlopeDegrees or OneToRatio) return false;
        result = value * info.Core.InternalScale;
        return true;
    }

    private const ulong X64IndefiniteNaN = 0xFFF8_0000_0000_0000UL;
    private const ulong PositiveQuietNaN = 0x7FF8_0000_0000_0000UL;

    internal static double RoundToAccuracy(double value, double accuracy, int method, double tolerance = RoundTolerance)
    {
        if (-tolerance > accuracy) return value;
        bool negative = false;
        if (SignThreshold > value) { value = -value; negative = true; }
        double steps = value / accuracy;
        switch (method)
        {
            case 0: steps = Math.Floor(steps + 0.5 + tolerance); break;
            case 1: steps = Math.Ceiling(steps - tolerance); break;
            case 2: steps = Math.Floor(steps + tolerance); break;
            default: return negative ? -value : value;
        }
        double rounded = steps * accuracy;
        // Match the x64 floating-point indefinite QNaN sign bit (0xFFF8000000000000) when zero-accuracy
        // division or infinity*zero produces NaN, instead of ARM64's positive default QNaN (0x7FF8000000000000).
        if (double.IsNaN(rounded) && !double.IsNaN(value) && !double.IsNaN(accuracy))
            return BitConverter.UInt64BitsToDouble(negative ? PositiveQuietNaN : X64IndefiniteNaN);
        return negative ? -rounded : rounded;
    }

    /// <summary>The number of places after the point (at most <paramref name="maximum"/>) a value
    /// needs so that rounding it to that many places stays within half a place of the maximum.</summary>
    internal static int CountPlaces(double value, double radix, int maximum)
    {
        if (maximum <= 0) return 0;
        double half = Math.Pow(radix, -maximum) * 0.5;
        int places = 0, exponent = 0;
        do
        {
            double step = Math.Pow(radix, exponent);
            double rounded = value;
            if (step >= 1e-12)
            {
                rounded = value >= 0.0 ? Math.Floor(value / step + 0.5 + RoundTolerance) * step
                    : -(Math.Floor(0.5 - value / step + RoundTolerance) * step);
            }
            if (half > Math.Abs(rounded - value)) break;
            places++; exponent--;
        } while (places < maximum);
        return places;
    }

    private static string? FormatDecimal(Layout layout, CompactFormatSettings settings)
    {
        double rounded = RoundToAccuracy(layout.Value, layout.Accuracy, layout.Rounding);
        bool negative = SignThreshold > rounded;
        if (negative) rounded = -rounded;
        int places = CountPlaces(layout.Accuracy, 10.0, 12);
        if (layout.SuppressTrailingZeros) places = CountPlaces(rounded, 10.0, places);
        string text = CompactFixedNumber.Format(rounded, places);
        int dot = text.IndexOf('.');
        string whole = dot < 0 ? text : text[..dot];
        string fraction = dot < 0 ? "" : layout.Decimal + text[(dot + 1)..];
        if (layout.Grouping) whole = Group(whole, layout);
        string body = whole + fraction;
        if (!TryAttachSymbol(body, layout.Symbol, settings, out body)) return null;
        return Sign(body, negative, layout);
    }

    private static string Sign(string body, bool negative, Layout layout)
        => negative ? "-" + body : layout.PlusPrefix ? "+" + body : body;

    /// <summary>Digit groups: the last three digits, then groups of three (or two) to the left.
    /// Nothing is grouped while editing or for at most three characters.</summary>
    private static string Group(string digits, Layout layout)
    {
        if (layout.Editing || digits.Length <= 3) return digits;
        var tail = new StringBuilder().Append(layout.Group).Append(digits, digits.Length - 3, 3);
        int size = layout.GroupAmount != 0 ? 3 : 2;
        int remaining = digits.Length - 3;
        string head = digits[..remaining];
        while (remaining > size)
        {
            tail.Insert(0, layout.Group + head[^size..]);
            remaining -= size;
            head = head[..remaining];
        }
        return head + tail;
    }

    /// <summary>Adds the unit symbol on its side, with a space when the symbol asks for one.
    /// A symbol without a label adds nothing; an unknown symbol is not guessed.</summary>
    private static bool TryAttachSymbol(string text, string symbol, CompactFormatSettings settings, out string result)
    {
        result = text;
        if (symbol.Length == 0) return true;
        if (!settings.Symbols.TryGetValue(symbol, out var label)) return false;
        if (label.Text.Length == 0) return true;
        string marked = label.HasSpace ? (label.IsPrefix ? label.Text + " " : " " + label.Text) : label.Text;
        result = label.IsPrefix ? marked + text : text + marked;
        return true;
    }

    /// <summary>The x64 truncating conversion (cvttsd2si): a value outside the range gives the
    /// "integer indefinite" minimum instead of saturating.</summary>
    private static long Truncate64(double value)
        => double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0 ? long.MinValue : (long)value;

    private static int Truncate32(double value)
        => double.IsNaN(value) || value >= 2147483648.0 || value <= -2147483649.0 ? int.MinValue : (int)value;

    private static string? Label(CompactFormatSettings settings, string symbol)
        => settings.Symbols.TryGetValue(symbol, out var label) ? label.Text : null;
}
