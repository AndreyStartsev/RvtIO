using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Product and project names as revit-ifc writes them.
/// </summary>
public class ProductNameTests
{
    private static Parameter TypeLink(int typeId) => new()
    {
        Id = -1002050,
        Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId,
        Value = typeId,
    };

    [Fact]
    public void Project_IsNamedByItsNumberWithTheNameAsLongNameAndTheStatusAsPhase()
    {
        // Test_2022: IFCPROJECT(...,'001-00',$,$,'Sample House','Project Status',...).
        var info = new RevitFileInfo
        {
            ProjectInfo = new ProjectInfo { Number = "001-00", Name = "Sample House", Status = "Project Status" },
        };
        Assert.Equal(("001-00", "Sample House", "Project Status"), IfcExporter.ResolveProjectAttributes(info));
        info.ProjectInfo = new ProjectInfo { Name = "Sample House" };
        Assert.Equal(("Sample House", (string?)null, (string?)null), IfcExporter.ResolveProjectAttributes(info));
    }

    [Fact]
    public void FamilyInstance_TakesItsTypesOwnFamilyAndTypeName()
    {
        // The general fields carry a guessed sibling's name; the type's own name-table chain
        // ("{family}:{type}") is what the naming rule reads.
        var type = new Element { Id = 20, CategoryId = -2000023, ValidatedIfcTypeObjectName = "Door family:Door type" };
        var door = new Element { Id = 10, CategoryId = -2000023, TypeId = 20, Name = "Sibling", FamilyName = "Sibling family" };
        door.Parameters.Add(TypeLink(20));
        var elements = new[] { type, door }.ToDictionary(e => e.Id);
        var counts = new Dictionary<int, int> { [10] = 1, [20] = 1 };
        Assert.Equal("Door family:Door type",
            IfcExporter.ResolveProductFamilyAndTypeName(door, "IFCDOOR", elements, counts));

        // A type link that disagrees with the element's TypeId confirms nothing.
        door.TypeId = 21;
        Assert.Equal("Sibling family:Sibling",
            IfcExporter.ResolveProductFamilyAndTypeName(door, "IFCDOOR", elements, counts));
    }

    [Fact]
    public void FloorWithAnExplicitReference_KeepsItsGeneralTypeName()
    {
        var slab = new Element
        {
            Id = 10, CategoryId = -2000032, Name = "General", FamilyName = "Floor",
            ValidatedFloorTypeReferenceName = "Authored Reference", ValidatedFloorTypeReferenceIsExplicit = true,
        };
        var elements = new Dictionary<int, Element> { [10] = slab };
        Assert.Equal("Floor:General", IfcExporter.ResolveProductFamilyAndTypeName(
            slab, "IFCSLAB", elements, new Dictionary<int, int> { [10] = 1 }));
        slab.ValidatedFloorTypeReferenceIsExplicit = false;
        slab.ValidatedFloorTypeReferenceName = "Floor type";
        Assert.Equal("Floor:Floor type", IfcExporter.ResolveProductFamilyAndTypeName(
            slab, "IFCSLAB", elements, new Dictionary<int, int> { [10] = 1 }));
    }

    [Theory]
    [InlineData(-2000181, "Wall Sweep")]
    [InlineData(-2001392, "Slab Edge")]
    [InlineData(-2001390, "Fascia")]
    [InlineData(-2001391, "Gutter")]
    [InlineData(-2001393, "Compound Ceiling")]
    public void SweepLikeElementWithoutAFamily_TakesTheFixedSystemFamilyName(int categoryId, string family)
    {
        var element = new Element { Id = 10, CategoryId = categoryId, Category = "OST_Placeholder", Name = "Profile A", TypeId = 20 };
        var elements = new Dictionary<int, Element> { [10] = element };
        var counts = new Dictionary<int, int> { [10] = 1 };
        Assert.Equal(family + ":Profile A", IfcExporter.ResolveProductFamilyAndTypeName(
            element, "IFCBUILDINGELEMENTPROXY", elements, counts));
    }

    [Fact]
    public void SweepLikeElementWithARealFamilyName_KeepsIt()
    {
        var element = new Element { Id = 10, CategoryId = -2001392, Name = "Profile A", FamilyName = "Authored", TypeId = 20 };
        Assert.Equal("Authored:Profile A", IfcExporter.ResolveProductFamilyAndTypeName(
            element, "IFCBUILDINGELEMENTPROXY",
            new Dictionary<int, Element> { [10] = element }, new Dictionary<int, int> { [10] = 1 }));
    }

    [Fact]
    public void OtherCategoryWithoutAFamily_KeepsTheCategoryPrefix()
    {
        var element = new Element { Id = 10, CategoryId = -2000151, Category = "OST_GenericModel", Name = "Thing" };
        Assert.Null(IfcExporter.FixedSystemFamilyName(element));
        Assert.Equal("OST_GenericModel:Thing", IfcExporter.ResolveProductFamilyAndTypeName(
            element, "IFCBUILDINGELEMENTPROXY",
            new Dictionary<int, Element> { [10] = element }, new Dictionary<int, int> { [10] = 1 }));
    }

    [Theory]
    [InlineData(-2000181)]
    [InlineData(-2001392)]
    [InlineData(-2001391)]
    public void UntypedNamelessSweepLikeProxy_HasNoFamilyAndTypePart(int categoryId)
    {
        var untyped = new Element { Id = 10, CategoryId = categoryId, Category = "OST_Placeholder" };
        var elements = new Dictionary<int, Element> { [10] = untyped };
        var counts = new Dictionary<int, int> { [10] = 1 };
        Assert.Equal("", IfcExporter.ResolveProductFamilyAndTypeName(untyped, "IFCBUILDINGELEMENTPROXY", elements, counts));
        // Any name, or another category, keeps the family:type form.
        untyped.Name = "N";
        Assert.EndsWith(":N", IfcExporter.ResolveProductFamilyAndTypeName(untyped, "IFCBUILDINGELEMENTPROXY", elements, counts));
        var other = new Element { Id = 11, CategoryId = -2000151, Category = "OST_GenericModel" };
        Assert.Equal("OST_GenericModel:Element 11", IfcExporter.ResolveProductFamilyAndTypeName(
            other, "IFCBUILDINGELEMENTPROXY", new Dictionary<int, Element> { [11] = other }, new Dictionary<int, int> { [11] = 1 }));
    }
}
