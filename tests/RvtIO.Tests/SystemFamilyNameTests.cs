using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Localized system family names read from the embedded names,
/// selected by the file's saved locale and attached only through an exact own type root.</summary>
public class SystemFamilyNameTests
{
    [Theory]
    [InlineData("ru-RU", "BasicWall", "Базовая стена")]
    [InlineData("en-US", "BasicWall", "Basic Wall")]
    [InlineData("ru-RU", "CurtainWall", "Витраж")]
    [InlineData("ru-RU", "Railing", "Ограждение")]
    [InlineData("ru-RU", "RoundDuct", "Воздуховод круглого сечения")]
    [InlineData("en-US", "RoundDuct", "Round Duct")]
    public void EmbeddedTableHasTheExactEntries(string culture, string key, string expected) =>
        Assert.Equal(expected, RevitUiNameTable.Get(culture, key));

    /// <summary>Every key the loader can request is in the embedded table, in both cultures: a miss would silently drop a name.</summary>
    [Theory]
    [InlineData("en-US")] [InlineData("ru-RU")]
    public void EmbeddedTableHoldsEveryRequestableKey(string culture)
    {
        var keys = Enum.GetNames<SystemFamilyKind>().Concat(Enum.GetNames<RevitUiText>()).Append(RevitSystemFamilyNames.StairNameKey);
        foreach (string key in keys)
            Assert.False(string.IsNullOrWhiteSpace(RevitUiNameTable.Get(culture, key)), $"{culture} {key}");
    }

    [Theory]
    [InlineData("ENU")] [InlineData("RUS")]
    public void LoadedNamesCoverEveryKindAndText(string locale)
    {
        var names = RevitSystemFamilyNames.Load("2022", locale)!;
        foreach (var kind in Enum.GetValues<SystemFamilyKind>()) Assert.NotNull(names.Get(kind));
        foreach (var text in Enum.GetValues<RevitUiText>()) Assert.NotNull(names.Get(text));
        Assert.NotNull(names.StairOccurrenceName);
    }

    [Theory]
    [InlineData("en-US\tBasicWall")]
    [InlineData("en-US\tBasicWall\ta\tb")]
    [InlineData("\tBasicWall\ttext")]
    [InlineData("en-US\t\ttext")]
    [InlineData("en-US\tBasicWall\ta\\qb")]
    [InlineData("en-US\tBasicWall\tabc\\")]
    [InlineData("en-US\tBasicWall\ta\nen-US\tBasicWall\tb")]
    public void EmbeddedTableParserRejectsMalformedLines(string text) =>
        Assert.Throws<InvalidDataException>(() => RevitUiNameTable.Parse(new StringReader(text)));

