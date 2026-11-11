using System.Net;
using System.Text;
using RvtIO.Model;

namespace RvtIO.Scene;

/// <summary>
/// Exports a self-contained, single-file offline 3D HTML viewer embedding a <see cref="ModelScene"/>
/// and a zero-dependency WebGL inspection application.
/// </summary>
public static class HtmlViewerExporter
{
    /// <summary>
    /// Writes a self-contained 3D HTML viewer for <paramref name="scene"/> to <paramref name="output"/>.
    /// </summary>
    /// <param name="scene">The model scene to embed.</param>
    /// <param name="output">Target UTF-8 writable stream.</param>
    /// <param name="options">Optional viewer presentation settings.</param>
    public static void Export(ModelScene scene, Stream output, HtmlViewerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new HtmlViewerOptions();

        string safeTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(options.Title) ? scene.Title : options.Title!);
        string initialTheme = string.Equals(options.InitialTheme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        string showGrids = options.ShowGridsByDefault ? "true" : "false";

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var writer = new StreamWriter(output, utf8NoBom, bufferSize: 16384, leaveOpen: true);

        writer.Write(BuildHtmlPrefix(safeTitle, initialTheme));
        writer.Flush();

        // Write XSS-safe JSON directly into <script id="rvtio-scene-data" type="application/json">
        SceneJsonSerializer.Write(scene, output, indented: false);
        output.Flush();

        writer.Write(BuildHtmlSuffix(showGrids));
        writer.Flush();
    }

    /// <summary>
    /// Writes a self-contained 3D HTML viewer for <paramref name="scene"/> to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="scene">The model scene to embed.</param>
    /// <param name="outputPath">Target <c>.html</c> file path.</param>
    /// <param name="options">Optional viewer presentation settings.</param>
    public static void Export(ModelScene scene, string outputPath, HtmlViewerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(outputPath);
        Export(scene, fs, options);
    }

