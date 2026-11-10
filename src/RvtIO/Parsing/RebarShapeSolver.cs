namespace RvtIO.Parsing;

/// <summary>
/// The centre line of a shape driven bar that the file stores no cache for (Revit 2020 and 2022 keep the shape, the dimension values and the
/// hook types of the instance, not its curves), rebuilt from the RebarShapeDefinitionBySegments of the shape. Everything is in feet in the
/// plane frame of the shape (x, y; z is the plane normal) and is moved to the model by the shape frame of the instance.
///
/// Rules (straight bars, hooked bars, open and closed stirrups):
///   * The edges are placed one after the other from the origin along their fixed directions with the dimension of the instance as length:
///     the dimensions are measured between the outer surfaces of the bar, so the reference polygon is the outer one.
///   * The centre line of an edge is its reference line moved inwards by half the bar diameter, to the side of the turn of the vertex
///     (RebarShapeVertex.m_turn: +1 left, -1 right) at one of its ends; an edge between vertices without a turn stays on the reference line.
///   * A corner of 90 degrees is an arc of the bend radius (bend diameter of the bar type: stirrup / tie for a shape of style 1, standard
///     otherwise, plus the bar diameter, halved) tangent to both centre lines.
///   * A hook is an arc of its angle at the end of the end edge: the arc touches the reference end of the edge with its outer surface
///     (arc centre at radius + half diameter from the end along the edge), turns to the side of the hook orientation, and continues in a
///     straight leg of "multiplier * diameter" behind the arc, or, when the bar type fixes a tangent length for the hook type, until the
///     leg is that long measured from the point where leg and edge lines meet.
///   * When the two hooks of a closed shape collide (their centre lines come closer than a bar diameter) the last edge and the end hook are
///     moved by one diameter along the minus normal; the move is spread over the edge in front of it, which therefore leans (the stirrup
///     of four edges that turn left; no other case was seen, any other collision yields no curve).
/// Anything not covered by these rules yields null.
/// </summary>
internal static class RebarShapeSolver
{
    /// <summary>A hook: its angle (radians, at least a quarter turn, at most a half turn), the bend radius of its centre line, the side it
    /// turns to seen along the bar (+1 left, -1 right), and the length of its leg: <paramref name="TangentLength"/> from the meeting point of
    /// leg and edge when the bar type fixes one, else <paramref name="Multiplier"/> bar diameters behind the arc.</summary>
    internal sealed record Hook(double Angle, double Radius, int Side, double? TangentLength, double Multiplier);

