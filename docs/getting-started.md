---
title: Getting started
---

# Getting started

## Command line tool

```sh
dotnet tool install --global RvtIO.Cli
rvtio project.rvt project.ifc
```

A `.ifc` output path selects IFC. Any other output path selects JSON, which is compact by default.

```sh
rvtio project.rvt project.compact.json --compact-json
rvtio project.rvt project.full.json --json-full
rvtio project.rvt project.parameters.ifc --ifc-revit-parameters
```

Run `rvtio --help` for all options. The exit codes and the `--report` file are described in [Export report and exit codes](export-report.md).

## Library

```sh
dotnet add package RvtIO
```

```csharp
using System;
using RvtIO;
using RvtIO.BuiltIns;
using RvtIO.Ifc;

var doc = RvtDocument.Open("project.rvt");

FilteredElementCollector walls = doc.Collector()
    .OfCategory(BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType();

foreach (var wall in walls)
{
    Console.WriteLine($"{wall.Id}: {wall.Name}");
}

new IfcExporter().Export(doc.RawData, "project.ifc");
```

### Triangle meshes

`ElementMeshes.Extract` returns the triangles of the model elements of an opened document, in feet. A repeated family instance
that is exactly its symbol is a placement of one shared symbol mesh:

```csharp
using RvtIO.Meshes;

var meshes = ElementMeshes.Extract(doc);
foreach (var element in meshes.Elements)
{
    // element.Meshes: own triangles in the coordinates of the document, one mesh per material.
    // element.Placements: symbols drawn by a transform; their triangles are meshes.Symbols[placement.SymbolId].
    Console.WriteLine($"{element.ElementId}: {element.Meshes.Count} meshes, {element.Placements.Count} placements, {element.Coverage}");
}
```

Open a file of any release other than Revit 2022 with `new RvtReaderOptions { R22Layout = true }` to decode its geometry
(the reader keeps its own Revit 2022 layout only for Revit 2022 files; measured on samples of Revit 2020 and 2025, while the
Revit 2022 samples give the same meshes either way). Without it `MeshSet.GeometryUndecoded` is true and the elements are listed
as `MeshCoverage.Undecoded`. The command line tool sets the option for every IFC export.

The library is silent by default. Set `RvtReaderOptions.DiagnosticOutput` for reading or `IfcExporter.DiagnosticOutput` for export to receive diagnostics through a `TextWriter`.

The supported surface is listed in [Public API](public-api.md); every type is in the [API reference](api/RvtIO.yml).

## Status

RvtIO is a preview and the API can change before 1.0.
