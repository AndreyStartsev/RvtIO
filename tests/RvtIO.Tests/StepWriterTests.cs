using RvtIO.Ifc;
using Xunit;

namespace RvtIO.Tests;

public class StepWriterTests
{
    [Theory]
    [InlineData(-1e-12, "-1.0E-12")]
    [InlineData(1e20, "1.0E+20")]
    [InlineData(1.25e-12, "1.25E-12")]
    [InlineData(2, "2.")]
    [InlineData(2.5, "2.5")]
    public void Real_PutsDecimalPointInExponentMantissa(double value, string expected)
    {
        Assert.Equal(expected, StepWriter.Real(value));
    }

    [Fact]
    public void Rollback_ForgetsTheArcsOfThePolylinesItTakesBack()
    {
        var w = new StepWriter();
        int kept = w.Add("IFCPOLYLINE", "()");
        w.PolylineArcs[kept] = [new(0, 1, 0.1)];
        var checkpoint = w.CreateCheckpoint();
        int takenBack = w.Add("IFCPOLYLINE", "()");
        w.PolylineArcs[takenBack] = [new(0, 2, 0.3)];

        w.Rollback(checkpoint);

        // the id is given out again, to a polyline whose writer knows of no arc
        Assert.Equal(takenBack, w.Add("IFCPOLYLINE", "()"));
        Assert.Equal([kept], w.PolylineArcs.Keys);
    }
}