    /// <param name="directions">Unit direction of every edge in the plane (z = 0).</param>
    /// <param name="lengths">Outer dimension of every edge.</param>
    /// <param name="turns">Turn of every vertex (edges + 1 values; the two ends are 0).</param>
    /// <returns>The pieces of the centre line from the first to the last end, or null when the shape is outside the supported rules.</returns>
    internal static List<RebarSegment>? Solve(IReadOnlyList<double[]> directions, IReadOnlyList<double> lengths, IReadOnlyList<int> turns,
        double diameter, double cornerRadius, Hook? start, Hook? end)
    {
        int n = directions.Count;
        if (n == 0 || lengths.Count != n || turns.Count != n + 1 || turns[0] != 0 || turns[n] != 0 || !(diameter > 0) || !(cornerRadius > 0)) return null;
        if (!double.IsFinite(diameter) || !double.IsFinite(cornerRadius) || lengths.Any(l => !(l > 0) || !double.IsFinite(l))) return null;
        // a direction of a damaged file may be NaN, which no later comparison rejects and Math.Sign throws on
        if (directions.Any(u => u.Length != 3 || !double.IsFinite(u[0]) || !double.IsFinite(u[1]) || u[2] != 0 || Math.Abs(Dot(u, u) - 1) > 1e-9)) return null;
        foreach (var hook in new[] { start, end })
            if (hook != null && (hook.Angle < Math.PI / 2 - 1e-9 || hook.Angle > Math.PI + 1e-9 || hook.Side is not (1 or -1) || !(hook.Radius > 0)
                || hook.TangentLength != null && hook.Angle > Math.PI - 1e-9 || hook.TangentLength is <= 0)) return null;
        var u = directions;
        // the corners turn by a quarter or less to the side of their turn; a shape with a corner that is not a quarter turn has its own rules
        int oblique = 0;
        for (int j = 1; j < n; j++)
        {
            double cross = u[j - 1][0] * u[j][1] - u[j - 1][1] * u[j][0];
            double angle = Math.Atan2(Math.Abs(cross), Dot(u[j - 1], u[j]));
            if (turns[j] != Math.Sign(cross) || angle < 1e-9 || angle > Math.PI / 2 + 1e-9) return null;
            if (Math.Abs(angle - Math.PI / 2) > 1e-9) oblique++;
        }
        // only the joggle was measured: three edges, both corners less than a quarter turn, in opposite turns
        if (oblique > 0) return start == null && end == null && n == 3 && oblique == 2 && turns[1] == -turns[2] ? SolveOblique(u, lengths, turns, diameter, cornerRadius) : null;
        var reference = new double[n + 1][];
        reference[0] = [0, 0, 0];
        for (int i = 0; i < n; i++) reference[i + 1] = Add(reference[i], Scale(u[i], lengths[i]));
        var side = Enumerable.Range(0, n).Select(i => turns[i] != 0 ? turns[i] : turns[i + 1]).ToArray();
        double[] Start(int i) => Add(reference[i], Scale(Left(u[i]), diameter / 2 * side[i]));
        double[] End(int i) => Add(reference[i + 1], Scale(Left(u[i]), diameter / 2 * side[i]));

        var points = new List<double[]> { start == null ? Start(0) : Add(Start(0), Scale(u[0], start.Radius + diameter / 2)) };
        var radii = new List<double> { 0 };
        for (int j = 1; j < n; j++)
        {
            double cross = u[j - 1][0] * u[j][1] - u[j - 1][1] * u[j][0];
            double[] gap = Sub(Start(j), Start(j - 1));
            double t = (gap[0] * u[j][1] - gap[1] * u[j][0]) / cross;
            points.Add(Add(Start(j - 1), Scale(u[j - 1], t)));
            radii.Add(cornerRadius);
        }
        points.Add(end == null ? End(n - 1) : Sub(End(n - 1), Scale(u[n - 1], end.Radius + diameter / 2)));
        radii.Add(0);

        var startPieces = start == null ? [] : HookPieces(points[0], u[0], diameter, start, atStart: true);
        var endPieces = end == null ? [] : HookPieces(points[^1], u[n - 1], diameter, end, atStart: false);

        var lines = Fillets(points, radii);
        if (start != null && end != null && Collide(startPieces, endPieces, lines, n, diameter))
        {
            // only the stirrup of four edges that all turn left is supported
            if (n != 4 || turns.Skip(1).Take(n - 1).Any(t => t != 1)) return null;
            double[] move = [0, 0, -diameter];
            for (int k = n - 1; k < points.Count; k++) points[k] = Add(points[k], move);
            endPieces = endPieces.Select(p => p.Shifted(move)).ToList();
            lines = Fillets(points, radii);
        }
        var result = new List<RebarSegment>(startPieces);
        result.AddRange(lines);
        result.AddRange(endPieces);
        return result;
    }

    /// <summary>A joggle of three edges (the first and the last edge with a fixed direction, two opposite turns) whose middle edge is given by
    /// the vector <paramref name="outer"/> between its two outer corners (the sum of its projections) instead of a direction: the direction is the
    /// one for which that vector leaves the lateral distance of the two outer corners (one diameter, to the side of the first turn) across it.
    /// With <paramref name="lengths"/> of the first and the last edge the rest is <see cref="Solve"/> of a shape whose middle edge has that direction
    /// and the length of the vector. Null when the shape is not a joggle or has hooks (<paramref name="noHooks"/> false).</summary>
    internal static List<RebarSegment>? SolveJoggle(double[] first, double[] last, double[] outer, IReadOnlyList<double> lengths, IReadOnlyList<int> turns,
        double diameter, double cornerRadius, bool noHooks)
    {
        if (!noHooks || turns.Count != 4 || turns[0] != 0 || turns[3] != 0 || turns[1] is not (1 or -1) || turns[2] != -turns[1] || lengths.Count != 2
            || first.Length != 3 || last.Length != 3 || outer.Length != 3 || outer[2] != 0) return null;
        double length = Math.Sqrt(Dot(outer, outer));
        // the asin needs |diameter / length| <= 1; a NaN or infinite value fails the comparisons
        if (!double.IsFinite(length) || !(diameter > 0) || !double.IsFinite(diameter) || !(length > diameter)) return null;
        double angle = Math.Atan2(outer[1], outer[0]) - Math.Asin(diameter * turns[1] / length);
        return Solve([first, [Math.Cos(angle), Math.Sin(angle), 0], last], [lengths[0], length, lengths[1]], turns, diameter, cornerRadius, null, null);
    }

