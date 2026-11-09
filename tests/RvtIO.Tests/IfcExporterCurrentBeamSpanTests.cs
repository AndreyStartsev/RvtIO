using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Pset_BeamCommon.Span of the current profile: the stored INSTANCE_LENGTH_PARAM of the beam (then of its type), written
/// in millimetres. All 13 sample project A beams carry 10300 although their extrusions are 10127 to 10300 long.</summary>
public class IfcExporterCurrentBeamSpanTests
{
    private const int InstanceLengthParamId = -1001375;

    private static Parameter Length(double feet, bool approximate = false) => new()
    {
        Id = InstanceLengthParamId, StorageType = StorageType.Double, Value = feet, IsApproximate = approximate,
    };

    [Fact]
    public void StoredInstanceLength_IsTheSpanInMillimetres()
    {
        var beam = new Element { Id = 1 };
        beam.Parameters.Add(Length(33.79265091863494));
        Assert.Equal(10300.0, IfcExporter.ResolveBeamSpanMillimetresCurrent(beam, null)!.Value, 9);
    }

    [Fact]
    public void TypeLength_IsUsedWhenTheOccurrenceHasNone_AndTheOccurrenceWins()
    {
        var type = new Element { Id = 2 };
        type.Parameters.Add(Length(10.0));
        var beam = new Element { Id = 1 };
        Assert.Equal(3048.0, IfcExporter.ResolveBeamSpanMillimetresCurrent(beam, type)!.Value, 9);
        beam.Parameters.Add(Length(20.0));
        Assert.Equal(6096.0, IfcExporter.ResolveBeamSpanMillimetresCurrent(beam, type)!.Value, 9);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(-3.0, false)]
    [InlineData(33.79, true)]
    [InlineData(double.NaN, false)]
    public void ZeroNegativeUnreadableOrDerivedLength_HasNoSpan(double feet, bool approximate)
    {
        var beam = new Element { Id = 1 };
        beam.Parameters.Add(Length(feet, approximate));
        Assert.Null(IfcExporter.ResolveBeamSpanMillimetresCurrent(beam, null));
    }

    [Fact]
    public void BeamWithoutAStoredLength_SpansItsDriveCurve()
    {
        var beam = new Element { Id = 1, CurveDriverLineLength = 10.0 };
        Assert.Equal(3048.0, IfcExporter.ResolveBeamSpanMillimetresCurrent(beam, null)!.Value, 9);
        beam.Parameters.Add(Length(20.0));
        Assert.Equal(6096.0, IfcExporter.ResolveBeamSpanMillimetresCurrent(beam, null)!.Value, 9);
    }

    [Fact]
    public void DriveCurveFirst_PrefersTheExactCurveOverTheRoundedStoredLength_AndFallsBack()
    {
        var brace = new Element { Id = 1, CurveDriverLineLength = 1.4569 };
        brace.Parameters.Add(Length(1.4567));
        Assert.Equal(1.4567 * 304.8, IfcExporter.ResolveBeamSpanMillimetresCurrent(brace, null)!.Value, 9);
        Assert.Equal(1.4569 * 304.8, IfcExporter.ResolveBeamSpanMillimetresCurrent(brace, null, driveCurveFirst: true)!.Value, 9);
        brace.CurveDriverLineLength = null;
        Assert.Equal(1.4567 * 304.8, IfcExporter.ResolveBeamSpanMillimetresCurrent(brace, null, driveCurveFirst: true)!.Value, 9);
    }

    [Fact]
    public void NoLengthParameter_HasNoSpan() =>
        Assert.Null(IfcExporter.ResolveBeamSpanMillimetresCurrent(new Element { Id = 1 }, new Element { Id = 2 }));
}
