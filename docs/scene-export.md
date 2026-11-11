---
title: Scene and HTML 3D viewer export
---

# 3D scene export and standalone HTML viewer

`RvtIO` exports browser-ready 3D geometry and element metadata directly from `.rvt` projects and `.rfa` families in three forms:

1. **Binary glTF 2.0 (`.glb`)** — standard 3D asset with reusable meshes, per-instance transforms, PBR materials and element metadata in `node.extras`.
2. **Versioned scene JSON (`rvtio-scene/1`)** — compact JSON contract with Base64 typed-array mesh buffers, category hierarchy, grouped element properties, datum grids and completeness notes.
3. **Self-contained 3D HTML viewer (`.html`)** — single offline HTML file embedding the `rvtio-scene/1` payload and a zero-dependency WebGL viewer with no external network requests or CDN links.

## Command line

Export a binary glTF 2.0 scene:

```sh
rvtio project.rvt project.glb
```

Export a self-contained HTML viewer:

```sh
rvtio project.rvt project.html --viewer-title "Tower A - Architectural"
```

Export the `rvtio-scene/1` JSON contract:

```sh
rvtio project.rvt project.scene.json
rvtio project.rvt scene.json --scene-json --scene-pretty
```

Filter by category or element IDs, control exported metadata, or apply shared coordinates:

```sh
rvtio project.rvt envelope.html --scene-categories Walls,Windows,Doors,Roofs
rvtio project.rvt subset.glb --scene-ids 184201,184205 --scene-no-parameters
rvtio project.rvt site.glb --scene-shared-coordinates --scene-include-rooms
```

### Options

| Option | Effect |
| --- | --- |
| `--scene-json` | Write `rvtio-scene/1` JSON instead of the compact data JSON. Automatic when the output path ends with `.scene.json`. |
| `--scene-pretty` | Indent the `rvtio-scene/1` JSON output. |
| `--scene-categories <c1,c2,...>` | Include only the listed categories. Accepts numeric category IDs (`-2000011`), `BuiltInCategory` names (`OST_Walls`) or English category names (`Walls`). |
| `--scene-ids <id1,id2,...>` | Include only the listed Revit element IDs. |
| `--scene-no-parameters` | Omit element and type parameters from the scene and HTML viewer. |
| `--scene-parameter-names <p1,...>` | Include only parameters matching the listed names or captions. |
| `--scene-shared-coordinates` | Transform coordinates by `RevitFileInfo.SharedTransform` before computing the relative-to-center (`rtcOffset`) origin. |
| `--scene-include-rooms` | Export room volumes (`OST_Rooms`) as translucent 3D enclosures. |
| `--scene-no-grids` | Omit datum grid lines (`OST_Grids`). |
| `--glb-z-up` | Keep Revit right-handed Z-up coordinates in `.glb` instead of rotating the root node to glTF Y-up. |
| `--viewer-title <text>` | Set a custom document and header title in the HTML viewer. |
| `--viewer-theme <dark\|light>` | Set the initial color theme of the HTML viewer (default: `dark`). |

All scene exports participate in `--report <report.json>` (`export.format` is `glb`, `html` or `scene-json`) and respect `--raise-limits` and `--allow-partial`.

## Library API

```csharp
using RvtIO;
using RvtIO.BuiltIns;
using RvtIO.Scene;

var doc = RvtDocument.Open("project.rvt", new RvtReaderOptions
{
    R22Layout = true,
    SeparateFacesWithDistinctOpenings = true,
    SystemFamilyNameLocale = "ENU",
});

var options = new HtmlViewerOptions
{
    Title = "Project Review",
    InitialTheme = "dark",
    CategoryIds = new HashSet<int>
    {
        (int)BuiltInCategory.OST_Walls,
        (int)BuiltInCategory.OST_Doors,
        (int)BuiltInCategory.OST_Windows,
    },
};

ModelScene scene = SceneExporter.Build(doc, options);
SceneExporter.ExportGlb(scene, "project.glb");
SceneExporter.ExportJson(scene, "project.scene.json", indented: true);
HtmlViewerExporter.Export(scene, "project.html", options);
```

`SceneExporter.OpenAndBuild(rvtPath, options)` and `HtmlViewerExporter.Export(rvtPath, outputPath, options)` open the file with 3D geometry decoding (`R22Layout = true`) enabled by default.

## `rvtio-scene/1` JSON contract

The scene JSON is a single UTF-8 object with `camelCase` property names and `"schema": "rvtio-scene/1"`. Additive properties may be introduced within `rvtio-scene/1`; breaking changes increment the schema version.

