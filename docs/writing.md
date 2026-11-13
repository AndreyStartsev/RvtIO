# Writing and save-as

`RvtWriter` and `RvtDocument.SaveAs` provide lossless container round-trips and targeted paged-stream and partition-record
rewrites for local Revit projects (`.rvt`) and families (`.rfa`), without requiring Revit or the Revit API.

```csharp
using RvtIO;

RvtWriteCapabilities capabilities = RvtWriter.InspectCapabilities("project.rvt");
if (capabilities.Supports(RvtWriteOperation.SaveAs))
{
    RvtWriteResult saved = RvtWriter.SaveAs("project.rvt", "project.copy.rvt");
}
```

From the command line:

```
rvtio save-as project.rvt --check
rvtio save-as project.rvt project.copy.rvt --report save-as.report.json
```

## Write-capability matrix

Every write call inspects the source file first and checks the requested operation against the capability matrix:

| Dimension | Supported | Refused |
| --- | --- | --- |
| File kind | Local project (`.rvt`, `.rte`) and family (`.rfa`, `.rft`) containers (`RvtFileKind.Project`, `RvtFileKind.Family`) | Containers whose kind cannot be identified (`unknown-file-kind`) |
| Sharing and hosting | Non-workshared local files (`Worksharing: Not enabled`, `IsSingleUserCloudModel: False`) | Workshared central or local copies (`workshared-model`), cloud models (`cloud-model`), or missing/unrecognised `BasicFileInfo` sharing/hosting/release fields (`missing-metadata`) |
| Release and ID width | Releases `2020` to `2023` (4-byte element IDs) and `2024` to `2027` (8-byte element IDs) when `BasicFileInfo` and `Formats/Latest` agree | Releases outside `2020` to `2027` (`unsupported-release`), unrecognised schemas (`unrecognised-release`), or saved/schema mismatches (`release-mismatch`) |
| `SaveAs` | All streams, storages, storage directory-entry metadata (`CLSID`, `StateBits`, timestamps), external references, preview images, and unknown payloads copied with sector size (`512` or `4,096` bytes) preserved | Truncated containers (`RvtInputException`), corrupt page parity (`corrupt-page-parity`), or cross-directory relocation that changes relative external reference targets (`relative-reference-relocation`, `unreadable-transmission-data`) |
| `PagedStreamRewrite` | Replacing the uncompressed payload of `Formats/Latest` or `Global/<Name>` while preserving stream prefixes, gzip framing, page parity, and container compatibility | Unreadable `Global/*` streams (`unreadable-paged-stream`) or malformed/incompatible replacement `Formats/Latest` schemas (`invalid-schema-payload`, `release-mismatch`, `incompatible-schema-payload`) |
| `PartitionRecordRewrite` | Replacing targeted record bodies inside `Partitions/<n>` (`101`, `102`, `103`) while preserving untouched blocks, loaded families, channels, and unknown class payloads | Missing framing marker classes (`missing-framing-markers`), missing partition streams (`missing-partitions`), framing errors (`partition-framing-failed`), or mismatched input Adler-32 checksums (`partition-checksum-failed`) |

## Framing, checksums, and preservation invariants

Cross-stream tables in a Revit container (`Global/ElemTable`, `Global/DocumentIncrementTable`, `Global/PartitionTable`) store
element identifiers, episode numbers, and partition identifiers, never byte offsets or compressed stream lengths. Changing the
byte length of a record inside `Partitions/<n>` therefore requires no cross-stream offset table edits.

When rewriting data, `RvtWriter` enforces the following invariants:

1. **Untouched streams and storage metadata stay identical.** `SaveAs` decodes nothing; every stream (`BasicFileInfo`,
   `TransmissionData`, `ProjectInformation`, `PartAtom`, `RevitPreview4.0`, `Formats/*`, `Global/*`, `Partitions/*`, and any
   unrecognised streams) is copied verbatim, and root/child storage directory-entry metadata (`HeaderCLSID`, `CLSID`, `StateBits`,
   and creation/modified FILETIMEs) is preserved and verified on read-back.
2. **Paged stream prefixes and parity are preserved.** `Formats/Latest` starts directly with its gzip member; `Global/*` streams
   keep their exact 8-byte header prefix before the gzip member. Every rewritten paged stream is sealed with cyclic Hamming
   parity pages (`64,896` payload bytes + `353` parity bytes per full page) and read back through both the parity verifier and
   gzip decoder before being written.
