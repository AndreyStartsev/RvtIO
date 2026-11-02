using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The filter of the elements that do not exist in the export phase, null when there is none to apply. With a known
    /// phase order it is the general rule (<see cref="ExportPhaseFilter"/>). Without it, the default export phase (the last one, no
    /// explicit <see cref="ExportPhaseId"/>) still needs only the set of phases: an element demolished in any phase of the project
    /// is demolished at or before the last one. That set is the ProjectPhase elements of the document; a document without any is
    /// not filtered.</summary>
    private ExportPhaseFilter? BuildExportPhaseFilter(RevitFileInfo info)
    {
        if (info.SerializedProjectPhaseIds is { } order)
            return ResolveActivePhase(info) is int phase ? ExportPhaseFilter.For(order, phase) : null;
        if (ExportPhaseId != null || ExportAllPhases || !CurrentProfile) return null;
        var phases = info.Elements.Where(element => element.RootClassName == "ProjectPhase"
                && !(info.ElemTableIsExact && element.IsDiscovered))
            .Select(element => element.Id).ToArray();
        return phases.Length == 0 ? null : ExportPhaseFilter.OfLastPhase(phases);
    }
}
