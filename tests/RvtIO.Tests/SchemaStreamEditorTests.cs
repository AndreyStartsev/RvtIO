using System.Text;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The schema stream editor and the spans the strict schema parser records. The streams are built here by a small writer of the
/// Formats/Latest grammar that shares no code with the parser or the editor; it also records where it puts every version and list, so
/// the recorded spans are checked against the writer and not against the parser itself.</summary>
public class SchemaStreamEditorTests
{
    // ---- an independent writer of the grammar -------------------------------------------------------------------------------

    private abstract record Ref;
    private sealed record NoRef : Ref;
    private sealed record Back(int Id) : Ref;
    private sealed record Inline(Cls Class) : Ref;

    private sealed record Fld(string Name, byte Type, byte Mods = 0, ushort Extra = 0, uint? Fixed = null, Ref? Target = null, Fld[]? Args = null);

    private sealed record Cls(string Name, Ref Base, uint Version, Fld[] Fields, string[] Guids);

    private sealed record Placed(string Name, int Id, int VersionAt, int ListAt, int ListLength);

    private sealed class Writer(bool guidLists = true)
    {
        private readonly MemoryStream _s = new();
        private int _counter = 12;
        public List<Placed> Placed { get; } = [];

        private void U8(int v) => _s.WriteByte((byte)v);
        private void U16(int v) { U8(v & 0xFF); U8(v >> 8); }
        private void U32(uint v) { U16((int)(v & 0xFFFF)); U16((int)(v >> 16)); }
        private void Text(string t) => _s.Write(Encoding.ASCII.GetBytes(t));
        private void Name16(string n) { U16(n.Length); Text(n); }

        public Writer Top(Cls c)
        {
            U16(0);
            Name16(c.Name);
            Body(c, _counter++);
            return this;
        }

        public byte[] Build(int padding = 8) => [.. _s.ToArray(), .. new byte[padding]];

        private void TypeRef(Ref r)
        {
            switch (r)
            {
                case NoRef: U16(0); break;
                case Back b: U16(b.Id); break;
                case Inline i:
                    int id = _counter++;
                    U16(0x8000 | id); U16(0); Name16(i.Class.Name);
                    Body(i.Class, id);
                    break;
            }
        }

        private void Body(Cls c, int id)
        {
            TypeRef(c.Base);
            int versionAt = (int)_s.Position;
            U32(c.Version);
            U32((uint)c.Fields.Length);
            foreach (var f in c.Fields)
            {
                U32((uint)f.Name.Length); Text(f.Name);
                Core(f, inTemplate: false);
            }
            if (!guidLists) { Placed.Add(new Placed(c.Name, id, versionAt, -1, 0)); return; }
            int listAt = (int)_s.Position;
            U32((uint)c.Guids.Length);
            foreach (var g in c.Guids) _s.Write(Convert.FromHexString(g));
            Placed.Add(new Placed(c.Name, id, versionAt, listAt, (int)_s.Position - listAt));
        }

        private void Core(Fld f, bool inTemplate)
        {
            U8(f.Type); U8(f.Mods); U16(f.Extra);
            if (f.Mods >> 4 == 1) U32(f.Fixed!.Value);
            if (f.Type == 0x0E && (f.Mods & 0x0F) == 0)
            {
                TypeRef(f.Target!);
                if (inTemplate) TypeRef(f.Target!); // a template argument writes its reference twice (only back references here)
            }
            else if (f.Type == 0x0D)
            {
                U32((uint)f.Args!.Length);
                foreach (var a in f.Args) { U8(0x20); Core(a, inTemplate: true); }
            }
        }
    }

    private static string Guid(int n) => string.Concat(Enumerable.Range(0, 16).Select(i => ((n * 31 + i * 7) & 0xFF).ToString("x2")));

    private static string[] Guids(int from, int count) => [.. Enumerable.Range(from, count).Select(Guid)];

    private static Fld Int(string name) => new(name, 0x04);

