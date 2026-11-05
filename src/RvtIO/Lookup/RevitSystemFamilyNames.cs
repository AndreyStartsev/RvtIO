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

/// <summary>Other UI texts Revit composes parameter values from. Each (DLL, id) pair matches the value
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

/// <summary>Localized system family names, read from the embedded <see cref="RevitUiNameTable"/> or, when a resource root is given,
/// from the localization folders of the user's own installation (&lt;root&gt;/&lt;version&gt;/&lt;locale&gt;). The locale is the one
/// recorded in the file's BasicFileInfo ("Locale when saved"); an unknown locale, version
/// or missing/malformed resource yields no names, never a substitute language.</summary>
internal sealed class RevitSystemFamilyNames
{
    // (string group, string id). The English text of every entry is the documented family name of that kind.
    internal static readonly IReadOnlyDictionary<SystemFamilyKind, (string Dll, int Id)> Sources =
        new Dictionary<SystemFamilyKind, (string, int)>
        {
            [SystemFamilyKind.BasicWall] = ("Revit.dll", 118),
            [SystemFamilyKind.CurtainWall] = ("HostObjRes.dll", 6077),
            [SystemFamilyKind.Floor] = ("Revit.dll", 10195),
            [SystemFamilyKind.FoundationSlab] = ("Revit.dll", 46779),
            [SystemFamilyKind.WallFoundation] = ("Revit.dll", 46241),
            [SystemFamilyKind.BasicRoof] = ("Revit.dll", 474),
            [SystemFamilyKind.SlopedGlazing] = ("HostObjRes.dll", 6095),
            [SystemFamilyKind.CompoundCeiling] = ("Revit.dll", 11696),
            [SystemFamilyKind.Pad] = ("Revit.dll", 11308),
            [SystemFamilyKind.Railing] = ("StairRampRes.dll", 6038),
            [SystemFamilyKind.WallSweep] = ("Revit.dll", 39347),
            [SystemFamilyKind.CastInPlaceStair] = ("StairRampRes.dll", 6192),
            [SystemFamilyKind.PrecastStair] = ("StairRampRes.dll", 6193),
            [SystemFamilyKind.AssembledStair] = ("StairRampRes.dll", 6194),
            [SystemFamilyKind.MonolithicRun] = ("StairRampRes.dll", 6195),
            [SystemFamilyKind.NonMonolithicRun] = ("StairRampRes.dll", 6196),
            [SystemFamilyKind.MonolithicLanding] = ("StairRampRes.dll", 6197),
            [SystemFamilyKind.NonMonolithicLanding] = ("StairRampRes.dll", 6198),
            // Own circular profile of the duct type (BuildingSystemsRes string 6028).
            [SystemFamilyKind.RoundDuct] = ("BuildingSystemsRes.dll", 6028),
            // The same AbsCurveType.m_Profile class selects the other duct families (Oval Duct, Rectangular Duct,
            // Flex Duct Rectangular, Flex Duct Round); Conduit with / without Fittings is RbsConduitType.m_bWithFitting.
            [SystemFamilyKind.OvalDuct] = ("BuildingSystemsRes.dll", 6026),
            [SystemFamilyKind.RectangularDuct] = ("BuildingSystemsRes.dll", 6027),
            [SystemFamilyKind.FlexDuctRectangular] = ("BuildingSystemsRes.dll", 6161),
            [SystemFamilyKind.FlexDuctRound] = ("BuildingSystemsRes.dll", 6163),
            [SystemFamilyKind.ConduitWithFittings] = ("BuildingSystemsRes.dll", 6626),
            [SystemFamilyKind.ConduitWithoutFittings] = ("BuildingSystemsRes.dll", 6627),
            // The own m_topsideSurfaceType int of the stairs support type: exactly 1 selects Carriage, every other int Stringer.
            [SystemFamilyKind.Stringer] = ("StairRampRes.dll", 6178),
            [SystemFamilyKind.Carriage] = ("StairRampRes.dll", 6179),
            // MEP curve types (pipes, flex pipes, pipe insulation and cable trays, for example
            // "Pipe Types" 4052, "Pipe Insulation" 5502).
            // Pipe Insulation is the first line of the command text "Name" + line break + "Tooltip".
            [SystemFamilyKind.PipeTypes] = ("BuildingSystemsRes.dll", 6888),
            [SystemFamilyKind.FlexPipeRound] = ("BuildingSystemsRes.dll", 6166),
            [SystemFamilyKind.PipeInsulation] = ("Revit.dll", 37263),
            [SystemFamilyKind.CableTrayWithFittings] = ("BuildingSystemsRes.dll", 6624),
            [SystemFamilyKind.CableTrayWithoutFittings] = ("BuildingSystemsRes.dll", 6625),
        };

