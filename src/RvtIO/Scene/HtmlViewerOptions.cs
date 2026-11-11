namespace RvtIO.Scene;

/// <summary>
/// Options controlling standalone 3D HTML viewer generation via <see cref="HtmlViewerExporter"/>.
/// </summary>
public sealed class HtmlViewerOptions : SceneExportOptions
{
    /// <summary>
    /// Initial visual theme of the generated HTML viewer (<c>"dark"</c> or <c>"light"</c>). Default is <c>"dark"</c>.
    /// </summary>
    public string InitialTheme { get; set; } = "dark";

    /// <summary>
    /// Whether datum grid lines are visible initially when the viewer opens. Default is <c>true</c>.
    /// </summary>
    public bool ShowGridsByDefault { get; set; } = true;
}
