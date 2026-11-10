using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>Grid export of the current profile: one IfcGrid per level and per orthogonal pair or triple of
/// direction families, instead of one per connected component on every storey.</summary>
public partial class IfcExporter
{
    private const int GridCategoryId = -2000220;
    private const double GridDirectionTolerance = 1e-6;

    /// <summary>A grid line of the exporter: its element, curve and unit direction.</summary>
    internal sealed record GridLine(Element Element, LocationCurve Curve, double[] Direction, string Label)
    {
        public double ZFeet => Curve.StartPoint.Length > 2 ? Curve.StartPoint[2] : 0.0;
    }

    /// <summary>The grids the current profile exports: straight, distinct (an exact duplicate of a line with the same label
    /// is exported once), by element id. Only records of the ElemTable count: a grid whose payload is left in the partitions
    /// without a table record is not exported.</summary>
    internal static List<GridLine> CollectGridLines(IEnumerable<Element> elements)
    {
        var lines = new List<GridLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements.Where(element => element.CategoryId == GridCategoryId && !element.IsDiscovered).OrderBy(element => element.Id))
        {
            // A datum plane without a category (ELEM_CATEGORY_PARAM -1) is no Grid and never reaches the exporter.
            if (element.Parameters.Any(parameter => parameter.Id == -1140362 && parameter.Value is int or long
                    && Convert.ToInt64(parameter.Value, CultureInfo.InvariantCulture) == -1))
                continue;
            if (element.Location is not { CurveType: "Line", ArcRadius: null } curve
                || curve.StartPoint.Length < 2 || curve.EndPoint.Length < 2
                || !curve.StartPoint.Concat(curve.EndPoint).All(double.IsFinite))
                continue;
            double[] start = curve.StartPoint, end = curve.EndPoint;
            double dx = end[0] - start[0], dy = end[1] - start[1];
            double dz = (end.Length > 2 ? end[2] : 0.0) - (start.Length > 2 ? start[2] : 0.0);
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length == 0) continue;
            string label = GridLabel(element);
            string signature = string.Join('|', label, string.Join(',', start.Select(v => v.ToString("R", CultureInfo.InvariantCulture))),
                string.Join(',', end.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
            if (!seen.Add(signature)) continue;
            // The stored plane direction, with the sign of its zero components, when it runs along the line.
            double[] direction = curve.Direction is { Length: 3 } stored && stored.All(double.IsFinite)
                && stored[0] * dx + stored[1] * dy + stored[2] * dz > 0
                ? stored : [dx / length, dy / length, dz / length];
            lines.Add(new GridLine(element, curve, direction, label));
        }
        return lines;
    }

    private static string GridLabel(Element grid) =>
        grid.Parameters.FirstOrDefault(parameter => parameter.Id == -1008000
            && parameter.Value is string value && !string.IsNullOrWhiteSpace(value))?.Value as string
        ?? (!string.IsNullOrWhiteSpace(grid.Name) ? grid.Name : null)
        ?? grid.Id.ToString(CultureInfo.InvariantCulture);

    private static bool SameDirection(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) <= GridDirectionTolerance && Math.Abs(a[1] - b[1]) <= GridDirectionTolerance
        && Math.Abs(a[2] - b[2]) <= GridDirectionTolerance;