    /// <summary>A joggle: a shape of three edges without hooks whose two corners are less than a quarter turn, in opposite turns. The
    /// dimension of an edge runs between the outer corners of its two ends (the outer corner of a bend lies on the outer side of the bar,
    /// half a diameter from the centre line, <c>d/2 tan(angle/2)</c> ahead of the centre line corner along the edge before the corner and
    /// as far behind it along the edge after it); the first and the last outer point follow the side of their neighbour. So the centre line
    /// corners are the vertices of a polygon whose edge i has the length <c>sqrt(L^2 - lateral^2) - delta(i) - delta(i + 1)</c>, where
    /// <c>lateral</c> is the diameter when the two outer corners are on different sides of the edge (a joggle: opposite turns) and 0
    /// otherwise, and the edges keep their fixed directions. The bend arcs are tangent to the polygon, as everywhere else.</summary>
    private static List<RebarSegment>? SolveOblique(IReadOnlyList<double[]> u, IReadOnlyList<double> lengths, IReadOnlyList<int> turns, double diameter, double cornerRadius)
    {
        int n = u.Count;
        var tangent = new double[n + 1];
        for (int j = 1; j < n; j++) tangent[j] = Math.Tan(Math.Acos(Math.Clamp(Dot(u[j - 1], u[j]), -1, 1)) / 2);
        // the outer side of a corner is opposite to its turn; the ends take the side of the corner next to them
        int Outer(int vertex) => -turns[Math.Clamp(vertex, 1, n - 1)];
        var points = new List<double[]> { Scale(Left(u[0]), diameter / 2 * turns[1]) };
        var radii = new List<double> { 0 };
        for (int i = 0; i < n; i++)
        {
            double lateral = Outer(i) == Outer(i + 1) ? 0 : diameter;
            double polygon = Math.Sqrt(lengths[i] * lengths[i] - lateral * lateral) - diameter / 2 * (tangent[i] + tangent[i + 1]);
            // the straight piece between the two arcs must have a length
            if (!(polygon - cornerRadius * (tangent[i] + tangent[i + 1]) > 0)) return null;
            points.Add(Add(points[i], Scale(u[i], polygon)));
            radii.Add(i == n - 1 ? 0 : cornerRadius);
        }
        return Fillets(points, radii);
    }

    /// <summary>The leg and the arc of a hook at the tangent point <paramref name="tangent"/> of the edge with direction <paramref name="u"/>:
    /// in travel order, leg then arc at the start of the bar, arc then leg at its end.</summary>
    private static List<RebarSegment> HookPieces(double[] tangent, double[] u, double diameter, Hook hook, bool atStart)
    {
        int s = hook.Side;
        double a = hook.Angle;
        double[] centre = Add(tangent, Scale(Left(u), s * hook.Radius));
        double[] around = Sub(tangent, centre);
        double leg = hook.TangentLength is double tangentLength ? tangentLength - hook.Radius * Math.Tan(a / 2) : hook.Multiplier * diameter;
        double[] axis = [0, 0, s];
        if (atStart)
        {
            double[] direction = Rotate(u, -s * a);
            double[] arcStart = Add(centre, Rotate(around, -s * a));
            return [Line(Sub(arcStart, Scale(direction, leg)), arcStart), Arc(centre, hook.Radius, Sub(arcStart, centre), axis, a)];
        }
        double[] exit = Rotate(u, s * a);
        double[] arcEnd = Add(centre, Rotate(around, s * a));
        return [Arc(centre, hook.Radius, around, axis, a), Line(arcEnd, Add(arcEnd, Scale(exit, leg)))];
    }

    /// <summary>The straight pieces and corner arcs of the polyline through <paramref name="points"/> (arcs of <paramref name="radii"/> at the
    /// inner points).</summary>
    private static List<RebarSegment> Fillets(IReadOnlyList<double[]> points, IReadOnlyList<double> radii)
    {
        var result = new List<RebarSegment>();
        var current = points[0];
        for (int k = 1; k < points.Count - 1; k++)
        {
            double[] uin = Unit(Sub(points[k], points[k - 1])), uout = Unit(Sub(points[k + 1], points[k]));
            double angle = Math.Acos(Math.Clamp(Dot(uin, uout), -1, 1));
            double tangentLength = radii[k] * Math.Tan(angle / 2);
            double[] arcStart = Sub(points[k], Scale(uin, tangentLength)), arcEnd = Add(points[k], Scale(uout, tangentLength));
            double[] inward = Unit(Sub(uout, Scale(uin, Dot(uout, uin))));
            double[] centre = Add(arcStart, Scale(inward, radii[k]));
            result.Add(Line(current, arcStart));
            result.Add(Arc(centre, radii[k], Sub(arcStart, centre), Unit(Cross(uin, uout)), angle));
            current = arcEnd;
        }
        result.Add(Line(current, points[^1]));
        return result;
    }

