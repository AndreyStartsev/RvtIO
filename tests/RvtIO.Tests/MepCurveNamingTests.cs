using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile names a pipe, flex pipe, pipe insulation and cable tray "{system family}:{type}" from the element's own
/// RbsCurve.m_idType and the type's own SymbolInfo name. The general type link and the name table can name another element.</summary>
public class MepCurveNamingTests
{
    [Theory]
    [InlineData("PipeTypes", "Pipe Types")]
    [InlineData("FlexPipeRound", "Flex Pipe Round")]
    [InlineData("PipeInsulation", "Pipe Insulation")]
    [InlineData("CableTrayWithFittings", "Cable Tray with Fittings")]
    [InlineData("CableTrayWithoutFittings", "Cable Tray without Fittings")]
    [InlineData("OvalDuct", "Oval Duct")]
    [InlineData("RectangularDuct", "Rectangular Duct")]
    [InlineData("FlexDuctRound", "Flex Duct Round")]
    [InlineData("FlexDuctRectangular", "Flex Duct Rectangular")]
    [InlineData("ConduitWithFittings", "Conduit with Fittings")]
    [InlineData("ConduitWithoutFittings", "Conduit without Fittings")]
    public void EnglishResourcesGiveTheFamilyNamesOfTheReference(string kind, string expected) =>
        Assert.Equal(expected, RevitSystemFamilyNames.Load("2022", "ENU")!
            .Get(Enum.Parse<SystemFamilyKind>(kind)));

    [Theory]
    [InlineData("RUS", "PipeInsulation")]
    [InlineData("RUS", "PipeTypes")]
    public void TheCommandTextOfPipeInsulationLosesItsTooltipLine(string locale, string kind)
    {
        string? name = RevitSystemFamilyNames.Load("2022", locale)!
            .Get(Enum.Parse<SystemFamilyKind>(kind));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain('\n', name);
    }

    [Theory]
    [InlineData(3227, -2008044, null, "PipeTypes")]
    [InlineData(3181, -2008050, null, "FlexPipeRound")]
    [InlineData(3217, -2008122, null, "PipeInsulation")]
    [InlineData(3130, -2008130, 1, "CableTrayWithFittings")]
    [InlineData(3130, -2008130, 0, "CableTrayWithoutFittings")]
    // The profile class of an AbsCurveType selects the duct family (124 round, 126 oval, 127 rectangular); the conduit type
    // selects by m_bWithFitting like the cable tray type.
    [InlineData(110, -2008000, 124, "RoundDuct")]
    [InlineData(110, -2008000, 126, "OvalDuct")]
    [InlineData(110, -2008000, 127, "RectangularDuct")]
    [InlineData(114, -2008020, 124, "FlexDuctRound")]
    [InlineData(114, -2008020, 127, "FlexDuctRectangular")]
    [InlineData(3135, -2008132, 1, "ConduitWithFittings")]
    [InlineData(3135, -2008132, 0, "ConduitWithoutFittings")]
    // A class never borrows another family's label, and a cable tray type needs its m_bWithFitting value.
    [InlineData(3227, -2008000, null, null)]
    [InlineData(3181, -2008044, null, null)]
    [InlineData(3130, -2008130, null, null)]
    [InlineData(3130, -2008044, 1, null)]
    [InlineData(3135, -2008132, null, null)]
    [InlineData(110, -2008000, 128, null)]
    public void TypeRootClassSelectsTheFamilyKind(int cls, int category, int? discriminator, string? expected) =>
        Assert.Equal(expected == null ? (SystemFamilyKind?)null : Enum.Parse<SystemFamilyKind>(expected),
            RevitSystemFamilyNames.KindOf((ushort)cls, discriminator, category));

