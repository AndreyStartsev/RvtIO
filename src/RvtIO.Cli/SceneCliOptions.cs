using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Scene;

namespace RvtIO.Cli;

/// <summary>Command-line options for 3D scene (<c>.glb</c>, <c>--scene-json</c>) and standalone HTML viewer (<c>.html</c>) exports.</summary>
internal sealed class SceneCliOptions
{
    public static readonly string[] OneValueNames =
    [
        "--viewer-title",
        "--viewer-theme",
        "--scene-categories",
        "--scene-ids",
        "--scene-parameter-names",
    ];

    public static readonly string[] SwitchNames =
    [
        "--scene-json",
        "--scene-pretty",
        "--scene-no-parameters",
        "--scene-shared-coordinates",
        "--scene-include-rooms",
        "--scene-no-grids",
        "--glb-z-up",
    ];

    private static readonly Lazy<Dictionary<string, List<int>>> DisplayCategoryLookup = new(BuildDisplayCategoryLookup);

    public bool SceneJson { get; init; }
    public bool PrettyJson { get; init; }
    public bool IncludeParameters { get; init; } = true;
    public bool ApplySharedCoordinates { get; init; }
    public bool IncludeRooms { get; init; }
    public bool IncludeGrids { get; init; } = true;
    public bool GlbZUp { get; init; }
    public string? Title { get; init; }
    public bool DarkTheme { get; init; } = true;
    public HashSet<int>? CategoryIds { get; init; }
    public HashSet<int>? ElementIds { get; init; }
    public HashSet<string>? ParameterNames { get; init; }

    /// <summary>True when any scene- or viewer-only option was passed on the command line.</summary>
    public bool HasAnySceneSpecificFlag { get; init; }

    public HtmlViewerOptions ToViewerOptions(SkipLog? skipLog = null, TextWriter? diagnosticOutput = null) => new()
    {
        Title = Title,
        InitialTheme = DarkTheme ? "dark" : "light",
        ShowGridsByDefault = IncludeGrids,
        IncludeParameters = IncludeParameters,
        ApplySharedCoordinates = ApplySharedCoordinates,
        IncludeRooms = IncludeRooms,
        IncludeGrids = IncludeGrids,
        CategoryIds = CategoryIds,
        ElementIds = ElementIds,
        IncludedParameterNames = ParameterNames,
        SkipLog = skipLog,
        DiagnosticOutput = diagnosticOutput,
    };

