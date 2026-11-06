using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;
using Direction = RvtIO.Parsing.TfBlockDeserializer.StairPathDirection;

namespace RvtIO.Tests;

public class StairShapeTests
{
    private static TfBlockDeserializer.StairRunInput Run(int id, params TfBlockDeserializer.StairPathDirection[] directions)
        => new(id, 100, TfBlockDeserializer.StairRunStyle.Straight, directions);

    [Theory]
    [InlineData(0, 0, "NOTDEFINED")] [InlineData(1, 0, "STRAIGHT_RUN_STAIR")]
    [InlineData(1, 5, "STRAIGHT_RUN_STAIR")] [InlineData(2, 1, "QUARTER_TURN_STAIR")]
    [InlineData(2, 0, "NOTDEFINED")] [InlineData(3, 2, null)]
    [InlineData(3, 1, "NOTDEFINED")] [InlineData(4, 3, "THREE_QUARTER_TURN_STAIR")]
    [InlineData(4, 2, "NOTDEFINED")] [InlineData(5, 4, "NOTDEFINED")]
    public void OriginalFlightCountAndPerpendicularDirectionBranches(int flights, int landings, string? expected)
    {
        var directions = new TfBlockDeserializer.StairPathDirection[] { new(1, 0, 0), new(0, 1, 0), new(-1, 0, 0), new(0, -1, 0), new(1, 0, 0) };
        Assert.Equal(expected, StairShape.Classify(Enumerable.Range(0, flights).Select(i => Run(200 + i, directions[i])).ToArray(), landings));
    }

    [Theory]
    [InlineData(1, 0, "TWO_STRAIGHT_RUN_STAIR")]
    [InlineData(-1, 0, "HALF_TURN_STAIR")]
    [InlineData(0, 1, "QUARTER_TURN_STAIR")]
    [InlineData(0.5, 0.5, "NOTDEFINED")]
    [InlineData(1.000000001, 0, "TWO_STRAIGHT_RUN_STAIR")]
    [InlineData(1.000000003, 0, "NOTDEFINED")]
    [InlineData(0.0000000005, 1, "QUARTER_TURN_STAIR")]
    [InlineData(0.000000002, 1, "NOTDEFINED")]
    public void DotProductUsesOriginalRelativeMathUtilContract(double x, double y, string expected)
        => Assert.Equal(expected, StairShape.Classify([Run(200, new Direction(1, 0, 0)), Run(201, new Direction(x, y, 0))], 1));

