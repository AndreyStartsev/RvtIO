# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

- Changes the default output of the current IFC profile for the openings of doors and windows (Closes #73). The Width and Height quantities of the IfcOpeningElement of a door or window in a straight wall are now those of the door or window as Revit has them: Width is the side along the wall (the Width of the instance, else of its type), Height is the vertical side (its Height) and Depth stays the thickness of the wall, in a wall of any direction. A side the door or window holds no value for is measured from the final opening in the frame of the wall. Before, the sides of the cut loop were named by the frame they are measured in, so a door 0.8 wide and 2.1 high got Height 0.8 and Width 2.1. Openings of curved walls and curtain walls, openings that nothing fills and openings of other fillers are unchanged, and so is every other value of the output. To get the earlier values back, use the new `--ifc-opening-legacy-axes` command line option or set `IfcExporter.OpeningQuantitiesSwapped` to true. `--ifc-opening-host-axes` (the opt-in that added the new reading) is still accepted and does nothing, and `IfcExporter.OpeningQuantitiesInHostAxes` still compiles; it is now true by default and the opposite of `OpeningQuantitiesSwapped`. Refs #6.
- Adds the exit codes 2 (input cannot be read: missing, empty, not a compound file, cut or damaged), 3 (a stream exceeded a reader limit and the read was cut) and 4 (a decoder failed on a readable file), a one-line message instead of an unhandled exception for a truncated or damaged file, and the options `--report`, `--allow-partial` and `--raise-limits`. A complete export without them is unchanged. See [the export report](docs/export-report.md). `RvtFileReader.Read` and `RvtDocument.Open` check that every stream of the compound file can be read to its declared end and throw `RvtInputException` (or `RvtLimitException` for a stream over the platform array length) instead of an `EndOfStreamException` from a decoder; `RvtReaderOptions.Diagnostics` collects the streams read and the limits that cut one.
- Derives IFC base quantities of beams, columns, members and piles from the surface integrals of the source geometry (planes, cylinders and cones); cutting solids no longer enter the totals.
- Takes the length of a curtain wall mullion from the axis of its own symbol, and the footprint of a curved wall from the area of its rebuilt curve loop.
- Takes door and window height and width from their parameters or frame extent before the bounding box, and computes their host area from the coarse solids including non-visible geometry.
- Selects the element table and record layout by the release of the file's own schema, the older of it and the saved release, and reports the release in the diagnostics.
- Decodes the document increment table of a file saved in several increments with a bound on the work it may do; a malformed table is rejected instead of guessed.
- Adds the opt-in `ResolveCurrentCopies` reader option and `--resolve-current-copies` command line option, which keep only the current copy of an element that several partition streams hold. It is off by default and the default output does not change.
- Fixes the partition framing check so that a damaged or truncated partition stream is rejected instead of read past its end.
- Reads the project length unit in every file layout (including the Revit 2020 display unit layout and family documents) for the IFC length unit, and reports the reason through diagnostics when it falls back to millimetres.
- Leaves out of the IFC the elements that are demolished in the export phase or an earlier one, or created in a later one. The current profile exports the last project phase by default; the new `--ifc-phase <last|all|id>` command line option and the `IfcExporter.ExportPhaseId` and `ExportAllPhases` properties select another phase or every phase, and rooms follow the export phase unless a room phase is given. The legacy profile and the JSON output are not filtered by default.
- Resolves the type of a stair run or landing through its stored type association, so the IFC names the real type, writes its stair flight type and property sets, and the stair run Reference. The IFC stair shape of the current profile stays undefined when a stair has a sketched run, and a railing aggregated into a stair no longer takes the classification code of the stair. The element class counts of the stair source are taken from the stream walk, which in files with their own class schema lets both profiles recognise the runs and landings of a stair. The run Length quantity keeps the inputs it had. Output without stairs or railings is unchanged.
- Reduces the memory allocated and the processor time spent in the tessellation of trimmed surfaces and in the ordering of boundary rings: grid cells are clipped in reused per-thread buffers and the ring search uses flat arrays instead of per-cell lists and LINQ. The output is unchanged byte for byte; an export of a large model allocates about 40 percent less memory.

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