    [Theory]
    [InlineData("ok")] [InlineData("duplicate_element")] [InlineData("block_only")] [InlineData("class_mismatch")]
    [InlineData("duplicate_root")] [InlineData("duplicate_name")] [InlineData("empty_name")]
    [InlineData("missing_root")] [InlineData("missing_name")] [InlineData("nonpositive_id")]
    public void TypeNameNeedsOneUniqueMaterializedTypeWithAnExactRoot(string mode)
    {
        var type = new Element { Id = mode == "nonpositive_id" ? 0 : 346163, CategoryId = -2008044,
            Name = "Сегмент трубы", ValidatedMepTypeName = "stale validation" };
        var state = new ParsingState();
        state.SystemTypeRoots[346163] = (1, 3227, null);
        state.MepTypeNames[346163] = (1, 3227, "Труба стальная электросварная");
        var elements = new List<Element> { type };
        switch (mode)
        {
            case "duplicate_element": elements.Add(new Element { Id = 346163 }); break;
            case "block_only": type.IsBlockOnly = true; break;
            case "class_mismatch": state.SystemTypeRoots[346163] = (1, 3217, null); break;
            case "duplicate_root": state.SystemTypeRoots[346163] = (2, 0, null); break;
            case "duplicate_name": state.MepTypeNames[346163] = (2, 0, null); break;
            case "empty_name": state.MepTypeNames[346163] = (1, 3227, " "); break;
            case "missing_root": state.SystemTypeRoots.Clear(); break;
            case "missing_name": state.MepTypeNames.Clear(); break;
        }
        ElementBuilder.ApplyValidatedMepTypeNames(elements, state);
        Assert.Equal(mode == "ok" ? "Труба стальная электросварная" : null, type.ValidatedMepTypeName);
        Assert.Equal("Сегмент трубы", type.Name);
    }

    [Theory]
    [InlineData("ok", 3210, 3227)] [InlineData("ok", 3180, 3181)] [InlineData("ok", 3216, 3217)]
    [InlineData("ok", 598, 3130)] [InlineData("ok", 3148, 110)]
    [InlineData("pipe_to_insulation_type", 3210, 3217)] [InlineData("unsupported_class", 3173, 3135)]
    [InlineData("ok", 3134, 3135)]
    [InlineData("duplicate_occurrence", 3210, 3227)] [InlineData("duplicate_type", 3210, 3227)]
    [InlineData("occurrence_read_twice", 3210, 3227)] [InlineData("type_root_read_twice", 3210, 3227)]
    [InlineData("no_type_link", 3210, 3227)] [InlineData("self_link", 3210, 3227)]
    [InlineData("block_only", 3210, 3227)] [InlineData("missing_type_root", 3210, 3227)]
    public void OccurrenceTypeNeedsItsOwnLinkAndATypeRootOfTheMatchingClass(string mode, int occurrenceClass, int typeClass)
    {
        var occurrence = new Element { Id = 725459, CategoryId = -2008044, SerializedMepCurveTypeId = 1 };
        var type = new Element { Id = 346163, CategoryId = -2008044 };
        var elements = new List<Element> { occurrence, type };
        var state = new ParsingState();
        state.SystemOccurrenceTypeIds[725459] = (1, (ushort)occurrenceClass, 346163);
        state.SystemTypeRoots[346163] = (1, (ushort)typeClass, null);
        switch (mode)
        {
            case "pipe_to_insulation_type": state.SystemTypeRoots[346163] = (1, 3217, null); break;
            case "duplicate_occurrence": elements.Add(new Element { Id = 725459 }); break;
            case "duplicate_type": elements.Add(new Element { Id = 346163 }); break;
            case "occurrence_read_twice": state.SystemOccurrenceTypeIds[725459] = (2, 0, null); break;
            case "type_root_read_twice": state.SystemTypeRoots[346163] = (2, 0, null); break;
            case "no_type_link": state.SystemOccurrenceTypeIds[725459] = (1, (ushort)occurrenceClass, null); break;
            case "self_link": state.SystemOccurrenceTypeIds[725459] = (1, (ushort)occurrenceClass, 725459); break;
            case "block_only": occurrence.IsBlockOnly = true; break;
            case "missing_type_root": state.SystemTypeRoots.Clear(); break;
        }
        ElementBuilder.ApplyMepCurveTypeLinks(elements, state);
        Assert.Equal(mode == "ok" ? 346163 : (int?)null, occurrence.SerializedMepCurveTypeId);
        Assert.Null(type.SerializedMepCurveTypeId);
    }

    private static (Element Pipe, Dictionary<int, Element> ById, Dictionary<int, int> Counts) PipeWithMisleadingGeneralLink()
    {
        // The general type link (ELEM_TYPE_PARAM) points to a fitting symbol; the own link is the pipe type.
        var symbol = new Element { Id = 50, CategoryId = -2008049, Name = "Исполнение 1", FamilyName = "XS_Переход" };
        var type = new Element { Id = 346163, CategoryId = -2008044, Name = "Сегмент трубы",
            ValidatedMepTypeName = "XS_Сталь нержавеющая сварная_Тройники", ValidatedMepFamilyName = "Pipe Types" };
        var pipe = new Element { Id = 725459, CategoryId = -2008044, Category = "OST_PipeCurves", TypeId = 50,
            Name = "Исполнение 1", FamilyName = "XS_Переход", SerializedMepCurveTypeId = 346163 };
        pipe.Parameters.Add(new Parameter
        {
            Id = -1002050, StorageType = StorageType.ElementId, Value = 50,
            Definition = new Definition { Id = -1002050, BuiltInParameter = "ELEM_TYPE_PARAM", Name = "ELEM_TYPE_PARAM" },
        });
        var byId = new[] { symbol, type, pipe }.ToDictionary(e => e.Id);
        return (pipe, byId, byId.Keys.ToDictionary(id => id, _ => 1));
    }

