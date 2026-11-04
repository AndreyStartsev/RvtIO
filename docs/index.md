---
title: RvtIO
---

# RvtIO

RvtIO is a .NET library and command line tool that reads Autodesk Revit projects (`.rvt`) and families (`.rfa`) directly from disk, without Revit or the Revit API, and exports IFC and JSON.

- Reads elements, parameters, geometry, file metadata and project information.
- Queries elements through `FilteredElementCollector`.
- Exports IFC2X3 coordination view, with optional Revit parameters as property sets.
- Writes compact JSON or a full JSON dump with every parameter and geometry.

The library targets .NET 8 and .NET 10, uses managed code only, and supports Windows, Linux and macOS.

## Where to go next

- [Getting started](getting-started.md): install the tool or the package and run a first export.
- [Public API](public-api.md): the supported surface of the library.
- [Export report and exit codes](export-report.md): how a complete export is told from a partial one.
- [API reference](api/RvtIO.yml): every public type, generated from the XML documentation.
- [Source on GitHub](https://github.com/mahach666/RvtIO) and [DeepWiki](https://deepwiki.com/mahach666/RvtIO) for an AI-generated overview of the repository.

RvtIO is not affiliated with, endorsed by or sponsored by Autodesk. Autodesk and Revit are trademarks of Autodesk, Inc.
