using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    private readonly Dictionary<int, List<QuantityValue>> _ownCutPairOpeningQuantitiesCurrent = new();

    /// <summary>The openings written from a resolved box whose depth earlier output made 0.2 mm larger (see
    /// <see cref="HostedOpeningGeometry.HadDepthClearance"/>).</summary>
    internal readonly HashSet<int> _openingsWithFormerDepthClearanceCurrent = new();

    internal bool TryCreateOwnCutPairOpeningBodyCurrent(StepWriter writer, Element cutter, Element host,
        HostedOpeningGeometry opening, int subContext, out int shape, out List<QuantityValue>? quantities)
        => TryCreateOwnCutPairOpeningBodyCurrent(writer, cutter, host, opening, subContext, out shape, out quantities, out _);

    internal bool TryCreateOwnCutPairOpeningBodyCurrent(StepWriter writer, Element cutter, Element host,
        HostedOpeningGeometry opening, int subContext, out int shape, out List<QuantityValue>? quantities,
        out (double[] OriginFt, double AxisX, double AxisY)? brepFrame)
    {
        shape = 0;
        quantities = null;
        brepFrame = null;
        // Only the body that is certified as the cutter's current solid is also taken for its extrusion; any other decoded
        // solid of a slab or a wall decides nothing but whether the cutter is no prism across the wall.
        bool certified = cutter.OwnCurrentCutPairBodyCurrent != null;
        bool uncertified = !certified;
        if (!CurrentProfile || cutter.CategoryId != -2000011 && cutter.CategoryId != FloorsCategoryId || host.OwnWallCrossSection != 1
            || host.CutOutSteps?.Count(step => step.ClassId == 977 && step.ElementId == cutter.Id) != 1
            || (cutter.OwnCurrentCutPairBodyCurrent ?? cutter.Geometry) is not { } body
            // The body of a wall carries no node ids: all its closed shells are the one Geometry node, however
            // many disjoint shells it holds (a cutter of three shells and 26 faces is a Brep).
            || body.BrepSolids is not { Count: > 0 } shells || shells.Any(shell => !shell.IsClosed)
            || shells.SelectMany(shell => shell.FaceIndices).Distinct().Count() != body.Faces.Count
            || host.OwnWallAxisLine is not { Length: 2 } axis
            || axis.Any(point => point.Length != 3 || point.Any(value => !double.IsFinite(value)))
            || !TryBuildCutPairOpening(host, cutter, out var expected) || opening != expected) return false;
        double dx = axis[1][0] - axis[0][0], dy = axis[1][1] - axis[0][1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length) || axis[1][2] != axis[0][2]) return false;
        var faces = shells.SelectMany(shell => shell.FaceIndices).Distinct().Order().Select(index => body.Faces[index]).ToList();
        var extrusion = CalculateOthersCurrentExtrusion(faces, [[-dy / length, dx / length, 0]]);
        // An uncertified solid is only taken when it is no prism across the wall: an extrusion keeps the box of the pair opening.
        if (uncertified && extrusion != null) return false;
        if (extrusion == null)
        {
            // The body export is restricted to the one axis across the wall; a cutter that is no prism
            // along it is written as the IfcFacetedBrep of its solid: the opening sits at the minimum corner of the solid in the
            // axes of the model, the vertices are given in the axes of the wall from that corner, and a Brep has no extrusion
            // quantities. shortcut: the minimum corner is taken from the vertices, which is the box of a planar solid; upgrade
            // by reading the box of the serialized solid when a cutter has curved faces.
            if (faces.Any(face => face.CurvedSurface || face.OuterVertices is not { Count: >= 3 })) return false;
            var points = faces.SelectMany(face => (face.OuterVertices ?? []).Concat((face.InnerVertices ?? []).SelectMany(loop => loop))).ToList();
            double[] corner = [points.Min(point => point[0]), points.Min(point => point[1]), points.Min(point => point[2])];
            double ux = dx / length, uy = dy / length;
            double[] FromCorner(double[] point) =>
            [
                (point[0] - corner[0]) * ux + (point[1] - corner[1]) * uy,
                (point[0] - corner[0]) * -uy + (point[1] - corner[1]) * ux,
                point[2] - corner[2],
            ];
            var written = CreateFacetedShapeRepresentations(writer, body, subContext, preserveValidatedPlanarFaces: true,
                preserveValidatedCoordinates: true, localFrame: FromCorner);
            if (written.RepresentationIds.Count == 0) return false;
            shape = writer.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List(written.RepresentationIds.Select(R))}");
            quantities = [];
            brepFrame = (corner, ux, uy);
            return true;
        }
        if (extrusion.FirstSourceLoop is not { Count: >= 3 } firstLoop) return false;
        bool SameLoop(IReadOnlyList<double[]> loop) => loop.Count == firstLoop.Count
            && loop.Zip(firstLoop).All(pair => pair.First.SequenceEqual(pair.Second));
        int first = extrusion.Regions.FindIndex(region => SameLoop(region.Outer) || region.Inner.Any(loop => SameLoop(loop)));
        if (first < 0) return false;
        var firstRegion = extrusion.Regions[first];
        extrusion.Regions.RemoveAt(first);
        extrusion.Regions.Insert(0, firstRegion);
        var checkpoint = writer.CreateCheckpoint();
        int item = WriteOthersCurrentSolid(writer, extrusion, faces,
            OthersCurrentProfileName(cutter.FamilyName ?? cutter.Name ?? ""), 0, 0, 0);
        if (item <= 0) { writer.Rollback(checkpoint); return false; }
        int representation = writer.Add("IFCSHAPEREPRESENTATION",
            $"{R(subContext)},{S("Body")},{S("SweptSolid")},{StepWriter.List(R(item))}");
        shape = writer.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List(R(representation))}");
        // The first authored loop is measured even when an outer loop is moved ahead of it while the swept area is created.
        quantities = OpeningQuantitiesCurrent(extrusion.LengthFt * FeetToMetres,
            firstLoop.Select(point => point.Select(value => value * FeetToMetres).ToArray()).ToList());
        return true;
    }
}
