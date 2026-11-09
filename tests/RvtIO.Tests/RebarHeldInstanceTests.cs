using RvtIO.Parsing;
using Xunit;
using Capture = RvtIO.Parsing.WideBlockWalker.Capture;
using Kind = RvtIO.Parsing.WideBlockWalker.CaptureKind;

namespace RvtIO.Tests;

/// <summary>The bars of a RebarSystem of Revit 2020 and 2021 are held instances of its RebarSystemGStep (HeldRebarInstanceKey, then a
/// RebarInstBase), read from hand-made captures of the walk.</summary>
public class RebarHeldInstanceTests
{
    private const int GStep = 4;

    private static Capture Int(string key, long value, int queue = GStep) =>
        new(key, Kind.Integer, value, 0, null, false, queue, key[..key.IndexOf('.')], key[(key.IndexOf('.') + 1)..]);

    private static Capture Real(string key, double value, int queue = GStep) =>
        new(key, Kind.Real, 0, value, null, false, queue, key[..key.IndexOf('.')], key[(key.IndexOf('.') + 1)..]);

    /// <summary>The held instance of key (index, layer) and the straight bar behind it: three positions over a set length of 2 feet (step 1 foot), no hooks, the identity
    /// frame, one length parameter (id 100) of 2 feet.</summary>
    private static List<Capture> Held(int index, int layer, long positions = 3, bool complete = true)
    {
        var caps = new List<Capture> { Int("HeldRebarInstanceKey.m_index", index), Int("HeldRebarInstanceKey.m_layer", layer) };
        if (!complete) return caps;
        caps.AddRange([
            Real("RebarInstBase.m_barLength", 2), Real("RebarInstBase.m_endPar", 2), Real("RebarInstBase.m_orientationAngleAtEnd", 0),
            Real("RebarInstBase.m_orientationAngleAtStart", 0), Real("RebarInstBase.m_maxSpacing", 1), Int("RebarInstBase.m_barTypeId", 9),
            Int("RebarInstBase.m_couplerId@0", -1), Int("RebarInstBase.m_couplerId@1", -1),
            Int("RebarInstBase.m_endTreatmentTypeId0", -1), Int("RebarInstBase.m_endTreatmentTypeId1", -1),
            Int("RebarInstBase.m_hookOrient0", -1), Int("RebarInstBase.m_hookOrient1", -1),
            Int("RebarInstBase.m_hookTypeId0", -1), Int("RebarInstBase.m_hookTypeId1", -1),
            Int("RebarInstBase.m_layoutRule", 2), Int("RebarInstBase.m_numberOfBarPositions", positions), Int("RebarInstBase.m_shapeId", 7),
            Int("RebarInstBase.m_includeFirstBar", 1), Int("RebarInstBase.m_includeLastBar", 1),
            Int("std::pair< ElementId, ParamVectorIterator >.first", 100), Int("ParamVectorIterator.m_index", 0),
            Real(WideBlockWalker.AllItemsKeyPrefix + "ParamVectorValues.m_doubles@0", 2),
        ]);
        double[] trf = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        for (int i = 0; i < 9; i++) caps.Add(Real($"Trf.m_3x3@{i / 3}/{i % 3}", trf[i]));
        for (int i = 0; i < 3; i++) caps.Add(Real($"Trf.m_or@{i}", trf[9 + i]));
        return caps;
    }

    private static readonly RebarReader.ShapeDefinition Straight = new([[1, 0, 0]], [100], [0, 0], 0, [-1, -1]);
    private static readonly RebarReader.BarKind Kind0 = new(0.3, 0.3, 0.2, new Dictionary<int, (double, double, double)>());

    [Fact]
    public void ReadHeld_ReadsOneInstanceBehindEachKeyAndAppliesTheStepOnce()
    {
        var held = RebarReader.ReadHeld([.. Held(0, 0, positions: 3), .. Held(0, 1, positions: 1)]);
        Assert.Equal([(0, 0), (1, 0)], held.Keys.OrderBy(k => k));
        var bar = RebarReader.SolveShapeDriven(held[(0, 0)], Straight, [], Kind0, 0.1)!;
        Assert.Equal(9, bar.BarTypeId);
        // bar k is the first bar moved by k steps of 1 foot along the normal of the frame (z), not by 2 k
        Assert.Equal([0.0, 1.0, 2.0], bar.Bars.Select(b => b[0].Origin[2]));
        Assert.Single(RebarReader.SolveShapeDriven(held[(1, 0)], Straight, [], Kind0, 0.1)!.Bars);
    }

    [Fact]
    public void ReadHeld_LeavesOutAKeyThatHoldsNoInstance()
    {
        var held = RebarReader.ReadHeld([.. Held(0, 0, complete: false), .. Held(0, 1)]);
        Assert.Equal([(1, 0)], held.Keys);
        Assert.Empty(RebarReader.ReadHeld([]));
    }

    private static FileSchema Schema(params string[] layerFields) => new(new SchemaDocument
    {
        Classes = [new(1, "RebarSystemLayer", 0, 1, [.. layerFields.Select(n => new SchemaField(n, 5, 0, 0, null, null, null, null))], [])],
    });

    [Fact]
    public void HeldLayout_IsTheLayerWithoutUncutCurves()
    {
        Assert.True(RebarReader.HeldLayout(Schema("m_barType", "m_active")));
        Assert.False(RebarReader.HeldLayout(Schema("m_barType", "m_uncutCurves")));
    }
}