    public static bool TryParse(string[] args, [NotNullWhen(true)] out SceneCliOptions? options, out string? error)
    {
        options = null;
        error = null;

        bool sceneJson = false;
        bool prettyJson = false;
        bool noParameters = false;
        bool sharedCoordinates = false;
        bool includeRooms = false;
        bool noGrids = false;
        bool glbZUp = false;
        bool hasAnyFlag = false;

        string? title = null;
        bool seenTitle = false;
        bool darkTheme = true;
        bool seenTheme = false;
        HashSet<int>? categoryIds = null;
        bool seenCategories = false;
        HashSet<int>? elementIds = null;
        bool seenIds = false;
        HashSet<string>? parameterNames = null;
        bool seenParamNames = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--scene-json":
                    sceneJson = true;
                    hasAnyFlag = true;
                    break;
                case "--scene-pretty":
                    prettyJson = true;
                    hasAnyFlag = true;
                    break;
                case "--scene-no-parameters":
                    noParameters = true;
                    hasAnyFlag = true;
                    break;
                case "--scene-shared-coordinates":
                    sharedCoordinates = true;
                    hasAnyFlag = true;
                    break;
                case "--scene-include-rooms":
                    includeRooms = true;
                    hasAnyFlag = true;
                    break;
                case "--scene-no-grids":
                    noGrids = true;
                    hasAnyFlag = true;
                    break;
                case "--glb-z-up":
                    glbZUp = true;
                    hasAnyFlag = true;
                    break;

                case "--viewer-title":
                    if (seenTitle || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "--viewer-title requires one non-empty text value.";
                        return false;
                    }
                    seenTitle = true;
                    hasAnyFlag = true;
                    title = args[++i];
                    break;

                case "--viewer-theme":
                    if (seenTheme || i + 1 >= args.Length)
                    {
                        error = "--viewer-theme requires one value: dark or light.";
                        return false;
                    }
                    string themeText = args[++i];
                    if (string.Equals(themeText, "dark", StringComparison.OrdinalIgnoreCase))
                        darkTheme = true;
                    else if (string.Equals(themeText, "light", StringComparison.OrdinalIgnoreCase))
                        darkTheme = false;
                    else
                    {
                        error = "--viewer-theme requires one value: dark or light.";
                        return false;
                    }
                    seenTheme = true;
                    hasAnyFlag = true;
                    break;

                case "--scene-categories":
                    if (seenCategories || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "--scene-categories requires a comma-separated list of category IDs or names.";
                        return false;
                    }
                    seenCategories = true;
                    hasAnyFlag = true;
                    if (!TryParseCategories(args[++i], out categoryIds, out error))
                        return false;
                    break;

                case "--scene-ids":
                    if (seenIds || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "--scene-ids requires a comma-separated list of element IDs.";
                        return false;
                    }
                    seenIds = true;
                    hasAnyFlag = true;
                    if (!TryParseElementIds(args[++i], out elementIds, out error))
                        return false;
                    break;

                case "--scene-parameter-names":
                    if (seenParamNames || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "--scene-parameter-names requires a comma-separated list of parameter names.";
                        return false;
                    }
                    seenParamNames = true;
                    hasAnyFlag = true;
                    var names = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (names.Count == 0)
                    {
                        error = "--scene-parameter-names requires at least one parameter name.";
                        return false;
                    }
                    parameterNames = names;
                    break;
            }
        }

        options = new SceneCliOptions
        {
            SceneJson = sceneJson,
            PrettyJson = prettyJson,
            IncludeParameters = !noParameters,
            ApplySharedCoordinates = sharedCoordinates,
            IncludeRooms = includeRooms,
            IncludeGrids = !noGrids,
            GlbZUp = glbZUp,
            Title = title,
            DarkTheme = darkTheme,
            CategoryIds = categoryIds,
            ElementIds = elementIds,
            ParameterNames = parameterNames,
            HasAnySceneSpecificFlag = hasAnyFlag,
        };
        return true;
    }

    private static bool TryParseElementIds(string raw, [NotNullWhen(true)] out HashSet<int>? ids, out string? error)
    {
        ids = new HashSet<int>();
        error = null;
        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "--scene-ids requires at least one integer element ID.";
            ids = null;
            return false;
        }
        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                error = $"--scene-ids: '{part}' is not a valid integer element ID.";
                ids = null;
                return false;
            }
            ids.Add(id);
        }
        return true;
    }

    private static bool TryParseCategories(string raw, [NotNullWhen(true)] out HashSet<int>? categoryIds, out string? error)
    {
        categoryIds = new HashSet<int>();
        error = null;
        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "--scene-categories requires at least one category ID or name.";
            categoryIds = null;
            return false;
        }

        var displayLookup = DisplayCategoryLookup.Value;
        foreach (string part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId))
            {
                categoryIds.Add(numericId);
                continue;
            }
            if (Enum.TryParse<BuiltInCategory>(part, ignoreCase: true, out var bic) && !int.TryParse(part, out _))
            {
                categoryIds.Add((int)bic);
                continue;
            }
            if (!part.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BuiltInCategory>("OST_" + part, ignoreCase: true, out var ostBic))
            {
                categoryIds.Add((int)ostBic);
                continue;
            }
            if (displayLookup.TryGetValue(part, out var matchedIds))
            {
                foreach (int id in matchedIds)
                    categoryIds.Add(id);
                continue;
            }

            error = $"--scene-categories: unknown category '{part}'. Use a category ID (e.g. -2000011), BuiltInCategory name (e.g. OST_Walls), or English category name (e.g. Walls).";
            categoryIds = null;
            return false;
        }
        return true;
    }

    private static Dictionary<string, List<int>> BuildDisplayCategoryLookup()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (BuiltInCategory cat in Enum.GetValues<BuiltInCategory>())
        {
            if (!cat.TryGetCategoryName(out string? display) || string.IsNullOrWhiteSpace(display))
                continue;
            if (!map.TryGetValue(display, out var list))
            {
                list = [];
                map[display] = list;
            }
            int id = (int)cat;
            if (!list.Contains(id))
                list.Add(id);
        }
        return map;
    }
}