    [Fact]
    public void MultiLineStraightAndSketchedPathsAreNotCountedAsFlights()
    {
        var multiple = Run(200, new(1, 0, 0), new(0, 1, 0));
        foreach (var style in new[] { TfBlockDeserializer.StairRunStyle.Straight, TfBlockDeserializer.StairRunStyle.Sketched })
        {
            Assert.Equal("NOTDEFINED", StairShape.Classify([multiple with { Style = style }], 0));
            Assert.Equal("STRAIGHT_RUN_STAIR", StairShape.Classify([multiple with { Style = style }, Run(201, new Direction(1, 0, 0))], 1));
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(0, true)] [InlineData(2, true)]
    public void ASpiralRunMakesTheWholeStairSpiralWhateverElseItHolds(int landings, bool withStraightRun)
    {
        var spiral = Run(200) with { Style = TfBlockDeserializer.StairRunStyle.Spiral };
        var runs = withStraightRun ? new[] { Run(201, new Direction(1, 0, 0)), spiral } : [spiral];
        Assert.Equal("SPIRAL_STAIR", StairShape.Classify(runs, landings));
        Assert.Equal("SPIRAL_STAIR", StairShape.Classify(runs, landings, sketchedIsFlight: false));
    }

    [Fact]
    public void ASketchedRunIsNotAFlightInTheCurrentProfile()
    {
        var sketched = Run(200, new Direction(1, 0, 0)) with { Style = TfBlockDeserializer.StairRunStyle.Sketched };
        Assert.Equal("STRAIGHT_RUN_STAIR", StairShape.Classify([sketched], 0));
        Assert.Equal("NOTDEFINED", StairShape.Classify([sketched], 0, sketchedIsFlight: false));
        Assert.Equal("NOTDEFINED", StairShape.Classify([sketched, sketched with { OwnerId = 201 }], 1, sketchedIsFlight: false));
        Assert.Equal("STRAIGHT_RUN_STAIR", StairShape.Classify([Run(201, new Direction(1, 0, 0))], 0, sketchedIsFlight: false));
    }

    /// <summary>A straight run, a landing and a sketched run turned back: the sketched run is no flight of the current profile, so the stair
    /// is straight (the reference export of the large DACH sample), while the older profile counts two flights turned back (half turn).</summary>
    [Theory]
    [InlineData(-1, 0, 1, "STRAIGHT_RUN_STAIR", "HALF_TURN_STAIR")]
    [InlineData(0, 1, 1, "STRAIGHT_RUN_STAIR", "QUARTER_TURN_STAIR")]
    [InlineData(-1, 0, 0, "STRAIGHT_RUN_STAIR", "NOTDEFINED")]
    public void AStraightRunWithASketchedRunIsStraightInTheCurrentProfile(double x, double y, int landings, string current, string older)
    {
        var runs = new[] { Run(200, new Direction(1, 0, 0)), Run(201, new Direction(x, y, 0)) with { Style = TfBlockDeserializer.StairRunStyle.Sketched } };
        Assert.Equal(current, StairShape.Classify(runs, landings, sketchedIsFlight: false));
        Assert.Equal(older, StairShape.Classify(runs, landings));
    }

    [Theory]
    [InlineData(true, "STRAIGHT_RUN_STAIR")]
    [InlineData(false, "HALF_TURN_STAIR")]
    public void TheExporterHandsTheProfileToTheShapeRuleOfAStairWithAStraightAndASketchedRun(bool currentProfile, string expected)
    {
        var (parent, elements, counts) = Fixture();
        parent.SerializedStairCore = [new(200, 3708), new(201, 3686), new(202, 3708)];
        foreach (var (id, cls, input) in new (int, ushort, TfBlockDeserializer.StairRunInput?)[]
                 { (201, 3686, null), (202, 3708, Run(202, new Direction(-1, 0, 0)) with { Style = TfBlockDeserializer.StairRunStyle.Sketched }) })
        {
            elements.Add(id, new() { Id = id, SerializedOwnElementClassId = cls, SerializedOwningElementId = 100, SerializedStairRunInput = input });
            counts.Add(id, 1);
        }
        Assert.Equal(expected, IfcExporter.ResolveNativeStairShape(parent, null, elements, counts, currentProfile));
    }

    [Theory]
    [InlineData("empty")] [InlineData("style")] [InlineData("nan")]
    [InlineData("inf")] [InlineData("zero")] [InlineData("vertical")] [InlineData("negative_landings")]
    public void UnsupportedOrInvalidInputsAreNotClassified(string mode)
    {
        var run = Run(200, new Direction(1, -0.0, 0)); int landings = 0;
        run = mode switch
        {
            "empty" => run with { Directions = [] },
            "style" => run with { Style = (TfBlockDeserializer.StairRunStyle)1 },
            "nan" => run with { Directions = [new(double.NaN, 0, 0)] },
            "inf" => run with { Directions = [new(0, double.PositiveInfinity, 0)] },
            "zero" => run with { Directions = [new(-0.0, 0, 0)] },
            "vertical" => run with { Directions = [new(1, 0, 1)] }, _ => run,
        };
        if (mode == "negative_landings") landings = -1;
        Assert.Null(StairShape.Classify([run], landings));
    }

    [Fact]
    public void ApiNumericIdSetOrderWinsOverHelperEnumeration()
    {
        var (parent, elements, counts) = Fixture();
        // Helper order is 300,200,400,500, but native API SET order is 200,300,400,500.
        parent.SerializedStairCore = [new(300, 3708), new(200, 3708), new(400, 3708), new(500, 3708), new(600, 3686), new(601, 3686), new(602, 3686)];
        elements[200].SerializedStairRunInput = Run(200, new Direction(1, 0, 0));
        Add(300, 3708, Run(300, new Direction(0, 1, 0))); Add(400, 3708, Run(400, new Direction(-1, 0, 0))); Add(500, 3708, Run(500, new Direction(0, -1, 0)));
        Add(600, 3686); Add(601, 3686); Add(602, 3686);
        Assert.Equal("THREE_QUARTER_TURN_STAIR", IfcExporter.ResolveNativeStairShape(parent, null, elements, counts));
        void Add(int id, ushort cls, TfBlockDeserializer.StairRunInput? input = null)
        {
            elements.Add(id, new() { Id = id, SerializedOwnElementClassId = cls, SerializedOwningElementId = 100, SerializedStairRunInput = input });
            counts.Add(id, 1);
        }
    }

    [Fact]
    public void TheCurrentProfileLeavesAStairWithASketchedRunUndefined()
    {
        var (parent, elements, counts) = Fixture();
        elements[200].SerializedStairRunInput = elements[200].SerializedStairRunInput! with { Style = TfBlockDeserializer.StairRunStyle.Sketched };
        Assert.Equal("STRAIGHT_RUN_STAIR", IfcExporter.ResolveNativeStairShape(parent, null, elements, counts));
        Assert.Equal("NOTDEFINED", IfcExporter.ResolveNativeStairShape(parent, null, elements, counts, currentProfile: true));
    }

    [Theory]
    [InlineData("parent_id")] [InlineData("parent_block")] [InlineData("parent_category")]
    [InlineData("parent_class")] [InlineData("parent_duplicate")] [InlineData("no_core")]
    [InlineData("duplicate_core")] [InlineData("child_missing")] [InlineData("child_duplicate")]
    [InlineData("child_block")] [InlineData("child_class")] [InlineData("child_owner")]
    [InlineData("wrong_role")] [InlineData("no_input")] [InlineData("input_owner")]
    [InlineData("input_parent")] [InlineData("parent_override")] [InlineData("type_override")]
    [InlineData("run_override")] [InlineData("run_type_override")]
    public void ExporterRejectsIncompleteOrConflictingValidation(string mode)
    {
        var (parent, elements, counts) = Fixture(); var child = elements[200]; var type = new Element { Id = 101 };
        switch (mode)
        {
            case "parent_id": parent.Id = -1; break;
            case "parent_block": parent.IsBlockOnly = true; break;
            case "parent_category": parent.CategoryId = -2000126; break;
            case "parent_class": parent.SerializedOwnElementClassId = 3742; break;
            case "parent_duplicate": counts[100] = 2; break;
            case "no_core": parent.SerializedStairCore = null; break;
            case "duplicate_core": parent.SerializedStairCore = [new(200, 3708), new(200, 3708)]; break;
            case "child_missing": elements.Remove(200); break;
            case "child_duplicate": counts[200] = 2; break;
            case "child_block": child.IsBlockOnly = true; break;
            case "child_class": child.SerializedOwnElementClassId = 3686; break;
            case "child_owner": child.SerializedOwningElementId = 999; break;
            case "wrong_role": parent.SerializedStairCore = [new(200, 3739)]; break;
            case "no_input": child.SerializedStairRunInput = null; break;
            case "input_owner": child.SerializedStairRunInput = child.SerializedStairRunInput! with { OwnerId = 999 }; break;
            case "input_parent": child.SerializedStairRunInput = child.SerializedStairRunInput! with { StairsId = 999 }; break;
            case "parent_override": Override(parent); break;
            case "type_override": Override(type); break;
            case "run_override": Override(child); break;
            case "run_type_override":
                child.Parameters.Add(new() { Definition = new() { Name = "ELEM_TYPE_PARAM" }, Value = 201 });
                elements[201] = new() { Id = 201 }; Override(elements[201]); break;
        }
        Assert.Equal("NOTDEFINED", IfcExporter.ResolveNativeStairShape(parent, type, elements, counts));
        void Override(Element element) => element.Parameters.Add(new() { Id = -1019017, Value = "IFCSTAIR" });
    }

    private static (Element Parent, Dictionary<int, Element> Elements, Dictionary<int, int> Counts) Fixture()
    {
        var parent = new Element { Id = 100, CategoryId = -2000120, SerializedOwnElementClassId = 3681, SerializedStairCore = [new(200, 3708)] };
        var child = new Element { Id = 200, SerializedOwnElementClassId = 3708, SerializedOwningElementId = 100, SerializedStairRunInput = Run(200, new Direction(1, 0, 0)) };
        return (parent, new() { [100] = parent, [200] = child }, new() { [100] = 1, [200] = 1 });
    }
}
