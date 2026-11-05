using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Attribute spelling of the current profile that differs from the legacy profile: empty strings, storey, site, opening, type and
/// slab/railing attributes.</summary>
public class IfcExporterDialectTests
{
    private const string Guid22 = "'3ZGD7y6S5209$mGLi_sPlj'";

    private static StepWriter Writer(params (string Type, string Args)[] entities)
    {
        var w = new StepWriter();
        foreach (var (type, args) in entities) w.Add(type, args);
        return w;
    }

    private static string ArgsOf(StepWriter w, int index) => w.EntityAt(index).Args;

    [Fact]
    public void EmptyStrings_RootNameAndDescriptionUnset_BecomeEmptyStrings()
    {
        var w = Writer(
            ("IFCRELAGGREGATES", $"{Guid22},#5,$,$,#20,(#21)"),
            ("IFCWALLTYPE", $"{Guid22},#5,'Generic - 200',$,$,$,$,'1',$,.NOTDEFINED."),
            ("IFCDISTRIBUTIONPORT", $"{Guid22},#5,'Port_1_1',$,'1',#7,$,.SOURCE."));

        Assert.Equal(3, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal($"{Guid22},#5,'',''," + "#20,(#21)", ArgsOf(w, 0));
        Assert.Equal($"{Guid22},#5,'Generic - 200',''," + "$,$,$,'1',$,.NOTDEFINED.", ArgsOf(w, 1));
        Assert.Equal($"{Guid22},#5,'Port_1_1',''," + "'1',#7,$,.SOURCE.", ArgsOf(w, 2));
    }

    [Fact]
    public void EmptyStrings_ValuesAreKept_AndAStringWithCommasAndQuotesDoesNotShiftTheAttributes()
    {
        string tricky = $"{Guid22},#5,'a,b''c, d',$,'Basic Wall:Generic',#9,$,'12'";
        var w = Writer(
            ("IFCWALL", tricky),
            ("IFCSLAB", $"{Guid22},#5,'Slab','Described','x',#9,$,'1',.FLOOR."));

        Assert.Equal(1, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal($"{Guid22},#5,'a,b''c, d','','Basic Wall:Generic',#9,$,'12'", ArgsOf(w, 0));
        Assert.Equal($"{Guid22},#5,'Slab','Described','x',#9,$,'1',.FLOOR.", ArgsOf(w, 1));
    }

    [Fact]
    public void EmptyStrings_NameUnset_BecomesAnEmptyString()
    {
        var w = Writer(("IFCRELDEFINESBYTYPE", $"{Guid22},#5,$,$,(#1,#2),#3"));

        Assert.Equal(1, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal($"{Guid22},#5,'','',(#1,#2),#3", ArgsOf(w, 0));
    }

    [Theory]
    // IfcProject, IfcSite and IfcBuilding carry the ObjectType right after the Description.
    [InlineData("IFCPROJECT", "'001',$,$,'Long','Phase',(#1),#2", "'001','','','Long','Phase',(#1),#2")]
    [InlineData("IFCBUILDING", "'B',$,$,#4,$,'B',.ELEMENT.,$,$,$", "'B','','',#4,$,'B',.ELEMENT.,$,$,$")]
    [InlineData("IFCSITE", "'Default',$,$,#4,$,$,.ELEMENT.", "'Default','','',#4,$,$,.ELEMENT.")]
    public void EmptyStrings_ProjectSiteAndBuildingObjectTypeIsAnEmptyString(string type, string tail, string expectedTail)
    {
        var w = Writer((type, $"{Guid22},#5,{tail}"));

        Assert.Equal(1, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal($"{Guid22},#5,{expectedTail}", ArgsOf(w, 0));
    }

    [Fact]
    public void EmptyStrings_ObjectTypeOfAnOrdinaryRootStaysUnset()
    {
        var w = Writer(("IFCGROUP", $"{Guid22},#5,'G',$,$"));

        IfcExporter.ApplyCurrentEmptyStrings(w);

        Assert.Equal($"{Guid22},#5,'G','',$", ArgsOf(w, 0));
    }

    [Fact]
    public void EmptyStrings_ShapeColourStyledItemMaterialRepresentationAndOrganization()
    {
        var w = Writer(
            ("IFCPRODUCTDEFINITIONSHAPE", "$,$,(#1)"),
            ("IFCMATERIALDEFINITIONREPRESENTATION", "$,$,(#2),#3"),
            ("IFCCOLOURRGB", "$,0.5,0.5,0.5"),
            ("IFCSTYLEDITEM", "$,(#4),$"),
            ("IFCORGANIZATION", "$,'RvtIO',$,$,$"),
            ("IFCSTYLEDITEM", "#9,(#4),'Kept'"));

        Assert.Equal(5, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal("'','',(#1)", ArgsOf(w, 0));
        Assert.Equal("'','',(#2),#3", ArgsOf(w, 1));
        Assert.Equal("'',0.5,0.5,0.5", ArgsOf(w, 2));
        Assert.Equal("$,(#4),''", ArgsOf(w, 3));
        Assert.Equal("'','RvtIO','',$,$", ArgsOf(w, 4));
        Assert.Equal("#9,(#4),'Kept'", ArgsOf(w, 5));
    }

    [Fact]
    public void EmptyStrings_PropertySetsRelationsAndSingleValuesAreSpelledEmpty()
    {
        var w = Writer(
            ("IFCPROPERTYSET", $"{Guid22},#5,'Pset_WallCommon',$,(#1)"),
            ("IFCRELDEFINESBYPROPERTIES", $"{Guid22},#5,$,$,(#2),#3"),
            ("IFCPROPERTYSINGLEVALUE", "'Reference',$,IFCIDENTIFIER('A'),$"));

        Assert.Equal(3, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal($"{Guid22},#5,'Pset_WallCommon','',(#1)", ArgsOf(w, 0));
        Assert.Equal($"{Guid22},#5,'','',(#2),#3", ArgsOf(w, 1));
        Assert.Equal("'Reference','',IFCIDENTIFIER('A'),$", ArgsOf(w, 2));
    }

    [Fact]
    public void EmptyStrings_ASetWithADescriptionKeepsIt()
    {
        string pset = $"{Guid22},#5,'Pset_WallCommon','kept',(#1)";
        var w = Writer(("IFCPROPERTYSET", pset));

        Assert.Equal(0, IfcExporter.ApplyCurrentEmptyStrings(w));

        Assert.Equal(pset, ArgsOf(w, 0));
    }

    [Fact]
    public void EmptyStrings_ASecondRunChangesNothing()
    {
        var w = Writer(("IFCRELAGGREGATES", $"{Guid22},#5,$,$,#20,(#21)"));
        Assert.Equal(1, IfcExporter.ApplyCurrentEmptyStrings(w));
        Assert.Equal(0, IfcExporter.ApplyCurrentEmptyStrings(w));
    }

    [Fact]
    public void EmptyStrings_ShortOrForeignEntitiesAreIgnored()
    {
        var w = Writer(
            ("IFCCARTESIANPOINT", "(0.,0.,0.)"),
            ("IFCSIUNIT", "*,.LENGTHUNIT.,$,.METRE."),
            ("IFCRELAGGREGATES", "'short',#5"));

        Assert.Equal(0, IfcExporter.ApplyCurrentEmptyStrings(w));
    }

    private static StepWriter OpeningFixture(string openingName, string openingTag, bool filled, string fillerName)
    {
        var w = new StepWriter();
        int host = w.Add("IFCWALL", $"{Guid22},#5,'Basic Wall:Generic:100','','Basic Wall:Generic',#9,$,'100'");
        int opening = w.Add("IFCOPENINGELEMENT",
            $"{Guid22},#5,{openingName},$,'OST_SWallRectOpening:Element 7',#9,$,{openingTag}");
        w.Add("IFCRELVOIDSELEMENT", $"{Guid22},#5,'','',#{host},#{opening}");
        if (filled)
        {
            int door = w.Add("IFCDOOR", $"{Guid22},#5,{fillerName},'','Door:Single',#9,$,'300',$,$");
            w.Add("IFCRELFILLSELEMENT", $"{Guid22},#5,'','',#{opening},#{door}");
        }
        return w;
    }

    [Fact]
    public void OpeningAttributes_UnfilledOpening_TakesObjectTypeOpeningAndTheHostTag()
    {
        var w = OpeningFixture("'Basic Wall:Generic:100'", "'7'", filled: false, fillerName: "");

        Assert.Equal(1, IfcExporter.ApplyCurrentOpeningAttributes(w));

        Assert.Equal($"{Guid22},#5,'Basic Wall:Generic:100',$,'Opening',#9,$,'100'", ArgsOf(w, 1));
    }

    [Fact]
    public void OpeningAttributes_CutShallowerThanItsHost_IsARecess()
    {
        var w = OpeningFixture("'Basic Wall:Generic:100'", "'7'", filled: false, fillerName: "");
        int through = w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Basic Wall:Generic:100',$,'x',#9,$,'8'");
        w.Add("IFCRELVOIDSELEMENT", $"{Guid22},#5,'','',#{w.EntityAt(0).Id},#{through}");

        Assert.Equal(2, IfcExporter.ApplyCurrentOpeningAttributes(w, new HashSet<int> { w.EntityAt(1).Id }));

        Assert.Equal($"{Guid22},#5,'Basic Wall:Generic:100',$,'Recess',#9,$,'100'", ArgsOf(w, 1));
        Assert.Equal($"{Guid22},#5,'Basic Wall:Generic:100',$,'Opening',#9,$,'100'", ArgsOf(w, w.EntityCount - 2));
    }

    [Fact]
    public void OpeningAttributes_FilledOpeningNamedAfterTheHost_TakesTheHostTag()
    {
        var w = OpeningFixture("'Basic Wall:Generic:100'", "'300'", filled: true, fillerName: "'Door:Single:300'");

        IfcExporter.ApplyCurrentOpeningAttributes(w);

        Assert.EndsWith(",'100'", ArgsOf(w, 1));
        Assert.Contains(",'Opening',", ArgsOf(w, 1));
    }

    [Fact]
    public void OpeningAttributes_OpeningNamedAfterItsFiller_KeepsTheFillersTag()
    {
        var w = OpeningFixture("'Door:Single:300'", "'300'", filled: true, fillerName: "'Door:Single:300'");

        IfcExporter.ApplyCurrentOpeningAttributes(w);

        Assert.EndsWith(",'300'", ArgsOf(w, 1));
        Assert.Contains(",'Opening',", ArgsOf(w, 1));
    }

    [Fact]
    public void OpeningAttributes_HostTaggedOpeningIsContainedWhereItsHostIs_ButAFillersExtrusionIsNot()
    {
        var w = new StepWriter();
        int host = w.Add("IFCWALL", $"{Guid22},#5,'Basic Wall:Generic:100','','Basic Wall:Generic',#9,$,'100'");
        int cut = w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Basic Wall:Generic:100',$,'x',#9,$,'7'");
        int doorOpening = w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Door:Single:300',$,'x',#9,$,'300'");
        int door = w.Add("IFCDOOR", $"{Guid22},#5,'Door:Single:300','','Door:Single',#9,$,'300',$,$");
        w.Add("IFCRELVOIDSELEMENT", $"{Guid22},#5,'','',#{host},#{cut}");
        w.Add("IFCRELVOIDSELEMENT", $"{Guid22},#5,'','',#{host},#{doorOpening}");
        w.Add("IFCRELFILLSELEMENT", $"{Guid22},#5,'','',#{doorOpening},#{door}");
        int containment = w.Add("IFCRELCONTAINEDINSPATIALSTRUCTURE", $"{Guid22},#5,'','',(#{host},#{door}),#800");

        IfcExporter.ApplyCurrentOpeningAttributes(w);

        string relation = w.EntityAt(w.EntityCount - 1).Args;
        Assert.Equal(containment, w.EntityAt(w.EntityCount - 1).Id);
        Assert.Equal($"{Guid22},#5,'','',(#{host},#{door},#{cut}),#800", relation);
    }

    [Fact]
    public void OpeningAttributes_FilledOpeningNamedAfterTheHostAndTheDoorsOwnUnfilledOpening_AreNotContained()
    {
        // The openings the door and window rule creates itself never reach the product wrapper: filled "body" openings (named and
        // tagged after the host) and, when the door is no product, the unfilled extrusion opening.
        string doorOpeningGuid = "'" + new string('B', 22) + "'";
        var w = new StepWriter();
        int host = w.Add("IFCWALL", $"{Guid22},#5,'Basic Wall:Generic:100','','Basic Wall:Generic',#9,$,'100'");
        int cut = w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Basic Wall:Generic:100',$,'x',#9,$,'7'");
        int body = w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Basic Wall:Generic:100',$,'x',#9,$,'300'");
        int extrusion = w.Add("IFCOPENINGELEMENT", $"{doorOpeningGuid},#5,'Basic Wall:Generic:100',$,'x',#9,$,'100'");
        int door = w.Add("IFCDOOR", $"{Guid22},#5,'Door:Single:300','','Door:Single',#9,$,'300',$,$");
        foreach (int opening in new[] { cut, body, extrusion })
            w.Add("IFCRELVOIDSELEMENT", $"{Guid22},#5,'','',#{host},#{opening}");
        w.Add("IFCRELFILLSELEMENT", $"{Guid22},#5,'','',#{body},#{door}");
        w.Add("IFCRELCONTAINEDINSPATIALSTRUCTURE", $"{Guid22},#5,'','',(#{host},#{door}),#800");

        IfcExporter.ApplyCurrentOpeningAttributes(w, null, new HashSet<string> { doorOpeningGuid.Trim('\'') });

        Assert.Equal($"{Guid22},#5,'','',(#{host},#{door},#{cut}),#800", w.EntityAt(w.EntityCount - 1).Args);
        Assert.EndsWith(",'100'", ArgsOf(w, 2)); // the body opening still takes the host's Tag
    }

    [Fact]
    public void OpeningAttributes_OpeningWithoutAHost_OnlyGetsTheObjectType()
    {
        var w = new StepWriter();
        w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Lonely',$,'OST_X',#9,$,'7'");

        IfcExporter.ApplyCurrentOpeningAttributes(w);

        Assert.Equal($"{Guid22},#5,'Lonely',$,'Opening',#9,$,'7'", ArgsOf(w, 0));
    }

    private const int ManufacturerParameterId = -1010108;

    private static Element WithManufacturer(int id, string? value, int typeId = 0)
    {
        var element = new Element { Id = id };
        if (typeId > 0)
            element.Parameters.Add(new Parameter
            {
                Id = -1002050,
                Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId,
                Value = typeId,
            });
        if (value != null)
            element.Parameters.Add(new Parameter { Id = ManufacturerParameterId, StorageType = StorageType.String, Value = value });
        return element;
    }

    private static List<(string Type, string Args)> ManufacturerEntities(Element cutter, params Element[] others)
    {
        var w = new StepWriter();
        int opening = w.Add("IFCOPENINGELEMENT", $"{Guid22},#5,'Basic Wall:Generic:100',$,'Opening',#9,$,'100'");
        var elements = others.Append(cutter).ToDictionary(e => e.Id);
        IfcExporter.ExportOpeningManufacturerPropertySetCurrent(w, "OpeningGuid", opening, 5, cutter, elements, []);
        return Enumerable.Range(1, w.EntityCount - 1).Select(i => (w.EntityAt(i).Type, w.EntityAt(i).Args)).ToList();
    }

    [Fact]
    public void OpeningManufacturer_TheCuttersManufacturerBecomesAPropertySetOfTheOpening()
    {
        var entities = ManufacturerEntities(WithManufacturer(300, "Acme"));

        Assert.Equal(["IFCPROPERTYSINGLEVALUE", "IFCPROPERTYSET", "IFCRELDEFINESBYPROPERTIES"], entities.Select(e => e.Type));
        Assert.Equal("'Manufacturer',$,IFCLABEL('Acme'),$", entities[0].Args);
        Assert.Contains(",'Pset_ManufacturerTypeInformation',$,(#2)", entities[1].Args);
        Assert.EndsWith(",(#1),#3", entities[2].Args);
    }

    [Fact]
    public void OpeningManufacturer_TheInstanceWinsOverItsType_AndTheTypeSuppliesAMissingOne()
    {
        var type = WithManufacturer(400, "Type maker");

        Assert.Contains("IFCLABEL('Own maker')", ManufacturerEntities(WithManufacturer(300, "Own maker", typeId: 400), type)[0].Args);
        Assert.Contains("IFCLABEL('Type maker')", ManufacturerEntities(WithManufacturer(300, null, typeId: 400), type)[0].Args);
    }

    [Fact]
    public void OpeningManufacturer_ACutterWithoutTheParameterGetsNoPropertySet()
    {
        Assert.Empty(ManufacturerEntities(WithManufacturer(300, null)));
        Assert.Empty(ManufacturerEntities(WithManufacturer(300, ""), WithManufacturer(400, null)));
    }

    private static Element TypeElement(int id, string? family, string name, int categoryId = -2000011)
    {
        var type = new Element { Id = id, CategoryId = categoryId, Name = name, FamilyName = family };
        return type;
    }

    private static StepWriter TypeFixture(string typeClass, string typeArgs, out int productEntity)
    {
        var w = new StepWriter();
        productEntity = w.Add("IFCWALL", $"{Guid22},#5,'Basic Wall:Generic:100','','Basic Wall:Generic',#9,$,'100'");
        int type = w.Add(typeClass, typeArgs);
        w.Add("IFCRELDEFINESBYTYPE", $"{Guid22},#5,'','',(#{productEntity}),#{type}");
        return w;
    }

    private static Element ProductOfType(int productId, int typeId) => new()
    {
        Id = productId,
        CategoryId = -2000011,
        Parameters = [new Parameter
        {
            Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId, Value = typeId,
        }],
    };

    [Fact]
    public void TypeObjects_WallType_NameWithoutFamilyAndNotDefinedPredefinedType()
    {
        var w = TypeFixture("IFCWALLTYPE", $"{Guid22},#5,'Basic Wall:Generic',$,$,$,$,'50',$,.STANDARD.", out int product);
        Element type = TypeElement(50, "Basic Wall", "Generic");
        var elements = new Dictionary<int, Element> { [50] = type, [100] = ProductOfType(100, 50) };

        int rewritten = IfcExporter.ApplyCurrentTypeObjects(w, elements, new Dictionary<int, int> { [product] = 100 });

        Assert.Equal(1, rewritten);
        Assert.Equal($"{Guid22},#5,'Generic',$,$,$,$,'50',$,.NOTDEFINED.", ArgsOf(w, 1));
    }

    [Fact]
    public void TypeObjects_ValidatedOwnNameIsUsedWithoutSplittingTheCombinedName()
    {
        var w = TypeFixture("IFCSLABTYPE", $"{Guid22},#5,'Floor: A:B',$,$,$,$,'50',$,.FLOOR.", out int product);
        Element type = TypeElement(50, "Floor", "ignored", -2000032);
        type.ValidatedIfcTypeObjectName = "Floor: A:B";
        type.ValidatedIfcTypeOwnName = " A:B";
        var elements = new Dictionary<int, Element> { [50] = type, [100] = ProductOfType(100, 50) };

        IfcExporter.ApplyCurrentTypeObjects(w, elements, new Dictionary<int, int> { [product] = 100 });

        Assert.Contains("' A:B'", ArgsOf(w, 1));
        Assert.DoesNotContain("Floor:", ArgsOf(w, 1));
    }

    [Fact]
    public void TypeObjects_SystemFurnitureTypeBecomesAFurnitureTypeWithAssemblyPlace()
    {
        var w = TypeFixture("IFCSYSTEMFURNITUREELEMENTTYPE", $"{Guid22},#5,'Desk:Large',$,$,$,$,'50',$", out int product);
        var elements = new Dictionary<int, Element>
        {
            [50] = TypeElement(50, "Desk", "Large", -2001100), [100] = ProductOfType(100, 50),
        };

        IfcExporter.ApplyCurrentTypeObjects(w, elements, new Dictionary<int, int> { [product] = 100 });

        Assert.Equal("IFCFURNITURETYPE", w.EntityAt(1).Type);
        Assert.Equal($"{Guid22},#5,'Large',$,$,$,$,'50',$,.NOTDEFINED.", ArgsOf(w, 1));
    }

    [Fact]
    public void TypeObjects_FurnitureTypeWithoutAssemblyPlace_GetsNotDefined()
    {
        var w = TypeFixture("IFCFURNITURETYPE", $"{Guid22},#5,'Chair',$,$,$,$,'50',$,$", out int product);
        var elements = new Dictionary<int, Element>
        {
            [50] = TypeElement(50, null, "Chair", -2001100), [100] = ProductOfType(100, 50),
        };

        IfcExporter.ApplyCurrentTypeObjects(w, elements, new Dictionary<int, int> { [product] = 100 });

        Assert.EndsWith(",.NOTDEFINED.", ArgsOf(w, 1));
        Assert.Contains("'Chair'", ArgsOf(w, 1));
    }

    [Fact]
    public void TypeObjects_ANameThatIsNotTheComputedOneIsKept()
    {
        var w = TypeFixture("IFCCOLUMNTYPE", $"{Guid22},#5,'Overridden',$,$,$,$,'50',$,.NOTDEFINED.", out int product);
        var elements = new Dictionary<int, Element>
        {
            [50] = TypeElement(50, "Column", "Round", -2000100), [100] = ProductOfType(100, 50),
        };

        Assert.Equal(0, IfcExporter.ApplyCurrentTypeObjects(w, elements, new Dictionary<int, int> { [product] = 100 }));
        Assert.Contains("'Overridden'", ArgsOf(w, 1));
    }

    [Fact]
    public void TypeObjects_AnUnresolvedProductLeavesTheTypeAsWritten()
    {
        var w = TypeFixture("IFCWALLTYPE", $"{Guid22},#5,'Basic Wall:Generic',$,$,$,$,'50',$,.STANDARD.", out _);

        Assert.Equal(0, IfcExporter.ApplyCurrentTypeObjects(w, new Dictionary<int, Element>(), new Dictionary<int, int>()));
        Assert.Contains("'Basic Wall:Generic'", ArgsOf(w, 1));
    }

    private static Element StairRun(int id, int stairsId, int baseRiser, int topRiser, int begin = 1, int end = 1)
    {
        var numbers = new RvtIO.Parsing.TfBlockDeserializer.StairRunNumbers(0.0, 1.5, 3.0, baseRiser, topRiser, begin, end);
        return new Element
        {
            Id = id,
            SerializedStairRunInput = new RvtIO.Parsing.TfBlockDeserializer.StairRunInput(
                id, stairsId, RvtIO.Parsing.TfBlockDeserializer.StairRunStyle.Straight, [new(1, 0, 0)], numbers),
        };
    }

    [Fact]
    public void StairFlightAttributes_RisersTreadsFromTheRun_RiserHeightAndTreadLengthFromTheStair()
    {
        var stairs = new Element { Id = 100, SerializedActualRiserHeightFeet = 0.5, SerializedActualTreadDepthFeet = 1.0 };
        var run = StairRun(200, 100, baseRiser: 0, topRiser: 3);   // 3 risers, begins and ends with a riser: 2 treads
        var byId = new Dictionary<int, Element> { [100] = stairs, [200] = run };

        string[] attributes = IfcExporter.SplitStepArguments(IfcExporter.StairFlightAttributes(run, byId));

        Assert.Equal(["3", "2", StepWriter.RealFromMillimetres(0.5 * 304.8), StepWriter.RealFromMillimetres(304.8)], attributes);
    }

    [Fact]
    public void StairFlightAttributes_RiserCountAndHeightArePerRun_NotTheStairValues()
    {
        var stairs = new Element
        {
            Id = 100, SerializedActualNumberOfRisers = 5, SerializedActualRiserHeightFeet = 0.6, SerializedActualTreadDepthFeet = 1.0,
        };
        var run = StairRun(200, 100, baseRiser: 0, topRiser: 3);   // elevations 0 to 1.5 over 3 risers: 0.5 per riser
        var byId = new Dictionary<int, Element> { [100] = stairs, [200] = run };

        string[] attributes = IfcExporter.SplitStepArguments(IfcExporter.StairFlightAttributes(run, byId));

        Assert.Equal("3", attributes[0]);
        Assert.Equal(StepWriter.RealFromMillimetres(0.5 * 304.8), attributes[2]);
    }

    [Fact]
    public void StairFlightAttributes_RiserHeightDividesByTheRiserIndexSpan()
    {
        var stairs = new Element { Id = 100, SerializedActualRiserHeightFeet = 0.6 };
        var run = StairRun(200, 100, baseRiser: 2, topRiser: 5);
        var byId = new Dictionary<int, Element> { [100] = stairs, [200] = run };

        string[] attributes = IfcExporter.SplitStepArguments(IfcExporter.StairFlightAttributes(run, byId));

        Assert.Equal(StepWriter.RealFromMillimetres(1.5 / 3 * 304.8), attributes[2]);
    }

    [Fact]
    public void StairFlightAttributes_UnknownSourcesStayUnset()
    {
        var run = StairRun(200, 100, baseRiser: 0, topRiser: 3);
        var noStairs = new Dictionary<int, Element> { [200] = run };

        Assert.Equal($"3,2,{StepWriter.RealFromMillimetres(0.5 * 304.8)},$", IfcExporter.StairFlightAttributes(run, noStairs));
        Assert.Equal("$,$,$,$", IfcExporter.StairFlightAttributes(new Element { Id = 7 }, noStairs));
    }

    private static Element StairWithType(out Dictionary<int, Element> byId, out Dictionary<int, int> counts)
    {
        var type = new Element
        {
            Id = 300, CategoryId = -2000120, Name = "190mm max riser", SerializedStairsTypeClassId = 3742,
            ValidatedSystemFamilyName = "Assembled Stair",
        };
        var stair = new Element
        {
            Id = 100, CategoryId = -2000120, SerializedOwnElementClassId = 3681, SerializedStairCore = [new(200, 3708)],
            ValidatedStairOccurrenceName = "Stair", SerializedStairTypeLinkIsResolved = true, SerializedStairTypeId = 300,
            SerializedStairNameParameters = [],
            Parameters = [new Parameter { Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = 300 }],
        };
        byId = new Dictionary<int, Element> { [100] = stair, [300] = type };
        counts = byId.Keys.ToDictionary(id => id, _ => 1);
        return stair;
    }

    [Fact]
    public void StairName_CurrentUsesTheTypeName_LegacyTheInstanceName()
    {
        var stair = StairWithType(out var byId, out var counts);

        Assert.Equal("Assembled Stair:190mm max riser:100", IfcExporter.ResolveNativeStairName(stair, byId, counts, currentProfile: true));
        Assert.Equal("Assembled Stair:Stair:100", IfcExporter.ResolveNativeStairName(stair, byId, counts));
    }

    [Fact]
    public void LandingObjectType_IsTheTypeNameAlone_InCurrent()
    {
        var type = new Element { Id = 300, CategoryId = -2000920, Name = "220mm Thickness", ValidatedStairComponentTypeName = "220mm Thickness" };
        var landing = ProductOfType(400, 300);
        landing.CategoryId = -2000920;
        landing.Category = "OST_StairsLandings";
        landing.TypeId = 300;
        var byId = new Dictionary<int, Element> { [300] = type, [400] = landing };
        var counts = byId.Keys.ToDictionary(id => id, _ => 1);

        Assert.Equal("220mm Thickness", IfcExporter.ResolveProductFamilyAndTypeName(landing, "IFCSLAB", byId, counts, currentProfile: true));
        Assert.Equal("OST_StairsLandings:220mm Thickness", IfcExporter.ResolveProductFamilyAndTypeName(landing, "IFCSLAB", byId, counts));
    }

    [Theory]
    [InlineData("Basic Wall:SIP 202mm Wall - conc clad", "Basic Wall : SIP 202mm Wall - conc clad")]
    [InlineData("Floor:Generic 150mm", "Floor : Generic 150mm")]
    [InlineData("Generic", "Generic")]
    [InlineData("A:B:C", "A : B:C")]
    public void ShortDescriptiveName_PutsSpacesAroundTheFirstColon(string input, string expected) =>
        Assert.Equal(expected, IfcExporter.CurrentShortDescriptiveName(input));

    [Fact]
    public void StoreyObjectType_IsTheLevelFamilyLabelAndTheTypeName()
    {
        var level = new Element { Id = 1, CategoryId = -2000240, SerializedUiFamilyLabel = "Level" };
        var type = new Element { Id = 2, CategoryId = -2000240, Name = "8mm Head" };

        Assert.Equal("Level:8mm Head", IfcExporter.StoreyObjectType(level, type));
        Assert.Null(IfcExporter.StoreyObjectType(level, null));
        Assert.Null(IfcExporter.StoreyObjectType(level, new Element { Id = 2, CategoryId = -2000011, Name = "x" }));
        Assert.Null(IfcExporter.StoreyObjectType(level, new Element { Id = 1, CategoryId = -2000240, Name = "self" }));
        Assert.Null(IfcExporter.StoreyObjectType(new Element { Id = 1, CategoryId = -2000240 }, type));
    }

    [Theory]
    [InlineData(-2001263, "FLOOR", true, "NOTDEFINED")]   // building pad
    [InlineData(-2001263, "FLOOR", false, "FLOOR")]
    [InlineData(-2000032, "FLOOR", true, "BASESLAB")]     // floor whose type is a foundation slab type
    [InlineData(-2000032, "FLOOR", false, "FLOOR")]
    [InlineData(-2000920, "LANDING", true, "LANDING")]
    public void SlabPredefinedType_PadAndFoundationFloor(int categoryId, string resolved, bool currentProfile, string expected)
    {
        var type = new Element { Id = 9, CategoryId = categoryId == -2000032 ? -2001300 : categoryId };
        var occurrence = ProductOfType(8, 9);
        occurrence.CategoryId = categoryId;

        Assert.Equal(expected, IfcExporter.CurrentSlabPredefinedType(occurrence, type, currentProfile, resolved));
    }

    [Fact]
    public void SlabPredefinedType_AFloorOfAnOrdinaryFloorTypeStaysFloor()
    {
        var type = new Element { Id = 9, CategoryId = -2000032 };
        var occurrence = ProductOfType(8, 9);
        occurrence.CategoryId = -2000032;

        Assert.Equal("FLOOR", IfcExporter.CurrentSlabPredefinedType(occurrence, type, true, "FLOOR"));
    }

    private const int PhaseId = 3;

    private static Element Level(string? familyLabel = "Level") => new()
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
        SerializedUiFamilyLabel = familyLabel,
        Parameters =
        [
            new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
            new Parameter
            {
                Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId, Value = 1001,
            },
        ],
    };

    private static Element LevelType() => new()
    {
        Id = 1001, CategoryId = -2000240, Category = "OST_Levels", Name = "8mm Head",
    };

    private static Element Wall(int id, double x0, double y0, double x1, double y1, params int[] references) => new()
    {
        Id = id,
        TypeId = 500,
        CategoryId = -2000011,
        Category = "OST_Walls",
        Name = "Wall",
        References = [PhaseId, .. references],
        Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.Integer, Value = PhaseId },
            new Parameter
            {
                Id = -1001952, Definition = new Definition { Name = "LEVEL_PARAM" },
                StorageType = StorageType.ElementId, Value = 1,
            },
            new Parameter
            {
                Id = -1001000, Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" },
                StorageType = StorageType.Double, Value = 1.0,
            },
        ],
        Location = new LocationCurve { CurveType = "Line", StartPoint = [x0, y0, 0], EndPoint = [x1, y1, 0] },
        BoundingBox = [Math.Min(x0, x1), Math.Min(y0, y1) - 0.5, 0, Math.Max(x0, x1), Math.Max(y0, y1) + 0.5, 3],
    };

    private static string Export(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        foreach (var element in elements) info.Elements.Add(element);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-dialect-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static string Entity(string ifc, string type) =>
        Regex.Match(ifc, $@"#\d+={type}\((.*?)\);").Groups[1].Value;

    [Theory]
    [InlineData(IfcProfile.Current, 255)]
    [InlineData(IfcProfile.Legacy, 300)]
    public void StringPropertyByteLimitAppliesOnlyToCurrentWithoutChangingProductNames(IfcProfile version, int expectedLength)
    {
        var wall = Wall(100, 0, 0, 4, 0); string text = new('a', 300); wall.Name = text;
        wall.Parameters.Add(new Parameter { Id = -1, StorageType = StorageType.String, Value = text,
            Definition = new Definition { Name = "Reference" } });
        string ifc = Export(version, Level(), LevelType(), wall);
        var references = Regex.Matches(ifc, @"#\d+=IFCPROPERTYSINGLEVALUE\((.*?)\);")
            .Select(match => IfcExporter.SplitStepArguments(match.Groups[1].Value))
            .Where(args => args[0] == "'Reference'" && args[2].StartsWith("IFCIDENTIFIER(")).ToList();
        Assert.NotEmpty(references);
        Assert.All(references, args => Assert.Equal($"IFCIDENTIFIER('{new string('a', expectedLength)}')", args[2]));
        var product = Regex.Match(ifc, @"#\d+=IFCWALL(?:STANDARDCASE)?\((.*?)\);");
        Assert.True(product.Success); Assert.Contains(text, product.Groups[1].Value);
    }

    [Fact]
    public void Storey_ObjectTypeIsFamilyAndTypeAndLongNameTheNameInCurrent()
    {
        string ifc = Export(IfcProfile.Current, Level(), LevelType(), Wall(100, 0, 0, 4, 0));

        Assert.Matches(@"'Ground','','Level:8mm Head',#\d+,\$,'Ground',\.ELEMENT\.,", Entity(ifc, "IFCBUILDINGSTOREY"));
    }

    [Fact]
    public void Storey_LegacyKeepsObjectTypeAndLongNameUnset()
    {
        string ifc = Export(IfcProfile.Legacy, Level(), LevelType(), Wall(100, 0, 0, 4, 0));

        Assert.Matches(@"'Ground',\$,\$,#\d+,\$,\$,\.ELEMENT\.,", Entity(ifc, "IFCBUILDINGSTOREY"));
    }

    [Fact]
    public void Storey_WithoutAFamilyLabelHasNoObjectType()
    {
        string ifc = Export(IfcProfile.Current, Level(familyLabel: null), LevelType(), Wall(100, 0, 0, 4, 0));

        Assert.Matches(@"'Ground','',\$,#\d+,\$,'Ground',\.ELEMENT\.,", Entity(ifc, "IFCBUILDINGSTOREY"));
    }

    [Fact]
    public void Site_EmptyLongNameAndLandTitleNumberInCurrent_UnsetInLegacy()
    {
        string current = Entity(Export(IfcProfile.Current, Level(), LevelType(), Wall(100, 0, 0, 4, 0)), "IFCSITE");
        string legacy = Entity(Export(IfcProfile.Legacy, Level(), LevelType(), Wall(100, 0, 0, 4, 0)), "IFCSITE");

        Assert.Matches(@"'Default','','',#\d+,\$,'',\.ELEMENT\.,\$,\$,\$,'',\$", current);
        Assert.Matches(@"'Default',\$,\$,#\d+,\$,\$,\.ELEMENT\.,\$,\$,\$,\$,\$", legacy);
    }

    [Fact]
    public void Relations_HaveEmptyNameAndDescriptionInCurrent()
    {
        string ifc = Export(IfcProfile.Current, Level(), LevelType(), Wall(100, 0, 0, 4, 0));

        Assert.Matches(@"IFCRELAGGREGATES\('[^']{22}',#\d+,'','',#\d+,\(#\d+\)\)", ifc);
        Assert.Matches(@"IFCRELCONTAINEDINSPATIALSTRUCTURE\('[^']{22}',#\d+,'','',", ifc);
        Assert.DoesNotMatch(@"IFCRELAGGREGATES\('[^']{22}',#\d+,\$", ifc);
        Assert.Matches(@"IFCBUILDING\('[^']{22}',#\d+,'[^']*','','',", ifc);
        Assert.Matches(@"IFCPRODUCTDEFINITIONSHAPE\('','',", ifc);
    }

    [Fact]
    public void Relations_LegacyKeepsUnsetNameAndDescription()
    {
        string ifc = Export(IfcProfile.Legacy, Level(), LevelType(), Wall(100, 0, 0, 4, 0));

        Assert.Matches(@"IFCRELAGGREGATES\('[^']{22}',#\d+,\$,\$,#\d+,\(#\d+\)\)", ifc);
        Assert.DoesNotMatch(@"IFCRELAGGREGATES\('[^']{22}',#\d+,''", ifc);
    }

    [Fact]
    public void WallPathConnections_AreNotWrittenInCurrent_ButAreInLegacy()
    {
        Element first = Wall(100, 0, 0, 4, 0, 101);
        Element second = Wall(101, 4, 0, 4, 4);

        string current = Export(IfcProfile.Current, Level(), LevelType(), first, second);
        string legacy = Export(IfcProfile.Legacy, Level(), LevelType(), Wall(100, 0, 0, 4, 0, 101), Wall(101, 4, 0, 4, 4));

        Assert.DoesNotContain("IFCRELCONNECTSPATHELEMENTS", current);
        Assert.Contains("IFCRELCONNECTSPATHELEMENTS", legacy);
    }
}
