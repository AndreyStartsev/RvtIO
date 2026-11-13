using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Exact occurrence-only Pset_CoveringCommon group. Every successfully emitted,
/// tagged ceiling covering with a QTO Reference reuses that exact property entity. Finish
/// follows FinishCalculator: the covering's own "Finish" parameter, then for a ceiling the
/// type's parameter or its Finish1/Finish2 layer material names. Other CoveringCommon
/// properties, non-coverings and type-level Psets are not covered here.</summary>
public class IfcExporterCoveringCommonPropertySetTests
{
    [Fact]
    public void SetQualifiedFinish_WinsOverThePropertyName()
    {
        Element covering = Product(
            id: 8010, typeId: 8011, categoryId: -2000038,
            category: "OST_Ceilings", name: "Acoustic ceiling");
        covering.Parameters.Add(new Parameter
        {
            Id = 9810, Definition = new Definition { Id = 9810, Name = "Finish" },
            StorageType = StorageType.String, Value = "Plain finish",
        });
        covering.Parameters.Add(new Parameter
        {
            Id = 9811, Definition = new Definition { Id = 9811, Name = "Pset_CoveringCommon.Finish" },
            StorageType = StorageType.String, Value = "Qualified finish",
        });

        string ifc = ExportToText(covering);

        Assert.Equal("IFCTEXT('Qualified finish')",
            Properties(ifc, SolePset(ifc, "Pset_CoveringCommon"))["Finish"].Expression);
    }

    [Fact]
    public void EmittedCeilingCovering_ReusesQtoReferenceEntity_AndOwnFinishParameter()
    {
        Element covering = Product(
            id: 8000, typeId: 8001, categoryId: -2000038,
            category: "OST_Ceilings", name: "Acoustic ceiling");
        covering.Parameters.Add(new Parameter
        {
            Id = 9800,
            Definition = new Definition { Id = 9800, Name = "Finish" },
            StorageType = StorageType.String,
            Value = "Finish 2",
        });

        string ifc = ExportToText(covering);

        int coveringId = SoleEntityId(ifc, "IFCCOVERING");
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_CoveringCommon");
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);

