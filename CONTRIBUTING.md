# Contributing to RvtIO

## Reporting bugs

Open a [GitHub issue](https://github.com/mahach666/RvtIO/issues) with:

- The Revit version that saved the file.
- The rvtio version.
- The command line used, with confidential values removed.
- A minimal description of how to reproduce the problem.
- Expected and actual results, including relevant error messages.

Do not attach Revit files if they contain confidential data.
A minimal description is better than sharing a confidential model.

Report security issues through GitHub private vulnerability reporting in
the Security tab. Do not open public issues for security vulnerabilities.

## Proposing changes

Open an issue first for larger changes so the scope and approach can be
discussed before implementation. Keep changes focused on the agreed problem.

## Development setup

Install the .NET SDK specified by `global.json`. It currently specifies
10.0.100, allows roll-forward to the latest feature band, and excludes
prerelease SDKs.

From the repository root, run:

```sh
dotnet build RvtIO.slnx -c Release
dotnet test
```

The library is in `src/RvtIO`, the CLI in `src/RvtIO.Cli`, and tests in
`tests/RvtIO.Tests`.

Tests that need real Revit files are skipped with a message when private
fixtures are unavailable. Set `RVTIO_TEST_FILES` to the folder containing
your private fixtures to run them. The repository ships no Revit models.

## Code conventions

- Follow the surrounding code and repository formatting settings.
- Keep nullable reference types enabled.
- Warnings are treated as errors in `src`.
- Write short English comments that explain why.
- Document public APIs with XML documentation comments.
- Do not write console output from the library. Use the `DiagnosticOutput`
  sinks for diagnostics. The library must remain silent by default.

## Validating exports

Export outputs must not change unintentionally.

Before changing export behavior, export the same input files with the
existing code and keep the results outside the repository. Export those
files again after the change, using the same options. Compare the before
and after IFC and JSON outputs, investigate differences, and explain
intentional changes in the pull request.

## Pull requests

Include:

- A description of the problem and the change.
- Tests for behavior changes, including relevant errors and edge cases.
- The build and test commands run, their results, and any skipped checks.
- Before and after export comparison results when exports are affected.

Do not add Revit models, customer data, vendor code, or binaries to the
repository. Keep private test fixtures outside version control.

Write commit messages in the imperative mood, for example,
"Fix room export phase selection".

## Releases

Push a tag matching the package version, such as `v0.2.0`, to run `release.yml`.
The workflow builds and tests the solution, attaches packages, symbols, source and checksums to a draft preview release,
and publishes the NuGet packages through Trusted Publishing. Review and publish the GitHub draft separately.
The NuGet policy must name this repository, `release.yml`, the configured NuGet profile and the `RvtIO*` package pattern.

## Licensing contributions

Contributions are licensed under the MIT license. By submitting a
contribution, you confirm that you have the right to contribute it under
that license.

## Questions

Ask questions in GitHub Discussions or repository issues.
