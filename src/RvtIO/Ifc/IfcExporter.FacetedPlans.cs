using System.Runtime.ExceptionServices;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The faceted shell plan of one symbol geometry, or the exception its computation threw.</summary>
    internal sealed class FacetedPlanSlot
    {
        public List<ClosedComponentCurrent>? Result;
        public ExceptionDispatchInfo? Error;
    }

    /// <summary>Plans computed ahead of the writer for the symbol geometries of the mapped instances, by geometry reference. Only the writer
    /// thread reads or removes entries, and only after <see cref="PrecomputeSymbolFacetedPlans"/> has returned.</summary>
    private readonly Dictionary<GeometryData, FacetedPlanSlot> _facetedPlans = new(ReferenceEqualityComparer.Instance);

    /// <summary>Computes <see cref="ComputeClosedComponents"/> for the distinct symbol geometries of all mapped instances, in parallel.
    /// Each loop iteration reads one geometry and writes one array slot of its own; the slots are published to the dictionary in index order
    /// on the calling thread. A plan nobody asks for is wasted work only, and a plan is handed out once.</summary>
    private void PrecomputeSymbolFacetedPlans(IReadOnlyList<Element> elements, IReadOnlyDictionary<int, Element> elementsById)
    {
        _facetedPlans.Clear();
        var geometries = new List<GeometryData>();
        var seen = new HashSet<GeometryData>(ReferenceEqualityComparer.Instance);
        bool currentProfile = CurrentProfile;
        foreach (var element in elements)
        {
            if (element.Geometry?.ExactMappedInstances is not { Count: > 0 } instances) continue;
            if (currentProfile && element.RuleClassName == "BaseRailing") continue;
            foreach (var instance in instances)
                if (elementsById.TryGetValue(instance.SymbolId, out var symbol)
                    && symbol.Geometry is { Faces.Count: > 0 } geometry && seen.Add(geometry))
                    geometries.Add(geometry);
        }
        if (geometries.Count == 0) return;
        var slots = ComputePlans(geometries, currentProfile, Environment.ProcessorCount);
        for (int index = 0; index < geometries.Count; index++) _facetedPlans[geometries[index]] = slots[index];
    }

    /// <summary>One slot per geometry, in input order. The loop body reads <c>geometries[index]</c> and writes <c>slots[index]</c> only.</summary>
    internal static FacetedPlanSlot[] ComputePlans(IReadOnlyList<GeometryData> geometries, bool currentProfile, int maxDegreeOfParallelism,
        Func<GeometryData, bool, bool, List<ClosedComponentCurrent>>? compute = null)
    {
        compute ??= ComputeClosedComponents;
        var slots = new FacetedPlanSlot[geometries.Count];
        Parallel.For(0, geometries.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, index =>
        {
            var slot = new FacetedPlanSlot();
            try { slot.Result = compute(geometries[index], false, currentProfile); }
            catch (Exception ex) { slot.Error = ExceptionDispatchInfo.Capture(ex); }
            slots[index] = slot;
        });
        return slots;
    }

    /// <summary>The precomputed plan of a symbol geometry (rethrowing what its computation threw), or null when there is none or it was used.</summary>
    private List<ClosedComponentCurrent>? TakeFacetedPlan(GeometryData geometry)
    {
        if (!_facetedPlans.Remove(geometry, out var slot)) return null;
        slot.Error?.Throw();
        return slot.Result;
    }
}
