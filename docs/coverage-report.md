# Coverage report

`rvtio coverage` states, for one Revit file, how many records of its partition streams are walked to their last byte by the class
schema the file carries about itself. It is a yardstick for the decoding work: the same file gives the same numbers on every run of
the same version of the tool. The command reads the file and writes nothing but the report.

```
rvtio coverage project.rvt --report project.coverage.json
```

```
File: project.rvt (saved by release 2022, schema of release 2022, 4307 classes, element id of 4 bytes)
Streams: 20, 9293824 raw bytes; partition streams framed 1 of 1; paged streams decoded 15 of 15
project 101: 8135 records, 8135 exact (100.00%); 1301600 payload bytes, 1301600 exact (100.00%)
project 102: 8135 records, 7904 exact (97.16%); 5120448 payload bytes, 4411203 exact (86.14%)
project 103: 8135 records, 8135 exact (100.00%); 97620 payload bytes, 97620 exact (100.00%)
family 101: 2410 records, 2410 exact (100.00%); 385600 payload bytes, 385600 exact (100.00%)
family 102: 2410 records, 2377 exact (98.63%); 911240 payload bytes, 880112 exact (96.58%)
family 103: 2410 records, 2410 exact (100.00%); 28920 payload bytes, 28920 exact (100.00%)
Not exact, top 15:
  102 project Alpha: 190 of 412; end of payload at Alpha.m_items (171); short by 4 bytes (19)
  102 project Beta: 41 of 41; class tag 5000 at Beta.m_pData (41)
  102 family Alpha: 33 of 60; end of payload at Gamma.m_value, in Gamma (33)
```

The numbers and class names of this example are made up.

## Options

| Option | Effect |
| --- | --- |
| `--report <report.json>` | Write the JSON report to this path. Written when the file was measured and when it cannot be read. The path must not name the input file or a directory. |
| `--top <n>` | The number of class lines printed under `Not exact` (default 15). `0` prints none. The report always holds every class. |

`coverage` must be the first argument. The file name is printed and reported without its directory.

## What is counted

**Streams.** Every stream of the compound file is listed, the storages walked recursively, with its path and raw length:

- `Partitions/<number>` is a partition stream. Its framing (content blocks, segments, gzip members, record lengths, the owner census of
  each block) is checked in full; one violation fails the whole stream.
- Every other stream of the storages `Formats` and `Global` is a paged stream: one gzip member behind page tails. It counts as decoded
  when the member inflates and its CRC-32 and length match.
- Every other stream is plain: only its length is listed.

**Records.** A partition stream holds content blocks: the first one is the project's own block, every other one is the document of one
loaded family. A block holds records on three channels, 101, 102 and 103, and one record per channel for each element id it owns. The
body of a record opens with a 2-byte class tag. Every record of every partition stream whose framing holds is counted, by block kind
(`project` or `family`), channel and class. The item that ends the chain of a channel (its owner id is all ones and it has no body) is
not a record: it is counted as a terminator of its group and in no other number.

A partition stream whose framing does not hold adds no record at all, also not the records in front of the violation. That is a
measured fact, not an error: the reason stands in the row of the stream and the exit code stays 0.

## What exact means

A record is **exact** when the walk of its body by the class schema of the file ends at the last byte of the body: the fields of the
class its tag names, base class first, and then every object those fields queue, in queue order. The walk reads nothing but the
schema and the body. It never scans for a position, never resynchronises and never skips what it cannot read: at the first thing it
cannot read it stops, and the record is not exact.

Exact means that every byte of the record was assigned to a field of the schema. It does not mean that the value of a field is
understood, or that the record is exported.

For a record of channel 102 or 103, the value stored in front of its length is compared with the Adler-32 checksum of its body. A
difference is counted as a checksum mismatch; it does not change whether the record is exact.

## Stop reasons

Every record that is not exact has one reason. A reason that comes from a field ends with ` at <Class>.<field>`, the field that was
being read, and with `, in <Class>` when that field belongs to a queued object and not to the fields of the record's own class.

| Reason | Meaning |
| --- | --- |
| `short by N bytes` | The walk ended without an error, N bytes (1 to 64) before the end of the body. |
| `short by more than 64 bytes` | The same, with more than 64 bytes left. |
| `no class tag` | The body has fewer than 2 bytes. Nothing is walked. |
| `class N has no schema` | The schema does not declare class N: the class tag of the record, a base class, or the class of an inline structure. |
| `end of payload` | A field needs more bytes than the body has left. |
| `class tag N` | A pointer names class N, which the schema does not declare. |
| `count` | The count of an array is above 5,000,000. The count itself is left out, so that all such records share one reason. |
| `text length` | A text is longer than 100,000 characters. |
| `type N` | The field has a type code the walk does not read. |
| `template field` | A template field without exactly one argument of a fixed count. |
| `depth` | Inline structures nest deeper than 40 levels. |
| `queue` | The record queues more than 1,000,000 objects. |
| `step budget` | The walk read more objects and scalars than 16 times the length of the body plus 65,536. A walk that is out of step with the data can meet a large count of items that take no bytes; the budget ends it. |

## Report format

The report is one JSON object, UTF-8, property names in camelCase. `schema` names the format version: `rvtio-coverage/1`. A
property without a value is left out.

