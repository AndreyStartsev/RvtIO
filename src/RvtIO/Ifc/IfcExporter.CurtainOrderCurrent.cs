using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The order in which the current profile exports the sub-elements of a curtain wall or curtain roof: all in one element call,
/// so the export state of the body material (the last non-null best material) flows from one sub-element to the next.
/// </summary>
public partial class IfcExporter
{
    /// <summary>Two panels whose lower bounds differ by at most this (feet) are in one row of the curtain grid.</summary>
    internal const double CurtainRowGapFeet = 0.25;

    internal static List<int> CurtainExportOrderCurrent(Element host, IReadOnlyCollection<Element> children)
    {
        var panels = children.Where(child => child.CategoryId != CurtainWallMullionCategoryId).OrderBy(child => child.Id).ToList();
        var ordered = new List<int>(children.Count);
        double[]? start = host.Location?.StartPoint, end = host.Location?.EndPoint;
        double dx = start != null && end != null ? end[0] - start[0] : 0, dy = start != null && end != null ? end[1] - start[1] : 0;
        double length = Math.Sqrt(dx * dx + dy * dy);
        var bounded = panels.Where(panel => (panel.DecodedBox ?? panel.Geometry?.BoundingBox) is { Length: >= 6 }).ToList();
        if (host.SerializedCurtainPanelOrder is { Length: > 0 } stored)
        {
            var listed = new HashSet<int>(panels.Select(panel => panel.Id));
            var visited = new HashSet<int>();
            foreach (int id in stored)
                if (listed.Contains(id) && visited.Add(id)) ordered.Add(id);
            ordered.AddRange(panels.Where(panel => visited.Add(panel.Id)).Select(panel => panel.Id));
        }
        else if (length > 1e-9 && bounded.Count == panels.Count)
        {
            dx /= length;
            dy /= length;
            var cells = panels.Select(panel =>
            {
                double[] box = (panel.DecodedBox ?? panel.Geometry!.BoundingBox)!;
                return (Panel: panel, Bottom: box[2], Along: ((box[0] + box[3]) / 2 - start![0]) * dx + ((box[1] + box[4]) / 2 - start![1]) * dy);
            }).OrderBy(cell => cell.Bottom).ToList();
            var row = new List<(Element Panel, double Bottom, double Along)>();
            for (int i = 0; i <= cells.Count; i++)
            {
                if (i < cells.Count && (row.Count == 0 || cells[i].Bottom - row[^1].Bottom <= CurtainRowGapFeet))
                {
                    row.Add(cells[i]);
                    continue;
                }
                ordered.AddRange(row.OrderBy(cell => cell.Along).ThenBy(cell => cell.Panel.Id).Select(cell => cell.Panel.Id));
                row.Clear();
                if (i < cells.Count) row.Add(cells[i]);
            }
        }
        else
            ordered.AddRange(panels.Select(panel => panel.Id));
        ordered.AddRange(children.Where(child => child.CategoryId == CurtainWallMullionCategoryId).Select(child => child.Id).Order());
        return ordered;
    }

    /// <summary>The export order of the children of every curtain host, the hosts by element id.</summary>
    internal static List<(int HostId, List<int> Order)> CurtainExportOrdersCurrent(
        IEnumerable<KeyValuePair<int, List<int>>> hosts, IReadOnlyDictionary<int, Element> elementsById, Func<int, bool> isExported)
    {
        var result = new List<(int, List<int>)>();
        foreach (var (hostId, childIds) in hosts.OrderBy(pair => pair.Key))
        {
            if (!elementsById.TryGetValue(hostId, out var host)) continue;
            var children = childIds.Where(id => isExported(id) && elementsById.ContainsKey(id)).Select(id => elementsById[id]).ToList();
            var order = CurtainExportOrderCurrent(host, children);
            if (order.Count > 0) result.Add((hostId, order));
        }
        return result;
    }

    internal static int ApplyCurtainPanelTypeOrderCurrent(StepWriter w, IReadOnlyDictionary<int, int> elementIdByEntity,
        IReadOnlyDictionary<int, (int HostId, int Position)> rankByElementId)
    {
        var indexById = new Dictionary<int, int>(w.EntityCount);
        for (int i = 0; i < w.EntityCount; i++) indexById[w.EntityAt(i).Id] = i;

        int rewritten = 0;
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, relationType, relationArgs) = w.EntityAt(i);
            if (relationType != "IFCRELDEFINESBYTYPE") continue;
            var relation = SplitStepArguments(relationArgs);
            if (relation.Length != 6 || !TryEntityId(relation[5], out int typeEntity)
                || !indexById.TryGetValue(typeEntity, out int typeIndex) || w.EntityAt(typeIndex).Type != "IFCPLATETYPE"
                || relation[4].Length < 3 || relation[4][0] != '(')
                continue;
            var members = new List<(int Entity, int Element)>();
            foreach (string reference in SplitStepArguments(relation[4][1..^1]))
            {
                if (!TryEntityId(reference, out int entity) || !elementIdByEntity.TryGetValue(entity, out int elementId))
                { members.Clear(); break; }
                members.Add((entity, elementId));
            }
            if (members.Count == 0) continue;
            var ordered = members.Select((member, position) => (member, position,
                    Key: rankByElementId.TryGetValue(member.Element, out var rank) ? rank : (member.Element, 0)))
                .OrderBy(item => item.Key.Item1).ThenBy(item => item.Key.Item2).ThenBy(item => item.member.Element)
                .Select(item => item.member).ToList();
            bool changed = false;
            if (!ordered.SequenceEqual(members))
            {
                relation[4] = "(" + string.Join(",", ordered.Select(member => "#" + member.Entity)) + ")";
                w.ReplaceArgs(i, string.Join(",", relation));
                changed = true;
            }
            string[] attributes = SplitStepArguments(w.EntityAt(typeIndex).Args);
            string tag = S(ordered[0].Element.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (attributes.Length >= 8 && attributes[7] != tag)
            {
                attributes[7] = tag;
                w.ReplaceArgs(typeIndex, string.Join(",", attributes));
                changed = true;
            }
            if (changed) rewritten++;
        }
        return rewritten;
    }
}
