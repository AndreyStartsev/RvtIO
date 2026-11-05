using RvtIO.Lookup;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The PE string table reader against images built in the tests; no external file is needed.</summary>
public class BuiltInParameterCaptionResourceTests
{
    [Theory]
    [InlineData("ru-RU", 1049, 1033)]
    [InlineData("en-US", 1033, 1049)]
    public void SyntheticImageDoesNotSubstituteOtherLanguage(string culture, ushort language, ushort other) =>
        Assert.Empty(PeStringTable.Read(SyntheticPe.Build(language, RevitUiNameTable.Get(culture, "Revit.dll")!), other)!);

    [Theory]
    [InlineData("ru-RU", 1049)]
    [InlineData("en-US", 1033)]
    public void SyntheticTruncatedImageIsRejected(string culture, ushort language) =>
        Assert.Null(PeStringTable.Read(SyntheticPe.Build(language, RevitUiNameTable.Get(culture, "Revit.dll")!)[..0x200], language));
}
