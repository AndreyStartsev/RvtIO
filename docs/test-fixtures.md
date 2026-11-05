# Private test fixtures

Some tests need input that cannot be published: Revit project files, class blocks captured from project files, and
dumps made from them. Those tests find their input through one helper, `TestFiles` in the test project, and are reported
as **skipped with a reason** when it is missing. They never pass silently and never fail for lack of a file.

## Where the files are looked for

1. The folder named by the environment variable `RVTIO_TEST_FILES`.
2. A folder `test_files` in the repository root (or in any parent of the test binary folder).

A file may lie directly in the folder or in one level of subfolders (`2020`, `2022`, `2025`, ...). The expected content:

| Path under the fixture folder | Used for |
|---|---|
| `2022/TESTFILE.rvt` | the main project file of the integration tests |
| `2020/Test_2020.rvt`, `2022/Test_2022.rvt`, `2025/Test_2025.rvt` | schema, element table and name tests of three Revit versions |
| `bim_dump.txt` | the dump of an independent BIM reader for `TESTFILE.rvt`, read by `ReferenceApiTests` |
| `formats_latest_decompressed.bin` | the decompressed `Formats/Latest` stream, read by `FormatsLatestAnalysisTests` |
| `TestData/*.bin` | class blocks captured from project files (38 files, below) |
| `TestData/compact_label_vectors.tsv` | only to re-derive `label_vectors.tsv` (see `docs/frozen-resource-fixtures.md`) |

`PageCodecSampleTests` reads every `.rvt` file in the folder and in one level of subfolders, whatever its name, and skips streams over
128 MB.

`ContainerRewriterSampleTests` copies every `.rvt` file in the folder and in one level of subfolders to a temporary file (deleted after
each file; it needs free disk space for the largest file) and compares the compact JSON of the copy with that of the source for the ten largest files of up to 8 MB.

The attributes `[SkippableFact]` and `[SkippableTheory]` (`SkippableFact.cs`) report a test that throws `SkipException` as
skipped; xunit 2.9.3 has no runtime skip of its own. `TestFiles.Find`, `FindRelative` and `OpenBlock` throw it, so a test
needs no code of its own to skip. Tests that write diagnostic dumps write them to `rvtio-tests` in the system temporary
folder (`TestFiles.Scratch`); no test uses a fixed absolute path.

## What runs without the private files

With no private file at all, 9244 of the 9608 test cases run and pass and 364 are skipped: 330 need the private files listed
below and 34 need optional inputs (the original resource files of `docs/frozen-resource-fixtures.md`, `bim_dump.txt`,
`formats_latest_decompressed.bin`). The tests that run use synthetic data built in the tests, the frozen hash fixtures
and the small fixtures committed under `tests/RvtIO.Tests/Fixtures`. With the full private fixture folder the
remaining cases run as well.

Run with the private files: `RVTIO_TEST_FILES=<folder> dotnet test` (PowerShell: `$env:RVTIO_TEST_FILES = '<folder>'`).

## Tests that need the private files

223 test methods (330 cases, theory rows counted) are skipped without the files:

| Class | Methods | Cases | Files |
|---|---|---|---|
| `ApiTests` | 47 | 47 | TESTFILE.rvt |
| `BinarySearchTests` | 4 | 4 | TESTFILE.rvt |
| `BlockDiagnosticTests` | 5 | 5 | TESTFILE.rvt |
| `CompactFormatSettingsTests` | 8 | 23 | TESTFILE.rvt |
| `CompactIdentityTests` | 1 | 1 | Test_2022.rvt, TESTFILE.rvt |
| `CompactIfcParametersTests` | 1 | 1 | Test_2022.rvt |
| `CompactOwnValueRulesTests` | 4 | 5 | Test_2022.rvt, TESTFILE.rvt |
| `CompactPropertySourcesTests` | 4 | 4 | Test_2022.rvt, TESTFILE.rvt |
| `CompactSolidValueTests` | 5 | 6 | Test_2022.rvt, TESTFILE.rvt |
| `CompactUserParametersTests` | 2 | 2 | Test_2022.rvt |
| `CurtainPanelOrderTests` | 3 | 3 | TestData blocks |
| `CurtainRoofReferenceSourceTests` | 4 | 16 | TestData blocks |
| `DocumentExportIdTests` | 1 | 1 | TESTFILE.rvt |
| `ElemTableAuthorityTests` | 2 | 2 | TESTFILE.rvt |
| `ElementExportIdTests` | 3 | 6 | TESTFILE.rvt |
| `ElementHeaderCategoryTest2022Tests` | 2 | 2 | TESTFILE.rvt, Test_2022.rvt |
| `ElementHeaderCategoryTestFileTests` | 1 | 1 | TESTFILE.rvt |
| `EpisodeGuidTests` | 2 | 2 | TESTFILE.rvt |
| `FormatsSchemaTests` | 3 | 5 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt |
| `GlobalStorageDecodeTests` | 2 | 4 | TESTFILE.rvt |
| `IfcCurrentFamilyHostTests` | 1 | 3 | TestData blocks |
| `IfcCurrentRoofTypeReferenceTests` | 1 | 2 | TestData blocks |
| `IfcCurrentSourcePlaneSlopeTests` | 1 | 4 | TestData blocks |
| `IfcExporterPlainWallBodyCurrentTests` | 4 | 4 | TestData blocks |
| `ReferenceApiTests` | 2 | 2 | TESTFILE.rvt, bim_dump.txt |
| `ArcProfileTests` | 1 | 1 | TestData blocks |
| `EdgeBoundsTests` | 3 | 15 | TestData blocks |
| `FaceAreaTests` | 2 | 2 | TestData blocks |
| `ValidatedTests` | 72 | 89 | TESTFILE.rvt |
| `ProfileRoofAreaTests` | 3 | 11 | TestData blocks |
| `ValidatedElementNamesTest2022Tests` | 2 | 2 | Test_2022.rvt, TESTFILE.rvt |
| `R22TranscoderTests` | 2 | 2 | Test_2025.rvt, TESTFILE.rvt |
| `RailingQuantitySourceTests` | 4 | 18 | TestData blocks |
| `SchemaModelTests` | 1 | 1 | Test_2022.rvt |
| `SpaceBoundaryLiteralTests` | 5 | 5 | TestData blocks |
| `TfSchemaToolsTests` | 3 | 6 | Test_2020.rvt, Test_2022.rvt, Test_2025.rvt |
| `UserParameterCatalogTests` | 1 | 1 | Test_2022.rvt, TESTFILE.rvt |
| `VkConnectorBlockTests` | 6 | 6 | TestData blocks |
| `WideBlockWalkerTests` | 3 | 14 | Test_2025.rvt, TESTFILE.rvt |
| `WideElementRecordsTests` | 2 | 2 | Test_2025.rvt, TESTFILE.rvt |