```json
{
  "schema": "rvtio-coverage/1",
  "status": "ok",
  "file": "project.rvt",
  "savedRelease": 2022,
  "schemaRelease": 2022,
  "idWidth": 4,
  "classCount": 4307,
  "streams": [
    { "path": "BasicFileInfo", "kind": "plain", "rawBytes": 1212 },
    { "path": "Formats/Latest", "kind": "paged", "rawBytes": 261349, "decoded": true, "inflatedBytes": 1310844 },
    { "path": "Partitions/1", "kind": "partition", "rawBytes": 8187086, "framing": "ok", "blocks": 14, "familyBlocks": 13,
      "segments": 96, "inflatedBytes": 8479000, "records": 31635 }
  ],
  "records": {
    "totals": { "items": 31635, "exact": 31371, "payloadBytes": 7845428, "exactBytes": 7105055, "checksumMismatches": 0, "terminators": 42 },
    "groups": [
      { "block": "project", "channel": 102, "items": 8135, "exact": 7904, "payloadBytes": 5120448, "exactBytes": 4411203,
        "checksumMismatches": 0, "terminators": 1 }
    ],
    "classes": [
      { "block": "project", "channel": 102, "class": "Alpha", "items": 412, "exact": 222, "payloadBytes": 310400, "exactBytes": 150112,
        "checksumMismatches": 0,
        "stops": [
          { "reason": "end of payload at Alpha.m_items", "count": 171, "owners": [1204, 1207, 1311] },
          { "reason": "short by 4 bytes", "count": 19, "owners": [977, 981, 1002] }
        ],
        "otherStops": 0 }
    ]
  }
}
```

| Property | Meaning |
| --- | --- |
| `status` | `ok`: the file was measured. `unreadable`: it cannot be read; the report then holds only `schema`, `status`, `kind`, `message` and `file`. |
| `kind`, `message` | Only when `status` is `unreadable`: `cannot-open`, `empty-file`, `not-a-compound-file`, `truncated-or-damaged` (also a file whose class table `Formats/Latest` is missing or does not parse) or `limit-exceeded` (a stream longer than the platform holds in one array), and the text of the error. |
| `file` | File name of the input, without its directory. |
| `savedRelease` | The release that saved the file, from `BasicFileInfo`; left out when it is not stated. |
| `schemaRelease` | The release of the class table of the file; left out when it is not recognised. |
| `idWidth` | Byte width of an element id in the records, 4 or 8, by the older of the two releases; left out when neither is known. |
| `classCount` | Classes of the class table. |
| `streams[]` | Every stream, ordered by `path` (ordinal), the partition streams by their number. |
| `streams[].path`, `kind`, `rawBytes` | The path with `/` between storage and stream, `partition`, `paged` or `plain`, and the stored length. |
| `streams[].framing` | Partition streams: `ok`, or the reason the framing does not hold: the name of the rule, or the message of the inflater for a damaged gzip member. Left out when the records of the file cannot be counted (`recordsFailure`). |
| `streams[].blocks`, `familyBlocks`, `segments`, `inflatedBytes`, `records` | Partition streams whose framing is `ok`: content blocks, those of them that are family documents, non-empty segments, the bytes all segments inflate to, and records. |
| `streams[].decoded`, `inflatedBytes` | Paged streams: whether the stream decoded, and the length of what it inflates to. |
| `records` | The count of the records; left out when `recordsFailure` is given. |
| `records.totals` | The sums over all groups, with the properties of a group. |
| `records.groups[]` | One row per block kind and channel, project before family, then by channel. |
| `records.classes[]` | One row per block kind, channel and class, every class, in the order of the groups; within a group the class with the most records that are not exact first, then by class name (ordinal). |
| `block`, `channel` | `project` or `family`, and 101, 102 or 103. |
| `class` | The name the schema gives the class tag of the record; `(class N)` for a tag the schema does not declare; `(no class tag)` for a body of fewer than 2 bytes. |
| `items`, `exact` | Records, and those of them that are exact. |
| `payloadBytes`, `exactBytes` | The sum of the body lengths of the records, and of the exact ones. |
| `checksumMismatches` | Records of channel 102 or 103 whose stored checksum differs from the Adler-32 of the body. Always 0 on channel 101, which stores none. |
| `terminators` | Groups and totals: the chain terminators (owner id all ones, no body), one per block and channel. They are not records and are in no other count. |
| `classes[].stops[]` | The reasons of the records that are not exact, the most frequent first (then by reason, ordinal), at most five: `reason`, `count`, and `owners`, the lowest owner ids with that reason (at most three). |
| `classes[].otherStops` | The number of records that are not exact for a reason beyond the five listed. |
| `recordsFailure` | Why the records cannot be counted at all: the class table has no framing classes, the width of the element id is not known, or the file has no `Partitions` storage or no partition stream. The streams are still listed. |

The counts add up: the class rows of a group to the group, the groups to the totals, `stops[].count` and `otherStops` of a class to
`items - exact`, and `records` of the framed partition streams to `totals.items`.

Standard output prints one line per group and, under `Not exact`, the classes with the most records that are not exact over all groups.
A percentage is rounded down, so `100.00%` is printed only when nothing is missing.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The file was measured. Also when a partition stream does not frame or the records cannot be counted: the report says so. |
| 1 | The command line is wrong, or the report cannot be written. |
| 2 | The file cannot be read: it is missing, empty, not a compound file, cut or damaged, its class table cannot be read, or a stream is longer than one array. One line on standard error. |

## What it does not measure

- The content of the `Global`, `Formats` and plain streams. A paged stream is only inflated; what it holds (the element table, the
  history, the class table itself) is not walked.
- The meaning of fields. An exact record is fully assigned to the fields of its class; whether a reader then uses those fields is
  another question, as is whether an element is exported. The export report (`--report` of an export, see
  [the export report](export-report.md)) answers that one.
- Elements. The command counts records: an element that several partition streams hold (a file saved in several increments) is
  counted once per copy, and the same element id occurs in the documents of several families.
- Records of a partition stream whose framing does not hold. They are missing from every count; compare `records` of the stream rows
  with the streams listed to see how much that is.
- A file without a readable class table. The walk needs the schema of the file, so such a file is reported as `unreadable`.
