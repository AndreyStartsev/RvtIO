using System.Globalization;
using ICSharpCode.SharpZipLib.Checksum;
using OpenMcdf;
using RvtIO.Parsing;
using RvtIO.Writing;

namespace RvtIO;

/// <summary>Kind of Revit compound file container.</summary>
public enum RvtFileKind
{
    /// <summary>Neither family nor project markers were found and the file extension is not <c>.rvt</c> or <c>.rfa</c>.</summary>
    Unknown = 0,
    /// <summary>Revit project or project template (<c>.rvt</c>, <c>.rte</c>).</summary>
    Project = 1,
    /// <summary>Revit family or family template (<c>.rfa</c>, <c>.rft</c>).</summary>
    Family = 2,
}

/// <summary>Write operations classified by the <see cref="RvtWriter"/> capability matrix.</summary>
public enum RvtWriteOperation
{
    /// <summary>Lossless container round-trip preserving the directory tree, sector size, and every stream's stored bytes.</summary>
    SaveAs = 0,
    /// <summary>Rewriting the uncompressed payload of <c>Formats/Latest</c> or a <c>Global/*</c> single-member paged stream.</summary>
    PagedStreamRewrite = 1,
    /// <summary>Rewriting targeted records inside <c>Partitions/&lt;n&gt;</c> while preserving untouched blocks, channels, and payloads.</summary>
    PartitionRecordRewrite = 2,
}

/// <summary>Support status of one <see cref="RvtWriteOperation"/> on an inspected file.</summary>
/// <param name="Operation">The evaluated write operation.</param>
/// <param name="IsSupported">True when the operation is supported for this file.</param>
/// <param name="RefusalCode">Stable refusal code when <paramref name="IsSupported"/> is false; otherwise null.</param>
/// <param name="Reason">Human-readable refusal reason when <paramref name="IsSupported"/> is false; otherwise null.</param>
public sealed record RvtOperationSupport(
    RvtWriteOperation Operation,
    bool IsSupported,
    string? RefusalCode = null,
    string? Reason = null);

/// <summary>Write-capability inspection result for a Revit container across file kind, release, and operation.</summary>
public sealed class RvtWriteCapabilities
{
    /// <summary>Detected container kind (<see cref="RvtFileKind.Project"/> or <see cref="RvtFileKind.Family"/>).</summary>
    public required RvtFileKind FileKind { get; init; }

    /// <summary>Release year recorded in <c>BasicFileInfo</c>, or null when absent.</summary>
    public required int? SavedRelease { get; init; }

    /// <summary>Release year identified from the embedded <c>Formats/Latest</c> class schema, or null when unrecognised.</summary>
    public required int? SchemaRelease { get; init; }

    /// <summary>Effective layout release year (<c>2020</c> to <c>2027</c> when supported), or null when unknown.</summary>
    public required int? EffectiveRelease { get; init; }

    /// <summary>Byte width of stored element identifiers (<c>4</c> up to Revit 2023, <c>8</c> from Revit 2024), or null when unknown.</summary>
    public required int? ElementIdWidth { get; init; }

    /// <summary>True when <c>BasicFileInfo</c> reports worksharing enabled.</summary>
    public required bool IsWorkshared { get; init; }

    /// <summary>True when <c>BasicFileInfo</c> marks a cloud model or cloud central path.</summary>
    public required bool IsCloudModel { get; init; }

    /// <summary>Total number of streams in the compound file container.</summary>
    public required int StreamCount { get; init; }

    /// <summary>Number of numbered partition streams under <c>Partitions/</c>.</summary>
    public required int PartitionStreamCount { get; init; }

    /// <summary>Per-operation support decisions keyed by <see cref="RvtWriteOperation"/>.</summary>
    public required IReadOnlyDictionary<RvtWriteOperation, RvtOperationSupport> Operations { get; init; }

    /// <summary>Returns true when <paramref name="operation"/> is supported on this file.</summary>
    public bool Supports(RvtWriteOperation operation) =>
        Operations.TryGetValue(operation, out var support) && support.IsSupported;
}

