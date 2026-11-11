using RvtIO.Model;

namespace RvtIO.Scene;

/// <summary>
/// Options controlling how a <see cref="ModelScene"/> is built and exported from a Revit document.
/// </summary>
public class SceneExportOptions
{
    /// <summary>
    /// Optional scene title. When null or empty, the project name or file name is used.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Optional subset of Revit element IDs to include in the scene. Null includes all eligible physical elements.
    /// </summary>
    public IReadOnlySet<int>? ElementIds { get; set; }

    /// <summary>
    /// Optional subset of category IDs (such as <c>(int)BuiltInCategory.OST_Walls</c>) to include in the scene.
    /// Null includes all eligible physical categories.
    /// </summary>
    public IReadOnlySet<int>? CategoryIds { get; set; }

    /// <summary>
    /// Optional caller predicate to select which elements are included in the scene.
    /// </summary>
    public Func<Element, bool>? ElementFilter { get; set; }

    /// <summary>
    /// Include element parameter metadata in exported elements. Default is <c>true</c>.
    /// </summary>
    public bool IncludeParameters { get; set; } = true;

    /// <summary>
    /// Include inherited type parameters when <see cref="IncludeParameters"/> is <c>true</c>. Default is <c>true</c>.
    /// </summary>
    public bool IncludeTypeParameters { get; set; } = true;

    /// <summary>
    /// Optional case-insensitive allow-list of parameter names or display captions to export.
    /// When null, all resolved readable parameters up to <see cref="MaxParametersPerElement"/> are included.
    /// </summary>
    public IReadOnlySet<string>? IncludedParameterNames { get; set; }

    /// <summary>
    /// Maximum number of parameters exported per element (0 or negative for unlimited). Default is 64.
    /// </summary>
    public int MaxParametersPerElement { get; set; } = 64;

    /// <summary>
    /// Include datum grid lines (<c>OST_Grids</c>) in the exported scene. Default is <c>true</c>.
    /// </summary>
    public bool IncludeGrids { get; set; } = true;

    /// <summary>
    /// Include translucent spatial volumes for rooms and MEP spaces (<c>OST_Rooms</c>, <c>OST_MEPSpaces</c>).
    /// Default is <c>false</c>.
    /// </summary>
    public bool IncludeRooms { get; set; }

    /// <summary>
    /// Transform model coordinates by <see cref="RevitFileInfo.SharedTransform"/> into shared/survey coordinates
    /// before computing the relative-to-center (<see cref="ModelScene.RtcOffset"/>) origin. Default is <c>false</c>.
    /// </summary>
    public bool ApplySharedCoordinates { get; set; }

    /// <summary>
    /// Optional <see cref="RvtIO.SkipLog"/> collecting elements omitted from the scene export.
    /// </summary>
    public SkipLog? SkipLog { get; set; }

    /// <summary>
    /// Optional writer for export diagnostic messages. Null (the default) is silent.
    /// </summary>
    public TextWriter? DiagnosticOutput { get; set; }
}
