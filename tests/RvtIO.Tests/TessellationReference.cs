using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The implementations of GRepParserV2 as they were before the allocation work (LINQ, a new list for every grid cell), kept unchanged as the
/// reference the randomized differential tests in <see cref="TessellationAllocationTests"/> compare the current ones with.</summary>
internal static class TessellationReference
{
    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e7;

    internal static double MaxAbs(double u, double v, double a, double b, double c, double d)
        => new[] { u, v, a, b, c, d }.Select(Math.Abs).Max();

    internal static List<List<double[]>> ClipGridCells(List<List<double[]>> sourcePolygons, List<double> uSamples, List<double> vSamples,
        bool allVCells, Func<double, double, double[]> evaluate)
    {
        static List<double[]> ClipBoundary(
            List<double[]> input, int axis, double value, bool keepGreater)
        {
            if (input.Count == 0) return input;
            bool Inside(double[] point) => keepGreater
                ? point[axis] >= value - 1e-12
                : point[axis] <= value + 1e-12;
            double[] Intersection(double[] first, double[] second)
            {
                // Adjacent clipped cells can traverse their shared source segment in
                // opposite directions.  Evaluate the intersection in one canonical
                // direction and write the clipping coordinate verbatim; otherwise the
                // two mathematically identical UV vertices can differ by one final bit
                // and quantize to different IFC points.
                if (first[axis] > second[axis]
                    || (first[axis] == second[axis]
                        && first[1 - axis] > second[1 - axis]))
                    (first, second) = (second, first);
                double denominator = second[axis] - first[axis];
                double fraction = Math.Abs(denominator) < 1e-20
                    ? 0 : (value - first[axis]) / denominator;
                fraction = Math.Clamp(fraction, 0, 1);
                var intersection = new[]
                {
                    first[0] + (second[0] - first[0]) * fraction,
                    first[1] + (second[1] - first[1]) * fraction,
                };
                intersection[axis] = value;
                return intersection;
            }

            var output = new List<double[]>();
            var previous = input[^1];
            bool previousInside = Inside(previous);
            foreach (var current in input)
            {
                bool currentInside = Inside(current);
                if (currentInside)
                {
                    if (!previousInside) output.Add(Intersection(previous, current));
                    output.Add(current);
                }
                else if (previousInside)
                {
                    output.Add(Intersection(previous, current));
                }
                previous = current;
                previousInside = currentInside;
            }
            return output;
        }

        static List<double[]> CleanUv(List<double[]> polygon)
        {
            double span = polygon.Count == 0 ? 1 : Math.Max(
                polygon.Max(point => point[0]) - polygon.Min(point => point[0]),
                polygon.Max(point => point[1]) - polygon.Min(point => point[1]));
            double epsilon = Math.Max(1e-12, span * 1e-10);
            var clean = new List<double[]>();
            foreach (var point in polygon)
                if (clean.Count == 0 || Math.Abs(point[0] - clean[^1][0]) > epsilon
                    || Math.Abs(point[1] - clean[^1][1]) > epsilon)
                    clean.Add(point);
            if (clean.Count > 1 && Math.Abs(clean[0][0] - clean[^1][0]) <= epsilon
                && Math.Abs(clean[0][1] - clean[^1][1]) <= epsilon)
                clean.RemoveAt(clean.Count - 1);
            return clean;
        }

        static double SignedArea(IReadOnlyList<double[]> polygon)
        {
            double area = 0;
            for (int index = 0; index < polygon.Count; index++)
            {
                var first = polygon[index];
                var second = polygon[(index + 1) % polygon.Count];
                area += first[0] * second[1] - second[0] * first[1];
            }
            return area / 2;
        }


        var result = new List<List<double[]>>();
        foreach (var source in sourcePolygons)
        {
            for (int uIndex = 0; uIndex + 1 < uSamples.Count; uIndex++)
            {
                var uClipped = ClipBoundary(source, 0, uSamples[uIndex], keepGreater: true);
                uClipped = ClipBoundary(uClipped, 0, uSamples[uIndex + 1], keepGreater: false);
                int vCellCount = allVCells ? vSamples.Count - 1 : 1;
                for (int vIndex = 0; vIndex < vCellCount; vIndex++)
                {
                    var polygon = uClipped;
                    if (vCellCount > 1)
                    {
                        polygon = ClipBoundary(polygon, 1, vSamples[vIndex], keepGreater: true);
                        polygon = ClipBoundary(polygon, 1, vSamples[vIndex + 1], keepGreater: false);
                    }
                    polygon = CleanUv(polygon);
                    if (polygon.Count < 3 || Math.Abs(SignedArea(polygon)) < 1e-14) continue;
                    var world = polygon.Select(point => evaluate(point[0], point[1])).ToList();
                    if (world.All(point => point.All(IsFinite))) result.Add(world);
                }
            }
        }
        return result;
    }

