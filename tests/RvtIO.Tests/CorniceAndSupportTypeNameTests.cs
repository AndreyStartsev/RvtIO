using System.Reflection;
using System.Text;
using System.Text.Json;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class CorniceAndSupportTypeNameTests
{
    // Own root of a sample project. The name is a fixture, never the family selector;
    // topside and underside have different values.
    private const string Support = "TT8AAKlGJYi8AAAAmA4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAABNPwAA/////////////////////////////////////wAAAP////9BD///////////xo1xY9wY1z/TvrQv7UvrP+Dv9/v9fu8/M8QMMUPM0D//////YCUAAAAAAAABAAAAABgAAABBAEQAUwBLAF8AIgQ1BEIEOAQyBDAEXwAoBDgEQAQ4BD0EMARfADgAMAAgADwEPAS8AAAA";

    [Theory]
    [InlineData(1, "Carriage")] [InlineData(0, "Stringer")]
    [InlineData(-1, "Stringer")] [InlineData(2, "Stringer")]
    [InlineData(int.MinValue, "Stringer")] [InlineData(int.MaxValue, "Stringer")]
    public void FamilySelectorPreservesOriginalIntComparison(int topside, string expected)
    {
        byte[] block = Convert.FromBase64String(Support);
        BitConverter.GetBytes(topside).CopyTo(block, FieldOffset(block, "m_topsideSurfaceType"));
        Assert.True(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, Size(block), 3736, out int? value));
        Assert.Equal(topside, value);
        Assert.Equal(expected, RevitSystemFamilyNames.KindOf(3736, value, -2000123)?.ToString());
    }

    [Fact]
    public void OwnSupportUsesTopsideNotUnderside_AndReadsOwnSymbolInfo()
    {
        byte[] block = Convert.FromBase64String(Support);
        Assert.Equal(0, BitConverter.ToInt32(block, FieldOffset(block, "m_topsideSurfaceType")));
        Assert.Equal(1, BitConverter.ToInt32(block, FieldOffset(block, "m_undersideSurfaceType")));
        Assert.Equal("ADSK_Тетива_Ширина_80 мм", TfBlockDeserializer.ExtractStairComponentTypeName(block, 0, Size(block), 3736));
        var state = new ParsingState();
        RvtFileReader.RecordSystemTypeRoot(state, block, 0, Size(block), 3736, 16205);
        Assert.Equal((1, (ushort)3736, (int?)0), state.SystemTypeRoots[16205]);
        Assert.Equal("ADSK_Тетива_Ширина_80 мм", state.StairComponentTypeNames[16205].Name);
    }

    [Theory]
    [InlineData("header")] [InlineData("class")] [InlineData("size")]
    [InlineData("footer")] [InlineData("truncated")]
    public void MalformedOwnSupportCannotProvideNameOrDiscriminator(string mutation)
    {
        byte[] block = Convert.FromBase64String(Support); uint size = Size(block);
        switch (mutation)
        {
            case "header": block[0] ^= 1; break;
            case "class": block[12] ^= 1; break;
            case "size": block[8] ^= 1; break;
            case "footer": block[^1] ^= 1; break;
            case "truncated": Array.Resize(ref block, block.Length - 1); break;
        }
        Assert.False(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, size, 3736, out _));
        Assert.Null(TfBlockDeserializer.ExtractStairComponentTypeName(block, 0, size, 3736));
    }

    [Fact]
    public void ContFootingTypeIsTheWallFoundationFamily()
    {
        Assert.Equal(SystemFamilyKind.WallFoundation, RevitSystemFamilyNames.KindOf(813, null, -2001300));
        Assert.Null(RevitSystemFamilyNames.KindOf(813, null, -2000032));
    }

    [Theory]
    [InlineData(3736, null, -2000123)] [InlineData(3736, 1, -2000919)]
    [InlineData(3727, 1, -2000123)] [InlineData(3736, 1, null)]
    public void UnknownDiscriminatorClassOrCategoryCannotSelectFamily(ushort cls, int? discriminator, int? category) =>
        Assert.Null(RevitSystemFamilyNames.KindOf(cls, discriminator, category));

    [Theory]
    [InlineData("RUS", "ru-RU")] [InlineData("ENU", "en-US")]
    public void NamesAreBuiltInTextsInSavedLocale(string locale, string culture)
    {
        var names = RevitSystemFamilyNames.Load("2022", locale)!;
        Assert.Equal(RevitUiNameTable.Get(culture, "Stringer"), names.Get(SystemFamilyKind.Stringer));
        Assert.Equal(RevitUiNameTable.Get(culture, "Carriage"), names.Get(SystemFamilyKind.Carriage));
        Assert.NotEqual(names.Get(SystemFamilyKind.Stringer), names.Get(SystemFamilyKind.Carriage));
    }

    [Theory]
    [InlineData(873)] [InlineData(3736)]
    public void ActualTypeWriterUsesOwnNameAndResourceFamily_PreservesOtherEntities(ushort cls)
    {
        var (elements, state) = Scene(cls);
        string before = JsonSerializer.Serialize(elements);
        var legacy = Write(elements, cls);
        Apply(elements, state);
        Assert.Equal(before, JsonSerializer.Serialize(elements));
        var actual = Write(elements, cls);
        Assert.Contains("'Native family:Own type'", TypeArgs(actual));
        Assert.Equal(legacy.Select(x => (x.Id, x.Type)), actual.Select(x => (x.Id, x.Type)));
        for (int i = 0; i < actual.Length; i++)
            Assert.Equal(legacy[i].Type.EndsWith("TYPE", StringComparison.Ordinal)
                ? legacy[i].Args.Replace("'Legacy family:Legacy type'", "'Native family:Own type'") : legacy[i].Args, actual[i].Args);
        Apply(elements, new ParsingState());
        Assert.Equal(legacy, Write(elements, cls));
    }

    [Theory]
    [InlineData(873)] [InlineData(3736)]
    public void PreviousOwnTableNameRetainsPriority(ushort cls)
    {
        var (elements, state) = Scene(cls); Apply(elements, state);
        elements[1].ValidatedIfcTypeObjectName = "Table family:Table name";
        Assert.Contains("'Table family:Table name'", TypeArgs(Write(elements, cls)));
    }

    [Theory]
    [InlineData(0, "Native Stringer")] [InlineData(1, "Native Carriage")]
    public void ExistingUniqueTypeLinkUsesOriginalFamilyForSupportObjectType(int topside, string family)
    {
        var (elements, state) = Scene(3736); elements[0].Name = "Own type";
        state.SystemTypeRoots[500] = (1, 3736, topside);
        string before = JsonSerializer.Serialize(elements);
        ElementBuilder.ApplyValidatedSystemFamilyNames(elements, state, RevitSystemFamilyNames.ForTests(
            new Dictionary<SystemFamilyKind, string> { [SystemFamilyKind.Stringer] = "Native Stringer", [SystemFamilyKind.Carriage] = "Native Carriage" }));
        Assert.Equal(family + ":Own type", IfcExporter.ResolveProductFamilyAndTypeName(elements[0], "IFCMEMBER",
            elements.ToDictionary(x => x.Id), elements.ToDictionary(x => x.Id, _ => 1)));
        Assert.Equal(before, JsonSerializer.Serialize(elements));
    }

    [Theory]
    [InlineData("category")] [InlineData("duplicate_root")] [InlineData("duplicate_type")]
    public void UnresolvedSupportFamilyCannotChangeOccurrenceObjectType(string mutation)
    {
        var (elements, state) = Scene(3736); elements[0].Name = "Own type"; elements[0].FamilyName = "Legacy occurrence family";
        if (mutation == "category") elements[1].CategoryId = -2000919;
        if (mutation == "duplicate_root") state.SystemTypeRoots[500] = (2, 0, null);
        if (mutation == "duplicate_type") elements.Add(new Element { Id = 500, CategoryId = -2000123 });
        ElementBuilder.ApplyValidatedSystemFamilyNames(elements, state, RevitSystemFamilyNames.ForTests(
            new Dictionary<SystemFamilyKind, string> { [SystemFamilyKind.Stringer] = "Native Stringer" }));
        Assert.Equal("Legacy occurrence family:Own type", IfcExporter.ResolveProductFamilyAndTypeName(elements[0], "IFCMEMBER",
            elements.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First()),
            elements.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.Count())));
    }

    [Theory]
    [InlineData("missing_name")] [InlineData("blank_name")] [InlineData("missing_root")]
    [InlineData("duplicate_root")] [InlineData("duplicate_name")] [InlineData("wrong_class")]
    [InlineData("wrong_category")] [InlineData("duplicate_element")] [InlineData("block_only")]
    [InlineData("missing_family")] [InlineData("invalid_id")]
    public void AmbiguousOrAbsentValidationCannotBorrowNamesAndClearsStaleSource(string mutation)
    {
        foreach (ushort cls in new ushort[] { 873, 3736 })
        {
            var (elements, state) = Scene(cls); var type = elements[1];
            switch (mutation)
            {
                case "missing_name": state.CorniceTypeNames.Clear(); state.StairComponentTypeNames.Clear(); break;
                case "blank_name": state.CorniceTypeNames[500] = (1, " \t"); state.StairComponentTypeNames[500] = (1, cls, " \t"); break;
                case "missing_root": state.SystemTypeRoots.Clear(); break;
                case "duplicate_root": state.SystemTypeRoots[500] = (2, 0, null); break;
                case "duplicate_name": state.CorniceTypeNames[500] = (2, null); state.StairComponentTypeNames[500] = (2, 0, null); break;
                case "wrong_class": state.SystemTypeRoots[500] = (1, 1829, null); break;
                case "wrong_category": type.CategoryId = -2000919; break;
                case "duplicate_element": elements.Add(new Element { Id = 500, CategoryId = type.CategoryId }); break;
                case "block_only": type.IsBlockOnly = true; break;
                case "missing_family": type.ValidatedSystemFamilyName = null; break;
                case "invalid_id": type.Id = 0; break;
            }
            type.ValidatedCorniceTypeName = "stale"; type.ValidatedStairComponentTypeName = "stale";
            string before = JsonSerializer.Serialize(elements);
            Apply(elements, state);
            Assert.Equal(before, JsonSerializer.Serialize(elements));
            if (mutation != "missing_family")
            {
                Assert.Null(type.ValidatedCorniceTypeName); Assert.Null(type.ValidatedStairComponentTypeName);
            }
            Assert.Equal("Legacy type", type.Name);
            if (mutation != "invalid_id") Assert.Contains("'Legacy family:Legacy type'", TypeArgs(Write(elements, cls)));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OtherSymbolRootBlocksSupportInEitherOrder(bool otherFirst)
    {
        byte[] block = Convert.FromBase64String(Support); uint id = BitConverter.ToUInt32(block, 0);
        byte[] other = new byte[64]; BitConverter.GetBytes(id).CopyTo(other, 0);
        var state = new ParsingState();
        if (otherFirst) RvtFileReader.RecordSystemTypeRoot(state, other, 0, 40, 1829, id);
        RvtFileReader.RecordSystemTypeRoot(state, block, 0, Size(block), 3736, id);
        if (!otherFirst) RvtFileReader.RecordSystemTypeRoot(state, other, 0, 40, 1829, id);
        var type = new Element { Id = (int)id, CategoryId = -2000123 };
        ElementBuilder.ApplyValidatedStairComponentTypeNames([type], state);
        Assert.Null(type.ValidatedStairComponentTypeName);
    }

    private static void Apply(List<Element> elements, ParsingState state)
    {
        ElementBuilder.ApplyValidatedCorniceTypeNames(elements, state);
        ElementBuilder.ApplyValidatedStairComponentTypeNames(elements, state);
    }

    private static (List<Element>, ParsingState) Scene(ushort cls)
    {
        int category = cls == 873 ? -2000181 : -2000123;
        var elements = new List<Element>
        {
            new() { Id = 100, TypeId = 500, CategoryId = category,
                Parameters = [new Parameter { Id = -1002050, Value = 500, StorageType = StorageType.ElementId,
                    Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } }] },
            new() { Id = 500, TypeId = -1, CategoryId = category, Name = "Legacy type", FamilyName = "Legacy family",
                ValidatedSystemFamilyName = "Native family" },
        };
        var state = new ParsingState();
        state.SystemTypeRoots[500] = (1, cls, 0);
        state.CorniceTypeNames[500] = (1, "Own type");
        state.StairComponentTypeNames[500] = (1, cls, "Own type");
        return (elements, state);
    }

    private static (int Id, string Type, string Args)[] Write(List<Element> elements, ushort cls)
    {
        var writer = new StepWriter();
        typeof(IfcExporter).GetMethod("WriteTypeObjectsAndRelDefinesByType", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [writer, 900, elements.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First()),
                new Dictionary<int, (int, int, string)> { [100] = (1100, 0, cls == 873 ? "IFCBUILDINGELEMENTPROXY" : "IFCMEMBER") },
                new Dictionary<(string, string), int>(), new Dictionary<int, bool?>()]);
        return Enumerable.Range(0, writer.EntityCount).Select(writer.EntityAt).ToArray();
    }

    private static string TypeArgs((int Id, string Type, string Args)[] rows) =>
        Assert.Single(rows, row => row.Type.EndsWith("TYPE", StringComparison.Ordinal) && row.Type != "IFCRELDEFINESBYTYPE").Args;

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    private static int FieldOffset(byte[] block, string name)
    {
        Type type = typeof(TfBlockDeserializer), cursorType = type.GetNestedType("Cursor", BindingFlags.NonPublic)!;
        object cursor = Activator.CreateInstance(cursorType)!;
        cursorType.GetField("D")!.SetValue(cursor, block); cursorType.GetField("Pos")!.SetValue(cursor, 14);
        cursorType.GetField("End")!.SetValue(cursor, block.Length - 4); cursorType.GetField("RootClassId")!.SetValue(cursor, (ushort)3736);
        var queue = new List<(ushort, uint)>();
        foreach (var field in TfBlockDeserializer.SerializationFields(3736)!)
        {
            if (field.Declaring == 3736 && field.Name == name) return (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
            type.GetMethod("ReadField", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [cursor, field, queue, 0, null]);
        }
        throw new InvalidOperationException(name);
    }
}
