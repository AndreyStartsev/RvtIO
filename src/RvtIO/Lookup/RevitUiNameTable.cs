using System.Globalization;
using System.Text;

namespace RvtIO.Lookup;

/// <summary>The user interface names the reader needs, embedded as Resources/revit_ui_names.tsv (UTF-8, tab separated:
/// culture, string group, string id, text; backslash, tab, line feed and carriage return are written as \\, \t, \n and \r).
/// It holds exactly the strings that <see cref="RevitSystemFamilyNames"/> can request, for the bundle version in
/// <see cref="BundleVersion"/>. A malformed line fails loudly instead of yielding a partial table.</summary>
internal static class RevitUiNameTable
{
    /// <summary>The Revit version the embedded strings belong to; files of older versions get no names, as before.</summary>
    internal const int BundleVersion = 2022;

    private const string ResourceName = "RvtIO.Resources.revit_ui_names.tsv";

    private static readonly Lazy<IReadOnlyDictionary<(string Culture, string File), IReadOnlyDictionary<int, string>>> Tables =
        new(() => Parse(OpenResource()));

    /// <summary>The strings of one string group in one culture, or null when the table has none for it.</summary>
    internal static IReadOnlyDictionary<int, string>? Get(string culture, string file) =>
        Tables.Value.GetValueOrDefault((culture, file));

    private static StreamReader OpenResource() =>
        new(typeof(RevitUiNameTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing resource {ResourceName}."), Encoding.UTF8);

    internal static IReadOnlyDictionary<(string Culture, string File), IReadOnlyDictionary<int, string>> Parse(TextReader reader)
    {
        var tables = new Dictionary<(string, string), Dictionary<int, string>>();
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 4 || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                throw new InvalidDataException($"Malformed line in revit_ui_names.tsv: {line}");
            if (!tables.TryGetValue((fields[0], fields[1]), out var table))
                tables[(fields[0], fields[1])] = table = new Dictionary<int, string>();
            if (!table.TryAdd(id, Unescape(fields[3], line)))
                throw new InvalidDataException($"Duplicate id in revit_ui_names.tsv: {line}");
        }
        return tables.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<int, string>)pair.Value);
    }

    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

    private static string Unescape(string text, string line)
    {
        if (!text.Contains('\\')) return text;
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (++i == text.Length) throw new InvalidDataException($"Malformed line in revit_ui_names.tsv: {line}");
            result.Append(text[i] switch
            {
                '\\' => '\\', 't' => '\t', 'n' => '\n', 'r' => '\r',
                _ => throw new InvalidDataException($"Malformed escape in revit_ui_names.tsv: {line}"),
            });
        }
        return result.ToString();
    }
}
