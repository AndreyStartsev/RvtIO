using System.Globalization;
using System.Text;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The Uniformat classification of the current profile: an IfcClassification "Uniformat" (edition 1998) and, for every distinct
/// Assembly Code, one IfcClassificationReference that an IfcRelAssociatesClassification attaches to the products and type
/// objects that carry the code.
/// </summary>
public partial class IfcExporter
{
    private const int AssemblyCodeParamId = -1002500;
    private const string UniformatUrlCurrent = "https://www.csiresources.org/standards/uniformat";

    private static readonly HashSet<string> UnclassifiedTypeClassesCurrent = new(StringComparer.Ordinal)
    {
        "IFCWALLTYPE", "IFCSLABTYPE", "IFCCOVERINGTYPE", "IFCCURTAINWALLTYPE", "IFCROOFTYPE", "IFCRAILINGTYPE",
        "IFCSTAIRTYPE", "IFCSTAIRFLIGHTTYPE", "IFCRAMPTYPE", "IFCRAMPFLIGHTTYPE", "IFCSPACETYPE",
        "IFCPIPESEGMENTTYPE", "IFCDUCTSEGMENTTYPE", "IFCCABLECARRIERSEGMENTTYPE", "IFCCABLESEGMENTTYPE",
    };

    private static readonly HashSet<string> UnclassifiedProductClassesCurrent = new(StringComparer.Ordinal)
    {
        "IFCSPACE", "IFCOPENINGELEMENT", "IFCSITE", "IFCBUILDING", "IFCBUILDINGSTOREY", "IFCGRID", "IFCPROJECT",
        "IFCZONE",
    };

    internal static string? AssemblyCodeCurrent(Element element, IReadOnlyDictionary<int, Element> elementsById)
    {
        if (NonEmptyStringParameter(element, AssemblyCodeParamId) is { } own) return own;
        return elementsById.TryGetValue(ResolveTypeObjectElementId(element, elementsById, currentProfile: true), out Element? type)
            && type.Id != element.Id ? NonEmptyStringParameter(type, AssemblyCodeParamId) : null;
    }

    internal static string AnsiStepStringCurrent(string text)
    {
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(1251)
            ?? throw new InvalidOperationException("The Windows-1251 code page is not available.");
        var sb = new StringBuilder("'");
        foreach (byte b in encoding.GetBytes(text))
        {
            if (b is < 0x20 or > 0x7E) sb.Append("\\X\\").Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else if (b == '\\') sb.Append("\\\\");
            else if (b == '\'') sb.Append("''");
            else sb.Append((char)b);
        }
        return sb.Append('\'').ToString();
    }

    internal static int ApplyCurrentClassifications(StepWriter w, int ownerHistId,
        IReadOnlyDictionary<int, Element> elementsById, IReadOnlyDictionary<int, int> productElementIdByEntity)
    {
        if (productElementIdByEntity.Count == 0) return 0;

        var classByEntity = new Dictionary<int, string>(w.EntityCount);
        var parentOf = new Dictionary<int, int>();
        var typeRelations = new List<string[]>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            classByEntity[id] = type;
            if (type is not ("IFCRELAGGREGATES" or "IFCRELDEFINESBYTYPE")) continue;
            string[] relation = SplitStepArguments(args);
            if (relation.Length != 6) continue;
            if (type == "IFCRELDEFINESBYTYPE") typeRelations.Add(relation);
            else if (TryEntityId(relation[4], out int relating) && relation[5].Length > 2 && relation[5][0] == '(')
                foreach (string part in SplitStepArguments(relation[5][1..^1]))
                    if (TryEntityId(part, out int partId)) parentOf.TryAdd(partId, relating);
        }

        var membersByCode = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var codeOrder = new List<string>();
        void Associate(int entity, string code)
        {
            if (!membersByCode.TryGetValue(code, out var members))
            {
                membersByCode[code] = members = [];
                codeOrder.Add(code);
            }
            members.Add(entity);
        }

        foreach (var (entity, elementId) in productElementIdByEntity.OrderBy(pair => pair.Key))
        {
            if (!classByEntity.TryGetValue(entity, out string? productClass)
                || UnclassifiedProductClassesCurrent.Contains(productClass)
                || !elementsById.TryGetValue(elementId, out Element? element))
                continue;
            if (AssemblyCodeCurrent(element, elementsById) is { } code) Associate(entity, code);
            int owner = entity;
            for (int depth = 0; depth < 8 && parentOf.TryGetValue(owner, out int parent); depth++) owner = parent;
            // A railing is exported from its own element and only aggregated into its stair or ramp afterwards: it is not a part inside the
            // product wrapper, so it does not take the code of the stair or ramp. The runs and landings of the stair keep it.
            bool stairRailing = productClass == "IFCRAILING" && classByEntity.TryGetValue(owner, out string? ownerClass) && ownerClass is "IFCSTAIR" or "IFCRAMP";
            if (owner != entity && !stairRailing && productElementIdByEntity.TryGetValue(owner, out int ownerElementId)
                && elementsById.TryGetValue(ownerElementId, out Element? ownerElement)
                && AssemblyCodeCurrent(ownerElement, elementsById) is { } ownerCode)
                Associate(entity, ownerCode);
        }

        foreach (string[] relation in typeRelations)
        {
            if (!TryEntityId(relation[5], out int typeEntity) || !classByEntity.TryGetValue(typeEntity, out string? typeClass)
                || UnclassifiedTypeClassesCurrent.Contains(typeClass)
                || relation[4].Length < 3 || relation[4][0] != '(') continue;
            var related = SplitStepArguments(relation[4][1..^1]);
            if (related.Length == 0 || !TryEntityId(related[0], out int firstProduct)
                || !productElementIdByEntity.TryGetValue(firstProduct, out int productElementId)
                || !elementsById.TryGetValue(productElementId, out Element? product)
                || !elementsById.TryGetValue(ResolveTypeObjectElementId(product, elementsById, currentProfile: true), out Element? typeElement)
                || NonEmptyStringParameter(typeElement, AssemblyCodeParamId) is not { } typeCode)
                continue;
            Associate(typeEntity, typeCode);
        }

        w.Add("IFCCLASSIFICATION", $"{S(UniformatUrlCurrent)},{S("1998")},$,{S("Uniformat")}");
        foreach (string code in codeOrder)
        {
            int reference = w.Add("IFCCLASSIFICATIONREFERENCE",
                $"{S(UniformatUrlCurrent)},{AnsiStepStringCurrent(code)},{S("Uniformat")},$");
            w.Add("IFCRELASSOCIATESCLASSIFICATION",
                $"{S(IfcGuid.FromName("classification:" + code))},{R(ownerHistId)},{S("Uniformat:" + code)},'',"
                + $"{StepWriter.List(membersByCode[code].Select(R))},{R(reference)}");
        }
        return codeOrder.Count;
    }
}
