using System.Globalization;
using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Pset_MemberCommon and Pset_PlateCommon: class-specific property sets attached only to IFCMEMBER and IFCPLATE products respectively; a member never receives Pset_PlateCommon and a plate never receives Pset_MemberCommon.</summary>
public class IfcExporterMemberPlateCommonPropertySetTests
{
    private const int MullionCategoryId = -2000171;       // OST_CurtainWallMullions
    private const int RailingSupportCategoryId = -2000123; // OST_RailingSupport
    private const int CurtainPanelCategoryId = -2000170;   // OST_CurtainWallPanels
    private const int StructuralFramingCategoryId = -2001320; // OST_StructuralFraming -> IFCBEAM

    [Fact]
    public void Mullion_GetsPsetMemberCommonWithReferenceIsExternalAndSpan_NotPlateCommon()
    {
        var mullion = LinearElement(
            id: 300, typeId: 301, name: "Mullion_50x100", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: 10.0);
        var info = new RevitFileInfo();
        info.Elements.Add(mullion);
        string path = TempIfcPath("member-single");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_MemberCommon'"));
            Assert.DoesNotContain("Pset_PlateCommon", ifc);

            int memberId = SoleEntityId(ifc, "IFCMEMBER");
            var (psetId, valueIds) = SolePsetOfName(ifc, "Pset_MemberCommon");
            int relatedId = SoleRelDefinesByPropertiesTarget(ifc, psetId);
            Assert.Equal(memberId, relatedId);

            var props = ResolveProperties(ifc, valueIds);
            // Property list per Pset: exactly IsExternal, Reference, Span - nothing more,
            // nothing missing (mullion source category always carries all three).
            Assert.Equal(
                new[] { "IsExternal", "Reference", "Span" },
                props.Keys.OrderBy(k => k, StringComparer.Ordinal));

            Assert.Equal("IFCBOOLEAN(.F.)", props["IsExternal"]);
            Assert.Equal("IFCIDENTIFIER('Mullion_50x100')", props["Reference"]);

            // Value agreement: Span is CURVE_ELEM_LENGTH (10 ft) converted to mm, not a
            // placeholder. This is the case that lets the suite tell "not built" from
            // "measuring wrong" - a wrong constant would fail this exact assertion.
            var spanMatch = Regex.Match(props["Span"], @"^IFCPOSITIVELENGTHMEASURE\(([^)]+)\)$");
            Assert.True(spanMatch.Success, $"unexpected Span literal: {props["Span"]}");
            double spanMm = double.Parse(spanMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.Equal(10.0 * 304.8, spanMm, precision: 6);
        }
        finally { Cleanup(path); }
    }

