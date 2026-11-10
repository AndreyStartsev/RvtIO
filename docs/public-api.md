# Public API

The `RvtIO` package is the supported surface listed here. Everything else (parsers, geometry carriers, schema classes,
census types, the stream decoders) is `internal`; it can change in any release and is visible only to the test and tool
projects through `InternalsVisibleTo`.

## Reading a file

| Type | Use |
|---|---|
| `RvtDocument` | Opens a file (`Open`) and gives the elements, project information, file metadata and category lookups. |
| `RvtFileReader` | Lower level read (`Read`) that returns the `RevitFileInfo` behind a document. |
| `RvtReaderOptions` | Options of a read, including `DiagnosticOutput`, `ResolveCurrentCopies` (keeps only the current copy of an element that several partition streams hold; default true, set it to false to decode every copy), `RaiseLimits` and `Diagnostics`. `RevitLocalizationDirectory` is `[Obsolete]` and ignored: the texts are built in. |
| `RvtReadDiagnostics`, `StreamRecord`, `ReaderLimitHit` | What a read collects when `RvtReaderOptions.Diagnostics` is set: the partition streams read and the streams cut by a limit. |
| `ExportReport`, `SkipLog` | The machine-readable report of a run (`ToJson`) and the elements an export left out; see [the export report](export-report.md). |
| `RvtExitCodes`, `RvtInputException`, `RvtLimitException`, `RvtContainerCheck` | The exit codes 0 to 4 of the command line tool (code 5 is returned only by `downgrade --check`), the exceptions `RvtFileReader.Read` throws for a file it cannot read, and the check that raises them. |
| `FilteredElementCollector` | Query over the elements of a document (`OfCategory`, `WhereElementIsNotElementType`, `Where`, ...). |

## Model

`Element`, `Parameter`, `Definition`, `ParameterDefinition`, `StorageType`, `RevitFileInfo`, `FileMetadata`, `ProjectInfo`,
`TransmissionInfo`, `ExternalFileRef`, `ClassSchema`, `ClassField`, `MaterialLayer` and the geometry types (`GeometryData`,
`GeoFace`, `GeoEdge`, `GeoSolid`, `GeoBrepSolid`, `GeoOpening`, `GeoInstance`, `LocationCurve`, `DimensionData`,
`InstanceTransform`) are the data a read returns. `ParameterListConverter` is the `System.Text.Json` converter that writes
the parameter lists of a `RevitFileInfo`.

`Element.BoundingBox` is the axis-aligned outline stored in the element header, as the Revit API bounding box (null for elements with no stored outline, such as grids and levels). The tight box of the decoded geometry is `Element.Geometry.BoundingBox`; before 0.3.0 `Element.BoundingBox` held that decoded value, which also changes the result of `FilteredElementCollector.WhereBoundingBoxIsNotNull`.

## Built-in identifiers

`BuiltInCategory`, `BuiltInParameter`, `BuiltInParameterGroup`, `ParameterType`, `UnitType`, `DisplayUnitType`,
`UnitSymbolType`, `UnitGroup`, `MEPSystemClassification`, `FlowDirectionType`, `PipeFlowConfigurationType` and their
extension and label helpers carry the ids and names stored in the file format.

## Export

| Type | Use |
|---|---|
| `IfcExporter` | Writes IFC from a `RevitFileInfo` (`Export`). Properties: `Profile`, `BaseQuantities`, `OpeningQuantitiesSwapped` (true writes the legacy Width and Height of door and window openings; default false), `OpeningQuantitiesInHostAxes` (the opposite of it, kept for compatibility), `ExportPhaseId`, `ExportAllPhases`, `RoomExportPhaseId`, `ExportRoomsOfAllPhases`, `RevitParameterGroups`, `SkipLog`, `ExportedProducts`, `DiagnosticOutput`. |
| `IfcProfile` | `Legacy` or `Current` (the default) output profile. |
| `IfcUnitFamily` | The length unit family an export of the current profile declares, by the length format of the project: `Metre` for metres and for metres and centimetres, `Foot` for feet, feet and fractional inches and US survey feet, `Millimetre` for millimetres, for every other format and for an unreadable one. The legacy profile always declares millimetres. |
| `CompactJsonExporter` | `Export(rvtPath, outputPath, readerOptions, options)` reads a file and writes the compact JSON. |
| `CompactJsonExportOptions` | `JsonClassNamePrefix`: text put in front of every class name in the `name` field. |

## Diagnostics

The library never writes to the console. A diagnostic message is written only when the caller gives a `TextWriter`:
`RvtReaderOptions.DiagnosticOutput` for a read (and for `CompactJsonExporter.Export`), `IfcExporter.DiagnosticOutput` (or the
constructor argument) for an IFC export. The default is `null`, which is silent. The writer is scoped to the call that
received it, so concurrent calls with different writers do not mix.

The `rvtio` command line tool sets the writer to standard error.
