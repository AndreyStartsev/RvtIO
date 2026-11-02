# Export report and exit codes

The command line exporter tells a complete export from a partial one by its exit code and, on request, by a JSON report.
Without the new options the output (IFC, JSON, standard error text) and the exit code of a complete export are unchanged.

## Options

| Option | Effect |
| --- | --- |
| `--report <report.json>` | Write the JSON report of the run to this path. Written on success and on failure. |
| `--allow-partial` | Export what was read when a reader limit cut a stream. The exit code is then 0 and the report says `"status": "partial"`. |
| `--raise-limits` | Raise the limits listed below for a very large model. |

## Exit codes

| Code | Name | Meaning |
| --- | --- | --- |
| 0 | success | The export was written. With `--allow-partial` or after members of a stream failed to decode, the report can still say `"status": "partial"`: read it when you need to know. |
| 1 | usage | The command line is wrong (unchanged from earlier versions). |
| 2 | input error | The input cannot be read: the file does not exist or cannot be opened, is empty, is not a compound file, or is cut or damaged so that a stream ends before its declared length. One line on standard error, no stack trace, nothing exported. |
| 3 | limit exceeded | A stream exceeded a limit of the reader and the read was cut. Nothing is exported unless `--allow-partial` is given. A stream longer than the platform allows in one array cannot be read at all: that case exits with 3 even with `--allow-partial`. |
| 4 | decode error | The compound file is readable but a decoder failed on its content (damaged or unsupported data). One line on standard error; the report holds the exception with its stack trace. |

A crash that is none of these (a defect in the exporter after the read) still ends the process with the runtime's unhandled exception code.

## Limits of the reader

These are the limits the reader has. A stream that reaches one is cut, listed under `limits.hit` and counted as partial.

| Code | Default | Raised by `--raise-limits` | What is cut |
| --- | --- | --- | --- |
| `elem-table-recovery-bytes` | 268435456 (256 MiB) | the platform array length | The output of the recovery of a damaged `Global/ElemTable`. The table keeps what the ordinary decoders produced. |
| `elem-table-recovery-iterations` | 4096 | unbounded (each iteration moves forward in the stream) | The number of relocks the recovery of a damaged `Global/ElemTable` makes. |
| `array-length` | the platform array length | cannot be raised | A stream of the compound file longer than one array. Exit code 3 whatever the options. |

## Report format

The report is one JSON object, UTF-8, property names in camelCase. `schema` names the format version: `rvtio-report/1`. New properties can be added within a version; a property is never removed or changed in meaning.

```json
{
  "schema": "rvtio-report/1",
  "status": "complete",
  "exitCode": 0,
  "input": { "file": "project.rvt", "bytes": 9293824 },
  "limits": { "raised": false, "partialAllowed": false, "hit": [] },
  "streams": [
    { "name": "Partitions/0", "rawBytes": 8187086, "members": 695, "decoded": 695,
      "failed": 0, "recovered": 0, "unverified": 0, "decodedFraction": 1 }
  ],
  "elements": { "inTable": 8135, "decoded": 8135, "decodedFraction": 1, "undecoded": [] },
  "issues": [],
  "export": {
    "format": "ifc", "considered": 59149, "exported": 28, "skipped": 59121, "exportedFraction": 0.000473,
    "skippedByClass": [
      { "class": "OST_Sheets", "reason": "record-outside-exact-element-table", "count": 17580, "examples": [173028, 173029] }
    ]
  }
}
```

| Property | Meaning |
| --- | --- |
| `status` | `complete`: no limit cut a stream and every member of every partition stream decoded and verified. `partial`: a limit cut a stream, a member did not decode, a member decoded without its checksum matching, or the element table decoded no row. `failed`: the file could not be read at all (exit codes 2 and 4, and 3 for a stream over the platform array length). A read cut by a limit that is not accepted (exit code 3) is `partial` and has no `export` property, because nothing was exported. |
| `exitCode` | The exit code of the process. |
| `input.file`, `input.bytes` | File name (no directory) and size of the input. |
| `error` | Only when `status` is `failed`: `kind` (`cannot-open`, `empty-file`, `not-a-compound-file`, `truncated-or-damaged`, `limit-exceeded`, `decode-failure`), `message`, and for `decode-failure` the `detail` with the stack trace. |
| `limits.raised`, `limits.partialAllowed` | Whether `--raise-limits` and `--allow-partial` were given. |
| `limits.hit[]` | Every stream cut by a limit: `stream`, `limit` (code from the table above), `value` (the limit in force), `raisable`, `detail`. |
| `streams[]` | The partition streams. `members` are the gzip members the stream is cut into; `decoded` produced output; `failed` produced none and their data is missing; `recovered` were rescued by a structural relock; `unverified` were kept although their checksum did not match or was never checked, which includes every rescued member. `decodedFraction` is `decoded / members`. |
| `elements.inTable`, `elements.decoded` | Rows of the element table, and those with a parsed record. `decodedFraction` is their quotient (1 for an empty table). |
| `issues[]` | Other findings that make a read `partial`, as `stream`, `code`, `detail`. The one code is `no-rows`: the element table decoded no row (a project always has elements, so the table did not decode). |
| `elements.undecoded[]` | Rows of the element table without a parsed record, as `class`, `reason`, `count`, `examples`. The class of an element that never decoded is `unknown`. |
| `export.format` | `ifc`, `json` (the compact export) or `json-full`. |
| `export.considered`, `exported`, `skipped` | Parsed elements the export looked at, wrote, and left out, counted as the exporter counts them. `exportedFraction` is `exported / considered`. |
| `export.skippedByClass[]` | Every skipped element class with its reason, count and up to five element ids (`examples`), largest first. The counts add up to `export.skipped`. |

A complete input also has skipped elements: the exporters leave out element types, annotations, records that only live inside other records and similar. `exportedFraction` is therefore not a measure of lost data; `status`, `streams` and `elements` are. The reasons below name the rule that left the element out.

### Reasons of an IFC export

`terrain-or-group-topography`, `block-only-record`, `group-template`, `record-outside-exact-element-table`, `owned-by-family-nonspatial`, `rebar-product-not-written`, `only-unfilled-faces`, `type-element`, `non-physical-category`, `no-project-phase-reference`, `level-is-not-a-project-level`, `wall-not-visible`, `curtain-panel-wall`, `curtain-child-without-bounds`, `opening-of-hidden-wall`, `no-ifc-class-for-category`, `opening-of-wall-without-body-openings`, `stacked-wall-container`, `no-geometry-or-location`, `hosted-element-without-host`, `no-geometry-or-bounds`, `phantom-storey-wall`, `unplaced-owner-proxy`, `open-surface-hosted-cutter-proxy`, `geometryless-product`.

### Reasons of a JSON export

The compact export writes the rows of the element table: `block-only-record` and `record-outside-exact-element-table` are the two reasons an element is left out. The full dump (`--json-full`) writes every parsed element.

## Scripting

```
rvtio project.rvt project.ifc --report project.report.json
case $? in
  0) ;;                                   # done; check "status" in the report if a partial read matters
  2) echo "the file is cut or damaged" ;;
  3) echo "limit exceeded: retry with --raise-limits" ;;
  4) echo "the content could not be decoded" ;;
esac
```
