using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The Uniformat classification of the current profile: the Assembly Code
/// of an element or its type becomes an IfcClassificationReference that an IfcRelAssociatesClassification attaches to the
/// products and to the type objects the family-instance exporters create.</summary>
public class IfcExporterClassificationCurrentTests
{
    private const string Guid22 = "'3ZGD7y6S5209$mGLi_sPlj'";
    private const int AssemblyCode = -1002500;

    private static Element Typed(int id, int typeId, string? code = null)
    {
        var element = new Element { Id = id, CategoryId = -2000023 };
        element.Parameters.Add(new Parameter { Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" }, StorageType = StorageType.Integer, Value = typeId });
        if (code != null) element.Parameters.Add(new Parameter { Id = AssemblyCode, StorageType = StorageType.String, Value = code });
        return element;
    }

    private static Element Symbol(int id, string? code)
    {
        var element = new Element { Id = id, CategoryId = -2000023 };
        if (code != null) element.Parameters.Add(new Parameter { Id = AssemblyCode, StorageType = StorageType.String, Value = code });
        return element;
    }

    private sealed class Model
    {
        public StepWriter W { get; } = new();
        public Dictionary<int, Element> Elements { get; } = new();
        public Dictionary<int, int> ProductElement { get; } = new();

        public int Product(string ifcClass, Element element, int? typeEntity = null)
        {
            Elements[element.Id] = element;
            int entity = W.Add(ifcClass, $"{Guid22},#5,'x',$,$,#1,$,'{element.Id}'");
            ProductElement[entity] = element.Id;
            if (typeEntity is int type)
                W.Add("IFCRELDEFINESBYTYPE", $"{Guid22},#5,$,$,(#{entity}),#{type}");
            return entity;
        }

        public int Type(string ifcClass, Element typeElement)
        {
            Elements[typeElement.Id] = typeElement;
            return W.Add(ifcClass, $"{Guid22},#5,'t',$,$,$,$,'1',$");
        }

        public int Run() => IfcExporter.ApplyCurrentClassifications(W, 5, Elements, ProductElement);

        public List<(string Type, string Args)> Entities(string type)
        {
            var list = new List<(string, string)>();
            for (int i = 0; i < W.EntityCount; i++)
                if (W.EntityAt(i).Type == type) list.Add((type, W.EntityAt(i).Args));
            return list;
        }
    }

    [Fact]
    public void ProductAndItsTypeObject_ShareOneReferenceOfTheTypesCode()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, "C1020");
        int type = m.Type("IFCDOORSTYLE", Symbol(200, "C1020"));
        int door = m.Product("IFCDOOR", Typed(100, 200), type);

        Assert.Equal(1, m.Run());

        Assert.Equal("'https://www.csiresources.org/standards/uniformat','1998',$,'Uniformat'", Assert.Single(m.Entities("IFCCLASSIFICATION")).Args);
        Assert.Equal("'https://www.csiresources.org/standards/uniformat','C1020','Uniformat',$", Assert.Single(m.Entities("IFCCLASSIFICATIONREFERENCE")).Args);
        string relation = Assert.Single(m.Entities("IFCRELASSOCIATESCLASSIFICATION")).Args;
        // the members are the type object and the product, in entity order
        Assert.Matches(@"^'[^']{22}',#5,'Uniformat:C1020','',\(#\d+,#\d+\),#\d+$", relation);
        Assert.Contains($"(#{Math.Min(door, type)},#{Math.Max(door, type)})", relation);
    }