    /// <summary>True when the start hook comes closer than a bar diameter to the end hook, or either hook to the straight edge at the other
    /// end (all in the plane: the centre lines are sampled at one degree).</summary>
    private static bool Collide(List<RebarSegment> startPieces, List<RebarSegment> endPieces, List<RebarSegment> lines, int edges, double diameter)
    {
        static List<(double[] A, double[] B)> Chords(IEnumerable<RebarSegment> pieces)
        {
            var chords = new List<(double[], double[])>();
            foreach (var piece in pieces)
            {
                if (!piece.IsArc) { chords.Add((At(piece, piece.Start), At(piece, piece.End))); continue; }
                int steps = Math.Max(1, (int)Math.Ceiling((piece.End - piece.Start) * 180 / Math.PI));
                for (int i = 0; i < steps; i++)
                    chords.Add((At(piece, piece.Start + (piece.End - piece.Start) * i / steps), At(piece, piece.Start + (piece.End - piece.Start) * (i + 1) / steps)));
            }
            return chords;
        }
        double Gap(IEnumerable<RebarSegment> a, IEnumerable<RebarSegment> b)
        {
            var second = Chords(b);
            return Chords(a).Min(x => second.Min(y => SegmentDistance(x.A, x.B, y.A, y.B)));
        }
        double min = Gap(startPieces, endPieces);
        if (edges >= 2)
        {
            // the straight pieces that touch no arc of a hook are the first and the last piece of the polyline
            min = Math.Min(min, Gap(startPieces, [lines[^1]]));
            min = Math.Min(min, Gap(endPieces, [lines[0]]));
        }
        return min < diameter * (1 - 1e-9);
    }

    private static double[] At(RebarSegment piece, double t) => piece.IsArc
        ? Add(piece.Origin, Scale(Add(Scale(piece.Axis, Math.Cos(t)), Scale(piece.Axis2, Math.Sin(t))), piece.Radius))
        : Add(piece.Origin, Scale(piece.Axis, t));

    /// <summary>The distance of two segments (Ericson, Real-Time Collision Detection, closest points of two segments).</summary>
    private static double SegmentDistance(double[] p1, double[] q1, double[] p2, double[] q2)
    {
        double[] d1 = Sub(q1, p1), d2 = Sub(q2, p2), r = Sub(p1, p2);
        double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r);
        double s, t;
        if (a <= 1e-24 && e <= 1e-24) return Math.Sqrt(Dot(r, r));
        if (a <= 1e-24) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = Dot(d1, r);
            if (e <= 1e-24) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = Dot(d1, d2), denominator = a * e - b * b;
                s = denominator > 1e-24 ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        double[] closest1 = Add(p1, Scale(d1, s)), closest2 = Add(p2, Scale(d2, t));
        double[] diff = Sub(closest1, closest2);
        return Math.Sqrt(Dot(diff, diff));
    }

    private static RebarSegment Line(double[] from, double[] to)
    {
        double[] d = Sub(to, from);
        double length = Math.Sqrt(Dot(d, d));
        return new RebarSegment(false, from, Scale(d, 1 / length), [0, 0, 0], 0, 0, length);
    }

    /// <summary>An arc from the vector <paramref name="start"/> (from the centre) turning by <paramref name="sweep"/> about <paramref name="axis"/>.</summary>
    private static RebarSegment Arc(double[] centre, double radius, double[] start, double[] axis, double sweep)
    {
        double[] x = Unit(start);
        return new RebarSegment(true, centre, x, Cross(Unit(axis), x), radius, 0, sweep);
    }

    private static RebarSegment Shifted(this RebarSegment piece, double[] by) => piece with { Origin = Add(piece.Origin, by) };

    private static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    private static double[] Scale(double[] a, double f) => [a[0] * f, a[1] * f, a[2] * f];
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    private static double[] Unit(double[] a) => Scale(a, 1 / Math.Sqrt(Dot(a, a)));
    private static double[] Left(double[] u) => [-u[1], u[0], 0];
    private static double[] Rotate(double[] v, double angle) =>
        [Math.Cos(angle) * v[0] - Math.Sin(angle) * v[1], Math.Sin(angle) * v[0] + Math.Cos(angle) * v[1], v[2]];
}
