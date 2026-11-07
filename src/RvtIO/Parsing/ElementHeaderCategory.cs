using RvtIO.Internal;

namespace RvtIO.Parsing;

/// <summary>
/// The ElementHeader every element record starts with (schema class 1340): its m_categroryId,
/// m_designOptionId and the axis-aligned outline it stores.
/// </summary>
internal static class ElementHeaderCategory
{
    private const int RegenCountOffset = 10;
    private const int RegenEntryLength = 14;

    // The header marks its stored outline with the tags of two classes of the embedded 2022 format
    // schema, and the pair is written the same in every release's records whatever ids the file's
    // own schema table gives these classes (a 2023 file declares Outline as 316 and still writes
    // 297), so the tags resolve by name from the embedded table alone.
    private static readonly Lazy<ushort> OutlineTag = new(() => (ushort)TagOfShippedSchema("Outline"));
    private static readonly Lazy<ushort> ParentsTag = new(() => (ushort)TagOfShippedSchema("ElementParents"));

    private static int TagOfShippedSchema(string name)
    {
        foreach (var (id, tfClass) in TfBlockDeserializer.Schemas)
            if (tfClass.Name == name) return id;
        throw new InvalidOperationException($"The embedded format schema has no class {name}.");
    }

    /// <summary>
    /// Reads m_categroryId of the record that starts at <paramref name="recordStart"/>; null when
    /// the bytes do not form a complete ElementHeader record (class tag, declared size or entry
    /// count do not fit the buffer).
    /// </summary>
    public static int? Read(byte[] buffer, int recordStart)
    {
        if (recordStart < 0 || (long)recordStart + RegenCountOffset + 4 > buffer.Length) return null;
        ushort recordClass = BitConverter.ToUInt16(buffer, recordStart + 8);
        if (recordClass is not (0x053C or 0x04F8)) return null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        long categoryOffset = (long)recordStart + RegenCountOffset + 4
            + RegenEntryLength * (long)BitConverter.ToUInt32(buffer, recordStart + RegenCountOffset);
        if (recordEnd > buffer.Length || categoryOffset + 4 > recordEnd) return null;
        return BitConverter.ToInt32(buffer, (int)categoryOffset);
    }

    /// <summary>
    /// Reads ElementHeader.m_designOptionId of the same record: the fields m_familyId and
    /// m_ownerViewId (int32 each) lie between the category and the design option.
    /// </summary>
    public static int? ReadDesignOption(byte[] buffer, int recordStart)
    {
        if (Read(buffer, recordStart) is null) return null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        long optionOffset = (long)recordStart + RegenCountOffset + 4
            + RegenEntryLength * (long)BitConverter.ToUInt32(buffer, recordStart + RegenCountOffset) + 12;
        return optionOffset + 4 <= recordEnd ? BitConverter.ToInt32(buffer, (int)optionOffset) : null;
    }

    /// <summary>
    /// The axis-aligned outline the record that starts at <paramref name="recordStart"/> stores:
    /// six little-endian doubles (min xyz, max xyz, feet, project coordinates) when the outline tag
    /// stands behind it, no box when 0 does. Null when the record holds no such pattern, or when
    /// the six doubles do not form one (a reference count must not be taken for a box, so only
    /// finite ordered values count).
    /// </summary>
    public static ElementHeaderBox? ReadBoundingBox(byte[] buffer, int recordStart)
    {
        if (Read(buffer, recordStart) is null) return null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        return ReadOutline(buffer, recordStart + RegenCountOffset + 4, recordEnd);
    }