    // Kinds added after the first bundle: a missing DLL or entry leaves only that kind unvalidated.
    private static bool IsOptional(SystemFamilyKind kind) =>
        kind is SystemFamilyKind.WallFoundation or SystemFamilyKind.MonolithicRun or SystemFamilyKind.NonMonolithicRun
            or SystemFamilyKind.MonolithicLanding or SystemFamilyKind.NonMonolithicLanding
            or SystemFamilyKind.RoundDuct or SystemFamilyKind.OvalDuct or SystemFamilyKind.RectangularDuct
            or SystemFamilyKind.FlexDuctRound or SystemFamilyKind.FlexDuctRectangular
            or SystemFamilyKind.ConduitWithFittings or SystemFamilyKind.ConduitWithoutFittings or SystemFamilyKind.Stringer or SystemFamilyKind.Carriage
            or SystemFamilyKind.PipeTypes or SystemFamilyKind.FlexPipeRound or SystemFamilyKind.PipeInsulation
            or SystemFamilyKind.CableTrayWithFittings or SystemFamilyKind.CableTrayWithoutFittings;

    private static readonly IReadOnlyDictionary<string, (string Culture, ushort LanguageId)> Locales =
        new Dictionary<string, (string, ushort)>(StringComparer.Ordinal)
        {
            ["RUS"] = ("ru-RU", 1049),
            ["ENU"] = ("en-US", 1033),
        };