| Property | Meaning |
| --- | --- |
| `schema` | Always `"rvtio-scene/1"`. |
| `title` | Human-readable model title. |
| `units` | `"m"` (metres). |
| `coordinateSystem` | `"right-handed-z-up"`. |
| `rtcOffset` | `[x, y, z]` double-precision origin offset in metres. Add `rtcOffset` to any scene coordinate to recover world metres: $P_{\text{world}} = P_{\text{scene}} + \text{rtcOffset}$. |
| `sharedTransform` | Optional 12-double `[basisX(3), basisY(3), basisZ(3), originMetres(3)]` internal-to-shared transform when present in the file. |
| `boundsMin`, `boundsMax` | `[x, y, z]` bounding box of the scene in RTC metres. |
| `materials[]` | `{ index, revitMaterialId?, name, color: [r,g,b], opacity, metallic, roughness, doubleSided }`. |
| `meshes[]` | Reusable indexed triangle prototypes: `{ index, sourceSymbolId?, isMirroredVariant, vertexCount, triangleCount, indexType ("uint16"\|"uint32"), localBoundsMin, localBoundsMax, positionsB64, normalsB64, indicesB64 }`. `positionsB64` and `normalsB64` are little-endian IEEE 754 `Float32Array` bytes (`vertexCount * 3` floats); `indicesB64` is little-endian `Uint16Array` or `Uint32Array` (`triangleCount * 3` indices). |
| `instances[]` | Placed mesh occurrences: `{ meshIndex, elementIndex, elementId, materialIndex, matrix, boundsMin, boundsMax }`, where `matrix` is a 16-float column-major 4x4 affine matrix in RTC metres. |
| `elements[]` | Exported physical elements: `{ id, uniqueId?, name, category, categoryId?, familyName?, typeName?, level?, hostId?, isInstanced, instanceIndices, propertyGroups: [{ group, properties: [{ name, value }] }] }`. |
| `categories[]` | Category tree nodes: `{ category, categoryId?, elementIndices }`. |
| `grids[]` | Datum grid segments in RTC metres: `{ id, name, start: [x,y,z], end: [x,y,z] }`. |
| `completeness` | `{ totalElements, consideredElements, exportedElements, instancedElements, reusableMeshCount, instanceCount, vertexCount, triangleCount, isPartialGeometry, notes[], omittedGroups: [{ code, reason, count }] }`. |

### Precision and instancing guarantees

- **Relative-to-center (RTC) coordinates**: Models located more than 100 m from the origin (such as georeferenced shared coordinates) automatically shift their scene origin to the double-precision bounding-box center stored in `rtcOffset`, preventing 32-bit `Float32` vertex jitter in WebGL and glTF viewers.
- **Reusable family symbol meshes**: Repeated `FamilyInstance` placements share a single `SceneMeshPrototype` in `meshes[]` (and a single glTF `mesh` in `.glb`) placed by per-instance 4x4 transforms.
- **Mirrored instances**: Instances whose placement transform has negative determinant ($\det(M) < 0$) reference a dedicated mirrored prototype variant (`isMirroredVariant: true`) with reversed triangle index winding `(i0, i2, i1)` so counter-clockwise front faces and outward `(M^{-1})^T` normals are preserved in standard glTF 2.0 and WebGL pipelines.

## Standalone HTML viewer capabilities and security

- **Navigation**: Orbit (left-drag), pan (right-drag or Shift + drag), cursor-targeted zoom (wheel), fit-to-model / fit-to-selection (`F`), orthographic/perspective toggle (`O`), and an interactive 3D ViewCube (`Top`, `Bottom`, `Front`, `Back`, `Left`, `Right`, `Home`).
- **Inspection & filtering**: 3D raycast picking with selection highlight, category hierarchy browser with visibility toggles, live text search across element names/IDs/categories/types/levels, `Hide` (`H`), `Isolate` (`I`), `Show All` (`Esc` / button), grouped property inspector, 6-axis interactive section box, datum grid toggle (`G`), 3D point-to-point distance measurement (`M`), theme toggle, and a model completeness summary dialog.
- **Offline & XSS safety**: Zero external scripts, stylesheets, fonts or images are referenced. All embedded JSON escapes `<`, `>`, `&` (`\u003C`, `\u003E`, `\u0026`) so `</script>` payloads in untrusted model strings cannot break out of `<script type="application/json">`, `<title>` is HTML-encoded, and the viewer UI renders every model string exclusively via DOM `.textContent`.
