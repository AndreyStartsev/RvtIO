using System.Text;

namespace RvtIO.Lookup;

/// <summary>The user interface names the reader needs, embedded as Resources/revit_ui_names.tsv (UTF-8, tab separated:
/// culture, key, text; backslash, tab, line feed and carriage return are written as \\, \t, \n and \r).
/// It holds exactly the strings that <see cref="RevitSystemFamilyNames"/> can request, for the bundle version in
/// <see cref="BundleVersion"/>. A malformed line fails loudly instead of yielding a partial table.</summary>
internal static class RevitUiNameTable
{
    /// <summary>The Revit version the embedded strings belong to; files of older versions get no names, as before.</summary>
    internal const int BundleVersion = 2022;

    private const string ResourceName = "RvtIO.Resources.revit_ui_names.tsv";

    private static readonly Lazy<IReadOnlyDictionary<(string Culture, string Key), string>> Tables =
        new(() => Parse(OpenResource()));

    /// <summary>The text of one key in one culture, or null when the table has none for it.</summary>
    internal static string? Get(string culture, string key) =>
        Tables.Value.GetValueOrDefault((culture, key));

    private static StreamReader OpenResource() =>
        new(typeof(RevitUiNameTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing resource {ResourceName}."), Encoding.UTF8);

    internal static IReadOnlyDictionary<(string Culture, string Key), string> Parse(TextReader reader)
    {
        var table = new Dictionary<(string, string), string>();
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] fields = line.Split('\t');
            if (fields.Length != 3 || fields[0].Length == 0 || fields[1].Length == 0)
                throw new InvalidDataException($"Malformed line in revit_ui_names.tsv: {line}");
            if (!table.TryAdd((fields[0], fields[1]), Unescape(fields[2], line)))
                throw new InvalidDataException($"Duplicate key in revit_ui_names.tsv: {line}");
        }
        return table;
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