    // The legacy profile writes the Span of a mullion with its 15-digit real: a foot is 304.8, not 304.80000000000001.
    [Theory]
    [InlineData(1.0, "304.8")]
    [InlineData(10.0, "3048.")]
    [InlineData(2.5, "762.")]
    public void LegacyMullion_SpanKeepsTheFifteenDigitReal(double curveElemLengthFt, string expected)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(LinearElement(
            id: 300, typeId: 301, name: "Mullion_50x100", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: curveElemLengthFt));
        string path = TempIfcPath("member-legacy-span");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueIds) = SolePsetOfName(ifc, "Pset_MemberCommon");
            Assert.Equal($"IFCPOSITIVELENGTHMEASURE({expected})", ResolveProperties(ifc, valueIds)["Span"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NamelessMullion_KeepsPsetMemberCommonButOmitsOnlyReference_NotTheWholePset()
    {
        var mullion = LinearElement(
            id: 370, typeId: 371, name: null, categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: 10.0);
        var info = new RevitFileInfo();
        info.Elements.Add(mullion);
        string path = TempIfcPath("member-nameless");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_MemberCommon'"));
            var (_, valueIds) = SolePsetOfName(ifc, "Pset_MemberCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(
                new[] { "IsExternal", "Span" },
                props.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("IFCBOOLEAN(.F.)", props["IsExternal"]);

            // The single-property Pset_QuantityTakeOff is omitted and no placeholder text leaks out as an IFCIDENTIFIER literal. The IFCMEMBER Name attribute still falls back to the same "Element {id}" text, so the check is specific to the Pset value shape.
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain($"IFCIDENTIFIER('Element {mullion.Id}')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Plate_GetsPsetPlateCommonWithReferenceAndIsExternal_NotMemberCommon_NoSpan()
    {
        var plate = LinearElement(
            id: 310, typeId: 311, name: "Panel_Glass", categoryId: CurtainPanelCategoryId,
            category: "OST_CurtainWallPanels", centreX: 0, curveElemLengthFt: null);
        var info = new RevitFileInfo();
        info.Elements.Add(plate);
        string path = TempIfcPath("plate-single");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_PlateCommon'"));
            Assert.DoesNotContain("Pset_MemberCommon", ifc);

            int plateId = SoleEntityId(ifc, "IFCPLATE");
            var (psetId, valueIds) = SolePsetOfName(ifc, "Pset_PlateCommon");
            int relatedId = SoleRelDefinesByPropertiesTarget(ifc, psetId);
            Assert.Equal(plateId, relatedId);

            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(
                new[] { "IsExternal", "Reference" },
                props.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("IFCBOOLEAN(.F.)", props["IsExternal"]);
            Assert.Equal("IFCIDENTIFIER('Panel_Glass')", props["Reference"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Mullion_WithApproximateCurveElemLength_OmitsUnvalidatedSpan()
    {
        // An IsApproximate bit alone is not the exact synthetic runtime marker, and this
        // fixture also lacks the serialized transform/host/closed-BRep validation. It must omit
        // Span instead of exporting the bounding-box number.
        var mullion = LinearElement(
            id: 360, typeId: 361, name: "Mullion_50x100", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: 10.0);
        mullion.Parameters.Single(p => p.Id == -1004005).IsApproximate = true;
        var info = new RevitFileInfo();
        info.Elements.Add(mullion);
        string path = TempIfcPath("member-approx-span");
        var diag = new StringWriter();

        try
        {
            new IfcExporter(diag).Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_MemberCommon'"));
            var (_, valueIds) = SolePsetOfName(ifc, "Pset_MemberCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(
                new[] { "IsExternal", "Reference" },
                props.Keys.OrderBy(k => k, StringComparer.Ordinal));
            string diagText = diag.ToString();
            Assert.Contains(
                "IFC Pset_MemberCommon.Span provenance: " +
                "runtime(INSTANCE_LENGTH_PARAM via BRep)=0 " +
                "calculator(explicit zero Span via BRep)=0 " +
                "authored(parameter)=0 total=0",
                diagText);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Mullion_WithExactCurveElemLength_ReportsProvenanceAsExact()
    {
        // Mirror of the test above with the default (exact) provenance, so the diagnostic
        // counters are pinned on both branches, not just the approximate one.
        var mullion = LinearElement(
            id: 362, typeId: 363, name: "Mullion_50x100", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: 10.0);
        var info = new RevitFileInfo();
        info.Elements.Add(mullion);
        string path = TempIfcPath("member-exact-span");
        var diag = new StringWriter();

        try
        {
            new IfcExporter(diag) { Profile = IfcProfile.Legacy }.Export(info, path);
            string diagText = diag.ToString();
            Assert.Contains(
                "IFC Pset_MemberCommon.Span provenance: " +
                "runtime(INSTANCE_LENGTH_PARAM via BRep)=0 " +
                "calculator(explicit zero Span via BRep)=0 " +
                "authored(parameter)=1 total=1",
                diagText);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RailingSupportMember_GetsMemberCommonWithoutSpan()
    {
        var railingSupport = LinearElement(
            id: 320, typeId: 321, name: "Baluster_Round", categoryId: RailingSupportCategoryId,
            category: "OST_RailingSupport", centreX: 0, curveElemLengthFt: 4.0);
        var info = new RevitFileInfo();
        info.Elements.Add(railingSupport);
        string path = TempIfcPath("member-railing-support");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_MemberCommon'"));
            var (_, valueIds) = SolePsetOfName(ifc, "Pset_MemberCommon");
            var props = ResolveProperties(ifc, valueIds);
            Assert.Equal(
                new[] { "IsExternal", "Reference" },
                props.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NonMemberNonPlateElement_GetsNeitherMemberCommonNorPlateCommon()
    {
        var beam = LinearElement(
            id: 330, typeId: 331, name: "BeamType_200x300", categoryId: StructuralFramingCategoryId,
            category: "OST_StructuralFraming", centreX: 0, curveElemLengthFt: 12.0);
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = TempIfcPath("beam-neither");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));
            Assert.DoesNotContain("Pset_MemberCommon", ifc);
            Assert.DoesNotContain("Pset_PlateCommon", ifc);
        }
        finally { Cleanup(path); }
    }

    private static Dictionary<string, string> BraceProperties(
        IfcProfile profile, int? structuralType, double? driveLengthFt, out string ifc,
        int categoryId = StructuralFramingCategoryId, string category = "OST_StructuralFraming")
    {
        var element = LinearElement(
            id: 350, typeId: 351, name: "BraceType_HSS", categoryId: categoryId,
            category: category, centreX: 0, curveElemLengthFt: null);
        element.NativeStructuralType = structuralType;
        element.CurveDriverLineLength = driveLengthFt;
        var info = new RevitFileInfo();
        info.Elements.Add(element);
        string path = TempIfcPath("brace-member");
        try
        {
            new IfcExporter { Profile = profile }.Export(info, path);
            ifc = File.ReadAllText(path);
        }
        finally { Cleanup(path); }
        if (!ifc.Contains("'Pset_MemberCommon'")) return [];
        return ResolveProperties(ifc, SolePsetOfName(ifc, "Pset_MemberCommon").valueIds);
    }

    [Fact]
    public void Brace_IsAMemberWithMemberCommonLoadBearingAndBeamSpan_NotABeam()
    {
        var props = BraceProperties(IfcProfile.Current, structuralType: 2, driveLengthFt: 10.0, out string ifc);

        SoleEntityId(ifc, "IFCMEMBER");
        Assert.DoesNotContain("=IFCBEAM(", ifc);
        Assert.DoesNotContain("Pset_BeamCommon", ifc);
        Assert.Equal(
            new[] { "IsExternal", "LoadBearing", "Reference", "Span" },
            props.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("IFCBOOLEAN(.T.)", props["LoadBearing"]);
        var span = Regex.Match(props["Span"], @"^IFCPOSITIVELENGTHMEASURE\(([^)]+)\)$");
        Assert.True(span.Success, props["Span"]);
        Assert.Equal(10.0 * 304.8, double.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture), precision: 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void NonBraceFraming_StaysABeamWithBeamCommon(int? structuralType)
    {
        BraceProperties(IfcProfile.Current, structuralType, driveLengthFt: 10.0, out string ifc);

        SoleEntityId(ifc, "IFCBEAM");
        Assert.DoesNotContain("=IFCMEMBER(", ifc);
        Assert.DoesNotContain("Pset_MemberCommon", ifc);
        Assert.Contains("'Pset_BeamCommon'", ifc);
    }

    [Fact]
    public void NonStructuralFraming_IsAProxy_NotABeamOrAMember()
    {
        BraceProperties(IfcProfile.Current, structuralType: 0, driveLengthFt: 10.0, out string ifc);

        SoleEntityId(ifc, "IFCBUILDINGELEMENTPROXY");
        Assert.DoesNotContain("=IFCBEAM(", ifc);
        Assert.DoesNotContain("=IFCMEMBER(", ifc);
        Assert.DoesNotContain("Pset_BeamCommon", ifc);
        Assert.DoesNotContain("Pset_MemberCommon", ifc);
    }

    [Fact]
    public void LegacyBrace_IsAMemberWithLoadBearingButNoSpan()
    {
        var props = BraceProperties(IfcProfile.Legacy, structuralType: 2, driveLengthFt: 10.0, out string ifc);

        SoleEntityId(ifc, "IFCMEMBER");
        Assert.Equal(
            new[] { "IsExternal", "LoadBearing", "Reference" },
            props.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void CurrentMullion_GetsNeitherLoadBearingNorSpan()
    {
        var props = BraceProperties(
            IfcProfile.Current, structuralType: 2, driveLengthFt: 10.0, out string ifc,
            MullionCategoryId, "OST_CurtainWallMullions");

        SoleEntityId(ifc, "IFCMEMBER");
        Assert.Equal(
            new[] { "IsExternal", "Reference" },
            props.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void TwoMullionsWithSameTypeNameAndSpan_ShareAllThreePropertySingleValueInstances()
    {
        var mullionA = LinearElement(
            id: 340, typeId: 341, name: "Mullion_50x150", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: 5.0);
        var mullionB = LinearElement(
            id: 342, typeId: 343, name: "Mullion_50x150", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 20, curveElemLengthFt: 5.0);
        var info = new RevitFileInfo();
        info.Elements.Add(mullionA);
        info.Elements.Add(mullionB);
        string path = TempIfcPath("member-shared-values");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal(2, Regex.Matches(ifc, "'Pset_MemberCommon'").Count);
            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('IsExternal'"));
            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Span'"));
            // 'Reference' also backs each element's Pset_QuantityTakeOff attachment with the
            // same text, so this asserts cross-Pset sharing too (see the sixth test below for
            // the direct version of that claim).
            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('Mullion_50x150'\)"));

            var (psetIdA, valuesA) = NthPsetOfName(ifc, "Pset_MemberCommon", 0);
            var (psetIdB, valuesB) = NthPsetOfName(ifc, "Pset_MemberCommon", 1);
            Assert.NotEqual(psetIdA, psetIdB);
            Assert.Equal(valuesA.OrderBy(v => v), valuesB.OrderBy(v => v));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MemberAndPlateWithSameTypeName_ShareReferenceAndIsExternalAcrossPsetNames()
    {
        var mullion = LinearElement(
            id: 350, typeId: 351, name: "SharedTypeName", categoryId: MullionCategoryId,
            category: "OST_CurtainWallMullions", centreX: 0, curveElemLengthFt: null);
        var plate = LinearElement(
            id: 352, typeId: 353, name: "SharedTypeName", categoryId: CurtainPanelCategoryId,
            category: "OST_CurtainWallPanels", centreX: 20, curveElemLengthFt: null);
        var info = new RevitFileInfo();
        info.Elements.Add(mullion);
        info.Elements.Add(plate);
        string path = TempIfcPath("member-plate-cross-pool");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('IsExternal'"));
            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('SharedTypeName'\)"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CurtainComponentIsExternal_ReplaysHostInheritanceThenSharedTypeCache()
    {
        var curtainType = new Element
        {
            Id = 1000,
            CategoryId = -2000011,
            SerializedCurtainWallFunction = 1,
        };
        var sharedPanelType = new Element { Id = 1001, CategoryId = CurtainPanelCategoryId };
        var freshMullionType = new Element { Id = 1002, CategoryId = MullionCategoryId };
        Element firstHost = BareTypedElement(1100, -2000011, curtainType.Id);
        Element secondHost = BareTypedElement(1200, -2000011, curtainType.Id);
        Element firstPanel = BareTypedElement(
            1101, CurtainPanelCategoryId, sharedPanelType.Id, firstHost.Id);
        Element secondPanel = BareTypedElement(
            1201, CurtainPanelCategoryId, sharedPanelType.Id, secondHost.Id);
        Element secondFreshMullion = BareTypedElement(
            1202, MullionCategoryId, freshMullionType.Id, secondHost.Id);
        Element derivedHostOnlyPanel = BareTypedElement(
            1301, CurtainPanelCategoryId, freshMullionType.Id);
        derivedHostOnlyPanel.HostId = secondHost.Id;

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [
                curtainType, sharedPanelType, freshMullionType,
                firstHost, firstPanel,
                secondHost, secondPanel, secondFreshMullion,
                derivedHostOnlyPanel,
            ]);

        Assert.True(values[firstPanel.Id]);
        Assert.False(values[secondPanel.Id]);
        Assert.True(values[secondFreshMullion.Id]);
        Assert.False(values[derivedHostOnlyPanel.Id]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void CurtainComponentIsExternal_FunctionUsesEveryNonInteriorEnumValue(
        int function, bool expected)
    {
        var wallType = new Element
        {
            Id = 3000,
            CategoryId = -2000011,
            SerializedCurtainWallFunction = function,
        };
        var panelType = new Element { Id = 3001, CategoryId = CurtainPanelCategoryId };
        Element host = BareTypedElement(3010, -2000011, wallType.Id);
        Element panel = BareTypedElement(
            3011, CurtainPanelCategoryId, panelType.Id, host.Id);

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [wallType, panelType, host, panel]);

        Assert.Equal(expected, values[panel.Id]);
    }

    [Fact]
    public void CurtainComponentIsExternal_ReadsFunctionOnlyFromResolvedType()
    {
        var wallType = new Element { Id = 3100, CategoryId = -2000011 };
        var panelType = new Element { Id = 3101, CategoryId = CurtainPanelCategoryId };
        Element host = BareTypedElement(3110, -2000011, wallType.Id);
        host.SerializedCurtainWallFunction = 1;
        Element panel = BareTypedElement(
            3111, CurtainPanelCategoryId, panelType.Id, host.Id);

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [wallType, panelType, host, panel]);

        Assert.False(values[panel.Id]);
    }

    [Fact]
    public void CurtainComponentIsExternal_DecodedFunctionParameterOnTypeIsAuthoritative()
    {
        var wallType = new Element { Id = 3200, CategoryId = -2000011 };
        wallType.Parameters.Add(new Parameter
        {
            Id = -1001006,
            Definition = new Definition
            {
                Id = -1001006,
                Name = "FUNCTION_PARAM",
                BuiltInParameter = "FUNCTION_PARAM",
            },
            StorageType = StorageType.Integer,
            Value = 1,
        });
        var panelType = new Element { Id = 3201, CategoryId = CurtainPanelCategoryId };
        Element host = BareTypedElement(3210, -2000011, wallType.Id);
        Element panel = BareTypedElement(
            3211, CurtainPanelCategoryId, panelType.Id, host.Id);

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [wallType, panelType, host, panel]);

        Assert.True(values[panel.Id]);
    }

    [Fact]
    public void CurtainComponentIsExternal_ExplicitInstanceOverridesHostAndCachedType()
    {
        var wallType = new Element
        {
            Id = 3300,
            CategoryId = -2000011,
            SerializedCurtainWallFunction = 1,
        };
        var panelType = new Element { Id = 3301, CategoryId = CurtainPanelCategoryId };
        Element firstHost = BareTypedElement(3310, -2000011, wallType.Id);
        Element secondHost = BareTypedElement(3320, -2000011, wallType.Id);
        Element exteriorButExplicitFalse = BareTypedElement(
            3311, CurtainPanelCategoryId, panelType.Id, firstHost.Id);
        exteriorButExplicitFalse.Parameters.Add(IsExternalParameter(0));
        Element explicitTrueAfterTypeCache = BareTypedElement(
            3321, CurtainPanelCategoryId, panelType.Id, secondHost.Id);
        explicitTrueAfterTypeCache.Parameters.Add(IsExternalParameter(1));

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [
                wallType, panelType,
                firstHost, exteriorButExplicitFalse,
                secondHost, explicitTrueAfterTypeCache,
            ]);

        Assert.False(values[exteriorButExplicitFalse.Id]);
        Assert.True(values[explicitTrueAfterTypeCache.Id]);
    }

    [Theory]
    [InlineData("type", "IsExternal", 1, true)]
    [InlineData("type-suffix", "IsExternal[Type]", 1, true)]
    [InlineData("instance-normalized", "is external", 1, true)]
    [InlineData("instance-normalized", "IS EXTERNAL", 0, false)]
    public void MemberPlateIsExternal_ResolvesExactParameterHierarchy(
        string scope, string name, int value, bool expected)
    {
        var panelType = new Element { Id = 3400, CategoryId = CurtainPanelCategoryId };
        Element panel = BareTypedElement(3401, CurtainPanelCategoryId, panelType.Id);
        Parameter parameter = IsExternalParameter(value, name: name);
        if (scope.StartsWith("type", StringComparison.Ordinal))
            panelType.Parameters.Add(parameter);
        else
            panel.Parameters.Add(parameter);

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
                [panelType, panel], [panel.Id]);

        Assert.Equal(expected, values[panel.Id]);
    }

    [Fact]
    public void MemberPlateIsExternal_ExactNumericCoercionAndDuplicateRulesFailClosed()
    {
        var type = new Element { Id = 3500, CategoryId = CurtainPanelCategoryId };
        Element equalDuplicates = BareTypedElement(
            3501, CurtainPanelCategoryId, type.Id);
        equalDuplicates.Parameters.Add(IsExternalParameter(1));
        equalDuplicates.Parameters.Add(IsExternalParameter(1));

        Element truncatedDouble = BareTypedElement(
            3502, CurtainPanelCategoryId, type.Id);
        truncatedDouble.Parameters.Add(IsExternalParameter(
            1.9, StorageType.Double));

        Element conflictingDuplicates = BareTypedElement(
            3503, CurtainPanelCategoryId, type.Id);
        conflictingDuplicates.Parameters.Add(IsExternalParameter(0));
        conflictingDuplicates.Parameters.Add(IsExternalParameter(1));

        Element plainText = BareTypedElement(
            3504, CurtainPanelCategoryId, type.Id);
        plainText.Parameters.Add(IsExternalParameter(
            "not numeric", StorageType.String));
        Element expression = BareTypedElement(
            3505, CurtainPanelCategoryId, type.Id);
        expression.Parameters.Add(IsExternalParameter("{1}", StorageType.String));

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [
                type, equalDuplicates, truncatedDouble, conflictingDuplicates,
                plainText, expression,
            ],
            [
                equalDuplicates.Id, truncatedDouble.Id, conflictingDuplicates.Id,
                plainText.Id, expression.Id,
            ]);

        Assert.True(values[equalDuplicates.Id]);
        Assert.True(values[truncatedDouble.Id]);
        Assert.False(values[conflictingDuplicates.Id]);
        Assert.False(values[plainText.Id]);
        Assert.False(values[expression.Id]);
    }

    [Fact]
    public void CurtainComponentIsExternal_UsesExactRoofMassAndArbitraryHostChain()
    {
        var roofPanelType = new Element { Id = 3600, CategoryId = CurtainPanelCategoryId };
        var exteriorMassPanelType = new Element { Id = 3620, CategoryId = CurtainPanelCategoryId };
        var interiorMassPanelType = new Element { Id = 3630, CategoryId = CurtainPanelCategoryId };
        var chainedPanelType = new Element { Id = 3640, CategoryId = CurtainPanelCategoryId };
        var roof = new Element { Id = 3601, CategoryId = -2000035 };
        var massExterior = new Element { Id = 3602, CategoryId = -2003413 };
        var massInterior = new Element { Id = 3603, CategoryId = -2003412 };
        var arbitraryHost = new Element
        {
            Id = 3604,
            CategoryId = -2000151,
            SerializedHostId = roof.Id,
        };
        Element roofPanel = BareTypedElement(
            3611, CurtainPanelCategoryId, roofPanelType.Id, roof.Id);
        Element exteriorMassPanel = BareTypedElement(
            3612, CurtainPanelCategoryId, exteriorMassPanelType.Id, massExterior.Id);
        Element interiorMassPanel = BareTypedElement(
            3613, CurtainPanelCategoryId, interiorMassPanelType.Id, massInterior.Id);
        Element chainedPanel = BareTypedElement(
            3614, CurtainPanelCategoryId, chainedPanelType.Id, arbitraryHost.Id);

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [
                roofPanelType, exteriorMassPanelType, interiorMassPanelType,
                chainedPanelType, roof, massExterior, massInterior, arbitraryHost,
                roofPanel, exteriorMassPanel, interiorMassPanel, chainedPanel,
            ],
            [roofPanel.Id, exteriorMassPanel.Id, interiorMassPanel.Id, chainedPanel.Id]);

        Assert.True(values[roofPanel.Id]);
        Assert.True(values[exteriorMassPanel.Id]);
        Assert.False(values[interiorMassPanel.Id]);
        Assert.True(values[chainedPanel.Id]);
    }

    [Fact]
    public void CurtainComponentIsExternal_MissingTypeFailsClosedBeforeExteriorHost()
    {
        var exteriorType = new Element
        {
            Id = 3700,
            CategoryId = -2000011,
            SerializedCurtainWallFunction = 1,
        };
        Element host = BareTypedElement(3710, -2000011, exteriorType.Id);
        Element panel = BareTypedElement(
            3711, CurtainPanelCategoryId, typeId: 999999,
            serializedHostId: host.Id);

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
            [exteriorType, host, panel]);

        Assert.False(values[panel.Id]);
    }

    [Fact]
    public void MemberPlateIsExternal_TypelessOccurrenceIgnoresTypeSuffixParameter()
    {
        var panel = new Element
        {
            Id = 3720,
            CategoryId = CurtainPanelCategoryId,
            Parameters = [IsExternalParameter(1, name: "IsExternal[Type]")],
        };

        IReadOnlyDictionary<int, bool> values =
            IfcExporter.ResolveMemberPlateIsExternalValues(
                [panel], [panel.Id]);

        Assert.False(values[panel.Id]);
    }

    [Fact]
    public void Export_UsesCurtainComponentIsExternalCacheValuesInPlatePsets()
    {
        var curtainType = new Element
        {
            Id = 2000,
            CategoryId = -2000011,
            SerializedCurtainWallFunction = 1,
        };
        var panelType = new Element { Id = 2001, CategoryId = CurtainPanelCategoryId };
        Element firstHost = CurtainHost(2100, curtainType.Id, centreX: 0);
        Element secondHost = CurtainHost(2200, curtainType.Id, centreX: 40);
        Element firstPanel = LinearElement(
            2101, panelType.Id, "Panel", CurtainPanelCategoryId,
            "OST_CurtainWallPanels", centreX: 5, curveElemLengthFt: null);
        Element secondPanel = LinearElement(
            2201, panelType.Id, "Panel", CurtainPanelCategoryId,
            "OST_CurtainWallPanels", centreX: 45, curveElemLengthFt: null);
        foreach ((Element panel, Element host) in new[]
                 {
                     (firstPanel, firstHost), (secondPanel, secondHost),
                 })
        {
            panel.Parameters.Add(TypeLink(panelType.Id));
            panel.SerializedHostId = host.Id;
            panel.HostId = host.Id;
            panel.References!.Add(host.Id);
        }

        var info = new RevitFileInfo();
        info.Elements.AddRange(
        [
            curtainType, panelType,
            firstHost, firstPanel,
            secondHost, secondPanel,
        ]);
        string path = TempIfcPath("plate-isexternal-cache-order");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var firstProperties = ResolveProperties(
                ifc, NthPsetOfName(ifc, "Pset_PlateCommon", 0).valueIds);
            var secondProperties = ResolveProperties(
                ifc, NthPsetOfName(ifc, "Pset_PlateCommon", 1).valueIds);
            Assert.Equal("IFCBOOLEAN(.T.)", firstProperties["IsExternal"]);
            Assert.Equal("IFCBOOLEAN(.F.)", secondProperties["IsExternal"]);
            Assert.Single(Regex.Matches(
                ifc, @"IFCPROPERTYSINGLEVALUE\('IsExternal',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));
            Assert.Single(Regex.Matches(
                ifc, @"IFCPROPERTYSINGLEVALUE\('IsExternal',(?:\$|''),IFCBOOLEAN\(\.F\.\),\$\)"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CurtainComponentIsExternal_UnexportedHostStillWrapsItsPanelsInOrder()
    {
        var curtainType = new Element { Id = 5000, CategoryId = -2000011, SerializedCurtainWallFunction = 1 };
        var panelType = new Element { Id = 5001, CategoryId = CurtainPanelCategoryId };
        var roof = new Element { Id = 5100, CategoryId = -2000035 };
        Element wall = BareTypedElement(5200, -2000011, curtainType.Id);
        Element roofPanel = BareTypedElement(5101, CurtainPanelCategoryId, panelType.Id, roof.Id);
        Element wallPanel = BareTypedElement(5201, CurtainPanelCategoryId, panelType.Id, wall.Id);

        IReadOnlyDictionary<int, bool> values = IfcExporter.ResolveMemberPlateIsExternalValues(
            [curtainType, panelType, roof, roofPanel, wall, wallPanel], [roofPanel.Id, wallPanel.Id]);

        Assert.True(values[roofPanel.Id]);
        Assert.False(values[wallPanel.Id]);
    }

    [Fact]
    public void CurtainComponentIsExternal_WrapsHostsInElementIdOrderNotRecordOrder()
    {
        var curtainType = new Element { Id = 7000, CategoryId = -2000011, SerializedCurtainWallFunction = 1 };
        var panelType = new Element { Id = 7001, CategoryId = CurtainPanelCategoryId };
        var mullionType = new Element { Id = 7002, CategoryId = MullionCategoryId };
        Element wall = BareTypedElement(7100, -2000011, curtainType.Id);
        var firstRoof = new Element { Id = 7200, CategoryId = -2000035 };
        var lastRoof = new Element { Id = 7300, CategoryId = -2000035 };
        Element wallMullion = BareTypedElement(7101, MullionCategoryId, mullionType.Id, wall.Id);
        Element firstRoofPanel = BareTypedElement(7201, CurtainPanelCategoryId, panelType.Id, firstRoof.Id);
        Element firstRoofMullion = BareTypedElement(7202, MullionCategoryId, mullionType.Id, firstRoof.Id);
        Element lastRoofPanel = BareTypedElement(7301, CurtainPanelCategoryId, panelType.Id, lastRoof.Id);
        Element lastRoofMullion = BareTypedElement(7302, MullionCategoryId, mullionType.Id, lastRoof.Id);

        IReadOnlyDictionary<int, bool> values = IfcExporter.ResolveMemberPlateIsExternalValues(
            [curtainType, panelType, mullionType, lastRoof, lastRoofPanel, lastRoofMullion,
                firstRoof, firstRoofPanel, firstRoofMullion, wall, wallMullion]);

        Assert.True(values[wallMullion.Id]);
        Assert.True(values[firstRoofPanel.Id]);
        Assert.False(values[firstRoofMullion.Id]);
        Assert.False(values[lastRoofPanel.Id]);
        Assert.False(values[lastRoofMullion.Id]);
    }

    [Fact]
    public void Export_PanelsOfAnUnwrittenRoofHostComeFirstForTheirSymbol()
    {
        var curtainType = new Element { Id = 6000, CategoryId = -2000011, SerializedCurtainWallFunction = 1 };
        var panelType = new Element { Id = 6001, CategoryId = CurtainPanelCategoryId };
        // A roof without geometry is not written, but Revit still exports its panels first.
        var roof = new Element { Id = 6100, CategoryId = -2000035, Category = "OST_Roofs" };
        Element wall = CurtainHost(6200, curtainType.Id, centreX: 40);
        Element roofPanel = LinearElement(
            6101, panelType.Id, "Roof panel", CurtainPanelCategoryId,
            "OST_CurtainWallPanels", centreX: 5, curveElemLengthFt: null);
        Element wallPanel = LinearElement(
            6201, panelType.Id, "Wall panel", CurtainPanelCategoryId,
            "OST_CurtainWallPanels", centreX: 45, curveElemLengthFt: null);
        foreach ((Element panel, Element host) in new[] { (roofPanel, roof), (wallPanel, wall) })
        {
            panel.Parameters.Add(TypeLink(panelType.Id));
            panel.SerializedHostId = host.Id;
            panel.HostId = host.Id;
            panel.References!.Add(host.Id);
        }

        var info = new RevitFileInfo();
        info.Elements.AddRange([curtainType, panelType, roof, roofPanel, wall, wallPanel]);
        string path = TempIfcPath("plate-isexternal-unwritten-roof");
        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.DoesNotContain("IFCROOF(", ifc);
            Assert.Equal("IFCBOOLEAN(.T.)", ResolveProperties(
                ifc, NthPsetOfName(ifc, "Pset_PlateCommon", 0).valueIds)["IsExternal"]);
            Assert.Equal("IFCBOOLEAN(.F.)", ResolveProperties(
                ifc, NthPsetOfName(ifc, "Pset_PlateCommon", 1).valueIds)["IsExternal"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Export_SkippedCurtainChildDoesNotPoisonLaterSharedType()
    {
        var curtainType = new Element
        {
            Id = 4000,
            CategoryId = -2000011,
            SerializedCurtainWallFunction = 1,
        };
        var panelType = new Element { Id = 4001, CategoryId = CurtainPanelCategoryId };
        Element firstHost = CurtainHost(4100, curtainType.Id, centreX: 0);
        Element secondHost = CurtainHost(4200, curtainType.Id, centreX: 40);
        Element skippedPanel = LinearElement(
            4101, panelType.Id, "Skipped panel", CurtainPanelCategoryId,
            "OST_CurtainWallPanels", centreX: 5, curveElemLengthFt: null);
        Element exportedPanel = LinearElement(
            4201, panelType.Id, "Exported panel", CurtainPanelCategoryId,
            "OST_CurtainWallPanels", centreX: 45, curveElemLengthFt: null);
        foreach ((Element panel, Element host) in new[]
                 {
                     (skippedPanel, firstHost), (exportedPanel, secondHost),
                 })
        {
            panel.Parameters.Add(TypeLink(panelType.Id));
            panel.SerializedHostId = host.Id;
            panel.References!.Add(host.Id);
        }
        skippedPanel.BoundingBox = null;
        skippedPanel.Geometry = null;

        var info = new RevitFileInfo();
        info.Elements.AddRange(
        [
            curtainType, panelType,
            firstHost, skippedPanel,
            secondHost, exportedPanel,
        ]);
        string path = TempIfcPath("plate-isexternal-skipped-child");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);
            var properties = ResolveProperties(
                ifc, NthPsetOfName(ifc, "Pset_PlateCommon", 0).valueIds);
            Assert.Equal("IFCBOOLEAN(.T.)", properties["IsExternal"]);
            // The independently evaluated type has its own inline set, not the host value.
            Assert.Equal(2, AllPsetsOfName(ifc, "Pset_PlateCommon").Count);
            Assert.Equal("IFCBOOLEAN(.F.)", ResolveProperties(
                ifc, NthPsetOfName(ifc, "Pset_PlateCommon", 1).valueIds)["IsExternal"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Export_NonCurtainMemberHonoursExactIsExternalParameter()
    {
        Element member = LinearElement(
            4301, 4302, "Railing support", RailingSupportCategoryId,
            "OST_RailingSupport", centreX: 0, curveElemLengthFt: null);
        member.Parameters.Add(IsExternalParameter(1));
        var info = new RevitFileInfo();
        info.Elements.Add(member);
        string path = TempIfcPath("member-explicit-isexternal");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);
            var properties = ResolveProperties(
                ifc, SolePsetOfName(ifc, "Pset_MemberCommon").valueIds);
            Assert.Equal("IFCBOOLEAN(.T.)", properties["IsExternal"]);
        }
        finally { Cleanup(path); }
    }

    private static Element BareTypedElement(
        int id, int categoryId, int typeId, int? serializedHostId = null) =>
        new()
        {
            Id = id,
            CategoryId = categoryId,
            SerializedHostId = serializedHostId,
            Parameters = [TypeLink(typeId)],
        };

    private static Element CurtainHost(int id, int typeId, double centreX)
    {
        Element host = LinearElement(
            id, typeId, "Curtain wall", -2000011, "OST_Walls",
            centreX, curveElemLengthFt: null);
        host.Parameters.Add(TypeLink(typeId));
        return host;
    }

    private static Parameter TypeLink(int typeId) => new()
    {
        Id = -1002050,
        Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId,
        Value = typeId,
    };

    private static Parameter IsExternalParameter(
        object value, StorageType storageType = StorageType.Integer,
        string name = "IsExternal") => new()
    {
        Id = 900001,
        Definition = new Definition { Id = 900001, Name = name },
        StorageType = storageType,
        Value = value,
    };

    private static Element LinearElement(
        int id, int typeId, string? name, int categoryId, string category, double centreX,
        double? curveElemLengthFt)
    {
        double[] axis = [0, 1, 0];
        double[] profileX = [1, 0, 0];
        double[] centre = [centreX, 5, 4];
        var parameters = new List<Parameter>
        {
            // PHASE_CREATED - a placed instance must reference its own creation phase to
            // pass the exporter's instance-vs-type-preview filter (see
            // HasProjectPhaseReference in IfcExporter.Export).
            new() { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
        };
        if (curveElemLengthFt is double len)
            parameters.Add(new Parameter
            {
                Id = -1004005, // CURVE_ELEM_LENGTH
                StorageType = StorageType.Double,
                Value = len,
            });
        return new Element
        {
            Id = id,
            TypeId = typeId,
            IsDiscovered = true,
            CategoryId = categoryId,
            Category = category,
            Name = name,
            References = [3],
            Parameters = parameters,
            BoundingBox = PrismBounds(centre, axis, profileX, length: 10, width: 0.1, depth: 0.1),
            Geometry = RectangularPrism(centre, axis, profileX, length: 9.2, width: 0.1, depth: 0.1),
        };
    }

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static int SoleEntityId(string ifc, string ifcType)
    {
        var match = Assert.Single(Regex.Matches(ifc, $@"#(\d+)={ifcType}\("));
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>Finds the single "#id=IFCPROPERTYSET('...',#hist,'psetName',$,(#a,#b,...));"
    /// entity of the given Pset name and returns (its own entity id, the list of value entity
    /// ids it references, in file order).</summary>
    private static (int psetId, List<int> valueIds) SolePsetOfName(string ifc, string psetName)
    {
        var all = AllPsetsOfName(ifc, psetName);
        return Assert.Single(all);
    }

    private static (int psetId, List<int> valueIds) NthPsetOfName(string ifc, string psetName, int index) =>
        AllPsetsOfName(ifc, psetName)[index];

    private static List<(int psetId, List<int> valueIds)> AllPsetsOfName(string ifc, string psetName)
    {
        var pattern = new Regex(
            $@"#(\d+)=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\(([^)]*)\)\);");
        var matches = pattern.Matches(ifc);
        Assert.True(matches.Count > 0, $"expected at least one {psetName} IFCPROPERTYSET");
        return matches.Select(m => (
            int.Parse(m.Groups[1].Value),
            Regex.Matches(m.Groups[2].Value, @"#(\d+)")
                .Select(vm => int.Parse(vm.Groups[1].Value)).ToList()
        )).ToList();
    }

    private static int SoleRelDefinesByPropertiesTarget(string ifc, int psetId)
    {
        var pattern = new Regex(
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#(\d+)\),#{psetId}\);");
        var match = Assert.Single(pattern.Matches(ifc));
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>Resolves each value entity id to its IfcPropertySingleValue and returns a map
    /// of property name -> the "TYPE(literal)" text of its NominalValue.</summary>
    private static Dictionary<string, string> ResolveProperties(string ifc, List<int> valueIds)
    {
        var result = new Dictionary<string, string>();
        foreach (int id in valueIds)
        {
            var pattern = new Regex($@"#{id}=IFCPROPERTYSINGLEVALUE\('([^']*)',(?:\$|''),([^,]+(?:\([^)]*\))?),\$\);");
            var match = Assert.Single(pattern.Matches(ifc));
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return result;
    }

    private static GeometryData RectangularPrism(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        double[][] points = PrismPoints(centre, axis, profileX, profileY, length, width, depth);
        int[][] faceIndices =
        [
            [0, 1, 3, 2],
            [4, 6, 7, 5],
            [0, 4, 5, 1],
            [2, 3, 7, 6],
            [0, 2, 6, 4],
            [1, 5, 7, 3],
        ];
        return new GeometryData
        {
            BoundingBox = Bounds(points),
            Faces = faceIndices.Select(indices => new GeoFace
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

    private static double[] PrismBounds(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        return Bounds(PrismPoints(centre, axis, profileX, profileY, length, width, depth));
    }

    private static double[][] PrismPoints(
        double[] centre, double[] axis, double[] profileX, double[] profileY,
        double length, double width, double depth)
    {
        var points = new double[8][];
        int index = 0;
        foreach (int alongSign in new[] { -1, 1 })
            foreach (int widthSign in new[] { -1, 1 })
                foreach (int depthSign in new[] { -1, 1 })
                    points[index++] =
                    [
                        centre[0] + axis[0] * length / 2 * alongSign
                            + profileX[0] * width / 2 * widthSign
                            + profileY[0] * depth / 2 * depthSign,
                        centre[1] + axis[1] * length / 2 * alongSign
                            + profileX[1] * width / 2 * widthSign
                            + profileY[1] * depth / 2 * depthSign,
                        centre[2] + axis[2] * length / 2 * alongSign
                            + profileX[2] * width / 2 * widthSign
                            + profileY[2] * depth / 2 * depthSign,
                    ];
        return points;
    }

    private static double[] Bounds(IEnumerable<double[]> points)
    {
        var list = points.ToList();
        return
        [
            list.Min(point => point[0]),
            list.Min(point => point[1]),
            list.Min(point => point[2]),
            list.Max(point => point[0]),
            list.Max(point => point[1]),
            list.Max(point => point[2]),
        ];
    }

    private static double[] Cross(double[] first, double[] second) =>
    [
        first[1] * second[2] - first[2] * second[1],
        first[2] * second[0] - first[0] * second[2],
        first[0] * second[1] - first[1] * second[0],
    ];
}
