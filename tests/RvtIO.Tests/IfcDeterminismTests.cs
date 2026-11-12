using System.Diagnostics;
using System.Globalization;
using System.Text;
using RvtIO.Cli;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Fixed-timestamp IFC header (<see cref="IfcExporter.Timestamp"/> and <c>--ifc-timestamp</c>) and byte-for-byte
/// deterministic IFC exports across repeated runs.</summary>
[Collection("ElemTableStaticMaps")]
public class IfcDeterminismTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 1, 15, 12, 30, 45, TimeSpan.Zero);

    [Fact]
    public void IfcTimestampOption_WithoutOption_ReturnsNull()
    {
        Assert.True(IfcTimestampOption.TryParse(["project.rvt", "project.ifc"], out var timestamp, out string? error));
        Assert.Null(timestamp);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("2026-01-15T12:30:45Z", "2026-01-15T12:30:45")]
    [InlineData("2026-01-15T14:30:45+02:00", "2026-01-15T12:30:45")]
    [InlineData("2026-01-15T12:30:45", "2026-01-15T12:30:45")]
    [InlineData("2026-01-15 12:30:45Z", "2026-01-15T12:30:45")]
    [InlineData("2026-01-15", "2026-01-15T00:00:00")]
    [InlineData("1768480245", "2026-01-15T12:30:45")]
    public void IfcTimestampOption_ValidFormats_NormalizeToUtc(string input, string expectedUtcText)
    {
        Assert.True(IfcTimestampOption.TryParse(["project.rvt", "project.ifc", "--ifc-timestamp", input], out var timestamp, out string? error));
        Assert.Null(error);
        Assert.NotNull(timestamp);
        Assert.Equal(expectedUtcText, timestamp.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
    }

    public static IEnumerable<object[]> InvalidTimestampArguments()
    {
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-timestamp" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-timestamp", "--ifc-revit-parameters" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-timestamp", "not-a-date" }];
        yield return [new[] { "project.rvt", "project.ifc", "--ifc-timestamp", "2026-01-15T12:30:45Z", "--ifc-timestamp", "2026-01-15T12:30:45Z" }];
    }

    [Theory]
    [MemberData(nameof(InvalidTimestampArguments))]
    public void IfcTimestampOption_InvalidOrDuplicateInput_FailsWithMessage(string[] args)
    {
        Assert.False(IfcTimestampOption.TryParse(args, out var timestamp, out string? error));
        Assert.Null(timestamp);
        Assert.NotNull(error);
        Assert.Contains("--ifc-timestamp", error);
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void RepeatedExportsWithFixedTimestamp_AreByteForByteIdentical(IfcProfile profile)
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-ifc-det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string firstPath = Path.Combine(directory, "project.ifc");
            string secondDir = Path.Combine(directory, "second");
            Directory.CreateDirectory(secondDir);
            string secondPath = Path.Combine(secondDir, "project.ifc");

            var exporter1 = new IfcExporter(includeRevitPropertySets: true)
            {
                Profile = profile,
                RevitParameterGroups = true,
                Timestamp = FixedUtc,
            };
            var exporter2 = new IfcExporter(includeRevitPropertySets: true)
            {
                Profile = profile,
                RevitParameterGroups = true,
                Timestamp = new DateTimeOffset(2026, 1, 15, 14, 30, 45, TimeSpan.FromHours(2)),
            };

            exporter1.Export(BuildMultiElementModel(), firstPath);
            exporter2.Export(BuildMultiElementModel(), secondPath);

            byte[] firstBytes = File.ReadAllBytes(firstPath);
            byte[] secondBytes = File.ReadAllBytes(secondPath);
            Assert.Equal(firstBytes, secondBytes);

            string text = Encoding.ASCII.GetString(firstBytes);
            Assert.Contains("FILE_NAME('output.ifc','2026-01-15T12:30:45',", text);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CliExportWithIfcTimestamp_ProducesByteIdenticalOutputAcrossRunsAndRejectsJsonOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-cli-det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rvt = Path.Combine(directory, "synthetic.rvt");
            WriteSyntheticRvt(rvt);

            string jsonOut = Path.Combine(directory, "out.json");
            var (jsonExit, jsonErr) = RunCli(rvt, jsonOut, "--ifc-timestamp", "2026-01-15T12:30:45Z");
            Assert.Equal(RvtExitCodes.Usage, jsonExit);
            Assert.Contains("--ifc-timestamp requires IFC output.", jsonErr);
            Assert.False(File.Exists(jsonOut));

            string dirA = Path.Combine(directory, "a");
            string dirB = Path.Combine(directory, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            string ifcA = Path.Combine(dirA, "export.ifc");
            string ifcB = Path.Combine(dirB, "export.ifc");

            var (exitA, _) = RunCli(rvt, ifcA, "--ifc-timestamp", "2026-01-15T12:30:45Z");
            var (exitB, _) = RunCli(rvt, ifcB, "--ifc-timestamp", "2026-01-15T14:30:45+02:00");
            Assert.Equal(RvtExitCodes.Success, exitA);
            Assert.Equal(RvtExitCodes.Success, exitB);

            byte[] bytesA = File.ReadAllBytes(ifcA);
            byte[] bytesB = File.ReadAllBytes(ifcB);
            Assert.Equal(bytesA, bytesB);
            Assert.Contains("'2026-01-15T12:30:45'", Encoding.ASCII.GetString(bytesA));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SkippableFact]
    public void RealFixtureRepeatedExportWithFixedTimestamp_IsByteForByteIdentical()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-fixture-det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string dirA = Path.Combine(directory, "a");
            string dirB = Path.Combine(directory, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            string outA = Path.Combine(dirA, "model.ifc");
            string outB = Path.Combine(dirB, "model.ifc");

            var infoA = new RvtFileReader(new RvtReaderOptions { R22Layout = true, SystemFamilyNameLocale = "ENU", SeparateFacesWithDistinctOpenings = true }).Read(path);
            var infoB = new RvtFileReader(new RvtReaderOptions { R22Layout = true, SystemFamilyNameLocale = "ENU", SeparateFacesWithDistinctOpenings = true }).Read(path);

            new IfcExporter { Profile = IfcProfile.Current, Timestamp = FixedUtc }.Export(infoA, outA);
            new IfcExporter { Profile = IfcProfile.Current, Timestamp = FixedUtc }.Export(infoB, outB);

            Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (int ExitCode, string StdErr) RunCli(params string[] arguments)
    {
        var start = new ProcessStartInfo(CliExitCodeTests.DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stderr.Result);
    }

    internal static void WriteSyntheticRvt(string path)
    {
        using var elemTable = new MemoryStream();
        using (var writer = new BinaryWriter(elemTable))
        {
            writer.Write((ushort)1312);
            writer.Write(2);
            writer.Write(1001); writer.Write(100); writer.Write(64);
            writer.Write(523); writer.Write(999); writer.Write(7); writer.Write(42);
            writer.Write(1002); writer.Write(101); writer.Write(65);
            writer.Write(523); writer.Write(999); writer.Write(8); writer.Write(43);
        }
        byte[] partition1 = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(false,
                CoverageCensusTests.R(1001, CoverageCensusTests.Body("Plain", 5, 0, 0, 0)),
                CoverageCensusTests.R(1002, CoverageCensusTests.Body("Store", 9, 0, 0, 0))));
        byte[] partition2 = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(true,
                CoverageCensusTests.R(1002, CoverageCensusTests.Body("Listed", 2, 0, 0, 0, 11, 0, 0, 0, 22, 0, 0, 0))));
        CoverageCensusTests.WriteFile(path,
            ("Global/ElemTable", PageCodec.EncodePadded(GzipMember.Write(elemTable.ToArray()))),
            ("Global/History", PageCodec.EncodePadded(GzipMember.Write(new byte[64]))),
            ("Formats/Latest", PageCodec.EncodePadded(GzipMember.Write(CoverageCensusTests.TableBytes))),
            ("Partitions/1", partition1),
            ("Partitions/2", partition2));
    }

    private static RevitFileInfo BuildMultiElementModel()
    {
        const int phaseCreatedId = -1012100;
        const int elemTypeId = -1002050;
        const int levelParamId = -1001952;

        var material = new Element
        {
            Id = 700,
            IsDiscovered = true,
            CategoryId = -2000700,
            Category = "OST_Materials",
            Name = "Concrete C30",
            HasShadingAppearance = true,
        };

        var level0 = new Element
        {
            Id = 10,
            IsDiscovered = true,
            CategoryId = -2000240,
            Category = "OST_Levels",
            Name = "Level 0",
            Parameters =
            [
                new Parameter { Id = -1007102, Definition = new Definition { Id = -1007102, Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 0.0 },
                new Parameter { Id = -1007103, Definition = new Definition { Id = -1007103, Name = "LEVEL_IS_BUILDING_STORY" }, StorageType = StorageType.Integer, Value = 1 },
            ],
        };

        var wallType = new Element
        {
            Id = 500,
            IsDiscovered = true,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Exterior 300",
            FamilyName = "Basic Wall",
            SerializedBasicWallTypeFunction = 1,
            Layers =
            [
                new MaterialLayer { LayerIndex = 0, Function = "Structure", ThicknessMm = 300.0, MaterialId = 700, MaterialName = "Concrete C30" },
            ],
        };

        var doorType = new Element
        {
            Id = 510,
            IsDiscovered = true,
            CategoryId = -2000023,
            Category = "OST_Doors",
            Name = "Single 900x2100",
            FamilyName = "Single-Flush",
            Parameters =
            [
                new Parameter { Id = -1001301, Definition = new Definition { Id = -1001301, Name = "DOOR_WIDTH" }, StorageType = StorageType.Double, Value = 2.952755905511811 },
                new Parameter { Id = -1001300, Definition = new Definition { Id = -1001300, Name = "DOOR_HEIGHT" }, StorageType = StorageType.Double, Value = 6.889763779527559 },
            ],
        };

        var wallA = PrismElement(100, -2000011, "OST_Walls", "Basic Wall:Exterior 300:100", 500, 10, 0, 20, 1, 10, 700, "W-01");
        var wallB = PrismElement(101, -2000011, "OST_Walls", "Basic Wall:Exterior 300:101", 500, 10, 25, 20, 1, 10, 700, "W-02");
        var door = PrismElement(200, -2000023, "OST_Doors", "Single-Flush:Single 900x2100:200", 510, 10, 5, 3, 1, 7, 700, "D-01");
        door.SuperInstanceId = 100;
        door.NativeMasterSymbolId = 510;

        static Element PrismElement(int id, int categoryId, string category, string name, int typeId, int levelId,
            double x0, double dx, double dy, double dz, int renderStyleId, string mark)
        {
            double[][] pts =
            [
                [x0, 0, 0], [x0 + dx, 0, 0], [x0 + dx, dy, 0], [x0, dy, 0],
                [x0, 0, dz], [x0 + dx, 0, dz], [x0 + dx, dy, dz], [x0, dy, dz],
            ];
            int[][] faces =
            [
                [0, 3, 2, 1], [4, 5, 6, 7],
                [0, 1, 5, 4], [1, 2, 6, 5],
                [2, 3, 7, 6], [3, 0, 4, 7],
            ];
            return new Element
            {
                Id = id,
                IsDiscovered = true,
                CategoryId = categoryId,
                Category = category,
                Name = name,
                References = [3, levelId, typeId],
                BoundingBox = [x0, 0, 0, x0 + dx, dy, dz],
                Geometry = new GeometryData
                {
                    BoundingBox = [x0, 0, 0, x0 + dx, dy, dz],
                    Faces = faces.Select(idx => new GeoFace
                    {
                        RenderStyleId = renderStyleId,
                        OuterVertices = idx.Select(k => pts[k]).ToList(),
                    }).ToList(),
                    BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
                },
                Parameters =
                [
                    new Parameter { Id = phaseCreatedId, StorageType = StorageType.ElementId, Value = 3 },
                    new Parameter { Id = elemTypeId, Definition = new Definition { Id = elemTypeId, Name = "ELEM_TYPE_PARAM" }, StorageType = StorageType.ElementId, Value = typeId },
                    new Parameter { Id = levelParamId, Definition = new Definition { Id = levelParamId, Name = "FAMILY_LEVEL_PARAM" }, StorageType = StorageType.ElementId, Value = levelId },
                    new Parameter { Id = -1001012, Definition = new Definition { Id = -1001012, Name = "ALL_MODEL_MARK" }, StorageType = StorageType.String, Value = mark },
                ],
            };
        }

        var info = new RevitFileInfo
        {
            SerializedProjectPhaseIds = [3],
            SerializedDefaultExportPhaseId = 3,
        };
        info.Elements.AddRange([material, level0, wallType, doorType, wallA, wallB, door]);
        return info;
    }
}