    /// <summary>
    /// Returns the complete self-contained 3D HTML viewer document for <paramref name="scene"/> as a string.
    /// </summary>
    /// <param name="scene">The model scene to embed.</param>
    /// <param name="options">Optional viewer presentation settings.</param>
    /// <returns>Complete HTML5 document string.</returns>
    public static string ExportToString(ModelScene scene, HtmlViewerOptions? options = null)
    {
        using var ms = new MemoryStream();
        Export(scene, ms, options);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Builds a <see cref="ModelScene"/> from <paramref name="document"/> and writes a self-contained 3D HTML viewer to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="document">Opened Revit document.</param>
    /// <param name="outputPath">Target <c>.html</c> file path.</param>
    /// <param name="options">Optional scene and viewer options.</param>
    public static void Export(RvtDocument document, string outputPath, HtmlViewerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scene = SceneExporter.Build(document, options);
        Export(scene, outputPath, options);
    }

    /// <summary>
    /// Reads a Revit <c>.rvt</c> or <c>.rfa</c> file from disk and writes a self-contained 3D HTML viewer to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="rvtPath">Source <c>.rvt</c> or <c>.rfa</c> file path.</param>
    /// <param name="outputPath">Target <c>.html</c> file path.</param>
    /// <param name="options">Optional scene and viewer options.</param>
    /// <param name="readerOptions">Optional file reader options.</param>
    public static void Export(string rvtPath, string outputPath, HtmlViewerOptions? options = null, RvtReaderOptions? readerOptions = null)
    {
        var scene = SceneExporter.OpenAndBuild(rvtPath, options, readerOptions);
        Export(scene, outputPath, options);
    }

    private static string BuildHtmlPrefix(string htmlEncodedTitle, string initialTheme) => $$"""
<!DOCTYPE html>
<html lang="en" data-theme="{{initialTheme}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{htmlEncodedTitle}} — RvtIO 3D Viewer</title>
<style>
:root {
  --bg-app: #0f141c;
  --bg-panel: #161d27;
  --bg-elev: #1e2736;
  --bg-hover: #273346;
  --bg-active: rgba(56, 189, 248, 0.16);
  --border: #2a3648;
  --text: #e6edf5;
  --text-muted: #93a3b8;
  --accent: #38bdf8;
  --accent-strong: #0284c7;
  --warn-bg: rgba(245, 158, 11, 0.16);
  --warn-text: #fbbf24;
  --ok-bg: rgba(16, 185, 129, 0.16);
  --ok-text: #34d399;
  --canvas-top: #151e2c;
  --canvas-bot: #0b1017;
  --shadow: 0 8px 28px rgba(0, 0, 0, 0.42);
}
html[data-theme="light"] {
  --bg-app: #eef2f7;
  --bg-panel: #ffffff;
  --bg-elev: #f3f6fa;
  --bg-hover: #e6ecf4;
  --bg-active: rgba(2, 132, 199, 0.14);
  --border: #d5dee9;
  --text: #182230;
  --text-muted: #5c6c80;
  --accent: #0284c7;
  --accent-strong: #0369a1;
  --warn-bg: rgba(217, 119, 6, 0.14);
  --warn-text: #b45309;
  --ok-bg: rgba(5, 150, 105, 0.13);
  --ok-text: #047857;
  --canvas-top: #dce6f2;
  --canvas-bot: #f5f8fc;
  --shadow: 0 8px 24px rgba(15, 23, 42, 0.12);
}
* { box-sizing: border-box; margin: 0; padding: 0; }
html, body { width: 100%; height: 100%; overflow: hidden; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; font-size: 13px; color: var(--text); background: var(--bg-app); user-select: none; }
button, input { font-family: inherit; font-size: inherit; color: inherit; }
#app { display: flex; flex-direction: column; width: 100%; height: 100%; }
#topbar { height: 44px; min-height: 44px; background: var(--bg-panel); border-bottom: 1px solid var(--border); display: flex; align-items: center; justify-content: space-between; padding: 0 14px; gap: 12px; z-index: 20; }
.brand { display: flex; align-items: center; gap: 10px; min-width: 0; }
.brand-badge { font-weight: 700; font-size: 11px; letter-spacing: 0.06em; padding: 3px 7px; border-radius: 5px; background: var(--accent); color: #081018; }
#doc-title { font-weight: 600; font-size: 14px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.top-actions { display: flex; align-items: center; gap: 8px; }
.pill-btn { display: inline-flex; align-items: center; gap: 6px; padding: 5px 10px; border-radius: 6px; border: 1px solid var(--border); background: var(--bg-elev); cursor: pointer; font-size: 12px; transition: background 0.12s; }
.pill-btn:hover { background: var(--bg-hover); }
.pill-btn.status-ok { background: var(--ok-bg); color: var(--ok-text); border-color: transparent; }
.pill-btn.status-warn { background: var(--warn-bg); color: var(--warn-text); border-color: transparent; }
#workspace { flex: 1; display: flex; min-height: 0; position: relative; }
.sidebar { width: 290px; min-width: 220px; max-width: 420px; background: var(--bg-panel); display: flex; flex-direction: column; min-height: 0; z-index: 10; }
.sidebar.left { border-right: 1px solid var(--border); }
.sidebar.right { width: 320px; border-left: 1px solid var(--border); }
.sidebar.collapsed { display: none; }
.panel-hdr { padding: 10px 12px; border-bottom: 1px solid var(--border); display: flex; align-items: center; justify-content: space-between; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
.search-wrap { padding: 8px 10px; border-bottom: 1px solid var(--border); display: flex; flex-direction: column; gap: 6px; }
.search-input { width: 100%; padding: 6px 9px; border-radius: 6px; border: 1px solid var(--border); background: var(--bg-elev); outline: none; user-select: text; }
.search-input:focus { border-color: var(--accent); }
.vis-bar { display: flex; gap: 4px; flex-wrap: wrap; }
.mini-btn { flex: 1; padding: 4px 6px; border-radius: 5px; border: 1px solid var(--border); background: var(--bg-elev); cursor: pointer; font-size: 11px; text-align: center; white-space: nowrap; }
.mini-btn:hover { background: var(--bg-hover); }
.mini-btn:disabled { opacity: 0.45; cursor: default; }
.scroll-area { flex: 1; overflow-y: auto; padding: 6px 0; }
.cat-block { border-bottom: 1px solid rgba(148, 163, 184, 0.08); }
.cat-row { display: flex; align-items: center; gap: 6px; padding: 6px 10px; cursor: pointer; font-weight: 600; }
.cat-row:hover { background: var(--bg-hover); }
.caret { width: 14px; font-size: 10px; color: var(--text-muted); text-align: center; flex-shrink: 0; }
.cat-label { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.count-badge { font-size: 11px; color: var(--text-muted); background: var(--bg-elev); padding: 1px 6px; border-radius: 10px; }
.eye-btn { border: none; background: transparent; cursor: pointer; padding: 2px 5px; border-radius: 4px; color: var(--text-muted); font-size: 12px; }
.eye-btn:hover { background: var(--bg-elev); color: var(--text); }
.eye-btn.hidden-state { opacity: 0.4; text-decoration: line-through; }
.elem-list { display: none; padding-bottom: 4px; }
.elem-list.open { display: block; }
.elem-row { display: flex; align-items: center; gap: 6px; padding: 4px 10px 4px 26px; cursor: pointer; font-size: 12px; }
.elem-row:hover { background: var(--bg-hover); }
.elem-row.selected { background: var(--bg-active); color: var(--accent); font-weight: 600; }
.elem-row.dimmed { opacity: 0.45; }
.elem-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.elem-id { font-size: 10px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
.more-row { display: block; width: calc(100% - 36px); margin: 4px 10px 4px 26px; padding: 4px 8px; border-radius: 5px; border: 1px dashed var(--border); background: var(--bg-elev); color: var(--accent); font-size: 11px; font-weight: 500; cursor: pointer; text-align: center; }
.more-row:hover { background: var(--bg-hover); border-color: var(--accent); }
#viewport { flex: 1; position: relative; min-width: 0; background: linear-gradient(180deg, var(--canvas-top), var(--canvas-bot)); overflow: hidden; }
#gl-canvas, #hud-canvas { position: absolute; inset: 0; width: 100%; height: 100%; display: block; }
#hud-canvas { pointer-events: none; }
#toolbar { position: absolute; bottom: 14px; left: 50%; transform: translateX(-50%); display: flex; align-items: center; gap: 5px; padding: 6px 8px; border-radius: 10px; background: var(--bg-panel); border: 1px solid var(--border); box-shadow: var(--shadow); z-index: 15; flex-wrap: wrap; max-width: calc(100% - 24px); }
.tool-btn { padding: 6px 10px; border-radius: 6px; border: 1px solid transparent; background: transparent; cursor: pointer; font-size: 12px; font-weight: 500; display: inline-flex; align-items: center; gap: 5px; white-space: nowrap; }
.tool-btn:hover { background: var(--bg-hover); }
.tool-btn.active { background: var(--bg-active); border-color: var(--accent); color: var(--accent); }
.tool-sep { width: 1px; height: 20px; background: var(--border); margin: 0 2px; }
#viewcube-wrap { position: absolute; top: 14px; right: 14px; width: 92px; display: flex; flex-direction: column; align-items: center; gap: 8px; z-index: 15; }
.vc-scene { width: 68px; height: 68px; perspective: 240px; }
#viewcube { width: 68px; height: 68px; position: relative; transform-style: preserve-3d; }
.vc-face { position: absolute; width: 68px; height: 68px; border: 1px solid var(--border); background: var(--bg-panel); opacity: 0.92; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 700; letter-spacing: 0.05em; cursor: pointer; transition: background 0.12s, color 0.12s; }
.vc-face:hover { background: var(--accent); color: #081018; }
#section-panel { position: absolute; left: 14px; top: 14px; width: 280px; background: var(--bg-panel); border: 1px solid var(--border); border-radius: 10px; box-shadow: var(--shadow); padding: 12px; display: none; flex-direction: column; gap: 8px; z-index: 16; }
#section-panel.open { display: flex; }
.sec-row { display: flex; align-items: center; gap: 6px; font-size: 11px; }
.sec-lbl { width: 38px; font-weight: 600; color: var(--text-muted); }
.sec-row input[type="range"] { flex: 1; accent-color: var(--accent); }
#measure-banner { position: absolute; top: 14px; left: 50%; transform: translateX(-50%); background: var(--bg-panel); border: 1px solid var(--accent); border-radius: 8px; padding: 8px 12px; box-shadow: var(--shadow); display: none; flex-direction: column; gap: 3px; z-index: 15; font-size: 12px; }
#measure-banner.open { display: flex; }
#empty-overlay { position: absolute; inset: 0; display: none; align-items: center; justify-content: center; pointer-events: none; padding: 24px; z-index: 12; }
#empty-overlay.open { display: flex; }
.empty-card { max-width: 440px; background: var(--bg-panel); border: 1px solid var(--border); border-radius: 12px; padding: 20px; text-align: center; box-shadow: var(--shadow); pointer-events: auto; }
.empty-card h3 { font-size: 15px; margin-bottom: 8px; }
.empty-card p { color: var(--text-muted); line-height: 1.5; }
.prop-empty { padding: 24px 16px; color: var(--text-muted); text-align: center; line-height: 1.5; }
.prop-group { border-bottom: 1px solid var(--border); }
.prop-group-hdr { padding: 8px 12px; font-weight: 600; font-size: 11px; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); background: var(--bg-elev); cursor: pointer; display: flex; justify-content: space-between; }
.prop-table { width: 100%; border-collapse: collapse; user-select: text; }
.prop-table tr { border-top: 1px solid rgba(148, 163, 184, 0.1); }
.prop-table td { padding: 5px 12px; font-size: 12px; vertical-align: top; word-break: break-word; }
.prop-table td.k { width: 44%; color: var(--text-muted); }
.prop-table td.v { width: 56%; font-weight: 500; }
#modal-backdrop { position: fixed; inset: 0; background: rgba(0, 0, 0, 0.55); display: none; align-items: center; justify-content: center; z-index: 50; padding: 20px; }
#modal-backdrop.open { display: flex; }
.modal-card { width: 100%; max-width: 520px; max-height: 82vh; overflow-y: auto; background: var(--bg-panel); border: 1px solid var(--border); border-radius: 12px; padding: 18px 20px; box-shadow: var(--shadow); display: flex; flex-direction: column; gap: 12px; user-select: text; }
.modal-hdr { display: flex; align-items: center; justify-content: space-between; font-size: 15px; font-weight: 600; }
.stat-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
.stat-box { background: var(--bg-elev); border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; }
.stat-val { font-size: 16px; font-weight: 700; font-variant-numeric: tabular-nums; }
.stat-lbl { font-size: 11px; color: var(--text-muted); margin-top: 2px; }
.note-list { list-style: disc; padding-left: 18px; color: var(--text-muted); line-height: 1.5; display: flex; flex-direction: column; gap: 4px; }
</style>
</head>
<body>
<div id="app">
  <header id="topbar">
    <div class="brand">
      <button id="btn-toggle-left" class="pill-btn" title="Toggle Model Browser">Browser</button>
      <span class="brand-badge">RvtIO</span>
      <span id="doc-title">{{htmlEncodedTitle}}</span>
    </div>
    <div class="top-actions">
      <button id="btn-completeness" class="pill-btn" title="View model completeness and omitted content summary"></button>
      <button id="btn-theme" class="pill-btn" title="Switch Dark / Light theme">Theme</button>
      <button id="btn-toggle-right" class="pill-btn" title="Toggle Properties Inspector">Properties</button>
    </div>
  </header>
  <div id="workspace">
    <aside id="left-sidebar" class="sidebar left">
      <div class="panel-hdr">
        <span>Model Hierarchy</span>
        <span id="tree-summary"></span>
      </div>
      <div class="search-wrap">
        <input id="tree-search" class="search-input" type="search" placeholder="Search name, category, type, #ID...">
        <div class="vis-bar">
          <button id="btn-show-all" class="mini-btn" title="Show all elements (U)">Show All</button>
          <button id="btn-isolate" class="mini-btn" disabled title="Isolate selected element (I)">Isolate</button>
          <button id="btn-hide-sel" class="mini-btn" disabled title="Hide selected element (H)">Hide</button>
          <button id="btn-fit-sel" class="mini-btn" disabled title="Zoom to selected element">Zoom</button>
        </div>
      </div>
      <div id="tree-root" class="scroll-area"></div>
    </aside>

    <main id="viewport">
      <canvas id="gl-canvas"></canvas>
      <canvas id="hud-canvas"></canvas>

      <div id="measure-banner">
        <strong id="measure-title">Measure Tool</strong>
        <span id="measure-readout">Click two points on model surfaces</span>
      </div>

      <div id="viewcube-wrap">
        <div class="vc-scene">
          <div id="viewcube">
            <div class="vc-face" data-view="FRONT" style="transform: rotateX(-90deg) translateZ(34px);">FRONT</div>
            <div class="vc-face" data-view="BACK" style="transform: rotateX(-90deg) rotateY(180deg) translateZ(34px);">BACK</div>
            <div class="vc-face" data-view="RIGHT" style="transform: rotateX(-90deg) rotateY(90deg) translateZ(34px);">RIGHT</div>
            <div class="vc-face" data-view="LEFT" style="transform: rotateX(-90deg) rotateY(-90deg) translateZ(34px);">LEFT</div>
            <div class="vc-face" data-view="TOP" style="transform: translateZ(34px);">TOP</div>
            <div class="vc-face" data-view="BOTTOM" style="transform: rotateY(180deg) translateZ(34px);">BTM</div>
          </div>
        </div>
        <button id="btn-iso" class="mini-btn" style="width:68px;">ISO</button>
      </div>

      <div id="section-panel">
        <div style="display:flex;justify-content:space-between;align-items:center;">
          <strong style="font-size:12px;">Section Box &amp; Planes</strong>
          <label style="font-size:11px;display:flex;align-items:center;gap:4px;cursor:pointer;">
            <input id="chk-section" type="checkbox"> Active
          </label>
        </div>
        <div class="sec-row"><span class="sec-lbl">X Min</span><input id="sec-xmin" type="range" min="0" max="1000" value="0"></div>
        <div class="sec-row"><span class="sec-lbl">X Max</span><input id="sec-xmax" type="range" min="0" max="1000" value="1000"></div>
        <div class="sec-row"><span class="sec-lbl">Y Min</span><input id="sec-ymin" type="range" min="0" max="1000" value="0"></div>
        <div class="sec-row"><span class="sec-lbl">Y Max</span><input id="sec-ymax" type="range" min="0" max="1000" value="1000"></div>
        <div class="sec-row"><span class="sec-lbl">Z Min</span><input id="sec-zmin" type="range" min="0" max="1000" value="0"></div>
        <div class="sec-row"><span class="sec-lbl">Z Max</span><input id="sec-zmax" type="range" min="0" max="1000" value="1000"></div>
        <div class="vis-bar">
          <button id="btn-sec-sel" class="mini-btn">Box Selection</button>
          <button id="btn-sec-reset" class="mini-btn">Reset Cut</button>
        </div>
      </div>

      <div id="empty-overlay">
        <div class="empty-card">
          <h3>No 3D Geometry to Display</h3>
          <p id="empty-reason"></p>
        </div>
      </div>

      <div id="toolbar">
        <button id="btn-fit" class="tool-btn" title="Fit entire model (F)">Fit Model</button>
        <button id="btn-proj" class="tool-btn" title="Toggle Perspective / Orthographic (P)">Perspective</button>
        <div class="tool-sep"></div>
        <button id="btn-style-shaded" class="tool-btn active" title="Shaded materials">Shaded</button>
        <button id="btn-style-clay" class="tool-btn" title="Architectural white clay shading">Clay</button>
        <button id="btn-style-wire" class="tool-btn" title="Wireframe edges">Wireframe</button>
        <div class="tool-sep"></div>
        <button id="btn-section" class="tool-btn" title="Section Planes &amp; Box">Section</button>
        <button id="btn-grids" class="tool-btn" title="Toggle Datum Grids">Grids</button>
        <button id="btn-measure" class="tool-btn" title="Point-to-point 3D Measurement">Measure</button>
      </div>
    </main>

    <aside id="right-sidebar" class="sidebar right">
      <div class="panel-hdr">
        <span>Element Properties</span>
        <span id="sel-badge"></span>
      </div>
      <div class="search-wrap">
        <input id="prop-filter" class="search-input" type="search" placeholder="Filter properties...">
      </div>
      <div id="prop-root" class="scroll-area">
        <div class="prop-empty">Click any 3D element in the viewport or select an item in the Model Hierarchy to inspect its identity and parameters.</div>
      </div>
    </aside>
  </div>
</div>

<div id="modal-backdrop">
  <div class="modal-card">
    <div class="modal-hdr">
      <span>Scene Completeness Summary</span>
      <button id="btn-modal-close" class="mini-btn" style="flex:0;padding:4px 10px;">Close</button>
    </div>
    <div id="modal-stats" class="stat-grid"></div>
    <div>
      <strong style="font-size:12px;">Summary Notes</strong>
      <ul id="modal-notes" class="note-list" style="margin-top:6px;"></ul>
    </div>
    <div id="modal-omitted-wrap">
      <strong style="font-size:12px;">Non-Visual / Omitted Records</strong>
      <table id="modal-omitted" class="prop-table" style="margin-top:6px;border:1px solid var(--border);"></table>
    </div>
  </div>
</div>

<script id="rvtio-scene-data" type="application/json">
""";

    private static string BuildHtmlSuffix(string showGridsLiteral) => $$"""
</script>
<script>
(function() {
  "use strict";

  function makeEl(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function b64ToBytes(b64) {
    var bin = atob(b64);
    var len = bin.length;
    var out = new Uint8Array(len);
    for (var i = 0; i < len; i++) out[i] = bin.charCodeAt(i);
    return out;
  }

  function decodeFloat32(b64) {
    var bytes = b64ToBytes(b64);
    return new Float32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >>> 2);
  }

  function decodeIndices(b64, indexType) {
    var bytes = b64ToBytes(b64);
    if (indexType === "uint16") return new Uint16Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >>> 1);
    return new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >>> 2);
  }

  var rawJson = document.getElementById("rvtio-scene-data").textContent;
  var scene = JSON.parse(rawJson);

  var titleNode = document.getElementById("doc-title");
  if (scene.title) titleNode.textContent = scene.title;

  var state = {
    selectedElemIdx: -1,
    hiddenElements: new Uint8Array(scene.elements.length),
    renderStyle: "shaded", // "shaded" | "clay" | "wire"
    ortho: false,
    showGrids: {{showGridsLiteral}} && scene.grids && scene.grids.length > 0,
    sectionActive: false,
    clipMin: [0, 0, 0],
    clipMax: [0, 0, 0],
    measureActive: false,
    measurePts: [],
    propFilterText: "",
    yaw: -Math.PI * 0.25,
    pitch: 0.52,
    distance: 20,
    target: [0, 0, 0],
    sceneCenter: [0, 0, 0],
    sceneRadius: 10
  };

  // Compute scene center & radius in RTC metres
  var bMin = scene.boundsMin || [0, 0, 0];
  var bMax = scene.boundsMax || [0, 0, 0];
  var padX = Math.max(0.25, (bMax[0] - bMin[0]) * 0.02);
  var padY = Math.max(0.25, (bMax[1] - bMin[1]) * 0.02);
  var padZ = Math.max(0.25, (bMax[2] - bMin[2]) * 0.02);
  var fullClipMin = [bMin[0] - padX, bMin[1] - padY, bMin[2] - padZ];
  var fullClipMax = [bMax[0] + padX, bMax[1] + padY, bMax[2] + padZ];
  state.clipMin = fullClipMin.slice();
  state.clipMax = fullClipMax.slice();

  function fitBounds(minArr, maxArr) {
    var cx = (minArr[0] + maxArr[0]) * 0.5;
    var cy = (minArr[1] + maxArr[1]) * 0.5;
    var cz = (minArr[2] + maxArr[2]) * 0.5;
    var dx = maxArr[0] - minArr[0], dy = maxArr[1] - minArr[1], dz = maxArr[2] - minArr[2];
    var r = Math.max(0.5, Math.sqrt(dx * dx + dy * dy + dz * dz) * 0.5);
    state.target = [cx, cy, cz];
    state.distance = r * 2.15;
  }

  fitBounds(bMin, bMax);
  state.sceneCenter = state.target.slice();
  state.sceneRadius = Math.max(1.0, state.distance / 2.15);

  // Completeness UI setup
  var comp = scene.completeness || {};
  var compBtn = document.getElementById("btn-completeness");
  compBtn.className = "pill-btn " + (comp.isPartialGeometry ? "status-warn" : "status-ok");
  compBtn.textContent = (comp.exportedElements || 0) + " 3D elements" + (comp.isPartialGeometry ? " (Partial)" : "");

  function populateCompletenessModal() {
    var statsRoot = document.getElementById("modal-stats");
    statsRoot.textContent = "";
    var cards = [
      ["Exported 3D Elements", comp.exportedElements || 0],
      ["Instanced Elements", comp.instancedElements || 0],
      ["Unique Meshes", comp.reusableMeshCount || 0],
      ["Placed Instances", comp.instanceCount || 0],
      ["Triangles", (comp.triangleCount || 0).toLocaleString()],
      ["Units / RTC", (scene.units || "m") + (scene.rtcOffset && (scene.rtcOffset[0] || scene.rtcOffset[1] || scene.rtcOffset[2]) ? " (Centered)" : "")]
    ];
    for (var i = 0; i < cards.length; i++) {
      var box = makeEl("div", "stat-box");
      box.appendChild(makeEl("div", "stat-val", cards[i][1]));
      box.appendChild(makeEl("div", "stat-lbl", cards[i][0]));
      statsRoot.appendChild(box);
    }

    var notesList = document.getElementById("modal-notes");
    notesList.textContent = "";
    var notes = comp.notes && comp.notes.length ? comp.notes : ["Scene loaded."];
    for (var n = 0; n < notes.length; n++) {
      notesList.appendChild(makeEl("li", "", notes[n]));
    }

    var omittedWrap = document.getElementById("modal-omitted-wrap");
    var omittedTbl = document.getElementById("modal-omitted");
    omittedTbl.textContent = "";
    var groups = comp.omittedGroups || [];
    if (!groups.length) {
      omittedWrap.style.display = "none";
    } else {
      omittedWrap.style.display = "block";
      for (var g = 0; g < groups.length; g++) {
        var tr = document.createElement("tr");
        tr.appendChild(makeEl("td", "k", groups[g].reason));
        tr.appendChild(makeEl("td", "v", groups[g].count));
        omittedTbl.appendChild(tr);
      }
    }
  }
  populateCompletenessModal();

  if (!scene.instances || scene.instances.length === 0) {
    document.getElementById("empty-overlay").classList.add("open");
    var emptyMsg = (comp.notes && comp.notes.length) ? comp.notes[0] : "This document contains no representable 3D geometry.";
    document.getElementById("empty-reason").textContent = emptyMsg;
  }

  // Matrix & Ray Math
  function mat4Identity() {
    return new Float32Array([1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1]);
  }

  function mat4Multiply(a, b) {
    var out = new Float32Array(16);
    for (var c = 0; c < 4; c++) {
      var b0 = b[c*4], b1 = b[c*4+1], b2 = b[c*4+2], b3 = b[c*4+3];
      out[c*4]   = a[0]*b0 + a[4]*b1 + a[8]*b2  + a[12]*b3;
      out[c*4+1] = a[1]*b0 + a[5]*b1 + a[9]*b2  + a[13]*b3;
      out[c*4+2] = a[2]*b0 + a[6]*b1 + a[10]*b2 + a[14]*b3;
      out[c*4+3] = a[3]*b0 + a[7]*b1 + a[11]*b2 + a[15]*b3;
    }
    return out;
  }

  function mat4Perspective(fovRad, aspect, near, far) {
    var f = 1.0 / Math.tan(fovRad * 0.5);
    var nf = 1.0 / (near - far);
    return new Float32Array([
      f / aspect, 0, 0, 0,
      0, f, 0, 0,
      0, 0, (far + near) * nf, -1,
      0, 0, (2 * far * near) * nf, 0
    ]);
  }

  function mat4Ortho(left, right, bottom, top, near, far) {
    var lr = 1 / (left - right), bt = 1 / (bottom - top), nf = 1 / (near - far);
    return new Float32Array([
      -2 * lr, 0, 0, 0,
      0, -2 * bt, 0, 0,
      0, 0, 2 * nf, 0,
      (left + right) * lr, (top + bottom) * bt, (far + near) * nf, 1
    ]);
  }

  function mat4LookAtZUp(eye, target) {
    var zx = eye[0] - target[0], zy = eye[1] - target[1], zz = eye[2] - target[2];
    var zLen = Math.hypot(zx, zy, zz) || 1;
    zx /= zLen; zy /= zLen; zz /= zLen;

    var upX = 0, upY = 0, upZ = 1;
    if (Math.abs(zz) > 0.999) { upX = 0; upY = zz > 0 ? 1 : -1; upZ = 0; }

    var xx = upY * zz - upZ * zy;
    var xy = upZ * zx - upX * zz;
    var xz = upX * zy - upY * zx;
    var xLen = Math.hypot(xx, xy, xz) || 1;
    xx /= xLen; xy /= xLen; xz /= xLen;

    var yx = zy * xz - zz * xy;
    var yy = zz * xx - zx * xz;
    var yz = zx * xy - zy * xx;

    return new Float32Array([
      xx, yx, zx, 0,
      xy, yy, zy, 0,
      xz, yz, zz, 0,
      -(xx*eye[0] + xy*eye[1] + xz*eye[2]),
      -(yx*eye[0] + yy*eye[1] + yz*eye[2]),
      -(zx*eye[0] + zy*eye[1] + zz*eye[2]),
      1
    ]);
  }

  function mat4Invert(m) {
    var inv = new Float32Array(16);
    inv[0] = m[5]*m[10]*m[15] - m[5]*m[11]*m[14] - m[9]*m[6]*m[15] + m[9]*m[7]*m[14] + m[13]*m[6]*m[11] - m[13]*m[7]*m[10];
    inv[4] = -m[4]*m[10]*m[15] + m[4]*m[11]*m[14] + m[8]*m[6]*m[15] - m[8]*m[7]*m[14] - m[12]*m[6]*m[11] + m[12]*m[7]*m[10];
    inv[8] = m[4]*m[9]*m[15] - m[4]*m[11]*m[13] - m[8]*m[5]*m[15] + m[8]*m[7]*m[13] + m[12]*m[5]*m[11] - m[12]*m[7]*m[9];
    inv[12] = -m[4]*m[9]*m[14] + m[4]*m[10]*m[13] + m[8]*m[5]*m[14] - m[8]*m[6]*m[13] - m[12]*m[5]*m[10] + m[12]*m[6]*m[9];
    inv[1] = -m[1]*m[10]*m[15] + m[1]*m[11]*m[14] + m[9]*m[2]*m[15] - m[9]*m[3]*m[14] - m[13]*m[2]*m[11] + m[13]*m[3]*m[10];
    inv[5] = m[0]*m[10]*m[15] - m[0]*m[11]*m[14] - m[8]*m[2]*m[15] + m[8]*m[3]*m[14] + m[12]*m[2]*m[11] - m[12]*m[3]*m[10];
    inv[9] = -m[0]*m[9]*m[15] + m[0]*m[11]*m[13] + m[8]*m[1]*m[15] - m[8]*m[3]*m[13] - m[12]*m[1]*m[11] + m[12]*m[3]*m[9];
    inv[13] = m[0]*m[9]*m[14] - m[0]*m[10]*m[13] - m[8]*m[1]*m[14] + m[8]*m[2]*m[13] + m[12]*m[1]*m[10] - m[12]*m[2]*m[9];
    inv[2] = m[1]*m[6]*m[15] - m[1]*m[7]*m[14] - m[5]*m[2]*m[15] + m[5]*m[3]*m[14] + m[13]*m[2]*m[7] - m[13]*m[3]*m[6];
    inv[6] = -m[0]*m[6]*m[15] + m[0]*m[7]*m[14] + m[4]*m[2]*m[15] - m[4]*m[3]*m[14] - m[12]*m[2]*m[7] + m[12]*m[3]*m[6];
    inv[10] = m[0]*m[5]*m[15] - m[0]*m[7]*m[13] - m[4]*m[1]*m[15] + m[4]*m[3]*m[13] + m[12]*m[1]*m[7] - m[12]*m[3]*m[5];
    inv[14] = -m[0]*m[5]*m[14] + m[0]*m[6]*m[13] + m[4]*m[1]*m[14] - m[4]*m[2]*m[13] - m[12]*m[1]*m[6] + m[12]*m[2]*m[5];
    inv[3] = -m[1]*m[6]*m[11] + m[1]*m[7]*m[10] + m[5]*m[2]*m[11] - m[5]*m[3]*m[10] - m[9]*m[2]*m[7] + m[9]*m[3]*m[6];
    inv[7] = m[0]*m[6]*m[11] - m[0]*m[7]*m[10] - m[4]*m[2]*m[11] + m[4]*m[3]*m[10] + m[8]*m[2]*m[7] - m[8]*m[3]*m[6];
    inv[11] = -m[0]*m[5]*m[11] + m[0]*m[7]*m[9] + m[4]*m[1]*m[11] - m[4]*m[3]*m[9] - m[8]*m[1]*m[7] + m[8]*m[3]*m[5];
    inv[15] = m[0]*m[5]*m[10] - m[0]*m[6]*m[9] - m[4]*m[1]*m[10] + m[4]*m[2]*m[9] + m[8]*m[1]*m[6] - m[8]*m[2]*m[5];

    var det = m[0]*inv[0] + m[1]*inv[4] + m[2]*inv[8] + m[3]*inv[12];
    if (Math.abs(det) < 1e-14) return null;
    var invDet = 1.0 / det;
    for (var i = 0; i < 16; i++) inv[i] *= invDet;
    return inv;
  }

  function mat3NormalFromMat4(m) {
    var a00 = m[0], a01 = m[1], a02 = m[2];
    var a10 = m[4], a11 = m[5], a12 = m[6];
    var a20 = m[8], a21 = m[9], a22 = m[10];
    var b01 = a22 * a11 - a12 * a21;
    var b11 = -a22 * a10 + a12 * a20;
    var b21 = a21 * a10 - a11 * a20;
    var det = a00 * b01 + a01 * b11 + a02 * b21;
    if (Math.abs(det) < 1e-12) return new Float32Array([1,0,0, 0,1,0, 0,0,1]);
    var id = 1.0 / det;
    return new Float32Array([
      b01 * id,
      (-a22 * a01 + a02 * a21) * id,
      (a12 * a01 - a02 * a11) * id,
      b11 * id,
      (a22 * a00 - a02 * a20) * id,
      (-a12 * a00 + a02 * a10) * id,
      b21 * id,
      (-a21 * a00 + a01 * a20) * id,
      (a11 * a00 - a01 * a10) * id
    ]);
  }

  // WebGL Setup
  var canvas = document.getElementById("gl-canvas");
  var hudCanvas = document.getElementById("hud-canvas");
  var hudCtx = hudCanvas.getContext("2d");
  var gl = canvas.getContext("webgl2", { antialias: true, alpha: true })
        || canvas.getContext("webgl", { antialias: true, alpha: true });

  var isWebGL2 = (typeof WebGL2RenderingContext !== "undefined") && (gl instanceof WebGL2RenderingContext);
  var uintExt = isWebGL2 ? true : !!(gl && gl.getExtension("OES_element_index_uint"));

  var vsSource = [
    "attribute vec3 aPos;",
    "attribute vec3 aNormal;",
    "uniform mat4 uViewProj;",
    "uniform mat4 uModel;",
    "uniform mat3 uNormalMat;",
    "varying vec3 vWorldPos;",
    "varying vec3 vNormal;",
    "void main() {",
    "  vec4 wp = uModel * vec4(aPos, 1.0);",
    "  vWorldPos = wp.xyz;",
    "  vNormal = normalize(uNormalMat * aNormal);",
    "  gl_Position = uViewProj * wp;",
    "}"
  ].join("\n");

  var fsSource = [
    "precision highp float;",
    "varying vec3 vWorldPos;",
    "varying vec3 vNormal;",
    "uniform vec4 uBaseColor;",
    "uniform vec3 uCamPos;",
    "uniform int uClipEnabled;",
    "uniform vec3 uClipMin;",
    "uniform vec3 uClipMax;",
    "uniform int uSelected;",
    "uniform int uUnlit;",
    "void main() {",
    "  if (uClipEnabled == 1) {",
    "    if (vWorldPos.x < uClipMin.x || vWorldPos.x > uClipMax.x ||",
    "        vWorldPos.y < uClipMin.y || vWorldPos.y > uClipMax.y ||",
    "        vWorldPos.z < uClipMin.z || vWorldPos.z > uClipMax.z) discard;",
    "  }",
    "  if (uUnlit == 1) {",
    "    gl_FragColor = uBaseColor;",
    "    return;",
    "  }",
    "  vec3 n = normalize(vNormal);",
    "  if (!gl_FrontFacing) n = -n;",
    "  vec3 viewDir = normalize(uCamPos - vWorldPos);",
    "  vec3 keyDir = normalize(vec3(0.52, -0.44, 0.73));",
    "  vec3 fillDir = normalize(vec3(-0.60, 0.55, 0.35));",
    "  float key = max(dot(n, keyDir), 0.0);",
    "  float fill = max(dot(n, fillDir), 0.0);",
    "  float hemi = 0.5 + 0.5 * n.z;",
    "  float shade = 0.32 + 0.48 * key + 0.16 * fill + 0.10 * hemi;",
    "  vec3 halfVec = normalize(keyDir + viewDir);",
    "  float spec = pow(max(dot(n, halfVec), 0.0), 28.0) * 0.14;",
    "  vec3 col = uBaseColor.rgb * shade + vec3(spec);",
    "  if (uSelected == 1) {",
    "    float rim = 1.0 - max(dot(n, viewDir), 0.0);",
    "    col = mix(col, vec3(0.15, 0.78, 0.98), 0.48 + 0.25 * rim);",
    "  }",
    "  gl_FragColor = vec4(clamp(col, 0.0, 1.0), uBaseColor.a);",
    "}"
  ].join("\n");

  function compileShader(type, src) {
    var s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    return s;
  }

  var prog = null, loc = {};
  var gpuProtos = [];
  var gridBuffer = null, gridVertexCount = 0;

  if (gl) {
    prog = gl.createProgram();
    gl.attachShader(prog, compileShader(gl.VERTEX_SHADER, vsSource));
    gl.attachShader(prog, compileShader(gl.FRAGMENT_SHADER, fsSource));
    gl.linkProgram(prog);
    gl.useProgram(prog);

    loc = {
      aPos: gl.getAttribLocation(prog, "aPos"),
      aNormal: gl.getAttribLocation(prog, "aNormal"),
      uViewProj: gl.getUniformLocation(prog, "uViewProj"),
      uModel: gl.getUniformLocation(prog, "uModel"),
      uNormalMat: gl.getUniformLocation(prog, "uNormalMat"),
      uBaseColor: gl.getUniformLocation(prog, "uBaseColor"),
      uCamPos: gl.getUniformLocation(prog, "uCamPos"),
      uClipEnabled: gl.getUniformLocation(prog, "uClipEnabled"),
      uClipMin: gl.getUniformLocation(prog, "uClipMin"),
      uClipMax: gl.getUniformLocation(prog, "uClipMax"),
      uSelected: gl.getUniformLocation(prog, "uSelected"),
      uUnlit: gl.getUniformLocation(prog, "uUnlit")
    };

    // Decode & upload reusable mesh prototypes once
    for (var m = 0; m < scene.meshes.length; m++) {
      var mDef = scene.meshes[m];
      var posArr = decodeFloat32(mDef.positionsB64);
      var normArr = decodeFloat32(mDef.normalsB64);
      var idxArr = decodeIndices(mDef.indicesB64, mDef.indexType);

      var vboPos = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, vboPos);
      gl.bufferData(gl.ARRAY_BUFFER, posArr, gl.STATIC_DRAW);

      var vboNorm = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, vboNorm);
      gl.bufferData(gl.ARRAY_BUFFER, normArr, gl.STATIC_DRAW);

      var ibo = gl.createBuffer();
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ibo);
      var glIdxType = gl.UNSIGNED_SHORT;
      var uploadIdx = idxArr;
      if (mDef.indexType !== "uint16") {
        if (uintExt) {
          glIdxType = gl.UNSIGNED_INT;
        } else {
          uploadIdx = new Uint16Array(idxArr.length);
          for (var k = 0; k < idxArr.length; k++) uploadIdx[k] = idxArr[k] & 0xFFFF;
        }
      }
      gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, uploadIdx, gl.STATIC_DRAW);

      gpuProtos.push({
        positions: posArr,
        normals: normArr,
        indices: idxArr,
        vboPos: vboPos,
        vboNorm: vboNorm,
        ibo: ibo,
        indexCount: uploadIdx.length,
        glIndexType: glIdxType,
        wireIbo: null,
        wireCount: 0
      });
    }

    // Upload datum grid lines
    if (scene.grids && scene.grids.length > 0) {
      var gVerts = new Float32Array(scene.grids.length * 6);
      for (var gi = 0; gi < scene.grids.length; gi++) {
        var gr = scene.grids[gi];
        gVerts[gi*6]   = gr.start[0]; gVerts[gi*6+1] = gr.start[1]; gVerts[gi*6+2] = gr.start[2];
        gVerts[gi*6+3] = gr.end[0];   gVerts[gi*6+4] = gr.end[1];   gVerts[gi*6+5] = gr.end[2];
      }
      gridBuffer = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, gridBuffer);
      gl.bufferData(gl.ARRAY_BUFFER, gVerts, gl.STATIC_DRAW);
      gridVertexCount = scene.grids.length * 2;
    }
  }

  function ensureWireframeBuffer(proto) {
    if (proto.wireIbo) return;
    var triIdx = proto.indices;
    var edgeArr = (proto.glIndexType === gl.UNSIGNED_INT)
      ? new Uint32Array(triIdx.length * 2)
      : new Uint16Array(triIdx.length * 2);
    var w = 0;
    for (var i = 0; i + 2 < triIdx.length; i += 3) {
      var a = triIdx[i], b = triIdx[i+1], c = triIdx[i+2];
      edgeArr[w++] = a; edgeArr[w++] = b;
      edgeArr[w++] = b; edgeArr[w++] = c;
      edgeArr[w++] = c; edgeArr[w++] = a;
    }
    proto.wireIbo = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, proto.wireIbo);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, edgeArr, gl.STATIC_DRAW);
    proto.wireCount = w;
  }

  // Precompute instance matrices, inverse matrices (for raycasting), centers, and mesh-batched draw order
  var runtimeInstances = [];
  var opaqueDrawOrder = [];
  for (var ii = 0; ii < scene.instances.length; ii++) {
    var srcInst = scene.instances[ii];
    var mArr = new Float32Array(srcInst.matrix);
    var invMat = mat4Invert(mArr) || mat4Identity();
    var normMat = mat3NormalFromMat4(mArr);
    var cx = (srcInst.boundsMin[0] + srcInst.boundsMax[0]) * 0.5;
    var cy = (srcInst.boundsMin[1] + srcInst.boundsMax[1]) * 0.5;
    var cz = (srcInst.boundsMin[2] + srcInst.boundsMax[2]) * 0.5;
    runtimeInstances.push({
      meshIndex: srcInst.meshIndex,
      elementIndex: srcInst.elementIndex,
      elementId: srcInst.elementId,
      materialIndex: srcInst.materialIndex,
      matrix: mArr,
      invMatrix: invMat,
      normalMatrix: normMat,
      boundsMin: srcInst.boundsMin,
      boundsMax: srcInst.boundsMax,
      center: [cx, cy, cz],
      distSq: 0
    });
    opaqueDrawOrder.push(ii);
  }
  opaqueDrawOrder.sort(function(ia, ib) {
    var dMesh = runtimeInstances[ia].meshIndex - runtimeInstances[ib].meshIndex;
    return dMesh !== 0 ? dMesh : (runtimeInstances[ia].materialIndex - runtimeInstances[ib].materialIndex);
  });

  function getCameraEye() {
    var cp = Math.cos(state.pitch), sp = Math.sin(state.pitch);
    var cy = Math.cos(state.yaw), sy = Math.sin(state.yaw);
    return [
      state.target[0] + state.distance * cp * cy,
      state.target[1] + state.distance * cp * sy,
      state.target[2] + state.distance * sp
    ];
  }

  var lastViewProj = mat4Identity();
  var lastInvViewProj = mat4Identity();

  function updateViewCubeCss() {
    var vc = document.getElementById("viewcube");
    var rotXDeg = (state.pitch * 180 / Math.PI) - 90;
    var rotZDeg = -(state.yaw * 180 / Math.PI) - 90;
    vc.style.transform = "rotateX(" + rotXDeg.toFixed(2) + "deg) rotateZ(" + rotZDeg.toFixed(2) + "deg)";
  }

  function projectToScreen(pt, width, height) {
    var x = pt[0], y = pt[1], z = pt[2];
    var m = lastViewProj;
    var cx = m[0]*x + m[4]*y + m[8]*z + m[12];
    var cy = m[1]*x + m[5]*y + m[9]*z + m[13];
    var cw = m[3]*x + m[7]*y + m[11]*z + m[15];
    if (cw <= 1e-5) return null;
    var ndcX = cx / cw, ndcY = cy / cw;
    if (ndcX < -1.2 || ndcX > 1.2 || ndcY < -1.2 || ndcY > 1.2) return null;
    return [(ndcX * 0.5 + 0.5) * width, (0.5 - ndcY * 0.5) * height];
  }

  function drawHudOverlay(width, height) {
    hudCtx.clearRect(0, 0, width, height);
    var isLight = document.documentElement.getAttribute("data-theme") === "light";

    // Draw grid bubble labels when grids are shown
    if (state.showGrids && scene.grids && scene.grids.length > 0) {
      hudCtx.font = "600 11px -apple-system, Segoe UI, sans-serif";
      hudCtx.textAlign = "center";
      hudCtx.textBaseline = "middle";
      for (var i = 0; i < scene.grids.length; i++) {
        var g = scene.grids[i];
        var scr = projectToScreen(g.start, width, height);
        if (!scr) continue;
        hudCtx.beginPath();
        hudCtx.arc(scr[0], scr[1], 10, 0, Math.PI * 2);
        hudCtx.fillStyle = isLight ? "rgba(255,255,255,0.9)" : "rgba(22,29,39,0.9)";
        hudCtx.fill();
        hudCtx.lineWidth = 1.25;
        hudCtx.strokeStyle = isLight ? "#0284c7" : "#38bdf8";
        hudCtx.stroke();
        hudCtx.fillStyle = isLight ? "#182230" : "#e6edf5";
        hudCtx.fillText(String(g.name || ""), scr[0], scr[1]);
      }
    }

    // Draw 3D measurement markers & line
    if (state.measurePts.length > 0) {
      var s0 = projectToScreen(state.measurePts[0], width, height);
      if (s0) {
        if (state.measurePts.length > 1) {
          var s1 = projectToScreen(state.measurePts[1], width, height);
          if (s1) {
            hudCtx.beginPath();
            hudCtx.moveTo(s0[0], s0[1]);
            hudCtx.lineTo(s1[0], s1[1]);
            hudCtx.strokeStyle = "#f59e0b";
            hudCtx.lineWidth = 2.2;
            hudCtx.stroke();

            hudCtx.beginPath();
            hudCtx.arc(s1[0], s1[1], 5, 0, Math.PI * 2);
            hudCtx.fillStyle = "#f59e0b";
            hudCtx.fill();
          }
        }
        hudCtx.beginPath();
        hudCtx.arc(s0[0], s0[1], 5, 0, Math.PI * 2);
        hudCtx.fillStyle = "#38bdf8";
        hudCtx.fill();
      }
    }
  }

  var rafPending = false;
  function requestRender() {
    if (!rafPending) {
      rafPending = true;
      requestAnimationFrame(renderFrame);
    }
  }

  function renderFrame() {
    rafPending = false;
    updateViewCubeCss();
    if (!gl || !prog) return;

    var dpr = Math.min(window.devicePixelRatio || 1, 2);
    var cssW = Math.max(1, canvas.clientWidth);
    var cssH = Math.max(1, canvas.clientHeight);
    var pxW = Math.floor(cssW * dpr), pxH = Math.floor(cssH * dpr);
    if (canvas.width !== pxW || canvas.height !== pxH) {
      canvas.width = pxW; canvas.height = pxH;
      hudCanvas.width = pxW; hudCanvas.height = pxH;
      hudCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    gl.viewport(0, 0, pxW, pxH);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);

    var aspect = cssW / cssH;
    var near = Math.max(0.02, Math.min(0.5, state.distance * 0.002));
    var far = Math.max(500, state.distance * 12 + state.sceneRadius * 8);
    var proj = state.ortho
      ? mat4Ortho(-state.distance * 0.55 * aspect, state.distance * 0.55 * aspect, -state.distance * 0.55, state.distance * 0.55, -far, far)
      : mat4Perspective(45 * Math.PI / 180, aspect, near, far);

    var eye = getCameraEye();
    var view = mat4LookAtZUp(eye, state.target);
    lastViewProj = mat4Multiply(proj, view);
    lastInvViewProj = mat4Invert(lastViewProj) || mat4Identity();

    gl.useProgram(prog);
    gl.uniformMatrix4fv(loc.uViewProj, false, lastViewProj);
    gl.uniform3f(loc.uCamPos, eye[0], eye[1], eye[2]);
    gl.uniform1i(loc.uClipEnabled, state.sectionActive ? 1 : 0);
    gl.uniform3f(loc.uClipMin, state.clipMin[0], state.clipMin[1], state.clipMin[2]);
    gl.uniform3f(loc.uClipMax, state.clipMax[0], state.clipMax[1], state.clipMax[2]);

    // Optional datum grid lines
    if (state.showGrids && gridBuffer && gridVertexCount > 0) {
      gl.depthMask(true);
      gl.disable(gl.BLEND);
      gl.uniformMatrix4fv(loc.uModel, false, mat4Identity());
      gl.uniformMatrix3fv(loc.uNormalMat, false, new Float32Array([1,0,0, 0,1,0, 0,0,1]));
      gl.uniform1i(loc.uSelected, 0);
      gl.uniform1i(loc.uUnlit, 1);
      var isLight = document.documentElement.getAttribute("data-theme") === "light";
      gl.uniform4f(loc.uBaseColor, isLight ? 0.35 : 0.45, isLight ? 0.45 : 0.58, isLight ? 0.58 : 0.74, 0.55);
      gl.bindBuffer(gl.ARRAY_BUFFER, gridBuffer);
      gl.enableVertexAttribArray(loc.aPos);
      gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 0, 0);
      if (loc.aNormal >= 0) gl.disableVertexAttribArray(loc.aNormal);
      gl.drawArrays(gl.LINES, 0, gridVertexCount);
    }

    gl.uniform1i(loc.uUnlit, state.renderStyle === "wire" ? 1 : 0);

    var opaqueList = [];
    var transparentList = [];
    for (var oi = 0; oi < opaqueDrawOrder.length; oi++) {
      var inst = runtimeInstances[opaqueDrawOrder[oi]];
      if (state.hiddenElements[inst.elementIndex]) continue;
      var mat = scene.materials[inst.materialIndex] || { color: [0.78, 0.8, 0.82], opacity: 1 };
      var op = (state.renderStyle === "clay" || state.renderStyle === "wire") ? 1.0 : (mat.opacity !== undefined ? mat.opacity : 1.0);
      if (op < 0.995) {
        var dx = inst.center[0] - eye[0], dy = inst.center[1] - eye[1], dz = inst.center[2] - eye[2];
        inst.distSq = dx*dx + dy*dy + dz*dz;
        transparentList.push(inst);
      } else {
        opaqueList.push(inst);
      }
    }

    function drawInstanceList(list, isTransparentPass) {
      var currentMeshIdx = -1;
      var currentMatKey = -2;
      var currentSelFlag = -1;
      for (var k = 0; k < list.length; k++) {
        var inst = list[k];
        var proto = gpuProtos[inst.meshIndex];
        if (!proto) continue;

        if (inst.meshIndex !== currentMeshIdx) {
          currentMeshIdx = inst.meshIndex;
          gl.bindBuffer(gl.ARRAY_BUFFER, proto.vboPos);
          gl.enableVertexAttribArray(loc.aPos);
          gl.vertexAttribPointer(loc.aPos, 3, gl.FLOAT, false, 0, 0);

          if (loc.aNormal >= 0) {
            gl.bindBuffer(gl.ARRAY_BUFFER, proto.vboNorm);
            gl.enableVertexAttribArray(loc.aNormal);
            gl.vertexAttribPointer(loc.aNormal, 3, gl.FLOAT, false, 0, 0);
          }

          if (state.renderStyle === "wire") {
            ensureWireframeBuffer(proto);
            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, proto.wireIbo);
          } else {
            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, proto.ibo);
          }
        }

        gl.uniformMatrix4fv(loc.uModel, false, inst.matrix);
        gl.uniformMatrix3fv(loc.uNormalMat, false, inst.normalMatrix);

        var selFlag = inst.elementIndex === state.selectedElemIdx ? 1 : 0;
        if (selFlag !== currentSelFlag) {
          currentSelFlag = selFlag;
          gl.uniform1i(loc.uSelected, selFlag);
        }

        var matKey = selFlag ? -1 : inst.materialIndex;
        if (matKey !== currentMatKey) {
          currentMatKey = matKey;
          var mat = scene.materials[inst.materialIndex] || { color: [0.78, 0.8, 0.82], opacity: 1 };
          if (state.renderStyle === "clay") {
            gl.uniform4f(loc.uBaseColor, 0.88, 0.89, 0.91, 1.0);
          } else if (state.renderStyle === "wire") {
            if (selFlag) gl.uniform4f(loc.uBaseColor, 0.22, 0.74, 0.97, 1.0);
            else gl.uniform4f(loc.uBaseColor, mat.color[0], mat.color[1], mat.color[2], 1.0);
          } else {
            gl.uniform4f(loc.uBaseColor, mat.color[0], mat.color[1], mat.color[2], isTransparentPass ? mat.opacity : 1.0);
          }
        }

        if (state.renderStyle === "wire") {
          gl.drawElements(gl.LINES, proto.wireCount, proto.glIndexType, 0);
        } else {
          gl.drawElements(gl.TRIANGLES, proto.indexCount, proto.glIndexType, 0);
        }
      }
    }

    // Pass 1: Opaque
    gl.depthMask(true);
    gl.disable(gl.BLEND);
    drawInstanceList(opaqueList, false);

    // Pass 2: Transparent (sorted back-to-front)
    if (transparentList.length > 0) {
      transparentList.sort(function(a, b) { return b.distSq - a.distSq; });
      gl.enable(gl.BLEND);
      gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      gl.depthMask(false);
      drawInstanceList(transparentList, true);
      gl.depthMask(true);
      gl.disable(gl.BLEND);
    }

    drawHudOverlay(cssW, cssH);
  }

  // Exact 3D Raycasting (Clipped AABB broadphase + front-to-back early exit + instance-local Moller-Trumbore)
  function unprojectRay(clientX, clientY) {
    var rect = canvas.getBoundingClientRect();
    var nx = ((clientX - rect.left) / Math.max(1, rect.width)) * 2 - 1;
    var ny = 1 - ((clientY - rect.top) / Math.max(1, rect.height)) * 2;

    function unproj(zNdc) {
      var m = lastInvViewProj;
      var x = m[0]*nx + m[4]*ny + m[8]*zNdc + m[12];
      var y = m[1]*nx + m[5]*ny + m[9]*zNdc + m[13];
      var z = m[2]*nx + m[6]*ny + m[10]*zNdc + m[14];
      var w = m[3]*nx + m[7]*ny + m[11]*zNdc + m[15];
      var iw = Math.abs(w) > 1e-12 ? 1 / w : 1;
      return [x * iw, y * iw, z * iw];
    }

    var pNear = unproj(-1);
    var pFar = unproj(1);
    var dx = pFar[0] - pNear[0], dy = pFar[1] - pNear[1], dz = pFar[2] - pNear[2];
    var len = Math.hypot(dx, dy, dz) || 1;
    return { origin: pNear, dir: [dx / len, dy / len, dz / len] };
  }

  function rayAabbEntry(orig, dir, minB, maxB, maxDist) {
    var tmin = 0, tmax = maxDist;
    for (var a = 0; a < 3; a++) {
      var b0 = state.sectionActive ? Math.max(minB[a], state.clipMin[a]) : minB[a];
      var b1 = state.sectionActive ? Math.min(maxB[a], state.clipMax[a]) : maxB[a];
      if (b0 > b1) return -1;
      var invD = Math.abs(dir[a]) > 1e-12 ? 1.0 / dir[a] : (dir[a] >= 0 ? 1e12 : -1e12);
      var t0 = (b0 - orig[a]) * invD;
      var t1 = (b1 - orig[a]) * invD;
      if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; }
      if (t0 > tmin) tmin = t0;
      if (t1 < tmax) tmax = t1;
      if (tmax < tmin) return -1;
    }
    return tmin;
  }

  function pickScene(clientX, clientY) {
    var ray = unprojectRay(clientX, clientY);
    var orig = ray.origin, dir = ray.dir;
    var candidates = [];

    for (var i = 0; i < runtimeInstances.length; i++) {
      var inst = runtimeInstances[i];
      if (state.hiddenElements[inst.elementIndex]) continue;
      var tEntry = rayAabbEntry(orig, dir, inst.boundsMin, inst.boundsMax, Infinity);
      if (tEntry >= 0) candidates.push({ inst: inst, tmin: tEntry });
    }

    if (!candidates.length) return null;
    candidates.sort(function(a, b) { return a.tmin - b.tmin; });

    var bestDist = Infinity;
    var bestHit = null;

    for (var c = 0; c < candidates.length; c++) {
      var cand = candidates[c];
      if (cand.tmin >= bestDist) break;

      var inst = cand.inst;
      var proto = gpuProtos[inst.meshIndex];
      if (!proto) continue;

      // Transform ray into prototype-local space
      var im = inst.invMatrix;
      var lox = im[0]*orig[0] + im[4]*orig[1] + im[8]*orig[2] + im[12];
      var loy = im[1]*orig[0] + im[5]*orig[1] + im[9]*orig[2] + im[13];
      var loz = im[2]*orig[0] + im[6]*orig[1] + im[10]*orig[2] + im[14];

      var ldx = im[0]*dir[0] + im[4]*dir[1] + im[8]*dir[2];
      var ldy = im[1]*dir[0] + im[5]*dir[1] + im[9]*dir[2];
      var ldz = im[2]*dir[0] + im[6]*dir[1] + im[10]*dir[2];

      var pos = proto.positions;
      var idx = proto.indices;
      var mm = inst.matrix;

      for (var t = 0; t + 2 < idx.length; t += 3) {
        var i0 = idx[t]*3, i1 = idx[t+1]*3, i2 = idx[t+2]*3;
        var v0x = pos[i0], v0y = pos[i0+1], v0z = pos[i0+2];
        var e1x = pos[i1] - v0x, e1y = pos[i1+1] - v0y, e1z = pos[i1+2] - v0z;
        var e2x = pos[i2] - v0x, e2y = pos[i2+1] - v0y, e2z = pos[i2+2] - v0z;

        var px = ldy * e2z - ldz * e2y;
        var py = ldz * e2x - ldx * e2z;
        var pz = ldx * e2y - ldy * e2x;
        var det = e1x * px + e1y * py + e1z * pz;
        if (Math.abs(det) < 1e-10) continue;
        var invDet = 1.0 / det;

        var tx = lox - v0x, ty = loy - v0y, tz = loz - v0z;
        var u = (tx * px + ty * py + tz * pz) * invDet;
        if (u < 0 || u > 1) continue;

        var qx = ty * e1z - tz * e1y;
        var qy = tz * e1x - tx * e1z;
        var qz = tx * e1y - ty * e1x;
        var v = (ldx * qx + ldy * qy + ldz * qz) * invDet;
        if (v < 0 || u + v > 1) continue;

        var localT = (e2x * qx + e2y * qy + e2z * qz) * invDet;
        if (localT <= 1e-5) continue;

        var lhx = lox + ldx * localT, lhy = loy + ldy * localT, lhz = loz + ldz * localT;
        var whx = mm[0]*lhx + mm[4]*lhy + mm[8]*lhz + mm[12];
        var why = mm[1]*lhx + mm[5]*lhy + mm[9]*lhz + mm[13];
        var whz = mm[2]*lhx + mm[6]*lhy + mm[10]*lhz + mm[14];

        if (state.sectionActive) {
          if (whx < state.clipMin[0] || whx > state.clipMax[0] ||
              why < state.clipMin[1] || why > state.clipMax[1] ||
              whz < state.clipMin[2] || whz > state.clipMax[2]) continue;
        }

        var worldDist = (whx - orig[0])*dir[0] + (why - orig[1])*dir[1] + (whz - orig[2])*dir[2];
        if (worldDist > 1e-4 && worldDist < bestDist) {
          bestDist = worldDist;
          bestHit = { elementIndex: inst.elementIndex, point: [whx, why, whz] };
        }
      }
    }
    return bestHit;
  }

  // Orbit / Pan / Zoom / Click Interaction
  var dragMode = 0; // 0 none, 1 orbit, 2 pan
  var downX = 0, downY = 0, lastX = 0, lastY = 0, movedPx = 0;

  canvas.addEventListener("contextmenu", function(e) { e.preventDefault(); });

  canvas.addEventListener("pointerdown", function(e) {
    canvas.setPointerCapture(e.pointerId);
    downX = lastX = e.clientX;
    downY = lastY = e.clientY;
    movedPx = 0;
    dragMode = (e.button === 1 || e.button === 2 || e.shiftKey) ? 2 : 1;
  });

  canvas.addEventListener("pointermove", function(e) {
    if (!dragMode) return;
    var dx = e.clientX - lastX;
    var dy = e.clientY - lastY;
    lastX = e.clientX; lastY = e.clientY;
    movedPx += Math.abs(dx) + Math.abs(dy);

    if (dragMode === 1) {
      state.yaw -= dx * 0.007;
      state.pitch = Math.max(-1.53, Math.min(1.53, state.pitch + dy * 0.007));
    } else if (dragMode === 2) {
      var panScale = state.distance * 0.0014;
      var cy = Math.cos(state.yaw), sy = Math.sin(state.yaw);
      var rx = -sy, ry = cy;
      var sp = Math.sin(state.pitch), cp = Math.cos(state.pitch);
      var ux = -sp * cy, uy = -sp * sy, uz = cp;

      state.target[0] -= (rx * dx - ux * dy) * panScale;
      state.target[1] -= (ry * dx - uy * dy) * panScale;
      state.target[2] -= (-uz * dy) * panScale;
    }
    requestRender();
  });

  canvas.addEventListener("pointerup", function(e) {
    if (!dragMode) return;
    dragMode = 0;
    if (movedPx < 5 && e.button === 0) {
      var hit = pickScene(e.clientX, e.clientY);
      if (state.measureActive) {
        if (hit) {
          if (state.measurePts.length >= 2) state.measurePts = [];
          state.measurePts.push(hit.point);
          updateMeasureReadout();
          requestRender();
        }
      } else {
        selectElement(hit ? hit.elementIndex : -1, true);
      }
    }
  });

  canvas.addEventListener("dblclick", function(e) {
    var hit = pickScene(e.clientX, e.clientY);
    if (hit) {
      selectElement(hit.elementIndex, true);
      zoomToElement(hit.elementIndex);
    }
  });

  canvas.addEventListener("wheel", function(e) {
    e.preventDefault();
    var factor = Math.exp(e.deltaY * 0.0012);
    state.distance = Math.max(0.15, Math.min(state.sceneRadius * 50, state.distance * factor));
    requestRender();
  }, { passive: false });

  function updateMeasureReadout() {
    var r = document.getElementById("measure-readout");
    if (state.measurePts.length === 0) {
      r.textContent = "Click two points on model surfaces";
    } else if (state.measurePts.length === 1) {
      r.textContent = "Point 1 set — click second surface point";
    } else {
      var a = state.measurePts[0], b = state.measurePts[1];
      var dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
      var d = Math.hypot(dx, dy, dz);
      r.textContent = d.toFixed(3) + " m (" + (d * 1000).toFixed(0) + " mm)  |  dX " + dx.toFixed(2) + "  dY " + dy.toFixed(2) + "  dZ " + dz.toFixed(2) + " m";
    }
  }

  function computeElementBounds(elIdx) {
    var el = scene.elements[elIdx];
    if (!el || !el.instanceIndices || !el.instanceIndices.length) return null;
    var minB = [Infinity, Infinity, Infinity], maxB = [-Infinity, -Infinity, -Infinity];
    for (var i = 0; i < el.instanceIndices.length; i++) {
      var inst = runtimeInstances[el.instanceIndices[i]];
      for (var k = 0; k < 3; k++) {
        if (inst.boundsMin[k] < minB[k]) minB[k] = inst.boundsMin[k];
        if (inst.boundsMax[k] > maxB[k]) maxB[k] = inst.boundsMax[k];
      }
    }
    return { min: minB, max: maxB };
  }

  function zoomToElement(elIdx) {
    var eb = computeElementBounds(elIdx);
    if (!eb) return;
    fitBounds(eb.min, eb.max);
    requestRender();
  }

  // Hierarchy Tree & Search (Lazy + Paginated Virtualization, Pre-indexed Search, 100% XSS-safe via DOM textContent)
  var TREE_PAGE_SIZE = 150;
  var TREE_MORE_STEP = 250;
  var treeRoot = document.getElementById("tree-root");
  var elemRowNodes = new Array(scene.elements.length);
  var elemCatMap = new Array(scene.elements.length);
  var mountedElemIndices = [];
  var catRowRefs = [];

  var searchKeys = new Array(scene.elements.length);
  for (var si = 0; si < scene.elements.length; si++) {
    var se = scene.elements[si];
    searchKeys[si] = (
      (se.name || "") + "\n" +
      (se.category || "") + "\n" +
      (se.familyName || "") + "\n" +
      (se.typeName || "") + "\n#" +
      se.id + "\n" +
      (se.uniqueId || "")
    ).toLowerCase();
  }

  function createElemRow(catState, elIdx) {
    if (elemRowNodes[elIdx]) return elemRowNodes[elIdx].row;
    var elObj = scene.elements[elIdx];
    var hid = !!state.hiddenElements[elIdx];
    var cls = "elem-row" + (elIdx === state.selectedElemIdx ? " selected" : "") + (hid ? " dimmed" : "");
    var row = makeEl("div", cls);
    row.setAttribute("data-el-idx", String(elIdx));

    var nameSpan = makeEl("span", "elem-name", elObj.name);
    var idSpan = makeEl("span", "elem-id", "#" + elObj.id);
    var elEye = makeEl("button", "eye-btn" + (hid ? " hidden-state" : ""), hid ? "-" : "o");
    elEye.title = "Hide / Show element";
    elEye.setAttribute("data-eye-el", String(elIdx));

    row.appendChild(nameSpan);
    row.appendChild(idSpan);
    row.appendChild(elEye);

    elemRowNodes[elIdx] = { row: row, eye: elEye, catState: catState };
    mountedElemIndices.push(elIdx);
    return row;
  }

  function renderCategorySlice(catState, targetCount) {
    var total = catState.indices.length;
    var end = Math.min(total, targetCount);
    if (end > catState.renderedCount) {
      var frag = document.createDocumentFragment();
      for (var k = catState.renderedCount; k < end; k++) {
        var elIdx = catState.indices[k];
        if (!elemRowNodes[elIdx]) {
          frag.appendChild(createElemRow(catState, elIdx));
        }
      }
      catState.renderedCount = end;
      if (catState.moreBtn) {
        catState.listWrap.insertBefore(frag, catState.moreBtn);
      } else {
        catState.listWrap.appendChild(frag);
      }
    }

    var rem = total - catState.renderedCount;
    if (rem > 0) {
      if (!catState.moreBtn) {
        var btn = makeEl("button", "more-row", "");
        btn.setAttribute("data-more-cat", String(catState.catIndex));
        catState.moreBtn = btn;
        catState.listWrap.appendChild(btn);
      }
      catState.moreBtn.textContent = "Show more (" + rem + " remaining)";
    } else if (catState.moreBtn) {
      catState.moreBtn.remove();
      catState.moreBtn = null;
    }
  }

  treeRoot.addEventListener("click", function(ev) {
    var t = ev.target;
    if (!t || !t.closest) return;

    var moreBtn = t.closest("[data-more-cat]");
    if (moreBtn) {
      var mcs = catRowRefs[parseInt(moreBtn.getAttribute("data-more-cat"), 10)];
      if (mcs) renderCategorySlice(mcs, mcs.renderedCount + TREE_MORE_STEP);
      return;
    }

    var eyeEl = t.closest("[data-eye-el]");
    if (eyeEl) {
      ev.stopPropagation();
      var eIdx = parseInt(eyeEl.getAttribute("data-eye-el"), 10);
      state.hiddenElements[eIdx] = state.hiddenElements[eIdx] ? 0 : 1;
      syncTreeVisibilityStates();
      requestRender();
      return;
    }

    var eyeCat = t.closest("[data-eye-cat]");
    if (eyeCat) {
      ev.stopPropagation();
      var ecs = catRowRefs[parseInt(eyeCat.getAttribute("data-eye-cat"), 10)];
      if (!ecs) return;
      var anyVisible = false;
      for (var x = 0; x < ecs.indices.length; x++) {
        if (!state.hiddenElements[ecs.indices[x]]) { anyVisible = true; break; }
      }
      var hideFlag = anyVisible ? 1 : 0;
      for (var y = 0; y < ecs.indices.length; y++) state.hiddenElements[ecs.indices[y]] = hideFlag;
      syncTreeVisibilityStates();
      requestRender();
      return;
    }

    var catHdr = t.closest("[data-hdr-cat]");
    if (catHdr) {
      var hcs = catRowRefs[parseInt(catHdr.getAttribute("data-hdr-cat"), 10)];
      if (!hcs) return;
      var open = hcs.listWrap.classList.toggle("open");
      hcs.caret.textContent = open ? "v" : ">";
      if (open && hcs.renderedCount === 0) {
        renderCategorySlice(hcs, TREE_PAGE_SIZE);
      }
      return;
    }

    var elRow = t.closest("[data-el-idx]");
    if (elRow) {
      selectElement(parseInt(elRow.getAttribute("data-el-idx"), 10), false);
    }
  });

  treeRoot.addEventListener("dblclick", function(ev) {
    var t = ev.target;
    if (!t || !t.closest) return;
    var elRow = t.closest("[data-el-idx]");
    if (elRow) zoomToElement(parseInt(elRow.getAttribute("data-el-idx"), 10));
  });

  function buildHierarchyTree(filterQuery) {
    treeRoot.textContent = "";
    elemRowNodes = new Array(scene.elements.length);
    elemCatMap = new Array(scene.elements.length);
    mountedElemIndices = [];
    catRowRefs = [];

    var q = (filterQuery || "").trim().toLowerCase();
    var matchedElements = 0;
    var rootFrag = document.createDocumentFragment();

    for (var c = 0; c < scene.categories.length; c++) {
      var cat = scene.categories[c];
      var matchingIndices;
      if (!q) {
        matchingIndices = cat.elementIndices;
      } else {
        matchingIndices = [];
        for (var j = 0; j < cat.elementIndices.length; j++) {
          var ei = cat.elementIndices[j];
          if (searchKeys[ei].indexOf(q) >= 0) matchingIndices.push(ei);
        }
      }
      if (!matchingIndices.length) continue;
      matchedElements += matchingIndices.length;

      var catIndex = catRowRefs.length;
      var block = makeEl("div", "cat-block");
      var hdr = makeEl("div", "cat-row");
      hdr.setAttribute("data-hdr-cat", String(catIndex));

      var autoOpen = !!q;
      var caret = makeEl("span", "caret", autoOpen ? "v" : ">");
      var lbl = makeEl("span", "cat-label", cat.category);
      var cnt = makeEl("span", "count-badge", matchingIndices.length);
      var eye = makeEl("button", "eye-btn", "Vis");
      eye.title = "Toggle category visibility";
      eye.setAttribute("data-eye-cat", String(catIndex));

      var listWrap = makeEl("div", "elem-list" + (autoOpen ? " open" : ""));

      hdr.appendChild(caret);
      hdr.appendChild(lbl);
      hdr.appendChild(cnt);
      hdr.appendChild(eye);
      block.appendChild(hdr);
      block.appendChild(listWrap);
      rootFrag.appendChild(block);

      var catState = {
        catIndex: catIndex,
        indices: matchingIndices,
        caret: caret,
        eye: eye,
        listWrap: listWrap,
        renderedCount: 0,
        moreBtn: null
      };
      catRowRefs.push(catState);

      for (var p = 0; p < matchingIndices.length; p++) {
        elemCatMap[matchingIndices[p]] = { catState: catState, pos: p };
      }

      if (autoOpen) {
        renderCategorySlice(catState, TREE_PAGE_SIZE);
      }
    }

    treeRoot.appendChild(rootFrag);
    document.getElementById("tree-summary").textContent = matchedElements + " items";
    syncTreeVisibilityStates();

    if (state.selectedElemIdx >= 0 && elemRowNodes[state.selectedElemIdx]) {
      elemRowNodes[state.selectedElemIdx].row.classList.add("selected");
    }
  }

  function syncTreeVisibilityStates() {
    for (var m = 0; m < mountedElemIndices.length; m++) {
      var i = mountedElemIndices[m];
      var ref = elemRowNodes[i];
      if (!ref) continue;
      var hid = !!state.hiddenElements[i];
      ref.row.classList.toggle("dimmed", hid);
      ref.eye.classList.toggle("hidden-state", hid);
      ref.eye.textContent = hid ? "-" : "o";
    }
    for (var c = 0; c < catRowRefs.length; c++) {
      var cr = catRowRefs[c];
      var allHidden = true;
      for (var j = 0; j < cr.indices.length; j++) {
        if (!state.hiddenElements[cr.indices[j]]) { allHidden = false; break; }
      }
      cr.eye.classList.toggle("hidden-state", allHidden);
    }
  }

  // Properties Inspector (100% XSS-safe via DOM textContent)
  var propRoot = document.getElementById("prop-root");
  function renderPropertiesPanel() {
    propRoot.textContent = "";
    var selBadge = document.getElementById("sel-badge");
    if (state.selectedElemIdx < 0 || state.selectedElemIdx >= scene.elements.length) {
      selBadge.textContent = "";
      propRoot.appendChild(makeEl("div", "prop-empty", "Click any 3D element in the viewport or select an item in the Model Hierarchy to inspect its identity and parameters."));
      return;
    }

    var el = scene.elements[state.selectedElemIdx];
    selBadge.textContent = "#" + el.id;
    var pf = (state.propFilterText || "").trim().toLowerCase();

    function appendGroup(title, pairs) {
      var filtered = [];
      for (var i = 0; i < pairs.length; i++) {
        var k = pairs[i][0], v = pairs[i][1];
        if (v === null || v === undefined || v === "") continue;
        if (!pf || String(k).toLowerCase().indexOf(pf) >= 0 || String(v).toLowerCase().indexOf(pf) >= 0) {
          filtered.push(pairs[i]);
        }
      }
      if (!filtered.length) return;

      var grp = makeEl("div", "prop-group");
      var hdr = makeEl("div", "prop-group-hdr");
      hdr.appendChild(makeEl("span", "", title));
      var toggleSym = makeEl("span", "", "-");
      hdr.appendChild(toggleSym);

      var tbl = makeEl("table", "prop-table");
      for (var r = 0; r < filtered.length; r++) {
        var tr = document.createElement("tr");
        tr.appendChild(makeEl("td", "k", filtered[r][0]));
        tr.appendChild(makeEl("td", "v", filtered[r][1]));
        tbl.appendChild(tr);
      }

      hdr.addEventListener("click", function() {
        var hidden = tbl.style.display === "none";
        tbl.style.display = hidden ? "" : "none";
        toggleSym.textContent = hidden ? "-" : "+";
      });

      grp.appendChild(hdr);
      grp.appendChild(tbl);
      propRoot.appendChild(grp);
    }

    var identityPairs = [
      ["Name", el.name],
      ["Element ID", String(el.id)],
      ["UniqueId", el.uniqueId || null],
      ["Category", el.category],
      ["Family", el.familyName || null],
      ["Type", el.typeName || null],
      ["Level", el.level || null],
      ["Host ID", el.hostId ? "#" + el.hostId : null],
      ["Geometry", el.isInstanced ? "Shared Symbol Instance" : "Element Mesh"]
    ];
    var eb = computeElementBounds(state.selectedElemIdx);
    if (eb) {
      var sx = (eb.max[0] - eb.min[0]).toFixed(2);
      var sy = (eb.max[1] - eb.min[1]).toFixed(2);
      var sz = (eb.max[2] - eb.min[2]).toFixed(2);
      identityPairs.push(["Bounding Size", sx + " x " + sy + " x " + sz + " m"]);
    }
    appendGroup("Identity", identityPairs);

    if (el.propertyGroups) {
      for (var g = 0; g < el.propertyGroups.length; g++) {
        var pg = el.propertyGroups[g];
        var pairs = [];
        for (var p = 0; p < pg.properties.length; p++) {
          pairs.push([pg.properties[p].name, pg.properties[p].value]);
        }
        appendGroup(pg.group, pairs);
      }
    }
  }

  function selectElement(elIdx, scrollTree) {
    if (state.selectedElemIdx >= 0 && elemRowNodes[state.selectedElemIdx]) {
      elemRowNodes[state.selectedElemIdx].row.classList.remove("selected");
    }
    state.selectedElemIdx = elIdx;
    var hasSel = elIdx >= 0;
    document.getElementById("btn-isolate").disabled = !hasSel;
    document.getElementById("btn-hide-sel").disabled = !hasSel;
    document.getElementById("btn-fit-sel").disabled = !hasSel;

    if (hasSel && scrollTree && !elemRowNodes[elIdx] && elemCatMap[elIdx]) {
      var mapEntry = elemCatMap[elIdx];
      var cs = mapEntry.catState;
      if (mapEntry.pos < cs.renderedCount + TREE_MORE_STEP) {
        renderCategorySlice(cs, Math.max(TREE_PAGE_SIZE, mapEntry.pos + 25));
      } else {
        if (cs.renderedCount === 0) renderCategorySlice(cs, TREE_PAGE_SIZE);
        var pinned = createElemRow(cs, elIdx);
        if (cs.moreBtn) cs.listWrap.insertBefore(pinned, cs.moreBtn);
        else cs.listWrap.appendChild(pinned);
      }
    }

    if (hasSel && elemRowNodes[elIdx]) {
      var ref = elemRowNodes[elIdx];
      ref.row.classList.add("selected");
      if (scrollTree && ref.catState) {
        ref.catState.listWrap.classList.add("open");
        ref.catState.caret.textContent = "v";
        ref.row.scrollIntoView({ block: "nearest" });
      }
    }

    renderPropertiesPanel();
    requestRender();
  }

  // Controls & Buttons Wiring
  buildHierarchyTree("");

  document.getElementById("tree-search").addEventListener("input", function(e) {
    buildHierarchyTree(e.target.value);
  });

  document.getElementById("prop-filter").addEventListener("input", function(e) {
    state.propFilterText = e.target.value;
    renderPropertiesPanel();
  });

  document.getElementById("btn-show-all").addEventListener("click", function() {
    state.hiddenElements.fill(0);
    syncTreeVisibilityStates();
    requestRender();
  });

  document.getElementById("btn-hide-sel").addEventListener("click", function() {
    if (state.selectedElemIdx < 0) return;
    state.hiddenElements[state.selectedElemIdx] = 1;
    syncTreeVisibilityStates();
    requestRender();
  });

  document.getElementById("btn-isolate").addEventListener("click", function() {
    if (state.selectedElemIdx < 0) return;
    state.hiddenElements.fill(1);
    state.hiddenElements[state.selectedElemIdx] = 0;
    syncTreeVisibilityStates();
    requestRender();
  });

  document.getElementById("btn-fit-sel").addEventListener("click", function() {
    if (state.selectedElemIdx >= 0) zoomToElement(state.selectedElemIdx);
  });

  document.getElementById("btn-fit").addEventListener("click", function() {
    fitBounds(bMin, bMax);
    requestRender();
  });

  document.getElementById("btn-proj").addEventListener("click", function() {
    state.ortho = !state.ortho;
    this.textContent = state.ortho ? "Orthographic" : "Perspective";
    this.classList.toggle("active", state.ortho);
    requestRender();
  });

  function setRenderStyle(style) {
    state.renderStyle = style;
    document.getElementById("btn-style-shaded").classList.toggle("active", style === "shaded");
    document.getElementById("btn-style-clay").classList.toggle("active", style === "clay");
    document.getElementById("btn-style-wire").classList.toggle("active", style === "wire");
    requestRender();
  }
  document.getElementById("btn-style-shaded").addEventListener("click", function() { setRenderStyle("shaded"); });
  document.getElementById("btn-style-clay").addEventListener("click", function() { setRenderStyle("clay"); });
  document.getElementById("btn-style-wire").addEventListener("click", function() { setRenderStyle("wire"); });

  var btnGrids = document.getElementById("btn-grids");
  if (!scene.grids || scene.grids.length === 0) {
    btnGrids.style.display = "none";
  } else {
    btnGrids.classList.toggle("active", state.showGrids);
    btnGrids.addEventListener("click", function() {
      state.showGrids = !state.showGrids;
      btnGrids.classList.toggle("active", state.showGrids);
      requestRender();
    });
  }

  document.getElementById("btn-measure").addEventListener("click", function() {
    state.measureActive = !state.measureActive;
    state.measurePts = [];
    this.classList.toggle("active", state.measureActive);
    document.getElementById("measure-banner").classList.toggle("open", state.measureActive);
    updateMeasureReadout();
    requestRender();
  });

  // Section Box Controls
  var secPanel = document.getElementById("section-panel");
  var btnSection = document.getElementById("btn-section");
  var chkSection = document.getElementById("chk-section");
  var secSliders = {
    xmin: document.getElementById("sec-xmin"), xmax: document.getElementById("sec-xmax"),
    ymin: document.getElementById("sec-ymin"), ymax: document.getElementById("sec-ymax"),
    zmin: document.getElementById("sec-zmin"), zmax: document.getElementById("sec-zmax")
  };

  btnSection.addEventListener("click", function() {
    var open = secPanel.classList.toggle("open");
    btnSection.classList.toggle("active", open || state.sectionActive);
  });

  function updateClipFromSliders() {
    var pairs = [["xmin","xmax",0], ["ymin","ymax",1], ["zmin","zmax",2]];
    for (var i = 0; i < 3; i++) {
      var a = parseInt(secSliders[pairs[i][0]].value, 10) / 1000;
      var b = parseInt(secSliders[pairs[i][1]].value, 10) / 1000;
      if (a > b) { var t = a; a = b; b = t; }
      var span = fullClipMax[i] - fullClipMin[i];
      state.clipMin[i] = fullClipMin[i] + span * a;
      state.clipMax[i] = fullClipMin[i] + span * b;
    }
    state.sectionActive = true;
    chkSection.checked = true;
    btnSection.classList.add("active");
    requestRender();
  }

  Object.keys(secSliders).forEach(function(k) {
    secSliders[k].addEventListener("input", updateClipFromSliders);
  });

  chkSection.addEventListener("change", function() {
    state.sectionActive = chkSection.checked;
    btnSection.classList.toggle("active", state.sectionActive || secPanel.classList.contains("open"));
    requestRender();
  });

  document.getElementById("btn-sec-reset").addEventListener("click", function() {
    secSliders.xmin.value = 0; secSliders.xmax.value = 1000;
    secSliders.ymin.value = 0; secSliders.ymax.value = 1000;
    secSliders.zmin.value = 0; secSliders.zmax.value = 1000;
    state.clipMin = fullClipMin.slice();
    state.clipMax = fullClipMax.slice();
    state.sectionActive = false;
    chkSection.checked = false;
    requestRender();
  });

  document.getElementById("btn-sec-sel").addEventListener("click", function() {
    if (state.selectedElemIdx < 0) return;
    var eb = computeElementBounds(state.selectedElemIdx);
    if (!eb) return;
    var margin = 0.25;
    for (var i = 0; i < 3; i++) {
      state.clipMin[i] = eb.min[i] - margin;
      state.clipMax[i] = eb.max[i] + margin;
      var span = Math.max(1e-5, fullClipMax[i] - fullClipMin[i]);
      var f0 = Math.max(0, Math.min(1, (state.clipMin[i] - fullClipMin[i]) / span));
      var f1 = Math.max(0, Math.min(1, (state.clipMax[i] - fullClipMin[i]) / span));
      secSliders[["xmin","ymin","zmin"][i]].value = Math.round(f0 * 1000);
      secSliders[["xmax","ymax","zmax"][i]].value = Math.round(f1 * 1000);
    }
    state.sectionActive = true;
    chkSection.checked = true;
    requestRender();
  });

  // ViewCube Presets
  var viewPresets = {
    TOP:    [ -Math.PI * 0.5,  1.569 ],
    BOTTOM: [ -Math.PI * 0.5, -1.569 ],
    FRONT:  [ -Math.PI * 0.5,  0.0 ],
    BACK:   [  Math.PI * 0.5,  0.0 ],
    RIGHT:  [  0.0,            0.0 ],
    LEFT:   [  Math.PI,        0.0 ],
    ISO:    [ -Math.PI * 0.25, 0.52 ]
  };
  document.querySelectorAll(".vc-face").forEach(function(face) {
    face.addEventListener("click", function() {
      var p = viewPresets[face.getAttribute("data-view")];
      if (p) { state.yaw = p[0]; state.pitch = p[1]; requestRender(); }
    });
  });
  document.getElementById("btn-iso").addEventListener("click", function() {
    state.yaw = viewPresets.ISO[0]; state.pitch = viewPresets.ISO[1];
    fitBounds(bMin, bMax);
    requestRender();
  });

  // Topbar Toggles & Modal
  document.getElementById("btn-toggle-left").addEventListener("click", function() {
    document.getElementById("left-sidebar").classList.toggle("collapsed");
    requestRender();
  });
  document.getElementById("btn-toggle-right").addEventListener("click", function() {
    document.getElementById("right-sidebar").classList.toggle("collapsed");
    requestRender();
  });
  document.getElementById("btn-theme").addEventListener("click", function() {
    var cur = document.documentElement.getAttribute("data-theme");
    document.documentElement.setAttribute("data-theme", cur === "light" ? "dark" : "light");
    requestRender();
  });

  var modal = document.getElementById("modal-backdrop");
  compBtn.addEventListener("click", function() { modal.classList.add("open"); });
  document.getElementById("btn-modal-close").addEventListener("click", function() { modal.classList.remove("open"); });
  modal.addEventListener("click", function(e) { if (e.target === modal) modal.classList.remove("open"); });

  // Keyboard Shortcuts
  window.addEventListener("keydown", function(e) {
    if (e.target && (e.target.tagName === "INPUT" || e.target.tagName === "TEXTAREA")) return;
    var k = e.key.toLowerCase();
    if (k === "f") {
      if (state.selectedElemIdx >= 0) zoomToElement(state.selectedElemIdx);
      else fitBounds(bMin, bMax);
      requestRender();
    } else if (k === "h" && state.selectedElemIdx >= 0) {
      state.hiddenElements[state.selectedElemIdx] = 1;
      syncTreeVisibilityStates();
      requestRender();
    } else if (k === "i" && state.selectedElemIdx >= 0) {
      state.hiddenElements.fill(1);
      state.hiddenElements[state.selectedElemIdx] = 0;
      syncTreeVisibilityStates();
      requestRender();
    } else if (k === "u") {
      state.hiddenElements.fill(0);
      syncTreeVisibilityStates();
      requestRender();
    } else if (k === "p") {
      document.getElementById("btn-proj").click();
    } else if (e.key === "Escape") {
      if (modal.classList.contains("open")) {
        modal.classList.remove("open");
      } else if (secPanel.classList.contains("open")) {
        secPanel.classList.remove("open");
        btnSection.classList.toggle("active", state.sectionActive);
      } else if (state.measureActive && state.measurePts.length > 0) {
        state.measurePts = [];
        updateMeasureReadout();
        requestRender();
      } else if (state.measureActive) {
        document.getElementById("btn-measure").click();
      } else {
        selectElement(-1, false);
      }
    }
  });

  window.addEventListener("resize", requestRender);
  requestRender();
})();
</script>
</body>
</html>
""";
}
