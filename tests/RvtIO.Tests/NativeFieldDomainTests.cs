using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NativeFieldDomainTests
{
    private static Parameter P(int id, StorageType storage, object value) => BipHelpers.MakeBipParam(id, id.ToString(), storage, value);
    private static object? Value(Element e, int id) => e.Parameters.FirstOrDefault(p => p.Id == id)?.Value;

    private static Dictionary<uint, ushort> Classes(params (uint Id, ushort Class)[] items) => items.ToDictionary(i => i.Id, i => i.Class);

    [Fact]
    public void ParameterCreatedByTheTable_IsRemovedOutsideItsDomains_AndKeptInsideThem()
    {
        var inside = new Element { Id = 1, CategoryId = -2000011 };
        inside.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        inside.NativeTableAddedParams = [(-1006000, ["10/-2000011"], StorageType.Integer, 5, false)];
        inside.NativeTableCreatedParams = [-1006000];
        var wrongCategory = new Element { Id = 2, CategoryId = -2000023 };
        wrongCategory.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        wrongCategory.NativeTableAddedParams = [(-1006000, ["10/-2000011"], StorageType.Integer, 5, false)];
        wrongCategory.NativeTableCreatedParams = [-1006000];
        var classWide = new Element { Id = 3, CategoryId = -2000023 };
        classWide.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        classWide.NativeTableAddedParams = [(-1006000, ["10"], StorageType.Integer, 5, false)];
        classWide.NativeTableCreatedParams = [-1006000];
        var info = new RevitFileInfo { Elements = [inside, wrongCategory, classWide] };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(info, Classes((1, 10), (2, 10), (3, 10)));

        Assert.Equal(5, Value(inside, -1006000));
        Assert.Null(Value(wrongCategory, -1006000));
        Assert.Equal(5, Value(classWide, -1006000));
        Assert.Null(inside.NativeTableAddedParams);
    }

    [Fact]
    public void OverwrittenValue_IsGivenBack_WhenNoRowAppliesToTheFinalDomain()
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "wrong"));
        element.NativeTableAddedParams = [(-1006001, ["10/-2000011"], StorageType.String, "wrong", false)];
        element.NativeTablePrevious = new() { [-1006001] = ("original", StorageType.String, false) };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("original", Value(element, -1006001));
    }

    [Fact]
    public void LaterPassValue_IsNotTouched()
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "decided later"));
        element.NativeTableAddedParams = [(-1006001, ["10/-2000011"], StorageType.String, "table", false)];
        element.NativeTablePrevious = new() { [-1006001] = ("original", StorageType.String, false) };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("decided later", Value(element, -1006001));
    }

    [Fact]
    public void InDomainRow_WinsOverALaterOutOfDomainRow()
    {
        var element = new Element { Id = 1, CategoryId = -2000011 };
        element.Parameters.Add(P(-1006002, StorageType.String, ""));
        element.NativeTableAddedParams =
        [
            (-1006002, ["10/-2000011"], StorageType.String, "450x200", false),
            (-1006002, ["10/-2000016"], StorageType.String, "", false),
        ];

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("450x200", Value(element, -1006002));
    }

    [Fact]
    public void NegativeElementIdOptionInNativeFieldTable_IsKeptInDomain_AndPrunedOutsideDomain()
    {
        var inDomain = new Element { Id = 10, CategoryId = -2000150 };
        var outOfDomain = new Element { Id = 11, CategoryId = -2000575 };
        var npIn = new TfBlockDeserializer.NativeParams();
        npIn.Fields["174:m_centerlineTickMarkStyleId"] = -2;
        var npOut = new TfBlockDeserializer.NativeParams();
        npOut.Fields["174:m_centerlineTickMarkStyleId"] = -2;

        var state = new ParsingState();
        state.ElemClassIdx[10] = 174;
        state.ElemClassIdx[11] = 174;
        state.NativeParams[10] = npIn;
        state.NativeParams[11] = npOut;

        var info = new RevitFileInfo { Elements = [inDomain, outOfDomain] };
        ElementBuilder.PostProcess(info, state);

        var keptParam = Assert.Single(inDomain.Parameters, p => p.Id == -1006445);
        Assert.Equal(-2, keptParam.Value);
        Assert.True(keptParam.HasRevitValue);

        Assert.DoesNotContain(outOfDomain.Parameters, p => p.Id == -1006445);
        string outJson = JsonSerializer.Serialize(new RevitFileInfo { Elements = [outOfDomain] });
        Assert.DoesNotContain("-1006445", outJson);
    }

    [Fact]
    public void UnsetClassifiedNonMinusOneFieldInNativeFieldTable_StillChecksDomains()
    {
        var inDomainTickZero = new Element { Id = 20, CategoryId = -2000150 };
        var outOfDomainTickZero = new Element { Id = 21, CategoryId = -2000575 };
        var inDomainHostMinusFour = new Element { Id = 22, CategoryId = -2000014 };
        var outOfDomainHostMinusFour = new Element { Id = 23, CategoryId = -2001330 };

        var np20 = new TfBlockDeserializer.NativeParams();
        np20.Fields["174:m_centerlineTickMarkStyleId"] = 0;
        var np21 = new TfBlockDeserializer.NativeParams();
        np21.Fields["174:m_centerlineTickMarkStyleId"] = 0;
        var np22 = new TfBlockDeserializer.NativeParams();
        np22.Fields["1795:m_hostId"] = -4;
        var np23 = new TfBlockDeserializer.NativeParams();
        np23.Fields["1795:m_hostId"] = -4;

        var state = new ParsingState();
        state.ElemClassIdx[20] = 174;
        state.ElemClassIdx[21] = 174;
        state.ElemClassIdx[22] = 1794;
        state.ElemClassIdx[23] = 1794;
        state.NativeParams[20] = np20;
        state.NativeParams[21] = np21;
        state.NativeParams[22] = np22;
        state.NativeParams[23] = np23;

        var info = new RevitFileInfo
        {
            Elements = [inDomainTickZero, outOfDomainTickZero, inDomainHostMinusFour, outOfDomainHostMinusFour],
        };
        ElementBuilder.PostProcess(info, state);

        var tickInDomain = Assert.Single(inDomainTickZero.Parameters, p => p.Id == -1006445);
        Assert.Equal(0, tickInDomain.Value);
        Assert.True(tickInDomain.HasValue);
        Assert.False(tickInDomain.HasRevitValue);

        Assert.DoesNotContain(outOfDomainTickZero.Parameters, p => p.Id == -1006445);
        string outTickJson = JsonSerializer.Serialize(new RevitFileInfo { Elements = [outOfDomainTickZero] });
        Assert.DoesNotContain("-1006445", outTickJson);

        var hostInDomain = Assert.Single(inDomainHostMinusFour.Parameters, p => p.Id == -1002108);
        Assert.Equal(-4, hostInDomain.Value);
        Assert.True(hostInDomain.HasValue);
        Assert.False(hostInDomain.HasRevitValue);

        Assert.NotEqual(-4, Value(outOfDomainHostMinusFour, -1002108));
        var hostOutParam = outOfDomainHostMinusFour.Parameters.FirstOrDefault(p => p.Id == -1002108);
        if (hostOutParam != null)
            Assert.False(hostOutParam.HasRevitValue);
    }
}
