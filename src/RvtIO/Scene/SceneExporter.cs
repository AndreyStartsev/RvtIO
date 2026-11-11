using RvtIO.Model;

namespace RvtIO.Scene;

/// <summary>
/// Builds and exports browser-ready 3D scenes (<see cref="ModelScene"/>) from Revit documents
/// in either the versioned <c>rvtio-scene/1</c> JSON contract or binary glTF 2.0 (<c>.glb</c>).
/// </summary>
public static class SceneExporter
{
    /// <summary>
    /// Builds an in-memory <see cref="ModelScene"/> from an opened <see cref="RvtDocument"/>.
    /// </summary>
    /// <param name="document">The source Revit document.</param>
    /// <param name="options">Optional filtering, metadata, coordinate, and diagnostic settings.</param>
    /// <returns>The constructed <see cref="ModelScene"/>.</returns>
    public static ModelScene Build(RvtDocument document, SceneExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return SceneBuilder.Build(document.RawData, options);
    }

    /// <summary>
    /// Builds an in-memory <see cref="ModelScene"/> from decoded <see cref="RevitFileInfo"/> data.
    /// </summary>
    /// <param name="fileInfo">The decoded Revit file data.</param>
    /// <param name="options">Optional filtering, metadata, coordinate, and diagnostic settings.</param>
    /// <returns>The constructed <see cref="ModelScene"/>.</returns>
    public static ModelScene Build(RevitFileInfo fileInfo, SceneExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileInfo);
        return SceneBuilder.Build(fileInfo, options);
    }

    /// <summary>
    /// Reads a Revit <c>.rvt</c> or <c>.rfa</c> file from disk with 3D geometry decoding enabled and builds a <see cref="ModelScene"/>.
    /// </summary>
    /// <param name="rvtPath">Path to the <c>.rvt</c> or <c>.rfa</c> file.</param>
    /// <param name="options">Optional scene export options.</param>
    /// <param name="readerOptions">Optional file reader options. When null, <see cref="RvtReaderOptions.R22Layout"/> and <see cref="RvtReaderOptions.SeparateFacesWithDistinctOpenings"/> are enabled automatically.</param>
    /// <returns>The constructed <see cref="ModelScene"/>.</returns>
    public static ModelScene OpenAndBuild(string rvtPath, SceneExportOptions? options = null, RvtReaderOptions? readerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rvtPath);
        var effectiveReaderOptions = readerOptions ?? new RvtReaderOptions
        {
            R22Layout = true,
            SeparateFacesWithDistinctOpenings = true,
            SystemFamilyNameLocale = "ENU",
            DiagnosticOutput = options?.DiagnosticOutput,
        };
        var doc = RvtDocument.Open(rvtPath, effectiveReaderOptions);
        return Build(doc, options);
    }

    /// <summary>
    /// Writes a <see cref="ModelScene"/> as a versioned <c>rvtio-scene/1</c> UTF-8 JSON stream.
    /// </summary>
    /// <param name="scene">The scene to serialize.</param>
    /// <param name="output">Target writable stream.</param>
    /// <param name="indented">True to format JSON with indentation; false (default) for compact output.</param>
    public static void ExportJson(ModelScene scene, Stream output, bool indented = false) =>
        SceneJsonSerializer.Write(scene, output, indented);

    /// <summary>
    /// Writes a <see cref="ModelScene"/> as a versioned <c>rvtio-scene/1</c> UTF-8 JSON file.
    /// </summary>
    /// <param name="scene">The scene to serialize.</param>
    /// <param name="outputPath">Target file path.</param>
    /// <param name="indented">True to format JSON with indentation; false (default) for compact output.</param>
    public static void ExportJson(ModelScene scene, string outputPath, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(outputPath);
        SceneJsonSerializer.Write(scene, fs, indented);
    }

    /// <summary>
    /// Deserializes a <see cref="ModelScene"/> from a <c>rvtio-scene/1</c> UTF-8 JSON stream.
    /// </summary>
    /// <param name="input">Readable stream containing <c>rvtio-scene/1</c> JSON.</param>
    /// <returns>The deserialized <see cref="ModelScene"/>.</returns>
    public static ModelScene LoadJson(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return SceneJsonSerializer.Read(input);
    }

    /// <summary>
    /// Writes a <see cref="ModelScene"/> as a binary glTF 2.0 (<c>.glb</c>) stream.
    /// </summary>
    /// <param name="scene">The scene to export.</param>
    /// <param name="output">Target writable stream.</param>
    /// <param name="convertZUpToYUp">When true (default), rotates the root glTF node from Revit Z-up to standard glTF Y-up.</param>
    public static void ExportGlb(ModelScene scene, Stream output, bool convertZUpToYUp = true) =>
        GlbSceneWriter.Write(scene, output, convertZUpToYUp);

    /// <summary>
    /// Writes a <see cref="ModelScene"/> as a binary glTF 2.0 (<c>.glb</c>) file.
    /// </summary>
    /// <param name="scene">The scene to export.</param>
    /// <param name="outputPath">Target <c>.glb</c> file path.</param>
    /// <param name="convertZUpToYUp">When true (default), rotates the root glTF node from Revit Z-up to standard glTF Y-up.</param>
    public static void ExportGlb(ModelScene scene, string outputPath, bool convertZUpToYUp = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(outputPath);
        GlbSceneWriter.Write(scene, fs, convertZUpToYUp);
    }
}