/// <summary>Thrown when a write or save-as operation is refused because the source file state or edit request is unsupported.</summary>
public sealed class RvtWriteRefusedException : Exception
{
    /// <summary>Stable machine-readable refusal code.</summary>
    public string Kind { get; }

    /// <summary>Creates the exception with its stable <paramref name="kind"/> code and diagnostic message.</summary>
    public RvtWriteRefusedException(string kind, string message, Exception? inner = null) : base(message, inner) =>
        Kind = kind;
}

/// <summary>Replacement of the uncompressed payload of a single-member paged stream (<c>Formats/Latest</c> or <c>Global/&lt;Name&gt;</c>).</summary>
/// <param name="StreamPath">Container path of the target paged stream, using <c>/</c> between storage and stream name.</param>
/// <param name="UncompressedPayload">Replacement uncompressed bytes to compress and page-encode.</param>
public sealed record RvtPagedStreamEdit(string StreamPath, ReadOnlyMemory<byte> UncompressedPayload);

/// <summary>Replacement of one framed record body inside <c>Partitions/&lt;PartitionNumber&gt;</c>.</summary>
/// <param name="PartitionNumber">Non-negative number of the target <c>Partitions/&lt;n&gt;</c> stream.</param>
/// <param name="Channel">Target channel (<c>101</c>, <c>102</c>, or <c>103</c>).</param>
/// <param name="OwnerId">Owner element identifier of the record inside the block.</param>
/// <param name="Body">Replacement record body starting with its 2-byte class tag.</param>
/// <param name="Block">Content block index (<c>0</c> = project/family own elements; <c>&gt; 0</c> = loaded-family keyed block).</param>
public sealed record RvtPartitionRecordEdit(
    int PartitionNumber,
    int Channel,
    ulong OwnerId,
    ReadOnlyMemory<byte> Body,
    int Block = 0);

/// <summary>Set of stream and partition record edits to persist atomically into a new Revit container.</summary>
public sealed class RvtWriteRequest
{
    /// <summary>Edits targeting single-member paged streams (<c>Formats/Latest</c> or <c>Global/*</c>).</summary>
    public IReadOnlyList<RvtPagedStreamEdit> PagedStreamEdits { get; init; } = [];

    /// <summary>Edits targeting framed records inside <c>Partitions/&lt;n&gt;</c> streams.</summary>
    public IReadOnlyList<RvtPartitionRecordEdit> PartitionRecordEdits { get; init; } = [];
}

/// <summary>Summary of a completed <see cref="RvtWriter.SaveAs"/> or <see cref="RvtWriter.Rewrite"/> operation.</summary>
public sealed class RvtWriteResult
{
    /// <summary>Full path of the written output file.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Container kind of the written file.</summary>
    public required RvtFileKind FileKind { get; init; }

    /// <summary>Effective Revit release year of the written file.</summary>
    public required int EffectiveRelease { get; init; }

    /// <summary>Total number of streams verified in the written container.</summary>
    public required int StreamCount { get; init; }

    /// <summary>Number of container streams replaced during the write (<c>0</c> for an unchanged <see cref="RvtWriter.SaveAs"/>).</summary>
    public required int ReplacedStreamCount { get; init; }

    /// <summary>Number of partition records rewritten across all <c>Partitions/&lt;n&gt;</c> streams.</summary>
    public required int RewrittenRecordCount { get; init; }
}

/// <summary>
/// Lossless save-as and targeted stream/record writer for local, non-workshared Revit projects (<c>.rvt</c>) and families (<c>.rfa</c>)
/// from releases 2020 through 2027.
///
/// Every write validates the source file against the capability matrix first, builds and self-verifies any rewritten paged or partition
/// streams in memory, writes to a temporary file next to the destination, reads the temporary file back to verify every stream's length
/// and SHA-256, and moves it into place without ever overwriting an existing file or mutating the source file.
/// </summary>
public static class RvtWriter
{
    /// <summary>Oldest Revit release supported by the writer capability matrix.</summary>
    public const int OldestSupportedRelease = 2020;

    /// <summary>Newest Revit release supported by the writer capability matrix.</summary>
    public const int NewestSupportedRelease = 2027;

