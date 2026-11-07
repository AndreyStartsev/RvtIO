using RvtIO.Ifc;
using Xunit;

namespace RvtIO.Tests;

public sealed class StepWriterRealExactTests
{
    [Theory]
    [InlineData(2699.9999999999627, "2699.9999999999627")]
    [InlineData(12.000000000005699, "12.000000000005699")]
    [InlineData(100.0, "100.")]
    [InlineData(0.0, "0.")]
    [InlineData(0.1, "0.10000000000000001")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(-350.51956176757812, "-350.51956176757812")]
    [InlineData(1e-5, "1.0000000000000001E-005")]
    [InlineData(6.123233995736766E-17, "6.123233995736766E-017")]
    [InlineData(-3.2808398950131236E+27, "-3.2808398950131236E+027")]
    [InlineData(1e20, "1.0E+020")]
    public void WritesTheDigitsOfTheDouble(double value, string expected) =>
        Assert.Equal(expected, StepWriter.RealExact(value));

    [Fact]
    public void KeepsNegativeZero() => Assert.Equal("-0.", StepWriter.RealExact(-0.0));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValuesKeepTheEarlierText(double value)
    {
        string text = value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(text + ".", StepWriter.RealExact(value));
    }

    [Fact]
    public void RoundTrips()
    {
        var random = new Random(7);
        for (int i = 0; i < 1000; i++)
        {
            double value = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-12, 12));
            Assert.Equal(value, double.Parse(StepWriter.RealExact(value), System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