    [Fact]
    public void AnElementsOwnCodeWinsOverItsTypes_AndEmptyCodesMakeNoReference()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, "B10");
        m.Product("IFCCOLUMN", Typed(100, 200, code: "A1"));
        m.Product("IFCCOLUMN", Typed(101, 200));
        m.Elements[300] = Symbol(300, "");
        m.Product("IFCCOLUMN", Typed(102, 300));
        m.Product("IFCCOLUMN", Typed(103, 999));

        Assert.Equal(2, m.Run());

        Assert.Equal(["Uniformat:A1", "Uniformat:B10"],
            m.Entities("IFCRELASSOCIATESCLASSIFICATION").Select(r => Regex.Match(r.Args, "'(Uniformat:[^']*)'").Groups[1].Value).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ABlankCodeIsACode_ButAnEmptyOneIsNot()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, " ");
        m.Product("IFCFLOWTERMINAL", Typed(100, 200));

        Assert.Equal(1, m.Run());

        Assert.Contains("'Uniformat: '", Assert.Single(m.Entities("IFCRELASSOCIATESCLASSIFICATION")).Args);
        Assert.Equal("'https://www.csiresources.org/standards/uniformat',' ','Uniformat',$", Assert.Single(m.Entities("IFCCLASSIFICATIONREFERENCE")).Args);
    }

    [Fact]
    public void HostObjectTypes_AreNotClassified_ButTheirProductsAre()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, "C1010145");
        int wallType = m.Type("IFCWALLTYPE", Symbol(200, "C1010145"));
        int wall = m.Product("IFCWALL", Typed(100, 200), wallType);

        Assert.Equal(1, m.Run());

        string relation = Assert.Single(m.Entities("IFCRELASSOCIATESCLASSIFICATION")).Args;
        Assert.Contains($",(#{wall}),#", relation);
        Assert.DoesNotContain($"#{wallType}", relation);
    }

    [Fact]
    public void ARailingAggregatedIntoAStair_DoesNotTakeTheStairsCode_ButItsRunsDo()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, "C2010");
        int stair = m.Product("IFCSTAIR", Typed(100, 200));
        int flight = m.Product("IFCSTAIRFLIGHT", Typed(101, 300));
        int railing = m.Product("IFCRAILING", Typed(102, 300));
        int ownCode = m.Product("IFCRAILING", Typed(103, 300, code: "C2020"));
        m.W.Add("IFCRELAGGREGATES", $"{Guid22},#5,'','',#{stair},(#{flight},#{railing},#{ownCode})");

        m.Run();

        var relations = m.Entities("IFCRELASSOCIATESCLASSIFICATION").Select(r => r.Args).ToArray();
        Assert.Contains(relations, r => r.Contains("'Uniformat:C2010'") && r.Contains($"(#{stair},#{flight})"));
        Assert.Contains(relations, r => r.Contains("'Uniformat:C2020'") && r.Contains($"(#{ownCode})"));
        Assert.DoesNotContain(relations, r => r.Contains($"#{railing}"));
    }

    [Fact]
    public void ThePartsOfAnAggregate_ReceiveTheCodeOfTheAggregatesElement()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, "B2020200");
        int wall = m.Product("IFCCURTAINWALL", Typed(100, 200));
        int panel = m.Product("IFCPLATE", Typed(101, 300));
        int mullion = m.Product("IFCMEMBER", Typed(102, 300));
        m.W.Add("IFCRELAGGREGATES", $"{Guid22},#5,'','',#{wall},(#{panel},#{mullion})");

        m.Run();

        Assert.Contains($"(#{wall},#{panel},#{mullion})", Assert.Single(m.Entities("IFCRELASSOCIATESCLASSIFICATION")).Args);
    }

    [Fact]
    public void SpacesAndOpenings_AreNoClassifiedElements()
    {
        var m = new Model();
        m.Elements[200] = Symbol(200, "A1");
        m.Product("IFCSPACE", Typed(100, 200));
        m.Product("IFCOPENINGELEMENT", Typed(101, 200));

        Assert.Equal(0, m.Run());

        Assert.Empty(m.Entities("IFCRELASSOCIATESCLASSIFICATION"));
        // the classification itself exists as soon as a product does
        Assert.Single(m.Entities("IFCCLASSIFICATION"));
    }

    [Fact]
    public void NoProducts_NoClassification()
    {
        var m = new Model();

        Assert.Equal(0, m.Run());

        Assert.Empty(m.Entities("IFCCLASSIFICATION"));
    }

    [Theory]
    [InlineData("6.2.2.А2.2.3", "'6.2.2.\\X\\C02.2.3'")]       // sample project A: the Cyrillic A is the Windows-1251 byte C0
    [InlineData("D2010410", "'D2010410'")]
    [InlineData("It's a \\ code", "'It''s a \\\\ code'")]
    public void ReferenceText_IsWrittenAsAnsiBytes(string text, string expected) =>
        Assert.Equal(expected, IfcExporter.AnsiStepStringCurrent(text));

    private static Element Beam(int id, string code)
    {
        double[][] points =
        [
            [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new Element
        {
            Id = id,
            TypeId = 5001,
            SuperInstanceId = 999,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "300x460",
            FamilyName = "Classification test family",
            References = [3],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter { Id = AssemblyCode, StorageType = StorageType.String, Value = code },
            ],
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Geometry = new GeometryData
            {
                BoundingBox = [0, 0, 0, 4, 3, 2],
                Faces = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(index => points[index]).ToList() }).ToList(),
                BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
            },
        };
    }

    private static string ExportBeam(IfcProfile version)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Beam(5000, "B1020"));
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-class-beam-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExportCurrent_AssociatesTheBeamWithItsAssemblyCode()
    {
        string ifc = ExportBeam(IfcProfile.Current);

        string beam = Regex.Match(ifc, @"^#(\d+)=IFCBEAM\(", RegexOptions.Multiline).Groups[1].Value;
        Assert.Matches(@"=IFCCLASSIFICATION\('https://www\.csiresources\.org/standards/uniformat','1998',\$,'Uniformat'\);", ifc);
        Assert.Matches(@"=IFCCLASSIFICATIONREFERENCE\('https://www\.csiresources\.org/standards/uniformat','B1020','Uniformat',\$\);", ifc);
        Assert.Matches($@"=IFCRELASSOCIATESCLASSIFICATION\('[^']{{22}}',#\d+,'Uniformat:B1020','',\(#{beam}\),#\d+\);", ifc);
    }

    [Fact]
    public void ExportLegacy_WritesNoClassification()
    {
        Assert.DoesNotContain("IFCCLASSIFICATION", ExportBeam(IfcProfile.Legacy));
    }

    [Fact]
    public void Export_WithoutProducts_WritesNoClassification_InEitherVersion()
    {
        string Export(IfcProfile version)
        {
            var info = new RevitFileInfo();
            info.Elements.Add(new Element { Id = 10, CategoryId = -2000240, Category = "OST_Levels", Name = "Level 1" });
            string path = Path.Combine(Path.GetTempPath(), $"revitreader-class-{Guid.NewGuid():N}.ifc");
            try
            {
                new IfcExporter { Profile = version }.Export(info, path);
                return File.ReadAllText(path);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        Assert.DoesNotContain("IFCCLASSIFICATION", Export(IfcProfile.Legacy));
        // a model without products has no classification either
        Assert.DoesNotContain("IFCCLASSIFICATION", Export(IfcProfile.Current));
    }
}