    /// <summary>
    /// The axis-aligned outline the header stores behind [outline tag or 0][ff ff ff ff]
    /// [parents tag]: six little-endian doubles (min xyz, max xyz, feet, project coordinates) when
    /// the outline tag stands there, no box when it is 0. Null when the range holds no such
    /// pattern, or when the six doubles do not form one (a reference count must not be taken for a
    /// box, so only finite ordered values count).
    /// </summary>
    private static ElementHeaderBox? ReadOutline(byte[] buffer, int start, long end)
    {
        ushort outlineTag = OutlineTag.Value, parentsTag = ParentsTag.Value;
        for (long i = start; i + 8 <= end; i++)
        {
            ushort tag = BitConverter.ToUInt16(buffer, (int)i);
            if (tag != outlineTag && tag != 0) continue;
            if (BitConverter.ToUInt32(buffer, (int)i + 2) != 0xFFFFFFFF) continue;
            if (BitConverter.ToUInt16(buffer, (int)i + 6) != parentsTag) continue;
            if (tag != 0)
            {
                if (i + 8 + 48 > end) return null;
                var box = new double[6];
                for (int d = 0; d < 6; d++) box[d] = BitConverter.ToDouble(buffer, (int)i + 8 + 8 * d);
                if (box.Any(value => !double.IsFinite(value))
                    || box[0] > box[3] || box[1] > box[4] || box[2] > box[5])
                    return null;
                return new ElementHeaderBox(true, box);
            }
            return new ElementHeaderBox(false, null);
        }
        return null;
    }

    /// <summary>
    /// Stores the header category, design option and outline of a record the anchored stream walk
    /// accepted. An id whose records disagree (or whose header cannot be read) is stored as null,
    /// which stays null: unknown is never turned into a guess.
    /// </summary>
    public static void Record(ParsingState state, byte[] buffer, int recordStart, uint elementId)
    {
        int? category = Read(buffer, recordStart);
        if (!state.HeaderCategories.TryGetValue(elementId, out int? previous)) state.HeaderCategories[elementId] = category;
        else if (previous != category) state.HeaderCategories[elementId] = null;
        int? option = ReadDesignOption(buffer, recordStart);
        if (!state.HeaderDesignOptions.TryGetValue(elementId, out int? previousOption)) state.HeaderDesignOptions[elementId] = option;
        else if (previousOption != option) state.HeaderDesignOptions[elementId] = null;
        long recordEnd = (long)recordStart + 12 + BitConverter.ToUInt32(buffer, recordStart + 4);
        Store(state, elementId, Read(buffer, recordStart) == null
            ? null : ReadOutline(buffer, recordStart + RegenCountOffset + 4, recordEnd));
    }

    /// <summary>
    /// Stores the outline of a record whose body starts with the serialized ElementHeader (the
    /// record layout of the files whose element ids are 64-bit, where the anchored walk does not
    /// apply). The verdict rules are the ones of <see cref="Record"/>.
    /// </summary>
    public static void RecordBody(ParsingState state, byte[] body, uint elementId)
    {
        if (body.Length < 8) return;
        ushort recordClass = BitConverter.ToUInt16(body, 0);
        if (recordClass is not (0x053C or 0x04F8)) return;
        Store(state, elementId, ReadOutline(body, 2, body.Length));
    }

    private static void Store(ParsingState state, uint elementId, ElementHeaderBox? box)
    {
        if (Environment.GetEnvironmentVariable("RVTIO_HDR_DIAG") == "1")
            DiagnosticSink.WriteLine($"HDRVERDICT {elementId} {(box == null ? "unknown" : box.Present ? "box" : "none")}");
        if (!state.HeaderBoundingBoxes.TryGetValue(elementId, out ElementHeaderBox? previousBox)) state.HeaderBoundingBoxes[elementId] = box;
        else if (!SameBox(previousBox, box)) state.HeaderBoundingBoxes[elementId] = null;
    }

    private static bool SameBox(ElementHeaderBox? first, ElementHeaderBox? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first is null || second is null) return false;
        return first.Present == second.Present
            && (first.Box == second.Box || first.Box != null && second.Box != null
                && first.Box.SequenceEqual(second.Box));
    }
}

/// <summary>The outline verdict of an element's header records: a stored box, or the absence of one.</summary>
public sealed record ElementHeaderBox(bool Present, double[]? Box);