    private static readonly IReadOnlyDictionary<string, byte[]> NoStreamReplacements = new Dictionary<string, byte[]>();

    /// <summary>Inspects <paramref name="sourcePath"/> and returns its write-capability matrix across <see cref="RvtWriteOperation"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> is null.</exception>
    /// <exception cref="RvtInputException">The file is missing, empty, not a compound file, or truncated.</exception>
    /// <exception cref="RvtLimitException">A stream exceeds the maximum single-array byte length.</exception>
    public static RvtWriteCapabilities InspectCapabilities(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        RvtContainerCheck.Verify(sourcePath);
        using var root = RootStorage.OpenRead(sourcePath);
        return InspectRoot(root, sourcePath).Capabilities;
    }

    /// <summary>Writes a verified lossless copy of <paramref name="sourcePath"/> to <paramref name="outputPath"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> or <paramref name="outputPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is empty or whitespace.</exception>
    /// <exception cref="IOException"><paramref name="outputPath"/> already exists, its directory is missing, or the source is locked for writing.</exception>
    /// <exception cref="RvtInputException">The source file is missing, empty, not a compound file, or truncated.</exception>
    /// <exception cref="RvtLimitException">A stream exceeds the maximum single-array byte length.</exception>
    /// <exception cref="RvtWriteRefusedException">The source file is outside the supported write-capability matrix.</exception>
    public static RvtWriteResult SaveAs(string sourcePath, string outputPath) =>
        Rewrite(sourcePath, outputPath, new RvtWriteRequest());

    /// <summary>Writes <paramref name="outputPath"/> from <paramref name="sourcePath"/> with the requested paged-stream and partition-record
    /// edits applied, preserving every other stream, block, channel, and record byte-for-byte.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/>, <paramref name="outputPath"/>, or <paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputPath"/> is empty or whitespace.</exception>
    /// <exception cref="IOException"><paramref name="outputPath"/> already exists, its directory is missing, or the source is locked for writing.</exception>
    /// <exception cref="RvtInputException">The source file is missing, empty, not a compound file, or truncated.</exception>
    /// <exception cref="RvtLimitException">A stream exceeds the maximum single-array byte length.</exception>
    /// <exception cref="RvtWriteRefusedException">The source file state or edit request is outside the supported scope.</exception>
    public static RvtWriteResult Rewrite(string sourcePath, string outputPath, RvtWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(request);
        if (request.PagedStreamEdits is null)
            throw new ArgumentException("PagedStreamEdits cannot be null.", nameof(request));
        if (request.PartitionRecordEdits is null)
            throw new ArgumentException("PartitionRecordEdits cannot be null.", nameof(request));

        string fullOutput = ValidateOutputPath(outputPath);
        RvtContainerCheck.Verify(sourcePath);

        Dictionary<string, byte[]>? replacements = null;
        RvtWriteCapabilities capabilities;
        using (var sourceFile = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var root = RootStorage.Open(sourceFile, StorageModeFlags.LeaveOpen))
        {
            var inspection = InspectRoot(root, sourcePath);
            capabilities = inspection.Capabilities;

            RequireSupported(capabilities, RvtWriteOperation.SaveAs);
            if (request.PagedStreamEdits.Count > 0)
                RequireSupported(capabilities, RvtWriteOperation.PagedStreamRewrite);
            if (request.PartitionRecordEdits.Count > 0)
                RequireSupported(capabilities, RvtWriteOperation.PartitionRecordRewrite);

            if (request.PagedStreamEdits.Count > 0 || request.PartitionRecordEdits.Count > 0)
                replacements = BuildReplacements(root, inspection, request);
        }

        var containerResult = ContainerRewriter.Rewrite(
            sourcePath,
            fullOutput,
            replacements ?? NoStreamReplacements);

        return new RvtWriteResult
        {
            OutputPath = fullOutput,
            FileKind = capabilities.FileKind,
            EffectiveRelease = capabilities.EffectiveRelease!.Value,
            StreamCount = containerResult.Entries.Values.Count(e => e.IsStream),
            ReplacedStreamCount = containerResult.Replaced,
            RewrittenRecordCount = request.PartitionRecordEdits.Count,
        };
    }

