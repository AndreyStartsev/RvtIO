using System.Globalization;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    private const int StairsCategoryId = -2000120, StairsRunsCategoryId = -2000919, StairsLandingsCategoryId = -2000920;

    /// <summary>The element that supplies the type object of a product. It is the one ELEM_TYPE_PARAM names, except for
    /// the parts of a stair in the current profile: a run keeps its type in TypeId only (no ELEM_TYPE_PARAM) and the parameter of
    /// a landing names a sibling landing; the stair, run and landing types are the elements of the same category that have
    /// no type of their own.</summary>
    internal static int ResolveTypeObjectElementId(
        Element instance, IReadOnlyDictionary<int, Element> elemById, bool currentProfile)
    {
        // A pipe, pipe insulation or cable tray is typed by its MEP curve type id, which the general type link can miss.
        if (currentProfile && instance.SerializedMepCurveTypeId is > 0) return instance.SerializedMepCurveTypeId.Value;
        return currentProfile && instance.CategoryId is StairsCategoryId or StairsRunsCategoryId or StairsLandingsCategoryId
            && instance.TypeId > 0 && instance.TypeId != instance.Id
            && elemById.TryGetValue(instance.TypeId, out var type)
            && type.CategoryId == instance.CategoryId && type.TypeId <= 0
                ? instance.TypeId : instance.FamilyTypeId;
    }

    /// <summary>IfcStairTypeEnum of IFC2X3.</summary>
    private static readonly string[] StairShapesCurrent =
    [
        "STRAIGHT_RUN_STAIR", "TWO_STRAIGHT_RUN_STAIR", "QUARTER_WINDING_STAIR", "QUARTER_TURN_STAIR", "HALF_WINDING_STAIR",
        "HALF_TURN_STAIR", "TWO_QUARTER_WINDING_STAIR", "TWO_QUARTER_TURN_STAIR", "THREE_QUARTER_WINDING_STAIR",
        "THREE_QUARTER_TURN_STAIR", "SPIRAL_STAIR", "DOUBLE_RETURN_STAIR", "CURVED_RUN_STAIR", "TWO_CURVED_RUN_STAIR",
        "USERDEFINED", "NOTDEFINED",
    ];

    /// <summary>The number that stands for the ShapeType of an IfcStair (as written, ".HALF_TURN_STAIR.") in the type key.
    /// The generic type of a stair is cached by the type element and the IFC type of the shape, so the stairs of one type
    /// element can fall into several IfcTypeProducts that share their GlobalId. A shape that is not an
    /// enumeration item (unset) shares one number.</summary>
    internal static int StairShapeKeyCurrent(string shapeToken)
    {
        int index = Array.IndexOf(StairShapesCurrent, shapeToken.Trim('.'));
        return shapeToken.Length > 2 && shapeToken[0] == '.' && index >= 0 ? index + 1 : StairShapesCurrent.Length + 1;
    }

    /// <summary>The number that stands for the style of a stair run in its type key: the generic type of a run is cached
    /// by the type element and the flight type, so the runs of one type element in
    /// different styles get several IfcStairFlightTypes. 0 when the style is not decoded.</summary>
    internal static int StairRunStyleKeyCurrent(Element run) =>
        run.SerializedStairRunInput is { } input && input.OwnerId == run.Id ? (int)input.Style : 0;

    /// <summary>The PredefinedType of the IfcStairFlightType of a run: STRAIGHT for a straight run and FREEFORM for a
    /// sketched one; NOTDEFINED when the style is not decoded.</summary>
    internal static string StairFlightTypePredefinedTypeCurrent(Element run) =>
        run.SerializedStairRunInput is { } input && input.OwnerId == run.Id
            ? input.Style switch
            {
                TfBlockDeserializer.StairRunStyle.Straight => "STRAIGHT",
                TfBlockDeserializer.StairRunStyle.Sketched => "FREEFORM",
                TfBlockDeserializer.StairRunStyle.Spiral => "SPIRAL",
                _ => "NOTDEFINED",
            }
            : "NOTDEFINED";

    /// <summary>Whether an instance with no GInstance of a symbol (a beam, column, proxy or
    /// furnishing element that holds its geometry itself) uses its own geometry, compared with its type element: the instance holds stored faces, edges or
    /// solids and their numbers differ from the type element's, so it gets a type of its own.
    /// A type element whose block is absent or unreadable keeps the shared type.</summary>
    internal static bool UsesInstanceGeometryOfTypeCurrent(Element instance, Element type) =>
        UsesInstanceGeometry(instance, type, withSolids: true);

    /// <summary>Whether a proxy with a GInstance of a symbol uses its own geometry: the instance holds stored faces,
    /// edges or solids of its own and the summary of the symbol is known, so it gets a type of its own even when its
    /// face, edge and solid numbers equal the symbol's. A symbol whose summary is unknown keeps the shared type.</summary>
    internal static bool UsesInstanceGeometryOfSymbolCurrent(Element instance, Element symbol) =>
        symbol.Geometry?.StoredNodeSummary != null && instance.Geometry?.StoredNodeSummary is { } own
        && (own != (0, 0) || instance.Geometry.StoredSolids > 0);

    private const int DoorOperationTypeParamId = -1001211; // DOOR_OPERATION_TYPE

    private static readonly HashSet<string> DoorStyleOperations = new(StringComparer.Ordinal)
    {
        "SINGLE_SWING_LEFT", "SINGLE_SWING_RIGHT", "DOUBLE_DOOR_SINGLE_SWING", "DOUBLE_DOOR_SINGLE_SWING_OPPOSITE_LEFT",
        "DOUBLE_DOOR_SINGLE_SWING_OPPOSITE_RIGHT", "DOUBLE_SWING_LEFT", "DOUBLE_SWING_RIGHT", "DOUBLE_DOOR_DOUBLE_SWING",
        "SLIDING_TO_LEFT", "SLIDING_TO_RIGHT", "DOUBLE_DOOR_SLIDING", "FOLDING_TO_LEFT", "FOLDING_TO_RIGHT",
        "DOUBLE_DOOR_FOLDING", "REVOLVING", "ROLLINGUP",
    };

    /// <summary>OperationType of the style a door makes: the Operation parameter of the door type when it spells a value of
    /// the enumeration. A left or right value is the other one for a mirrored door (hand flip differs from facing flip) and
    /// stays NOTDEFINED when the flips of the door are not read.</summary>
    internal static string DoorStyleOperationCurrent(Element type, Element door)
    {
        if (NonEmptyStringParameter(type, DoorOperationTypeParamId) is not { } text || !DoorStyleOperations.Contains(text))
            return "NOTDEFINED";
        bool left = text.EndsWith("_LEFT", StringComparison.Ordinal);
        if (!left && !text.EndsWith("_RIGHT", StringComparison.Ordinal)) return text;
        return door.SerializedFlippedSymbol switch
        {
            false => text,
            true => left ? text[..^4] + "RIGHT" : text[..^5] + "LEFT",
            null => "NOTDEFINED",
        };
    }

    /// <summary>RVTIO_TYPEKEY_DIAG: one line per proxy, beam, column and furnishing element that is keyed into a type (stderr;
    /// every class with the value "all"), with the stored object counts, surface area and volume of the instance, of its
    /// geometry symbol and of its type element, for judging <see cref="UsesInstanceGeometryOfTypeCurrent"/>.</summary>
    private static readonly string? TypeKeyDiagnosticsMode = Environment.GetEnvironmentVariable("RVTIO_TYPEKEY_DIAG") is { Length: > 0 } mode ? mode : null;

    private static bool TypeKeyDiagnostics(string ifcType) => TypeKeyDiagnosticsMode != null
        && (TypeKeyDiagnosticsMode == "all" || ifcType is "IFCBUILDINGELEMENTPROXY" or "IFCBEAM" or "IFCCOLUMN" or "IFCFURNISHINGELEMENT");

    private static string DescribeStoredGeometry(Element? element)
    {
        if (element?.Geometry is not { } geometry) return "none";
        var (area, volume) = FaceAreaAndVolume(geometry.Faces);
        return $"{geometry.StoredNodeSummary?.Faces}/{geometry.StoredNodeSummary?.Edges} instances={geometry.Instances?.Count ?? 0}"
            + $" mapped={geometry.ExactMappedInstances?.Count ?? 0} faces={geometry.Faces.Count} shells={geometry.BrepSolids?.Count ?? 0}"
            + $" area={area.ToString("G9", CultureInfo.InvariantCulture)} volume={volume.ToString("G9", CultureInfo.InvariantCulture)} classes="
            + (geometry.StoredClassCounts == null ? "-" : string.Join(",", geometry.StoredClassCounts
                .OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")));
    }

    /// <summary>Surface area and enclosed volume (feet) of planar faces given as rings: net area by the Newell vector
    /// of the outer ring less the inner rings, volume as a third of the sum of normal . point . area.</summary>
    private static (double Area, double Volume) FaceAreaAndVolume(IEnumerable<GeoFace> faces)
    {
        static double[] Newell(IReadOnlyList<double[]> ring)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                x += (a[1] - b[1]) * (a[2] + b[2]);
                y += (a[2] - b[2]) * (a[0] + b[0]);
                z += (a[0] - b[0]) * (a[1] + b[1]);
            }
            return [x / 2, y / 2, z / 2];
        }
        double area = 0, volume = 0;
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: >= 3 } outer) continue;
            var normal = Newell(outer);
            double outerArea = Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]);
            if (outerArea <= 0) continue;
            double net = outerArea;
            foreach (var inner in face.InnerVertices ?? [])
                if (inner.Count >= 3)
                {
                    var hole = Newell(inner);
                    net -= Math.Sqrt(hole[0] * hole[0] + hole[1] * hole[1] + hole[2] * hole[2]);
                }
            area += net;
            volume += (normal[0] * outer[0][0] + normal[1] * outer[0][1] + normal[2] * outer[0][2]) / outerArea * net / 3;
        }
        return (area, Math.Abs(volume));
    }

    /// <summary>The MEP system type of the elements whose own connectors say nothing about a system, taken from the
    /// elements connected to them. The System Type of a pipe or duct element is read from its own connectors, which
    /// the connected network shares. Some families (tees and transitions) keep no connector data of their own in the file, so their
    /// parameter is absent, while the connectors of the pipes and fittings next to them name them in their references.
    /// Only elements without a system type of their own are returned, and only when every neighbour with one agrees;
    /// an element whose own connectors reach several system types is left out. The result is resolved neighbour by
    /// neighbour, so a run of such elements takes the type of the first element with a system.</summary>
    internal static Dictionary<int, int> ResolveConnectedSystemTypes(IReadOnlyDictionary<int, Element> elemById)
    {
        var neighbours = new Dictionary<int, HashSet<int>>();
        var ownType = new Dictionary<int, int>();
        void Link(int first, int second)
        {
            if (!neighbours.TryGetValue(first, out var set)) neighbours[first] = set = [];
            set.Add(second);
        }
        foreach (var element in elemById.Values)
        {
            if (element.IsDiscovered || element.SerializedConnectorGraph is not { Count: > 0 } graph) continue;
            for (int k = 0; k < graph.Count; k++)
            {
                foreach (var reference in graph[k].Refs)
                    if (reference.Id != element.Id && reference.Id > 0)
                    {
                        Link(element.Id, reference.Id);
                        Link(reference.Id, element.Id);
                    }
                if (element.SerializedConnectorSystems is not { } systems || k >= systems.Count || systems[k] <= 0
                    || !elemById.TryGetValue(systems[k], out var system) || system.SerializedMepSystem is not { TypeId: > 0 } mep)
                    continue;
                // -1 marks an element that reaches several system types.
                ownType[element.Id] = ownType.TryGetValue(element.Id, out int known) && known != mep.TypeId ? -1 : mep.TypeId;
            }
        }

        var derived = new Dictionary<int, int>();
        var conflicting = new HashSet<int>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (id, around) in neighbours)
            {
                if (ownType.ContainsKey(id) || derived.ContainsKey(id) || conflicting.Contains(id)) continue;
                var types = new HashSet<int>();
                foreach (int other in around)
                    if (ownType.TryGetValue(other, out int type) && type > 0) types.Add(type);
                    else if (derived.TryGetValue(other, out type)) types.Add(type);
                if (types.Count == 1) { derived[id] = types.First(); changed = true; }
                else if (types.Count > 1) conflicting.Add(id);
            }
        }
        return derived;
    }
}
