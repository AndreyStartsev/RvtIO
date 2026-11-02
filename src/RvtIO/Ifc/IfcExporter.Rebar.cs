using System.Globalization;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Ifc;

/// <summary>Reinforcement bars of the current profile (IFC2X3): every Rebar and RebarInSystem element is an IfcReinforcingBar whose Body is
/// one IfcSweptDiskSolid per bar of the set, swept along the centre line (an IfcCompositeCurve of IfcPolyline pieces and trimmed
/// IfcCircle arcs). A bar is contained in the storey of its lowest point (in the building when the project has no storey) unless it is
/// a member of an assembly; the bars of one RebarSystem
/// (area reinforcement) that are not members of an assembly are related to an IfcGroup of the system.</summary>
public partial class IfcExporter
{
    /// <summary>RebarBarType's family label, the first part of the name of every bar.</summary>
    private const string RebarFamilyLabel = "Rebar Bar";

    private const double FeetToMillimetres = 304.8;

    /// <summary>The parameter of a line in the unit of the file: the end parameter of a directrix adds it to the degrees
    /// of its arcs, so it is not a length the final conversion could reach.</summary>
    private double LengthUnitsPerFoot => _unitFamily switch
    {
        IfcUnitFamily.Foot => 1,
        IfcUnitFamily.Metre => FeetToMetres,
        _ => FeetToMillimetres,
    };

    /// <summary>The bar products of each RebarSystem by system element id, in element order.</summary>
    private readonly Dictionary<int, List<int>> _rebarSystemMembers = new();

    /// <summary>Writes the product of a reinforcement element; false (nothing written) when its type has no usable name.</summary>
    private bool WriteRebarProduct(StepWriter w, Element elem, IReadOnlyDictionary<int, Element> elemById, int ownerHistId,
        int axisId, int parentPlacementId, int bodySubCtxId, Dictionary<(string PropertyName, string ValueExpr), int> propertySingleValueIds,
        bool shapeInName, Dictionary<int, int> storeyIds, int defaultStoreyId, Dictionary<int, List<int>> storeyContents)
    {
        var data = elem.RebarBars!;
        if (data.Bars.Count == 0 || data.Bars.Any(bar => bar.Count == 0)) return false;
        string? barTypeName = data.BarTypeName;
        if (string.IsNullOrEmpty(barTypeName)) return false;

        var items = new List<string>();
        double firstBarLengthFt = 0;
        foreach (var bar in data.Bars)
        {
            double lengthFt;
            items.Add(R(WriteSweptDisk(w, bar, data.ModelDiameter / 2 * FeetToMillimetres, out lengthFt)));
            if (firstBarLengthFt == 0) firstBarLengthFt = lengthFt;
        }
        int representationId = w.Add("IFCSHAPEREPRESENTATION",
            $"{R(bodySubCtxId)},{S("Body")},{S("AdvancedSweptSolid")},{StepWriter.List(items)}");
        int shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"{S("")},{S("")},{StepWriter.List(R(representationId))}");
        int placementId = w.Add("IFCLOCALPLACEMENT", $"{R(parentPlacementId)},{R(axisId)}");

        // BarLength: the stored length of a shape driven bar, else the length of the first bar, rounded as the document says
        // (a length a hair below a half step rounds up: 1444.9999999999927 mm gives 1450)
        double barLengthMm = (data.BarLength ?? firstBarLengthFt) * FeetToMillimetres;
        if (data.LengthRoundingMillimetres > 0)
            barLengthMm = Math.Floor(barLengthMm / data.LengthRoundingMillimetres + 0.5 + 1e-9) * data.LengthRoundingMillimetres;
        double nominalMm = data.NominalDiameter * FeetToMillimetres;
        double area = Math.PI * Math.Pow(nominalMm / 2000, 2);

