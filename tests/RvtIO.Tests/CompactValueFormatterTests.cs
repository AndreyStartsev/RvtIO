using System.Globalization;
using RvtIO.Compact;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The display string of a Double parameter (CompactValueFormatter) against the output of the current profile
/// label utilities. <see cref="CompactFormatVectors"/> holds reference rows: parameter values of six sample documents with
/// their real FormatOptions and rows of a sweep of units, accuracies, flags, rounding methods and separators.</summary>
public class CompactValueFormatterTests
{
    private const string UnitPrefix = "autodesk.unit.unit:";
    private const string SymbolPrefix = "autodesk.unit.symbol:";
    private const string Spec = "autodesk.spec.aec:length-2.0.1";
    private static readonly UnitSchemaEvaluator.UnitConversions Sdk = UnitSchemaEvaluator.EvaluateDisplay(
        new(0, 0, []), out _) ?? throw new InvalidDataException("SDK numeric display unavailable");
    private static readonly Dictionary<string, CompactSymbolLabel> Symbols = ParseSymbols(CompactFormatVectors.Symbols);

    private static Dictionary<string, CompactSymbolLabel> ParseSymbols(string table)
    {
        var symbols = new Dictionary<string, CompactSymbolLabel>();
        foreach (var line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('|');
            symbols.Add(SymbolPrefix + p[0], new(p[1], p[2] == "Prefix", p[3] == "true"));
        }
        return symbols;
    }

    private static CompactFormatSettings Settings(TfBlockDeserializer.UnitFormatOption option, int decimalSymbol = 0,
        int groupSymbol = 1, int groupAmount = 1)
        => new(new TfBlockDeserializer.UnitOptions([option], 1, decimalSymbol, groupSymbol, groupAmount), Sdk, Symbols);

    private static TfBlockDeserializer.UnitFormatOption Option(string unit, double accuracy = 1, string symbol = "",
        int rounding = 0, string flags = "00000")
        => new(Spec, symbol.Length == 0 ? "" : SymbolPrefix + symbol, UnitPrefix + unit, accuracy, rounding,
            Flag(flags[0]), Flag(flags[1]), Flag(flags[2]), 0, Flag(flags[3]), Flag(flags[4]));

    private static byte Flag(char c) => (byte)(c == '1' ? 1 : 0);

    private static string? Format(double value, string unit, double accuracy = 1, string symbol = "", string flags = "00000",
        int rounding = 0, int decimalSymbol = 0, int groupSymbol = 1, int groupAmount = 1, bool editing = false)
    {
        var option = Option(unit, accuracy, symbol, rounding, flags);
        return CompactValueFormatter.Format(value, option, Settings(option, decimalSymbol, groupSymbol, groupAmount), editing);
    }