    internal static readonly IReadOnlyDictionary<RevitUiText, (string Dll, int Id)> TextSources =
        new Dictionary<RevitUiText, (string, int)>
        {
            [RevitUiText.LevelFamily] = ("Revit.dll", 10273),
            [RevitUiText.TopographySurface] = ("GeneratedRes.dll", 7251),
            [RevitUiText.Subregion] = ("Revit.dll", 35),
            [RevitUiText.BoundaryConditions] = ("Revit.dll", 48369),
            [RevitUiText.NotAssociated] = ("EssentialsRes.dll", 6225),
            [RevitUiText.ModelGroup] = ("GeneratedRes.dll", 6762),
            [RevitUiText.ReferencePlane] = ("Revit.dll", 11013),
            [RevitUiText.SystemUndefined] = ("Revit.dll", 5391),
            [RevitUiText.SupplyAir] = ("Revit.dll", 5388),
            [RevitUiText.ReturnAir] = ("Revit.dll", 5384),
            [RevitUiText.ExhaustAir] = ("Revit.dll", 5321),
            [RevitUiText.OtherAir] = ("Revit.dll", 5367),
            [RevitUiText.HydronicSupply] = ("Revit.dll", 6114),
            [RevitUiText.HydronicReturn] = ("Revit.dll", 6115),
            [RevitUiText.Sanitary] = ("Revit.dll", 5911),
            [RevitUiText.Vent] = ("Revit.dll", 5991),
            [RevitUiText.DomesticHotWater] = ("Revit.dll", 6268),
            [RevitUiText.DomesticColdWater] = ("Revit.dll", 6269),
            [RevitUiText.PipingOther] = ("Revit.dll", 5854),
            [RevitUiText.FireProtectionWet] = ("Revit.dll", 7248),
            [RevitUiText.FireProtectionDry] = ("Revit.dll", 7249),
            [RevitUiText.FireProtectionPreAction] = ("Revit.dll", 7250),
            [RevitUiText.FireProtectionOther] = ("Revit.dll", 7251),
            [RevitUiText.LookupNone] = ("Revit.dll", 11746),
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

    /// <summary>The bundle of the file's Revit version, else the newest older one. The family names are the same text in Revit 2020 and 2022
    /// to 2026, except "Ramp", which is not in <see cref="Sources"/>.</summary>
    private static string BundleVersion(string root, string revitVersion)
    {
        if (!Directory.Exists(root) || Directory.Exists(Path.Combine(root, revitVersion)) || !int.TryParse(revitVersion, out int version))
            return revitVersion;
        return Directory.EnumerateDirectories(root).Select(path => Path.GetFileName(path))
            .Where(name => int.TryParse(name, out int bundle) && bundle < version)
            .OrderByDescending(int.Parse).FirstOrDefault() ?? revitVersion;
    }

    /// <summary>The (string group, string id) pairs <see cref="Load"/> can request: the embedded table holds exactly these.</summary>
    internal static IEnumerable<(string Dll, int Id)> RequestableStrings =>
        Sources.Values.Concat(TextSources.Values).Append((StairNameDll, StairNameId)).Distinct();

    private const string StairNameDll = "Revit.dll";
    private const int StairNameId = 11099;

    /// <summary>Loads the names of the file's saved locale. With no <paramref name="root"/> the strings come from the embedded table
    /// (<see cref="RevitUiNameTable"/>); with a root they are read from the localization folders of the user's own installation
    /// (&lt;root&gt;/&lt;version&gt;/&lt;culture&gt;), which overrides the embedded table.</summary>
    public static RevitSystemFamilyNames? Load(string? root, string? revitVersion, string? savedLocale)
    {
        if (string.IsNullOrEmpty(revitVersion) || savedLocale == null || !Locales.TryGetValue(savedLocale, out var locale))
            return null;
        // A resource file the source does not have is null; a malformed one throws.
        Func<string, IReadOnlyDictionary<int, string>?> open;
        if (string.IsNullOrEmpty(root))
        {
            // The embedded strings are those of version 2022; an older file gets no names, as with a missing localization folder.
            if (!int.TryParse(revitVersion, out int version) || version < RevitUiNameTable.BundleVersion) return null;
            open = dll => RevitUiNameTable.Get(locale.Culture, dll);
        }
        else
        {
            string directory = Path.Combine(root, BundleVersion(root, revitVersion), locale.Culture);
            open = dll =>
            {
                string path = Path.Combine(directory, dll);
                if (!File.Exists(path)) return null;
                return PeStringTable.Read(File.ReadAllBytes(path), locale.LanguageId)
                    ?? throw new InvalidDataException($"Malformed string table: {path}");
            };
        }
        var tables = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);
        var names = new Dictionary<SystemFamilyKind, string>();
        foreach (var (kind, (dll, id)) in Sources)
        {
            if (!tables.TryGetValue(dll, out var table))
            {
                // Older resource bundles keep all previously supported names.
                var opened = open(dll);
                if (opened == null)
                {
                    if (IsOptional(kind)) continue;
                    return null;
                }
                tables[dll] = table = opened;
            }
            if (!table.TryGetValue(id, out string? name) || string.IsNullOrWhiteSpace(name))
            {
                // Missing new component entries must not discard previously supported
                // names. The missing kind alone remains unvalidated.
                if (IsOptional(kind)) continue;
                return null;
            }
            if (kind == SystemFamilyKind.PipeInsulation) name = name.Split('\n')[0].Trim();
            if (name.Length == 0) continue;
            names[kind] = name;
        }
        // The stair name is the string IDS_STAIR (11099), independently of
        // type name/construction method. Missing this new entry affects only names.
        string? stairName = tables.GetValueOrDefault(StairNameDll)?.GetValueOrDefault(StairNameId);
        // UI texts are optional: a missing DLL or entry leaves only that text unvalidated.
        var texts = new Dictionary<RevitUiText, string>();
        foreach (var (text, (dll, id)) in TextSources)
        {
            if (!tables.TryGetValue(dll, out var table))
            {
                var opened = open(dll);
                if (opened == null) continue;
                tables[dll] = table = opened;
            }
            if (table.TryGetValue(id, out string? value) && !string.IsNullOrWhiteSpace(value)) texts[text] = value;
        }
        return new RevitSystemFamilyNames(names, string.IsNullOrWhiteSpace(stairName) ? null : stairName) { _texts = texts };
    }

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