        Assert.Equal(coveringId, SolePsetTarget(ifc, qto.Id));
        Assert.Equal(coveringId, SolePsetTarget(ifc, common.Id));
        Assert.Equal(Assert.Single(qto.ValueIds), common.ValueIds[0]);
        Assert.Equal(qto.ValueIds[0], properties["Reference"].Id);
        Assert.Equal(
            "IFCIDENTIFIER('Acoustic ceiling')",
            properties["Reference"].Expression);
        Assert.Equal(new[] { "Reference", "Finish" }, properties.Keys);
        Assert.Equal("IFCTEXT('Finish 2')", properties["Finish"].Expression);
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('Acoustic ceiling'\)"));
        Assert.Contains(IfcGuid.FromName("covering-common-pset:8000"), ifc);
        Assert.Contains(IfcGuid.FromName("covering-common-rel:8000"), ifc);
        Assert.DoesNotContain("IFCCOVERINGTYPE", ifc);
    }

    [Fact]
    public void SharedReferenceAcrossCoverings_PoolsValue_ButKeepsUniqueAttachments()
    {
        string ifc = ExportToText(
            Product(8010, 8012, -2000038, "OST_Ceilings", "Shared ceiling", 0),
            Product(8011, 8012, -2000038, "OST_Ceilings", "Shared ceiling", 10));

        int[] coveringIds = EntityIds(ifc, "IFCCOVERING").OrderBy(id => id).ToArray();
        List<Pset> qtos = Psets(ifc, "Pset_QuantityTakeOff");
        List<Pset> commons = Psets(ifc, "Pset_CoveringCommon");

        Assert.Equal(2, coveringIds.Length);
        Assert.Equal(2, qtos.Count);
        Assert.Equal(2, commons.Count);
        int referenceId = Assert.Single(qtos.SelectMany(pset => pset.ValueIds).Distinct());
        Assert.All(commons, common =>
            Assert.Equal(referenceId, Assert.Single(common.ValueIds)));
        Assert.Equal(
            coveringIds,
            commons.Select(pset => SolePsetTarget(ifc, pset.Id))
                .OrderBy(id => id).ToArray());
        Assert.Single(Regex.Matches(
            ifc,
            @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('Shared ceiling'\)"));

        foreach (int id in new[] { 8010, 8011 })
        {
            Assert.Contains(IfcGuid.FromName($"covering-common-pset:{id}"), ifc);
            Assert.Contains(IfcGuid.FromName($"covering-common-rel:{id}"), ifc);
        }
        List<string> commonGlobalIds = Regex.Matches(
                ifc,
                @"#\d+=(?:IFCPROPERTYSET|IFCRELDEFINESBYPROPERTIES)\('([^']+)'[^\r\n]*(?:Pset_CoveringCommon|covering-common)")
            .Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(commonGlobalIds.Count, commonGlobalIds.Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullOrBlockedQtoReference_CreatesNoCoveringCommonOrphan(bool blocked)
    {
        Element covering = Product(
            id: 8020, typeId: 8021, categoryId: -2000038,
            category: "OST_Ceilings",
            name: blocked ? "Fallback ceiling reference" : null);
        covering.ValidatedCeilingTypeReferenceIsBlocked = blocked;

        string ifc = ExportToText(covering);

        Assert.True(SoleEntityId(ifc, "IFCCOVERING") > 0);
        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        Assert.DoesNotContain("Pset_CoveringCommon", ifc);
        Assert.DoesNotContain("covering-common-pset", ifc);
        Assert.DoesNotContain("covering-common-rel", ifc);
    }

    [Fact]
    public void ExplicitReferenceResolver_CreatesQtoAndCoveringCommonWithoutBaselineName()
    {
        Element covering = Product(
            id: 8030, typeId: 8031, categoryId: -2000038,
            category: "OST_Ceilings", name: null);
        covering.Parameters.Add(new Parameter
        {
            Id = 9830,
            Definition = new Definition { Id = 9830, Name = "Re fer ence" },
            StorageType = StorageType.String,
            Value = "Authored ceiling reference",
        });

        string ifc = ExportToText(covering);
        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_CoveringCommon");

        Assert.Equal(Assert.Single(qto.ValueIds), Assert.Single(common.ValueIds));
        Assert.Equal(
            "IFCIDENTIFIER('Authored ceiling reference')",
            Properties(ifc, common)["Reference"].Expression);
        Assert.DoesNotContain("IFCIDENTIFIER('Element 8030')", ifc);
    }

    [Fact]
    public void EmittedNonCovering_KeepsQtoButReceivesNoCoveringCommon()
    {
        string ifc = ExportToText(Product(
            id: 8040, typeId: 8041, categoryId: -2000032,
            category: "OST_Floors", name: "Floor type"));

        Assert.True(SoleEntityId(ifc, "IFCSLAB") > 0);
        Assert.Single(Psets(ifc, "Pset_QuantityTakeOff"));
        Assert.DoesNotContain("Pset_CoveringCommon", ifc);
        Assert.DoesNotContain("covering-common-pset", ifc);
        Assert.DoesNotContain("covering-common-rel", ifc);
    }

    [Fact]
    public void GeometrylessCeiling_CreatesNoProductQtoOrCoveringCommon()
    {
        Element covering = Product(
            id: 8050, typeId: 8051, categoryId: -2000038,
            category: "OST_Ceilings", name: "Suppressed ceiling");
        covering.Geometry = null;

        string ifc = ExportToText(covering);

        Assert.Empty(EntityIds(ifc, "IFCCOVERING"));
        Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
        Assert.DoesNotContain("Pset_CoveringCommon", ifc);
        Assert.DoesNotContain("covering-common-pset", ifc);
        Assert.DoesNotContain("covering-common-rel", ifc);
    }

    [Fact]
    public void CeilingFinishLayerMaterials_AreDistinctInLayerOrder_AndPrivate()
    {
        Element first = CeilingWithType(8100, 8190, 0);
        Element second = CeilingWithType(8101, 8190, 10);
        Element type = CeilingType(8190,
            Layer(1, 8201), Layer(5, 8202), Layer(4, 8203), Layer(5, 8202), Layer(100, 8204));

        // The current profile shares an equal Finish text (the property-set rule cache); the legacy profile does not.
        string ifc = ExportToText(IfcProfile.Legacy, first, second, type,
            Material(8201, "Structure"), Material(8202, "Plaster"),
            Material(8203, "Paint"), Material(8204, "Membrane"));

        List<Pset> commons = Psets(ifc, "Pset_CoveringCommon");
        Assert.Equal(2, commons.Count);
        foreach (Pset common in commons)
        {
            Dictionary<string, PropertyValue> properties = Properties(ifc, common);
            Assert.Equal(new[] { "Reference", "Finish" }, properties.Keys);
            Assert.Equal("IFCTEXT('Plaster;Paint;')", properties["Finish"].Expression);
        }
        Assert.Equal(2, Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Finish'").Count);
    }

    [Theory]
    [InlineData("type_parameter", "IFCTEXT('Type finish')")]
    [InlineData("empty_type_parameter", "IFCTEXT('Plaster;')")]
    [InlineData("blank_material_name", "IFCTEXT('Plaster;')")]
    public void CeilingFinishPrecedence(string mutation, string expected)
    {
        Element ceiling = CeilingWithType(8110, 8191, 0);
        Element type = CeilingType(8191, Layer(5, 8211), Layer(4, 8212));
        Element plaster = Material(8211, "Plaster");
        Element other = Material(8212, mutation == "blank_material_name" ? " " : "Paint");
        if (mutation is "type_parameter" or "empty_type_parameter")
            type.Parameters.Add(Text("Finish", mutation == "type_parameter" ? "Type finish" : ""));
        if (mutation == "empty_type_parameter") type.SerializedCeilingTypeLayers = [Layer(5, 8211)];

        string ifc = ExportToText(ceiling, type, plaster, other);

        Assert.Equal(expected,
            Properties(ifc, SolePset(ifc, "Pset_CoveringCommon"))["Finish"].Expression);
    }

    [Theory]
    [InlineData("no_type_link")] [InlineData("type_category")] [InlineData("no_layers")]
    [InlineData("no_finish_layer")] [InlineData("base_material")] [InlineData("unnamed_material")]
    [InlineData("duplicate_material")] [InlineData("name_override")]
    [InlineData("approximate_parameter")] [InlineData("numeric_parameter")]
    [InlineData("empty_occurrence_parameter")] [InlineData("not_a_ceiling")]
    public void UnvalidatedCeilingFinishIsOmitted(string mutation)
    {
        Element ceiling = CeilingWithType(8120, 8192, 0);
        Element type = CeilingType(8192, Layer(1, 8221), Layer(5, 8222));
        Element structure = Material(8221, "Structure");
        Element plaster = Material(8222, "Plaster");
        var extra = new List<Element>();
        switch (mutation)
        {
            case "no_type_link": ceiling.SerializedCeilingTypeId = null; break;
            case "type_category": type.CategoryId = -2000032; break;
            case "no_layers": type.SerializedCeilingTypeLayers = null; break;
            case "no_finish_layer": type.SerializedCeilingTypeLayers = [Layer(1, 8221)]; break;
            case "base_material": type.SerializedCeilingTypeLayers = [Layer(5, -1)]; break;
            case "unnamed_material": plaster.SerializedMaterialName = null; break;
            case "duplicate_material": extra.Add(Material(8222, "Plaster")); break;
            case "name_override": plaster.Parameters.Add(Text("IfcName", "Override")); break;
            case "approximate_parameter":
                Parameter approximate = Text("Finish", "Approximate");
                approximate.IsApproximate = true;
                ceiling.Parameters.Add(approximate);
                break;
            case "numeric_parameter":
                type.Parameters.Add(new Parameter
                {
                    Definition = new Definition { Name = "Finish" },
                    StorageType = StorageType.Double,
                    Value = 1.0,
                });
                break;
            case "empty_occurrence_parameter": ceiling.Parameters.Add(Text("Finish", "")); break;
            case "not_a_ceiling":
                ceiling.CategoryId = -2000032;
                ceiling.Category = "OST_Floors";
                break;
        }

        string ifc = ExportToText([ceiling, type, structure, plaster, .. extra]);

        Assert.DoesNotContain("'Finish'", ifc);
    }

    [Fact]
    public void MaterialName_IsReadFromTheOwnMaterialElemRoot()
    {
        byte[] block = Convert.FromBase64String(PlasterboardMaterialBlock);
        uint dataSize = BitConverter.ToUInt32(block, 8);

        Assert.Equal(198369u, BitConverter.ToUInt32(block, 0));
        Assert.Equal("Finishes - Interior - Plasterboard",
            RvtIO.Parsing.TfBlockDeserializer.ExtractMaterialName(block, 0, dataSize, 2486));
        Assert.Null(RvtIO.Parsing.TfBlockDeserializer.ExtractMaterialName(block, 0, dataSize, 2479));
        byte[] footer = (byte[])block.Clone();
        footer[^1] ^= 0xFF;
        Assert.Null(RvtIO.Parsing.TfBlockDeserializer.ExtractMaterialName(footer, 0, dataSize, 2486));
        Assert.Null(RvtIO.Parsing.TfBlockDeserializer.ExtractMaterialName(
            block[..(block.Length - 8)], 0, dataSize, 2486));
    }

    [Fact]
    public void MaterialName_ReadsPastANonEmptyMaterialPathMap()
    {
        byte[] block = Convert.FromBase64String(SampleTexturedMaterialBlock);

        Assert.Equal(7113548u, BitConverter.ToUInt32(block, 0));
        Assert.Equal("XS_Шпатлевка,грунтовка",
            RvtIO.Parsing.TfBlockDeserializer.ExtractMaterialName(
                block, 0, BitConverter.ToUInt32(block, 8), 2486));
    }

    // Exact MaterialElem block of material 7113548 in sample project A; its m_materialPathMap holds
    // one texture path entry.
    private const string SampleTexturedMaterialBlock =
        "TItsANFw4c4DJQAAtgn/////RwsAAAAA/////0YLAAAAAAAAAAAAAAAAAAAAAP////+NAgEAAABMi2wA/////////////////////////////////////wAAAP////+vCQAAAAABAAAAAAAAABoAAAAzAFwAbQBhAHQAcwBcAGIAcgB1AHMAaABfAGwAbwBuAGcAXwBiAHUAbQBwAC4AcABuAGcAAwAAAAEAAAAAAAAAAAAAAAu58P8KAAAAOpnu/wAAAABDlvD/AAAAAESW8P8AAAAARpbw/wAAAABIlvD/AAAAAEmW8P8TAAAAKAQ/BDAEQgQ7BDUEMgQ6BDAELAAzBEAEQwQ9BEIEPgQyBDoEMAQNufD/AAAAACxMYwABAAAAMQCzTGMAAQAAADIAOk1jAAMAAAAyADYAOQABAAAA/////8MFFgAAAFgAUwBfACgEPwQwBEIEOwQ1BDIEOgQwBCwAMwRABEMEPQRCBD4EMgQ6BDAEAAAAAAAAAAAAAAAAAAAAP/////8AAAAA/////wAAAAD/////AAAAAP////8AAAAAYnOOAEAAAABPi2wA/////wAAAAAAAAAAAAAAAAAAEAAAACgEPwQwBEIEOwQ1BDIEOgQwBCwAMwRABEMEPQRCBD4EAAAAAA0AAABHAGUAbgBlAHIAaQBjAFMAYwBoAGUAbQBhAAAAAAAAAAAAOAAAAP////87AP////87AP////87AP////87AP////8rAP////87AP////87AP////87AP////87AP////82AP////87AP////87AP////87AP////87AP////87AP////8rAP////82AP////8xAP////8rAP////87AP////8rAP////8uAP////8rAP////82AP////8rAP////8uAP////8xAP////8uAP////8xAP////8uAP////8uAP////8rAP////82AP////82AP////8uAP////8uAP////82AP////82AP////8uAP////8uAP////8rAP////8uAP////8uAP////8xAP////8uAP////8uAP////8uAP////87AP////87AP////87AP////82AP////82AP////87AP////87AP////82AP////8vABUAAABhAHMAcwBlAHQAbABpAGIAcgBhAHIAeQBf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AHIAZQB2AGkAcwBpAG8AbgAAAAAAAAAAAAEAAAAGAAAAcwB3AGEAdABjAGgAAAAAAAAAAAALAAAAUwB3AGEAdABjAGgALQBDAHUAYgBlAAkAAAB0AGgAdQBtAGIAbgBhAGkAbAAAAAAAAAAAAD0AAAB0AG0AcAA6AD8ATQBhAHQAZQByAGkAYQBsAFQAaAB1AG0AYgBuAGEAaQBsAHMAXwBQAEkARABfADcAMgAzADAAXAA3ADYAMAA0AGEAMwBmADAAWABTAF8AKARCBEMEOgQwBEIEQwRABDoEMAQoADIAKQAuAHAAbgBnAAcAAAB2AGUAcgBzAGkAbwBuAAAAAAAAAAAABAAAAA0AAABQAGEAdAB0AGUAcgBuAE8AZgBmAHMAZQB0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP////9HC/////9JC/////9GC/////9ICxMAAABVAG4AaQBmAGkAZQBkAEIAaQB0AG0AYQBwAFMAYwBoAGUAbQBhAAAAAAAAAAAAMwAAAP////87AP////87AP////87AP////87AP////8rAP////87AP////87AP////87AP////87AP////82AP////87AP////87AP////87AP////87AP////87AP////82AP////8xAP////8rAP////87AP////87AP////87AP////87AP////82AP////87AP////8rAP////82AP////82AP////82AP////8rAP////8sAP////8sAP////8sAP////8sAP////8rAP////8uAP////8rAP////8uAP////8uAP////8uAP////8rAP////8uAP////8uAP////87AP////87AP////87AP////8uAP////8uAP////82AP////8rAP////8uAP////82ABUAAABhAHMAcwBlAHQAbABpAGIAcgBhAHIAeQBfAGIAYQBzAGUALgBmAGIAeAAAAAAAAAAAAAwAAAAaAAAAAAAAAAAAAABCme7/AAAAAAAAAABGme7/AAAAAAAAAABHme7/AAAAAAAAAABIme7/AAAAAAAAAABJme7/AAAAAAAAAACfme7/AAAAAAAAAACgme7/AAAAAAAAAAChme7/AAAAAAAAAACime7/AAAAAAAAAACjme7/AAAAAAAAAACkme7/AAAAAAAAAAClme7/AAAAAAAAAACmme7/AAAAAAAAAACome7/AAAAAAAAAACpme7/AAAAAAAAAACqme7/AAAAAAAAAACrme7/AAAAAAAAAACsme7/AAAAAAAAAACtme7/AAAAAAAAAACume7/AAAAAAAAAACvme7/AAAAAAAAAACwme7/AAAAAAAAAACxme7/AAAAAAAAAACyme7/AAAAAAAAAACzme7/AAAAAAAAAAC0me7/BAAAAMZq7v8AAAAAAHLu/wAAAACdme7/AAAAAJ6Z7v8AAAAABAAAAO9x7v8AAAAA/3Hu/wAAAAA/me7/AAAAAECZ7v8AAAAAAAAAABQAAABBAGQAdgBhAG4AYwBlAGQAVQBJAEQAZQBmAGkAbgBpAHQAaQBvAG4AAAAAAAAAAAAuAAAATQBhAHAAcwAvAFUAbgBpAGYAaQBlAGQAQgBpAHQAbQBhAHAALwBVAG4AaQBmAGkAZQBkAEIAaQB0AG0AYQBwAEEAZAB2AGEAbgBjAGUAZABVAEkALgB4AG0AbAAKAAAAQQBzAHMAZQB0AEwAaQBiAEkARAAAAAAAAAAAACQAAAA1AEEAMQA1ADMANQA5ADAALQA3ADMAQQBFAC0ANABCAEYAQgAtADkANAAxAEQALQA3ADIAQwA3ADAANgA5AEMANwA2ADUANAAKAAAAQgBhAHMAZQBTAGMAaABlAG0AYQAAAAAAAAAAABMAAABVAG4AaQBmAGkAZQBkAEIAaQB0AG0AYQBwAFMAYwBoAGUAbQBhAAwAAABFAHgAYwBoAGEAbgBnAGUARwBVAEkARAAAAAAAAAAAAAAAAAAGAAAASABpAGQAZABlAG4AAAAAAAAAAAAAFQAAAEkAbQBwAGwAZQBtAGUAbgB0AGEAdABpAG8AbgBHAGUAbgBlAHIAaQBjAAAAAAAAAAAAAAAAABcAAABJAG0AcABsAGUAbQBlAG4AdABhAHQAaQBvAG4ATQBlAG4AdABhAGwAUgBhAHkAAAAAAAAAAAAiAAAATQBhAHAAcwAvAFUAbgBpAGYAaQBlAGQAQgBpAHQAbQBhAHAALwBNAGUAbgB0AGEAbABJAG0AYQBnAGUALgB4AG0AbAARAAAASQBtAHAAbABlAG0AZQBuAHQAYQB0AGkAbwBuAE8ARwBTAAAAAAAAAAAAGgAAAE0AYQBwAHMALwBVAG4AaQBmAGkAZQBkAEIAaQB0AG0AYQBwAC8ATwBHAFMALgB4AG0AbAAVAAAASQBtAHAAbABlAG0AZQBuAHQAYQB0AGkAbwBuAFAAcgBlAHYAaQBlAHcAAAAAAAAAAAAAAAAADQAAAFMAYwBoAGUAbQBhAFYAZQByAHMAaQBvAG4AAAAAAAAAAAAFAAAADAAAAFUASQBEAGUAZgBpAG4AaQB0AGkAbwBuAAAAAAAAAAAAJgAAAE0AYQBwAHMALwBVAG4AaQBmAGkAZQBkAEIAaQB0AG0AYQBwAC8AVQBuAGkAZgBpAGUAZABCAGkAdABtAGEAcABVAEkALgB4AG0AbAAGAAAAVQBJAE4AYQBtAGUAAAAAAAAAAAANAAAAVQBuAGkAZgBpAGUAZABCAGkAdABtAGEAcAALAAAAVgBlAHIAcwBpAG8AbgBHAFUASQBEAAAAAAAAAAAAJAAAAEYAQgA0ADYANgBFAEIAQwAtADgAQwAxADkALQA0ADkANQA1AC0AQgBGADcAQQAtADYAOQBDAEIARQA0ADkAMAAwADcANgAwAAkAAABhAHMAcwBlAHQAdAB5AHAAZQAAAAAAAAAAAAcAAAB0AGUAeAB0AHUAcgBlAAgAAABjAGEAdABlAGcAbwByAHkAAAAAAAAAAAABAAAAOgATAAAAYwBvAG0AbQBvAG4AXwBTAGgAYQByAGUAZABfAEEAcwBzAGUAdAAAAAAAAAAAAAAAAAARAAAAYwBvAG0AbQBvAG4AXwBUAGkAbgB0AF8AYwBvAGwAbwByAAAAAAAAAAAAKVyPwvUo1D8pXI/C9SjUPylcj8L1KNQ/AAAAAAAA8D8SAAAAYwBvAG0AbQBvAG4AXwBUAGkAbgB0AF8AdABvAGcAZwBsAGUAAAAAAAAAAAAACwAAAGQAZQBzAGMAcgBpAHAAdABpAG8AbgAAAAAAAAAAABYAAAAjBD0EOAREBDgERgQ4BEAEPgQyBDAEPQQ9BEsEOQQgAEAEMARBBEIEQAQuAAcAAABrAGUAeQB3AG8AcgBkAAAAAAAAAAAADgAAADoAOgA6BDAEQARCBEsEOgBABDAENwQ9BD4ENQQJAAAAbABvAGMAYQBsAG4AYQBtAGUAAAAAAAAAAAAOAAAAQgBpAHQAbQBhAHAAIABUAGUAeAB0AHUAcgBlAAkAAABsAG8AYwBhAGwAdAB5AHAAZQAAAAAAAAAAAAcAAABUAGUAeAB0AHUAcgBlAAgAAAByAGUAdgBpAHMAaQBvAG4AAAAAAAAAAAABAAAABgAAAHMAdwBhAHQAYwBoAAAAAAAAAAAAAAAAAB0AAAB0AGUAeAB0AHUAcgBlAF8ATABpAG4AawBUAGUAeAB0AHUAcgBlAFQAcgBhAG4AcwBmAG8AcgBtAHMAAAAAAAAAAAAAEgAAAHQAZQB4AHQAdQByAGUAXwBNAGEAcABDAGgAYQBuAG4AZQBsAAAAAAAAAAAAAQAAAB4AAAB0AGUAeAB0AHUAcgBlAF8ATQBhAHAAQwBoAGEAbgBuAGUAbABfAEkARABfAEEAZAB2AGEAbgBjAGUAZAAAAAAAAAAAAAEAAAAlAAAAdABlAHgAdAB1AHIAZQBfAE0AYQBwAEMAaABhAG4AbgBlAGwAXwBVAFYAVwBTAG8AdQByAGMAZQBfAEEAZAB2AGEAbgBjAGUAZAAAAAAAAAAAAAAAAAASAAAAdABlAHgAdAB1AHIAZQBfAE8AZgBmAHMAZQB0AEwAbwBjAGsAAAAAAAAAAAAAGAAAAHQAZQB4AHQAdQByAGUAXwBSAGUAYQBsAFcAbwByAGwAZABPAGYAZgBzAGUAdABYAAAAAAAAAAAAHwAAAGEAdQB0AG8AZABlAHMAawAuAHUAbgBpAHQALgB1AG4AaQB0ADoAaQBuAGMAaABlAHMALQAxAC4AMAAuADEAAAAAAAAAAAAYAAAAdABlAHgAdAB1AHIAZQBfAFIAZQBhAGwAVwBvAHIAbABkAE8AZgBmAHMAZQB0AFkAAAAAAAAAAAAfAAAAYQB1AHQAbwBkAGUAcwBrAC4AdQBuAGkAdAAuAHUAbgBpAHQAOgBpAG4AYwBoAGUAcwAtADEALgAwAC4AMQAAAAAAAAAAABcAAAB0AGUAeAB0AHUAcgBlAF8AUgBlAGEAbABXAG8AcgBsAGQAUwBjAGEAbABlAFgAAAAAAAAAAAAfAAAAYQB1AHQAbwBkAGUAcwBrAC4AdQBuAGkAdAAuAHUAbgBpAHQAOgBpAG4AYwBoAGUAcwAtADEALgAwAC4AMQAAAAAAAIA/QBcAAAB0AGUAeAB0AHUAcgBlAF8AUgBlAGEAbABXAG8AcgBsAGQAUwBjAGEAbABlAFkAAAAAAAAAAAAfAAAAYQB1AHQAbwBkAGUAcwBrAC4AdQBuAGkAdAAuAHUAbgBpAHQAOgBpAG4AYwBoAGUAcwAtADEALgAwAC4AMQDf7/f7/X4/QBEAAAB0AGUAeAB0AHUAcgBlAF8AUwBjAGEAbABlAEwAbwBjAGsAAAAAAAAAAAAADwAAAHQAZQB4AHQAdQByAGUAXwBVAE8AZgBmAHMAZQB0AAAAAAAAAAAAAAAAAAAAAAAPAAAAdABlAHgAdAB1AHIAZQBfAFUAUgBlAHAAZQBhAHQAAAAAAAAAAAABDgAAAHQAZQB4AHQAdQByAGUAXwBVAFMAYwBhAGwAZQAAAAAAAAAAAAAAAAAAAPA/DwAAAHQAZQB4AHQAdQByAGUAXwBVAFYAUwBjAGEAbABlAAAAAAAAAAAAAAAAAAAA8D8PAAAAdABlAHgAdAB1AHIAZQBfAFYATwBmAGYAcwBlAHQAAAAAAAAAAAAAAAAAAAAAAA8AAAB0AGUAeAB0AHUAcgBlAF8AVgBSAGUAcABlAGEAdAAAAAAAAAAAAAEOAAAAdABlAHgAdAB1AHIAZQBfAFYAUwBjAGEAbABlAAAAAAAAAAAAAAAAAAAA8D8OAAAAdABlAHgAdAB1AHIAZQBfAFcAQQBuAGcAbABlAAAAAAAAAAAAAAAAAACAVkAJAAAAdABoAHUAbQBiAG4AYQBpAGwAAAAAAAAAAACyAAAAbABpAGIAOgA/AGEAZABwADoAPwBhAHMAcwBlAHQAPQBcADUAQQAxADUAMwA1ADkAMAAtADcAMwBBAEUALQA0AEIARgBCAC0AOQA0ADEARAAtADcAMgBDADcAMAA2ADkAQwA3ADYANQA0AFwAUgBlAHMAbwB1AHIAYwBlAHMAXAAwAEUANgA1AEEAOAA5ADQALQBCADIARABBAC0ANABBAEIAMgAtAEIAMwBFADQALQBGADQAMgAyADAAMwBBADMAMwAxAEQANwBfAHAAaAB5AHMAbQBhAHQAXwBhAHMAcABlAGMAdABzAF8AMQBfAGcAZQBuAGUAcgBpAGMAXwBkAGkAZgBmAHUAcwBlADoAVQBuAGkAZgBpAGUAZABCAGkAdABtAGEAcABcAHQAaAB1AG0AYgBuAGEAaQBsAFwAMABcAFUAbgBpAGYAaQBlAGQAQgBpAHQAbQBhAHAAXwBwAG4AZwAUAAAAdQBuAGkAZgBpAGUAZABiAGkAdABtAGEAcABfAEIAaQB0AG0AYQBwAAAAAAAAAAAAGgAAADMAXABtAGEAdABzAFwAYgByAHUAcwBoAF8AbABvAG4AZwBfAGIAdQBtAHAALgBwAG4AZwAYAAAAdQBuAGkAZgBpAGUAZABiAGkAdABtAGEAcABfAEIAaQB0AG0AYQBwAF8AdQByAG4AAAAAAAAAAAAAAAAAEgAAAHUAbgBpAGYAaQBlAGQAYgBpAHQAbQBhAHAAXwBCAGwAdQByAAAAAAAAAAAAexSuR+F6hD8ZAAAAdQBuAGkAZgBpAGUAZABiAGkAdABtAGEAcABfAEIAbAB1AHIAXwBPAGYAZgBzAGUAdAAAAAAAAAAAAAAAAAAAAAAAFwAAAHUAbgBpAGYAaQBlAGQAYgBpAHQAbQBhAHAAXwBGAGkAbAB0AGUAcgBpAG4AZwAAAAAAAAAAAAAAAAAUAAAAdQBuAGkAZgBpAGUAZABiAGkAdABtAGEAcABfAEkAbgB2AGUAcgB0AAAAAAAAAAAAABcAAAB1AG4AaQBmAGkAZQBkAGIAaQB0AG0AYQBwAF8AUgBHAEIAQQBtAG8AdQBuAHQAAAAAAAAAAAAAAAAAAADwPwcAAAB2AGUAcgBzAGkAbwBuAAAAAAAAAAAABAAAAAMlAAA=";

    // Exact MaterialElem block of material 198369 in Test_2022.rvt.
    private const string PlasterboardMaterialBlock =
        "4QYDAPhAD2bgEQAAtgn/////RwsAAAAA/////0YLAAAAAAAAAAAAAAAAAAAAAP////+NAgEAAADhBgMA/////////////////////////////////////wAAAP////+vCQAAAAAAAAAAAQAAAAAAAAAAAAAAC7nw/wcAAAA6me7/AwAAAEsAMQAwAEOW8P8AAAAARJbw/wAAAABGlvD/AAAAAEiW8P8AAAAASZbw/yAAAAAxADIALgA1AG0AbQAgAFAAbABhAHMAdABlAHIAYgBvAGEAcgBkACAAZgBpAHgAZQBkACAAdABvACAATwBTAEIADbnw/wAAAAAAAAAAIgAAAEYAaQBuAGkAcwBoAGUAcwAgAC0AIABJAG4AdABlAHIAaQBvAHIAIAAtACAAUABsAGEAcwB0AGUAcgBiAG8AYQByAGQA/////5ALAAAAAAAAAAAAAAA//////wAAAAD/////AAAAAP////8AAAAA/////wAAAAD5+fkAgAAAAOsGAwD/////AAAAAAAAAAAAAAAAAAAKAAAAVQBuAGEAcwBzAGkAZwBuAGUAZAAKAAAAVQBuAGEAcwBzAGkAZwBuAGUAZAAPAAAAVwBhAGwAbABQAGEAaQBuAHQAUwBjAGgAZQBtAGEAAAAAAAAAAAApAAAA/////zsA/////zsA/////zsA/////zsA/////ysA/////zsA/////zsA/////zsA/////zsA/////zYA/////zsA/////zsA/////zsA/////zsA/////zsA/////zYA/////zEA/////ysA/////zsA/////zsA/////zsA/////zsA/////zYA/////zYA/////zsA/////zsA/////zYA/////ysA/////y4A/////ysA/////zYA/////zYA/////zEA/////ysA/////zYA/////zYA/////zYA/////ysA/////ysA/////y4A/////y8AFQAAAGEAcwBzAGUAdABsAGkAYgByAGEAcgB5AF8AYgBhAHMAZQAuAGYAYgB4AAAAAAAAAAAABAAAAAEAAAAt9snt5EieSqu5IshmTAEsAAAAAAEAAAD/////QAsAAAAACAAAAFMAdABhAG4AZABhAHIAZAAAAAAAEgAAAEQAbwB1AGcAbABhAHMAIABGAGkAcgAgACgAVwBl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";

    private static Element CeilingWithType(int id, int typeId, double centreX)
    {
        Element ceiling = Product(id, typeId, -2000038, "OST_Ceilings", "Ceiling", centreX);
        ceiling.SerializedCeilingTypeId = typeId;
        return ceiling;
    }

    private static Element CeilingType(int id, params RvtIO.Internal.RawCompoundStructureLayer[] layers) => new()
    {
        Id = id,
        CategoryId = -2000038,
        Category = "OST_Ceilings",
        Name = "Ceiling type",
        SerializedCeilingTypeLayers = layers,
    };

    private static RvtIO.Internal.RawCompoundStructureLayer Layer(int function, int materialId) =>
        new(0, function, -1, materialId, -1, 0, 1);

    private static Element Material(int id, string name) => new()
    {
        Id = id,
        CategoryId = -2000700,
        Category = "OST_Materials",
        Name = name,
        SerializedMaterialName = name,
    };

    private static Parameter Text(string name, string value) => new()
    {
        Definition = new Definition { Name = name },
        StorageType = StorageType.String,
        Value = value,
    };

    private sealed record Pset(int Id, IReadOnlyList<int> ValueIds);
    private sealed record PropertyValue(int Id, string Expression);

    private static Dictionary<string, PropertyValue> Properties(string ifc, Pset pset)
    {
        var result = new Dictionary<string, PropertyValue>();
        foreach (int valueId in pset.ValueIds)
        {
            Match match = Assert.Single(Regex.Matches(
                ifc,
                $@"#{valueId}=IFCPROPERTYSINGLEVALUE\('((?:''|[^'])*)',(?:\$|''),(.*),\$\);"));
            result.Add(
                match.Groups[1].Value.Replace("''", "'"),
                new PropertyValue(valueId, match.Groups[2].Value));
        }
        return result;
    }

    private static Pset SolePset(string ifc, string psetName) =>
        Assert.Single(Psets(ifc, psetName));

    private static List<Pset> Psets(string ifc, string psetName) =>
        Regex.Matches(
                ifc,
                $@"#(\d+)=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\((#[0-9]+(?:,#[0-9]+)*)\)\);")
            .Select(match => new Pset(
                int.Parse(match.Groups[1].Value),
                match.Groups[2].Value.Split(',')
                    .Select(value => int.Parse(value[1..])).ToList()))
            .ToList();

    private static int SolePsetTarget(string ifc, int psetId)
    {
        Match match = Assert.Single(Regex.Matches(
            ifc,
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#(\d+)\),#{psetId}\);"));
        return int.Parse(match.Groups[1].Value);
    }

    private static int SoleEntityId(string ifc, string ifcType) =>
        Assert.Single(EntityIds(ifc, ifcType));

    private static List<int> EntityIds(string ifc, string ifcType) =>
        Regex.Matches(ifc, $@"#(\d+)={ifcType}\(")
            .Select(match => int.Parse(match.Groups[1].Value)).ToList();

    internal static Element Product(
        int id, int typeId, int categoryId, string category, string? name,
        double centreX = 0) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            SuperInstanceId = 999,
            IsDiscovered = true,
            CategoryId = categoryId,
            Category = category,
            Name = name,
            FamilyName = "Covering-common test family",
            References = [3],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
            ],
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Geometry = SolidPrism(centreX),
        };

    private static GeometryData SolidPrism(double centreX)
    {
        double[][] points =
        [
            [centreX, 0, 0], [centreX + 4, 0, 0],
            [centreX + 4, 3, 0], [centreX, 3, 0],
            [centreX, 0, 2], [centreX + 4, 0, 2],
            [centreX + 4, 3, 2], [centreX, 3, 2],
        ];
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7],
            [0, 1, 5, 4], [1, 2, 6, 5],
            [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        return new GeometryData
        {
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                },
            ],
        };
    }

    private static string ExportToText(params Element[] elements) => ExportToText(new IfcExporter().Profile, elements);

    private static string ExportToText(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"revitreader-covering-common-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