3. **Untouched blocks, chains, and opaque payloads stay byte-for-byte identical.** Inside an edited `Partitions/<n>` stream:
   - Untouched content blocks (including every keyed block belonging to a loaded family document) are copied verbatim with their
     original deflate payloads and `SignatureMarker` intact.
   - Within an edited block, untouched segment chains (including untouched chains on the same channel) are copied verbatim.
   - Within an edited chain, untouched records (including records whose class tag has no known field layout or carries opaque
     trailing bytes) are copied verbatim in file order.
   - Edited records receive updated `[size][body][size]` framing and fresh Adler-32 checksums on channels `102` and `103`
     (channel `101` carries no record checksum).
   - Edited chains respect Revit's `131,072`-byte (`128 KB`) segment body bound, emitting standalone segments (`flags & 3 == 0`)
     for batches up to `131,072` bytes and multi-segment chains (`2 -> 3 -> 1`) for larger record bodies while preserving the
     source chain's upper flag bits and opaque dword.
   - Edited blocks write an empty `SignatureMarker` (`n = 0, m = 0`) because their bytes are no longer signed by Revit.
4. **Post-encode verification.** Every rewritten partition stream is verified in memory before container assembly: page parity
   must pass, full `PartitionFraming` must walk every block and segment without error, Adler-32 checksums must match on every
   record, every unedited record must match the input byte-for-byte, and every edited record must match its requested body.
5. **Atomic persistence and source immutability.** Output is written to a sibling `.tmp` file, re-inspected against the capability
   matrix when edits were applied, read back from disk to verify every storage's directory-entry metadata and every stream's
   declared length and SHA-256 hash, and moved to the destination with `overwrite: false`. Existing files (including the source
   file itself) are never overwritten, and any failure deletes the temporary file while leaving the source untouched.

## Refusal codes

`RvtWriteRefusedException.Kind` and `RvtOperationSupport.RefusalCode` report stable machine-readable strings:

| Code | Meaning |
| --- | --- |
| `unknown-file-kind` | Container has neither `PartAtom` nor `ProjectInformation` and its path extension is not `.rvt`/`.rte`/`.rfa`/`.rft`. |
| `cloud-model` | `BasicFileInfo` marks a single-user or workshared cloud model (`IsSingleUserCloudModel: True` or cloud central path). |
| `workshared-model` | `BasicFileInfo` reports worksharing enabled. |
| `missing-metadata` | `BasicFileInfo` is missing, unreadable, or lacks a recognised `Worksharing`, `IsSingleUserCloudModel`, or saving-release field, or `TransmissionData` is missing when relocating a file with `RvtLinkSymbol` records. |
| `relative-reference-relocation` | Saving to a different directory would change the resolved target of a relative external file reference (or an unverified `PointCloudType` path). |
| `unreadable-transmission-data` | `TransmissionData` cannot be parsed cleanly when saving to a different directory. |
| `corrupt-page-parity` | A `Formats/*`, `Global/*`, or `Partitions/*` stream fails Hamming page parity verification. |
| `missing-schema` | `Formats/Latest` is missing, unreadable, or cannot be parsed. |
| `unrecognised-release` | `Formats/Latest` does not match a known Revit schema release. |
| `release-mismatch` | `BasicFileInfo` saving release differs from the `Formats/Latest` schema release (on source inspection or after replacing `Formats/Latest`). |
| `unsupported-release` | Effective release is outside `2020` to `2027`. |
| `unreadable-paged-stream` | A `Global/*` stream fails decompression or prefix verification. |
| `missing-framing-markers` | `Formats/Latest` lacks one or more partition framing marker classes. |
| `missing-partitions` | No numbered stream exists under `Partitions/`. |
| `partition-framing-failed` | A `Partitions/<n>` stream fails strict block, segment, gzip, or owner-census framing. |
| `partition-checksum-failed` | An input record on channel `102` or `103` has a mismatched Adler-32 checksum. |
| `invalid-stream-target` | A `RvtPagedStreamEdit` names a missing or non-paged stream. |
| `invalid-schema-payload` | A replacement `Formats/Latest` payload does not parse as a valid class schema. |
| `incompatible-schema-payload` | A replacement `Formats/Latest` payload changes element ID width or framing marker class identifiers required by existing partition streams. |
| `duplicate-edit` | The same stream or `(PartitionNumber, Block, Channel, OwnerId)` record is targeted twice in one request. |
| `missing-partition` | A `RvtPartitionRecordEdit` names a `Partitions/<n>` stream that does not exist in the source container. |
| `missing-record` | A `RvtPartitionRecordEdit` names a `(Block, Channel, OwnerId)` record that does not exist in the target partition. |
| `invalid-record-target` | A `RvtPartitionRecordEdit` uses an invalid partition number, block index, channel, `-1` terminator owner, 32-bit overflow owner, or body shorter than 2 bytes. |
