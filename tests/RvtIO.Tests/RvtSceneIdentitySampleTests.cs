using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The golden identity test of <see cref="RvtSceneReader"/>: the model key and the source ids it writes for a private
/// project file. They are what a scene's consumers store (with the key, the Revit UniqueId of each element), so a change here
/// is a breaking release of RvtIO.Scene.</summary>
public class RvtSceneIdentitySampleTests
{
    [SkippableFact]
    public void KeyAndSourceIdsOfTheMainProjectFile()
    {
        var result = RvtSceneReader.Read(TestFiles.Find("TESTFILE.rvt"));
        var document = result.Document;

        Assert.Equal("TESTFILE.rvt", Assert.Single(document.Models).Key);
        Assert.Equal(28, document.Elements.Count);
        var sourceIds = document.Elements.ToDictionary(element => element.Id, element => element.SourceId);
        Assert.Equal("d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405a0", sourceIds[263584]);
        Assert.Equal("d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405c7", sourceIds[263623]);
        // Every source id is the Revit UniqueId: the episode GUID and the element id in hex.
        Assert.All(document.Elements, element => Assert.EndsWith($"-{element.Id:x8}", element.SourceId));
        Assert.Empty(document.Validate());
        Assert.Empty(result.Diagnostics);
    }
}