    private static (double[] Normal, double Area) NewellNormalArea(List<double[]> ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        double area = len / 2;
        if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
        return (new[] { nx, ny, nz }, area);
    }

    internal static List<List<double[]>> OrderRingsW(
        List<(double[] A, double[] B)> edges, bool includeContainingCycles = false,
        bool preferLargestCyclePerComponent = false)
    {
        // The IFC writer uses a 0.01 mm coordinate lattice. Vertices in different lattice
        // bins must remain separate here; merging them can remove a shared
        // boundary sample from a planar cap while the adjacent analytic face retains it.
        const double Tol = 1e-5 / 0.3048;
        static (long X, long Y, long Z) Key(double[] p) =>
            ((long)Math.Round(p[0] / Tol), (long)Math.Round(p[1] / Tol), (long)Math.Round(p[2] / Tol));

        // Build a geometric, undirected edge graph. The GRep can repeat the same segment
        // through several topology records; collapsing those duplicates prevents a valid
        // loop from being consumed by an arbitrary parallel copy.
        var nodeByKey = new Dictionary<(long, long, long), int>();
        var points = new List<double[]>();
        int Node(double[] p)
        {
            var key = Key(p);
            if (nodeByKey.TryGetValue(key, out int n)) return n;
            n = points.Count;
            nodeByKey[key] = n;
            points.Add(p);
            return n;
        }

        var graphEdges = new List<(int A, int B)>();
        var seenEdges = new HashSet<(int, int)>();
        foreach (var edge in edges)
        {
            int a = Node(edge.A), b = Node(edge.B);
            if (a == b) continue;
            var ek = a < b ? (a, b) : (b, a);
            if (seenEdges.Add(ek)) graphEdges.Add((a, b));
        }
        if (graphEdges.Count < 3) return new();

        var adjacent = Enumerable.Range(0, points.Count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < graphEdges.Count; i++)
        {
            adjacent[graphEdges[i].A].Add(i);
            adjacent[graphEdges[i].B].Add(i);
        }

        // Peel open chains. They are common after a recovered stream gap, but can never
        // participate in a Face boundary. The remaining 2-core contains only cycles.
        var active = Enumerable.Repeat(true, graphEdges.Count).ToArray();
        var degree = adjacent.Select(a => a.Count).ToArray();
        var peel = new Queue<int>(Enumerable.Range(0, points.Count).Where(i => degree[i] < 2));
        while (peel.Count > 0)
        {
            int node = peel.Dequeue();
            foreach (int edgeIndex in adjacent[node])
            {
                if (!active[edgeIndex]) continue;
                active[edgeIndex] = false;
                var e = graphEdges[edgeIndex];
                int other = e.A == node ? e.B : e.A;
                degree[node]--;
                if (--degree[other] == 1) peel.Enqueue(other);
            }
        }

        var componentByNode = Enumerable.Repeat(-1, points.Count).ToArray();
        int componentCount = 0;
        for (int seed = 0; seed < points.Count; seed++)
        {
            if (degree[seed] < 2 || componentByNode[seed] >= 0) continue;
            var queue = new Queue<int>();
            componentByNode[seed] = componentCount;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                foreach (int edgeIndex in adjacent[node])
                {
                    if (!active[edgeIndex]) continue;
                    var edge = graphEdges[edgeIndex];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (componentByNode[other] >= 0) continue;
                    componentByNode[other] = componentCount;
                    queue.Enqueue(other);
                }
            }
            componentCount++;
        }

