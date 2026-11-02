using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// Which elements exist in the export phase: the phases are taken in the order of the project's phase list
/// (<see cref="RevitFileInfo.SerializedProjectPhaseIds"/>), and an element is left out when its demolished phase is at or before the
/// export phase, or its created phase is after it. A phase id that is not in the list (the null id, a stale id) never excludes.
/// Rooms, areas and MEP spaces are not decided here: they follow their own phase.
/// </summary>
internal sealed class ExportPhaseFilter
{
    private const int RoomCategoryId = -2000160;
    private const int AreaCategoryId = -2003200;
    private const int MepSpaceCategoryId = -2003600;

    private readonly int[] _order;
    private readonly int _activeIndex;

    private ExportPhaseFilter(int[] phaseOrder, int activeIndex)
    {
        _order = phaseOrder;
        _activeIndex = activeIndex;
    }

    /// <summary>The filter for <paramref name="exportPhaseId"/> in the authored <paramref name="phaseOrder"/> (no evidence when it is not in it).</summary>
    internal static ExportPhaseFilter For(int[] phaseOrder, int exportPhaseId) => new(phaseOrder, Array.IndexOf(phaseOrder, exportPhaseId));

    /// <summary>The export phase is the last of <paramref name="phases"/>, whose order is not known: a phase is never after the last one,
    /// so only the demolished phase decides.</summary>
    internal static ExportPhaseFilter OfLastPhase(int[] phases) => new(phases, phases.Length - 1);

    /// <summary>True: the element does not exist in the export phase and is not exported. False: it exists, is spatial, or there is no
    /// evidence (the export phase is not in the phase list).</summary>
    internal bool Excludes(Element element)
    {
        if (_activeIndex < 0 || element.CategoryId is RoomCategoryId or AreaCategoryId or MepSpaceCategoryId) return false;
        if (element.SerializedDemolishedPhaseId is int demolished && Array.IndexOf(_order, demolished) is >= 0 and var demolishedIndex
            && demolishedIndex <= _activeIndex)
            return true;
        return element.SerializedCreatedPhaseId is int created && Array.IndexOf(_order, created) is >= 0 and var createdIndex
            && createdIndex > _activeIndex;
    }
}
