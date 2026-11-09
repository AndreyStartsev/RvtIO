namespace RvtIO.Lookup;

/// <summary>System family kinds whose display name Revit supplies from its own resources
/// rather than from model data. Each kind is decided by the type element's own root class.</summary>
internal enum SystemFamilyKind
{
    BasicWall,
    CurtainWall,
    Floor,
    FoundationSlab,
    WallFoundation,
    BasicRoof,
    SlopedGlazing,
    CompoundCeiling,
    Pad,
    Railing,
    WallSweep,
    AssembledStair,
    CastInPlaceStair,
    PrecastStair,
    MonolithicRun,
    NonMonolithicRun,
    MonolithicLanding,
    NonMonolithicLanding,
    RoundDuct,
    OvalDuct,
    RectangularDuct,
    FlexDuctRound,
    FlexDuctRectangular,
    ConduitWithFittings,
    ConduitWithoutFittings,
    Stringer,
    Carriage,
    PipeTypes,
    FlexPipeRound,
    PipeInsulation,
    CableTrayWithFittings,
    CableTrayWithoutFittings,
}

/// <summary>Other UI texts Revit composes parameter values from. Each member's text matches the value
/// the Revit 2022 API returns under the English and the Russian UI.</summary>
internal enum RevitUiText
{
    LevelFamily, // "Level": Level type family, prefix of "Host" for instances on a level
    TopographySurface, // "Surface"
    Subregion, // "Subregion"
    BoundaryConditions, // "Boundary Conditions"
    NotAssociated, // "<not associated>"
    ModelGroup, // family label of a model group instance
    ReferencePlane, // "Reference Plane": family label of a reference plane
    // System classification texts (RBS_SYSTEM_CLASSIFICATION_PARAM), one per RbsSystemType basic system type.
    SystemUndefined, // "Undefined"
    SupplyAir,           // basic system type 1
    ReturnAir,           // 2
    ExhaustAir,          // 3
    OtherAir,            // 4
    HydronicSupply,      // 7
    HydronicReturn,      // 8
    Sanitary,            // 16
    Vent,                // 17
    DomesticHotWater,    // 19
    DomesticColdWater,   // 20
    PipingOther,         // 22
    FireProtectionWet,   // 23
    FireProtectionDry,   // 24
    FireProtectionPreAction, // 25
    FireProtectionOther, // 26
    LookupNone, // "<None>": the empty entry of a lookup table (the six other resources whose English text is "<None>" have the same text)
}