        // For every surviving edge, find the shortest alternate path between its ends.
        // Together they form the smallest cycle containing that edge. Dedup by edge set;
        // on ordinary degree-2 loops every edge discovers the exact same boundary.
        var candidates = new List<(List<double[]> Ring, HashSet<int> Edges, double Area, int Component)>();
        var seenCycles = new HashSet<string>();
        for (int excluded = 0; excluded < graphEdges.Count; excluded++)
        {
            if (!active[excluded]) continue;
            var target = graphEdges[excluded];
            var prevNode = Enumerable.Repeat(-1, points.Count).ToArray();
            var prevEdge = Enumerable.Repeat(-1, points.Count).ToArray();
            var queue = new Queue<int>();
            prevNode[target.A] = target.A;
            queue.Enqueue(target.A);
            while (queue.Count > 0 && prevNode[target.B] < 0)
            {
                int node = queue.Dequeue();
                foreach (int edgeIndex in adjacent[node])
                {
                    if (edgeIndex == excluded || !active[edgeIndex]) continue;
                    var edge = graphEdges[edgeIndex];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (prevNode[other] >= 0) continue;
                    prevNode[other] = node;
                    prevEdge[other] = edgeIndex;
                    queue.Enqueue(other);
                }
            }
            if (prevNode[target.B] < 0) continue;

            var cycleNodes = new List<int>();
            var cycleEdges = new HashSet<int> { excluded };
            for (int node = target.B; ; node = prevNode[node])
            {
                cycleNodes.Add(node);
                if (node == target.A) break;
                cycleEdges.Add(prevEdge[node]);
            }
            cycleNodes.Reverse();
            if (cycleNodes.Count < 3) continue;
            string cycleKey = string.Join(',', cycleEdges.OrderBy(i => i));
            if (!seenCycles.Add(cycleKey)) continue;
            var ring = cycleNodes.Select(i => points[i]).ToList();
            double area = NewellNormalArea(ring).Area;
            if (area >= 1e-8) candidates.Add((ring, cycleEdges, area, componentByNode[target.A]));
        }

        // Shortest paths can prefer a collinear zero-area shortcut and never expose the
        // real inner cycle (roof reveals are a common example). For small graphs enumerate
        // every simple cycle as well; the budget keeps pathological blocks bounded.
        int activeEdgeCount = active.Count(x => x);
        if (points.Count <= 48 && activeEdgeCount <= 64)
        {
            int cycleBudget = 4096;
            var visited = new bool[points.Count];
            void SearchCycles(int start, int node, List<int> pathNodes, List<int> pathEdges)
            {
                if (cycleBudget <= 0 || pathNodes.Count > 32) return;
                foreach (int edgeIndex in adjacent[node])
                {
                    if (!active[edgeIndex]) continue;
                    var edge = graphEdges[edgeIndex];
                    int other = edge.A == node ? edge.B : edge.A;
                    if (other == start)
                    {
                        if (pathNodes.Count < 3) continue;
                        var cycleEdges = new HashSet<int>(pathEdges) { edgeIndex };
                        string cycleKey = string.Join(',', cycleEdges.OrderBy(i => i));
                        if (!seenCycles.Add(cycleKey)) continue;
                        cycleBudget--;
                        var ring = pathNodes.Select(i => points[i]).ToList();
                        double area = NewellNormalArea(ring).Area;
                        if (area >= 1e-8)
                            candidates.Add((ring, cycleEdges, area, componentByNode[start]));
                        continue;
                    }
                    // The start is the smallest node in the cycle. This removes rotated
                    // duplicates; the edge-set key above removes the reverse traversal.
                    if (other < start || visited[other]) continue;
                    visited[other] = true;
                    pathNodes.Add(other);
                    pathEdges.Add(edgeIndex);
                    SearchCycles(start, other, pathNodes, pathEdges);
                    pathEdges.RemoveAt(pathEdges.Count - 1);
                    pathNodes.RemoveAt(pathNodes.Count - 1);
                    visited[other] = false;
                }
            }

            for (int start = 0; start < points.Count && cycleBudget > 0; start++)
            {
                if (componentByNode[start] < 0) continue;
                visited[start] = true;
                SearchCycles(start, start, new List<int> { start }, new List<int>());
                visited[start] = false;
            }
        }

        if (!includeContainingCycles)
        {
            var primary = new List<List<double[]>>();
            foreach (var component in candidates.GroupBy(c => c.Component).OrderBy(group => group.Key))
                primary.Add((preferLargestCyclePerComponent
                        ? component.OrderByDescending(cycle => Math.Round(cycle.Area, 8))
                            .ThenByDescending(cycle => ExtentScore(cycle.Ring))
                            .ThenByDescending(cycle => cycle.Ring.Count)
                        : component.OrderBy(cycle => Math.Round(cycle.Area, 8))
                            .ThenBy(cycle => ExtentScore(cycle.Ring))
                            .ThenBy(cycle => cycle.Ring.Count))
                    .First().Ring);
            return primary;
        }

