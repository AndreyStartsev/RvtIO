# Scene adapter

`RvtIO.Scene` turns a Revit project read by RvtIO into a scene of the `IOScene` package: a `SceneDocument` with one
model, the drawable elements, their triangle meshes in metres, their names and their parameters. A viewer or another
consumer of the scene reads it without knowing Revit. The scene itself is specified in the contract of the `IOScene`
repository (`docs/contract.md`), and the property keys it writes are registered there in `docs/keys.md`.

The adapter is a preview and is not published as a package yet: reference the project `src/RvtIO.Scene`.

## Usage

```csharp
using IOScene;
using RvtIO.Scene;

var result = RvtSceneReader.Read("project.rvt");
foreach (var diagnostic in result.Diagnostics)
    Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
SceneJson.Save(result.Document, "project.ioscene.json");
```

`Read(path)` opens the file as the command line tool opens it for an IFC export (`R22Layout`, English system family
names, faces with distinct openings kept apart). `Read(document, options)` takes a document you opened yourself; it
needs `RvtSceneOptions.ModelKey`, because a document does not know its file name. Open a file of a release other than
Revit 2022 with `R22Layout = true`, or its elements have no geometry and the result has the error `geometry-undecoded`.

`RvtSceneResult` holds the `Document`, the `Diagnostics` and counts: elements, elements with geometry, elements by
coverage, geometries, instances and symbol placements.

## What the scene holds

| Scene | From |
|---|---|
| `SceneModel.Key` | `RvtSceneOptions.ModelKey`, else the file name without its directory (`project.rvt`). This derivation does not change. |
| `SceneModel` | `SourceFormat` `rvt`, `SourceLengthUnit` `ft`, `Producer` `RvtIO.Scene <version>`. |
| `SceneElement` | One per drawable element of `ElementMeshes.Extract` (the elements the IFC export would consider products, see the remarks of `ElementMeshes`). `Id` is the element id. |
| `SourceId` | The Revit `UniqueId`. This derivation does not change; a golden test pins it on a fixture file. |
| `Name`, `Category`, `TypeName` | `Element.Name`; the English name of the built-in category (else `Element.Category`); the name of the type element. |
| `SceneGeometry`, `SceneInstance` | A symbol placed whole is one geometry per mesh in the frame of its family, and each placement is one instance per mesh with the placement's transform. A mirrored placement keeps its transform as it is: the winding of the triangles is not changed. A mesh of an element's own is a geometry used once, with vertices relative to the lowest corner of the element's meshes, which is the translation of its instances. All lengths are feet x 0.3048, in the internal coordinates of the document, Z up. |
| `SceneMaterial` | One per material element that a mesh uses: its name and its colour (`MATERIAL_PARAM_COLOR`, alpha from `MATERIAL_PARAM_TRANSPARENCY`), else a neutral grey. |
| `ScenePropertySet` | `revit:instance`: the parameters of the element. `revit:type`: the parameters of its type, one set shared by every element of the type. A parameter without a value is left out. |
| `SceneProperty` | Name: `Definition.Name` (for a built-in parameter its enum name, such as `WALL_USER_HEIGHT_PARAM`). Key: `revit:bip:<id>` for a built-in parameter, none for the others. Value: text. |

## Diagnostics

| Code | Severity | Meaning |
|---|---|---|
| `geometry-undecoded` | error | The geometry of the file was not decoded; open it with `R22Layout`. |
| `read-cut` | error | A reader limit cut a stream while the file was read (`Read(path)` only); elements may be missing. |
| `undecoded` | warning | An element of such a file; it is in the scene without geometry. |
| `no-geometry` | warning | An element whose geometry record has nothing that could be drawn; it is in the scene without geometry. |
| `partial-geometry` | warning | Faces that could not be triangulated, or a placed symbol or Geometry node without a decoded polygon. |
| `box-substitute` | warning | A rescaled symbol drawn as its bounding box. |
| `no-source-id` | warning | Elements without a UniqueId; they cannot be identified outside the scene. |

## Limits

- Values are text only: the value the file stores, in Revit internal units (feet, radians, ...), written with the
  invariant culture; an element id is its number. Typed values (numbers in SI units, booleans, element references)
  wait for public RvtIO API.
- Shared, project and family parameters have no key yet; the format of their keys is registered with the first release.
- No levels (`LevelId` and the `Levels` table stay empty): RvtIO has no public level of an element.
- No `IfcGuid`: the GlobalId of the IFC export is not public API.
- No tree: every element is a top-level element of the model. No links, grids or views.
- Materials have a name and a colour; no roughness, metalness or textures.
- Element names are those of `RvtDocument`; the validated names the IFC export applies to files read by their own class
  schema are not public API.
