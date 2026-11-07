using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The current profile: the room of a family instance. One test point of the instance is looked up in the room solids of
/// the instance's created phase; the first room that holds it wins. The test point of an instance in a wall lies one foot
/// beyond the wall's side face that the instance's local Y leaves through (<see cref="WallSideTestPointCurrent"/>); any
/// other instance takes the centre of its bounding box moved by at most half a foot towards the elevation of its associated
/// level. The point of a work-plane instance, along the plane's normal, is not reproduced. The room stored with the
/// instance (m_idRoom) is kept; this lookup only decides the instances without one.
/// </summary>
public partial class IfcExporter
{
    /// <summary>Categories whose instances ask for a room.</summary>
    internal static readonly HashSet<int> RoomRelationCategoriesCurrent =
    [
        -2001000, -2000100, -2000023, -2001040, -2001060, -2000080, -2001100, -2000151, -2001120, -2001140, -2001180,
        -2001160, -2001260, -2001350, -2001330, -2001300, -2000014, -2001360, -2008010, -2008013, -2008016, -2008055,
        -2008049, -2008075, -2008077, -2008079, -2008081, -2008083, -2008085, -2008087, -2008099,
    ];

    /// <summary>The classes that are room related (generic family instances only).</summary>
    internal static bool IsRoomRelatedClassCurrent(string ifcType) =>
        ifcType.StartsWith("IFCFLOW", StringComparison.Ordinal) || ifcType.StartsWith("IFCDISTRIBUTION", StringComparison.Ordinal)
        || ifcType is "IFCENERGYCONVERSIONDEVICE" or "IFCELECTRICDISTRIBUTIONPOINT" or "IFCFURNISHINGELEMENT" or "IFCFURNITURE"
            or "IFCSYSTEMFURNITUREELEMENT" or "IFCBUILDINGELEMENTPROXY";

    /// <summary>Whether the instance is asked for a room at all: a plain family instance of a listed category that is
    /// exported as a room-related class.</summary>
    internal static bool IsRoomInstanceCurrent(Element instance, string ifcType) =>
        IsRoomRelatedClassCurrent(ifcType) && instance.CategoryId is int category && RoomRelationCategoriesCurrent.Contains(category)
        && instance.RuleClassName is null or "FamilyInstance";

    private const double RoomPointTolerance = 1e-6;
    private const double LevelPullFeet = 0.5;

    private const double WallSideOffsetFeet = 1.0;