    [Fact]
    public void PipeIsNamedAfterItsOwnTypeAndSystemFamily_InCurrentOnly()
    {
        var (pipe, byId, counts) = PipeWithMisleadingGeneralLink();

        Assert.Equal("Pipe Types:XS_Сталь нержавеющая сварная_Тройники",
            IfcExporter.ResolveProductFamilyAndTypeName(pipe, "IFCFLOWSEGMENT", byId, counts, currentProfile: true));
        // The legacy profile keeps the general fields.
        Assert.DoesNotContain("Pipe Types", IfcExporter.ResolveProductFamilyAndTypeName(pipe, "IFCFLOWSEGMENT", byId, counts));
    }

    [Fact]
    public void ReferenceFallbackIsTheOwnTypeName()
    {
        var (pipe, byId, _) = PipeWithMisleadingGeneralLink();

        Assert.Equal("XS_Сталь нержавеющая сварная_Тройники", IfcExporter.ResolveMepCurveTypeName(pipe, byId));
    }

    [Theory]
    [InlineData("no_family")] [InlineData("no_name")] [InlineData("no_link")] [InlineData("link_to_missing_element")]
    public void IncompleteValidationKeepsTheGeneralName(string mode)
    {
        var (pipe, byId, counts) = PipeWithMisleadingGeneralLink();
        var type = byId[346163];
        switch (mode)
        {
            case "no_family": type.ValidatedMepFamilyName = null; break;
            case "no_name": type.ValidatedMepTypeName = null; break;
            case "no_link": pipe.SerializedMepCurveTypeId = null; break;
            case "link_to_missing_element": pipe.SerializedMepCurveTypeId = 999; break;
        }

        Assert.Equal(mode == "no_family" ? "XS_Сталь нержавеющая сварная_Тройники" : null,
            IfcExporter.ResolveMepCurveTypeName(pipe, byId));
        Assert.Null(IfcExporter.ResolveMepCurveFamilyAndTypeName(pipe, byId));
        Assert.Equal("XS_Переход:Исполнение 1",
            IfcExporter.ResolveProductFamilyAndTypeName(pipe, "IFCFLOWSEGMENT", byId, counts, currentProfile: true));
    }

    [Fact]
    public void RoundDuctUsesItsExistingTypeNameValidation()
    {
        var type = new Element { Id = 2146, CategoryId = -2008000, ValidatedDuctTypeName = "По умолчанию",
            ValidatedSystemFamilyName = "Round Duct" };
        var duct = new Element { Id = 77, CategoryId = -2008000, SerializedMepCurveTypeId = 2146 };
        var byId = new[] { type, duct }.ToDictionary(e => e.Id);

        Assert.Equal("Round Duct:По умолчанию", IfcExporter.ResolveMepCurveFamilyAndTypeName(duct, byId));
    }

