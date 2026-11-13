using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using OpenMcdf;

namespace RvtIO.Writing;

/// <summary>Directory-entry metadata of a storage in a compound file (and header CLSID for the root storage).</summary>
internal readonly record struct StorageDirectoryMetadata(
    Guid Clsid,
    uint StateBits,
    ulong CreationFileTime,
    ulong ModifiedFileTime,
    Guid HeaderClsid = default);

/// <summary>One storage or stream of a container: its kind, for a stream its length and SHA-256 (upper-case hexadecimal), and for a
/// storage its directory-entry metadata.</summary>
internal readonly record struct ContainerEntry(
    bool IsStream,
    long Length,
    string Sha256,
    StorageDirectoryMetadata Storage = default);

/// <summary>What <see cref="ContainerRewriter.Rewrite"/> wrote: every storage and stream by path ('/' between the names, no leading
/// '/'), with the length and SHA-256 of the bytes that were written, how many streams came from the replacement map, and the root
/// storage directory-entry metadata.</summary>
internal sealed record ContainerRewriteResult(
    IReadOnlyDictionary<string, ContainerEntry> Entries,
    int Replaced,
    StorageDirectoryMetadata RootStorage = default);

/// <summary>
/// Copies a compound file (the container of a Revit file) into a new compound file, stream by stream, with some streams replaced by
/// given bytes. The stored bytes of a stream are copied as they are; nothing is decoded. The directory tree is kept (storages, stream
/// names), and so are the sector size (512 or 4,096 bytes) of the source and the directory-entry metadata of the root and child storages
/// (CLSID, state bits, and creation/modified FILETIMEs; plus the compound file header CLSID). Streams are copied through a buffer of
/// 1 MB, so the memory use does not depend on the size of the streams; only the replacement bytes and the directory and allocation
/// tables of the two files are held.
///
/// The output is written to a temporary file in the folder of the output and moved to the output path only after the written file was
/// read back: the same storages and streams must be there with the expected storage directory-entry metadata, and every stream must have
/// the length and SHA-256 of the bytes that were meant to be written (the bytes read from the source, or the replacement). The temporary
/// file is removed when anything fails, and an existing output is never replaced.
///
/// On Windows the source is opened for reading with writers denied for as long as it is read, so the copy is one state of the source
/// and not a mix of two. When another process already holds the source open for writing (an application that has the file open), the
/// open fails with an <see cref="IOException"/> and nothing is written; there is no fall back to a shared read.
/// On other systems the share mode is advisory and a writer that already has the file open keeps working: there the caller must make
/// sure that nothing writes to the source while it is copied, and the copy is not guarded against a concurrent change.
/// </summary>
internal static class ContainerRewriter
{
    private const int BufferLength = 1 << 20;

    // A damaged directory can loop back on itself or point several entries at the same children. These bound the work, as in RvtContainerCheck.
    private const int MaxDepth = 64;
    private const int MaxEntries = 200_000;

