using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>Container semantics for the core members of a stair.
/// Admission happens after product emission, not from decoded geometry or bounding boxes.
/// Railing host links are independent of core ownership. Run/landing/support names use
/// numeric ordinals of the stair's component sets, including filtered members.
/// The current profile repeats a multistory stair on each level of its data cell: copy k (from 2)
/// is an IfcStair without representation in the storey of that level, moved by the distance between
/// that level and the base level of the stair, which aggregates copies of the stair's components and
/// railings over the same representation items. The GlobalId of the copy of the i-th exported support comes from the i-th
/// support of the whole numeric set, exported or not; the copy keeps the Tag of the support it repeats.</summary>
internal static class StairAggregation
{
    internal static void Apply(StepWriter writer, IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products,
        Dictionary<int, List<int>> storeyContents, int ownerHistory, bool currentProfile = false,
        IReadOnlyDictionary<int, int>? storeyIds = null, IReadOnlyDictionary<int, int>? storeyPlacements = null,
        IReadOnlyDictionary<int, double>? levelElevations = null)
    {
        var candidates = elements.Where(element => element.SerializedStairCore is { Length: > 0 }).ToArray();
        if (candidates.Length == 0) return;
        var counts = new Dictionary<int, int>();
        foreach (var element in elements) counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        var unique = elements.Where(element => counts[element.Id] == 1).ToDictionary(element => element.Id);
        var railingsByHost = unique.Values.Where(element => !element.IsBlockOnly && element.SerializedRailingStairHostId is > 0)
            .ToLookup(element => element.SerializedRailingStairHostId!.Value);
        var indexById = new Dictionary<int, int>(writer.EntityCount);
        var decomposed = new HashSet<int>();
        var existingParents = new HashSet<int>();
        for (int index = 0; index < writer.EntityCount; index++)
        {
            var entity = writer.EntityAt(index);
            indexById.Add(entity.Id, index);
            if (entity.Type is not ("IFCRELAGGREGATES" or "IFCRELNESTS")) continue;
            var args = IfcExporter.SplitStepArguments(entity.Args);
            if (args.Length != 6) continue;
            if (Id(args[4], out int parent)) existingParents.Add(parent);
            foreach (string child in List(args[5])) if (Id(child, out int id)) decomposed.Add(id);
        }
        foreach (var parent in candidates)
        {
            if (parent.Id <= 0 || parent.IsBlockOnly || parent.CategoryId != -2000120
                || parent.RuleClassName != "StairsElement" || !unique.ContainsKey(parent.Id)
                || !products.TryGetValue(parent.Id, out var emittedParent)
                || emittedParent.ifcType != "IFCSTAIR" || existingParents.Contains(emittedParent.entityId)
                || !indexById.TryGetValue(emittedParent.entityId, out int parentIndex)) continue;
            var parentEntity = writer.EntityAt(parentIndex);
            var parentArgs = IfcExporter.SplitStepArguments(parentEntity.Args);
            if (parentEntity.Type != "IFCSTAIR" || parentArgs.Length != 9
                || parentArgs[7] != StepWriter.Str(parent.Id.ToString())
                || parentArgs[0].Length != 24 || IfcGuid.Parse(parentArgs[0][1..^1]) == null) continue;
            var core = parent.SerializedStairCore!;
            if (core.Select(child => child.Id).Distinct().Count() != core.Length
                || core.Any(child => child.Id <= 0 || child.Id == parent.Id
                    || child.ClassId is not (3708 or 3686 or 3727)
                    || !unique.TryGetValue(child.Id, out var element) || element.IsBlockOnly
                    || element.SerializedOwningElementId != parent.Id
                    || element.SerializedOwnElementClassId != child.ClassId)) continue;
            var handles = new List<int>();
            foreach (var child in core)
            {
                string expectedClass = child.ClassId switch
                {
                    3708 => "IFCSTAIRFLIGHT", 3686 => "IFCSLAB", _ => "IFCMEMBER",
                };
                if (!products.TryGetValue(child.Id, out var product) || product.ifcType != expectedClass
                    || !indexById.TryGetValue(product.entityId, out int index)
                    || decomposed.Contains(product.entityId)) continue;
                var entity = writer.EntityAt(index);
                var args = IfcExporter.SplitStepArguments(entity.Args);
                if (entity.Type == expectedClass && args.Length >= 8
                    && args[7] == StepWriter.Str(child.Id.ToString()) && HasBody(args[6]))
                    handles.Add(entity.Id);
            }
            if (handles.Count == 0) continue;
            string? validatedParentName = IfcExporter.ResolveNativeStairName(parent, unique, counts, currentProfile);
            if (validatedParentName != null && parentArgs[2] == StepWriter.Str(validatedParentName))
            {
                // The index is incremented before the Body success gate, separately for
                // runs, landings and supports; the numeric order of the support set is fixed.
                // Never renumber around a filtered/failed sibling.
                foreach (ushort role in new ushort[] { 3708, 3686, 3727 })
                {
                    int ordinal = 0;
                    foreach (var child in core.Where(child => child.ClassId == role).OrderBy(child => child.Id))
                    {
                        ordinal++;
                        if (!products.TryGetValue(child.Id, out var product) || !handles.Contains(product.entityId)) continue;
                        string original = validatedParentName + (role switch
                            { 3708 => " Run ", 3686 => " Landing ", _ => " Stringer " })
                            + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string? name = IfcExporter.ResolveNativeStairNameOverride(unique[child.Id], original);
                        if (name == null) continue;
                        int childIndex = indexById[product.entityId];
                        var args = IfcExporter.SplitStepArguments(writer.EntityAt(childIndex).Args);
                        args[2] = StepWriter.Str(name);
                        writer.ReplaceArgs(childIndex, string.Join(",", args));
                    }
                }
            }
            // Railings use Railing.HostId, not the stair's incomplete registered
            // list or m_OwningElementId. The container must already have
            // admitted a successful core component before railings can join it.
            foreach (var railing in railingsByHost[parent.Id])
            {
                if (railing.Id <= 0 || railing.Id == parent.Id || railing.RuleClassName != "BaseRailing"
                    || railing.SerializedOwningElementId == null
                    || !products.TryGetValue(railing.Id, out var product) || product.ifcType != "IFCRAILING"
                    || decomposed.Contains(product.entityId)
                    || !indexById.TryGetValue(product.entityId, out int index)) continue;
                var entity = writer.EntityAt(index);
                var args = IfcExporter.SplitStepArguments(entity.Args);
                if (entity.Type == "IFCRAILING" && args.Length == 9
                    && args[7] == StepWriter.Str(railing.Id.ToString()) && HasBody(args[6])) handles.Add(entity.Id);
            }
            // The relation GlobalId hashes "IfcRelAggregates:" + the actual parent GlobalId; this is not an index XOR.
            string guid = IfcGuid.FromMd5("IfcRelAggregates:" + parentArgs[0][1..^1]);
            writer.Add("IFCRELAGGREGATES", $"{StepWriter.Str(guid)},{StepWriter.Ref(ownerHistory)},$,$,"
                + $"{StepWriter.Ref(emittedParent.entityId)},{StepWriter.List(handles.Order().Select(StepWriter.Ref))}");
            parentArgs[6] = "$";
            writer.ReplaceArgs(parentIndex, string.Join(",", parentArgs));
            var handleSet = handles.ToHashSet();
            foreach (var contents in storeyContents.Values) contents.RemoveAll(handleSet.Contains);
            decomposed.UnionWith(handles);
            existingParents.Add(emittedParent.entityId);
            if (currentProfile && storeyIds != null && storeyPlacements != null
                && parent.SerializedStairCopyLevelIds is { Length: > 0 } levels)
            {
                var supports = core.Where(child => child.ClassId == 3727).Select(child => child.Id).Order().ToArray();
                var seeds = new Dictionary<int, string>();
                int position = 0;
                foreach (int id in supports)
                    if (products.TryGetValue(id, out var product) && handles.Contains(product.entityId))
                        seeds.Add(product.entityId, GlobalId(supports[position++]));
                WriteLevelCopies(emittedParent.entityId, parent.SerializedStairBaseLevelId, levels, parentArgs, handles, seeds);
            }
        }

        // An element that is not exported has the GlobalId it would have been exported with.
        string GlobalId(int elementId) =>
            products.TryGetValue(elementId, out var product) && indexById.TryGetValue(product.entityId, out int index)
                ? IfcExporter.SplitStepArguments(writer.EntityAt(index).Args)[0][1..^1]
                : unique[elementId].SerializedExportId is Guid exportId ? IfcGuid.ToIfcGuid(exportId) : IfcGuid.FromElementId(elementId);

        void WriteLevelCopies(int stairId, int? baseLevel, int[] levels, string[] stairArgs, List<int> handles,
            Dictionary<int, string> supportSeeds)
        {
            // The stair and its components stand on the placement the storey placements hang from. A copy stands
            // on a placement that is as far from the storey of its level as that one is from the base level of the
            // stair, whichever storey holds the stair: a base level that is not a storey counts with the height its
            // storey placement would have. A stair without a base level of a known height is not repeated.
            if (!Placement(stairArgs[5], out var placement) || baseLevel is not int baseLevelId
                || !storeyContents.Values.Any(held => held.Contains(stairId))) return;
            double[] offset;
            if (storeyIds!.TryGetValue(baseLevelId, out int baseStorey))
            {
                if (!storeyPlacements!.TryGetValue(baseStorey, out int basePlacement) || !Offset(basePlacement, placement[0], out offset)) return;
            }
            else if (levelElevations != null && levelElevations.TryGetValue(baseLevelId, out double elevation)) offset = [0, 0, elevation];
            else return;
            string stairGuid = stairArgs[0][1..^1], suffix = "";
            for (int copy = 0; copy < levels.Length; copy++)
            {
                // Component names keep the suffixes of the earlier copies; the stair name does not.
                string number = " : " + (copy + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix += number;
                // A level that is not an exported storey has no placement to repeat the stair under.
                if (!storeyIds.TryGetValue(levels[copy], out int storey) || !storeyPlacements!.TryGetValue(storey, out int storeyPlacement)
                    || !storeyContents.TryGetValue(storey, out var contents) || !Offset(storeyPlacement, placement[0], out _)) continue;
                int point = writer.Add("IFCCARTESIANPOINT",
                    StepWriter.List(offset.Select(value => StepWriter.RealExact(value == 0 ? 0 : -value))));
                int origin = writer.Add("IFCLOCALPLACEMENT",
                    $"{StepWriter.Ref(storeyPlacement)},{StepWriter.Ref(writer.Add("IFCAXIS2PLACEMENT3D", $"{StepWriter.Ref(point)},$,$"))}");
                int copyPlacement = writer.Add("IFCLOCALPLACEMENT", $"{StepWriter.Ref(origin)},{placement[1]}");
                var copyArgs = (string[])stairArgs.Clone();
                string copyGuid = IfcGuid.FromMd5(stairGuid + "Sub-element:IfcStair Copy: " + number[3..]);
                copyArgs[0] = StepWriter.Str(copyGuid);
                copyArgs[2] = Suffixed(stairArgs[2], number);
                copyArgs[5] = StepWriter.Ref(copyPlacement);
                int copyStair = writer.Add("IFCSTAIR", string.Join(",", copyArgs));
                var children = new List<int>();
                foreach (int handle in handles.Order())
                {
                    var entity = writer.EntityAt(indexById[handle]);
                    var args = IfcExporter.SplitStepArguments(entity.Args);
                    if (!Placement(args[5], out var childPlacement)) continue;
                    // A component placed under anything else has no placement that follows the copy.
                    int relativeTo = childPlacement[0] == stairArgs[5] ? copyPlacement
                        : childPlacement[0] == placement[0] ? origin : 0;
                    if (relativeTo == 0) continue;
                    string guid = args[0][1..^1];
                    if (entity.Type == "IFCRAILING")
                        args[0] = StepWriter.Str(IfcGuid.FromMd5($"IfcRailing:{copy + 1}:{guid}"));
                    else
                    {
                        string role = entity.Type switch { "IFCSTAIRFLIGHT" => "IfcStairFlight", "IFCSLAB" => "IfcSlab", _ => "IfcMember" };
                        args[0] = StepWriter.Str(IfcGuid.FromMd5($"{supportSeeds.GetValueOrDefault(handle, guid)}Sub-element:{role}: {copy}"));
                        args[2] = Suffixed(args[2], suffix);
                    }
                    args[5] = StepWriter.Ref(writer.Add("IFCLOCALPLACEMENT", $"{StepWriter.Ref(relativeTo)},{childPlacement[1]}"));
                    args[6] = CopyShape(args[6]);
                    children.Add(writer.Add(entity.Type, string.Join(",", args)));
                }
                if (children.Count > 0)
                    writer.Add("IFCRELAGGREGATES", $"{StepWriter.Str(IfcGuid.FromMd5("IfcRelAggregates:" + copyGuid))},{StepWriter.Ref(ownerHistory)},$,$,"
                        + $"{StepWriter.Ref(copyStair)},{StepWriter.List(children.Select(StepWriter.Ref))}");
                contents.Add(copyStair);
            }
        }

        bool Placement(string reference, out string[] args)
        {
            args = [];
            if (!Id(reference, out int id) || !indexById.TryGetValue(id, out int index)
                || writer.EntityAt(index).Type != "IFCLOCALPLACEMENT") return false;
            args = IfcExporter.SplitStepArguments(writer.EntityAt(index).Args);
            return args.Length == 2;
        }

        // The offset of a placement without rotation from the given placement.
        bool Offset(int placementId, string relativeTo, out double[] offset)
        {
            offset = [];
            if (!Placement(StepWriter.Ref(placementId), out var args) || args[0] != relativeTo
                || !Id(args[1], out int axisId) || !indexById.TryGetValue(axisId, out int axisIndex)) return false;
            var axis = IfcExporter.SplitStepArguments(writer.EntityAt(axisIndex).Args);
            if (writer.EntityAt(axisIndex).Type != "IFCAXIS2PLACEMENT3D" || axis.Length != 3 || axis[1] != "$" || axis[2] != "$"
                || !Id(axis[0], out int pointId) || !indexById.TryGetValue(pointId, out int pointIndex)
                || writer.EntityAt(pointIndex).Type != "IFCCARTESIANPOINT") return false;
            var coordinates = List(writer.EntityAt(pointIndex).Args);
            offset = new double[coordinates.Length];
            for (int axisNumber = 0; axisNumber < offset.Length; axisNumber++)
                if (!double.TryParse(coordinates[axisNumber], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out offset[axisNumber])) return false;
            return offset.Length == 3;
        }

        static string Suffixed(string name, string suffix) =>
            name.Length >= 2 && name[0] == '\'' && name[^1] == '\'' ? name[..^1] + suffix + "'" : name;

        // A new shape with new representations over the items of the original.
        string CopyShape(string shape)
        {
            if (!Id(shape, out int id) || !indexById.TryGetValue(id, out int index)) return shape;
            var args = IfcExporter.SplitStepArguments(writer.EntityAt(index).Args);
            if (writer.EntityAt(index).Type != "IFCPRODUCTDEFINITIONSHAPE" || args.Length != 3) return shape;
            args[2] = StepWriter.List(List(args[2]).Select(representation =>
                Id(representation, out int repId) && indexById.TryGetValue(repId, out int repIndex)
                    && writer.EntityAt(repIndex).Type == "IFCSHAPEREPRESENTATION"
                    ? StepWriter.Ref(writer.Add("IFCSHAPEREPRESENTATION", writer.EntityAt(repIndex).Args)) : representation));
            return StepWriter.Ref(writer.Add("IFCPRODUCTDEFINITIONSHAPE", string.Join(",", args)));
        }

        bool HasBody(string shape)
        {
            if (!Id(shape, out int id) || !indexById.TryGetValue(id, out int index)) return false;
            var entity = writer.EntityAt(index);
            var args = IfcExporter.SplitStepArguments(entity.Args);
            if (entity.Type != "IFCPRODUCTDEFINITIONSHAPE" || args.Length != 3) return false;
            foreach (string representation in List(args[2]))
            {
                if (!Id(representation, out int repId) || !indexById.TryGetValue(repId, out int repIndex)) continue;
                var rep = writer.EntityAt(repIndex);
                var repArgs = IfcExporter.SplitStepArguments(rep.Args);
                if (rep.Type == "IFCSHAPEREPRESENTATION" && repArgs.Length == 4 && repArgs[1] == "'Body'"
                    && List(repArgs[3]) is { Length: > 0 } items
                    && items.All(item => Id(item, out int itemId) && indexById.ContainsKey(itemId))) return true;
            }
            return false;
        }
    }

    /// <summary>Sub-element of the ExportId that names the stair holding the body (the GlobalId of
    /// the second IfcStair differs from the first by 2 in bits 32-63).</summary>
    internal const int LegacyStairBodySubElementIndex = 2;

    /// <summary>A stair of the legacy class Stairs (it has no runs, landings or supports) is exported by the current profile as an
    /// IfcStair without representation that aggregates a second IfcStair under a copy of its placement; the second holds the
    /// body, and the railings hosted by the stair are aggregated next to it (a stair with runs is one IfcStair that aggregates them).
    /// The property sets, the spatial containment and the name stay with the first, the type is shared. Returns, for each stair
    /// split, the entity id of the second IfcStair.</summary>
    internal static Dictionary<int, int> SplitLegacyBodies(StepWriter writer, IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products,
        Dictionary<int, List<int>> storeyContents, int ownerHistory, int axisId, ISet<Guid> usedExportIds)
    {
        var bodies = new Dictionary<int, int>();
        var stairs = elements.Where(element => element.Id > 0 && !element.IsBlockOnly && element.CategoryId == -2000120
            && element.RootClassName == "Stairs" && products.TryGetValue(element.Id, out var product) && product.ifcType == "IFCSTAIR")
            .OrderBy(element => element.Id).ToList();
        if (stairs.Count == 0) return bodies;
        var counts = new Dictionary<int, int>();
        foreach (var element in elements) counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        var railingsByHost = elements.Where(element => element.Id > 0 && !element.IsBlockOnly && counts[element.Id] == 1
                && element.SerializedRailingStairHostId is > 0 && element.RuleClassName == "BaseRailing"
                && element.SerializedOwningElementId != null)
            .ToLookup(element => element.SerializedRailingStairHostId!.Value);
        var indexById = new Dictionary<int, int>(writer.EntityCount);
        var decomposed = new HashSet<int>();
        for (int index = 0; index < writer.EntityCount; index++)
        {
            var entity = writer.EntityAt(index);
            indexById.Add(entity.Id, index);
            if (entity.Type is not ("IFCRELAGGREGATES" or "IFCRELNESTS")) continue;
            var args = IfcExporter.SplitStepArguments(entity.Args);
            if (args.Length == 6) foreach (string child in List(args[5])) if (Id(child, out int id)) decomposed.Add(id);
        }
        foreach (var stair in stairs)
        {
            if (counts[stair.Id] != 1) continue;
            var parent = products[stair.Id];
            int parentIndex = indexById[parent.entityId];
            var parentArgs = IfcExporter.SplitStepArguments(writer.EntityAt(parentIndex).Args);
            if (writer.EntityAt(parentIndex).Type != "IFCSTAIR" || parentArgs.Length != 9
                || parentArgs[7] != StepWriter.Str(stair.Id.ToString()) || parentArgs[6] == "$"
                || parentArgs[0].Length != 24 || decomposed.Contains(parent.entityId)) continue;
            string parentGuid = parentArgs[0][1..^1];
            string bodyGuid = IfcExporter.ResolveExactElementGlobalId(stair, usedExportIds, LegacyStairBodySubElementIndex, true)
                ?? IfcGuid.FromName($"stair-body:{stair.Id}");
            int placement = writer.Add("IFCLOCALPLACEMENT", $"{parentArgs[5]},{StepWriter.Ref(axisId)}");
            int body = writer.Add("IFCSTAIR", string.Join(',', StepWriter.Str(bodyGuid), parentArgs[1], parentArgs[2], parentArgs[3],
                parentArgs[4], StepWriter.Ref(placement), parentArgs[6], parentArgs[7], parentArgs[8]));
            parentArgs[6] = "$";
            writer.ReplaceArgs(parentIndex, string.Join(",", parentArgs));
            var handles = new List<int> { body };
            foreach (var railing in railingsByHost[stair.Id].OrderBy(railing => railing.Id))
            {
                if (railing.Id == stair.Id || !products.TryGetValue(railing.Id, out var product) || product.ifcType != "IFCRAILING"
                    || decomposed.Contains(product.entityId)) continue;
                handles.Add(product.entityId);
            }
            writer.Add("IFCRELAGGREGATES", $"{StepWriter.Str(IfcGuid.FromMd5("IfcRelAggregates:" + parentGuid))},{StepWriter.Ref(ownerHistory)},$,$,"
                + $"{StepWriter.Ref(parent.entityId)},{StepWriter.List(handles.Select(StepWriter.Ref))}");
            var handleSet = handles.ToHashSet();
            foreach (var contents in storeyContents.Values) contents.RemoveAll(handleSet.Contains);
            decomposed.UnionWith(handles);
            bodies[stair.Id] = body;
        }
        return bodies;
    }

    private static bool Id(string value, out int id)
    {
        id = 0;
        return value.StartsWith('#') && int.TryParse(value.AsSpan(1), out id) && id > 0;
    }
    private static string[] List(string value) => value.StartsWith('(') && value.EndsWith(')') && value.Length > 2
        ? IfcExporter.SplitStepArguments(value[1..^1]) : [];
}
