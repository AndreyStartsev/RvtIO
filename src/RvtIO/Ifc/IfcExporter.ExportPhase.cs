using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The filter of the elements that do not exist in the export phase, null when there is none to apply: the rule of
    /// <see cref="ProductRules.BuildExportPhaseFilter"/> for <see cref="ExportPhaseId"/>, with the last project phase as the default of the
    /// current profile unless <see cref="ExportAllPhases"/> is set.</summary>
    private ExportPhaseFilter? BuildExportPhaseFilter(RevitFileInfo info) =>
        ProductRules.BuildExportPhaseFilter(info, ExportPhaseId, CurrentProfile && !ExportAllPhases);
}