    /// <summary>The embedded table is well formed: every row parses, keys are unique per culture, the comment lines are skipped,
    /// and both cultures hold the same keys.</summary>
    [Fact]
    public void EmbeddedTableRowsParseWithUniqueKeysAndBothCulturesHoldTheSameKeys()
    {
        var rows = EmbeddedTables.Rows("revit_ui_names.tsv", 3);
        Assert.Equal(114, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => (r[0], r[1])).Distinct().Count());
        var parsed = RevitUiNameTable.Parse(new StringReader(EmbeddedTables.Text("revit_ui_names.tsv")));
        Assert.Equal(rows.Count, parsed.Count);
        foreach (string culture in new[] { "en-US", "ru-RU" })
            Assert.Equal(57, rows.Count(r => r[0] == culture));
        Assert.Equal(
            rows.Where(r => r[0] == "en-US").Select(r => r[1]).OrderBy(k => k).ToArray(),
            rows.Where(r => r[0] == "ru-RU").Select(r => r[1]).OrderBy(k => k).ToArray());
    }

    [Fact]
    public void EmbeddedTableEscapesRoundTrip()
    {
        const string text = "a\nb\tc\\d\re";
        var parsed = RevitUiNameTable.Parse(new StringReader("# comment\n\nen-US\tBasicWall\t" + RevitUiNameTable.Escape(text)));
        Assert.Equal(text, parsed[("en-US", "BasicWall")]);
    }

    [Theory]
    [InlineData("RUS", "BasicWall", "Базовая стена")]
    [InlineData("RUS", "FoundationSlab", "Фундаментная плита")]
    [InlineData("RUS", "WallSweep", "Выступающий профиль")]
    [InlineData("ENU", "CompoundCeiling", "Compound Ceiling")]
    [InlineData("ENU", "Pad", "Pad")]
    [InlineData("RUS", "RoundDuct", "Воздуховод круглого сечения")]
    [InlineData("ENU", "RoundDuct", "Round Duct")]
    public void Load_UsesTheSavedLocaleOfTheFile(string locale, string kind, string expected) =>
        Assert.Equal(expected, RevitSystemFamilyNames.Load("2022", locale)!
            .Get(Enum.Parse<SystemFamilyKind>(kind)));

    [Theory]
    [InlineData("2022", "DEU")] [InlineData("2022", null)] [InlineData("2020", "RUS")] [InlineData("2021", "ENU")]
    [InlineData(null, "RUS")] [InlineData("beta", "ENU")]
    public void Load_UnknownLocaleOrVersionGivesNoNames(string? version, string? locale) =>
        Assert.Null(RevitSystemFamilyNames.Load(version, locale));

    // The releases before the embedded names have the English names of the curtain wall, the stair and (2021 only) the MEP curve families
    // and nothing else.
    [Theory]
    [InlineData("2021", "CurtainWall", "Curtain Wall")] [InlineData("2021", "AssembledStair", "Assembled Stair")]
    [InlineData("2021", "CastInPlaceStair", "Cast-In-Place Stair")] [InlineData("2021", "PrecastStair", "Precast Stair")]
    [InlineData("2021", "PipeTypes", "Pipe Types")] [InlineData("2021", "RectangularDuct", "Rectangular Duct")]
    [InlineData("2021", "RoundDuct", "Round Duct")] [InlineData("2021", "PipeInsulation", "Pipe Insulation")]
    [InlineData("2021", "CableTrayWithFittings", "Cable Tray with Fittings")]
    [InlineData("2021", "BasicWall", null)] [InlineData("2021", "Floor", null)] [InlineData("2021", "Railing", null)]
    [InlineData("2020", "CurtainWall", "Curtain Wall")] [InlineData("2020", "AssembledStair", "Assembled Stair")]
    [InlineData("2020", "PipeTypes", null)] [InlineData("2020", "BasicWall", null)]
    public void LoadPreviousReleaseFamilies_GivesTheNamesOfThoseFamiliesOnly(string version, string kind, string? expected)
    {
        var names = RevitSystemFamilyNames.LoadPreviousReleaseFamilies(version, "ENU")!;
        Assert.Equal(expected, names.Get(Enum.Parse<SystemFamilyKind>(kind)));
        Assert.Equal("Stair", names.StairOccurrenceName);
        Assert.Null(names.Get(RevitUiText.LevelFamily));
        Assert.Null(names.Get(RevitUiText.SupplyAir));
    }

    [Theory]
    [InlineData("2019", "ENU")] [InlineData("2022", "ENU")] [InlineData("2021", "RUS")] [InlineData("2020", "RUS")]
    [InlineData("2021", "DEU")] [InlineData("2021", null)] [InlineData(null, "ENU")] [InlineData("beta", "ENU")]
    public void LoadPreviousReleaseFamilies_OtherVersionOrLocaleGivesNoNames(string? version, string? locale) =>
        Assert.Null(RevitSystemFamilyNames.LoadPreviousReleaseFamilies(version, locale));

    // A curtain wall and a stair type of a 2020/2021 file are named by the type root's own class, a basic wall type is not.
    [Theory]
    [InlineData(2559, null, -2000011, "Curtain Wall")]
    [InlineData(3742, 0, -2000120, "Assembled Stair")]
    [InlineData(3742, 1, -2000120, "Cast-In-Place Stair")]
    [InlineData(3742, 2, -2000120, "Precast Stair")]
    [InlineData(512, null, -2000011, null)]
    [InlineData(2559, null, -2000120, null)]
    [InlineData(3742, null, -2000120, null)]
    public void ApplyValidatedSystemFamilyNames_PreviousReleaseNamesCurtainWallAndStairTypes(int cls, int? discriminator, int category, string? expected)
    {
        var state = new ParsingState();
        state.SystemTypeRoots[500] = (1, (ushort)cls, discriminator);
        var type = new Element { Id = 500, CategoryId = category, Name = "Type" };
        ElementBuilder.ApplyValidatedSystemFamilyNames([type], state, RevitSystemFamilyNames.LoadPreviousReleaseFamilies("2021", "ENU"));
        Assert.Equal(expected, type.ValidatedSystemFamilyName);
        Assert.Null(type.FamilyName);
    }

    // The stair of a 2020/2021 file takes the instance name of the English table; an element of another category does not.
    [Theory]
    [InlineData(-2000120, "Stair")] [InlineData(-2000011, null)]
    public void ApplyValidatedStairOccurrenceNames_PreviousReleaseNamesAStairsRoot(int category, string? expected)
    {
        var stair = new Element { Id = 700, CategoryId = category, RootClassName = "Stairs" };
        ElementBuilder.ApplyValidatedStairOccurrenceNames([stair], RevitSystemFamilyNames.LoadPreviousReleaseFamilies("2021", "ENU"));
        Assert.Equal(expected, stair.ValidatedStairOccurrenceName);
        ElementBuilder.ApplyValidatedStairOccurrenceNames([stair], RevitSystemFamilyNames.LoadPreviousReleaseFamilies("2021", "RUS"));
        Assert.Null(stair.ValidatedStairOccurrenceName);
    }

    // The embedded names apply to every version from 2022 on.
    [Theory]
    [InlineData("2022")] [InlineData("2023")] [InlineData("2026")]
    public void Load_NewerVersionUsesTheEmbeddedNames(string version) =>
        Assert.Equal("Basic Wall", RevitSystemFamilyNames.Load(version, "ENU")!.Get(SystemFamilyKind.BasicWall));

    [Theory]
    [InlineData(512, null, -2000011, "BasicWall")]
    [InlineData(2559, null, -2000011, "CurtainWall")]
    [InlineData(1915, 0, -2000032, "Floor")]
    [InlineData(1915, 1, -2001300, "FoundationSlab")]
    [InlineData(3452, null, -2000035, "BasicRoof")]
    [InlineData(646, null, -2000038, "CompoundCeiling")]
    [InlineData(571, null, -2001263, "Pad")]
    [InlineData(3705, null, -2000126, "Railing")]
    [InlineData(873, null, -2000181, "WallSweep")]
    [InlineData(3742, 0, -2000120, "AssembledStair")]
    [InlineData(3742, 1, -2000120, "CastInPlaceStair")]
    [InlineData(3742, 2, -2000120, "PrecastStair")]
    public void KindOf_MapsExactRootClassInItsCategory(int cls, int? footing, int category, string kind) =>
        Assert.Equal(Enum.Parse<SystemFamilyKind>(kind), RevitSystemFamilyNames.KindOf((ushort)cls, footing, category));

    [Theory]
    [InlineData(512, null, -2000032)] [InlineData(2559, null, -2000038)]
    [InlineData(1915, null, -2000032)] [InlineData(1915, 1, -2000032)] [InlineData(1915, 0, -2001300)]
    [InlineData(1915, 2, -2001300)] [InlineData(3666, null, -2000011)] [InlineData(1829, null, -2000011)]
    [InlineData(652, null, -2000038)] [InlineData(3742, null, -2000120)] [InlineData(3742, 3, -2000120)]
    [InlineData(3742, 1, -2000919)] [InlineData(3718, 2, -2000919)] [InlineData(3697, 2, -2000920)]
    public void KindOf_RejectsUnmappedOrMisfiledRoots(int cls, int? footing, int category) =>
        Assert.Null(RevitSystemFamilyNames.KindOf((ushort)cls, footing, category));

    [Theory]
    [InlineData("ok", "Базовая стена")] [InlineData("duplicate_root", null)] [InlineData("malformed_root", null)]
    [InlineData("duplicate_element", null)] [InlineData("wrong_category", null)] [InlineData("no_resources", null)]
    public void ApplyValidatedSystemFamilyNames_NeedsOneExactOwnRoot(string mode, string? expected)
    {
        var state = new ParsingState();
        state.SystemTypeRoots[500] = mode switch
        {
            "duplicate_root" => (2, (ushort)0, null),
            "malformed_root" => (1, (ushort)0, null),
            _ => (1, (ushort)512, null),
        };
        var type = new Element { Id = 500, CategoryId = mode == "wrong_category" ? -2000032 : -2000011, Name = "Type" };
        var elements = new List<Element> { type };
        if (mode == "duplicate_element") elements.Add(new Element { Id = 500, CategoryId = -2000011 });
        type.ValidatedSystemFamilyName = "stale";
        var names = mode == "no_resources" ? null : RevitSystemFamilyNames.ForTests(
            new Dictionary<SystemFamilyKind, string> { [SystemFamilyKind.BasicWall] = "Базовая стена" });
        ElementBuilder.ApplyValidatedSystemFamilyNames(elements, state, names);
        Assert.Equal(expected, type.ValidatedSystemFamilyName);
        Assert.Equal("Type", type.Name);
        Assert.Null(type.FamilyName);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void RecordSystemTypeRoot_CountsEveryOwnSymbolRoot(bool duplicate, bool foreignOwner)
    {
        var state = new ParsingState();
        byte[] block = new byte[64];
        BitConverter.GetBytes(foreignOwner ? 7u : 500u).CopyTo(block, 0);
        RvtFileReader.RecordSystemTypeRoot(state, block, 0, 40, 512, 500);
        if (duplicate) RvtFileReader.RecordSystemTypeRoot(state, block, 0, 40, 512, 500);
        Assert.Equal(duplicate ? 2 : 1, state.SystemTypeRoots[500].BlockCount);
        Assert.Equal((ushort)0, state.SystemTypeRoots[500].ClassId);
        RvtFileReader.RecordSystemTypeRoot(state, block, 0, 40, 1794, 600);
        Assert.False(state.SystemTypeRoots.ContainsKey(600));
    }

    [Theory]
    [InlineData("wall", "Базовая стена:Wall type:9000")]
    // A validated system type names the family whatever the occurrence's general field says.
    [InlineData("system_family_wins", "Базовая стена:Wall type:9000")]
    [InlineData("category_mismatch", "OST_Walls:Wall type:9000")]
    [InlineData("duplicate_type", "OST_Walls:Wall type:9000")]
    [InlineData("no_type_link", "OST_Walls:Wall type:9000")]
    public void ProductName_UsesTheOwnTypesSystemFamilyName(string mode, string expected)
    {
        var occurrence = Product(9000, -2000011, "OST_Walls", "Wall type");
        if (mode != "no_type_link") occurrence.Parameters.Add(TypeLink(9001));
        if (mode == "system_family_wins") occurrence.FamilyName = "Own family";
        var type = new Element { Id = 9001, CategoryId = mode == "category_mismatch" ? -2000032 : -2000011, ValidatedSystemFamilyName = "Базовая стена" };
        var elements = new List<Element> { occurrence, type };
        if (mode == "duplicate_type") elements.Add(new Element { Id = 9001, CategoryId = -2000011 });
        Assert.Contains($"'{Encode(expected)}'", Export(elements.ToArray()));
    }

    [Fact]
    public void ProductName_FoundationSlabTypeMayBeFiledUnderStructuralFoundation()
    {
        var occurrence = Product(9010, -2000032, "OST_Floors", "Slab type");
        occurrence.Parameters.Add(TypeLink(9011));
        var type = new Element { Id = 9011, CategoryId = -2001300, ValidatedSystemFamilyName = "Фундаментная плита" };
        Assert.Contains($"'{Encode("Фундаментная плита:Slab type:9010")}'", Export(occurrence, type));
    }

    [Theory]
    [InlineData(9021, "Ограждение:Railing type:9020")]
    [InlineData(9022, "OST_StairsRailing:Railing type:9020")]
    public void ProductName_RailingUsesItsOwnAttributeLink(int elemTypeParam, string expected)
    {
        var occurrence = Product(9020, -2000126, "OST_StairsRailing", "Railing type");
        occurrence.SerializedStairsRailingAttrId = 9021;
        if (elemTypeParam != 9021) occurrence.Parameters.Add(TypeLink(elemTypeParam));
        var type = new Element { Id = 9021, CategoryId = -2000126, ValidatedSystemFamilyName = "Ограждение" };
        var other = new Element { Id = 9022, CategoryId = -2000126, ValidatedSystemFamilyName = "Ограждение" };
        Assert.Contains($"'{Encode(expected)}'", Export(occurrence, type, other));
    }

    [Fact]
    public void ProductName_ValidatedNativeRailingTypeWinsOverGeneralTypeFanOut()
    {
        var occurrence = Product(9060, -2000126, "OST_StairsRailing", "Railing type");
        occurrence.SerializedOwnElementClassId = 485;
        occurrence.SerializedStairsRailingAttrId = 9061;
        occurrence.Parameters.Add(TypeLink(9062));
        occurrence.TypeId = 9062;
        var type = new Element { Id = 9061, CategoryId = -2000126, ValidatedSystemFamilyName = "Ограждение" };
        var other = new Element { Id = 9062, CategoryId = -2000126, Name = "Unrelated type" };
        string ifc = Export(occurrence, type, other);
        Assert.Contains($"'{Encode("Ограждение:Railing type:9060")}'", ifc);
        Assert.Contains($"'{Encode("Ограждение:Railing type")}'", ifc);
        Assert.Equal(9062, occurrence.TypeId);
        Assert.Equal(9062, occurrence.FamilyTypeId);
        Assert.Null(occurrence.FamilyName);
        Assert.Equal("Railing type", occurrence.Name);
    }

    [Theory]
    [InlineData(null, -2000126, 1, 1, -2000126, 9061)]
    [InlineData(1794, -2000126, 1, 1, -2000126, 9061)]
    [InlineData(2007, -2000126, 1, 1, -2000126, 9061)]
    [InlineData(485, -2000120, 1, 1, -2000120, 9061)]
    [InlineData(485, -2000126, 0, 1, -2000126, 9061)]
    [InlineData(485, -2000126, 2, 1, -2000126, 9061)]
    [InlineData(485, -2000126, 1, 0, -2000126, 9061)]
    [InlineData(485, -2000126, 1, 2, -2000126, 9061)]
    [InlineData(485, -2000126, 1, 1, -2000011, 9061)]
    [InlineData(485, -2000126, 1, 1, -2000126, 9060)]
    [InlineData(485, -2000126, 1, 1, -2000126, 0)]
    public void ProductFamily_ConflictingRailingLinksNeedUniqueOwnNativeValidation(
        int? ownClass, int category, int occurrenceCount, int typeCount, int typeCategory, int ownType)
    {
        var occurrence = Product(9060, category, "OST_StairsRailing", "Railing type");
        occurrence.SerializedOwnElementClassId = ownClass is int classId ? checked((ushort)classId) : null;
        occurrence.SerializedStairsRailingAttrId = ownType;
        occurrence.Parameters.Add(TypeLink(9062));
        var type = new Element { Id = 9061, CategoryId = typeCategory, ValidatedSystemFamilyName = "Ограждение" };
        var elements = new Dictionary<int, Element> { [9060] = occurrence, [9061] = type };
        var counts = new Dictionary<int, int> { [9060] = occurrenceCount, [9061] = typeCount };
        Assert.Null(IfcExporter.ResolveSystemFamilyName(occurrence, elements, counts));
    }

    [Theory]
    [InlineData(null, "Базовая стена:Wall type")]
    [InlineData("Own family", "Own family:Wall type")]
    public void TypeObjectName_PrefixesTheSystemFamilyName(string? familyName, string expected)
    {
        var occurrence = Product(9030, -2000011, "OST_Walls", "Wall type");
        occurrence.Parameters.Add(TypeLink(9031));
        var type = new Element
        {
            Id = 9031, CategoryId = -2000011, Name = "Wall type", FamilyName = familyName,
            ValidatedSystemFamilyName = "Базовая стена",
        };
        string ifc = Export(occurrence, type);
        Assert.Matches($@"=IFC[A-Z]*TYPE\('[^']*',#\d+,'{Regex.Escape(Encode(expected))}'", ifc);
    }

    [Theory]
    [InlineData(true, -1, "Сборная лестница:Stair type:9040")]
    [InlineData(false, -1, "OST_Stairs:Stair type:9040")]
    [InlineData(true, 9042, "OST_Stairs:Stair type:9040")]
    public void ProductName_StairUsesItsResolvedOwnTypeLink(bool resolved, int elemTypeParam, string expected)
    {
        var occurrence = Product(9040, -2000120, "OST_Stairs", "Stair type");
        occurrence.SerializedStairTypeId = 9041;
        occurrence.SerializedStairTypeLinkIsResolved = resolved;
        if (elemTypeParam > 0) occurrence.Parameters.Add(TypeLink(elemTypeParam));
        var type = new Element { Id = 9041, CategoryId = -2000120, ValidatedSystemFamilyName = "Сборная лестница" };
        var other = new Element { Id = 9042, CategoryId = -2000120, ValidatedSystemFamilyName = "Сборная лестница" };
        Assert.Contains($"'{Encode(expected)}'", Export(occurrence, type, other));
    }

    [Theory]
    [InlineData(3718, 1, -2000919, "OST_StairsRuns", "Монолитный марш")]
    [InlineData(3718, 0, -2000919, "OST_StairsRuns", "Немонолитный марш")]
    [InlineData(3697, 1, -2000920, "OST_StairsLandings", "Монолитная площадка")]
    [InlineData(3697, 0, -2000920, "OST_StairsLandings", "Немонолитная площадка")]
    public void StairComponentProductAndTypeUseTheOwnTypesResourceFamily(
        ushort cls, int flag, int category, string categoryName, string family)
    {
        var occurrence = Product(9050, category, categoryName, "unrelated occurrence name");
        occurrence.Parameters.Add(TypeLink(9051));
        var type = new Element { Id = 9051, CategoryId = category, Name = "borrowed level name" };
        var state = new ParsingState(); state.SystemTypeRoots[9051] = (1, cls, flag);
        state.StairComponentTypeNames[9051] = (1, cls, "arbitrary type name");
        ElementBuilder.ApplyValidatedStairComponentTypeNames([occurrence, type], state);
        Assert.Equal(1, ElementBuilder.ApplyValidatedSystemFamilyNames([occurrence, type], state,
            RevitSystemFamilyNames.Load("2022", "RUS")));
        string ifc = Export(occurrence, type);
        Assert.Contains($"'{Encode(family + ":arbitrary type name:9050")}'", ifc);
        Assert.Contains($"'{Encode(family + ":arbitrary type name")}'", ifc);
        Assert.Null(type.FamilyName);
        Assert.Equal("borrowed level name", type.Name);
    }

    [Fact]
    public void CircularDuctWriterUsesOriginalFamilyWithoutChangingPublicTypeGraph()
    {
        var occurrence = Product(9070, -2008000, "OST_DuctCurves", "general occurrence name");
        occurrence.Parameters.Add(TypeLink(9071));
        occurrence.ValidatedDistributionTypeReferenceName = "Own type name";
        var type = new Element { Id = 9071, CategoryId = -2008000, Name = "general type name" };
        var state = new ParsingState(); state.SystemTypeRoots[9071] = (1, 110, 124);
        state.DuctTypeNames[9071] = (1, "Own type name");
        ElementBuilder.ApplyValidatedDuctTypeNames([occurrence, type], state);
        ElementBuilder.ApplyValidatedSystemFamilyNames([occurrence, type], state,
            RevitSystemFamilyNames.Load("2022", "RUS"));
        string ifc = Export(occurrence, type);
        Assert.Contains($"'{Encode("Воздуховод круглого сечения:Own type name:9070")}'", ifc);
        Assert.Contains($"'{Encode("Воздуховод круглого сечения:Own type name")}'", ifc);
        Assert.Contains($"'{Encode("Воздуховод круглого сечения:Own type name")}'",
            ifc.Split('\n').Single(line => line.Contains("=IFCDUCTSEGMENTTYPE(")));
        Assert.Equal("general occurrence name", occurrence.Name);
        Assert.Equal("general type name", type.Name);
        Assert.Null(type.FamilyName);
    }

    private static Parameter TypeLink(int typeId) => new()
    {
        Id = -1002050,
        Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId,
        Value = typeId,
    };

    private static string Encode(string text) =>
        Regex.Replace(text, @"[^\x20-\x7E]+", match =>
            "\\X2\\" + string.Concat(match.Value.Select(ch => ((int)ch).ToString("X4"))) + "\\X0\\");

    private static Element Product(int id, int categoryId, string category, string name) => new()
    {
        Id = id,
        TypeId = id + 1,
        IsDiscovered = true,
        CategoryId = categoryId,
        Category = category,
        Name = name,
        References = [3],
        Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
        BoundingBox = [0, 0, 0, 4, 3, 2],
        Geometry = new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Faces = new[]
            {
                new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 },
            }.Select(face => new GeoFace { OuterVertices = face.Select(index => Corners[index]).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        },
    };

    private static readonly double[][] Corners =
    [
        [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0],
        [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2],
    ];

    private static string Export(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-system-family-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
