using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcLengthUnitTests
{
    [Theory]
    [InlineData("0.3048", "304.8")]
    [InlineData("3.", "3000.")]
    [InlineData("-0.25", "-250.")]
    [InlineData("0.", "0.")]
    [InlineData("1.5", "1500.")]
    [InlineData("104723.066996631", "104723066.996631")]
    [InlineData("0.30000000000000004", "300.00000000000004")]
    [InlineData("0.076200000000000003", "76.200000000000003")]
    [InlineData("5.0E-06", "0.005")]
    [InlineData("2.5e+2", "250000.")]
    [InlineData(" 2.7 ", "2700.")]
    public void ShiftNumber_MovesTheDecimalPointThreePlacesWithoutRounding(string input, string expected) =>
        Assert.Equal(expected, IfcLengthUnit.ShiftNumber(input));

    [Theory]
    [InlineData("$")]
    [InlineData("*")]
    [InlineData("#12")]
    [InlineData(".T.")]
    [InlineData("")]
    [InlineData("'1.5'")]
    public void ShiftNumber_LeavesWhatIsNotANumber(string input) =>
        Assert.Equal(input, IfcLengthUnit.ShiftNumber(input));

    [Theory]
    [InlineData("-0.", "-0.")]      // the written negative zero of a building pad's usage offset survives the shift
    [InlineData("0.", "0.")]
    [InlineData("0.076200000000000003", "76.200000000000003")]
    public void LayerSetUsage_ShiftsItsOffsetButKeepsANegativeZero(string offset, string expected) =>
        Assert.Equal($"#5,.AXIS3.,.NEGATIVE.,{expected}",
            IfcLengthUnit.ToMillimetres("IFCMATERIALLAYERSETUSAGE", $"#5,.AXIS3.,.NEGATIVE.,{offset}"));

    [Fact]
    public void Points_ScaleEveryCoordinate()
    {
        Assert.Equal("(304.8,609.6,1000.)", IfcLengthUnit.ToMillimetres("IFCCARTESIANPOINT", "(0.3048,0.6096,1.)"));
        Assert.Equal("(1000.,-500.)", IfcLengthUnit.ToMillimetres("IFCCARTESIANPOINT", "(1.,-0.5)"));
    }

    [Fact]
    public void ListedAttributes_ScaleAndTheRestIsKept()
    {
        Assert.Equal("#1,#2,#3,2700.", IfcLengthUnit.ToMillimetres("IFCEXTRUDEDAREASOLID", "#1,#2,#3,2.7"));
        Assert.Equal(".AREA.,$,#5,1500.,200.", IfcLengthUnit.ToMillimetres("IFCRECTANGLEPROFILEDEF", ".AREA.,$,#5,1.5,0.2"));
        Assert.Equal(".AREA.,$,#5,6.", IfcLengthUnit.ToMillimetres("IFCCIRCLEPROFILEDEF", ".AREA.,$,#5,0.006"));
        Assert.Equal("#9,25.,$", IfcLengthUnit.ToMillimetres("IFCMATERIALLAYER", "#9,0.025,$"));
    }

    [Fact]
    public void StringsWithSeparatorsDoNotShiftAttributeIndices()
    {
        string args = "'1bOe',#5,'Level, (1) it''s','','Level',#4960,$,'Level',.ELEMENT.,-0.8";
        Assert.Equal("'1bOe',#5,'Level, (1) it''s','','Level',#4960,$,'Level',.ELEMENT.,-800.",
            IfcLengthUnit.ToMillimetres("IFCBUILDINGSTOREY", args));
    }

    [Fact]
    public void StairFlightLengthsAreTheTenthAndEleventhAttributes()
    {
        Assert.Equal("'g',#5,'n',$,$,#1,#2,'t',18,17,180.56,300.",
            IfcLengthUnit.ToMillimetres("IFCSTAIRFLIGHT", "'g',#5,'n',$,$,#1,#2,'t',18,17,0.18056,0.3"));
    }

    [Theory]
    [InlineData("IFCDOOR", "'g',#5,'n',$,$,#1,#2,'t',2180.,1032.5")]
    [InlineData("IFCWINDOW", "'g',#5,'n',$,$,#1,#2,'t',2710.,1500.")]
    [InlineData("IFCPROPERTYSINGLEVALUE", "'Span',$,IFCPOSITIVELENGTHMEASURE(750.),$")]
    [InlineData("IFCDIRECTION", "(0.,0.,1.)")]
    [InlineData("IFCCARTESIANTRANSFORMATIONOPERATOR3D", "#1,#2,#3,1.,#4")]
    public void EntitiesWrittenInMillimetresOrDimensionlessAreNotTouched(string type, string args) =>
        Assert.Equal(args, IfcLengthUnit.ToMillimetres(type, args));

    [Theory]
    [InlineData(IfcProfile.Current, "0.1", "''")]
    [InlineData(IfcProfile.Legacy, "0.01", "$")]
    public void Export_DeclaresMillimetresAndMovesTheStoreyElevation(IfcProfile version, string precision, string identifier)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 10, CategoryId = -2000240, Category = "OST_Levels", Name = "Level 1" });
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-length-unit-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.Contains("=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", ifc);
            Assert.DoesNotContain("=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", ifc);
            Assert.Contains($"IFCGEOMETRICREPRESENTATIONCONTEXT({identifier},'Model',3,{precision},", ifc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class IfcLengthUnitFamilyTests
{
    [Fact]
    public void Foot_DividesMetreAttributesByTheFootAndMillimetreAttributesBy304_8()
    {
        Assert.Equal("#9,1.", IfcLengthUnit.ToFamily("IFCVECTOR", "#9,0.3048", IfcUnitFamily.Foot));
        Assert.Equal("(0.,0.,-6.)", IfcLengthUnit.ToFamily("IFCCARTESIANPOINT", "(0.,0.,-1.8288)", IfcUnitFamily.Foot));
        Assert.Equal("#1,#2,'a','','',#3,#4,'5',6.999999999999999,3.", IfcLengthUnit.ToFamily("IFCDOOR", "#1,#2,'a','','',#3,#4,'5',2133.6,914.4", IfcUnitFamily.Foot));
    }

    [Fact]
    public void Foot_ScalesAreasVolumesAndTypedPropertyValuesOnly()
    {
        Assert.Equal("'Q','',$,1.", IfcLengthUnit.ToFamily("IFCQUANTITYAREA", "'Q','',$,0.092903040000000006", IfcUnitFamily.Foot));
        Assert.Equal("'Q','',$,1.", IfcLengthUnit.ToFamily("IFCQUANTITYVOLUME", "'Q','',$,0.028316846592000004", IfcUnitFamily.Foot));
        Assert.Equal("'Span','',IFCPOSITIVELENGTHMEASURE(1.),$", IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", "'Span','',IFCPOSITIVELENGTHMEASURE(304.8),$", IfcUnitFamily.Foot));
        const string thermal = "'U','',IFCTHERMALTRANSMITTANCEMEASURE(0.5),$";
        Assert.Equal(thermal, IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", thermal, IfcUnitFamily.Foot));
        Assert.Equal("#1,.AREA.,'',#2,$", IfcLengthUnit.ToFamily("IFCMEASUREWITHUNIT", "#1,.AREA.,'',#2,$", IfcUnitFamily.Foot));
    }

    [Fact]
    public void Metre_KeepsMetresAndMovesMillimetreAttributes()
    {
        Assert.Equal("(1.8034,2.)", IfcLengthUnit.ToFamily("IFCCARTESIANPOINT", "(1.8034,2.)", IfcUnitFamily.Metre));
        Assert.Equal("'Q','',$,2.2352", IfcLengthUnit.ToFamily("IFCQUANTITYLENGTH", "'Q','',$,2.2352", IfcUnitFamily.Metre));
        Assert.Equal("'P','',IFCLENGTHMEASURE(0.0254),$", IfcLengthUnit.ToFamily("IFCPROPERTYSINGLEVALUE", "'P','',IFCLENGTHMEASURE(25.4),$", IfcUnitFamily.Metre));
    }

    [Fact]
    public void FootUnitAssignment_NamesTheFootAndTheSquareAndCubicFoot()
    {
        var w = new StepWriter();
        int length = w.Add("IFCSIUNIT", "*,.LENGTHUNIT.,$,.METRE.");
        IfcExporter.CreateUnitAssignmentCurrent(w, length, IfcUnitFamily.Foot);
        var text = new System.IO.StringWriter();
        w.WriteTo(text, "f", "IFC2X3", "d", "a");
        string file = text.ToString();
        Assert.Contains("'FOOT'", file);
        Assert.Contains("'SQUARE FOOT'", file);
        Assert.Contains("'CUBIC FOOT'", file);
        Assert.Contains("IFCLENGTHMEASURE(0.30480000000000002)", file);
        Assert.Contains("IFCAREAMEASURE(0.092903040000000006)", file);
        Assert.Contains("IFCVOLUMEMEASURE(0.028316846592000004)", file);
        Assert.DoesNotContain("SQUARE_METRE", file);
    }

    [Fact]
    public void Millimetre_IsTheUnchangedShift() =>
        Assert.Equal(IfcLengthUnit.ToMillimetres("IFCVECTOR", "#9,0.3048"), IfcLengthUnit.ToFamily("IFCVECTOR", "#9,0.3048", IfcUnitFamily.Millimetre));

    [Theory]
    [InlineData(null, IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:millimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:feetFractionalInches-1.0.0", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:feet-1.0.1", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:meters-1.0.0", IfcUnitFamily.Metre)]
    [InlineData("autodesk.unit.unit:metersCentimeters-1.0.0", IfcUnitFamily.Metre)]
    [InlineData("autodesk.unit.unit:usSurveyFeet-1.0.0", IfcUnitFamily.Foot)]
    [InlineData("autodesk.unit.unit:centimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:decimeters-1.0.1", IfcUnitFamily.Millimetre)]
    [InlineData("autodesk.unit.unit:inches-1.0.1", IfcUnitFamily.Millimetre)]
    public void Family_FollowsTheStoredLengthUnit(string? unit, IfcUnitFamily expected) =>
        Assert.Equal(expected, IfcLengthUnitChoice.Family(unit));

    [Theory]
    [InlineData("autodesk.unit.unit:millimeters-1.0.1", true)]
    [InlineData("autodesk.unit.unit:metersCentimeters-1.0.0", true)]
    [InlineData("autodesk.unit.unit:usSurveyFeet-1.0.0", true)]
    [InlineData("autodesk.unit.unit:centimeters-1.0.1", false)]
    [InlineData("autodesk.unit.unit:fractionalInches-1.0.0", false)]
    [InlineData(null, false)]
    public void OwnFamily_IsNullForAUnitWithoutAFamilyAndForAnUnreadableOption(string? unit, bool own) =>
        Assert.Equal(own, IfcLengthUnitChoice.OwnFamily(unit) != null);

    [Theory]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:metersCentimeters-1.0.0", "=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);", "metersCentimeters-1.0.0 -> Metre")]
    [InlineData(IfcProfile.Current, "autodesk.unit.unit:centimeters-1.0.1", "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "centimeters-1.0.1 has no unit family of its own; using the default Millimetre")]
    [InlineData(IfcProfile.Current, null, "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "stored option unreadable (")]
    [InlineData(IfcProfile.Legacy, "autodesk.unit.unit:centimeters-1.0.1", "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "centimeters-1.0.1 -> Millimetre")]
    [InlineData(IfcProfile.Legacy, "autodesk.unit.unit:metersCentimeters-1.0.0", "=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);", "metersCentimeters-1.0.0 -> Millimetre")]
    public void Export_DeclaresTheFamilyOfTheStoredUnitAndNamesTheDefaultInTheDiagnostics(IfcProfile profile, string? unit, string declared, string diagnostic)
    {
        var info = new RevitFileInfo { SerializedLengthUnit = unit };
        info.Elements.Add(new Element { Id = 10, CategoryId = -2000240, Category = "OST_Levels", Name = "Level 1" });
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-length-unit-{Guid.NewGuid():N}.ifc");
        var diagnostics = new StringWriter();
        try
        {
            new IfcExporter { Profile = profile, DiagnosticOutput = diagnostics }.Export(info, path);
            Assert.Contains(declared, File.ReadAllText(path));
            string line = Assert.Single(diagnostics.ToString().Split(Environment.NewLine), text => text.StartsWith("IFC length unit:", StringComparison.Ordinal));
            Assert.Contains(diagnostic, line);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