`ReferenceApiTests` has 16 more methods that need `bim_dump.txt`; they are skipped when it is missing and, together with
`FormatsLatestAnalysisTests` (needs `formats_latest_decompressed.bin`), account for 17 of the 34 cases that stay skipped
with a fixture folder that lacks those two files.

## Class blocks (`TestData`)

The 38 captured blocks were removed from the repository: they are pieces of third-party project files that cannot be published. The 13 test classes that read
them (38 methods, 90 cases depend on a block) were not given synthetic replacements. Their assertions are exact values decoded from the real records (areas,
slopes, names, counts of faces and connectors), and the code under test walks the record layout of a real Revit class,
so a synthetic byte sequence would need a writer for those layouts and different expected values: it would not be the
same test. The tests therefore read the blocks from `TestData` of the fixture folder. In the block names, `sample-a` stands for one sample architectural project, followed by the element kind, the element id and the part of the record that was captured:

| Class | Blocks |
|---|---|
| `CurtainPanelOrderTests` | `sample_a_curtain_wall_9169323.bin` |
| `CurtainRoofReferenceSourceTests` | `sample-a-curtain-roof-2158349`, `-2698440`, `-4066652`, `-4201316`, `sample-a-curtain-roof-type-16025` |
| `IfcCurrentFamilyHostTests` | `sample-a-family-314844`, `-363949`, `-363968` |
| `IfcCurrentRoofTypeReferenceTests` | `sample-a-roof-type-9792925` |
| `IfcCurrentSourcePlaneSlopeTests` | `sample-a-support-6957681-grep`, `-6957682-grep`, `-6957698-grep`, `-6957699-grep` |
| `IfcExporterPlainWallBodyCurrentTests` | `sample-a-wall-2520908-hole`, `sample-a-wall-638404-cut`, `sample-a-wall-9751957-round` |
| `ArcProfileTests` | `sample-a-wall-9765668-grep` |
| `EdgeBoundsTests` | `sample-a-wall-8954843-grep`, `-8959579-grep`, `-9750515-grep`, `-9750549-grep`, `-9765007-grep`, `-9765665-grep`, `-9765668-grep`, `-9766112-grep` |
| `FaceAreaTests` | `sample-a-room-8964053-curved`, `sample-a-wall-8961372-arc` |
| `ProfileRoofAreaTests` | `sample-a-roof-7311849-root`, `sample-a-roof-7311849-grep` |
| `RailingQuantitySourceTests` | `sample-a-railing-7583306-root`, `-7583306-symbol`, `sample-a-railing-2653815-symbol` |
| `SpaceBoundaryLiteralTests` | `sample-a-room-2766296-fourfaces`, `sample-a-room-5992163-sliver`, `sample-a-room-8964053-curved` |
| `VkConnectorBlockTests` | `vk_flex_pipe_9123013`, `vk_proxy_directional_5919313`, `vk_proxy_mixed_domain_2823056` |

(The blocks listed without extension are `.bin` files.) The remaining tests of these classes, which build their input in the
test, still run everywhere. The label vector table of `CompactLabelsTests` is not private: its 362 rows are frozen as hashes (`docs/frozen-resource-fixtures.md`) and the 362 theory rows
run everywhere.

## Inline captures

Some tests carry small byte captures inline, as base64 or hex strings in the source (for example `BaseRailingSerializationTests`,
`IfcExporterCoveringCommonPropertySetTests`, `WallFaceLoopTests`, `PlanarSpace*Tests`, `StairElementSerializationTests`). They run everywhere.
Every capture was decoded, including the compressed and nested ones, and checked for personal data. Texts that identify a person, an
organisation or a local path were replaced by neutral text of the same length, so that the record layouts and all assertions stay the same.
Expected values that come from private project files and could identify their source are compared by SHA-256 instead of by text.
