using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The Hermite spline that drives a flexible duct or pipe in a file with its own schema (Revit 2020-2021, 2023 and later): the
/// nodes of the GHermiteSpline its CurveDriver queues are read under the keys of the Revit 2022 walk, so the length of the element follows
/// from them as in a Revit 2022 file; another root, a driver without a spline and a driver that queues two splines give no nodes.</summary>
public class WideFlexCurveTests
{
    private const int FlexDuctId = 1, DuctId = 2, BaseDriverId = 3, DriverId = 4, CurveId = 5, SplineId = 6, NodeId = 7, LineId = 8;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? fixedCount = null, int? reference = null) =>
        new(name, type, mods, 0, fixedCount, reference, null, null);

    private static SchemaDocument Schema() => new()
    {
        Classes =
        [
            new(FlexDuctId, "RbsFlexDuctCurve", 0, 1, [Field("m_pCurveDriver", 14, 2)], []),
            new(DuctId, "RbsDuctCurve", 0, 1, [Field("m_pCurveDriver", 14, 2)], []),
            new(BaseDriverId, "CurveDriver", 0, 1, [Field("m_pCrv", 14, 2), Field("m_pSecond", 14, 2)], []),
            new(DriverId, "RbsCurveDriver", BaseDriverId, 1, [], []),
            new(CurveId, "GCurve", 0, 1, [Field("m_endParams", 7, 16, 2)], []),
            new(SplineId, "GHermiteSpline", CurveId, 2, [Field("m_Periodic", 1), Field("m_NodeArray", 14, 80, reference: NodeId)], []),
            new(NodeId, "SplineNode", 0, 3, [Field("m_iParametr", 7), Field("m_iPoint", 7, 16, 3), Field("m_iTangent", 7, 16, 3)], []),
            new(LineId, "GLine", CurveId, 1, [Field("m_origin", 7, 16, 3), Field("m_dirVec", 7, 16, 3)], []),
        ],
    };

    private static readonly WideNativeParams Params = new(Schema());

    private static void Pointer(BinaryWriter w, int classId) { w.Write(0xFFFFFFFFu); w.Write((ushort)classId); }

    private static void Spline(BinaryWriter w, params double[][] nodes)
    {
        w.Write(0.0); w.Write(0.0);                                  // GCurve end parameters
        w.Write((byte)0);                                            // m_Periodic
        w.Write((uint)nodes.Length);
        foreach (double[] node in nodes) foreach (double value in node) w.Write(value);
    }

    private static TfBlockDeserializer.NativeParams Build(int rootId, Action<BinaryWriter> queue)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)rootId);
        Pointer(w, DriverId);
        queue(w);
        byte[] block = stream.ToArray();
        var walk = Params.Walker.Run(block, 0, block.Length, rootId);
        Assert.True(walk.Exact, walk.Error);
        return Params.Build(walk, rootId)!;
    }

    private static void AssertNoNodes(TfBlockDeserializer.NativeParams np) =>
        Assert.DoesNotContain(np.DoubleFields.Keys, key => key.StartsWith("87:m_pCrv.", StringComparison.Ordinal));

    [Fact]
    public void HermiteDriveSpline_KeepsItsNodesUnderTheKeysOfTheRevit2022Walk()
    {
        var np = Build(FlexDuctId, w =>
        {
            Pointer(w, SplineId); w.Write(0u);                       // driver: its curve, no second one
            Spline(w, [0, 1, 2, 3, 0, 1, 0], [10, 4, 5, 6, 0, 1, 0], [25, 7, 8, 9, 0, 1, 0]);
        });

        Assert.Equal(3, np.DoubleFields["87:m_pCrv.nodes"]);
        Assert.Equal([10.0, 4, 5, 6, 0, 1, 0], Enumerable.Range(0, 7).Select(k => np.DoubleFields[$"87:m_pCrv.node1@{k}"]));
        Assert.Equal([25.0, 7, 8, 9, 0, 1, 0], Enumerable.Range(0, 7).Select(k => np.DoubleFields[$"87:m_pCrv.node2@{k}"]));
    }

    [Fact]
    public void HermiteDriveSpline_GivesTheLengthOfTheElement()
    {
        // two nodes 10 apart on a line with tangents along the line: the spline is that line
        var np = Build(FlexDuctId, w =>
        {
            Pointer(w, SplineId); w.Write(0u);
            Spline(w, [0, 0, 0, 0, 1, 0, 0], [10, 10, 0, 0, 1, 0, 0]);
        });

        double? feet = ElementBuilder.FlexCurveLength(np);

        Assert.NotNull(feet);
        Assert.Equal(10.0, feet.Value, 9);
    }

    [Fact]
    public void RootThatIsNotAFlexCurve_KeepsNoNodes()
    {
        AssertNoNodes(Build(DuctId, w =>
        {
            Pointer(w, SplineId); w.Write(0u);
            Spline(w, [0, 0, 0, 0, 1, 0, 0], [10, 10, 0, 0, 1, 0, 0]);
        }));
    }

    [Fact]
    public void DriverWithoutASpline_KeepsNoNodes()
    {
        AssertNoNodes(Build(FlexDuctId, w =>
        {
            Pointer(w, LineId); w.Write(0u);
            w.Write(0.0); w.Write(3.0);                              // GCurve end parameters
            foreach (double value in new double[] { 0, 0, 0, 1, 0, 0 }) w.Write(value);
        }));
    }

    [Fact]
    public void DriverWithTwoSplines_KeepsNoNodes()
    {
        AssertNoNodes(Build(FlexDuctId, w =>
        {
            Pointer(w, SplineId); Pointer(w, SplineId);
            Spline(w, [0, 0, 0, 0, 1, 0, 0], [10, 10, 0, 0, 1, 0, 0]);
            Spline(w, [0, 0, 0, 0, 0, 1, 0], [5, 0, 5, 0, 0, 1, 0]);
        }));
    }

    [Fact]
    public void SplineOfASingleNode_KeepsNoNodes()
    {
        AssertNoNodes(Build(FlexDuctId, w =>
        {
            Pointer(w, SplineId); w.Write(0u);
            Spline(w, [0, 0, 0, 0, 1, 0, 0]);
        }));
    }

    [Theory]
    [InlineData(3177, true)]    // flexible duct
    [InlineData(3180, true)]    // flexible pipe
    [InlineData(3148, false)]   // straight duct
    [InlineData(0, false)]
    public void LengthOfAFlexCurve_IsAppliedToFlexDuctsAndPipesOnly(int curveClass, bool applied)
    {
        var np = new TfBlockDeserializer.NativeParams();
        np.DoubleFields["87:m_pCrv.nodes"] = 2;
        double[][] nodes = [[0, 0, 0, 0, 1, 0, 0], [10, 10, 0, 0, 1, 0, 0]];
        for (int i = 0; i < nodes.Length; i++)
            for (int k = 0; k < 7; k++) np.DoubleFields[$"87:m_pCrv.node{i}@{k}"] = nodes[i][k];

        double? length = null;
        ElementBuilder.ApplyFlexCurveGeometry(np, (ushort)curveClass, (id, storage, value, force) => { if (id == -1004005) length = (double)value; });

        Assert.Equal(applied, length.HasValue);
        if (applied) Assert.Equal(10.0, length!.Value, 9);
    }
}