    private sealed record Inspection(
        RvtWriteCapabilities Capabilities,
        PartitionFraming.Tags? FramingTags,
        IReadOnlyDictionary<string, string> CanonicalStreamPaths);

    private static string ValidateOutputPath(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output file path cannot be empty.", nameof(outputPath));

        string fullOutput;
        try { fullOutput = Path.GetFullPath(outputPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new IOException($"'{outputPath}' is not a usable output file path.", ex); }

        string? directory = Path.GetDirectoryName(fullOutput);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The output directory for '{fullOutput}' does not exist.");

        if (File.Exists(fullOutput) || Directory.Exists(fullOutput))
            throw new IOException($"'{Path.GetFileName(fullOutput)}' already exists; the output is never replaced.");

        return fullOutput;
    }

    private static void RequireSupported(RvtWriteCapabilities capabilities, RvtWriteOperation operation)
    {
        var support = capabilities.Operations[operation];
        if (!support.IsSupported)
            throw new RvtWriteRefusedException(support.RefusalCode!, support.Reason!);
    }

    private static Inspection InspectRoot(RootStorage root, string sourcePath)
    {
        var bfi = BasicFileInfoReader.Read(root);
        int? savedRelease = SchemaRelease.Saved(bfi.GetValueOrDefault("RevitVersion"));
        bool isWorkshared = bfi.TryGetValue("Worksharing", out string? ws)
            && !string.IsNullOrWhiteSpace(ws)
            && !string.Equals(ws.Trim(), "Not enabled", StringComparison.OrdinalIgnoreCase);
        bool isCloudModel = string.Equals(bfi.GetValueOrDefault("IsSingleUserCloudModel"), "True", StringComparison.OrdinalIgnoreCase)
            || IsCloudCentralPath(bfi.GetValueOrDefault("Central Model Path"));

        var streamEntries = new List<(string Path, Storage Parent, string Name, long Length)>();
        CollectStreams(root, "", streamEntries);
        var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in streamEntries) canonicalPaths.TryAdd(entry.Path, entry.Path);

        RvtFileKind fileKind = DetectFileKind(canonicalPaths, bfi.GetValueOrDefault("LastSavePath"), sourcePath);

        var partitionEntries = streamEntries
            .Where(e => PartitionNumber(e.Path) is not null)
            .OrderBy(e => PartitionNumber(e.Path)!.Value)
            .ToList();

        (string Code, string Reason)? parityFailure = null;
        (string Code, string Reason)? globalDecodeFailure = null;
        foreach (var entry in streamEntries)
        {
            bool isFormats = entry.Path.StartsWith("Formats/", StringComparison.OrdinalIgnoreCase);
            bool isGlobal = entry.Path.StartsWith("Global/", StringComparison.OrdinalIgnoreCase);
            bool isPartition = PartitionNumber(entry.Path) is not null;
            if (!isFormats && !isGlobal && !isPartition) continue;

            byte[] raw = ReadStreamBytes(entry.Parent, entry.Name, entry.Length);
            var check = PageCodec.Verify(raw);
            if (check.Status is PageCheckStatus.BadPage or PageCheckStatus.BadTail)
            {
                parityFailure ??= ("corrupt-page-parity", $"Stream '{entry.Path}' fails page parity at page {check.Page} ({check.Status}).");
                continue;
            }

            if (isGlobal && globalDecodeFailure is null)
            {
                try { _ = PagedStreamCodec.ReadPrefix(raw); }
                catch (InvalidDataException ex)
                {
                    globalDecodeFailure = ("unreadable-paged-stream", $"Paged stream '{entry.Path}' cannot be decoded cleanly: {ex.Message}");
                }
            }
        }

        SchemaDocument? schemaDoc = TfSchemaReader.Read(root);
        FormatsSchema? formatsSchema = schemaDoc is null ? null : new FormatsSchema(schemaDoc);
        int? schemaRelease = formatsSchema is null ? null : SchemaRelease.FromSchema(formatsSchema);
        int? effectiveRelease = SchemaRelease.Effective(savedRelease, schemaRelease);
        int? idWidth = SchemaRelease.IdWidth(effectiveRelease);
        PartitionFraming.Tags? framingTags = formatsSchema is null ? null : PartitionFraming.TagsOf(formatsSchema);

        (string Code, string Reason)? baseFailure = EvaluateBaseEligibility(
            fileKind,
            isCloudModel,
            isWorkshared,
            parityFailure,
            schemaDoc,
            savedRelease,
            schemaRelease);

        RvtOperationSupport saveAsSupport;
        RvtOperationSupport pagedSupport;
        RvtOperationSupport partitionSupport;

        if (baseFailure is { } refusal)
        {
            saveAsSupport = new RvtOperationSupport(RvtWriteOperation.SaveAs, false, refusal.Code, refusal.Reason);
            pagedSupport = new RvtOperationSupport(RvtWriteOperation.PagedStreamRewrite, false, refusal.Code, refusal.Reason);
            partitionSupport = new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, refusal.Code, refusal.Reason);
        }
        else
        {
            saveAsSupport = new RvtOperationSupport(RvtWriteOperation.SaveAs, true);
            pagedSupport = globalDecodeFailure is { } gFail
                ? new RvtOperationSupport(RvtWriteOperation.PagedStreamRewrite, false, gFail.Code, gFail.Reason)
                : new RvtOperationSupport(RvtWriteOperation.PagedStreamRewrite, true);
            partitionSupport = EvaluatePartitionEligibility(partitionEntries, framingTags, idWidth);
        }

