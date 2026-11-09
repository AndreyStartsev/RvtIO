using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The straight drive curve of a duct, pipe, conduit or cable tray in a file with its own schema (Revit 2020-2021, 2023 and later):
/// the GLine its CurveDriver queues is read under the keys of the Revit 2022 walk, so the length of the element follows from it as in a
/// Revit 2022 file; another root, a driver without a GLine and a driver that queues two lines give no curve.</summary>
public class WideMepCurveLineTests
{
    private const int DuctId = 1, DriverId = 2, BaseDriverId = 3, LineId = 4, CurveId = 5, OtherId = 6, ArcId = 7;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? fixedCount = null) =>
        new(name, type, mods, 0, fixedCount, null, null, null);

    private static SchemaDocument Schema() => new()
    {
        Classes =
        [
            new(DuctId, "RbsDuctCurve", 0, 1, [Field("m_pCurveDriver", 14, 2)], []),
            new(OtherId, "FamilyInstance", 0, 1, [Field("m_pCurveDriver", 14, 2)], []),
            new(BaseDriverId, "CurveDriver", 0, 1, [Field("m_pCrv", 14, 2), Field("m_pSecond", 14, 2)], []),
            new(DriverId, "RbsCurveDriver", BaseDriverId, 1, [], []),
            new(CurveId, "GCurve", 0, 1, [Field("m_endParams", 7, 16, 2)], []),
            new(LineId, "GLine", CurveId, 1, [Field("m_origin", 7, 16, 3), Field("m_dirVec", 7, 16, 3)], []),
            new(ArcId, "GArc", CurveId, 1, [Field("m_radius", 7)], []),
        ],
    };

    private static readonly WideNativeParams Params = new(Schema());

    private static void Pointer(BinaryWriter w, int classId) { w.Write(0xFFFFFFFFu); w.Write((ushort)classId); }

    private static void Line(BinaryWriter w, double start, double end, double[] origin, double[] direction)
    {
        w.Write(start); w.Write(end);
        foreach (double value in origin) w.Write(value);
        foreach (double value in direction) w.Write(value);
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

    [Fact]
    public void StraightDriveLine_IsKeptUnderTheKeysOfTheRevit2022Walk()
    {
        var np = Build(DuctId, w =>
        {
            Pointer(w, LineId); w.Write(0u);                       // driver: its curve, no second one
            Line(w, 1.0, 5.0, [10, 20, 30], [0, -0.6, 0.8]);
        });

        Assert.Equal([10.0, 20.0, 30.0], Enumerable.Range(0, 3).Select(i => np.DoubleFields[$"87:m_pCrv.origin@{i}"]));
        Assert.Equal([0.0, -0.6, 0.8], Enumerable.Range(0, 3).Select(i => np.DoubleFields[$"87:m_pCrv.dirVec@{i}"]));
        Assert.Equal([1.0, 5.0], Enumerable.Range(0, 2).Select(i => np.DoubleFields[$"87:m_pCrv.endParams@{i}"]));

        double length = 0;
        ElementBuilder.ApplyMepCurveGeometry(np, 3148, (id, storage, value, force) => { if (id == -1004005) length = (double)value; });
        Assert.Equal(4.0, length, 12);
    }

    [Fact]
    public void RootThatIsNotAnMepCurve_KeepsNoDriveLine()
    {
        var np = Build(OtherId, w =>
        {
            Pointer(w, LineId); w.Write(0u);
            Line(w, 0, 3, [0, 0, 0], [1, 0, 0]);
        });

        Assert.DoesNotContain(np.DoubleFields.Keys, key => key.StartsWith("87:m_pCrv.", StringComparison.Ordinal));
    }

    [Fact]
    public void DriverWithoutALine_KeepsNoDriveLine()
    {
        var np = Build(DuctId, w =>
        {
            Pointer(w, ArcId); w.Write(0u);
            w.Write(0.0); w.Write(1.0); w.Write(2.5);               // GCurve end parameters and the radius of the arc
        });

        Assert.DoesNotContain(np.DoubleFields.Keys, key => key.StartsWith("87:m_pCrv.", StringComparison.Ordinal));
    }

    [Fact]
    public void DriverWithTwoLines_KeepsNoDriveLine()
    {
        var np = Build(DuctId, w =>
        {
            Pointer(w, LineId); Pointer(w, LineId);
            Line(w, 0, 3, [0, 0, 0], [1, 0, 0]);
            Line(w, 0, 9, [0, 0, 0], [0, 1, 0]);
        });

        Assert.DoesNotContain(np.DoubleFields.Keys, key => key.StartsWith("87:m_pCrv.", StringComparison.Ordinal));
    }
}