    /// <summary>Component-wise comparison: x, then y, then z, each within the tolerance.</summary>
    private static int CompareDirections(double[] a, double[] b)
    {
        for (int i = 0; i < 3; i++)
            if (Math.Abs(a[i] - b[i]) > GridDirectionTolerance) return a[i] < b[i] ? -1 : 1;
        return 0;
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    /// <summary>One IfcGrid as the current profile decides it: the U, V and optional W axis lines and the direction key text
    /// that goes into its GlobalId.</summary>
    internal sealed record GridSet(List<GridLine> U, List<GridLine> V, List<GridLine> W, string Key);

    /// <summary>The IfcGrids of one level and grid name: grids are keyed by
    /// their unit direction (ordered x, y, z). While more than three directions remain and two of them are
    /// orthogonal, the first such pair with its anti-parallel lines becomes a rectangular grid; what is left is
    /// cut into triples in direction order (a trailing single direction is dropped).</summary>
    internal static List<GridSet> SplitGridLines(IReadOnlyList<GridLine> lines)
    {
        var families = new List<(double[] Key, List<GridLine> Lines)>();
        foreach (var line in lines)
        {
            int at = families.FindIndex(family => CompareDirections(family.Key, line.Direction) == 0);
            if (at >= 0) families[at].Lines.Add(line);
            else families.Add((line.Direction, [line]));
        }
        families.Sort((a, b) => CompareDirections(a.Key, b.Key));

        var result = new List<GridSet>();
        List<GridLine> WithAntiParallel((double[] Key, List<GridLine> Lines) family)
        {
            var all = new List<GridLine>(family.Lines);
            foreach (var other in families)
                if (!ReferenceEquals(other.Lines, family.Lines) && Math.Abs(Dot(other.Key, family.Key) + 1.0) <= 1e-10)
                {
                    all.AddRange(other.Lines);
                    break;
                }
            return all;
        }

        if (families.Count > 1)
            while (families.Count != 3)
            {
                (double[] Key, List<GridLine> Lines)? u = null, v = null;
                foreach (var first in families)
                {
                    foreach (var second in families)
                        if (Math.Abs(Dot(first.Key, second.Key)) <= 1e-10)
                        {
                            u = first; v = second;
                            break;
                        }
                    if (u != null) break;
                }
                if (u == null || v == null) break;
                var uLines = WithAntiParallel(u.Value);
                var vLines = WithAntiParallel(v.Value);
                result.Add(new GridSet(uLines, vLines, [], GridDirectionText(u.Value.Key) + ":" + GridDirectionText(v.Value.Key)));
                var exported = uLines.Concat(vLines).ToHashSet();
                foreach (var family in families) family.Lines.RemoveAll(exported.Contains);
                families.RemoveAll(family => family.Lines.Count == 0);
            }

        if (families.Count > 1)
            for (int i = 0; i < families.Count; i += 3)
            {
                if (i + 1 >= families.Count) continue;
                var key = GridDirectionText(families[i].Key) + ":" + GridDirectionText(families[i + 1].Key);
                var w = new List<GridLine>();
                if (i + 2 < families.Count)
                {
                    key += ":" + GridDirectionText(families[i + 2].Key);
                    w = families[i + 2].Lines;
                }
                result.Add(new GridSet(families[i].Lines, families[i + 1].Lines, w, key));
            }
        return result;
    }

    /// <summary>Direction text in "%f; %f; %f" form: six decimals, the sign of a negative number that rounds to zero kept.</summary>
    internal static string GridDirectionText(double[] direction) =>
        string.Create(CultureInfo.InvariantCulture, $"{direction[0]:F6}; {direction[1]:F6}; {direction[2]:F6}");

    /// <summary>The GlobalId of an IfcGrid: the MD5 of "IfcGrid:{name}:{direction key}:{storey GlobalId}", where
    /// the name is the one byte a narrow format reads from the wide-character grid name, so "Default Grid" contributes "D".</summary>
    internal static string GridGlobalId(string gridName, string directionKey, string storeyGlobalId)
    {
        string prefix = gridName.Length > 0 && (gridName[0] & 0xFF) != 0 ? ((char)(gridName[0] & 0xFF)).ToString() : "";
        return IfcGuid.FromMd5($"IfcGrid:{prefix}:{directionKey}:{storeyGlobalId}");
    }

    private const double GridLevelTolerance = 1e-9;

    /// <summary>The elevations (feet) a grid reaches: the range of its datum plane's envelope along the world Z axis.
    /// Levels from the lower to the upper bound hold the grid; null when the plane is not decoded.</summary>
    internal static (double Lower, double Upper)? GridZInterval(Element grid)
    {
        if (grid.GridPlane is not { Envelope.Length: 4, Origin.Length: 3, XVector.Length: 3, YVector.Length: 3 } plane)
            return null;
        double lower = double.PositiveInfinity, upper = double.NegativeInfinity;
        foreach (double u in new[] { plane.Envelope[0], plane.Envelope[2] })
            foreach (double v in new[] { plane.Envelope[1], plane.Envelope[3] })
            {
                double z = plane.Origin[2] + u * plane.XVector[2] + v * plane.YVector[2];
                lower = Math.Min(lower, z); upper = Math.Max(upper, z);
            }
        return double.IsFinite(lower) && double.IsFinite(upper) ? (lower, upper) : null;
    }

    /// <summary>The grid lines on the plane that carries most of them: the fallback for grids whose datum plane (and so
    /// the levels they reach, <see cref="GridZInterval"/>) is not decoded.</summary>
    internal static List<GridLine> MainPlaneGridLines(IReadOnlyList<GridLine> lines)
    {
        const double planeTolerance = 1e-6;
        var main = lines.GroupBy(line => Math.Round(line.ZFeet / planeTolerance))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First();
        return main.ToList();
    }

    /// <summary>The lines of the grid whose elevation interval reaches the elevation of a level within 1e-9.</summary>
    internal static (double Lower, double Upper)? LevelIntervalOf(Element grid) =>
        grid.SerializedGridLevelInterval is { Length: 2 } interval ? (interval[0], interval[1]) : GridZInterval(grid);

    internal static List<GridLine> GridLinesOnLevel(IReadOnlyList<GridLine> lines, double elevationFeet) =>
        lines.Where(line => LevelIntervalOf(line.Element) is { } interval
            && interval.Lower <= elevationFeet + GridLevelTolerance && elevationFeet - GridLevelTolerance <= interval.Upper).ToList();

    /// <summary>A point of a grid axis in millimetres written as the metre value the length pass moves (the plan point; z is 0).</summary>
    private static string GridPointCurrent(double[] feet) => StepWriter.List(
        StepWriter.RealFromMillimetres(feet[0] * 304.8), StepWriter.RealFromMillimetres(feet[1] * 304.8), "0.");

    /// <summary>Direction entity: components within 1e-9 of an integer are that integer, and a direction along
    /// a global axis is the one shared IfcDirection of that axis (written once, on first use); any other direction is a
    /// new entity with its components printed with 17 digits.</summary>
    private static int GridDirectionCurrent(StepWriter w, double[] direction, Dictionary<(int, int, int), int> globalDirections)
    {
        const double tolerance = 1e-9;
        var clean = direction.Select(value => Math.Abs(value - Math.Round(value)) <= tolerance ? Math.Round(value) : value).ToArray();
        int axis = Array.FindIndex(clean, value => Math.Abs(Math.Abs(value) - 1.0) <= tolerance);
        if (axis < 0 || clean.Where((_, i) => i != axis).Any(value => Math.Abs(value) > tolerance))
            return w.Add("IFCDIRECTION", StepWriter.List(clean.Select(D17)));
        var key = (axis == 0 ? (int)clean[0] : 0, axis == 1 ? (int)clean[1] : 0, axis == 2 ? (int)clean[2] : 0);
        if (!globalDirections.TryGetValue(key, out int id))
            globalDirections[key] = id = w.Add("IFCDIRECTION", StepWriter.List(
                key.Item1.ToString(CultureInfo.InvariantCulture) + ".", key.Item2.ToString(CultureInfo.InvariantCulture) + ".",
                key.Item3.ToString(CultureInfo.InvariantCulture) + "."));
        return id;
    }

    /// <summary>Whether a grid is put on a level: the level's elevation lies in the grid's elevation interval
    /// (the vertical extent of its datum plane). A grid whose extent is unknown is put on every level.</summary>
    internal static bool GridIsOnLevel(GridLine line, double levelElevationFeet)
    {
        const double tolerance = 1e-9;
        return line.Element.GridElevationRangeFeet is not { Length: 2 } range
            || range[0] <= levelElevationFeet + tolerance && levelElevationFeet - tolerance <= range[1];
    }

    private HashSet<int> CreateCurrentGrids(StepWriter w, int ownerHistId, int subCtxId, int axisId,
        IReadOnlyList<Element> elements, bool elemTableIsExact, IReadOnlyList<int> storeyIfcIds,
        IReadOnlyDictionary<int, int> storeyPlacements, IDictionary<int, List<int>> storeyContents)
    {
        var written = new HashSet<int>();
        // A grid recovered outside a verified ElemTable is a stale block of a deleted element and is skipped.
        var lines = CollectGridLines(elements.Where(element => !(elemTableIsExact && element.IsDiscovered)));
        if (lines.Count == 0) return written;
        // A grid line stands on the levels its datum plane reaches; lines without a decoded
        // plane use their main plane on all levels.
        var intervals = lines.Select(line => LevelIntervalOf(line.Element)).ToList();
        bool byInterval = intervals.All(interval => interval != null);
        if (!byInterval) lines = MainPlaneGridLines(lines);

        // Levels with the same elevation share the first level's grids.
        var levels = new List<(int Storey, double Elevation)>();
        foreach (int storey in storeyIfcIds)
            if (_storeyLevels.TryGetValue(storey, out var level) && !levels.Any(known => known.Elevation == GetElevation(level)))
                levels.Add((storey, GetElevation(level)));
        levels = levels.OrderBy(level => level.Elevation).ToList();

        const string GridName = "Default Grid";
        string lastLoggedKeys = "";
        var globalDirections = new Dictionary<(int, int, int), int>();
        for (int level = 0; level < levels.Count; level++)
        {
            if (!_storeyGlobalIds.TryGetValue(levels[level].Storey, out string? storeyGlobalId)) continue;
            double elevation = levels[level].Elevation;
            var levelLines = byInterval
                ? lines.Where(line => LevelIntervalOf(line.Element) is { } interval
                    && interval.Lower <= elevation + GridLevelTolerance && elevation - GridLevelTolerance <= interval.Upper).ToList()
                : lines;
            var sets = SplitGridLines(levelLines);
            string keys = string.Join("|", sets.Select(set => set.Key));
            if (keys != lastLoggedKeys)
                foreach (var set in sets)
                    _diag?.WriteLine($"IFC grid key: {set.Key} (U {set.U.Count}, V {set.V.Count}, W {set.W.Count}; level {_storeyLevels[levels[level].Storey].Id})");
            lastLoggedKeys = keys;
            foreach (var set in sets)
            {
                var curveSetIds = new List<int>();
                string Axes(List<GridLine> axisLines)
                {
                    var ids = new List<int>();
                    foreach (var line in axisLines)
                    {
                        // An IfcTrimmedCurve over an IfcLine (the start point, a
                        // vector of one foot along the axis) trimmed by the start and end points; the plan curve has z = 0.
                        int startId = w.Add("IFCCARTESIANPOINT", GridPointCurrent(line.Curve.StartPoint));
                        int vectorId = w.Add("IFCVECTOR", $"{R(GridDirectionCurrent(w, line.Direction, globalDirections))},{D(FeetToMetres)}");
                        int lineId = w.Add("IFCLINE", $"{R(startId)},{R(vectorId)}");
                        int endId = w.Add("IFCCARTESIANPOINT", GridPointCurrent(line.Curve.EndPoint));
                        int curveId = w.Add("IFCTRIMMEDCURVE",
                            $"{R(lineId)},{StepWriter.List(R(startId))},{StepWriter.List(R(endId))},.T.,.CARTESIAN.");
                        bool sameSense = SameDirection(line.Direction, axisLines[0].Direction);
                        ids.Add(w.Add("IFCGRIDAXIS", $"{S(line.Label)},{R(curveId)},{(sameSense ? ".T." : ".F.")}"));
                        int curveSetId = w.Add("IFCGEOMETRICCURVESET", StepWriter.List(R(curveId)));
                        curveSetIds.Add(curveSetId);
                        AddGridCurveStyle(w, curveSetId);
                        written.Add(line.Element.Id);
                    }
                    return StepWriter.List(ids.Select(R));
                }
                string uAxes = Axes(set.U), vAxes = Axes(set.V), wAxes = set.W.Count > 0 ? Axes(set.W) : "$";
                int representationId = w.Add("IFCSHAPEREPRESENTATION",
                    $"{R(subCtxId)},{S("FootPrint")},{S("GeometricCurveSet")},{StepWriter.List(curveSetIds.Select(R))}");
                int shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List(R(representationId))}");
                int placementId = w.Add("IFCLOCALPLACEMENT", $"{R(storeyPlacements[levels[level].Storey])},{R(axisId)}");
                int gridId = w.Add("IFCGRID",
                    $"{S(GridGlobalId(GridName, set.Key, storeyGlobalId))},{R(ownerHistId)},{S(GridName)},'','',"
                    + $"{R(placementId)},{R(shapeId)},{uAxes},{vAxes},{wAxes}");
                storeyContents[levels[level].Storey].Add(gridId);
            }
        }
        return written;
    }
}