/// <summary>Localized system family names, read from the embedded <see cref="RevitUiNameTable"/>. The locale is the one
/// recorded in the file's BasicFileInfo ("Locale when saved"); an unknown locale or version
/// yields no names, never a substitute language.</summary>
internal sealed class RevitSystemFamilyNames
{
    private static readonly IReadOnlyDictionary<string, string> Cultures =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RUS"] = "ru-RU",
            ["ENU"] = "en-US",
        };

        /// <summary>Basic system type of an RbsSystemType (3190:m_basicSystemType) to its classification text id.</summary>
        internal static RevitUiText? SystemClassification(int basicSystemType) => basicSystemType switch
        {
            1 => RevitUiText.SupplyAir, 2 => RevitUiText.ReturnAir, 3 => RevitUiText.ExhaustAir, 4 => RevitUiText.OtherAir,
            7 => RevitUiText.HydronicSupply, 8 => RevitUiText.HydronicReturn, 16 => RevitUiText.Sanitary,
            17 => RevitUiText.Vent, 19 => RevitUiText.DomesticHotWater, 20 => RevitUiText.DomesticColdWater,
            22 => RevitUiText.PipingOther, 23 => RevitUiText.FireProtectionWet, 24 => RevitUiText.FireProtectionDry,
            25 => RevitUiText.FireProtectionPreAction, 26 => RevitUiText.FireProtectionOther,
            _ => null,
        };

    private readonly IReadOnlyDictionary<SystemFamilyKind, string> _names;
    private IReadOnlyDictionary<RevitUiText, string> _texts = new Dictionary<RevitUiText, string>();
    internal string? StairOccurrenceName { get; }

    private RevitSystemFamilyNames(IReadOnlyDictionary<SystemFamilyKind, string> names, string? stairOccurrenceName = null)
    {
        _names = names;
        StairOccurrenceName = stairOccurrenceName;
    }

    public string? Get(SystemFamilyKind kind) => _names.GetValueOrDefault(kind);

    /// <summary>A UI text in the file's saved locale; null when its resource is missing.</summary>
    public string? Get(RevitUiText text) => _texts.GetValueOrDefault(text);

    /// <summary>The key of the stair occurrence name in the embedded table.</summary>
    internal const string StairNameKey = "StairOccurrenceName";

    /// <summary>Loads the names of the file's saved locale from the embedded table. Files older than the table's version get no names.</summary>
    public static RevitSystemFamilyNames? Load(string? revitVersion, string? savedLocale)
    {
        if (string.IsNullOrEmpty(revitVersion) || savedLocale == null || !Cultures.TryGetValue(savedLocale, out string? culture))
            return null;
        if (!int.TryParse(revitVersion, out int version) || version < RevitUiNameTable.BundleVersion) return null;
        var names = new Dictionary<SystemFamilyKind, string>();
        foreach (var kind in Enum.GetValues<SystemFamilyKind>())
        {
            string? name = RevitUiNameTable.Get(culture, kind.ToString());
            if (kind == SystemFamilyKind.PipeInsulation) name = name?.Split('\n')[0].Trim();
            if (!string.IsNullOrWhiteSpace(name)) names[kind] = name;
        }
        string? stairName = RevitUiNameTable.Get(culture, StairNameKey);
        var texts = new Dictionary<RevitUiText, string>();
        foreach (var text in Enum.GetValues<RevitUiText>())
            if (RevitUiNameTable.Get(culture, text.ToString()) is { } value && !string.IsNullOrWhiteSpace(value)) texts[text] = value;
        return new RevitSystemFamilyNames(names, string.IsNullOrWhiteSpace(stairName) ? null : stairName) { _texts = texts };
    }

    /// <summary>The English names of the families of the releases before the embedded names (Revit 2020 and 2021). The MEP curve families
    /// (ducts, pipes, conduits, cable trays, pipe insulation; 2021 only) are named when <paramref name="locale"/>, the locale in effect (the
    /// requested one, else the one the file was saved in), is English. Curtain Wall, the stair families, the slab families (Floor, Foundation Slab,
    /// Pad; both releases) and the stair occurrence name are named only when <paramref name="requestedLocale"/>, the locale a profile asks for whatever language the file was saved in, is
    /// English, so a reader without a requested locale (the legacy profile) is unchanged. Null when nothing is named; no other kind or text is
    /// in it: only the type roots of those families and the stairs read this table.</summary>
    internal static RevitSystemFamilyNames? LoadPreviousReleaseFamilies(string? revitVersion, string? locale, string? requestedLocale)
    {
        if (!int.TryParse(revitVersion, out int version) || version is not (2020 or 2021)) return null;
        var names = new Dictionary<SystemFamilyKind, string>();
        string? stairName = null;
        if (version == 2021 && IsEnglish(locale, out string? mepCulture))
            AddNames(names, MepCurveFamilies, mepCulture!);
        if (IsEnglish(requestedLocale, out string? culture))
        {
            AddNames(names, CurtainStairAndSlabFamilies, culture!);
            stairName = RevitUiNameTable.Get(culture!, StairNameKey);
        }
        return names.Count == 0 ? null : new RevitSystemFamilyNames(names, string.IsNullOrWhiteSpace(stairName) ? null : stairName);
    }

    private static bool IsEnglish(string? locale, out string? culture)
    {
        culture = null;
        return locale != null && Cultures.TryGetValue(locale, out culture) && culture == "en-US";
    }

    private static void AddNames(Dictionary<SystemFamilyKind, string> names, IEnumerable<SystemFamilyKind> kinds, string culture)
    {
        foreach (var kind in kinds)
        {
            string? name = RevitUiNameTable.Get(culture, kind.ToString());
            if (kind == SystemFamilyKind.PipeInsulation) name = name?.Split('\n')[0].Trim();
            if (!string.IsNullOrWhiteSpace(name)) names[kind] = name;
        }
    }

    private static readonly SystemFamilyKind[] CurtainStairAndSlabFamilies =
    [
        SystemFamilyKind.CurtainWall, SystemFamilyKind.AssembledStair, SystemFamilyKind.CastInPlaceStair, SystemFamilyKind.PrecastStair,
        SystemFamilyKind.Floor, SystemFamilyKind.FoundationSlab, SystemFamilyKind.Pad,
    ];

    private static readonly SystemFamilyKind[] MepCurveFamilies =
    [
        SystemFamilyKind.RoundDuct, SystemFamilyKind.OvalDuct, SystemFamilyKind.RectangularDuct, SystemFamilyKind.FlexDuctRound,
        SystemFamilyKind.FlexDuctRectangular, SystemFamilyKind.ConduitWithFittings, SystemFamilyKind.ConduitWithoutFittings,
        SystemFamilyKind.PipeTypes, SystemFamilyKind.FlexPipeRound, SystemFamilyKind.PipeInsulation,
        SystemFamilyKind.CableTrayWithFittings, SystemFamilyKind.CableTrayWithoutFittings,
    ];

    internal static RevitSystemFamilyNames ForTests(IReadOnlyDictionary<SystemFamilyKind, string> names,
        string? stairOccurrenceName = null, IReadOnlyDictionary<RevitUiText, string>? texts = null) =>
        new(names, stairOccurrenceName) { _texts = texts ?? new Dictionary<RevitUiText, string>() };

    /// <summary>The kind of a type element's own exact root, or null. Category must agree with
    /// the class so a mis-filed type never borrows another family's name.</summary>
    internal static SystemFamilyKind? KindOf(ushort rootClass, int? discriminator, int? categoryId) =>
        (rootClass, categoryId) switch
        {
            (512, -2000011) => SystemFamilyKind.BasicWall,
            (2559, -2000011) => SystemFamilyKind.CurtainWall,
            (1915, -2000032) when discriminator == 0 => SystemFamilyKind.Floor,
            (1915, -2001300) when discriminator == 1 => SystemFamilyKind.FoundationSlab,
            (813, -2001300) => SystemFamilyKind.WallFoundation, // ContFootingType
            (3452, -2000035) => SystemFamilyKind.BasicRoof,
            (908, -2000035) => SystemFamilyKind.SlopedGlazing, // CurtainRoofAttributes
            (646, -2000038) => SystemFamilyKind.CompoundCeiling,
            (571, -2001263) => SystemFamilyKind.Pad,
            (3705, -2000126) => SystemFamilyKind.Railing,
            (873, -2000181) => SystemFamilyKind.WallSweep,
            // StairsType.m_constructionMethod: "Precast Stair" (2), "Monolithic Stair" (1) and the assembled default (0).
            (3742, -2000120) when discriminator == 0 => SystemFamilyKind.AssembledStair,
            (3742, -2000120) when discriminator == 1 => SystemFamilyKind.CastInPlaceStair,
            (3742, -2000120) when discriminator == 2 => SystemFamilyKind.PrecastStair,
            (3718, -2000919) when discriminator == 1 => SystemFamilyKind.MonolithicRun,
            (3718, -2000919) when discriminator == 0 => SystemFamilyKind.NonMonolithicRun,
            (3697, -2000920) when discriminator == 1 => SystemFamilyKind.MonolithicLanding,
            (3697, -2000920) when discriminator == 0 => SystemFamilyKind.NonMonolithicLanding,
            // AbsDuctType is shape-neutral; only its own profile class selects this family.
            (110, -2008000) when discriminator == 124 => SystemFamilyKind.RoundDuct,
            (110, -2008000) when discriminator == 126 => SystemFamilyKind.OvalDuct,
            (110, -2008000) when discriminator == 127 => SystemFamilyKind.RectangularDuct,
            (114, -2008020) when discriminator == 124 => SystemFamilyKind.FlexDuctRound,
            (114, -2008020) when discriminator == 127 => SystemFamilyKind.FlexDuctRectangular,
            (3135, -2008132) when discriminator == 1 => SystemFamilyKind.ConduitWithFittings,
            (3135, -2008132) when discriminator == 0 => SystemFamilyKind.ConduitWithoutFittings,
            (3736, -2000123) when discriminator == 1 => SystemFamilyKind.Carriage,
            (3736, -2000123) when discriminator != null => SystemFamilyKind.Stringer,
            (3227, -2008044) => SystemFamilyKind.PipeTypes,
            (3181, -2008050) => SystemFamilyKind.FlexPipeRound,
            // The root class alone selects the family, as with the other types of this table: a pipe insulation type
            // is filed under OST_PipeInsulations but its category is not needed to pick the family name.
            (3217, _) => SystemFamilyKind.PipeInsulation,
            (3130, -2008130) when discriminator == 1 => SystemFamilyKind.CableTrayWithFittings,
            (3130, -2008130) when discriminator == 0 => SystemFamilyKind.CableTrayWithoutFittings,
            _ => null,
        };
}
