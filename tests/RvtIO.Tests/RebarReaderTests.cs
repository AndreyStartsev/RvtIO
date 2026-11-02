using System.Text;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The centre lines of a shape driven bar (<see cref="RebarReader"/>) read from a hand-made Rebar block in the grammar of
/// <see cref="WideBlockWalker"/>, and the pieces that read them: the keyed template field and the byte array capture of the walker.</summary>
public class RebarReaderTests
{
    private const int Identifier = 1, ElementId = 2, Trf = 3, GCurve = 4, GLine = 5, GArc = 6, OneBar = 7, Cache = 8, InstBase = 9,
        Instance = 10, Element = 11, Rebar = 12;

    private static SchemaField Field(string name, byte type, byte mods = 0, int? reference = null, int? fixedCount = null,
        List<SchemaField>? args = null) => new(name, type, mods, 0, fixedCount, reference, null, args);

    private static SchemaDocument Schema() => new()
    {
        Classes =
        [
            new(Identifier, "Identifier", 0, 2, [Field("m_id64", 11)], []),
            new(ElementId, "ElementId", 0, 1, [Field("m_id", 14, 0, Identifier)], []),
            new(Trf, "Trf", 0, 1, [Field("m_3x3", 13, 16, null, 3, [Field("", 7, 16, null, 3)]), Field("m_or", 7, 16, null, 3)], []),
            new(GCurve, "GCurve", 0, 1, [Field("m_endParams", 7, 16, null, 2)], []),
            new(GLine, "GLine", GCurve, 1, [Field("m_origin", 7, 16, null, 3), Field("m_dirVec", 7, 16, null, 3)], []),
            new(GArc, "GArc", GCurve, 1,
                [Field("m_xVec", 7, 16, null, 3), Field("m_yVec", 7, 16, null, 3), Field("m_radius", 7), Field("m_center", 7, 16, null, 3)], []),
            new(OneBar, "OneBarCenterlineCurves", 0, 1, [Field("m_centerlineCurves", 14, 81)], []),
            new(Cache, "RebarInstCache", 0, 1,
                [Field("m_noSelfIntersectingCenterlineCurves", 14, 81), Field("m_selfIntersectingCenterlineCurves", 14, 81)], []),
            new(InstBase, "RebarInstBase", 0, 1,
                [Field("m_barPositionsRelativeToFirstBarArr", 14, 80, Trf), Field("m_oCacheData", 14, 1), Field("m_includedBars", 1, 80),
                 Field("m_barTypeId", 14, 0, ElementId), Field("m_barLength", 7)], []),
            new(Instance, "RebarInstance", InstBase, 1, [], []),
            new(Element, "Element", 0, 1, [Field("m_id", 14, 0, ElementId)], []),
            new(Rebar, "Rebar", Element, 1, [Field("m_oInstance", 14, 1)], []),
        ],
    };

    private static void Pointer(BinaryWriter w, int tag) { w.Write(1u); w.Write((ushort)tag); }