    /// <summary>Writes <paramref name="outputPath"/> as a copy of <paramref name="sourcePath"/> in which each stream named by a key of
    /// <paramref name="replacements"/> (its path, for example <c>Formats/Latest</c>; names compare without regard to case, as in the
    /// container) holds the given bytes.</summary>
    /// <param name="beforeCheck">Called with the path of the temporary file once it is written and before it is read back; tests change the file there.</param>
    /// <exception cref="ArgumentException">A key names no stream of the source (it may name a storage), or two keys name the same stream.</exception>
    /// <exception cref="IOException">The output exists (the source itself included), its folder is missing, the source is open for writing in
    /// another process (Windows only), or a file cannot be read or written.</exception>
    /// <exception cref="InvalidDataException">The source directory is damaged, or the written file differs from what was meant to be written.</exception>
    internal static ContainerRewriteResult Rewrite(string sourcePath, string outputPath, IReadOnlyDictionary<string, byte[]> replacements,
        Action<string>? beforeCheck = null)
    {
        var replacementByName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in replacements)
            if (!replacementByName.TryAdd(name, bytes))
                throw new ArgumentException($"The replacement map names the stream '{name}' twice (names compare without regard to case).", nameof(replacements));

        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException($"'{Path.GetFileName(output)}' already exists; the output is never replaced.");
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var result = WriteTemporary(sourcePath, temporary, replacementByName);
            beforeCheck?.Invoke(temporary);
            CheckWritten(temporary, result.Entries, result.RootStorage);
            File.Move(temporary, output, overwrite: false);
            return result;
        }
        catch (Exception exception)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The failure that is thrown stays the first one; a leftover file is named in it.
                if (File.Exists(temporary)) exception.Data["temporary-file"] = temporary;
            }
            throw;
        }
    }

    private static ContainerRewriteResult WriteTemporary(string sourcePath, string temporary, Dictionary<string, byte[]> replacements)
    {
        using var sourceFile = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var source = RootStorage.Open(sourceFile, StorageModeFlags.LeaveOpen);
        var version = SectorVersion(sourceFile);

        var entries = new Dictionary<string, ContainerEntry>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        StorageDirectoryMetadata rootMetadata;
        using (var temporaryFile = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using (var destination = RootStorage.Create(temporaryFile, version, StorageModeFlags.LeaveOpen))
            {
                Copy(source, destination, "", replacements, used, entries, new byte[BufferLength], 0);
                rootMetadata = CopyRootMetadata(source, destination);
            }
            temporaryFile.Flush(flushToDisk: true);
        }

        foreach (string name in replacements.Keys)
            if (!used.Contains(name))
                throw new ArgumentException($"The replacement map names '{name}', which is not a stream of the source.", nameof(replacements));
        return new ContainerRewriteResult(entries, used.Count, rootMetadata);
    }

    private static OpenMcdf.Version SectorVersion(Stream file)
    {
        Span<byte> header = stackalloc byte[28];
        file.Position = 0;
        file.ReadExactly(header);
        return BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) switch
        {
            3 => OpenMcdf.Version.V3,
            4 => OpenMcdf.Version.V4,
            var other => throw new InvalidDataException($"The compound file has major version {other}; only 3 and 4 exist."),
        };
    }

    private static void Copy(Storage from, Storage to, string prefix, Dictionary<string, byte[]> replacements, HashSet<string> used,
        Dictionary<string, ContainerEntry> entries, byte[] buffer, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"The directory nests storages deeper than {MaxDepth} levels at '{prefix}'.");
        foreach (var entry in from.EnumerateEntries())
        {
            if (entries.Count >= MaxEntries) throw new InvalidDataException($"The directory holds more than {MaxEntries} entries.");
            string path = prefix + entry.Name;
            if (entry.Type == EntryType.Storage)
            {
                var sourceChild = from.OpenStorage(entry.Name);
                var destinationChild = to.CreateStorage(entry.Name);
                var metadata = ReadStorageMetadata(sourceChild);
                Add(entries, path, new ContainerEntry(false, 0, "", metadata));
                Copy(sourceChild, destinationChild, path + "/", replacements, used, entries, buffer, depth + 1);
                ApplyStorageMetadata(destinationChild, metadata);
                continue;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (var output = to.CreateStream(entry.Name))
            {
                if (replacements.TryGetValue(path, out var bytes))
                {
                    used.Add(path);
                    output.Write(bytes);
                    hash.AppendData(bytes);
                    length = bytes.Length;
                }
                else
                {
                    using var input = from.OpenStream(entry.Name);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                        length += read;
                    }
                    if (length != entry.Length)
                        throw new InvalidDataException($"Stream '{path}' holds {length.ToString("N0", CultureInfo.InvariantCulture)} of its {entry.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes.");
                }
            }
            Add(entries, path, new ContainerEntry(true, length, Convert.ToHexString(hash.GetHashAndReset())));
        }
    }

    private static StorageDirectoryMetadata ReadStorageMetadata(Storage storage) =>
        new(storage.CLSID, storage.StateBits, storage.CreationFileTime, storage.ModifiedFileTime);

    private static void ApplyStorageMetadata(Storage storage, StorageDirectoryMetadata metadata)
    {
        storage.CLSID = metadata.Clsid;
        storage.StateBits = metadata.StateBits;
        storage.CreationFileTime = metadata.CreationFileTime;
        storage.ModifiedFileTime = metadata.ModifiedFileTime;
    }

    private static StorageDirectoryMetadata CopyRootMetadata(RootStorage from, RootStorage to)
    {
        to.HeaderCLSID = from.HeaderCLSID;
        to.CLSID = from.CLSID;
        to.StateBits = from.StateBits;
        to.ModifiedFileTime = from.ModifiedFileTime;
        return new StorageDirectoryMetadata(from.CLSID, from.StateBits, to.CreationFileTime, from.ModifiedFileTime, from.HeaderCLSID);
    }

    private static void Add(Dictionary<string, ContainerEntry> entries, string path, ContainerEntry entry)
    {
        if (!entries.TryAdd(path, entry)) throw new InvalidDataException($"The directory holds '{path}' twice.");
    }

    /// <summary>Reads the compound file at <paramref name="path"/> to its end and requires exactly the storages and streams of
    /// <paramref name="expected"/>, each storage with its directory-entry metadata and each stream with its length and SHA-256.</summary>
    /// <exception cref="InvalidDataException">The first difference, named by its path.</exception>
    internal static void CheckWritten(string path, IReadOnlyDictionary<string, ContainerEntry> expected,
        StorageDirectoryMetadata? expectedRoot = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var root = RootStorage.Open(file, StorageModeFlags.LeaveOpen);
        if (expectedRoot is { } rootWant)
            VerifyRootMetadata(root, rootWant);
        var found = new HashSet<string>(StringComparer.Ordinal);
        Verify(root, "", expected, found, new byte[BufferLength], 0);
        foreach (string name in expected.Keys)
            if (!found.Contains(name))
                throw new InvalidDataException($"The written container lacks '{name}'.");
    }

    private static void VerifyRootMetadata(RootStorage root, StorageDirectoryMetadata want)
    {
        if (root.HeaderCLSID != want.HeaderClsid)
            throw new InvalidDataException($"Root storage header was written with CLSID {want.HeaderClsid} and has {root.HeaderCLSID}.");
        VerifyStorageMetadata(root, "<root>", want);
    }

    private static void VerifyStorageMetadata(Storage storage, string path, StorageDirectoryMetadata want)
    {
        if (storage.CLSID != want.Clsid)
            throw new InvalidDataException($"Storage '{path}' was written with CLSID {want.Clsid} and has {storage.CLSID}.");
        if (storage.StateBits != want.StateBits)
            throw new InvalidDataException($"Storage '{path}' was written with state bits 0x{want.StateBits:X8} and has 0x{storage.StateBits:X8}.");
        if (storage.CreationFileTime != want.CreationFileTime)
            throw new InvalidDataException($"Storage '{path}' was written with creation FILETIME {want.CreationFileTime.ToString(CultureInfo.InvariantCulture)} and has {storage.CreationFileTime.ToString(CultureInfo.InvariantCulture)}.");
        if (storage.ModifiedFileTime != want.ModifiedFileTime)
            throw new InvalidDataException($"Storage '{path}' was written with modified FILETIME {want.ModifiedFileTime.ToString(CultureInfo.InvariantCulture)} and has {storage.ModifiedFileTime.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static void Verify(Storage storage, string prefix, IReadOnlyDictionary<string, ContainerEntry> expected, HashSet<string> found, byte[] buffer, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"The written container nests storages deeper than {MaxDepth} levels at '{prefix}'.");
        foreach (var entry in storage.EnumerateEntries())
        {
            string path = prefix + entry.Name;
            if (!expected.TryGetValue(path, out var want)) throw new InvalidDataException($"The written container holds '{path}', which was not written.");
            if (!found.Add(path)) throw new InvalidDataException($"The written container holds '{path}' twice.");
            if (entry.Type == EntryType.Storage)
            {
                if (want.IsStream) throw new InvalidDataException($"'{path}' was written as a stream and is a storage.");
                var child = storage.OpenStorage(entry.Name);
                VerifyStorageMetadata(child, path, want.Storage);
                Verify(child, path + "/", expected, found, buffer, depth + 1);
                continue;
            }
            if (!want.IsStream) throw new InvalidDataException($"'{path}' was written as a storage and is a stream.");
            // The declared length of the directory entry must be the written one; a stream whose sectors are missing reads as empty.
            if (entry.Length != want.Length)
                throw new InvalidDataException($"Stream '{path}' was written with {want.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes and its directory entry declares {entry.Length.ToString("N0", CultureInfo.InvariantCulture)}.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (var input = storage.OpenStream(entry.Name))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
            }
            if (length != want.Length)
                throw new InvalidDataException($"Stream '{path}' was written with {want.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes and reads {length.ToString("N0", CultureInfo.InvariantCulture)}.");
            if (Convert.ToHexString(hash.GetHashAndReset()) != want.Sha256)
                throw new InvalidDataException($"Stream '{path}' reads other bytes than were written.");
        }
    }
}
