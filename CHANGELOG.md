# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

- Derives IFC base quantities of beams, columns, members and piles from the surface integrals of the source geometry (planes, cylinders and cones); cutting solids no longer enter the totals.
- Takes the length of a curtain wall mullion from the axis of its own symbol, and the footprint of a curved wall from the area of its rebuilt curve loop.
- Takes door and window height and width from their parameters or frame extent before the bounding box, and computes their host area from the coarse solids including non-visible geometry.
- Selects the element table and record layout by the release of the file's own schema, the older of it and the saved release, and reports the release in the diagnostics.
- Decodes the document increment table of a file saved in several increments with a bound on the work it may do; a malformed table is rejected instead of guessed.
- Adds the opt-in `ResolveCurrentCopies` reader option and `--resolve-current-copies` command line option, which keep only the current copy of an element that several partition streams hold. It is off by default and the default output does not change.
- Fixes the partition framing check so that a damaged or truncated partition stream is rejected instead of read past its end.
- Reads the project length unit in every file layout (including the Revit 2020 display unit layout and family documents) for the IFC length unit, and reports the reason through diagnostics when it falls back to millimetres.
- Leaves out of the IFC the elements that are demolished in the export phase or an earlier one, or created in a later one. The current profile exports the last project phase by default; the new `--ifc-phase <last|all|id>` command line option and the `IfcExporter.ExportPhaseId` and `ExportAllPhases` properties select another phase or every phase, and rooms follow the export phase unless a room phase is given. The legacy profile and the JSON output are not filtered by default.

## [0.1.0] - first public preview

- Reads Revit projects (`.rvt`) and families (`.rfa`) saved by Revit 2020 to 2026 without Revit; Revit 2027 files open
  with partial support.
- Writes IFC2X3 (coordination view) with optional Revit parameters as property sets, compact JSON and a full JSON dump.
- `RvtDocument`, `FilteredElementCollector` and the element model for reading; `IfcExporter` and `CompactJsonExporter`
  for export; the `rvtio` command line tool.
- Targets .NET 8 and .NET 10; the library is silent by default and reports through `DiagnosticOutput`.
- Excludes resolved non-primary design options from IFC while retaining all options in the document API.
- Reads additional user parameters on materials, curve center lines and view markers.
- Corrects element identifiers, material layers and compressed data handling in newer project layouts.
