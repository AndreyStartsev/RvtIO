using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RvtIO.BuiltIns;
using RvtIO.Cli;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Tests for Issue #25: self-contained offline 3D HTML viewer, XSS safety, and scene CLI options.</summary>
public class HtmlViewerExporterTests
{
    [Fact]
    public void ExportHtml_ProducesSelfContainedDocumentWithZeroExternalNetworkDependencies()
    {
        var info = SceneExportTests.CreateSampleBuildingModel();
        var options = new HtmlViewerOptions
        {
            Title = "Sample Office Pavilion - 3D Review",
            InitialTheme = "light",
            ShowGridsByDefault = true,
            IncludeRooms = true,
        };
        var scene = SceneExporter.Build(info, options);
        string html = HtmlViewerExporter.ExportToString(scene, options);

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>Sample Office Pavilion - 3D Review", html, StringComparison.Ordinal);
        Assert.Contains("data-theme=\"light\"", html, StringComparison.Ordinal);

        // Zero external scripts, stylesheets, fonts, images, or https:// URLs
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link ", html, StringComparison.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, @"https?://[^\s""'<>]+", RegexOptions.IgnoreCase))
        {
            // Only the W3C inline SVG XML namespace identifier is permitted; never a network resource
            Assert.Equal("http://www.w3.org/2000/svg", m.Value);
        }

        // Extract embedded JSON payload and verify full rvtio-scene/1 round-trip
        string embeddedJson = ExtractEmbeddedSceneJson(html);
        var decodedScene = SceneJsonSerializer.ReadFromString(embeddedJson);
        Assert.Equal(ModelScene.CurrentSchema, decodedScene.Schema);
        Assert.Equal(scene.Elements.Count, decodedScene.Elements.Count);
        Assert.Equal(scene.Meshes.Count, decodedScene.Meshes.Count);
        Assert.Equal(scene.Instances.Count, decodedScene.Instances.Count);

        // Verify viewer controls and WebGL capabilities are present in the single-file bundle
        Assert.Contains("webgl2", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewcube", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("section", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Isolate", html, StringComparison.Ordinal);
        Assert.Contains("Measure", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportHtml_NeutralizesUntrustedXssPayloadsAcrossAllModelStrings()
    {
        const string scriptBreakout = "</script><script>window.__rvtio_xss = 1;</script>";
        const string imgOnError = "<img src=x onerror=\"window.__rvtio_xss=2\"> & 'quotes'";

        var level = new Element
        {
            Id = 1,
            Name = $"Lvl {scriptBreakout}",
            IsNativeLevel = true,
            CategoryId = (int)BuiltInCategory.OST_Levels,
            Category = "OST_Levels",
        };

        var mat = new Element
        {
            Id = 2,
            Name = $"Mat {imgOnError}",
            CategoryId = (int)BuiltInCategory.OST_Materials,
            Category = "OST_Materials",
            Parameters = [SceneExportTests.MakeIntParam(-1001201, "MATERIAL_PARAM_COLOR", 0x00808080)],
        };

        var grid = new Element
        {
            Id = 3,
            Name = $"G{scriptBreakout}",
            CategoryId = (int)BuiltInCategory.OST_Grids,
            Category = "OST_Grids",
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, 0, 0], EndPoint = [10, 0, 0] },
        };

        var wall = new Element
        {
            Id = 10,
            UniqueId = $"uid-{scriptBreakout}",
            Name = $"Wall {scriptBreakout} {imgOnError}",
            FamilyName = $"Fam {scriptBreakout}",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Geometry = SceneExportTests.CreateBoxGeometry(0, 0, 0, 8, 1, 9, renderStyleId: 2),
            Parameters =
            [
                SceneExportTests.MakeElementIdParam(-1001107, "WALL_BASE_CONSTRAINT", 1),
                SceneExportTests.MakeStringParam(7001, $"Param {imgOnError}", $"Value {scriptBreakout} {imgOnError}"),
            ],
        };

        var info = new RevitFileInfo
        {
            ProjectInfo = new ProjectInfo { Name = $"Project {scriptBreakout}" },
            Elements = [level, mat, grid, wall],
        };

        var options = new HtmlViewerOptions
        {
            Title = $"Title {scriptBreakout} {imgOnError}",
        };

        var scene = SceneExporter.Build(info, options);
        string html = HtmlViewerExporter.ExportToString(scene, options);

        // 1. Raw XSS payloads MUST NOT appear unescaped anywhere in the HTML document
        Assert.DoesNotContain(scriptBreakout, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>window.__rvtio_xss", html, StringComparison.OrdinalIgnoreCase);

        // 2. Exactly two <script> blocks exist in the entire HTML file (the JSON data block + the viewer script)
        Assert.Equal(2, Regex.Matches(html, @"<script\b", RegexOptions.IgnoreCase).Count);
        Assert.Equal(2, Regex.Matches(html, @"</script>", RegexOptions.IgnoreCase).Count);

        // 3. Parsing the embedded JSON recovers the exact original strings losslessly for safe .textContent rendering
        string embeddedJson = ExtractEmbeddedSceneJson(html);
        var reloaded = SceneJsonSerializer.ReadFromString(embeddedJson);
        var reloadedWall = Assert.Single(reloaded.Elements);
        Assert.Equal(wall.Name, reloadedWall.Name);
        Assert.Equal(level.Name, reloadedWall.Level);
        var allProps = reloadedWall.PropertyGroups.SelectMany(g => g.Properties).ToList();
        var customProp = Assert.Single(allProps, p => p.Name == $"Param {imgOnError}");
        Assert.Equal($"Value {scriptBreakout} {imgOnError}", customProp.Value);
        Assert.Contains(allProps, p => p.Name == "Base Constraint" && p.Value == level.Name);
    }

    [Fact]
    public void ExportHtml_HandlesEmptyAndUndecodedModelsGracefully()
    {
        var emptyScene = SceneExporter.Build(new RevitFileInfo());
        string emptyHtml = HtmlViewerExporter.ExportToString(emptyScene);
        var parsedEmpty = SceneJsonSerializer.ReadFromString(ExtractEmbeddedSceneJson(emptyHtml));
        Assert.Empty(parsedEmpty.Elements);
        Assert.NotEmpty(parsedEmpty.Completeness.Notes);

        var undecodedInfo = new RevitFileInfo
        {
            GeometryUndecoded = true,
            Elements =
            [
                new Element { Id = 55, Name = "Wall 55", CategoryId = (int)BuiltInCategory.OST_Walls, Category = "OST_Walls" },
            ],
        };
        var undecodedScene = SceneExporter.Build(undecodedInfo);
        string undecodedHtml = HtmlViewerExporter.ExportToString(undecodedScene);
        var parsedUndecoded = SceneJsonSerializer.ReadFromString(ExtractEmbeddedSceneJson(undecodedHtml));
        Assert.True(parsedUndecoded.Completeness.IsPartialGeometry);
        Assert.NotEmpty(parsedUndecoded.Completeness.Notes);
        Assert.Contains("3D geometry was not decoded", parsedUndecoded.Completeness.Notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExportHtml_ScalesToThousandsOfElementsAndReportsPartialNewerFormatAccurately()
    {
        var symbol = new Element
        {
            Id = 500,
            Name = "M_Sprinkler_Pendent",
            FamilyName = "M_Sprinkler",
            CategoryId = (int)BuiltInCategory.OST_Sprinklers,
            Category = "OST_Sprinklers",
            Geometry = SceneExportTests.CreateBoxGeometry(-0.2, -0.2, -0.4, 0.2, 0.2, 0),
        };

        var elements = new List<Element>(2502) { symbol };
        for (int i = 0; i < 2500; i++)
        {
            double x = (i % 50) * 10.0;
            double y = (i / 50) * 10.0;
            elements.Add(new Element
            {
                Id = 1000 + i,
                TypeId = 500,
                UniqueId = $"spr-{i:D4}",
                Name = $"Sprinkler #{i + 1}",
                FamilyName = "M_Sprinkler",
                CategoryId = (int)BuiltInCategory.OST_Sprinklers,
                Category = "OST_Sprinklers",
                Geometry = new GeometryData
                {
                    Instances = [new GeoInstance { SymbolId = 500, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, x, y, 9.0] }],
                },
            });
        }

        var info = new RevitFileInfo
        {
            GeometryUndecoded = true, // e.g. a few newer-release analytical records were skipped while 3D instances decoded
            Elements = elements,
        };

        var scene = SceneExporter.Build(info);
        Assert.Equal(2500, scene.Completeness.ExportedElements);
        Assert.Equal(2500, scene.Completeness.InstancedElements);
        Assert.Equal(1, scene.Completeness.ReusableMeshCount);
        Assert.True(scene.Completeness.IsPartialGeometry);
        Assert.Contains("Exported 2500 3D elements", scene.Completeness.Notes[0], StringComparison.Ordinal);
        Assert.Contains(scene.Completeness.Notes, n => n.Contains("Some newer-format records", StringComparison.Ordinal));
        Assert.DoesNotContain(scene.Completeness.Notes, n => n.Contains("3D geometry was not decoded", StringComparison.Ordinal));

        string html = HtmlViewerExporter.ExportToString(scene);
        Assert.Contains("TREE_PAGE_SIZE", html, StringComparison.Ordinal);
        Assert.Contains("opaqueDrawOrder", html, StringComparison.Ordinal);
        Assert.Contains("rayAabbEntry", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SceneCliOptions_ParsesValidFlagsAndRejectsMalformedArguments()
    {
        Assert.True(SceneCliOptions.TryParse(
            [
                "in.rvt", "out.html",
                "--viewer-title", "My Model",
                "--viewer-theme", "light",
                "--scene-categories", "Walls,OST_Doors,-2000014",
                "--scene-ids", "101, 102, 205",
                "--scene-parameter-names", "Mark,Fire Rating",
                "--scene-shared-coordinates",
                "--scene-include-rooms",
                "--scene-no-grids",
            ],
            out var parsed,
            out string? error));

        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("My Model", parsed.Title);
        Assert.False(parsed.DarkTheme);
        Assert.True(parsed.ApplySharedCoordinates);
        Assert.True(parsed.IncludeRooms);
        Assert.False(parsed.IncludeGrids);
        Assert.NotNull(parsed.CategoryIds);
        Assert.Contains((int)BuiltInCategory.OST_Walls, parsed.CategoryIds);
        Assert.Contains((int)BuiltInCategory.OST_Doors, parsed.CategoryIds);
        Assert.Contains(-2000014, parsed.CategoryIds);
        Assert.Equal([101, 102, 205], parsed.ElementIds!.OrderBy(x => x).ToArray());
        Assert.Contains("Mark", parsed.ParameterNames!);
        Assert.Contains("Fire Rating", parsed.ParameterNames!);

        // Reject invalid theme
        Assert.False(SceneCliOptions.TryParse(["in.rvt", "out.html", "--viewer-theme", "neon"], out _, out string? themeErr));
        Assert.Contains("dark or light", themeErr, StringComparison.Ordinal);

        // Reject unknown category
        Assert.False(SceneCliOptions.TryParse(["in.rvt", "out.glb", "--scene-categories", "NotARealCategory"], out _, out string? catErr));
        Assert.Contains("NotARealCategory", catErr, StringComparison.Ordinal);

        // Reject non-integer element ID
        Assert.False(SceneCliOptions.TryParse(["in.rvt", "out.glb", "--scene-ids", "100,abc"], out _, out string? idErr));
        Assert.Contains("abc", idErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CliProcess_ValidatesSceneArgumentsAndPreservesReportAndExitCodes()
    {
        // --help lists .glb, .html and --scene-json
        var (helpCode, helpOut, _) = RunCli("--help");
        Assert.Equal(RvtExitCodes.Success, helpCode);
        Assert.Contains("output.glb", helpOut, StringComparison.Ordinal);
        Assert.Contains("output.html", helpOut, StringComparison.Ordinal);
        Assert.Contains("--scene-json", helpOut, StringComparison.Ordinal);

        // Mutually exclusive flags exit with Usage (1)
        Assert.Equal(RvtExitCodes.Usage, RunCli("model.rvt", "model.glb", "--compact-json").ExitCode);
        Assert.Equal(RvtExitCodes.Usage, RunCli("model.rvt", "model.html", "--json-full").ExitCode);
        Assert.Equal(RvtExitCodes.Usage, RunCli("model.rvt", "model.html", "--scene-json").ExitCode);
        Assert.Equal(RvtExitCodes.Usage, RunCli("model.rvt", "model.glb", "--ifc-phase", "last").ExitCode);
        Assert.Equal(RvtExitCodes.Usage, RunCli("model.rvt", "model.html", "--viewer-theme", "sepia").ExitCode);

        // Unreadable file with .glb and .html output writes failed --report and exits with InputError (2)
        string tempDir = Path.Combine(Path.GetTempPath(), "rvtio-scene-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string badRvt = Path.Combine(tempDir, "invalid.rvt");
            string outHtml = Path.Combine(tempDir, "viewer.html");
            string reportPath = Path.Combine(tempDir, "report.json");
            File.WriteAllText(badRvt, "not a compound file");

            var (code, _, _) = RunCli(badRvt, outHtml, "--report", reportPath);
            Assert.Equal(RvtExitCodes.InputError, code);
            Assert.False(File.Exists(outHtml));
            using var reportJson = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal("failed", reportJson.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string ExtractEmbeddedSceneJson(string html)
    {
        const string openTag = "<script id=\"rvtio-scene-data\" type=\"application/json\">";
        int start = html.IndexOf(openTag, StringComparison.Ordinal);
        Assert.True(start >= 0, "Embedded rvtio-scene-data script block was not found.");
        start += openTag.Length;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(end > start, "Closing </script> tag for rvtio-scene-data was not found.");
        return html[start..end];
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The CLI process timed out.");
        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
