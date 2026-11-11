---
title: Getting started
---

# Getting started

## Command line tool

```sh
dotnet tool install --global RvtIO.Cli
rvtio project.rvt project.ifc
```

A `.ifc` output path selects IFC, `.glb` selects a binary glTF 2.0 scene, `.html` / `.htm` selects a standalone 3D HTML viewer, and `.scene.json` (or `--scene-json`) selects the versioned `rvtio-scene/1` JSON contract. Any other output path selects data JSON, which is compact by default.

```sh
rvtio project.rvt project.glb
rvtio project.rvt viewer.html
rvtio project.rvt project.compact.json --compact-json
rvtio project.rvt project.full.json --json-full
rvtio project.rvt project.parameters.ifc --ifc-revit-parameters
```

Run `rvtio --help` for all options. Scene and viewer options are described in [Scene and HTML 3D viewer export](scene-export.md); the exit codes and the `--report` file are described in [Export report and exit codes](export-report.md).

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

The library is silent by default. Set `RvtReaderOptions.DiagnosticOutput` for reading or `IfcExporter.DiagnosticOutput` for export to receive diagnostics through a `TextWriter`.

The supported surface is listed in [Public API](public-api.md); every type is in the [API reference](api/RvtIO.yml).

## Status

RvtIO is a preview and the API can change before 1.0.