    public static IEnumerable<object[]> Rows()
    {
        foreach (var line in CompactFormatVectors.Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            yield return [line.TrimEnd('\r')];
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void MatchesTheReferenceOutput(string row)
    {
        var f = row.Split('|');
        var separators = f[5].Split(',');
        string? ours = Format(double.Parse(f[6], CultureInfo.InvariantCulture), f[0], double.Parse(f[2], CultureInfo.InvariantCulture),
            f[1] == "-" ? "" : f[1], f[3], int.Parse(f[4]), int.Parse(separators[0]), int.Parse(separators[1]), int.Parse(separators[2]),
            editing: f.Length > 8 && f[8] == "1");
        Assert.Equal(f[7], ours);
    }

    [Fact]
    public void VectorTableCoversEveryUnitKindOfTheFormatter()
    {
        var units = CompactFormatVectors.Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('|')[0]).ToHashSet();
        foreach (var unit in new[] { "millimeters-1.0.1", "squareMeters-1.0.1", "cubicMeters-1.0.1", "degrees-1.0.1", "general-1.0.1",
                     "percentage-1.0.1", "currency-1.0.0", "slopeDegrees-1.0.0", "1ToRatio-1.0.1", "celsius-1.0.1", "fahrenheit-1.0.1",
                     "fractionalInches-1.0.0", "feetFractionalInches-1.0.0", "degreesMinutes-1.0.0", "stationingFeet-1.0.0",
                     "stationingMeters-1.0.0", "stationingSurveyFeet-1.0.0", "riseDividedBy12Inches-1.0.1", "riseDividedBy120Inches-1.0.1",
                     "riseDividedBy1Foot-1.0.1", "riseDividedBy10Feet-1.0.1", "riseDividedBy1000Millimeters-1.0.1", "metersCentimeters-1.0.0" })
            Assert.Contains(unit, units);
        Assert.True(CompactFormatVectors.Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 300);
    }

    [Theory]
    [InlineData(0.5, 1.0, 0, 1.0)]
    [InlineData(-0.5, 1.0, 0, -1.0)]
    [InlineData(2.5, 1.0, 0, 3.0)]
    [InlineData(2.4999, 1.0, 0, 2.0)]
    [InlineData(2.5, 1.0, 1, 3.0)]
    [InlineData(2.0, 1.0, 1, 2.0)]
    [InlineData(2.5, 1.0, 2, 2.0)]
    [InlineData(-2.5, 1.0, 2, -2.0)]
    [InlineData(3.7, 1.0, 5, 3.7)]
    public void RoundingMethodsOfTheAccuracy(double value, double accuracy, int method, double expected)
        => Assert.Equal(expected, CompactValueFormatter.RoundToAccuracy(value, accuracy, method));

    [Fact]
    public void ToleranceIsOneBillionthOfAStepAndRoundsAHalfStepUp()
    {
        // 0.495 mm is a tie of 0.01; the double nearest to it is below the tie, the 1e-9 step tolerance lifts it.
        Assert.Equal(0.5, CompactValueFormatter.RoundToAccuracy(0.495, 0.01, 0), 12);
        Assert.Equal(0.49, CompactValueFormatter.RoundToAccuracy(0.49 + 0.5e-2 - 2e-9 * 0.01, 0.01, 0), 12);
        Assert.Equal(0.50, CompactValueFormatter.RoundToAccuracy(0.49 + 0.5e-2 - 0.9e-9 * 0.01, 0.01, 0), 12);
        // a value just below a negative tie keeps its sign and is rounded on its magnitude
        Assert.Equal(-0.5, CompactValueFormatter.RoundToAccuracy(-0.495, 0.01, 0), 12);
        // a negative value inside the sign threshold is rounded as positive: +0, not -0
        Assert.False(double.IsNegative(CompactValueFormatter.RoundToAccuracy(-1e-11, 1, 0)));
        Assert.True(double.IsNegative(CompactValueFormatter.RoundToAccuracy(-0.1, 1, 0)));
    }

    [Theory]
    [InlineData(1.0, 10.0, 12, 0)]
    [InlineData(0.01, 10.0, 12, 2)]
    [InlineData(0.0001, 10.0, 12, 4)]
    [InlineData(0.5, 10.0, 12, 1)]
    [InlineData(0.25, 10.0, 12, 2)]
    [InlineData(100.0, 10.0, 12, 0)]
    [InlineData(1e-6, 10.0, 12, 6)]
    [InlineData(1.0 / 384.0, 10.0, 12, 12)]
    [InlineData(0.0, 10.0, 12, 0)]
    [InlineData(1.0 / 32.0, 2.0, 20, 5)]
    [InlineData(1.0 / 12.0, 2.0, 20, 20)]
    public void PlacesNeededByAnAccuracy(double value, double radix, int maximum, int expected)
        => Assert.Equal(expected, CompactValueFormatter.CountPlaces(value, radix, maximum));

    [Theory]
    [InlineData(0.0, 0, "0")]
    [InlineData(0.0, 2, "0.00")]
    [InlineData(-0.0, 0, "-0")]
    [InlineData(-0.0, 2, "-0.00")]
    [InlineData(123.456, 5, "123.45600")]
    [InlineData(0.000123, 3, "0.000")]
    [InlineData(0.0005, 3, "0.001")]
    [InlineData(0.005208333333333333, 12, "0.005208333333")]
    [InlineData(999999999999999.9, 1, "999999999999999.9")]
    [InlineData(9.999999999999999e17, 1, "999999999999999900.0")]
    [InlineData(1e22, 0, "10000000000000000000000")]
    [InlineData(3280839895013123.5, 2, "3280839895013123.50")]
    [InlineData(12345.678, 0, "12346")]
    [InlineData(0.07, 2, "0.07")]
    public void FixedNotationTextWithAFixedNumberOfPlaces(double value, int places, string expected)
        => Assert.Equal(expected, CompactFixedNumber.Format(value, places));

    [Fact]
    public void EditingFormTurnsGroupingAndPlusPrefixOffAndWidensTheFeetSeparator()
    {
        double value = 1234567.891 / 304.8;
        Assert.Equal("1234567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00011", editing: true));
        Assert.Equal("1,234,567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00010"));
        Assert.Equal("24'  0\"", Format(24, "feetFractionalInches-1.0.0", 1.0 / 384.0, editing: true));
        Assert.Equal("24' - 0\"", Format(24, "feetFractionalInches-1.0.0", 1.0 / 384.0));
    }

    [Fact]
    public void ANegativeValueThatRoundsToZeroPrintsItsSignOnlyBelowTheThreshold()
    {
        Assert.Equal("-0 m²", Format(-0.0929, "squareMeters-1.0.1", symbol: "mSup2-1.0.1"));
        Assert.Equal("0", Format(-3e-12 * 0.0032808398950131233, "millimeters-1.0.1"));
        Assert.Equal("-0 mm", Format(-0.1 * 0.0032808398950131233, "millimeters-1.0.1", symbol: "mm-1.0.1"));
        Assert.Equal("+-0 mm", Format(-0.1 * 0.0032808398950131233, "millimeters-1.0.1", symbol: "mm-1.0.1", flags: "00001"));
    }

    [Fact]
    public void ValuesBeyondOneE30AreThreeQuestionMarks()
    {
        Assert.Equal("???", Format(1e30, "millimeters-1.0.1"));
        Assert.Equal("???", Format(-1e29, "millimeters-1.0.1"));
        Assert.Equal("???", Format(-1e32, "squareMeters-1.0.1", symbol: "mSup2-1.0.1"));
        Assert.NotEqual("???", Format(1e27, "millimeters-1.0.1"));
    }

    [Theory]
    [InlineData(-1.0, 1234.5678, "1234.567800")]
    [InlineData(-1.1e-10, -0.0929, "-0.092900")]
    [InlineData(-5e-10, 0.0, "0.000000")]
    public void AnAccuracyBelowTheToleranceFallsBackToTheRawNumber(double accuracy, double value, string expected)
        => Assert.Equal(expected, Format(value, "squareMeters-1.0.1", accuracy, "mSup2-1.0.1"));

    [Theory]
    [InlineData(0.0, 1234.5678, "-NaN m²")]
    [InlineData(0.0, -0.0929, "NaN m²")]
    [InlineData(-1e-11, 1234.5678, "114.69510170611 m²")]
    [InlineData(-1e-10, 0.0, "-0.0000000000 m²")]
    [InlineData(1e-15, -0.0929, "-0 m²")]
    [InlineData(1e300, 1234.5678, "0 m²")]
    [InlineData(5e-324, 1234.5678, "Infinity m²")]
    [InlineData(5e-324, -0.0929, "-Infinity m²")]
    public void DegenerateAccuraciesPrintWhatPrints(double accuracy, double value, string expected)
        => Assert.Equal(expected, Format(value, "squareMeters-1.0.1", accuracy, "mSup2-1.0.1"));

    [Fact]
    public void ZeroAccuracyProducesX64IndefiniteNaNSignBit()
    {
        Assert.Equal(0xFFF8_0000_0000_0000UL, BitConverter.DoubleToUInt64Bits(CompactValueFormatter.RoundToAccuracy(1.0, 0.0, 0)));
        Assert.Equal(0xFFF8_0000_0000_0000UL, BitConverter.DoubleToUInt64Bits(CompactValueFormatter.RoundToAccuracy(0.0, 0.0, 0)));
        Assert.Equal(0x7FF8_0000_0000_0000UL, BitConverter.DoubleToUInt64Bits(CompactValueFormatter.RoundToAccuracy(-1.0, 0.0, 0)));
    }

    [Fact]
    public void GeneralUnitDropsTrailingZeros()
    {
        Assert.Equal("0.1", Format(0.1, "general-1.0.1", accuracy: 1e-6));
        Assert.Equal("3.141593", Format(Math.PI, "general-1.0.1", accuracy: 1e-6));
        Assert.Equal("1000000", Format(1e6, "general-1.0.1", accuracy: 1e-6));
    }

    [Fact]
    public void DecimalSymbolAndDigitGroupsFollowTheDocumentSettings()
    {
        double value = 1234567.891 / 304.8; // feet -> 1234567.891 mm
        Assert.Equal("1234567,89", Format(value, "millimeters-1.0.1", 0.01, decimalSymbol: 1));
        Assert.Equal("1,234,567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00010"));
        Assert.Equal("1.234.567,89", Format(value, "millimeters-1.0.1", 0.01, flags: "00010", decimalSymbol: 1, groupSymbol: 0));
        Assert.Equal("12,34,567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00010", groupAmount: 0));
        Assert.Equal("1 234 567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00010", groupSymbol: 2));
        Assert.Equal("1'234'567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00010", groupSymbol: 3));
        Assert.Equal("+1234567.89", Format(value, "millimeters-1.0.1", 0.01, flags: "00001"));
        Assert.Equal("123", Format(123 / 304.8, "millimeters-1.0.1", 1, flags: "00010"));
    }

    [Fact]
    public void TrailingZerosAreDroppedOnlyWhenAsked()
    {
        Assert.Equal("304.8", Format(1.0, "millimeters-1.0.1", 0.01, flags: "00100"));
        Assert.Equal("304.80", Format(1.0, "millimeters-1.0.1", 0.01));
        Assert.Equal("305", Format(1.0, "millimeters-1.0.1", 1, flags: "00100"));
    }

    [Fact]
    public void SymbolsAreAttachedOnTheirSideWithOrWithoutASpace()
    {
        Assert.Equal("150 mm", Format(0.4921259842519685, "millimeters-1.0.1", symbol: "mm-1.0.1"));
        Assert.Equal("45.00°", Format(0.7853981633974483, "degrees-1.0.1", 0.01, "degree-1.0.1"));
        Assert.Equal("$3.00", Format(3, "currency-1.0.0", 0.01, "usDollar-1.0.0"));
        Assert.Equal("100.00%", Format(1, "percentage-1.0.1", 0.01, "percent-1.0.1"));
    }

    [Fact]
    public void FractionalUnitsUseTheFootAndInchMarksOfTheDocument()
    {
        Assert.Equal("1' - 0\"", Format(1, "feetFractionalInches-1.0.0", 1.0 / 384.0));
        Assert.Equal("27' - 2 5/16\"", Format(27.0 + 2.3125 / 12, "feetFractionalInches-1.0.0", 1.0 / 384.0));
        Assert.Equal("-0' - 6\"", Format(-0.5, "feetFractionalInches-1.0.0", 1.0 / 384.0));
        Assert.Equal("3/32\"", Format(0.0078125, "fractionalInches-1.0.0", 1.0 / 128.0));
        Assert.Equal("0\"", Format(0, "fractionalInches-1.0.0", 1.0 / 128.0));
    }

    [Fact]
    public void ADocumentWithoutTheFootMarkCannotFormatFeetAndInches()
    {
        var option = Option("feetFractionalInches-1.0.0", 1.0 / 384.0);
        var withoutMarks = new CompactFormatSettings(new TfBlockDeserializer.UnitOptions([option], 1, 0, 1, 1), Sdk,
            new Dictionary<string, CompactSymbolLabel>());
        Assert.Null(CompactValueFormatter.Format(1, option, withoutMarks));
    }

    [Theory]
    [InlineData("kilometersPerHour-1.0.0")] // a unit id the document does not know
    [InlineData("feetFractionalInches-1.0.1")] // a special unit under a version the expected export does not special-case
    [InlineData("fractionalInches-1.0.1")]
    [InlineData("general-1.0.0")]
    [InlineData("stationingFeet-1.0.1")]
    [InlineData("riseDividedBy12Inches-1.0.0")]
    [InlineData("slopeDegrees-1.0.1")]
    [InlineData("celsius-1.0.0")]
    [InlineData("shaku-1.0.0")]
    public void UnitsWithoutAValidatedLayoutAreNotFormatted(string unit)
        => Assert.Null(Format(1, unit, 0.01));

    [Fact]
    public void AnUnknownSymbolIsNotGuessed()
    {
        var option = Option("millimeters-1.0.1", 1, "neverHeardOf-1.0.0");
        Assert.Null(CompactValueFormatter.Format(1, option, Settings(option)));
    }

    [Fact]
    public void InfiniteValuesAreThreeQuestionMarksAndNaNIsNotFormatted()
    {
        Assert.Equal("???", Format(double.PositiveInfinity, "millimeters-1.0.1"));
        Assert.Equal("???", Format(double.NegativeInfinity, "squareMeters-1.0.1", symbol: "mSup2-1.0.1"));
        Assert.Equal("???", Format(double.MaxValue, "feetFractionalInches-1.0.0", 1.0 / 384.0));
        Assert.Null(Format(double.NaN, "millimeters-1.0.1"));
    }

    [Fact]
    public void ASpecIsLookedUpByItsNameWithoutTheVersion()
    {
        var option = Option("millimeters-1.0.1", 1, "mm-1.0.1") with { Spec = "autodesk.spec.aec:length-1.0.0" };
        var settings = Settings(option);
        Assert.Equal("150 mm", CompactValueFormatter.Format(0.4921259842519685, "autodesk.spec.aec:length-2.0.1", settings));
        Assert.Equal("150 mm", CompactValueFormatter.Format(0.4921259842519685, "autodesk.spec.aec:length", settings));
        Assert.Null(CompactValueFormatter.Format(0.4921259842519685, "autodesk.spec.aec:area-2.0.0", settings));
    }

    [Fact]
    public void TemperatureScalesHaveTheirOwnFormulas()
    {
        Assert.Equal("26.85", Format(300, "celsius-1.0.1", 0.01));
        Assert.Equal("80.33", Format(300, "fahrenheit-1.0.1", 0.01));
        Assert.Equal("540.00", Format(300, "rankine-1.0.1", 0.01));
    }
}
