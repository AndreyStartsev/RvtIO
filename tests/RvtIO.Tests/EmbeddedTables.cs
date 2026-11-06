using Xunit;

namespace RvtIO.Tests;

/// <summary>Rows of the tab separated tables embedded in the library, read from the assembly itself; comment (#) and blank lines are skipped.</summary>
internal static class EmbeddedTables
{
    public static string Text(string name)
    {
        using var stream = typeof(RvtIO.Lookup.RevitUiNameTable).Assembly.GetManifestResourceStream("RvtIO.Resources." + name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static List<string[]> Rows(string name, int columns)
    {
        var rows = new List<string[]>();
        foreach (string line in Text(name).Split('\n'))
        {
            string row = line.TrimEnd('\r');
            if (row.Length == 0 || row[0] == '#') continue;
            string[] fields = row.Split('\t');
            Assert.Equal(columns, fields.Length);
            rows.Add(fields);
        }
        return rows;
    }
}
