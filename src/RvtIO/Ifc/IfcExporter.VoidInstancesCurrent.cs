using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>Whether every decoded Face of the element is unfilled (<see cref="ProductRules.HasOnlyUnfilledFaces"/>). The current
    /// profile writes no such instance.</summary>
    internal static bool HasOnlyUnfilledFacesCurrent(Element element) => ProductRules.HasOnlyUnfilledFaces(element);
}
