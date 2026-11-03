using RvtIO.Parsing;

namespace RvtIO.Ifc;

/// <summary>The Straight/Sketched subset of the stair shape classification.
/// Directions come from native cached paths in element id order.
/// An unknown projected endpoint Z is not invented.</summary>
internal static class StairShape
{
    /// <param name="sketchedIsFlight">False for the current profile: a stair with a sketched run is not classified there (every such stair
    /// of the measured samples is NOTDEFINED while its flight type is FREEFORM).</param>
    internal static string? Classify(IReadOnlyList<TfBlockDeserializer.StairRunInput> runs, int landings, bool sketchedIsFlight = true)
    {
        if (landings < 0) return null;
        var lines = new List<TfBlockDeserializer.StairPathDirection>();
        foreach (var run in runs)
        {
            if (run.Style is TfBlockDeserializer.StairRunStyle.Sketched && !sketchedIsFlight) return null;
            if (run.Style is not (TfBlockDeserializer.StairRunStyle.Straight or TfBlockDeserializer.StairRunStyle.Sketched)
                || run.Directions is not { Length: > 0 }) return null;
            if (run.Directions.Any(direction => !double.IsFinite(direction.X) || !double.IsFinite(direction.Y)
                || direction.Z != 0 || direction.X == 0 && direction.Y == 0)) return null;
            // All admitted paths consist of GLine; both style branches reject
            // more than one line. Do not count a multi-line sketch as a flight.
            if (run.Directions.Length == 1) lines.Add(run.Directions[0]);
        }
        if (lines.Count == 1) return "STRAIGHT_RUN_STAIR";
        if (lines.Count == 2)
        {
            double direction = Dot(lines[0], lines[1]);
            if (Equal(direction, 1) && landings == 1) return "TWO_STRAIGHT_RUN_STAIR";
            if (Equal(direction, 0) && landings == 1) return "QUARTER_TURN_STAIR";
            if (Equal(direction, -1) && landings == 1) return "HALF_TURN_STAIR";
        }
        if (lines.Count == 3 && Zero(Dot(lines[0], lines[1])) && Zero(Dot(lines[1], lines[2])) && landings == 2)
            return null; // DOUBLE_RETURN / TWO_QUARTER_TURN requires projected endpoint Z.
        if (lines.Count == 4 && landings == 3 && Zero(Dot(lines[0], lines[1]))
            && Zero(Dot(lines[1], lines[2])) && Zero(Dot(lines[2], lines[3])))
            return "THREE_QUARTER_TURN_STAIR";
        return "NOTDEFINED";
    }

    private static double Dot(TfBlockDeserializer.StairPathDirection a, TfBlockDeserializer.StairPathDirection b)
        => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    // Fixed tolerance of the exporter's math utility, not a fitted geometry/numeric tolerance.
    private static bool Equal(double a, double b)
    {
        double sum = Math.Abs(a) + Math.Abs(b);
        return sum < 1.0e-9 || Math.Abs(a - b) <= sum * 1.0e-9;
    }
    private static bool Zero(double value) => Math.Abs(value) <= 1.0e-9;
}
