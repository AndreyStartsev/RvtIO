# Dependencies

All package versions live in `Directory.Packages.props` (central package management); project files carry no versions.
Build settings shared by every project live in `Directory.Build.props`; the SDK is pinned in `global.json`
(`10.0.100`, `rollForward: latestFeature`, no prereleases).

## Runtime packages (library)

| Package | Version | Used for |
|---|---|---|
| OpenMcdf | 3.4.0 | Reading the compound file container of `.rvt` / `.rfa` files. |
| SharpZipLib | 1.4.2 | Raw deflate (`Inflater`, `InflaterInputStream`) and `Adler32` for the chunk streams. |

### Decisions

- **OpenMcdf 3.1.0 -> 3.4.0.** 3.1.0 is affected by GHSA-jxpf-xq2m-q525 (fixed in 3.1.3) and GHSA-5qwm-7pvp-w988
  (fixed in 3.1.4): both are unbounded loops on a crafted directory tree with a cycle. The library opens untrusted
  files and enumerates and opens streams, so the code path is reachable and a patched release is required.
  3.4.0 is the newest release on nuget.org; the exported IFC and JSON are byte-identical to the previous version
 .
- **SharpCompress removed.** It was referenced by the library but no source file uses it (no `SharpCompress`
  namespace appears anywhere in `src`, `tests` or `tools`). Dropping it also removes GHSA-6c8g-7p36-r338 (a
  directory traversal in archive extraction, fixed in 0.48.0), which was not reachable in any case.
- **SharpZipLib stays at 1.4.2.** It is the newest release, has no known advisory, and is used directly
  (`ChunkDecompressor`, `OwnPartitionParts`, one CLI diagnostic command, tests). Replacing it with
  `System.IO.Compression` was not done: it would risk behavior changes on corrupt or truncated streams
  (the inflater is driven with `noHeader: true` and explicit partial-read handling) for no security gain.

## Runtime packages (scene adapter)

| Package | Version | Used for |
|---|---|---|
| IOScene | `[0.1.0, 0.2.0-0)` | The scene model (`SceneDocument`) that `RvtIO.Scene` builds; `SceneJson` of IOScene saves it as JSON. |

Any 0.1 release from 0.1.0 on is accepted; the `-0` upper bound keeps out the previews of the next breaking line.
That prerelease bound raises NU5104 when packing, which `RvtIO.Scene.csproj` suppresses, and `tools/pack-release.ps1`
fails a release whose lower bound is missing or a preview.

## Test packages

Pinned to exact versions: Microsoft.NET.Test.Sdk 18.11.1, xunit 2.9.3 and xunit.runner.visualstudio 4.0.1.
The test SDK and Visual Studio adapter are updated together; the xUnit test framework remains at 2.9.3.
The tests project also references OpenMcdf directly (it opens containers itself).

## Auditing

`NuGetAudit` is on with `NuGetAuditMode=all` (direct and transitive) and `NuGetAuditLevel=low`, so every known
advisory is reported as an NU1901-NU1904 warning at restore. Warnings are not promoted to errors.
Check manually:

    dotnet list package --vulnerable --include-transitive
    dotnet list package --outdated

After updating the test SDK and adapter, the audit reports no vulnerable packages in any project.
Re-run the audit when changing dependencies.

## Build settings

`Directory.Build.props` sets `LangVersion` 14.0, `Nullable` enable, `ImplicitUsings` enable, `Deterministic` true,
`EmbedUntrackedSources` true, `PublishRepositoryUrl` true, `VersionPrefix` 0.4.0, symbol packages (`snupkg`),
and `ContinuousIntegrationBuild` true when `CI`, `GITHUB_ACTIONS` or `TF_BUILD` is set. `Microsoft.SourceLink.GitHub`
is a build-time dependency only (`PrivateAssets` all). `tests/Directory.Build.props` imports it and turns
`GenerateDocumentationFile` off. The library, the CLI and the tests target `net8.0` and `net10.0`.