    private static void Transform(BinaryWriter w, double x)
    {
        foreach (double v in new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1 }) w.Write(v);   // m_3x3 (identity)
        w.Write(x); w.Write(0.0); w.Write(0.0);                                   // m_or
    }

    [Fact]
    public void KeyedTemplateField_IsCapturedPerRowAndColumn()
    {
        var schema = Schema();
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)Trf);
        foreach (double v in new[] { 1.0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }) w.Write(v);
        var block = stream.ToArray();
        var walk = new WideBlockWalker(schema, RebarReader.Key, []).Run(block, 0, block.Length, Trf);
        Assert.True(walk.Exact, walk.Error);
        Assert.Equal(6, walk.Captures.Single(c => c.Key == "Trf.m_3x3@1/2").Real);
        Assert.Equal(7, walk.Captures.Single(c => c.Key == "Trf.m_3x3@2/0").Real);
        Assert.Equal(12, walk.Captures.Single(c => c.Key == "Trf.m_or@2").Real);
    }

    [Fact]
    public void SegmentMoved_AppliesRotationThenOrigin()
    {
        // a quarter turn about Z followed by a shift of (10, 0, 0)
        double[] trf = [0, -1, 0, 1, 0, 0, 0, 0, 1, 10, 0, 0];
        var line = new RebarSegment(false, [1, 0, 0], [1, 0, 0], [0, 0, 0], 0, 0, 2);
        var moved = line.Moved(trf);
        Assert.Equal([10, 1, 0], moved.Origin.Select(v => Math.Round(v)).ToArray());
        Assert.Equal([0, 1, 0], moved.Axis.Select(v => Math.Round(v)).ToArray());
        var arc = new RebarSegment(true, [1, 0, 0], [1, 0, 0], [0, 1, 0], 0.5, 0, Math.PI / 2).Moved(trf);
        Assert.Equal([0, 1, 0], arc.Axis.Select(v => Math.Round(v)).ToArray());
        Assert.Equal([-1, 0, 0], arc.Axis2.Select(v => Math.Round(v)).ToArray());
        Assert.Equal(0.5, arc.Radius);
    }

    /// <summary>A Rebar of two bar positions (offsets 0 and 1 foot) whose first bar is a 2 foot line and a quarter circle.</summary>
    private static byte[] RebarBlockWithCurves(bool included)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.Unicode);
        w.Write((ushort)Rebar);
        w.Write(55L);
        Pointer(w, Instance);
        w.Write(2u); Transform(w, 0); Transform(w, 1);
        Pointer(w, Cache);
        w.Write(2u); w.Write((byte)1); w.Write((byte)(included ? 1 : 0));
        w.Write(9L);
        w.Write(3.5);
        w.Write(1u); Pointer(w, OneBar);
        w.Write(0u);
        w.Write(2u); Pointer(w, GLine); Pointer(w, GArc);
        // q3 GLine: params 0..2, origin (0,0,0), direction (1,0,0)
        foreach (double v in new[] { 0.0, 2.0, 0, 0, 0, 1, 0, 0 }) w.Write(v);
        // q4 GArc: params 0..pi/2, xVec (1,0,0), yVec (0,1,0), radius 0.5, centre (2,0.5,0)
        foreach (double v in new[] { 0.0, Math.PI / 2, 1, 0, 0, 0, 1, 0, 0.5, 2, 0.5, 0 }) w.Write(v);
        return stream.ToArray();
    }

    [Fact]
    public void ReadRebar_MovesTheCachedBarByEveryPositionTwiceAndKeepsTheLengthAndType()
    {
        var walker = new WideBlockWalker(Schema(), RebarReader.Key, []);
        var block = RebarBlockWithCurves(included: true);
        var walk = walker.Run(block, 0, block.Length, Rebar);
        Assert.True(walk.Exact, walk.Error);
        var bars = RebarReader.ReadRebar(walker, walk);
        Assert.NotNull(bars);
        Assert.Equal(9, bars.BarTypeId);
        Assert.Equal(3.5, bars.BarLength);
        Assert.Equal(2, bars.Bars.Count);
        Assert.Equal(2, bars.Bars[0].Count);
        Assert.Equal(0, bars.Bars[0][0].Origin[0]);
        // The current profile applies the position transform twice: the second position (offset 1) lies 2 feet from the first
        Assert.Equal(2, bars.Bars[1][0].Origin[0]);
        Assert.Equal(4, bars.Bars[1][1].Origin[0]);
        Assert.True(bars.Bars[0][1].IsArc);
        Assert.Equal(0.5, bars.Bars[0][1].Radius);
    }

    [Fact]
    public void ReadRebar_LeavesOutABarThatIsNotIncluded()
    {
        var walker = new WideBlockWalker(Schema(), (d, f) => RebarReader.Key(d, f), []);
        var block = RebarBlockWithCurves(included: false);
        var walk = walker.Run(block, 0, block.Length, Rebar);
        Assert.True(walk.Exact, walk.Error);
        Assert.Single(RebarReader.ReadRebar(walker, walk)!.Bars);
    }


    private static string Export(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-rebar-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>One bar of a line (1 foot) and a quarter circle (radius 0.5 foot), in a system.</summary>
    private static Element Bar(int id, int systemId, bool inAssembly = false)
    {
        IReadOnlyList<RebarSegment> bar =
        [
            new(false, [0, 0, 0], [1, 0, 0], [0, 0, 0], 0, 0, 1),
            new(true, [1, 0.5, 0], [0, -1, 0], [1, 0, 0], 0.5, 0, Math.PI / 2),
        ];
        // diameters 8 mm (nominal) and 8 mm (model), in feet; the document rounds lengths to 10 mm
        return new Element
        {
            Id = id, CategoryId = -2009000, TypeId = 900,
            RebarBars = new RebarBars(900, systemId, null, [bar, bar], 8 / 304.8, 8 / 304.8, "Rebar 8", 10, InAssembly: inAssembly),
        };
    }

    [Fact]
    public void Export_WritesAReinforcingBarWithOneSweptDiskPerBarAndAGroupForItsSystem()
    {
        string ifc = Export(Bar(500, 600), Bar(501, 600), new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1" });
        string[] lines = ifc.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        string bar = Assert.Single(lines, l => l.Contains("IFCREINFORCINGBAR(") && l.Contains("'500'"));
        // Name family:type:id, ObjectType the type name for a bar of a system, Tag, SteelGrade, diameter, area, rounded length (1 ft + 0.5 ft * pi / 2 = 544.2 mm)
        Assert.Contains("'Rebar Bar:Rebar 8:500','','Rebar 8',", bar);
        Assert.Contains(",'500','',8.,", bar);
        Assert.Contains(",540.,.NOTDEFINED.,$)", bar);
        Assert.Equal(4, lines.Count(l => l.Contains("IFCSWEPTDISKSOLID(")));
        // disk radius 4 mm, end parameter 304.8 mm of line + 90 degrees of arc
        Assert.Contains(lines, l => l.Contains("IFCSWEPTDISKSOLID(") && l.Contains(",4.,$,0.,394.8)"));
        Assert.Contains(lines, l => l.Contains("IFCTRIMMEDCURVE(") && l.Contains("IFCPARAMETERVALUE(90.)"));
        Assert.Contains(lines, l => l.Contains("IFCCIRCLE(") && l.EndsWith(",152.4);"));
        // the group of the system relates both bars
        Assert.Contains(lines, l => l.Contains("IFCGROUP(") && l.Contains("'Structural Area Reinforcement:Area Reinforcement 1'"));
        var relation = Assert.Single(lines, l => l.Contains("IFCRELASSIGNSTOGROUP("));
        Assert.Matches(@",\(#\d+,#\d+\),\$,#\d+\);", relation);
        // a project without a storey contains its bars in the building
        string id = bar[..bar.IndexOf('=')];
        var containment = Assert.Single(lines, l => l.Contains("IFCRELCONTAINEDINSPATIALSTRUCTURE("));
        Assert.Contains(id, Regex.Match(containment, @",\(([^)]*)\),").Groups[1].Value.Split(','));
        Assert.Contains(lines, l => l.StartsWith("#") && l.Contains("=IFCBUILDING(") && containment.EndsWith(l[..l.IndexOf('=')] + ");"));
    }

    [Fact]
    public void Export_CountsEveryWrittenReinforcingBarAsAnExportedProduct()
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange([Bar(500, 600), Bar(501, 600), new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1" }]);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-rebar-{Guid.NewGuid():N}.ifc");
        var skipLog = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipLog };
        try
        {
            exporter.Export(info, path);
            int bars = File.ReadAllText(path).Split('\n').Count(l => l.Contains("IFCREINFORCINGBAR("));
            Assert.Equal(2, bars);
            Assert.Equal(bars, exporter.ExportedProducts);
            Assert.Equal(info.Elements.Count, exporter.ExportedProducts + skipLog.Total);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Export_WritesNoGroupForTheBarsOfASystemThatAreAssemblyMembers()
    {
        string ifc = Export(Bar(500, 600, inAssembly: true), Bar(501, 600, inAssembly: true), new Element { Id = 600, CategoryId = -2009003, Name = "Area Reinforcement 1" });
        Assert.Equal(2, ifc.Split('\n').Count(l => l.Contains("IFCREINFORCINGBAR(")));
        Assert.DoesNotContain("IFCGROUP(", ifc);
        Assert.DoesNotContain("IFCRELASSIGNSTOGROUP(", ifc);
        // an assembly aggregates its members: they are contained in no spatial structure
        Assert.DoesNotContain("IFCRELCONTAINEDINSPATIALSTRUCTURE(", ifc);
    }

    [Fact]
    public void SegmentMinZ_IsTheLowestPointOfALineAndOfAnArc()
    {
        Assert.Equal(1, new RebarSegment(false, [0, 0, 3], [0, 0, 1], [0, 0, 0], 0, -2, 2).MinZ());
        // a half circle in the XZ plane through its lowest point: centre z 10, radius 2, angles 180 to 360 degrees (z = 10 + 2 sin a)
        Assert.Equal(8, new RebarSegment(true, [0, 0, 10], [1, 0, 0], [0, 0, 1], 2, Math.PI, 2 * Math.PI).MinZ(), 12);
        // the quarter from 0 to 90 degrees stays above the centre: its lowest point is the start
        Assert.Equal(10, new RebarSegment(true, [0, 0, 10], [1, 0, 0], [0, 0, 1], 2, 0, Math.PI / 2).MinZ(), 12);
        // the lowest point of the circle at 270 degrees is also found when the arc is given by a start beyond one turn
        Assert.Equal(8, new RebarSegment(true, [0, 0, 10], [1, 0, 0], [0, 0, 1], 2, 2 * Math.PI + 3.5, 2 * Math.PI + 5.5).MinZ(), 12);
    }

    private static IReadOnlyList<RebarSegment> Line(double x, double z, double length) => [new(false, [x, 0, z], [1, 0, 0], [0, 0, 0], 0, 0, length)];

    /// <summary>Twelve lines of 10 feet one foot apart (z 0 to 11, bars of radius 0.1) and a window between x = 4 and x = 6 over the lines 6 to 8.</summary>
    [Fact]
    public void CutBars_TakesTheLinesWhoseMovedFirstBarLiesInTheOutline()
    {
        var lines = Enumerable.Range(0, 12).Select(k => Line(0, k, 10)).ToList();
        const double r = 0.1;
        // whole bars of the lines 0 to 5, and the piece left of the window (x 0 to 4) of the lines 6 to 8
        var whole = RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(0, 0, 10)[0], [-r, -r, -r], [10 + r, r, 5 + r]), r)!;
        Assert.Equal(6, whole.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(k => (double)k), whole.Select(bar => bar[0].Origin[2]));
        var left = RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(0, 6, 4)[0], [-r, -r, 6 - r], [4 + r, r, 8 + r]), r)!;
        Assert.Equal([6.0, 7.0, 8.0], left.Select(bar => bar[0].Origin[2]));
        Assert.All(left, bar => Assert.Equal(4, bar[0].End));
        // the piece right of the window starts on its own x and still lies on the lines 6 to 8
        var right = RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(6, 6, 4)[0], [6 - r, -r, 6 - r], [10 + r, r, 8 + r]), r)!;
        Assert.Equal([6.0, 7.0, 8.0], right.Select(bar => bar[0].Origin[2]));
        Assert.All(right, bar => Assert.Equal(6, bar[0].Origin[0]));
    }

    [Fact]
    public void CutBars_IsNullWhenTheFirstBarLiesOnNoLine()
    {
        var lines = Enumerable.Range(0, 3).Select(k => Line(0, k, 10)).ToList();
        Assert.Null(RebarReader.CutBars(lines, new RebarReader.CutInstance(Line(0, 0.5, 10)[0], [-1, -1, -1], [11, 1, 4]), 0.1));
    }
}