    /// <summary>The test point of an instance hosted by a wall: the instance's
    /// origin carried along its local Y axis to the wall's side face that Y leaves through, and one foot beyond that
    /// face. Of the faces parallel to the wall's sides it is the one farthest along +Y; null when the instance has no
    /// transform or its host is no wall with such faces.</summary>
    internal static double[]? WallSideTestPointCurrent(Element instance, IReadOnlyDictionary<int, Element> elementsById)
    {
        if (instance.SerializedHostId is not int hostId || hostId <= 0 || !elementsById.TryGetValue(hostId, out var host)
            || host.CategoryId != WallsCategoryId || instance.Transform is not { } transform
            || transform.Origin is not { Length: 3 } origin || transform.BasisY is not { Length: 3 } basisY
            || host.Geometry?.Faces is not { Count: > 0 } faces)
            return null;
        double yLength = Math.Sqrt(basisY[0] * basisY[0] + basisY[1] * basisY[1] + basisY[2] * basisY[2]);
        if (!(yLength > 0) || !double.IsFinite(yLength)) return null;
        double[] y = [basisY[0] / yLength, basisY[1] / yLength, basisY[2] / yLength];

        double bestDistance = double.NegativeInfinity;
        double[]? bestNormal = null;
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: >= 3 } ring) continue;
            // Newell normal and a point of the face
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                double[] a = ring[i], b = ring[(i + 1) % ring.Count];
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(length > 1e-12)) continue;
            nx /= length; ny /= length; nz /= length;
            double alongY = nx * y[0] + ny * y[1] + nz * y[2];
            // a side face of the wall: upright and facing along the Y axis
            if (Math.Abs(nz) > 0.01 || Math.Abs(alongY) < 0.5) continue;
            double offset = nx * (ring[0][0] - origin[0]) + ny * (ring[0][1] - origin[1]) + nz * (ring[0][2] - origin[2]);
            double distance = offset / alongY; // along +Y from the origin to the face's plane
            if (distance <= bestDistance) continue;
            bestDistance = distance;
            double sign = Math.Sign(alongY);
            bestNormal = [nx * sign, ny * sign, nz * sign];
        }
        if (bestNormal == null || !double.IsFinite(bestDistance)) return null;
        return
        [
            origin[0] + y[0] * bestDistance + bestNormal[0] * WallSideOffsetFeet,
            origin[1] + y[1] * bestDistance + bestNormal[1] * WallSideOffsetFeet,
            origin[2] + y[2] * bestDistance + bestNormal[2] * WallSideOffsetFeet,
        ];
    }

    /// <summary>The test point (feet) of an instance: the point beside its host wall's face when it is hosted by a wall,
    /// else the centre of its bounding box, moved by at most half a foot towards the elevation of its associated level;
    /// null without a box.</summary>
    internal static double[]? RoomTestPointCurrent(Element instance, IReadOnlyDictionary<int, double> levelElevations,
        IReadOnlyDictionary<int, Element>? elementsById = null)
    {
        if (elementsById != null && WallSideTestPointCurrent(instance, elementsById) is { } wallPoint) return wallPoint;
        if ((instance.Geometry?.BoundingBox ?? instance.DecodedBox) is not { Length: >= 6 } box) return null;
        double[] center = [(box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2];
        // The associated level, found as the storey resolution does.
        foreach (int parameterId in new[] { -1001952, -1002062, -1114000, -1001350, -1001365, -1001383, -1002063 })
            if (instance.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId)?.Value is int levelId
                && levelElevations.TryGetValue(levelId, out double elevation))
            {
                double delta = elevation - center[2];
                if (Math.Abs(delta) > 1e-9) center[2] += Math.Sign(delta) * Math.Min(LevelPullFeet, Math.Abs(delta));
                break;
            }
        return center;
    }

    /// <summary>The room each room-related family instance is in (0 when none of the rooms with a decoded solid holds its
    /// test point): element id -> room element id, for the instances whose test point can be decided.</summary>
    internal static Dictionary<int, int> ResolveInstanceRoomsCurrent(IReadOnlyCollection<Element> allElements,
        IReadOnlyDictionary<int, Element> elementsById, IEnumerable<KeyValuePair<int, string>> instanceClasses,
        IReadOnlyDictionary<int, double> levelElevations)
    {
        var volumesByPhase = new Dictionary<int, List<RoomVolumeCurrent>>();
        foreach (var room in allElements.Where(element => element.CategoryId == RoomCategoryId && element.SerializedRoomPhaseId is >= 0
                     && element.SerializedRoomIsLocationless != true).OrderBy(element => element.Id))
            if (RoomVolumeCurrent.Create(room) is { } volume)
            {
                if (!volumesByPhase.TryGetValue(room.SerializedRoomPhaseId!.Value, out var list))
                    volumesByPhase[room.SerializedRoomPhaseId.Value] = list = new List<RoomVolumeCurrent>();
                list.Add(volume);
            }
        var result = new Dictionary<int, int>();
        if (volumesByPhase.Count == 0) return result;
        foreach (var (elementId, ifcType) in instanceClasses)
        {
            if (!elementsById.TryGetValue(elementId, out var instance) || !IsRoomInstanceCurrent(instance, ifcType)
                || instance.SerializedCreatedPhaseId is not int phase
                || RoomTestPointCurrent(instance, levelElevations, elementsById) is not { } point)
                continue;
            int found = 0;
            if (volumesByPhase.TryGetValue(phase, out var volumes))
                foreach (var volume in volumes)
                    if (volume.Contains(point[0], point[1], point[2], RoomPointTolerance))
                    {
                        found = volume.RoomId;
                        break;
                    }
            result[elementId] = found;
        }
        return result;
    }

    /// <summary>A room solid: a point inside or on the boundary belongs
    /// to the room. Lengths are feet in project coordinates.</summary>
    internal sealed class RoomVolumeCurrent
    {
        private sealed record Face(List<(double X, double Y, double Z)> Outer, List<List<(double X, double Y, double Z)>> Holes,
            double Nx, double Ny, double Nz, double D, double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ);

        private readonly List<Face> _faces;

        public int RoomId { get; }
        public double ZMin { get; }
        public double ZMax { get; }
        private readonly double _minX, _minY, _maxX, _maxY;

        private RoomVolumeCurrent(int roomId, List<Face> faces)
        {
            RoomId = roomId;
            _faces = faces;
            ZMin = faces.Min(face => face.MinZ);
            ZMax = faces.Max(face => face.MaxZ);
            _minX = faces.Min(face => face.MinX);
            _maxX = faces.Max(face => face.MaxX);
            _minY = faces.Min(face => face.MinY);
            _maxY = faces.Max(face => face.MaxY);
        }

        /// <summary>The solid of a room whose planar boundary cache is decoded; null otherwise.</summary>
        public static RoomVolumeCurrent? Create(Element room)
        {
            if (room.SerializedSpaceBoundaryCurrent is not { Faces.Count: > 0 } boundary) return null;
            var faces = new List<Face>();
            foreach (var source in boundary.Faces)
            {
                if (source.OuterVertices is not { Count: >= 3 } outerSource) continue;
                var outer = outerSource.Select(vertex => (X: vertex[0], Y: vertex[1], Z: vertex[2])).ToList();
                // Newell normal of the outer loop.
                double nx = 0, ny = 0, nz = 0;
                for (int i = 0; i < outer.Count; i++)
                {
                    var a = outer[i]; var b = outer[(i + 1) % outer.Count];
                    nx += (a.Y - b.Y) * (a.Z + b.Z);
                    ny += (a.Z - b.Z) * (a.X + b.X);
                    nz += (a.X - b.X) * (a.Y + b.Y);
                }
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-12) continue;
                nx /= length; ny /= length; nz /= length;
                var holes = source.InnerVertices?.Select(loop => loop.Select(vertex => (X: vertex[0], Y: vertex[1], Z: vertex[2])).ToList()).ToList() ?? [];
                faces.Add(new Face(outer, holes, nx, ny, nz, nx * outer[0].X + ny * outer[0].Y + nz * outer[0].Z,
                    outer.Min(v => v.X), outer.Max(v => v.X), outer.Min(v => v.Y), outer.Max(v => v.Y),
                    outer.Min(v => v.Z), outer.Max(v => v.Z)));
            }
            return faces.Count == 0 ? null : new RoomVolumeCurrent(room.Id, faces);
        }

        /// <summary>Whether the point is inside the solid or within <paramref name="tolerance"/> of its boundary.</summary>
        public bool Contains(double x, double y, double z, double tolerance)
        {
            if (z < ZMin - tolerance || z > ZMax + tolerance
                || x < _minX - tolerance || x > _maxX + tolerance || y < _minY - tolerance || y > _maxY + tolerance)
                return false;
            foreach (var face in _faces)
                if (OnFace(face, x, y, z, tolerance)) return true;
            // Vertical ray upwards: an odd number of faces above the point means it is inside.
            int crossings = 0;
            foreach (var face in _faces)
            {
                if (Math.Abs(face.Nz) < 1e-9 || x < face.MinX || x > face.MaxX || y < face.MinY || y > face.MaxY) continue;
                double faceZ = (face.D - face.Nx * x - face.Ny * y) / face.Nz;
                if (faceZ > z && InPolygon(face.Outer, x, y) && !face.Holes.Any(hole => InPolygon(hole, x, y))) crossings++;
            }
            return crossings % 2 == 1;
        }

        private static bool OnFace(Face face, double x, double y, double z, double tolerance)
        {
            if (x < face.MinX - tolerance || x > face.MaxX + tolerance || y < face.MinY - tolerance || y > face.MaxY + tolerance
                || z < face.MinZ - tolerance || z > face.MaxZ + tolerance)
                return false;
            double distance = face.Nx * x + face.Ny * y + face.Nz * z - face.D;
            if (Math.Abs(distance) > tolerance) return false;
            // Project the point onto the face's plane and test it against the outline in the dominant plane.
            double px = x - distance * face.Nx, py = y - distance * face.Ny, pz = z - distance * face.Nz;
            double ax = Math.Abs(face.Nx), ay = Math.Abs(face.Ny), az = Math.Abs(face.Nz);
            Func<(double X, double Y, double Z), (double, double)> project = ax >= ay && ax >= az
                ? v => (v.Y, v.Z) : ay >= az ? v => (v.X, v.Z) : v => (v.X, v.Y);
            var (u, v2) = project((px, py, pz));
            return InPolygon2(face.Outer.Select(project).ToList(), u, v2, tolerance)
                && !face.Holes.Any(hole => InPolygon2(hole.Select(project).ToList(), u, v2, -tolerance));
        }

        private static bool InPolygon(List<(double X, double Y, double Z)> ring, double x, double y) =>
            InPolygon2(ring.Select(v => (v.X, v.Y)).ToList(), x, y, 0);

        /// <summary>Even-odd containment; a point within <paramref name="margin"/> of an edge counts as inside for a
        /// positive margin and as outside for a negative one.</summary>
        private static bool InPolygon2(List<(double, double)> ring, double x, double y, double margin)
        {
            bool inside = false;
            double marginSquared = margin * margin;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var (ax, ay) = ring[j];
                var (bx, by) = ring[i];
                if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
                if (margin != 0)
                {
                    double dx = bx - ax, dy = by - ay;
                    double lengthSquared = dx * dx + dy * dy;
                    double t = lengthSquared == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0, 1);
                    double ex = ax + t * dx - x, ey = ay + t * dy - y;
                    if (ex * ex + ey * ey <= marginSquared) return margin > 0;
                }
            }
            return inside;
        }
    }
}
