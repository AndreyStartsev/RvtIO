using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Pset_WallCommon and Pset_ReinforcementBarPitchOfWall. Both are attached to every successfully tagged wall product
/// (IFCWALL/IFCWALLSTANDARDCASE) that already emitted Pset_QuantityTakeOff; the entity backing the QTO Reference property is reused
/// verbatim, never re-pooled, as for Pset_ColumnCommon.
/// LoadBearing comes from the instance parameter BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM ("Structural Usage", id -1001119).
/// IsExternal requires the canonical basic wall type's own serialized function; absent or conflicting source omits the property.
/// ExtendToStructure follows a named occurrence parameter, else the wall's own serialized top attachment (JoinToRoofGStep).
/// </summary>
public class IfcExporterWallCommonPropertySetTests
{
    private const int PhaseId = 3;
    private const int WallStructuralUsageParamId = -1001119;

    [Fact]
    public void BearingWall_GetsPsetWallCommonWithReferenceAndLoadBearingTrue_AndReinforcementBarPitch()
    {
        var wall = Wall(id: 100, typeId: 101, name: "Wall_Concrete_200", structuralUsage: 1);
        var info = new RevitFileInfo();
        info.Elements.Add(Level());
        info.Elements.Add(wall);
        string path = TempIfcPath("wallcommon-bearing");

        try
        {
            // The legacy profile contract: the current profile also writes ExtendToStructure for a wall whose join step is not decoded.
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_WallCommon'"));
            Assert.Single(Regex.Matches(ifc, "'Pset_ReinforcementBarPitchOfWall'"));

            int wallId = SoleEntityId(ifc, "IFCWALLSTANDARDCASE");

            var (wcPsetId, wcValueIds) = SolePsetOfName(ifc, "Pset_WallCommon");
            Assert.Equal(wallId, SoleRelDefinesByPropertiesTarget(ifc, wcPsetId));
            var wcProps = ResolveProperties(ifc, wcValueIds);
            // No canonical type source: only Reference and LoadBearing are validated.
            Assert.Equal(
                new[] { "LoadBearing", "Reference" },
                wcProps.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("IFCIDENTIFIER('Wall_Concrete_200')", wcProps["Reference"]);
            Assert.Equal("IFCBOOLEAN(.T.)", wcProps["LoadBearing"]);

            var (rbpPsetId, rbpValueIds) = SolePsetOfName(ifc, "Pset_ReinforcementBarPitchOfWall");
            Assert.Equal(wallId, SoleRelDefinesByPropertiesTarget(ifc, rbpPsetId));
            var rbpProps = ResolveProperties(ifc, rbpValueIds);
            Assert.Equal(new[] { "Reference" }, rbpProps.Keys);
            Assert.Equal("IFCLABEL('Wall_Concrete_200')", rbpProps["Reference"]);

            Assert.DoesNotContain("IsExternal", ifc);
            Assert.DoesNotContain("ExtendToStructure", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NonBearingWall_GetsLoadBearingFalse()
    {
        var wall = Wall(id: 110, typeId: 111, name: "Wall_Concrete_100", structuralUsage: 0);
        var info = new RevitFileInfo();
        info.Elements.Add(Level());
        info.Elements.Add(wall);
        string path = TempIfcPath("wallcommon-nonbearing");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, wcValueIds) = SolePsetOfName(ifc, "Pset_WallCommon");
            var wcProps = ResolveProperties(ifc, wcValueIds);
            Assert.Equal("IFCBOOLEAN(.F.)", wcProps["LoadBearing"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void WallWithNoStructuralUsageParameter_OmitsOnlyLoadBearing_KeepsReference()
    {
        // Absent source must never default to a constant: the property list shrinks, the Pset itself and its Reference survive intact.
        var wall = Wall(id: 120, typeId: 121, name: "Wall_Concrete_300", structuralUsage: null);
        var info = new RevitFileInfo();
        info.Elements.Add(Level());
        info.Elements.Add(wall);
        string path = TempIfcPath("wallcommon-no-structural-usage");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, wcValueIds) = SolePsetOfName(ifc, "Pset_WallCommon");
            var wcProps = ResolveProperties(ifc, wcValueIds);
            Assert.Equal(new[] { "Reference" }, wcProps.Keys);
            Assert.Equal("IFCIDENTIFIER('Wall_Concrete_300')", wcProps["Reference"]);

            // Pset_ReinforcementBarPitchOfWall is unaffected by LoadBearing source; it only depends on Reference, which is present here.
            Assert.Single(Regex.Matches(ifc, "'Pset_ReinforcementBarPitchOfWall'"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void WallWithApproximateStructuralUsage_OmitsLoadBearing()
    {
        // An IsApproximate bit means the direct lookup existed but cannot be reproduced exactly: unlike genuine absence, but it must also omit
        // the property rather than guess.
        var wall = Wall(id: 130, typeId: 131, name: "Wall_Concrete_400", structuralUsage: 1);
        wall.Parameters.Single(p => p.Definition?.Id == WallStructuralUsageParamId)
            .IsApproximate = true;
        var info = new RevitFileInfo();
        info.Elements.Add(Level());
        info.Elements.Add(wall);
        string path = TempIfcPath("wallcommon-approximate-structural-usage");

        try
        {
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, wcValueIds) = SolePsetOfName(ifc, "Pset_WallCommon");
            var wcProps = ResolveProperties(ifc, wcValueIds);
            Assert.Equal(new[] { "Reference" }, wcProps.Keys);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NamelessWall_GetsNoWallCommonOrReinforcementBarPitch_NoPlaceholder()
    {
        // Unlike Pset_MemberCommon/Pset_PlateCommon (attached independently of QTO success), Pset_WallCommon/Pset_ReinforcementBarPitchOfWall
        // sit in the same QTO-gated block as Pset_ColumnCommon: no QTO Reference means no wall Pset at all.
        var wall = Wall(id: 140, typeId: 141, name: null, structuralUsage: 1);
        var info = new RevitFileInfo();
        info.Elements.Add(Level());
        info.Elements.Add(wall);
        string path = TempIfcPath("wallcommon-nameless");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain("Pset_WallCommon", ifc);
            Assert.DoesNotContain("Pset_ReinforcementBarPitchOfWall", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TwoWallsSharingLoadBearingValue_ReuseSamePooledPropertyEntity()
    {
        // LoadBearing is pooled like every other boolean property in this exporter: two walls with the same Structural Usage must
        // reference the identical IfcPropertySingleValue entity.
        var wallA = Wall(id: 150, typeId: 151, name: "Type A", structuralUsage: 1);
        var wallB = Wall(id: 160, typeId: 161, name: "Type B", structuralUsage: 1, offsetX: 20);
        foreach (var wall in new[] { wallA, wallB }) // the current profile also needs the Structural box
            wall.Parameters.Add(new Parameter { Id = -1001596, Definition = new Definition { Id = -1001596, Name = "WALL_STRUCTURAL_SIGNIFICANT" },
                StorageType = StorageType.Integer, Value = 1 });
        var info = new RevitFileInfo();
        info.Elements.Add(Level());
        info.Elements.AddRange([wallA, wallB]);
        string path = TempIfcPath("wallcommon-pooled");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var psets = AllPsetsOfName(ifc, "Pset_WallCommon");
            Assert.Equal(2, psets.Count);
            var propsA = ResolveProperties(ifc, psets[0].valueIds);
            var propsB = ResolveProperties(ifc, psets[1].valueIds);

            // Same literal id embedded in both value lists (pooled, not re-created).
            int loadBearingIdA = psets[0].valueIds[
                Array.IndexOf([.. propsA.Keys], "LoadBearing")];
            int loadBearingIdB = psets[1].valueIds[
                Array.IndexOf([.. propsB.Keys], "LoadBearing")];
            Assert.Equal(loadBearingIdA, loadBearingIdB);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Wall_OwnCanonicalBasicTypeFunctionProducesIsExternal(int function)
    {
        var wall = Wall(100, 999, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall",
            SerializedBasicWallTypeFunction = function };
        string ifc = ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.Equal(function == 0 ? "IFCBOOLEAN(.F.)" : "IFCBOOLEAN(.T.)", values["IsExternal"]);
        Assert.Equal("IFCBOOLEAN(.T.)", values["LoadBearing"]);
        Assert.Equal("IFCIDENTIFIER('Wall')", values["Reference"]);
        Assert.DoesNotContain("ExtendToStructure", values.Keys);
    }

    [Theory]
    [InlineData("missing_type")] [InlineData("missing_link")] [InlineData("missing_validation")]
    [InlineData("wrong_category")] [InlineData("function_range")]
    [InlineData("occurrence_override")] [InlineData("type_override")]
    [InlineData("type_suffix_override")] [InlineData("conflict")]
    [InlineData("approximate")] [InlineData("wrong_storage")]
    public void Wall_IncompleteOrConflictingFunctionOmitsIsExternal(string mutation)
    {
        var wall = Wall(100, 500, "Wall", 1);
        // Generic TypeId is not a substitute for the canonical ELEM_TYPE_PARAM link.
        if (mutation != "missing_link") wall.Parameters.Add(TypeParameter(500));
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall",
            SerializedBasicWallTypeFunction = 1 };
        if (mutation == "wrong_category") type.CategoryId = -2000032;
        if (mutation == "missing_validation") type.SerializedBasicWallTypeFunction = null;
        if (mutation == "function_range") type.SerializedBasicWallTypeFunction = 6;
        if (mutation is "occurrence_override" or "type_override" or "type_suffix_override")
            (mutation == "occurrence_override" ? wall : type).Parameters.Add(new Parameter
            {
                Definition = new Definition { Name = mutation == "type_suffix_override" ? "IsExternal[Type]" : "IsExternal" },
                StorageType = StorageType.Integer, Value = 0,
            });
        if (mutation is "conflict" or "approximate" or "wrong_storage")
            type.Parameters.Add(new Parameter { Id = -1001006,
                StorageType = mutation == "wrong_storage" ? StorageType.String : StorageType.Integer,
                Value = mutation == "wrong_storage" ? "1" : mutation == "conflict" ? 0 : 1,
                IsApproximate = mutation == "approximate" });
        string ifc = mutation == "missing_type" ? ExportWalls(wall) : ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.DoesNotContain("IsExternal", values.Keys);
        Assert.Equal("IFCBOOLEAN(.T.)", values["LoadBearing"]);
    }

    [Theory]
    [InlineData("IsExternal")]
    [InlineData("Pset_WallCommon.IsExternal")]
    public void Wall_NamedIsExternalBlocksTheTypeFunctionValue(string name)
    {
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall",
            SerializedBasicWallTypeFunction = 1 };
        var wall = Wall(100, 500, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        wall.Parameters.Add(new Parameter { Definition = new Definition { Name = name },
            StorageType = StorageType.Integer, Value = 0 });
        string ifc = ExportWalls(type, wall);
        Assert.DoesNotContain("IsExternal", OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE")).Keys);
    }

    [Fact]
    public void Wall_OccurrenceOverrideDoesNotContaminateSiblingOrTypeValuePool()
    {
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall",
            SerializedBasicWallTypeFunction = 1 };
        var first = Wall(100, 500, "First", 1);
        var second = Wall(200, 500, "Second", 1, offsetX: 20);
        first.Parameters.Add(TypeParameter(500)); second.Parameters.Add(TypeParameter(500));
        first.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.Integer, Value = 0 });
        string ifc = ExportWalls(type, first, second);
        var walls = Regex.Matches(ifc, @"#(\d+)=IFCWALLSTANDARDCASE\(").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(2, walls.Length);
        Assert.DoesNotContain("IsExternal", OccurrenceCommonValues(ifc, walls[0]).Keys);
        Assert.Equal("IFCBOOLEAN(.T.)", OccurrenceCommonValues(ifc, walls[1])["IsExternal"]);
        Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('IsExternal',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));
    }

    [Fact]
    public void Wall_GlobalIdIsTheExportIdOnlyForTheFirstWallCarryingIt()
    {
        Guid exportId = Parsing.ElementExportIds.ExportId(
            Guid.Parse("d45b1816-cbcc-4dc6-b0bb-007467f3c600"), 263584u);
        var first = Wall(100, 500, "Wall", 1);
        var second = Wall(101, 500, "Wall", 1, offsetX: 10);
        var plain = Wall(102, 500, "Wall", 1, offsetX: 20);
        first.SerializedExportId = exportId;
        second.SerializedExportId = exportId;
        string ifc = ExportWalls(first, second, plain);

        string GlobalIdOf(int tag) => Regex.Match(ifc,
            $@"IFCWALLSTANDARDCASE\('([^']*)',[^\n]*,'{tag}'\);").Groups[1].Value;
        Assert.Equal("3KMnWMoynDnh2x07HdzyEW", GlobalIdOf(100));
        Assert.Equal(IfcGuid.FromElementId(101), GlobalIdOf(101));
        Assert.Equal(IfcGuid.FromElementId(102), GlobalIdOf(102));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpatialStructure_GlobalIdsComeFromTheDocumentGuid(bool projectGuidParameter)
    {
        // The document GUID with the export ids of -16/-15/-14 and of storey level 1.
        var info = new RevitFileInfo { SerializedDocumentGuid = Guid.Parse("e340d1fc-19c1-4200-9ff0-415bc126641d") };
        info.Elements.Add(Level());
        info.Elements.Add(Wall(100, 500, "Wall", 1));
        if (projectGuidParameter)
            info.Elements[0].Parameters.Add(new Parameter
            {
                Id = -1019002, Definition = new Definition { Id = -1019002, Name = "IfcProject GUID" },
                StorageType = StorageType.String, Value = "0123456789ABCDEFGHIJKL",
            });
        string path = TempIfcPath("document-guids");
        string ifc;
        try { new IfcExporter().Export(info, path); ifc = File.ReadAllText(path); }
        finally { Cleanup(path); }

        string GlobalIdOf(string entity) => Regex.Match(ifc, $@"={entity}\('([^']*)'").Groups[1].Value;
        Assert.Equal(projectGuidParameter ? IfcGuid.FromName("project") : "3ZGD7y6S5209$mGLi_sPlj", GlobalIdOf("IFCPROJECT"));
        Assert.Equal(projectGuidParameter ? IfcGuid.FromName("building") : "3ZGD7y6S5209$mGLi_sPli", GlobalIdOf("IFCBUILDING"));
        Assert.Equal(projectGuidParameter ? IfcGuid.FromName("site") : "3ZGD7y6S5209$mGLi_sPll", GlobalIdOf("IFCSITE"));
        Assert.Equal(IfcGuid.ToIfcGuid(Parsing.ElementExportIds.ExportId(info.SerializedDocumentGuid.Value, 1u)),
            GlobalIdOf("IFCBUILDINGSTOREY"));
        string projectSiteRelation = Regex.Match(ifc, @"IFCRELAGGREGATES\('([^']*)',#\d+,(?:\$|''),(?:\$|''),#(\d+),").Groups[1].Value;
        Assert.Equal(projectGuidParameter ? IfcGuid.FromName("agg:project-site") : "0M1SxRcfh5eV0GOZD2r2NA",
            projectSiteRelation);
    }

    [Theory]
    [InlineData(IfcProfile.Current, "3Zu5Bv0LOHrPC10026FoQQ")] // the level's own export id (level 311)
    [InlineData(IfcProfile.Legacy, "3ZGD7y6S5209$mGLl19cKg")] // the document GUID with the level id
    public void StoreyGlobalId_DependsOnTheReferenceVersion(IfcProfile version, string expected)
    {
        var info = new RevitFileInfo { SerializedDocumentGuid = Guid.Parse("e340d1fc-19c1-4200-9ff0-415bc126641d") };
        var level = Level();
        level.Id = 311;
        level.SerializedExportId = Parsing.ElementExportIds.ExportId(Guid.Parse("e3e052f9-0156-11d5-9301-0000863f27ad"), 311u);
        info.Elements.Add(level);
        info.Elements.Add(Wall(100, 500, "Wall", 311));
        string path = TempIfcPath("storey-guid-version");
        string ifc;
        try { new IfcExporter { Profile = version }.Export(info, path); ifc = File.ReadAllText(path); }
        finally { Cleanup(path); }
        Assert.Equal(expected, Regex.Match(ifc, @"=IFCBUILDINGSTOREY\('([^']*)'").Groups[1].Value);
    }

    [Fact]
    public void Storey_RelationsUseTheLevelAndProjectInformationExportIds()
    {
        // Level 311 and ProjectInformation 49504.
        var level = Level();
        level.SerializedExportId = Parsing.ElementExportIds.ExportId(Guid.Parse("e3e052f9-0156-11d5-9301-0000863f27ad"), 311u);
        var projectInformation = new Element
        {
            Id = 49504, CategoryId = -2003101, Category = "OST_ProjectInformation",
            SerializedExportId = Parsing.ElementExportIds.ExportId(Guid.Parse("8764c510-57b7-44c3-bddf-266d86c26380"), 49504u),
        };
        var info = new RevitFileInfo();
        info.Elements.AddRange([level, projectInformation, Wall(100, 500, "Wall", 1)]);
        string path = TempIfcPath("storey-relations");
        string ifc;
        try { new IfcExporter().Export(info, path); ifc = File.ReadAllText(path); }
        finally { Cleanup(path); }

        Assert.Matches(@"IFCRELCONTAINEDINSPATIALSTRUCTURE\('3Zu5Bv0LOHrPC10066FoQQ',", ifc);
        Assert.Matches(@"IFCRELAGGREGATES\('27PCKGLxT4mxtV9cw6mgBW',", ifc);
    }

    [Theory]
    [InlineData(true, "IFCBOOLEAN(.T.)")]
    [InlineData(false, "IFCBOOLEAN(.F.)")]
    public void Wall_SerializedTopAttachmentProducesExtendToStructure(bool joinedToTop, string expected)
    {
        var wall = Wall(100, 500, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        wall.SerializedWallJoinedToTop = joinedToTop;
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        string ifc = ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.Equal(expected, values["ExtendToStructure"]);
        Assert.Equal("IFCBOOLEAN(.T.)", values["LoadBearing"]);
    }

    [Theory]
    [InlineData("occurrence", 0, true, "IFCBOOLEAN(.F.)")]
    [InlineData("occurrence", 1, false, "IFCBOOLEAN(.T.)")]
    [InlineData("occurrence", 1, null, "IFCBOOLEAN(.T.)")]
    [InlineData("type", 1, false, "IFCBOOLEAN(.F.)")]
    [InlineData("type_suffix", 0, true, "IFCBOOLEAN(.T.)")]
    public void Wall_OnlyOccurrenceNamedExtendToStructureOverrides(
        string scope, int value, bool? joinedToTop, string expected)
    {
        var wall = Wall(100, 500, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        wall.SerializedWallJoinedToTop = joinedToTop;
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        (scope == "occurrence" ? wall : type).Parameters.Add(new Parameter
        {
            Definition = new Definition
                { Name = scope == "type_suffix" ? "ExtendToStructure[Type]" : "ExtendToStructure" },
            StorageType = StorageType.Integer, Value = value,
        });
        string ifc = ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.Equal(expected, values["ExtendToStructure"]);
    }

    [Theory]
    [InlineData(0, true, "IFCBOOLEAN(.F.)")]
    [InlineData(1, false, "IFCBOOLEAN(.T.)")]
    [InlineData(1, null, "IFCBOOLEAN(.T.)")]
    public void Wall_SetQualifiedExtendToStructureOverridesTheTopAttachment(int value, bool? joinedToTop, string expected)
    {
        var wall = Wall(100, 500, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        wall.SerializedWallJoinedToTop = joinedToTop;
        wall.Parameters.Add(new Parameter
        {
            Definition = new Definition { Name = "Pset_WallCommon.ExtendToStructure" },
            StorageType = StorageType.Integer, Value = value,
        });
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        string ifc = ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.Equal(expected, values["ExtendToStructure"]);
    }

    [Theory]
    [InlineData(0, 1, "IFCBOOLEAN(.F.)")]
    [InlineData(1, 0, "IFCBOOLEAN(.T.)")]
    public void Wall_SetQualifiedExtendToStructureWinsOverThePropertyName(int qualified, int plain, string expected)
    {
        var wall = Wall(100, 500, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        wall.SerializedWallJoinedToTop = plain == 1;
        wall.Parameters.Add(new Parameter
        {
            Definition = new Definition { Name = "ExtendToStructure" },
            StorageType = StorageType.Integer, Value = plain,
        });
        wall.Parameters.Add(new Parameter
        {
            Definition = new Definition { Name = "Pset_WallCommon.ExtendToStructure" },
            StorageType = StorageType.Integer, Value = qualified,
        });
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        string ifc = ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.Equal(expected, values["ExtendToStructure"]);
    }

    [Theory]
    [InlineData("missing_type")] [InlineData("missing_link")]
    public void Wall_TopAttachmentNeedsNoWallType(string mutation)
    {
        var wall = Wall(100, 500, "Wall", 1);
        if (mutation != "missing_link") wall.Parameters.Add(TypeParameter(500));
        wall.SerializedWallJoinedToTop = true;
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        string ifc = mutation == "missing_type" ? ExportWalls(wall) : ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.Equal("IFCBOOLEAN(.T.)", values["ExtendToStructure"]);
    }

    [Theory]
    [InlineData("unvalidated")] [InlineData("approximate_override")]
    [InlineData("conflicting_override")]
    public void Wall_UnvalidatedOrBlockedTopAttachmentOmitsExtendToStructure(string mutation)
    {
        var wall = Wall(100, 500, "Wall", 1);
        wall.Parameters.Add(TypeParameter(500));
        wall.SerializedWallJoinedToTop = mutation == "unvalidated" ? null : true;
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        if (mutation == "approximate_override")
            wall.Parameters.Add(new Parameter
            {
                Definition = new Definition { Name = "ExtendToStructure" },
                StorageType = StorageType.Integer, Value = 1, IsApproximate = true,
            });
        if (mutation == "conflicting_override")
            foreach (int value in new[] { 0, 1 })
                wall.Parameters.Add(new Parameter
                {
                    Definition = new Definition { Name = "ExtendToStructure" },
                    StorageType = StorageType.Integer, Value = value,
                });
        string ifc = ExportWalls(type, wall);
        var values = OccurrenceCommonValues(ifc, SoleEntityId(ifc, "IFCWALLSTANDARDCASE"));
        Assert.DoesNotContain("ExtendToStructure", values.Keys);
        Assert.Equal("IFCBOOLEAN(.T.)", values["LoadBearing"]);
    }

    [Fact]
    public void Walls_ShareOnePooledExtendToStructureValue()
    {
        var type = new Element { Id = 500, CategoryId = -2000011, Name = "Basic wall" };
        var first = Wall(100, 500, "First", 1);
        var second = Wall(200, 500, "Second", 1, offsetX: 20);
        foreach (var wall in new[] { first, second })
        {
            wall.Parameters.Add(TypeParameter(500));
            wall.SerializedWallJoinedToTop = true;
        }
        string ifc = ExportWalls(type, first, second);
        Assert.Single(Regex.Matches(ifc,
            @"IFCPROPERTYSINGLEVALUE\('ExtendToStructure',(?:\$|''),IFCBOOLEAN\(\.T\.\),\$\)"));
        Assert.Equal(2, AllPsetsOfName(ifc, "Pset_WallCommon").Count);
    }

    private static Parameter TypeParameter(int typeId) => new()
    {
        Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId, Value = typeId,
    };

    private static string ExportWalls(params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Level()); info.Elements.AddRange(elements);
        string path = TempIfcPath("wall-native-function");
        try { new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path); return File.ReadAllText(path); }
        finally { Cleanup(path); }
    }

    private static Dictionary<string, string> OccurrenceCommonValues(string ifc, int wallId)
    {
        var pset = Assert.Single(AllPsetsOfName(ifc, "Pset_WallCommon"), p =>
            Regex.IsMatch(ifc, $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#{wallId}\),#{p.psetId}\);"));
        return ResolveProperties(ifc, pset.valueIds);
    }


    private static Element Wall(
        int id, int typeId, string? name, int? structuralUsage, double offsetX = 0) => new()
    {
        Id = id,
        TypeId = typeId,
        CategoryId = -2000011,
        Category = "OST_Walls",
        Name = name,
        References = [PhaseId],
        Parameters =
        [
            PhaseParameter(),
            new Parameter
            {
                Id = -1001952,
                Definition = new Definition { Name = "LEVEL_PARAM" },
                StorageType = StorageType.ElementId,
                Value = 1,
            },
            new Parameter
            {
                Id = -1001000,
                Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" },
                StorageType = StorageType.Double,
                Value = 1.0,
            },
            .. structuralUsage is int usage
                ?
                [
                    new Parameter
                    {
                        Id = WallStructuralUsageParamId,
                        Definition = new Definition
                        {
                            Id = WallStructuralUsageParamId,
                            Name = "WALL_STRUCTURAL_USAGE_PARAM",
                        },
                        StorageType = StorageType.Integer,
                        Value = usage,
                    },
                ]
                : Array.Empty<Parameter>(),
        ],
        Location = new LocationCurve
        {
            CurveType = "Line",
            StartPoint = [offsetX, 0, 0],
            EndPoint = [offsetX + 4, 0, 0],
        },
        BoundingBox = [offsetX, -0.5, 0, offsetX + 4, 0.5, 3],
    };

    private static Parameter PhaseParameter() => new()
    {
        Id = -1012100,
        StorageType = StorageType.Integer,
        Value = PhaseId,
    };

    /// A real, accepted project storey (id 1, matching every Wall()'s LEVEL_PARAM above). Without it the phantom-storey wall gate
    /// (see IfcExporterPhantomStoreyWallSuppressionTests) drops the whole product before the Pset code is reached.
    private static Element Level() => new()
    {
        Id = 1,
        TypeId = 1001,
        CategoryId = -2000240,
        Category = "OST_Levels",
        Name = "Ground",
        IsNativeLevel = true,
        SerializedFamilyId = -1,
        SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

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

    private static (int psetId, List<int> valueIds) SolePsetOfName(string ifc, string psetName) =>
        Assert.Single(AllPsetsOfName(ifc, psetName));

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
}
