# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html); versions before 1.0 may change the
API between minor releases.

## [Unreleased]

- Measures the opening of a door or window in a straight wall by its decoded cut loop (Width along the wall, Height vertical) instead of the nominal size of the door or window when the cut is larger than the nominal size, as Revit cuts it; the nominal size stays for an opening whose cut loop is not decoded (for the sample `arch25` this changes the Width and Height of 124 openings, which now agree with the reference export). Closes #72.
- Gives a stair with straight and sketched runs the shape of its straight runs in the current profile (Closes #70). A sketched run (flight type FREEFORM) is no flight of the shape rule there, so a straight run, a landing and a sketched run turned back is STRAIGHT_RUN_STAIR, as in the reference export, instead of undefined; a stair of sketched runs alone stays undefined. The older profile and files without such stairs are unchanged.

## [0.2.0] - 2026-11-04

### Breaking changes

- Default output, current IFC profile (Closes #73): the Width and Height quantities of the IfcOpeningElement of a door or window in a straight wall are now those of the door or window as Revit has them, Width along the wall (the Width of the instance, else of its type) and Height vertical (its Height), with Depth the thickness of the wall, in a wall of any direction. A side the door or window holds no value for is measured from the final opening in the frame of the wall. Before, the sides of the cut loop were named by the frame they are measured in, so a door 0.8 wide and 2.1 high got Height 0.8 and Width 2.1. Openings of curved walls and curtain walls, openings that nothing fills and openings of other fillers stay as they were. To get the earlier values, use the new `--ifc-opening-legacy-axes` command line option or set `IfcExporter.OpeningQuantitiesSwapped` to true. `--ifc-opening-host-axes` (the opt-in of the previous change) is still accepted and does nothing, and `IfcExporter.OpeningQuantitiesInHostAxes` still compiles; it is now true by default and the opposite of `OpeningQuantitiesSwapped`. Refs #6.
- Default output, current IFC profile: the IFC holds only the elements that exist in the last project phase. Elements demolished in the export phase or an earlier one, or created in a later one, are left out, with their property sets, quantity sets, types and relations. To get every element as before, use `--ifc-phase all` or set `IfcExporter.ExportAllPhases` to true; `--ifc-phase <id>` and `IfcExporter.ExportPhaseId` select one phase. Rooms follow the export phase unless a room phase is given. The legacy profile and the JSON output are not filtered by default.
- Default output, IFC quantities: base quantities of beams, columns, members, piles, floors and plates are derived from the surface integrals of the source geometry, and door and window height and width come from their parameters or frame extent before the bounding box. NetVolume, NetSurfaceArea, CrossSectionArea, Length, GrossArea, NetArea and the door and window Height, Width, Area, OverallHeight and OverallWidth can differ from 0.1.0; most values move toward the reference exporter. In the legacy profile members whose length is now derived can also gain the property Span. There is no option that restores the 0.1.0 values.
- Default output, stairs: a stair flight carries NumberOfRiser, NumberOfTreads, RiserHeight and TreadLength and the property set Pset_StairFlightCommon; runs, landings, stringers and railings of a stair are aggregated into the stair (IfcRelAggregates) and are no longer contained in the storey; a landing takes the name and Reference of its type. See also the stair type entry below. There is no option that restores the 0.1.0 layout.
- API: `RvtFileReader.Read` and `RvtDocument.Open` throw `RvtInputException` (or `RvtLimitException` for a stream over the platform array length) for a file that cannot be read, instead of an `EndOfStreamException`, `FileFormatException` or a similar exception from a decoder. Code that catches the old exception types must catch the new ones.
- Command line: the exit codes 2, 3 and 4 are new (see below). A damaged or truncated file ended in an unhandled exception in 0.1.0 (or, when only the end of a stream the export does not use was missing, in an export without a message) and now returns 2 with a one-line message.
- Default output, current IFC profile: the GlobalId of the IfcStairFlightType of a stair run or landing changes, because its seed now contains the run style, and the type gets the predefined type STRAIGHT instead of NOTDEFINED (2 types in the Revit 2025 architecture sample, 4 in the Revit 2024 DACH sample). The IfcRelDefinesByType of those types follows.

### Added

- Exit codes 2 (input cannot be read: missing, empty, not a compound file, cut or damaged), 3 (a stream exceeded a reader limit and the read was cut) and 4 (a decoder failed on a readable file), and the options `--report`, `--allow-partial` and `--raise-limits`. A complete export without them is unchanged. See [the export report](docs/export-report.md). `RvtFileReader.Read` and `RvtDocument.Open` check that every stream of the compound file can be read to its declared end; `RvtReaderOptions.Diagnostics` collects the streams read and the limits that cut one. The report counts partial reads, written reinforcing bars and the assembly written for a beam system, and refuses a report path that names the input or the export output, also through a link.
- The `--ifc-phase <last|all|id>` command line option and the `IfcExporter.ExportPhaseId` and `ExportAllPhases` properties (see the breaking change above).
- The opt-in `RvtReaderOptions.ResolveCurrentCopies` reader option and `--resolve-current-copies` command line option, which keep only the current copy of an element that several partition streams hold. It is off by default and the default output does not change.
- The full JSON, and the IFC property sets written by `--ifc-revit-parameters`, get the parameter CURTAIN_WALL_PANELS_WIDTH (a copy of GENERIC_WIDTH) on elements that hold only the generic width; the full JSON lists it in ParameterDefinitions. CURVE_ELEM_LENGTH values can differ in the last digit. Compact JSON is unchanged.
- The IFC length unit is read from the project length unit in every file layout (including the Revit 2020 display unit layout and family documents), so a file whose length unit is not the millimetre now gets that unit in the IFC (a family in feet gets FOOT, SQUARE FOOT and CUBIC FOOT instead of millimetre, square metre and cubic metre); the reason is reported through diagnostics when it falls back to millimetres.
- Stair runs and landings resolve their type through the stored type association, so the IFC names the real type, writes its stair flight type and property sets, and the stair run Reference. The IFC stair shape of the current profile stays undefined when a stair has a sketched run, and a railing aggregated into a stair no longer takes the classification code of the stair. The run Length quantity keeps the inputs it had. The element class counts of the stair source are taken from the stream walk, which in files with their own class schema lets both profiles recognise the runs and landings of a stair.

### Fixed

- Reads the element table of a file whose partition stream carries padding after the last page segment of a paged gzip member (the Revit 2021 MEP sample project, Closes #8). Before, the table fell back to a decoder that returned extra bytes and the export held about half of the elements (13706 of 27583), most without properties or geometry, and elements that are not in the file. Output of other files is unchanged.
- Paged gzip members are decoded with an exact deflate end, and a missing trailer, a reserved flag or a wrong header CRC is rejected. A damaged or truncated partition stream is rejected by the framing check instead of read past its end.
- Selects the element table and record layout by the release of the file's own schema, the older of it and the saved release, and reports the release in the diagnostics. Decodes the document increment table of a file saved in several increments with a bound on the work it may do; a malformed table is rejected instead of guessed.
- `--ifc-room-phase` and the room phase property accept the phase id 0.
- Takes the length of a curtain wall mullion from the axis of its own symbol, and the footprint of a curved wall from the area of its rebuilt curve loop.
- IFC slab and plate quantities: the NetVolume of floors and of plates (IfcPlate) is taken from the source geometry too; values move toward the reference exporter (floors by 0.01 to 0.2 percent, the curtain louvres of the 2024 DACH sample by 7 percent). Floors that have no vertical prism gain a Width quantity.
- The Width quantity of foundation slabs (pile caps and base slabs), which since the base quantities change took the vertical side of the body instead of the first side of its profile (Closes #78). Other slabs take the world X extent of their vertical prism and fall back to the body only when they have none; this equals 0.1.0 for floors and pile caps, while base slabs and some roofs now equal the reference exporter.
- The NetVolume of round foundation piles whose solids are open shells without measures is written again (Closes #79). It is taken per node, only when the node is a straight prism (two opposite end caps, and at least one side face, all of them flat and parallel to the cap normal or cylinder faces along it, with their points on the cap boundaries), as the larger cap area times the distance of the caps, and added to the measured and closed solids of the foundation; a node below zero and a cutting node take no part, also not for the NetArea. 0.1.0 summed the open shells, which gave values up to ten times too large. A pile whose end caps differ by more than 2 percent still gets no NetVolume (#42).
- The extrusion of an opening that the door or window code rewrites from the final outline can change shape (profile size, extrusion depth, placement direction), independent of `--ifc-opening-legacy-axes`. Seen on one opening of the 2022 rac basic sample; the profile size and extrusion depth now equal those of the reference exporter.
- A door or window with a non-finite dimension is ignored for the opening, and each side of an opening is taken from the door or window independently.
- Legacy IFC profile: the GlobalIds of the type-level IfcRelAssociatesMaterial relations (and of the relations of layer sets) change in files where the output gains or loses entities before them, for example the mullion Span property in the Revit 2022 rac basic sample, because the legacy seed is the STEP number of the relating material. Ids of the current profile do not depend on entity numbers and are unchanged.

### Performance

- Reduces the memory allocated and the processor time spent in the tessellation of trimmed surfaces and in the ordering of boundary rings: grid cells are clipped in reused per-thread buffers and the ring search uses flat arrays instead of per-cell lists and LINQ. The output is unchanged byte for byte.
- Speeds up the export of models with many family instances: the record chunks are scanned for shared parameter GUIDs without an allocation per offset, face corner keys are sorted ordinally, and the faceted shells of the family symbols are planned on several threads before the writer needs them. The output is unchanged byte for byte. The processor time can rise because plans that the writer does not use are computed too, and single-core machines gain nothing.
- Together, the two changes above cut the wall time against 0.1.0 by 17 to 18 percent in single repeated runs of large JSON exports and by 2 to 3 percent in large IFC exports. Peak memory depends on the collector and can be higher than in 0.1.0.

### Project

- Release packaging: `pack-release.ps1` builds the standalone assembly archives, releases are published directly instead of as drafts, and the build and test dependencies and workflow actions are updated.

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
