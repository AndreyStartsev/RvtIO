using System.Text.Json;

namespace RvtIO.Parsing;

/// <summary>Public API Definition.Name and ParameterGroup of built-in parameters, observed in
/// exported documents (English, 2022 format) where no definition conflicts.
/// Negative IDs only: project and shared parameter IDs are file specific.</summary>
internal static class ApiParameterDefinitions
{
    private static readonly Lazy<Dictionary<int, (string Name, int GroupId, string DataType, string Kind)>> Table = new(Load);

    /// <summary>dataType is the versionless spec id (e.g. autodesk.spec.aec:length); empty when the
    /// definition has no data type (enumerations, element references).</summary>
    public static bool TryGet(int parameterId, out string name, out int groupId, out string dataType)
    {
        if (Table.Value.TryGetValue(parameterId, out var definition))
        {
            (name, groupId, dataType, _) = definition;
            return true;
        }
        name = "";
        groupId = -1;
        dataType = "";
        return false;
    }

    /// <summary>Public API storage type of a built-in parameter (the kind of its RawValue in the capture).</summary>
    public static bool TryGetStorage(int parameterId, out Model.StorageType storage)
    {
        storage = Model.StorageType.None;
        if (!Table.Value.TryGetValue(parameterId, out var definition)) return false;
        storage = definition.Kind switch
        {
            "Double" => Model.StorageType.Double,
            "Integer" => Model.StorageType.Integer,
            "ElementId" => Model.StorageType.ElementId,
            "String" => Model.StorageType.String,
            _ => Model.StorageType.None,
        };
        return storage != Model.StorageType.None;
    }

    private static Dictionary<int, (string, int, string, string)> Load()
    {
        using var stream = typeof(ApiParameterDefinitions).Assembly.GetManifestResourceStream(
            "RvtIO.Resources.api_parameter_definitions_2022.json")
            ?? throw new InvalidDataException("Missing API parameter definitions resource");
        using var json = JsonDocument.Parse(stream);
        var table = new Dictionary<int, (string, int, string, string)>();
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            var value = entry.Value;
            table.Add(int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture),
                (value[0].GetString() ?? throw new InvalidDataException("Null parameter name"), value[1].GetInt32(), value[2].GetString() ?? "", value[3].GetString() ?? ""));
        }
        return table;
    }
}
