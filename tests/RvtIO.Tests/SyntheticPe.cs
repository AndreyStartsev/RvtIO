using System.Text;
using RvtIO.Lookup;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Builds a minimal PE image whose only content is an RT_STRING table for one language.</summary>
internal static class SyntheticPe
{
    private const int HeaderSize = 0x400;
    private const int SectionRva = 0x1000;

    public static byte[] Build(ushort language, IReadOnlyDictionary<int, string> strings)
    {
        var blocks = strings.GroupBy(pair => pair.Key / 16 + 1).OrderBy(group => group.Key).ToList();
        int typeDirectory = 24;
        int blockDirectory = typeDirectory + 16 + 8 * blocks.Count;
        int dataEntries = blockDirectory + 24 * blocks.Count;
        int stringData = dataEntries + 16 * blocks.Count;
        var section = new MemoryStream();
        var writer = new BinaryWriter(section);
        void WriteDirectory(int entries) { writer.Write(0); writer.Write(0); writer.Write(0); writer.Write((ushort)0); writer.Write((ushort)entries); }
        WriteDirectory(1);
        writer.Write(6u); writer.Write(0x80000000u | (uint)typeDirectory);
        WriteDirectory(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) { writer.Write((uint)blocks[i].Key); writer.Write(0x80000000u | (uint)(blockDirectory + 24 * i)); }
        for (int i = 0; i < blocks.Count; i++)
        {
            WriteDirectory(1);
            writer.Write((uint)language); writer.Write((uint)(dataEntries + 16 * i));
        }
        var payloads = blocks.Select(block =>
        {
            var bytes = new MemoryStream();
            for (int slot = 0; slot < 16; slot++)
            {
                string text = strings.GetValueOrDefault((block.Key - 1) * 16 + slot) ?? string.Empty;
                bytes.Write(BitConverter.GetBytes((ushort)text.Length));
                bytes.Write(Encoding.Unicode.GetBytes(text));
            }
            return bytes.ToArray();
        }).ToList();
        int position = stringData;
        foreach (byte[] payload in payloads)
        {
            writer.Write((uint)(SectionRva + position)); writer.Write((uint)payload.Length); writer.Write(0u); writer.Write(0u);
            position += payload.Length;
        }
        foreach (byte[] payload in payloads) writer.Write(payload);
        byte[] resources = section.ToArray();

        var image = new byte[HeaderSize + resources.Length];
        BitConverter.GetBytes((ushort)0x5A4D).CopyTo(image, 0);
        BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
        BitConverter.GetBytes(0x00004550).CopyTo(image, 0x80);
        int coff = 0x84, optional = coff + 20, optionalSize = 240;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(image, coff);
        BitConverter.GetBytes((ushort)1).CopyTo(image, coff + 2);
        BitConverter.GetBytes((ushort)optionalSize).CopyTo(image, coff + 16);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(image, optional);
        BitConverter.GetBytes((uint)SectionRva).CopyTo(image, optional + 112 + 8 * 2);
        BitConverter.GetBytes((uint)resources.Length).CopyTo(image, optional + 112 + 8 * 2 + 4);
        int sectionHeader = optional + optionalSize;
        BitConverter.GetBytes((uint)resources.Length).CopyTo(image, sectionHeader + 8);
        BitConverter.GetBytes((uint)SectionRva).CopyTo(image, sectionHeader + 12);
        BitConverter.GetBytes((uint)resources.Length).CopyTo(image, sectionHeader + 16);
        BitConverter.GetBytes((uint)HeaderSize).CopyTo(image, sectionHeader + 20);
        resources.CopyTo(image, HeaderSize);
        return image;
    }
}

/// <summary>A temporary resource root laid out like a Revit installation (&lt;root&gt;/2022/&lt;culture&gt;/*.dll), built from the
/// embedded names, so that the override path can be tested without the original DLLs.</summary>
internal sealed class SyntheticRevitRoot : IDisposable
{
    private static readonly (string Culture, ushort Language)[] Cultures = [("en-US", 1033), ("ru-RU", 1049)];

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rvtio-resources-" + Guid.NewGuid().ToString("N"));

    /// <param name="omitDlls">resource files left out of every culture</param>
    /// <param name="blank">(file, id) pairs whose text is replaced by a single space</param>
    public SyntheticRevitRoot(IEnumerable<string>? omitDlls = null, IEnumerable<(string Dll, int Id)>? blank = null)
    {
        var omit = omitDlls?.ToHashSet(StringComparer.Ordinal) ?? [];
        var blanked = blank?.ToHashSet() ?? [];
        foreach (var (culture, language) in Cultures)
        {
            string directory = System.IO.Path.Combine(Path, "2022", culture);
            Directory.CreateDirectory(directory);
            foreach (string dll in RevitSystemFamilyNames.RequestableStrings.Select(pair => pair.Dll).Distinct())
            {
                var table = RevitUiNameTable.Get(culture, dll);
                if (omit.Contains(dll) || table == null) continue;
                var edited = table.ToDictionary(pair => pair.Key, pair => blanked.Contains((dll, pair.Key)) ? " " : pair.Value);
                File.WriteAllBytes(System.IO.Path.Combine(directory, dll), SyntheticPe.Build(language, edited));
            }
        }
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
