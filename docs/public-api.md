# Public API

The `RvtIO` package is the supported surface listed here. Everything else (parsers, geometry carriers, schema classes,
census types, the stream decoders) is `internal`; it can change in any release and is visible only to the test and tool
projects through `InternalsVisibleTo`.

## Reading a file

| Type | Use |
|---|---|
| `RvtDocument` | Opens a file (`Open`) and gives the elements, project information, file metadata and category lookups. |
| `RvtFileReader` | Lower level read (`Read`) that returns the `RevitFileInfo` behind a document. |
| `RvtReaderOptions` | Options of a read, including `DiagnosticOutput`. |
| `FilteredElementCollector` | Query over the elements of a document (`OfCategory`, `WhereElementIsNotElementType`, `Where`, ...). |

## Model

`Element`, `Parameter`, `Definition`, `ParameterDefinition`, `StorageType`, `RevitFileInfo`, `FileMetadata`, `ProjectInfo`,
`TransmissionInfo`, `ExternalFileRef`, `ClassSchema`, `ClassField`, `MaterialLayer` and the geometry types (`GeometryData`,
`GeoFace`, `GeoEdge`, `GeoSolid`, `GeoBrepSolid`, `GeoOpening`, `GeoInstance`, `LocationCurve`, `DimensionData`,
`InstanceTransform`) are the data a read returns. `ParameterListConverter` is the `System.Text.Json` converter that writes
the parameter lists of a `RevitFileInfo`.

## Built-in identifiers

`BuiltInCategory`, `BuiltInParameter`, `BuiltInParameterGroup`, `ParameterType`, `UnitType`, `DisplayUnitType`,
`UnitSymbolType`, `UnitGroup`, `MEPSystemClassification`, `FlowDirectionType`, `PipeFlowConfigurationType` and their
extension and label helpers carry the ids and names stored in the file format.

## Export

| Type | Use |
|---|---|
| `IfcExporter` | Writes IFC from a `RevitFileInfo` (`Export`). Properties: `Profile`, `BaseQuantities`, `ExportPhaseId`, `ExportAllPhases`, `RoomExportPhaseId`, `ExportRoomsOfAllPhases`, `RevitParameterGroups`, `DiagnosticOutput`. |
| `IfcProfile` | `Legacy` or `Current` (the default) output profile. |
| `IfcUnitFamily` | The length unit family an export declares. |
| `CompactJsonExporter` | `Export(rvtPath, outputPath, readerOptions, options)` reads a file and writes the compact JSON. |
| `CompactJsonExportOptions` | `JsonClassNamePrefix`: text put in front of every class name in the `name` field. |

## Diagnostics

The library never writes to the console. A diagnostic message is written only when the caller gives a `TextWriter`:
`RvtReaderOptions.DiagnosticOutput` for a read (and for `CompactJsonExporter.Export`), `IfcExporter.DiagnosticOutput` (or the
constructor argument) for an IFC export. The default is `null`, which is silent. The writer is scoped to the call that
received it, so concurrent calls with different writers do not mix.

The `rvtio` command line tool sets the writer to standard error.