    /// <summary>A schema with top-level classes, a base reference, an inline base, an inline field type, a back reference, a template
    /// argument, a fixed count and lists of 0, 1 and 3 entries. Ids: Alpha 12, Beta 13, Gamma 14, GammaBase 15, Delta 16, DeltaPart 17,
    /// Epsilon 18, Zeta 19.</summary>
    private static List<Cls> Model() =>
    [
        new("Alpha", new NoRef(), 3, [Int("m_a"), new("m_b", 0x07)], Guids(1, 3)),
        new("Beta", new Back(12), 1, [Int("m_c")], []),
        new("Gamma", new Inline(new Cls("GammaBase", new NoRef(), 5, [Int("m_g")], Guids(10, 1))), 2, [new("m_fixed", 0x04, 0x10, 0, 4)], Guids(20, 2)),
        new("Delta", new NoRef(), 7,
            [new("m_ref", 0x0E, 0, 0, null, new Inline(new Cls("DeltaPart", new Back(13), 4, [Int("m_p")], Guids(30, 2)))),
             new("m_again", 0x0E, 0, 0, null, new Back(12))], Guids(40, 1)),
        new("Epsilon", new Back(15), 9, [new("m_list", 0x0D, 0, 0, null, null, [new("", 0x0E, 0, 0, null, new Back(13))])], []),
        new("Zeta", new NoRef(), 0, [], Guids(50, 5)),
    ];

    private static (byte[] Data, List<Placed> Placed) Write(List<Cls> model, int padding = 8, bool guidLists = true)
    {
        var w = new Writer(guidLists);
        foreach (var c in model) w.Top(c);
        return (w.Build(padding), w.Placed);
    }

    // The model with one class (top-level or defined inside another one) changed.
    private static Cls Changed(Cls c, string name, uint? version, string[]? guids)
    {
        Ref Inner(Ref r) => r is Inline i ? new Inline(Changed(i.Class, name, version, guids)) : r;
        Fld Field(Fld f) => f with { Target = f.Target is null ? null : Inner(f.Target), Args = f.Args?.Select(Field).ToArray() };
        var self = c.Name == name ? c with { Version = version ?? c.Version, Guids = guids ?? c.Guids } : c;
        return self with { Base = Inner(self.Base), Fields = [.. self.Fields.Select(Field)] };
    }

    private static List<Cls> With(List<Cls> model, string name, uint? version = null, string[]? guids = null) =>
        [.. model.Select(c => Changed(c, name, version, guids))];

    // ---- spans ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ParserRecordsTheSpanOfEveryVersionAndEveryList_IncludingNestedDefinitions()
    {
        var (data, placed) = Write(Model());
        var document = TfSchemaReader.Parse(data);

        Assert.Equal(placed.Count, document.Classes.Count);
        Assert.Equal(8, document.Classes.Count);
        foreach (var p in placed)
        {
            var c = document.Classes.Single(x => x.Name == p.Name);
            Assert.Equal(p.Id, c.Id);
            Assert.Equal(new ClassSpans(new ByteSpan(p.VersionAt, 4), new ByteSpan(p.ListAt, p.ListLength)), c.Spans);
        }
    }

    [Fact]
    public void ParserRecordsNoListSpan_InTheOlderLayout()
    {
        var (data, placed) = Write(Model().Take(2).ToList(), guidLists: false);
        var document = TfSchemaReader.Parse(data);

        Assert.False(document.GuidLists);
        foreach (var p in placed)
            Assert.Equal(new ClassSpans(new ByteSpan(p.VersionAt, 4), null), document.Classes.Single(x => x.Name == p.Name).Spans);
    }

    [Fact]
    public void ClassesThatDoNotComeFromTheStream_HaveNoSpans()
    {
        var (data, _) = Write(Model());
        var document = TfSchemaReader.Parse(data);
        var roundTrip = TfSchemaReader.ReadJson(TfSchemaReader.WriteJson(document));

        Assert.All(document.Classes, c => Assert.NotNull(c.Spans));
        Assert.All(roundTrip.Classes, c => Assert.Null(c.Spans));
    }

    // ---- edits ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutEdits_TheResultIsACopyEqualToTheInput()
    {
        var (data, _) = Write(Model(), padding: 40);
        byte[] result = SchemaStreamEditor.Apply(data, []);

        Assert.Equal(data, result);
        Assert.NotSame(data, result);
    }

