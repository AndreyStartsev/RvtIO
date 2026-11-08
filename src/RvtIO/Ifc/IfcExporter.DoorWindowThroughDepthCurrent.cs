using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>
    /// The Depth of the opening of a door or window in a straight wall whose family cuts with a profile (the symbol stores cut loops)
    /// is the total layer width of the wall: the opening reaches from one face to the other, whatever box the family cutter has. The
    /// caller does not ask for a family without cut loops: it cuts with a void body of its own, whose depth is what Revit cuts. For an
    /// opening whose box was not rebuilt from a cut loop and is an axis-aligned rectangular prism in the frame of the wall, extruded
    /// along the wall or vertically, the side across the wall is set to the wall width and centred on the axis of the wall. Returns
    /// the depth, or null when the opening is left as it was (it is no such prism, or it already is that deep and centred).
    /// </summary>
    private static double? TryThroughDepthCurrent(StepWriter w, Dictionary<int, int> indexById, IReadOnlyDictionary<int, Element> elementsById,
        Element wall, string[] wallAttributes, string[] openingAttributes, GlobalsCurrent globals, List<int> detached)
    {
        const double tolerance = 1e-8;
        if (WallTotalWidthCurrent(w, indexById, wallAttributes, wall, elementsById, out _) is not { } width || !(width > 0) || !double.IsFinite(width)
            || ModelPlacementCurrent(w, indexById, wallAttributes[5]) is not { } wallPlacement
            || ModelPlacementCurrent(w, indexById, openingAttributes[5]) is not { } openingPlacement
            || !TryEntityId(openingAttributes[6], out int shapeId) || !indexById.TryGetValue(shapeId, out int shapeIndex)
            || SplitStepArguments(w.EntityAt(shapeIndex).Args) is not { Length: 3 } shape || shape[2].Length < 3
            || SplitStepArguments(shape[2][1..^1]) is not { Length: 1 } representations
            || !TryEntityId(representations[0], out int representationId) || !indexById.TryGetValue(representationId, out int representationIndex)
            || SplitStepArguments(w.EntityAt(representationIndex).Args) is not { Length: 4 } representation
            || representation[1] != "'Body'" || representation[3].Length < 3
            || SplitStepArguments(representation[3][1..^1]) is not { Length: 1 } items
            || !TryEntityId(items[0], out int solidId) || !indexById.TryGetValue(solidId, out int solidIndex)
            || w.EntityAt(solidIndex).Type != "IFCEXTRUDEDAREASOLID"
            || SplitStepArguments(w.EntityAt(solidIndex).Args) is not { Length: 4 } solid
            || !TryParseStepReal(solid[3], out double extrusion)
            || !TryEntityId(solid[0], out int profileId) || !indexById.TryGetValue(profileId, out int profileIndex)
            || w.EntityAt(profileIndex).Type != "IFCRECTANGLEPROFILEDEF"
            || SplitStepArguments(w.EntityAt(profileIndex).Args) is not { Length: 5 } profile
            || !TryParseStepReal(profile[3], out double xDim) || !TryParseStepReal(profile[4], out double yDim)
            || !TryEntityId(profile[2], out int profilePlacementId) || !indexById.TryGetValue(profilePlacementId, out int profilePlacementIndex)
            || w.EntityAt(profilePlacementIndex).Type != "IFCAXIS2PLACEMENT2D"
            || SplitStepArguments(w.EntityAt(profilePlacementIndex).Args) is not { Length: 2 } profilePlacement
            || !TryReadPointCurrent(w, indexById, profilePlacement[0], out var centre) || centre.Length != 2
            || !TryEntityId(solid[1], out int solidPlacementId) || !indexById.TryGetValue(solidPlacementId, out int solidPlacementIndex)
            || SplitStepArguments(w.EntityAt(solidPlacementIndex).Args) is not { Length: 3 } solidPlacement
            || !TryReadPointCurrent(w, indexById, solidPlacement[0], out var solidOrigin) || solidOrigin.Length != 3
            || Axis3FrameCurrent(w, indexById, solid[1]) is not { } solidFrame
            || DirectionOfCurrent(w, indexById, solid[2]) is not { } direction
            || !(extrusion > 0) || !(xDim > 0) || !(yDim > 0))
            return null;
        double[] reference = profilePlacement[1] == "$" ? [1, 0, 0] : DirectionOfCurrent(w, indexById, profilePlacement[1]) ?? [1, 0, 0];
        double[] profileX = [reference[0], reference[1], 0], profileY = [-reference[1], reference[0], 0];

        // vectors and points of the solid, in the frame of the wall (X along the wall, Y across it, Z up)
        double[] VectorToWall(double[] v) => wallPlacement.Axes.ToLocal(openingPlacement.Axes.Apply(solidFrame.Apply(v)));
        double[] PointToWall(double[] p)
        {
            double[] q = solidFrame.Apply(p);
            return wallPlacement.ToLocal(openingPlacement.ToWorld([solidOrigin[0] + q[0], solidOrigin[1] + q[1], solidOrigin[2] + q[2]]));
        }
        var corners = new List<double[]>();
        foreach (double sx in new[] { -0.5, 0.5 })
            foreach (double sy in new[] { -0.5, 0.5 })
                foreach (double along in new[] { 0, extrusion })
                    corners.Add(PointToWall([
                        centre[0] + sx * xDim * profileX[0] + sy * yDim * profileY[0] + along * direction[0],
                        centre[1] + sx * xDim * profileX[1] + sy * yDim * profileY[1] + along * direction[1],
                        along * direction[2]]));
        double[] low = [corners.Min(c => c[0]), corners.Min(c => c[1]), corners.Min(c => c[2])];
        double[] high = [corners.Max(c => c[0]), corners.Max(c => c[1]), corners.Max(c => c[2])];
        if (corners.Any(c => Enumerable.Range(0, 3).Any(i => Math.Abs(c[i] - low[i]) > tolerance && Math.Abs(c[i] - high[i]) > tolerance)))
            return null;
        if (Math.Abs(high[1] - low[1] - width) <= tolerance && Math.Abs(high[1] + low[1]) <= tolerance) return null;

        // the profile side that lies across the wall; the extrusion must not go across it
        double xAcross = VectorToWall(profileX)[1], yAcross = VectorToWall(profileY)[1];
        bool acrossX = Math.Abs(xAcross) > 0.9;
        if (!acrossX && Math.Abs(yAcross) <= 0.9 || Math.Abs(VectorToWall(direction)[1]) > 0.1) return null;
        double[] acrossAxis = acrossX ? profileX : profileY;
        double shift = -(high[1] + low[1]) / 2 * Math.Sign(acrossX ? xAcross : yAcross);
        int point = globals.Point2D(centre[0] + shift * acrossAxis[0], centre[1] + shift * acrossAxis[1]);
        int placement = w.Add("IFCAXIS2PLACEMENT2D", $"{StepWriter.Ref(point)},{profilePlacement[1]}");
        int newProfile = w.Add("IFCRECTANGLEPROFILEDEF",
            $".AREA.,{profile[1]},{StepWriter.Ref(placement)},{(acrossX ? StepWriter.Real(width) : profile[3])},{(acrossX ? profile[4] : StepWriter.Real(width))}");
        w.ReplaceArgs(solidIndex, $"{StepWriter.Ref(newProfile)},{solid[1]},{solid[2]},{solid[3]}");
        detached.Add(profileId);
        return width;
    }
}
