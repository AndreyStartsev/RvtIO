using System.Reflection;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class DoorStyleOperationTests
{
    private const int Operation = -1001211;

    [Theory]
    [InlineData("SINGLE_SWING_RIGHT", false, "SINGLE_SWING_RIGHT")]
    [InlineData("SINGLE_SWING_RIGHT", true, "SINGLE_SWING_LEFT")]
    [InlineData("SINGLE_SWING_LEFT", true, "SINGLE_SWING_RIGHT")]
    [InlineData("SLIDING_TO_LEFT", true, "SLIDING_TO_RIGHT")]
    [InlineData("DOUBLE_DOOR_SINGLE_SWING_OPPOSITE_LEFT", true, "DOUBLE_DOOR_SINGLE_SWING_OPPOSITE_RIGHT")]
    [InlineData("SINGLE_SWING_LEFT", null, "NOTDEFINED")]
    [InlineData("DOUBLE_DOOR_SLIDING", null, "DOUBLE_DOOR_SLIDING")]
    [InlineData("DOUBLE_DOOR_SLIDING", true, "DOUBLE_DOOR_SLIDING")]
    [InlineData("REVOLVING", false, "REVOLVING")]
    [InlineData("Single swing right", false, "NOTDEFINED")]
    [InlineData("single_swing_right", false, "NOTDEFINED")]
    [InlineData("USERDEFINED", false, "NOTDEFINED")]
    [InlineData("", false, "NOTDEFINED")]
    [InlineData(null, false, "NOTDEFINED")]
    public void OperationIsTheTypeParameter_MirroredForAFlippedDoor(string? text, bool? flipped, string expected)
        => Assert.Equal(expected, IfcExporter.DoorStyleOperationCurrent(Type(text), new Element { Id = 1, SerializedFlippedSymbol = flipped }));

    [Fact]
    public void OperationIsNotReadFromAnotherParameterOrANumber()
    {
        var type = new Element { Id = 500, Parameters = [
            new Parameter { Id = -1001206, StorageType = StorageType.String, Value = "SINGLE_SWING_LEFT" },
            new Parameter { Id = Operation, StorageType = StorageType.Integer, Value = 1 }] };
        Assert.Equal("NOTDEFINED", IfcExporter.DoorStyleOperationCurrent(type, new Element { Id = 1, SerializedFlippedSymbol = false }));
    }

    [Theory]
    // one style per type: its doors agree, or the operation stays undefined
    [InlineData("IFCDOOR", true, "SINGLE_SWING_LEFT", false, false, "SINGLE_SWING_LEFT")]
    [InlineData("IFCDOOR", true, "SINGLE_SWING_LEFT", true, true, "SINGLE_SWING_RIGHT")]
    [InlineData("IFCDOOR", true, "SINGLE_SWING_LEFT", false, true, "NOTDEFINED")]
    [InlineData("IFCDOOR", true, "SINGLE_SWING_LEFT", true, false, "NOTDEFINED")]
    [InlineData("IFCDOOR", true, "SINGLE_SWING_LEFT", false, null, "NOTDEFINED")]
    [InlineData("IFCDOOR", true, "DOUBLE_DOOR_SLIDING", false, true, "DOUBLE_DOOR_SLIDING")]
    [InlineData("IFCDOOR", true, null, false, false, "NOTDEFINED")]
    // window styles and the other profile are not changed
    [InlineData("IFCWINDOW", true, "SINGLE_SWING_LEFT", false, false, "NOTDEFINED")]
    [InlineData("IFCDOOR", false, "SINGLE_SWING_LEFT", false, false, "NOTDEFINED")]
    public void StyleCarriesTheOperationItsDoorsAgreeOn(string product, bool currentProfile, string? text,
        bool? first, bool? second, string expected)
    {
        Element Door(int id, bool? flipped) => new() { Id = id, NativeMasterSymbolId = 500, SerializedFlippedSymbol = flipped,
            Parameters = [new Parameter { Id = -1002050, StorageType = StorageType.ElementId,
                Definition = new Definition { Name = "ELEM_TYPE_PARAM" }, Value = 500 }] };
        var elements = new Dictionary<int, Element> { [1] = Door(1, first), [2] = Door(2, second), [500] = Type(text) };
        var products = new Dictionary<int, (int entityId, int placementId, string ifcType)>
            { [1] = (1001, 0, product), [2] = (1002, 0, product) };
        var writer = new StepWriter();
        typeof(IfcExporter).GetMethod("WriteTypeObjectsAndRelDefinesByTypeCore", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [writer, 900, elements, products,
                new Dictionary<(string PropertyName, string ValueExpr), int>(), new Dictionary<int, bool?>(), currentProfile]);
        using var output = new StringWriter(); writer.WriteTo(output, "test.ifc", "IFC2X3", "test", "test");
        string style = product == "IFCDOOR" ? "IFCDOORSTYLE" : "IFCWINDOWSTYLE";
        string line = Assert.Single(output.ToString().Split('\n'), line => Regex.IsMatch(line, $@"^#\d+={style}\("));
        Assert.EndsWith(product == "IFCDOOR" ? $",.{expected}.,.NOTDEFINED.,.F.,.F.);" : ",.NOTDEFINED.,.NOTDEFINED.,.F.,.F.);",
            line.TrimEnd());
    }

    private static Element Type(string? text) => new() { Id = 500, ValidatedIfcTypeObjectName = "Own family:Own type",
        Parameters = text == null ? [] : [new Parameter { Id = Operation, StorageType = StorageType.String, Value = text }] };
}
