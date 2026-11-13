namespace RvtIO.Geometry.Meshing;

using Pt = (double U, double V);

internal static class RevolvedMesher
{
    private const double TolUv = 1e-10;
    private const double TolNormal = 0.1;
    private const double TolPt3 = 1e-10;
    private const double PoleTol = 1e-10;
    private static readonly bool RefuseLoopOutsideEnvelope = Environment.GetEnvironmentVariable("RVTIO_X_REVENV") == "1";

    /// <summary>Refuses (false) every face the rules do not cover: more than one loop, a NURBS or missing edge curve, a missing
    /// uv fit, a reversed normal on an unflipped face, an oblique loop piece. Never throws for such input.</summary>
    /// <summary>Half-circle arc numbers of the last meshes (diagnostics only, RVTIO_KNIFE=1).</summary>
    [ThreadStatic] internal static List<string>? Knife;

    /// <summary>Why the last face was refused (diagnostics only).</summary>
    [ThreadStatic] internal static string? LastRefusal;

    internal static bool TryMesh(BrFace face, double surfaceTolerance, double maxEdgeLength, out Mesh mesh)
    {
        mesh = null!;
        LastRefusal = null;
        if (face.Revolved is not { } surf || face.Loops.Count == 0 || face.Loops.Any(l => l.Coedges.Count == 0)) { LastRefusal = "no surface or empty loop"; return false; }
        if (!(maxEdgeLength > 0.0) || double.IsInfinity(maxEdgeLength) || !(surfaceTolerance > 0.0)) { LastRefusal = "tolerances"; return false; }
        if (!ValidSurface(surf)) { LastRefusal = "invalid surface"; return false; }
        try
        {
            var eval = new RevolvedEval(surf);
            var env = surf.GeEnvelope;
            var built = face.Loops.Select(l => BuildLoop(l, surf, surfaceTolerance, maxEdgeLength)).ToList();
            // a loop inside an odd number of other loops is a hole (clockwise), the others are outer loops (counter-clockwise); every outer loop
            // with the holes inside it is a region of its own that is meshed apart (a face cut into pieces by a gap)
            double LoopArea(List<Pt> lp) { double s = 0; for (int i = 0; i + 1 < lp.Count; i++) s += lp[i].U * lp[i + 1].V - lp[i + 1].U * lp[i].V; return Math.Abs(s) * 0.5; }
            var depth = built.Select((lp, i) => built.Where((o, j) => j != i && Inside(o, lp[0])).Count()).ToArray();
            var regions = new List<List<List<Pt>>>();
            var outerOf = new Dictionary<int, int>();
            for (int i = 0; i < built.Count; i++)
                if (depth[i] % 2 == 0) { outerOf[i] = regions.Count; regions.Add(new() { built[i] }); }
            for (int i = 0; i < built.Count; i++)
                if (depth[i] % 2 == 1)
                {
                    int host = Enumerable.Range(0, built.Count).Where(j => depth[j] % 2 == 0 && Inside(built[j], built[i][0])).OrderBy(j => LoopArea(built[j])).First();
                    var h = new List<Pt>(built[i]); h.Reverse();
                    regions[outerOf[host]].Add(h);
                }
            var meshes = new List<Mesh>();
            foreach (var loops in regions)
            {
                if (!TryMeshRegion(face, surf, eval, env, surfaceTolerance, maxEdgeLength, loops, out var part)) { LastRefusal ??= "region"; return false; }
                meshes.Add(part);
            }
            if (meshes.Count == 1) mesh = meshes[0];
            else
            {
                var vs = new List<double[]>(); var ts = new List<int[]>();
                foreach (var part in meshes)
                {
                    int off = vs.Count;
                    vs.AddRange(part.Vertices);
                    ts.AddRange(part.Triangles.Select(t => new[] { t[0] + off, t[1] + off, t[2] + off }));
                }
                mesh = new Mesh { Vertices = vs.ToArray(), Triangles = ts.ToArray() };
            }
            return mesh.Triangles.Length > 0;
        }
        catch (Exception ex) when (ex is RevolvedUnsupportedException or IndexOutOfRangeException or ArgumentOutOfRangeException
            or KeyNotFoundException or InvalidOperationException or NullReferenceException or DivideByZeroException)
        {
            LastRefusal = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool Inside(List<Pt> polygon, Pt p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].V > p.V) != (polygon[j].V > p.V)
                && p.U < (polygon[j].U - polygon[i].U) * (p.V - polygon[i].V) / (polygon[j].V - polygon[i].V) + polygon[i].U)
                inside = !inside;
        return inside;
    }

    /// <summary>The mesh of one region: its outer loop first, then its holes (clockwise).</summary>
    private static bool TryMeshRegion(BrFace face, RevolvedSurface surf, RevolvedEval eval, double[] env, double surfaceTolerance,
        double maxEdgeLength, List<List<Pt>> loops, out Mesh mesh)
    {
        mesh = null!;
        {
            var loop = loops[0];
            if (RefuseLoopOutsideEnvelope && loops.Any(lp => lp.Any(p => p.U < env[0] - TolUv || p.U > env[1] + TolUv || p.V < env[2] - TolUv || p.V > env[3] + TolUv)))
            { LastRefusal = "loop outside the envelope"; return false; }
            // a loop that is not the border of the envelope (a trimmed face): the grid starts at the box of the loop
            double[] root = { loop.Min(p => p.U), loop.Max(p => p.U), loop.Min(p => p.V), loop.Max(p => p.V) };
            bool rectangular = loops.All(RevolvedCells.IsAxisParallel);
            if (rectangular) root = env;
            var (cells, gridPoints) = RevolvedGrid.Build(eval, root, surfaceTolerance, maxEdgeLength);
            var items = RevolvedCells.CellWork(cells, loops);
            var (recs, blocks) = BuildBlocks(cells, gridPoints, items);
            bool cw = face.Flipped == surf.NormalReversed;
            mesh = Emit(eval, env, recs, blocks, loops, cw);
            if (mesh.Triangles.Length == 0) LastRefusal = "no triangles";
            return mesh.Triangles.Length > 0;
        }
    }

    private static bool Fin3(double[]? p) => p is { Length: 3 } && double.IsFinite(p[0]) && double.IsFinite(p[1]) && double.IsFinite(p[2]);

    private static bool ValidSurface(RevolvedSurface s)
    {
        if (!Fin3(s.Origin) || !Fin3(s.Axis) || !double.IsFinite(s.V0) || !double.IsFinite(s.U0)) return false;
        if (s.GeEnvelope is not { Length: 4 } e || e.Any(x => !double.IsFinite(x)) || !(e[1] > e[0]) || !(e[3] > e[2])) return false;
        if (s.ProfileAt != null) return true;
        if (s.ProfileStart != null) return Fin3(s.ProfileStart) && Fin3(s.ProfileDir);
        return Fin3(s.ArcCenter) && Fin3(s.ArcE1) && Fin3(s.ArcE2) && s.ArcRadius > 0 && double.IsFinite(s.ArcRadius) && Math.Abs(s.ArcEps) == 1.0;
    }

    private static double[] Cross(double[] a, double[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    private static double Norm(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

    private static Pt Snap(Pt uv, double[] env)
    {
        double u = uv.U, v = uv.V;
        if (Math.Abs(u - env[0]) > TolUv) { if (Math.Abs(u - env[1]) <= TolUv) u = env[1]; }
        else u = env[0];
        if (Math.Abs(v - env[2]) > TolUv) { if (Math.Abs(v - env[3]) <= TolUv) v = env[3]; }
        else v = env[2];
        return (u, v);
    }

    /// <summary>Uv samples of one coedge in the direction of its edge curve.</summary>
    private static List<Pt> EdgeSamples(MeshCoedge co, IReadOnlyList<Pt> fit, double[] env, double tol, double maxLen, double sg)
    {
        var curve = co.Curve!;
        if (curve.Kind == CurveKind.Nurbs)
        {
            // an interpolated edge: the sample parameters of the 3D curve, the uv from the parameter curve of the same knots
            var nb = curve.Nurbs ?? throw new RevolvedUnsupportedException("nurbs edge without parameters");
            var uvCurve = co.ParamCurve?.Nurbs ?? throw new RevolvedUnsupportedException("nurbs edge without parameter curve");
            if (nb.Weights != null || uvCurve.Weights != null) throw new RevolvedUnsupportedException("rational edge curve");
            double lo = nb.Knots[nb.Degree], hi = nb.Knots[nb.ControlPoints.Length];
            var raw = NurbsEval.Tessellate3d(nb, lo, hi, tol * 0.5);
            double gap = Math.Sqrt(Math.Pow(nb.ControlPoints[0][0] - nb.ControlPoints[^1][0], 2) + Math.Pow(nb.ControlPoints[0][1] - nb.ControlPoints[^1][1], 2) + Math.Pow(nb.ControlPoints[0][2] - nb.ControlPoints[^1][2], 2));
            int minCount = gap <= 1e-4 ? 6 : 4;
            if (raw.Count < minCount)
            {
                raw.Clear();
                for (int i = 0; i < minCount; i++) raw.Add(i * (hi - lo) / (minCount - 1) + lo);
            }
            var ts = ArcSampler.BisectLong(raw, t => NurbsEval.Point3d(nb, t), maxLen);
            var smp = new List<Pt>(ts.Count);
            foreach (double t in ts)
            {
                var p = NurbsEval.Point2d(uvCurve, t);   // Compact face order (rotation, profile)
                smp.Add(Snap((p.Y, sg * p.X), env));
            }
            return smp;
        }
        var pc = RevolvedPCurve.Fit(fit) ?? throw new RevolvedUnsupportedException("uv fit curve");
        if (curve.Kind == CurveKind.Arc)
        {
            var a = curve.Arc ?? throw new RevolvedUnsupportedException("arc");
            if (!(a.Radius > 0.0) || !double.IsFinite(a.Radius) || !Fin3(a.Center) || !Fin3(a.Normal) || !Fin3(a.Ref)
                || !double.IsFinite(a.Start) || !double.IsFinite(a.End) || !Fin3(curve.P0) || !Fin3(curve.P1))
                throw new RevolvedUnsupportedException("arc");
            double pa = ArcSampler.ParamOf(a, curve.P0!), pb = ArcSampler.ParamOf(a, curve.P1!);
            var (ps, pe) = pa <= pb ? (curve.P0!, curve.P1!) : (curve.P1!, curve.P0!);
            // the circle about the axis (and the circle of a cone) takes the stored rotation range of the edge: re-measuring it
            // on the vertices adds rounding noise that decides the interval count of a half circle, an exact multiple of the
            // pi/4 step
            var (from, to) = a.EdgeRange ?? ArcSampler.RendererInterval(a, ps, pe);
            if (to == from) throw new RevolvedUnsupportedException("empty arc interval");
            if (Environment.GetEnvironmentVariable("RVTIO_KNIFE") == "1" && Math.Abs((to - from) / (Math.PI / 4) - Math.Round((to - from) / (Math.PI / 4))) < 1e-6)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                string F(double v) => v.ToString("R", ic);
                string V(double[] v) => string.Join(",", v.Select(F));
                (Knife ??= new()).Add($"[meshknife] R={F(a.Radius)} start={F(a.Start)} end={F(a.End)} pa={F(pa)} pb={F(pb)} from={F(from)} to={F(to)} edgeRange={(a.EdgeRange == null ? "-" : F(a.EdgeRange.Value.Item1) + ":" + F(a.EdgeRange.Value.Item2))}"
                    + $" center={V(a.Center)} normal={V(a.Normal)} ref={V(a.Ref)} p0={V(curve.P0!)} p1={V(curve.P1!)} tol={F(tol)}");
            }
            var ts = ArcSampler.ArcParams(a.Radius, from, to, tol, a.End - a.Start > ArcSampler.LargeArc);
            var k = pc.OnInterval(a.Start, a.End);
            var res = new List<Pt>(ts.Length);
            foreach (double t0 in ts)
            {
                double t = t0;
                for (int g = 0; t < k.Lo - 1e-9; g++) { t += ArcSampler.TwoPi; if (g > 16) throw new RevolvedUnsupportedException("arc parameter"); }
                for (int g = 0; t > k.Hi + 1e-9; g++) { t -= ArcSampler.TwoPi; if (g > 16) throw new RevolvedUnsupportedException("arc parameter"); }
                var p = k.Eval(t);
                res.Add(Snap((p.X, p.Y), env));
            }
            return res;
        }
        if (curve.Kind != CurveKind.Line || !Fin3(curve.P0) || !Fin3(curve.P1)) throw new RevolvedUnsupportedException("curve kind");
        double length = Norm(curve.P1![0] - curve.P0![0], curve.P1[1] - curve.P0[1], curve.P1[2] - curve.P0[2]);
        int n = PlanarMesher.Pow2Count(length, maxLen);
        var kl = pc.OnInterval(0.0, 1.0);
        var line = new List<Pt>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            var p = kl.Eval((double)i / n);
            line.Add(Snap((p.X, p.Y), env));
        }
        return line;
    }

    private static bool Near(Pt a, Pt b) => Math.Abs(a.U - b.U) < 1e-9 && Math.Abs(a.V - b.V) < 1e-9;

    /// <summary>The closed counter-clockwise uv loop (last = first) in mesher order (u = profile, v = rotation).</summary>
    private static List<Pt> BuildLoop(Loop loop, RevolvedSurface surf, double tol, double maxLen)
    {
        double sg = surf.NormalReversed ? -1.0 : 1.0;
        var env = surf.GeEnvelope;
        var cyc = new List<Pt>();
        foreach (var co in loop.Coedges)
        {
            if (co.Curve == null || co.UvStart is not { Length: 2 } us || co.UvEnd is not { Length: 2 } ue || co.UvFit is not { Length: >= 2 } fitRaw)
                throw new RevolvedUnsupportedException("coedge data");
            if (fitRaw.Any(q => q.Length != 2 || !double.IsFinite(q[0]) || !double.IsFinite(q[1])) || !double.IsFinite(us[0]) || !double.IsFinite(us[1])
                || !double.IsFinite(ue[0]) || !double.IsFinite(ue[1])) throw new RevolvedUnsupportedException("coedge data");
            // Compact face order (rotation, profile) -> mesher order (profile, rotation)
            var fit = fitRaw.Select(q => (q[1], sg * q[0])).ToList();
            Pt uv0 = (us[1], sg * us[0]);
            var smp = EdgeSamples(co, fit, env, tol, maxLen, sg);
            // the first sample must be at the start of the coedge
            double dFirst = Math.Abs(smp[0].U - uv0.U) + Math.Abs(smp[0].V - uv0.V);
            double dLast = Math.Abs(smp[^1].U - uv0.U) + Math.Abs(smp[^1].V - uv0.V);
            if (dFirst > dLast) smp.Reverse();
            int skip = cyc.Count > 0 && Near(cyc[^1], smp[0]) ? 1 : 0;
            for (int i = skip; i < smp.Count; i++) cyc.Add(smp[i]);
        }
        if (cyc.Count < 3) throw new RevolvedUnsupportedException("loop");
        if (Near(cyc[^1], cyc[0])) cyc.RemoveAt(cyc.Count - 1);
        double area = 0.0;
        for (int i = 0; i < cyc.Count; i++)
        {
            var a = cyc[i]; var b = cyc[(i + 1) % cyc.Count];
            area += a.U * b.V - b.U * a.V;
        }
        var res = new List<Pt> { cyc[0] };
        if (area < 0) for (int i = cyc.Count - 1; i >= 1; i--) res.Add(cyc[i]);
        else for (int i = 1; i < cyc.Count; i++) res.Add(cyc[i]);
        res.Add(cyc[0]);
        return res;
    }

    private static bool EqAxis(Pt a, Pt b, double t) => Math.Abs(a.U - b.U) <= t && Math.Abs(a.V - b.V) <= t;

    /// <summary>Records (grid points first) and the triangle blocks [a, b, c] (record indices) of all items.</summary>
    private static (List<Pt> Recs, List<int> Blocks) BuildBlocks(List<RevolvedGrid.Cell> cells, List<(double U, double V)> gridPoints,
        List<RevolvedCells.Item> items)
    {
        if (cells.Count == 1)
        {
            // one cell: the records are the points of the loop ring, the grid corners are not records of their own
            var only = items[0].Ring!.Select(p => (p.U, p.V)).ToList();
            var oneLoops = new List<int[]> { Enumerable.Range(0, only.Count).ToArray() };
            foreach (var hole in items[0].Holes ?? new List<List<Pt>>())
            {
                oneLoops.Add(Enumerable.Range(only.Count, hole.Count).ToArray());
                only.AddRange(hole.Select(p => (p.U, p.V)));
            }
            var single = new List<int>();
            foreach (var (a, b, c) in GeTess.TriangulateProfile(only, oneLoops, oneLoops.Select((_, i) => i == 0).ToArray()))
                single.AddRange(new[] { 3, a, b, c });
            return (only, single);
        }
        var recs = new List<Pt>(gridPoints);
        // grid point ids of the T-vertices (the later split of an equal point wins, the first position is kept)
        var tIndex = new Dictionary<Pt, int>();
        var tList = new List<(Pt P, int Id)>();
        void AddT(Pt p, int id)
        {
            if (tIndex.TryGetValue(p, out int at)) tList[at] = (p, id);
            else { tIndex[p] = tList.Count; tList.Add((p, id)); }
        }
        foreach (var c in cells)
        {
            if (c.Dir < 0) continue;
            var ch0 = cells[c.Ch0];
            int p = ch0.Cn[2], q = c.Dir == 0 ? ch0.Cn[3] : ch0.Cn[1];
            if (c.Dir == 1) { AddT((c.U1, c.Val), p); AddT((c.U0, c.Val), q); }
            else { AddT((c.Val, c.V1), p); AddT((c.Val, c.V0), q); }
        }
        int Find(Pt p)
        {
            for (int i = 0; i < recs.Count; i++) if (EqAxis(p, recs[i], TolUv)) return i;
            recs.Add(p);
            return recs.Count - 1;
        }
        var blocks = new List<int>();
        foreach (var it in items)
        {
            var l = it.Cell;
            if (it.IsQuad)
            {
                int c0 = l.Cn[0], c1 = l.Cn[1], c2 = l.Cn[2], c3 = l.Cn[3];
                blocks.AddRange(new[] { 3, c0, c1, c2, 3, c0, c2, c3 });
                continue;
            }
            var all = new List<Pt>(it.Ring!);
            var ringLoops = new List<int[]> { Enumerable.Range(0, all.Count).ToArray() };
            foreach (var hole in it.Holes ?? new List<List<Pt>>())
            {
                ringLoops.Add(Enumerable.Range(all.Count, hole.Count).ToArray());
                all.AddRange(hole);
            }
            var corners = new Pt[] { (l.U0, l.V0), (l.U0, l.V1), (l.U1, l.V1), (l.U1, l.V0) };
            var ids = new int[all.Count];
            for (int r = 0; r < all.Count; r++)
            {
                var p = all[r];
                int idx = -1;
                for (int k = 0; k < 4; k++) if (EqAxis(p, corners[k], TolUv)) { idx = l.Cn[k]; break; }
                if (idx < 0)
                    foreach (var (tp, ti) in tList) if (EqAxis(p, tp, TolUv)) { idx = ti; break; }
                if (idx < 0) idx = Find(p);
                ids[r] = idx;
            }
            foreach (var (a, b, c) in GeTess.TriangulateProfile(all.Select(p => (p.U, p.V)).ToList(), ringLoops, ringLoops.Select((_, i) => i == 0).ToArray()))
                blocks.AddRange(new[] { 3, ids[a], ids[b], ids[c] });
        }
        return (recs, blocks);
    }

    /// <summary>Vertex table: a vertex with the same uv (window 1e-10) and an equal normal is reused, else one at the same 3D
    /// position (window 1e-8, accepted within 1e-10) with an equal normal.</summary>
    private sealed class VertexTable
    {
        private const double Cell = 1e-8;
        private readonly List<Pt> uv = new();
        private readonly List<double[]> p3 = new();
        private readonly List<double[]> nrm = new();
        private readonly Dictionary<(long, long), List<int>> huv = new();
        private readonly Dictionary<(long, long, long), List<int>> h3 = new();

        public int Count => uv.Count;
        public double[] Position(int i) => p3[i];

        private static List<int> Candidates<TK>(Dictionary<TK, List<int>> table, IEnumerable<TK> keys) where TK : notnull
        {
            var o = new List<int>();
            foreach (var k in keys) if (table.TryGetValue(k, out var l)) o.AddRange(l);
            o.Sort();
            return o;
        }

        private int Find(List<int> cands, Func<int, double> dist, Func<int, bool> window, double[] n)
        {
            double bestAny = 1e300, bestEq = 1e300;
            int res = -1;
            foreach (int i in cands)
            {
                double d2 = dist(i);
                if (!window(i)) continue;
                if (bestAny > d2)
                {
                    bestAny = d2;
                    var m = nrm[i];
                    if (Norm(m[0] - n[0], m[1] - n[1], m[2] - n[2]) <= TolNormal) { bestEq = d2; res = i; }
                }
            }
            return res >= 0 && bestEq <= TolUv * TolUv ? res : -1;
        }

        public int Add(Pt p, double[] pos, double[] n)
        {
            long kx = (long)Math.Floor(p.U / Cell), ky = (long)Math.Floor(p.V / Cell);
            var ku = new List<(long, long)>();
            for (long a = -1; a <= 1; a++) for (long b = -1; b <= 1; b++) ku.Add((kx + a, ky + b));
            int i = Find(Candidates(huv, ku),
                j => (uv[j].U - p.U) * (uv[j].U - p.U) + (uv[j].V - p.V) * (uv[j].V - p.V),
                j => Math.Abs(uv[j].U - p.U) <= TolUv && Math.Abs(uv[j].V - p.V) <= TolUv, n);
            long x = (long)Math.Floor(pos[0] / Cell), y = (long)Math.Floor(pos[1] / Cell), z = (long)Math.Floor(pos[2] / Cell);
            if (i < 0)
            {
                var k3 = new List<(long, long, long)>();
                for (long a = -1; a <= 1; a++) for (long b = -1; b <= 1; b++) for (long c = -1; c <= 1; c++) k3.Add((x + a, y + b, z + c));
                i = Find(Candidates(h3, k3),
                    j => { var q = p3[j]; double a = q[0] - pos[0], b = q[1] - pos[1], c = q[2] - pos[2]; return a * a + b * b + c * c; },
                    j => { var q = p3[j]; return Math.Abs(q[0] - pos[0]) <= 100.0 * TolUv && Math.Abs(q[1] - pos[1]) <= 100.0 * TolUv && Math.Abs(q[2] - pos[2]) <= 100.0 * TolUv; },
                    n);
            }
            if (i < 0)
            {
                i = uv.Count;
                uv.Add(p); p3.Add(pos); nrm.Add(n);
                if (!huv.TryGetValue((kx, ky), out var l1)) huv[(kx, ky)] = l1 = new List<int>();
                l1.Add(i);
                if (!h3.TryGetValue((x, y, z), out var l2)) h3[(x, y, z)] = l2 = new List<int>();
                l2.Add(i);
            }
            return i;
        }
    }

    /// <summary>Unit surface normal (sign is irrelevant, only differences are compared), the limit value at a pole.</summary>
    private static double[] NormalAt(RevolvedEval s, double[] env, double u, double v)
    {
        (double[] Cross, double PuNorm) Jac(double uu, double vv)
        {
            double h = 1e-6 * Math.Max(Math.Abs(env[1] - env[0]), 1e-12);
            var a = s.Pt(uu + h, vv); var b = s.Pt(uu - h, vv);
            var pu = new[] { (a[0] - b[0]) / (2 * h), (a[1] - b[1]) / (2 * h), (a[2] - b[2]) / (2 * h) };
            double hv = 1e-6 * Math.Max(Math.Abs(env[3] - env[2]), 1e-12);
            var c = s.Pt(uu, vv + hv); var d = s.Pt(uu, vv - hv);
            var pv = new[] { (c[0] - d[0]) / (2 * hv), (c[1] - d[1]) / (2 * hv), (c[2] - d[2]) / (2 * hv) };
            return (Cross(pu, pv), Norm(pu[0], pu[1], pu[2]));
        }
        var (j, puNorm) = Jac(u, v);
        double n = Norm(j[0], j[1], j[2]);
        if (n > 1e-7 * Math.Max(puNorm, 1.0)) return new[] { j[0] / n, j[1] / n, j[2] / n };
        double dd = 1e-4 * (env[1] - env[0]);
        double uu2 = Math.Abs(u - env[0]) < Math.Abs(u - env[1]) ? u + dd : u - dd;
        (j, _) = Jac(uu2, v);
        double m = Math.Max(Norm(j[0], j[1], j[2]), 1e-300);
        return new[] { j[0] / m, j[1] / m, j[2] / m };
    }

    /// <summary>
    /// Pole mask of the surface: 4 pole at u min, 1 at u max, 8 at v min, 2 at v max. A pole at the max side only counts
    /// together with a pole at the min side (a quirk of the mesher's surface property query).
    /// </summary>
    private static int PoleMask(RevolvedEval s, double[] env)
    {
        double u0 = env[0], u1 = env[1], v0 = env[2], v1 = env[3];
        var us = new List<double> { u0 }; var vs = new List<double> { v0 };
        double su = (u1 - u0) / 5.0, sv = (v1 - v0) / 5.0;
        for (int k = 1; k < 5; k++) { us.Add(us[^1] + su); vs.Add(vs[^1] + sv); }
        us.Add(u1); vs.Add(v1);
        var p00 = s.Pt(u0, v0); var p11 = s.Pt(u1, v1);
        bool Same(double[] a, double[] b) => Norm(a[0] - b[0], a[1] - b[1], a[2] - b[2]) <= PoleTol;
        bool uMin = vs.All(v => Same(p00, s.Pt(u0, v))), uMax = vs.All(v => Same(p11, s.Pt(u1, v)));
        bool vMin = us.All(u => Same(p00, s.Pt(u, v0))), vMax = us.All(u => Same(p11, s.Pt(u, v1)));
        int mask = 0;
        if (uMin) { mask |= 4; if (uMax) mask |= 1; }
        if (vMin) { mask |= 8; if (vMax) mask |= 2; }
        return mask;
    }

    private static bool IsLoopVertex(Pt p, List<Pt> loop)
    {
        for (int i = 0; i < loop.Count - 1; i++) if (EqAxis(p, loop[i], TolUv)) return true;
        return false;
    }

    private static bool OnLoopNotVertex(Pt p, List<Pt> loop)
    {
        if (IsLoopVertex(p, loop)) return false;
        for (int i = 0; i < loop.Count - 1; i++)
        {
            var a = loop[i]; var b = loop[i + 1];
            double dx = b.U - a.U, dy = b.V - a.V, l2 = dx * dx + dy * dy, d;
            if (l2 == 0) d = Math.Sqrt((p.U - a.U) * (p.U - a.U) + (p.V - a.V) * (p.V - a.V));
            else
            {
                double t = Math.Max(0.0, Math.Min(1.0, ((p.U - a.U) * dx + (p.V - a.V) * dy) / l2));
                double ex = p.U - a.U - t * dx, ey = p.V - a.V - t * dy;
                d = Math.Sqrt(ex * ex + ey * ey);
            }
            if (d <= TolUv) return true;
        }
        return false;
    }

    private static double Area(Pt a, Pt b, Pt c) => ((a.V - b.V) * (c.U - a.U) + (c.V - a.V) * (b.U - a.U)) * 0.5;

    private static Mesh Emit(RevolvedEval surf, double[] env, List<Pt> recs, List<int> blockList, List<List<Pt>> loops, bool cw)
    {
        int n = recs.Count, nblk = blockList.Count / 4;
        var blocks = blockList; // [3, a, b, c] per triangle; a leading 0 marks a removed triangle
        var used = new bool[n];
        for (int k = 0; k < nblk; k++) for (int j = 1; j <= 3; j++) used[blocks[4 * k + j]] = true;
        var b17 = recs.Select(r => !loops.Any(lp => IsLoopVertex(r, lp)) && loops.Any(lp => OnLoopNotVertex(r, lp))).ToArray();
        var vt = new VertexTable();
        var rec2v = new int[n];
        var vflag = new Dictionary<int, bool>();
        for (int i = 0; i < n; i++)
        {
            if (!used[i]) continue;
            var (u, v) = recs[i];
            int m = vt.Add((u, v), surf.Pt(u, v), NormalAt(surf, env, u, v));
            rec2v[i] = m;
            if (!vflag.TryGetValue(m, out bool fl) || fl) vflag[m] = b17[i];
        }
        void Fix(int bs)
        {
            var a = recs[blocks[bs + 1]]; var b = recs[blocks[bs + 2]]; var c = recs[blocks[bs + 3]];
            if ((Area(a, b, c) <= 0.0) == cw) return;
            (blocks[bs + 1], blocks[bs + 3]) = (blocks[bs + 3], blocks[bs + 1]);
        }
        for (int k = 0; k < nblk; k++) Fix(4 * k);
        int mask = PoleMask(surf, env);
        bool AtPole(int r)
        {
            var (u, v) = recs[r];
            return ((mask & 4) != 0 && Math.Abs(u - env[0]) <= 1e-9) || ((mask & 1) != 0 && Math.Abs(u - env[1]) <= 1e-9)
                || ((mask & 8) != 0 && Math.Abs(v - env[2]) <= 1e-9) || ((mask & 2) != 0 && Math.Abs(v - env[3]) <= 1e-9);
        }
        var star = new Dictionary<int, List<(int K, int J)>>();
        for (int k = 0; k < nblk; k++)
            for (int j = 0; j < 3; j++)
            {
                int r = blocks[4 * k + 1 + j];
                if (!b17[r] || AtPole(r)) continue;
                int m = rec2v[r];
                if (vflag.TryGetValue(m, out bool fl) && !fl) continue;
                if (!star.TryGetValue(m, out var l)) star[m] = l = new List<(int, int)>();
                l.Add((k, j));
            }
        int removed = 0;
        foreach (int m in star.Keys.OrderBy(x => x).ToList())
        {
            var entries = star[m];
            Pt center = default;
            bool haveCenter = false;
            var ring = new SortedDictionary<double, int>();
            var alive = new List<(int K, int J)>();
            foreach (var (k, j) in entries)
            {
                if (blocks[4 * k] == 0) continue;
                alive.Add((k, j));
                if (!haveCenter) { center = recs[blocks[4 * k + 1 + j]]; haveCenter = true; }
                for (int off = 1; off <= 2; off++)
                {
                    int w = blocks[4 * k + 1 + (j + off) % 3];
                    var q = recs[w];
                    double ang = Math.Atan2(q.V - center.V, q.U - center.U);
                    if (ang < 0) ang += 2.0 * Math.PI;
                    ring[2.0 * Math.PI - ang] = w;
                }
            }
            if (alive.Count == 0) continue;
            var order = ring.Values.ToArray();
            var pts = order.Select(w => (recs[w].U, recs[w].V)).ToList();
            var tris = GeTess.TriangulateProfile(pts, new[] { Enumerable.Range(0, pts.Count).ToArray() }, new[] { true });
            if (tris.Count == 0) continue;
            foreach (var (k, _) in alive) blocks[4 * k] = 0;
            foreach (var (a, b, c) in tris)
            {
                blocks.AddRange(new[] { 3, order[a], order[b], order[c] });
                int bs = blocks.Count - 4;
                Fix(bs);
                int k2 = bs / 4;
                for (int j = 0; j < 3; j++)
                {
                    int mw = rec2v[blocks[bs + 1 + j]];
                    if (mw > m && star.TryGetValue(mw, out var sl)) sl.Add((k2, j));
                }
            }
            removed++;
        }
        var tri = new List<int[]>();
        bool Eq3(double[] a, double[] b) => Norm(a[0] - b[0], a[1] - b[1], a[2] - b[2]) <= TolPt3;
        for (int k = 0; k < blocks.Count / 4; k++)
        {
            if (blocks[4 * k] == 0) continue;
            int a = rec2v[blocks[4 * k + 1]], b = rec2v[blocks[4 * k + 2]], c = rec2v[blocks[4 * k + 3]];
            if (Eq3(vt.Position(a), vt.Position(b)) || Eq3(vt.Position(b), vt.Position(c)) || Eq3(vt.Position(a), vt.Position(c))) continue;
            tri.Add(new[] { a, b, c });
        }
        var verts = Enumerable.Range(0, vt.Count).Select(i => vt.Position(i)).ToList();
        if (removed == 0) return new Mesh { Vertices = verts.ToArray(), Triangles = tri.ToArray() };
        // after a removal the vertex list is rebuilt in order of first use
        var remap = new Dictionary<int, int>();
        var ov = new List<double[]>();
        var ot = new int[tri.Count][];
        for (int t = 0; t < tri.Count; t++)
        {
            ot[t] = new int[3];
            for (int j = 0; j < 3; j++)
            {
                int i = tri[t][j];
                if (!remap.TryGetValue(i, out int ni)) { remap[i] = ni = ov.Count; ov.Add(verts[i]); }
                ot[t][j] = ni;
            }
        }
        return new Mesh { Vertices = ov.ToArray(), Triangles = ot };
    }
}