    [Fact]
    public void TypeObjectNameUsesTheOwnNameOnlyInCurrent()
    {
        var type = new Element { Id = 553634, CategoryId = -2008044, Name = "Сегмент трубы",
            ValidatedMepTypeName = "Гильза из х/ц трубы", ValidatedMepFamilyName = "Pipe Types" };

        Assert.Equal(("Pipe Types:Гильза из х/ц трубы", "Гильза из х/ц трубы"), IfcExporter.TypeObjectNameParts(type, true));
        Assert.Equal(("Сегмент трубы", "Сегмент трубы"), IfcExporter.TypeObjectNameParts(type));
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    [Fact]
    public void RealPipeTypeBlockGivesItsOwnNameAndRoot()
    {
        byte[] block = Convert.FromBase64String(MepCurveBlocks.PipeType);
        Assert.Equal("Гильза из х/ц трубы ГОСТ 31416-2009", TfBlockDeserializer.ExtractMepTypeName(block, 0, Size(block), 3227));
        Assert.True(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, Size(block), 3227, out int? discriminator));
        Assert.Null(discriminator);
        // The class of the frame must be the class asked for.
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, 0, Size(block), 3217));
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, 0, Size(block), 3130));
        block[^1] ^= 1;
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, 0, Size(block), 3227));
    }

    [Fact]
    public void RealCableTrayTypeBlockGivesItsNameAndTheWithFittingFlag()
    {
        byte[] block = Convert.FromBase64String(MepCurveBlocks.CableTrayType);
        Assert.Equal("IEK_NF_Лоток неперфорированный HDZ_L=3000_t=1,2",
            TfBlockDeserializer.ExtractMepTypeName(block, 0, Size(block), 3130));
        Assert.True(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, Size(block), 3130, out int? withFitting));
        Assert.Equal(1, withFitting);
    }

    [Fact]
    public void RealOccurrenceBlocksGiveTheirOwnTypeLink()
    {
        byte[] pipe = Convert.FromBase64String(MepCurveBlocks.PipeOccurrence);
        byte[] tray = Convert.FromBase64String(MepCurveBlocks.CableTrayOccurrence);
        Assert.Equal(553634, TfBlockDeserializer.ExtractSystemOccurrenceTypeId(pipe, 0, Size(pipe), 3210));
        Assert.Equal(377386, TfBlockDeserializer.ExtractSystemOccurrenceTypeId(tray, 0, Size(tray), 598));
        // A wrong class tag or a broken footer confirms nothing.
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(pipe, 0, Size(pipe), 3216));
        pipe[^1] ^= 1;
        Assert.Null(TfBlockDeserializer.ExtractSystemOccurrenceTypeId(pipe, 0, Size(pipe), 3210));
    }

    [Theory]
    [InlineData("pipe", "Pipe Types", "Гильза из х/ц трубы ГОСТ 31416-2009")]
    [InlineData("tray", "Cable Tray with Fittings", "IEK_NF_Лоток неперфорированный HDZ_L=3000_t=1,2")]
    public void BlocksToProductName_NameTableAndGeneralTypeLinkAreNotNeeded(string kind, string family, string typeName)
    {
        bool pipe = kind == "pipe";
        byte[] occurrenceBlock = Convert.FromBase64String(pipe ? MepCurveBlocks.PipeOccurrence : MepCurveBlocks.CableTrayOccurrence);
        byte[] typeBlock = Convert.FromBase64String(pipe ? MepCurveBlocks.PipeType : MepCurveBlocks.CableTrayType);
        ushort occurrenceClass = pipe ? (ushort)3210 : (ushort)598, typeClass = pipe ? (ushort)3227 : (ushort)3130;
        uint occurrenceId = pipe ? 725949u : 552249u, typeId = pipe ? 553634u : 377386u;
        int category = pipe ? -2008044 : -2008130;

        var state = new ParsingState();
        RvtFileReader.RecordSystemTypeRoot(state, typeBlock, 0, Size(typeBlock), typeClass, typeId);
        RvtFileReader.RecordSystemOccurrenceTypeId(state, occurrenceBlock, 0, Size(occurrenceBlock), occurrenceClass, occurrenceId);
        // The general fields carry the name table's string and another element's symbol.
        var type = new Element { Id = (int)typeId, CategoryId = category, Name = "Сегмент трубы" };
        var occurrence = new Element { Id = (int)occurrenceId, CategoryId = category, Category = pipe ? "OST_PipeCurves" : "OST_CableTray",
            Name = "Сегмент трубы", FamilyName = "Чужое семейство" };
        var elements = new List<Element> { occurrence, type };

        ElementBuilder.ApplyValidatedMepTypeNames(elements, state);
        ElementBuilder.ApplyMepCurveTypeLinks(elements, state);
        ElementBuilder.ApplyValidatedSystemFamilyNames(elements, state, RevitSystemFamilyNames.Load("2022", "ENU"));

        Assert.Equal((int)typeId, occurrence.SerializedMepCurveTypeId);
        Assert.Equal(family, type.ValidatedMepFamilyName);
        Assert.Null(type.ValidatedSystemFamilyName);
        var byId = elements.ToDictionary(e => e.Id);
        var counts = byId.Keys.ToDictionary(id => id, _ => 1);
        Assert.Equal($"{family}:{typeName}",
            IfcExporter.ResolveProductFamilyAndTypeName(occurrence, "IFCFLOWSEGMENT", byId, counts, currentProfile: true));
        Assert.Equal((family + ":" + typeName, typeName), IfcExporter.TypeObjectNameParts(type, true));
        // The legacy profile output does not change: the general fields stay in charge.
        Assert.Equal("Чужое семейство:Сегмент трубы",
            IfcExporter.ResolveProductFamilyAndTypeName(occurrence, "IFCFLOWSEGMENT", byId, counts));
        Assert.Equal(("Сегмент трубы", "Сегмент трубы"), IfcExporter.TypeObjectNameParts(type));
    }

    [Fact]
    public void MepCurveFamilyNamesOfRevit2021_NameAPipeAndACableTrayTypeAndNothingElse()
    {
        var state = new ParsingState();
        byte[] pipeType = Convert.FromBase64String(MepCurveBlocks.PipeType);
        byte[] trayType = Convert.FromBase64String(MepCurveBlocks.CableTrayType);
        RvtFileReader.RecordSystemTypeRoot(state, pipeType, 0, Size(pipeType), 3227, 553634);
        RvtFileReader.RecordSystemTypeRoot(state, trayType, 0, Size(trayType), 3130, 377386);
        var pipe = new Element { Id = 553634, CategoryId = -2008044 };
        var tray = new Element { Id = 377386, CategoryId = -2008130 };

        int assigned = ElementBuilder.ApplyValidatedSystemFamilyNames([pipe, tray], state,
            RevitSystemFamilyNames.LoadPreviousReleaseFamilies("2021", "ENU", "ENU"));

        Assert.Equal(2, assigned);
        Assert.Equal("Pipe Types", pipe.ValidatedMepFamilyName);
        Assert.Equal("Cable Tray with Fittings", tray.ValidatedMepFamilyName);
        Assert.Equal(0, ElementBuilder.ApplyValidatedSystemFamilyNames([pipe, tray], state, RevitSystemFamilyNames.Load("2021", "ENU")));
    }

    [Fact]
    public void RepeatedIdenticalOccurrenceBlockKeepsTheValidationButADifferentOneBreaksIt()
    {
        byte[] pipe = Convert.FromBase64String(MepCurveBlocks.PipeOccurrence);
        var state = new ParsingState();
        RvtFileReader.RecordSystemOccurrenceTypeId(state, pipe, 0, Size(pipe), 3210, 725949);
        RvtFileReader.RecordSystemOccurrenceTypeId(state, pipe, 0, Size(pipe), 3210, 725949);
        Assert.Equal((1, (ushort)3210, (int?)553634), state.SystemOccurrenceTypeIds[725949]);

        // The same bytes under another owner id do not read as that owner's own root.
        RvtFileReader.RecordSystemOccurrenceTypeId(state, pipe, 0, Size(pipe), 3210, 725950);
        RvtFileReader.RecordSystemOccurrenceTypeId(state, pipe, 0, Size(pipe), 3210, 725950);
        Assert.Equal((2, (ushort)0, (int?)null), state.SystemOccurrenceTypeIds[725950]);
    }

    [Fact]
    public void RepeatedIdenticalTypeBlockKeepsTheValidationButAnotherClassBreaksIt()
    {
        byte[] type = Convert.FromBase64String(MepCurveBlocks.PipeType);
        var state = new ParsingState();
        RvtFileReader.RecordSystemTypeRoot(state, type, 0, Size(type), 3227, 553634);
        RvtFileReader.RecordSystemTypeRoot(state, type, 0, Size(type), 3227, 553634);
        Assert.Equal((1, (ushort)3227, (int?)null), state.SystemTypeRoots[553634]);
        Assert.Equal((1, (ushort)3227, "Гильза из х/ц трубы ГОСТ 31416-2009"), state.MepTypeNames[553634]);

        // A frame of another class under the same id is a conflicting root.
        byte[] tray = Convert.FromBase64String(MepCurveBlocks.CableTrayType);
        RvtFileReader.RecordSystemTypeRoot(state, tray, 0, Size(tray), 3130, 553634);
        Assert.Equal(2, state.SystemTypeRoots[553634].BlockCount);
        Assert.Equal((ushort)0, state.SystemTypeRoots[553634].ClassId);
        Assert.Equal((2, (ushort)0, (string?)null), state.MepTypeNames[553634]);
    }

    [Fact]
    public void UnframedBytesGiveNoMepTypeNameOrRoot()
    {
        // An unrelated, unframed byte run confirms neither the root nor a name.
        byte[] block = new byte[64];
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, 0, 48, 3130));
        Assert.False(TfBlockDeserializer.TryExtractSystemTypeRoot(block, 0, 48, 3130, out int? withFitting));
        Assert.Null(withFitting);
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, -1, 48, 3130));
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, 0, uint.MaxValue, 3227));
        Assert.Null(TfBlockDeserializer.ExtractMepTypeName(block, 0, 48, 110));
    }
}