    [Fact]
    public void SetVersion_ChangesFourBytesOnly()
    {
        var (data, placed) = Write(Model());
        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Version: 2)]);

        Assert.Equal(data.Length, result.Length);
        var differing = Enumerable.Range(0, data.Length).Where(i => data[i] != result[i]).ToList();
        int at = placed.Single(p => p.Name == "Alpha").VersionAt;
        Assert.Equal(Enumerable.Range(at, 4).Where(i => data[i] != result[i]), differing); // 3 -> 2: one byte differs, inside the dword
        Assert.Equal(Write(With(Model(), "Alpha", version: 2)).Data, result);
        Assert.Equal(2, TfSchemaReader.Parse(result).Classes.Single(c => c.Name == "Alpha").Version);
    }

    [Theory]
    [InlineData("Alpha", 0)]       // lower to zero
    [InlineData("Alpha", 3)]       // the stored version
    [InlineData("GammaBase", 4)]   // a class defined inside another definition
    [InlineData("DeltaPart", 1)]
    public void SetVersion_LowersOrKeepsTheVersion(string name, int version)
    {
        var (data, _) = Write(Model());
        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit(name, Version: version)]);

        Assert.Equal(Write(With(Model(), name, version: (uint)version)).Data, result);
    }

    [Fact]
    public void SetVersion_NeverRaisesByDefault_AndRaisesWhenAllowed()
    {
        var (data, _) = Write(Model());
        var raise = new SchemaEdit("Alpha", Version: 4);

        var e = Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [raise]));
        Assert.Contains("higher", e.Message);
        byte[] result = SchemaStreamEditor.Apply(data, [raise], allowVersionRaise: true);
        Assert.Equal(Write(With(Model(), "Alpha", version: 4)).Data, result);
    }

    [Theory]
    [InlineData("Alpha", 2)]    // 3 entries -> 2
    [InlineData("Alpha", 0)]    // -> empty
    [InlineData("Alpha", 9)]    // -> longer
    [InlineData("Beta", 4)]     // empty -> 4 entries
    [InlineData("Zeta", 1)]     // the last definition, followed by the padding
    [InlineData("GammaBase", 3)]
    [InlineData("DeltaPart", 0)]
    public void ReplaceGuids_ShortensOrLengthensTheList_AndMovesWhatFollows(string name, int count)
    {
        var (data, placed) = Write(Model(), padding: 24);
        string[] list = Guids(200, count);
        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit(name, Guids: list)]);

        var (expected, _) = Write(With(Model(), name, guids: list), padding: 24);
        Assert.Equal(expected, result);
        int was = (placed.Single(p => p.Name == name).ListLength - 4) / 16;
        Assert.Equal(data.Length + 16 * (count - was), result.Length);
        Assert.Equal(new byte[24], result[^24..]); // the padding is kept
    }

    [Fact]
    public void ReplaceGuids_AcceptsUpperCaseDigits_AndReadsBackLowerCase()
    {
        var (data, _) = Write(Model());
        string upper = Guid(77).ToUpperInvariant();
        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit("Beta", Guids: [upper])]);

        Assert.Equal([Guid(77)], TfSchemaReader.Parse(result).Classes.Single(c => c.Name == "Beta").Guids);
    }

    [Fact]
    public void SeveralEdits_AreAppliedTogether_InAnyOrder()
    {
        var (data, _) = Write(Model());
        var edits = new[]
        {
            new SchemaEdit("Zeta", Guids: Guids(300, 1)),                  // shorter
            new SchemaEdit("Alpha", Version: 1, Guids: Guids(310, 6)),     // both, longer
            new SchemaEdit("DeltaPart", Version: 2),                       // inline field type
            new SchemaEdit("GammaBase", Guids: []),                        // inline base, emptied
            new SchemaEdit("Beta", Guids: Guids(320, 2)),                  // base reference
        };
        var expectedModel = With(With(With(With(With(Model(), "Zeta", guids: Guids(300, 1)), "Alpha", 1, Guids(310, 6)), "DeltaPart", 2), "GammaBase", guids: []), "Beta", guids: Guids(320, 2));

        byte[] forward = SchemaStreamEditor.Apply(data, edits);
        byte[] backward = SchemaStreamEditor.Apply(data, [.. edits.AsEnumerable().Reverse()]);

        Assert.Equal(Write(expectedModel).Data, forward);
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void ADefinitionThatKeepsItsInlineParts_IsRewrittenAsAWhole()
    {
        // Delta holds DeltaPart inside its field list; its own list comes after the inline definition, so it moves with it.
        var (data, _) = Write(Model());
        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit("Delta", Version: 6, Guids: Guids(400, 4)), new SchemaEdit("DeltaPart", Guids: Guids(410, 3))]);

        var after = TfSchemaReader.Parse(result);
        Assert.Equal(Guids(400, 4), after.Classes.Single(c => c.Name == "Delta").Guids);
        Assert.Equal(Guids(410, 3), after.Classes.Single(c => c.Name == "DeltaPart").Guids);
        Assert.Equal(6, after.Classes.Single(c => c.Name == "Delta").Version);
        Assert.Equal(Write(With(With(Model(), "Delta", 6, Guids(400, 4)), "DeltaPart", guids: Guids(410, 3))).Data, result);
    }

    [Fact]
    public void TheResult_ParsesToTheSameSchemaExceptTheEdits()
    {
        var (data, _) = Write(Model());
        var before = TfSchemaReader.Parse(data);
        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit("Epsilon", Version: 8, Guids: Guids(500, 2))]);
        var after = TfSchemaReader.Parse(result);

        Assert.Equal(before.Classes.Count, after.Classes.Count);
        for (int i = 0; i < before.Classes.Count; i++)
        {
            Assert.Equal((before.Classes[i].Id, before.Classes[i].Name, before.Classes[i].Base), (after.Classes[i].Id, after.Classes[i].Name, after.Classes[i].Base));
            Assert.Equal(before.Classes[i].Fields.Count, after.Classes[i].Fields.Count);
            if (before.Classes[i].Name != "Epsilon")
            {
                Assert.Equal(before.Classes[i].Version, after.Classes[i].Version);
                Assert.Equal(before.Classes[i].Guids, after.Classes[i].Guids);
            }
        }
        // The spans of the result are consistent with the bytes: editing it again at the new positions is an identity for the same values.
        Assert.Equal(result, SchemaStreamEditor.Apply(result, [new SchemaEdit("Epsilon", Version: 8, Guids: Guids(500, 2))]));
    }

    [Fact]
    public void AVersionEdit_WorksInTheOlderLayout_ButAListEditIsRefused()
    {
        var (data, _) = Write(Model().Take(2).ToList(), guidLists: false);

        byte[] result = SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Version: 1)]);
        Assert.Equal(Write(With(Model().Take(2).ToList(), "Alpha", version: 1), guidLists: false).Data, result);
        var e = Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Guids: Guids(1, 1))]));
        Assert.Contains("no GUID lists", e.Message);
    }

    // ---- refusals ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownClass_IsRefused()
    {
        var (data, _) = Write(Model());
        var e = Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Omega", Version: 1)]));
        Assert.Contains("no class named Omega", e.Message);
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("alpha", Version: 1)])); // names are case sensitive
    }

    [Fact]
    public void AnAmbiguousName_IsRefused()
    {
        var model = Model();
        model.Add(new Cls("Alpha", new NoRef(), 1, [], []));
        var (data, _) = Write(model);

        var e = Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Version: 1)]));
        Assert.Contains("More than one class", e.Message);
        SchemaStreamEditor.Apply(data, [new SchemaEdit("Beta", Version: 0)]); // the other names still work
    }

    [Fact]
    public void ABadEdit_IsRefused()
    {
        var (data, _) = Write(Model());
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Version: 1), new SchemaEdit("Alpha", Version: 0)])); // twice
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha")]));                                                    // nothing to change
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Version: -1)]));
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Guids: ["abcd"])]));                                  // short
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Guids: [new string('g', 32)])]));                      // not hex
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Guids: [Guid(1) + "00"])]));                          // long
    }

    [Fact]
    public void ARefusedEdit_LeavesTheInputUnchanged()
    {
        var (data, _) = Write(Model());
        byte[] copy = [.. data];
        Assert.Throws<ArgumentException>(() => SchemaStreamEditor.Apply(data, [new SchemaEdit("Zeta", Guids: Guids(1, 2)), new SchemaEdit("Omega", Version: 1)]));
        Assert.Equal(copy, data);
    }

    [Fact]
    public void BytesThatAreNotASchema_AreRefused()
    {
        Assert.Throws<FormatException>(() => SchemaStreamEditor.Apply([1, 2, 3, 4], []));
        var (data, _) = Write(Model());
        Assert.Throws<FormatException>(() => SchemaStreamEditor.Apply(data[..^30], [])); // cut inside a definition
    }

    // ---- the check of the result ---------------------------------------------------------------------------------------------

    private static (SchemaDocument Before, byte[] Edited) EditedAlpha()
    {
        var (data, _) = Write(Model());
        return (TfSchemaReader.Parse(data), SchemaStreamEditor.Apply(data, [new SchemaEdit("Alpha", Version: 2, Guids: Guids(60, 2))]));
    }

    private static readonly SchemaEdit[] AlphaEdit = [new("Alpha", Version: 2, Guids: Guids(60, 2))];

    [Fact]
    public void Verify_AcceptsTheExpectedResult()
    {
        var (before, edited) = EditedAlpha();
        SchemaStreamEditor.Verify(before, TfSchemaReader.Parse(edited), AlphaEdit);
    }

    [Fact]
    public void Verify_RejectsAnEditThatWasNotApplied_OrAppliedTwiceAsMuch()
    {
        var (data, _) = Write(Model());
        var before = TfSchemaReader.Parse(data);

        var e = Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, before, AlphaEdit));
        Assert.Contains("Alpha", e.Message);
        // Another class changed besides the requested one.
        var other = TfSchemaReader.Parse(Write(With(With(Model(), "Alpha", 2, Guids(60, 2)), "Zeta", version: 1)).Data);
        e = Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, other, AlphaEdit));
        Assert.Contains("Zeta", e.Message);
        // The wrong list in the edited class.
        var wrongList = TfSchemaReader.Parse(Write(With(Model(), "Alpha", 2, Guids(61, 2))).Data);
        Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, wrongList, AlphaEdit));
    }

    [Fact]
    public void Verify_RejectsChangedFieldsBasesAndNames()
    {
        var (data, _) = Write(Model());
        var before = TfSchemaReader.Parse(data);
        var model = With(With(Model(), "Alpha", 2, Guids(60, 2)), "Zeta");

        SchemaDocument Read(List<Cls> m) => TfSchemaReader.Parse(Write(m).Data);
        List<Cls> Replace(string name, Func<Cls, Cls> change) => [.. model.Select(c => c.Name == name ? change(c) with { Version = c.Version, Guids = c.Guids } : c)];

        Assert.Contains("fields", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read(Replace("Beta", c => c with { Fields = [Int("m_c"), Int("m_extra")] })), AlphaEdit)).Message);
        Assert.Contains("fields", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read(Replace("Beta", c => c with { Fields = [new("m_c", 0x08)] })), AlphaEdit)).Message);
        Assert.Contains("fields", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read(Replace("Delta", c => c with { Fields = [c.Fields[0], c.Fields[1] with { Target = new Back(13) }] })), AlphaEdit)).Message);
        Assert.Contains("fields", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read(Replace("Epsilon", c => c with { Fields = [c.Fields[0] with { Args = [new("", 0x0E, 0, 0, null, new Back(12))] }] })), AlphaEdit)).Message);
        Assert.Contains("base", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read(Replace("Beta", c => c with { Base = new NoRef() })), AlphaEdit)).Message);
        Assert.Contains("id or name", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read(Replace("Zeta", c => c with { Name = "Zeta2" })), AlphaEdit)).Message);
        Assert.Contains("class count", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, Read([.. model, new Cls("Extra", new NoRef(), 1, [], [])]), AlphaEdit)).Message);
    }

    [Fact]
    public void Verify_RejectsEveryChangedAttributeOfAField()
    {
        var (data, _) = Write(Model());
        var before = TfSchemaReader.Parse(data);
        var edit = new[] { new SchemaEdit("Zeta", Version: 0) };
        SchemaStreamEditor.Verify(before, before, edit);

        // The class and the field index (the nested template argument for the last case) whose attribute is changed.
        void Rejects(string className, int field, Func<SchemaField, SchemaField> change)
        {
            var classes = before.Classes.Select(c => c.Name != className ? c
                : c with { Fields = [.. c.Fields.Select((f, i) => i == field ? change(f) : f)] }).ToList();
            var after = new SchemaDocument { GuidLists = before.GuidLists, Classes = classes };
            Assert.Contains("fields", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(before, after, edit)).Message);
        }

        Rejects("Gamma", 0, f => f with { Name = "m_other" });
        Rejects("Gamma", 0, f => f with { Type = 0x05 });
        Rejects("Gamma", 0, f => f with { Mods = 0x11 });
        Rejects("Gamma", 0, f => f with { Extra = 2 });
        Rejects("Gamma", 0, f => f with { Fixed = 5 });
        Rejects("Delta", 1, f => f with { Ref = 13 });
        Rejects("Delta", 1, f => f with { Ref2 = 13 });
        Rejects("Epsilon", 0, f => f with { Args = null });
        Rejects("Epsilon", 0, f => f with { Args = [f.Args![0] with { Ref2 = 12 }] });
        Rejects("Epsilon", 0, f => f with { Args = [] });
    }

    [Fact]
    public void Verify_RejectsAChangedLayout()
    {
        var (data, _) = Write(Model().Take(2).ToList());
        var lists = TfSchemaReader.Parse(data);
        var plain = TfSchemaReader.Parse(Write(Model().Take(2).ToList(), guidLists: false).Data);

        Assert.Contains("layout", Assert.Throws<InvalidDataException>(() => SchemaStreamEditor.Verify(lists, plain, [new SchemaEdit("Alpha", Version: 1)])).Message);
    }
}