        // A recovered graph component may contain an outer EdgeLoop and opening EdgeLoops
        // joined by non-boundary records. The original minimum cycle remains authoritative
        // unless there is an edge-disjoint cycle that geometrically contains it; in that
        // exact case the containing cycle is the exterior and the minimum cycle is a hole.
        // Composite cycles reuse boundary edges and therefore cannot satisfy this relation.
        var result = new List<List<double[]>>();
        static double ExtentScore(List<double[]> ring)
        {
            double dx = ring.Max(p => p[0]) - ring.Min(p => p[0]);
            double dy = ring.Max(p => p[1]) - ring.Min(p => p[1]);
            double dz = ring.Max(p => p[2]) - ring.Min(p => p[2]);
            return dx * dx + dy * dy + dz * dz;
        }
        foreach (var component in candidates.GroupBy(c => c.Component).OrderBy(g => g.Key))
        {
            var cycles = component.OrderBy(c => c.Area)
                .ThenBy(c => ExtentScore(c.Ring))
                .ThenBy(c => c.Ring.Count).ToList();
            var primary = cycles[0];
            var outerMatch = cycles.Skip(1)
                .Where(cycle => !cycle.Edges.Overlaps(primary.Edges))
                .Select(cycle =>
                {
                    bool contains = RingContains(cycle.Ring, primary.Ring, out bool touchesBoundary);
                    return (Cycle: cycle, Contains: contains, TouchesBoundary: touchesBoundary);
                })
                .Where(match => match.Contains)
                .OrderByDescending(match => match.Cycle.Area)
                .ThenByDescending(match => ExtentScore(match.Cycle.Ring))
                .FirstOrDefault();
            if (outerMatch.Cycle.Ring == null)
            {
                result.Add(primary.Ring);
                continue;
            }

            var outer = outerMatch.Cycle;
            result.Add(outer.Ring);
            // A true hole is strictly inside the outer boundary. A cycle touching that
            // boundary describes a notch/opening cut and must not become an IfcFaceBound.
            if (!outerMatch.TouchesBoundary) result.Add(primary.Ring);
            var selectedEdges = new HashSet<int>();
            selectedEdges.UnionWith(outer.Edges);
            selectedEdges.UnionWith(primary.Edges);
            foreach (var cycle in cycles)
            {
                if (cycle.Edges.Overlaps(selectedEdges)
                    || !RingContains(outer.Ring, cycle.Ring, out bool touchesBoundary)
                    || touchesBoundary) continue;
                result.Add(cycle.Ring);
                selectedEdges.UnionWith(cycle.Edges);
            }
        }
        return result.OrderByDescending(ring => NewellNormalArea(ring).Area).ToList();

        static bool RingContains(IReadOnlyList<double[]> outer, IReadOnlyList<double[]> inner,
            out bool touchesBoundary)
        {
            touchesBoundary = false;
            var normal = NewellNormalArea(outer.ToList()).Normal;
            int dropAxis = Math.Abs(normal[0]) >= Math.Abs(normal[1])
                && Math.Abs(normal[0]) >= Math.Abs(normal[2]) ? 0
                : Math.Abs(normal[1]) >= Math.Abs(normal[2]) ? 1 : 2;
            (double X, double Y) Project(double[] point) => dropAxis switch
            {
                0 => (point[1], point[2]),
                1 => (point[0], point[2]),
                _ => (point[0], point[1]),
            };
            var polygon = outer.Select(Project).ToArray();
            foreach (var point3d in inner)
            {
                var point = Project(point3d);
                bool inside = false;
                bool onBoundary = false;
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    var a = polygon[j];
                    var b = polygon[i];
                    double cross = (b.X - a.X) * (point.Y - a.Y)
                        - (b.Y - a.Y) * (point.X - a.X);
                    if (cross == 0
                        && point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X)
                        && point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y))
                    {
                        onBoundary = true;
                        break;
                    }
                    if ((a.Y > point.Y) != (b.Y > point.Y)
                        && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
                if (onBoundary)
                {
                    touchesBoundary = true;
                    continue;
                }
                if (!inside) return false;
            }
            return true;
        }
    }

}