        // For shape driven bars of Revit 2026 files the shape follows the type
        string typeLabel = shapeInName && data.SystemId == 0 && data.ShapeName != null ? $"{barTypeName} : Shape {data.ShapeName}" : barTypeName;
        string elemName = $"{RebarFamilyLabel}:{typeLabel}:{elem.Id.ToString(CultureInfo.InvariantCulture)}";
        string objectType = data.SystemId > 0 ? barTypeName : $"{RebarFamilyLabel}:{barTypeName}";
        // RebarInSystem: the sub-element index is the number of bars plus 3; a Rebar has none
        string globalId = ResolveExactElementGlobalId(elem, _usedExportIds, data.SystemId > 0 ? data.Bars.Count + 3 : 0, CurrentProfile)
            ?? IfcGuid.FromElementId(elem.Id);
        int entityId = w.Add("IFCREINFORCINGBAR",
            $"{S(globalId)},{R(ownerHistId)},{S(elemName)},{S("")},{S(objectType)},{R(placementId)},{R(shapeId)},{S(elem.Id.ToString(CultureInfo.InvariantCulture))},"
            + $"{S("")},{D(nominalMm)},{D(area)},{D(barLengthMm)},.NOTDEFINED.,$");
        ExportQuantityTakeOffPropertySet(w, elem, entityId, ownerHistId, barTypeName, propertySingleValueIds);
        // A bar has no level of its own (Element.m_assocLevelId = -1), so the storey is the one that holds the bottom of its bounding box;
        // the bars of an assembly are aggregated by it instead of contained; a project without a storey puts them in the building
        if (!data.InAssembly)
        {
            double bottomFt = data.Bars.SelectMany(bar => bar).Min(piece => piece.MinZ()) - data.ModelDiameter / 2;
            int byBottom = storeyIds.Count > 0 ? ResolveStoreyByBottomCurrent(bottomFt, storeyIds, elemById) : 0;
            int storeyId = byBottom != 0 ? byBottom : defaultStoreyId;
            if (!storeyContents.TryGetValue(storeyId, out var contents)) storeyContents[storeyId] = contents = [];
            contents.Add(entityId);
        }
        // The bars of an assembly are related to the assembly (IfcRelAggregates) and no group of their system is written
        if (data.SystemId > 0 && !data.InAssembly)
        {
            if (!_rebarSystemMembers.TryGetValue(data.SystemId, out var members)) _rebarSystemMembers[data.SystemId] = members = [];
            members.Add(entityId);
        }
        return true;
    }

    /// <summary>One IfcSweptDiskSolid along the pieces of a bar. Lengths of the directrix are written in metres and shifted to
    /// the file unit with the rest of the geometry; the disk (radius) is written in millimetres and converted with it, its end parameter in the file unit.</summary>
    private int WriteSweptDisk(StepWriter w, IReadOnlyList<RebarSegment> pieces, double radiusMm, out double lengthFt)
    {
        string Point(double[] p) => R(w.Add("IFCCARTESIANPOINT", StepWriter.List(D(p[0] * FeetToMetres), D(p[1] * FeetToMetres), D(p[2] * FeetToMetres))));
        string Direction(double[] v) => R(w.Add("IFCDIRECTION", StepWriter.List(D(v[0]), D(v[1]), D(v[2]))));
        var curves = new List<int>();
        lengthFt = 0;
        // the end parameter of the directrix is the sum of the parameter extents of its pieces: millimetres of a line, degrees of an arc
        double endParameter = 0;
        foreach (var piece in pieces)
        {
            int curve;
            if (!piece.IsArc)
            {
                double[] At(double t) => [piece.Origin[0] + piece.Axis[0] * t, piece.Origin[1] + piece.Axis[1] * t, piece.Origin[2] + piece.Axis[2] * t];
                curve = w.Add("IFCPOLYLINE", StepWriter.List(Point(At(piece.Start)), Point(At(piece.End))));
                lengthFt += Math.Abs(piece.End - piece.Start);
                endParameter += Math.Abs(piece.End - piece.Start) * LengthUnitsPerFoot;
            }
            else
            {
                // the circle is placed so that the arc starts at its parameter 0: the reference direction points to the start of the arc
                double[] x = piece.Axis, y = piece.Axis2;
                double[] axis = [x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]];
                double cos = Math.Cos(piece.Start), sin = Math.Sin(piece.Start);
                double[] start = [x[0] * cos + y[0] * sin, x[1] * cos + y[1] * sin, x[2] * cos + y[2] * sin];
                int placement = w.Add("IFCAXIS2PLACEMENT3D", $"{Point(piece.Origin)},{Direction(axis)},{Direction(start)}");
                int circle = w.Add("IFCCIRCLE", $"{R(placement)},{D(piece.Radius * FeetToMetres)}");
                double degrees = (piece.End - piece.Start) * 180 / Math.PI;
                curve = w.Add("IFCTRIMMEDCURVE",
                    $"{R(circle)},{StepWriter.List("IFCPARAMETERVALUE(0.)")},{StepWriter.List($"IFCPARAMETERVALUE({D(degrees)})")},.T.,.PARAMETER.");
                lengthFt += Math.Abs(piece.End - piece.Start) * piece.Radius;
                endParameter += Math.Abs(degrees);
            }
            curves.Add(curve);
        }
        var segments = curves.Select((curve, i) =>
            R(w.Add("IFCCOMPOSITECURVESEGMENT", $"{(i == curves.Count - 1 ? ".DISCONTINUOUS." : ".CONTINUOUS.")},.T.,{R(curve)}"))).ToList();
        int composite = w.Add("IFCCOMPOSITECURVE", $"{StepWriter.List(segments)},.F.");
        return w.Add("IFCSWEPTDISKSOLID", $"{R(composite)},{D(radiusMm)},$,0.,{D(endParameter)}");
    }

    /// <summary>The IfcGroup of every RebarSystem that has bars, with the IfcRelAssignsToGroup that relates them.</summary>
    private void WriteRebarSystemGroups(StepWriter w, int ownerHistId, IReadOnlyDictionary<int, Element> elemById)
    {
        foreach (var (systemId, members) in _rebarSystemMembers.OrderBy(pair => pair.Key))
        {
            if (!elemById.TryGetValue(systemId, out var system) || string.IsNullOrEmpty(system.Name)) continue;
            string label = $"Structural Area Reinforcement:{system.Name}";
            string globalId = ResolveExactElementGlobalId(system, _usedExportIds, handleBased: CurrentProfile) ?? IfcGuid.FromElementId(system.Id);
            int group = w.Add("IFCGROUP", $"{S(globalId)},{R(ownerHistId)},{S(label)},{S("")},{S(label)}");
            w.Add("IFCRELASSIGNSTOGROUP",
                $"{S(IfcGuid.FromMd5($"IfcRelAssignsToGroup:{globalId}"))},{R(ownerHistId)},{S("")},{S("")},{StepWriter.List(members.Select(R))},$,{R(group)}");
        }
        _rebarSystemMembers.Clear();
    }

    /// <summary>JoistSystem's family label, the first part of the name of every beam system.</summary>
    private const string BeamSystemFamilyLabel = "Structural Beam System";

    /// <summary>The IfcElementAssembly (BEAM_GRID, no representation) of every beam system whose beams were exported, and the
    /// IfcRelAggregates to those beams. The beams leave the storey and the assembly takes their place.
    /// The assembly is not registered as an exported element: it gets no material or type relation (the later passes read that list).
    /// Returns the ids of the beam systems written.</summary>
    private List<int> WriteJoistAssemblies(StepWriter w, IReadOnlyList<Element> elements, int ownerHistId, int parentPlacementId, int axisId,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> exported, IReadOnlyDictionary<int, int> exportedStoreyIds,
        Dictionary<int, List<int>> storeyContents, Dictionary<(string PropertyName, string ValueExpr), int> propertySingleValueIds)
    {
        var written = new List<int>();
        foreach (var elem in elements.Where(e => e.JoistSystem != null).OrderBy(e => e.Id))
        {
            var data = elem.JoistSystem!;
            var memberIds = data.BeamIds.Where(exported.ContainsKey).Distinct().ToList();
            if (string.IsNullOrEmpty(data.TypeName) || memberIds.Count == 0 || !exportedStoreyIds.TryGetValue(memberIds[0], out int storeyId)) continue;
            string objectType = $"{BeamSystemFamilyLabel}:{data.TypeName}";
            string globalId = ResolveExactElementGlobalId(elem, _usedExportIds, handleBased: CurrentProfile) ?? IfcGuid.FromElementId(elem.Id);
            string tag = elem.Id.ToString(CultureInfo.InvariantCulture);
            int placement = w.Add("IFCLOCALPLACEMENT", $"{R(parentPlacementId)},{R(axisId)}");
            int entity = w.Add("IFCELEMENTASSEMBLY",
                $"{S(globalId)},{R(ownerHistId)},{S($"{objectType}:{tag}")},{S("")},{S(objectType)},{R(placement)},$,{S(tag)},.NOTDEFINED.,.BEAM_GRID.");
            // IfcRelAggregates GlobalId: the export id with sub-element index 2
            var memberEntities = memberIds.Select(id => exported[id].entityId).Order().ToList();
            w.Add("IFCRELAGGREGATES",
                $"{S(ResolveExactElementGlobalId(elem, _usedExportIds, 2, CurrentProfile) ?? IfcGuid.FromName($"joist-aggregate:{elem.Id}"))},{R(ownerHistId)},{S("")},{S("")},"
                + $"{R(entity)},{StepWriter.List(memberEntities.Select(R))}");
            ExportQuantityTakeOffPropertySet(w, elem, entity, ownerHistId, data.TypeName, propertySingleValueIds);
            var memberSet = memberEntities.ToHashSet();
            foreach (var contents in storeyContents.Values) contents.RemoveAll(memberSet.Contains);
            if (!storeyContents.TryGetValue(storeyId, out var storey)) storeyContents[storeyId] = storey = [];
            storey.Add(entity);
            written.Add(elem.Id);
        }
        return written;
    }
}
