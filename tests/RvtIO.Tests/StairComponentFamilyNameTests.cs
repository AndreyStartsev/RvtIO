using System.Reflection;
using System.Text;
using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class StairComponentFamilyNameTests
{
    // Own R22 type roots, captured by RootProbe raw-roots from Test_2022. The
    // English type name is not the family discriminator and is never used by decode.
    private const string MonolithicRun = "mJ4CAOJPVNjBAAAAhg4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAACYngIA/////////////////////////////////////wAAAP////9BD////////////Sg/yo/y4z8AAAAAAAAAAED1T/1T/8Q/QPVP/VP/pD8AAAAAAAAAAAsCAAD//////////////////////////wEAAAAAAAAAAAAAAAEAAAALAAAAMQA5ADAAbQBtACAARABlAHAAdABoAMEAAAA=";
    private const string NonMonolithicRun = "oikCAJVRqifVAAAAhg4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAACiKQIA/////////////////////////////////////wAAAP////9BD///////////+af+qX/q3z8AAAAAAAAAAED1T/1T/8Q/QPVP/VP/xD8AAAAAAAAAAP////////////////////+7HAQAuxwEAAEAAAAAAAAAAAAAAAABAQAVAAAANQAwAG0AbQAgAFQAcgBlAGEAZAAgADEAMwBtAG0AIABSAGkAcwBlAHIA1QAAAA==";
    private const string MonolithicLanding = "l54CAGxGozmoAAAAcQ4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAACXngIA/////////////////////////////////////wAAAP////9BD///////////4O/3+/1+7z9A9U/9U//EPwAAAAAAAAAACwIAAP///////////////wAAAAABAQAPAAAAMgAyADAAbQBtACAAVABoAGkAYwBrAG4AZQBzAHMAqAAAAA==";
    private const string NonMonolithicLanding = "oykCABxMJ722AAAAcQ4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAACjKQIA/////////////////////////////////////wAAAP////9BD///////////eJH2k+bW7z9A9U/9U//EPwAAAAAAAAAA/////////////////////wAAAAAAAQEWAAAATgBvAG4ALQBNAG8AbgBvAGwAaQB0AGgAaQBjACAATABhAG4AZABpAG4AZwC2AAAA";

    [Theory]
    [InlineData(3718, 1, -2000919, "MonolithicRun")]
    [InlineData(3718, 0, -2000919, "NonMonolithicRun")]
    [InlineData(3697, 1, -2000920, "MonolithicLanding")]
    [InlineData(3697, 0, -2000920, "NonMonolithicLanding")]
    public void KindComesFromOwnMonolithicFlag(int cls, int flag, int category, string kind) =>
        Assert.Equal(kind, RevitSystemFamilyNames.KindOf((ushort)cls, flag, category)?.ToString());

    [Theory]
    [InlineData(3718, null, -2000919)] [InlineData(3718, 2, -2000919)]
    [InlineData(3718, -1, -2000919)] [InlineData(3718, 1, -2000920)]
    [InlineData(3697, null, -2000920)] [InlineData(3697, 2, -2000920)]
    [InlineData(3697, -1, -2000920)] [InlineData(3697, 1, -2000919)]
    [InlineData(3708, 1, -2000919)] [InlineData(3686, 1, -2000920)]
    public void UnknownFlagOrWrongClassCategoryHasNoFamily(int cls, int? flag, int category) =>
        Assert.Null(RevitSystemFamilyNames.KindOf((ushort)cls, flag, category));

    [Theory]
    [InlineData(MonolithicRun, 3718, 1)] [InlineData(NonMonolithicRun, 3718, 0)]
    [InlineData(MonolithicLanding, 3697, 1)] [InlineData(NonMonolithicLanding, 3697, 0)]
    public void CompleteOwnTypeRootYieldsTheExactByteFlag(string fixture, ushort cls, int expected)
    {
        byte[] block = Convert.FromBase64String(fixture);
        Assert.True(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, Size(block), cls, out int? flag));
        Assert.Equal(expected, flag);
    }

    [Theory]
    [InlineData("header")] [InlineData("class")] [InlineData("size")]
    [InlineData("footer")] [InlineData("truncated")]
    public void MalformedOwnRootCannotProvideFamily(string mutation)
    {
        foreach ((string fixture, ushort cls) in new[] { (MonolithicRun, (ushort)3718), (MonolithicLanding, (ushort)3697) })
        {
            byte[] block = Convert.FromBase64String(fixture); uint size = Size(block);
            switch (mutation)
            {
                case "header": block[0] ^= 1; break;
                case "class": block[12] ^= 1; break;
                case "size": block[8] ^= 1; break;
                case "footer": block[^1] ^= 1; break;
                case "truncated": Array.Resize(ref block, block.Length - 1); break;
            }
            Assert.False(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, size, cls, out _));
            Assert.Null(TfBlockDeserializer.ExtractStairComponentTypeName(block, 0, size, cls));
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(255)]
    public void NonBooleanFlagIsNotCoercedToTrue(byte invalid)
    {
        foreach ((string fixture, ushort cls) in new[] { (MonolithicRun, (ushort)3718), (MonolithicLanding, (ushort)3697) })
        {
            byte[] block = Convert.FromBase64String(fixture);
            block[FlagOffset(block, cls)] = invalid;
            Assert.False(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, Size(block), cls, out _));
        }
    }

    [Theory]
    [InlineData("MonolithicRun", "Monolithic Run", "Монолитный марш")]
    [InlineData("NonMonolithicRun", "Non-Monolithic Run", "Немонолитный марш")]
    [InlineData("MonolithicLanding", "Monolithic Landing", "Монолитная площадка")]
    [InlineData("NonMonolithicLanding", "Non-Monolithic Landing", "Немонолитная площадка")]
    public void FamilyUsesBuiltInNamesInTheSavedLanguage(string kind, string english, string russian)
    {
        Assert.Equal(russian, RevitSystemFamilyNames.Load("2022", "RUS")!.Get(Enum.Parse<SystemFamilyKind>(kind)));
        Assert.Equal(english, RevitSystemFamilyNames.Load("2022", "ENU")!.Get(Enum.Parse<SystemFamilyKind>(kind)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateOwnRootPermanentlyBlocksTheName(bool malformedFirst)
    {
        byte[] block = Convert.FromBase64String(MonolithicRun); uint id = BitConverter.ToUInt32(block, 0);
        var state = new ParsingState();
        if (malformedFirst)
        {
            byte[] bad = (byte[])block.Clone(); bad[^1] ^= 1;
            RvtFileReader.RecordSystemTypeRoot(state, bad, 0, Size(block), 3718, id);
        }
        else RvtFileReader.RecordSystemTypeRoot(state, block, 0, Size(block), 3718, id);
        RvtFileReader.RecordSystemTypeRoot(state, block, 0, Size(block), 3718, id);
        Assert.Equal((2, (ushort)0, (int?)null), state.SystemTypeRoots[id]);
        var type = new Element { Id = (int)id, CategoryId = -2000919, Name = "unrelated type name" };
        Assert.Equal(0, ElementBuilder.ApplyValidatedSystemFamilyNames([type], state,
            RevitSystemFamilyNames.Load("2022", "RUS")));
        Assert.Null(type.ValidatedSystemFamilyName);
    }

    [Theory]
    [InlineData(MonolithicRun, 3718, "190mm Depth")]
    [InlineData(NonMonolithicRun, 3718, "50mm Tread 13mm Riser")]
    [InlineData(MonolithicLanding, 3697, "220mm Thickness")]
    [InlineData(NonMonolithicLanding, 3697, "Non-Monolithic Landing")]
    public void ComponentTypeNameIsItsOwnSymbolInfo(string fixture, ushort cls, string expected)
    {
        byte[] block = Convert.FromBase64String(fixture);
        Assert.Equal(expected, TfBlockDeserializer.ExtractStairComponentTypeName(block, 0, Size(block), cls));
    }

    [Theory]
    [InlineData("ok")] [InlineData("no_name")] [InlineData("missing_root")]
    [InlineData("duplicate_root")] [InlineData("duplicate_name")] [InlineData("wrong_class")]
    [InlineData("wrong_category")] [InlineData("other_root")] [InlineData("blank_name")]
    [InlineData("duplicate_element")] [InlineData("block_only")]
    public void OwnTypeNameCannotBorrowPublicNamesOrRepairAmbiguity(string mutation)
    {
        var state = new ParsingState();
        if (mutation != "missing_root") state.SystemTypeRoots[500] =
            mutation == "duplicate_root" ? (2, (ushort)0, null)
            : mutation == "other_root" ? (1, (ushort)1829, null) : (1, (ushort)3718, 1);
        if (mutation != "no_name") state.StairComponentTypeNames[500] =
            mutation == "duplicate_name" ? (2, (ushort)0, null)
            : (1, mutation == "wrong_class" ? (ushort)3697 : (ushort)3718,
                mutation == "blank_name" ? " " : "Exact own type");
        var type = new Element
        {
            Id = 500, CategoryId = mutation == "wrong_category" ? -2000920 : -2000919,
            Name = "Borrowed level name", ValidatedStairComponentTypeName = "stale",
            IsBlockOnly = mutation == "block_only",
        };
        var elements = new List<Element> { type };
        if (mutation == "duplicate_element") elements.Add(new Element { Id = 500, CategoryId = -2000919 });
        string before = JsonSerializer.Serialize(type);
        ElementBuilder.ApplyValidatedStairComponentTypeNames(elements, state);
        Assert.Equal(mutation == "ok" ? "Exact own type" : null, type.ValidatedStairComponentTypeName);
        Assert.Equal(before, JsonSerializer.Serialize(type));
        Assert.Equal("Borrowed level name", type.Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DifferentOwnSymbolRootBlocksNameInEitherOrder(bool otherFirst)
    {
        byte[] block = Convert.FromBase64String(MonolithicRun); uint id = BitConverter.ToUInt32(block, 0);
        byte[] foreign = new byte[64]; BitConverter.GetBytes(id).CopyTo(foreign, 0);
        var state = new ParsingState();
        if (otherFirst) RvtFileReader.RecordSystemTypeRoot(state, foreign, 0, 40, 1829, id);
        RvtFileReader.RecordSystemTypeRoot(state, block, 0, Size(block), 3718, id);
        if (!otherFirst) RvtFileReader.RecordSystemTypeRoot(state, foreign, 0, 40, 1829, id);
        var type = new Element { Id = (int)id, CategoryId = -2000919 };
        ElementBuilder.ApplyValidatedStairComponentTypeNames([type], state);
        Assert.Null(type.ValidatedStairComponentTypeName);
    }

    // Obtain the mutation offset by the same exact root-schema walk, not a byte
    // scan or an assumed fixed offset. This is fixture mutation code only.
    private static int FlagOffset(byte[] block, ushort cls)
    {
        Type type = typeof(TfBlockDeserializer);
        Type cursorType = type.GetNestedType("Cursor", BindingFlags.NonPublic)!;
        object cursor = Activator.CreateInstance(cursorType)!;
        cursorType.GetField("D")!.SetValue(cursor, block);
        cursorType.GetField("Pos")!.SetValue(cursor, 14);
        cursorType.GetField("End")!.SetValue(cursor, block.Length - 4);
        cursorType.GetField("RootClassId")!.SetValue(cursor, cls);
        var queue = new List<(ushort, uint)>();
        var read = type.GetMethod("ReadField", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var field in TfBlockDeserializer.SerializationFields(cls)!)
        {
            if (field.Declaring == cls && field.Name == "m_hasMonolithicSupport")
                return (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
            read.Invoke(null, [cursor, field, queue, 0, null]);
        }
        throw new InvalidOperationException("No own monolithic flag in fixture schema");
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);
}