        var operations = new Dictionary<RvtWriteOperation, RvtOperationSupport>
        {
            [RvtWriteOperation.SaveAs] = saveAsSupport,
            [RvtWriteOperation.PagedStreamRewrite] = pagedSupport,
            [RvtWriteOperation.PartitionRecordRewrite] = partitionSupport,
        };

        var capabilities = new RvtWriteCapabilities
        {
            FileKind = fileKind,
            SavedRelease = savedRelease,
            SchemaRelease = schemaRelease,
            EffectiveRelease = effectiveRelease,
            ElementIdWidth = idWidth,
            IsWorkshared = isWorkshared,
            IsCloudModel = isCloudModel,
            StreamCount = streamEntries.Count,
            PartitionStreamCount = partitionEntries.Count,
            Operations = operations,
        };

        return new Inspection(capabilities, framingTags, canonicalPaths);
    }

    private static (string Code, string Reason)? EvaluateBaseEligibility(
        RvtFileKind fileKind,
        bool isCloudModel,
        bool isWorkshared,
        (string Code, string Reason)? parityFailure,
        SchemaDocument? schemaDoc,
        int? savedRelease,
        int? schemaRelease)
    {
        if (fileKind == RvtFileKind.Unknown)
            return ("unknown-file-kind", "The file kind (.rvt project or .rfa family) could not be determined from container streams or file extension.");
        if (isCloudModel)
            return ("cloud-model", "Cloud-hosted Revit models are outside the supported local-file write scope.");
        if (isWorkshared)
            return ("workshared-model", "Workshared Revit models are outside the supported non-workshared local-file write scope.");
        if (parityFailure is not null)
            return parityFailure;
        if (schemaDoc is null)
            return ("missing-schema", "The embedded class schema (Formats/Latest) is missing or unreadable.");
        if (schemaRelease is null)
            return ("unrecognised-release", "The Revit release of Formats/Latest is not recognised.");
        if (savedRelease is int saved && saved != schemaRelease.Value)
            return ("release-mismatch", $"The file was saved by Revit {saved} while Formats/Latest carries the schema of Revit {schemaRelease.Value}.");
        if (schemaRelease.Value is < OldestSupportedRelease or > NewestSupportedRelease)
            return ("unsupported-release", $"Revit release {schemaRelease.Value} is outside the supported write range ({OldestSupportedRelease}-{NewestSupportedRelease}).");
        return null;
    }

    private static RvtOperationSupport EvaluatePartitionEligibility(
        List<(string Path, Storage Parent, string Name, long Length)> partitionEntries,
        PartitionFraming.Tags? framingTags,
        int? idWidth)
    {
        if (framingTags is null)
            return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, "missing-framing-markers", "Formats/Latest lacks the partition framing marker classes.");
        if (idWidth is not (4 or 8))
            return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, "unknown-id-width", "The element identifier byte width is unknown.");
        if (partitionEntries.Count == 0)
            return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, false, "missing-partitions", "The file contains no numbered partition streams under Partitions/.");

        var adler = new Adler32();
        ulong terminator = idWidth.Value == 8 ? ulong.MaxValue : uint.MaxValue;
        foreach (var entry in partitionEntries)
        {
            byte[] raw = ReadStreamBytes(entry.Parent, entry.Name, entry.Length);
            bool checksumMismatch = false;
            ulong badOwner = 0;
            int badChannel = 0;
            bool ok = PartitionFraming.Walk(raw, framingTags.Value, idWidth.Value, record =>
            {
                if (checksumMismatch) return;
                if (record.Owner == terminator && record.Body.Length == 0) return;
                if (record.Channel == 101) return;
                adler.Reset();
                adler.Update(record.Body.ToArray());
                if ((uint)adler.Value != record.Extra)
                {
                    checksumMismatch = true;
                    badOwner = record.Owner;
                    badChannel = record.Channel;
                }
            }, out string? reason, out _);

            if (!ok)
                return new RvtOperationSupport(
                    RvtWriteOperation.PartitionRecordRewrite,
                    false,
                    "partition-framing-failed",
                    $"Partition stream '{entry.Path}' fails framing verification ({reason}).");

            if (checksumMismatch)
                return new RvtOperationSupport(
                    RvtWriteOperation.PartitionRecordRewrite,
                    false,
                    "partition-checksum-failed",
                    $"Partition stream '{entry.Path}' has a mismatched Adler-32 checksum on channel {badChannel}, owner {badOwner}.");
        }

        return new RvtOperationSupport(RvtWriteOperation.PartitionRecordRewrite, true);
    }

    private static Dictionary<string, byte[]> BuildReplacements(
        RootStorage root,
        Inspection inspection,
        RvtWriteRequest request)
    {
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var pagedEdit in request.PagedStreamEdits)
        {
            if (pagedEdit is null)
                throw new RvtWriteRefusedException("invalid-stream-target", "A paged stream edit entry is null.");
            string trimmed = pagedEdit.StreamPath?.Trim() ?? "";
            if (!IsSupportedPagedStreamTarget(trimmed))
                throw new RvtWriteRefusedException(
                    "invalid-stream-target",
                    $"'{pagedEdit.StreamPath}' is not a supported single-member paged stream; only Formats/Latest and Global/<Name> are allowed.");
            if (!inspection.CanonicalStreamPaths.TryGetValue(trimmed, out string? canonical))
                throw new RvtWriteRefusedException("invalid-stream-target", $"Stream '{trimmed}' does not exist in the source container.");
            if (replacements.ContainsKey(canonical))
                throw new RvtWriteRefusedException("duplicate-edit", $"Stream '{canonical}' is targeted more than once.");

            if (string.Equals(canonical, "Formats/Latest", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = TfSchemaReader.TryParse(pagedEdit.UncompressedPayload.ToArray());
                if (parsed is null)
                    throw new RvtWriteRefusedException("invalid-schema-payload", "Replacement payload for Formats/Latest does not parse as a valid class schema.");
            }

            byte[] originalStored = ReadStreamByPath(root, canonical);
            replacements[canonical] = PagedStreamCodec.Reencode(originalStored, pagedEdit.UncompressedPayload.Span);
        }

        if (request.PartitionRecordEdits.Count > 0)
        {
            foreach (var recEdit in request.PartitionRecordEdits)
            {
                if (recEdit is null)
                    throw new RvtWriteRefusedException("invalid-record-target", "A partition record edit entry is null.");
                if (recEdit.PartitionNumber < 0)
                    throw new RvtWriteRefusedException("invalid-record-target", $"Partition number {recEdit.PartitionNumber} cannot be negative.");
            }

            foreach (var group in request.PartitionRecordEdits.GroupBy(e => e.PartitionNumber))
            {
                string partKey = "Partitions/" + group.Key.ToString(CultureInfo.InvariantCulture);
                if (!inspection.CanonicalStreamPaths.TryGetValue(partKey, out string? canonical))
                    throw new RvtWriteRefusedException("missing-partition", $"Partition stream '{partKey}' does not exist in the source container.");
                if (replacements.ContainsKey(canonical))
                    throw new RvtWriteRefusedException("duplicate-edit", $"Stream '{canonical}' is targeted more than once.");

                byte[] originalStored = ReadStreamByPath(root, canonical);
                var internalEdits = group
                    .Select(e => new PartitionRecordEdit(e.Block, e.Channel, e.OwnerId, e.Body))
                    .ToList();

                try
                {
                    replacements[canonical] = PartitionStreamEditor.Apply(
                        originalStored,
                        inspection.FramingTags!.Value,
                        inspection.Capabilities.ElementIdWidth!.Value,
                        internalEdits);
                }
                catch (ArgumentException ex)
                {
                    string code = ClassifyRecordEditArgumentError(ex.Message);
                    throw new RvtWriteRefusedException(code, $"In '{canonical}': {ex.Message}", ex);
                }
            }
        }

        return replacements;
    }

    private static string ClassifyRecordEditArgumentError(string message)
    {
        if (message.Contains("edited twice", StringComparison.Ordinal)) return "duplicate-edit";
        if (message.Contains("does not exist", StringComparison.Ordinal)) return "missing-record";
        return "invalid-record-target";
    }

    private static bool IsSupportedPagedStreamTarget(string path)
    {
        if (string.Equals(path, "Formats/Latest", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith("Global/", StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> rest = path.AsSpan("Global/".Length);
        return rest.Length > 0 && !rest.Contains('/');
    }

    private static RvtFileKind DetectFileKind(
        Dictionary<string, string> canonicalPaths,
        string? lastSavePath,
        string sourcePath)
    {
        if (canonicalPaths.ContainsKey("PartAtom")) return RvtFileKind.Family;
        if (canonicalPaths.ContainsKey("ProjectInformation")) return RvtFileKind.Project;
        return KindFromPath(lastSavePath) is not RvtFileKind.Unknown and var fromMeta
            ? fromMeta
            : KindFromPath(sourcePath);
    }

    private static RvtFileKind KindFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RvtFileKind.Unknown;
        string ext = Path.GetExtension(path);
        if (ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rft", StringComparison.OrdinalIgnoreCase))
            return RvtFileKind.Family;
        if (ext.Equals(".rvt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rte", StringComparison.OrdinalIgnoreCase))
            return RvtFileKind.Project;
        return RvtFileKind.Unknown;
    }

    private static bool IsCloudCentralPath(string? centralPath) =>
        !string.IsNullOrWhiteSpace(centralPath)
        && (centralPath.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase)
            || centralPath.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase)
            || centralPath.StartsWith("CLD://", StringComparison.OrdinalIgnoreCase));

    private static void CollectStreams(
        Storage storage,
        string prefix,
        List<(string Path, Storage Parent, string Name, long Length)> entries)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            if (entry.Type == EntryType.Storage)
                CollectStreams(storage.OpenStorage(entry.Name), prefix + entry.Name + "/", entries);
            else if (entry.Type == EntryType.Stream)
                entries.Add((prefix + entry.Name, storage, entry.Name, entry.Length));
        }
    }

    private static int? PartitionNumber(string path) =>
        path.StartsWith("Partitions/", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(path.AsSpan("Partitions/".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;

    private static byte[] ReadStreamBytes(Storage parent, string name, long length)
    {
        var bytes = new byte[checked((int)length)];
        using var stream = parent.OpenStream(name);
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadStreamByPath(RootStorage root, string streamPath)
    {
        string[] parts = streamPath.Split('/');
        Storage current = root;
        for (int i = 0; i < parts.Length - 1; i++)
            current = current.OpenStorage(parts[i]);
        using var stream = current.OpenStream(parts[^1]);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
