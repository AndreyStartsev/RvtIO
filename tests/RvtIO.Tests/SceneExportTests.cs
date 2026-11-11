using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Tests for Issue #24: browser-ready 3D scene export (<c>rvtio-scene/1</c> JSON and binary glTF 2.0 <c>.glb</c>).</summary>
public class SceneExportTests
{
    private const double FeetToMetres = 0.3048;

    [Fact]
    public void PlanarFaceWithInnerWindowOpening_TriangulatesHoleAndExcludesVoidArea()
    {
        var wall = new Element
        {
            Id = 101,
            UniqueId = "doc-guid-00000065",
            Name = "Exterior Wall",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace
                    {
                        Normal = [0, -1, 0],
                        PlaneSurface = true,
                        // 10 ft wide (X: 0..10) by 10 ft tall (Z: 0..10) at Y = 0 => gross 100 sq ft
                        OuterVertices =
                        [
                            [0, 0, 0],
                            [10, 0, 0],
                            [10, 0, 10],
                            [0, 0, 10],
                        ],
                        // 2 ft wide by 4 ft tall window opening => 8 sq ft hole => net 92 sq ft
                        InnerVertices =
                        [
                            [
                                [4, 0, 3],
                                [4, 0, 7],
                                [6, 0, 7],
                                [6, 0, 3],
                            ],
                        ],
                    },
                ],
            },
        };

        var info = new RevitFileInfo { Elements = [wall] };
        var scene = SceneExporter.Build(info);

        Assert.Single(scene.Elements);
        var mesh = Assert.Single(scene.Meshes);
        Assert.True(mesh.TriangleCount >= 6);

        double expectedAreaSqM = (100.0 - 8.0) * FeetToMetres * FeetToMetres;
        double actualAreaSqM = ComputeMeshTriangleArea(mesh);
        Assert.Equal(expectedAreaSqM, actualAreaSqM, precision: 4);

        // Every vertex normal must be unit length and point along (0, -1, 0)
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var n = new Vector3(mesh.Normals[v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
            Assert.InRange(n.Length(), 0.999f, 1.001f);
            Assert.True(Vector3.Dot(n, new Vector3(0, -1, 0)) > 0.99f);
        }

        // No triangle centroid may fall inside the window opening (X in 4.1..5.9 m*ft, Z in 3.1..6.9 m*ft)
        float minHoleX = (float)(4.1 * FeetToMetres), maxHoleX = (float)(5.9 * FeetToMetres);
        float minHoleZ = (float)(3.1 * FeetToMetres), maxHoleZ = (float)(6.9 * FeetToMetres);
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            int i0 = mesh.Indices[t * 3], i1 = mesh.Indices[t * 3 + 1], i2 = mesh.Indices[t * 3 + 2];
            float cx = (mesh.Positions[i0 * 3] + mesh.Positions[i1 * 3] + mesh.Positions[i2 * 3]) / 3f;
            float cz = (mesh.Positions[i0 * 3 + 2] + mesh.Positions[i1 * 3 + 2] + mesh.Positions[i2 * 3 + 2]) / 3f;
            bool insideOpening = cx > minHoleX && cx < maxHoleX && cz > minHoleZ && cz < maxHoleZ;
            Assert.False(insideOpening, $"Triangle {t} centroid ({cx}, {cz}) fell inside the window opening.");
        }
    }

    [Fact]
    public void SolidFallbackWithRectangularOpening_GeneratesPiercedWallWithReveals()
    {
        var wall = new Element
        {
            Id = 102,
            Name = "Solid Fallback Wall",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Geometry = new GeometryData
            {
                Solids =
                [
                    new GeoSolid
                    {
                        Position = [10, 0, 0],
                        RefDirection = [1, 0, 0],
                        ExtrusionDirection = [0, 0, 1],
                        ProfileWidth = 20,
                        ProfileDepth = 1,
                        ExtrusionHeight = 10,
                        Openings =
                        [
                            new GeoOpening
                            {
                                Position = [0, 0, 3.5],
                                Width = 4,
                                Height = 7,
                            },
                        ],
                    },
                ],
            },
        };

        var scene = SceneExporter.Build(new RevitFileInfo { Elements = [wall] });
        var mesh = Assert.Single(scene.Meshes);
        Assert.True(mesh.TriangleCount >= 16);
        Assert.Equal(0.0, scene.BoundsMin[0], precision: 3);
        Assert.Equal(20.0 * FeetToMetres, scene.BoundsMax[0], precision: 3);
        Assert.Equal(10.0 * FeetToMetres, scene.BoundsMax[2], precision: 3);
    }

    [Fact]
    public void RepeatedFamilyInstances_SharePrototypesAndPreserveOutwardWindingForMirroredTransforms()
    {
        // Canonical family symbol: 2 ft x 3 ft x 4 ft box centered at local (0, 0, 2)
        var symbol = new Element
        {
            Id = 500,
            Name = "Desk 1500x750",
            FamilyName = "Office Desk",
            CategoryId = (int)BuiltInCategory.OST_Furniture,
            Category = "OST_Furniture",
            Geometry = CreateBoxGeometry(-1, -1.5, 0, 1, 1.5, 4),
        };

        var elements = new List<Element> { symbol };
        for (int i = 0; i < 20; i++)
        {
            double angle = (i % 4) * (Math.PI / 4.0);
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            // Odd instances are mirrored across local X (negative determinant!)
            double mirrorSign = (i % 2 == 1) ? -1.0 : 1.0;
            double tx = (i % 5) * 8.0;
            double ty = (i / 5) * 10.0;
            double tz = 0.0;

            // GeoInstance.Transform layout: [r00,r01,r02, r10,r11,r12, r20,r21,r22, tx,ty,tz]
            // where worldX = tx + x*r00 + y*r01 + z*r02
            double[] trf =
            [
                mirrorSign * cos, -sin, 0,
                mirrorSign * sin,  cos, 0,
                0,                 0,   1,
                tx,                ty,  tz,
            ];

            elements.Add(new Element
            {
                Id = 1000 + i,
                TypeId = 500,
                UniqueId = $"desk-guid-{i:D4}",
                Name = $"Desk Instance {i + 1}",
                FamilyName = "Office Desk",
                CategoryId = (int)BuiltInCategory.OST_Furniture,
                Category = "OST_Furniture",
                Geometry = new GeometryData
                {
                    Instances = [new GeoInstance { SymbolId = 500, Transform = trf }],
                },
            });
        }

        var scene = SceneExporter.Build(new RevitFileInfo { Elements = elements });

        Assert.Equal(20, scene.Elements.Count);
        Assert.Equal(20, scene.Completeness.ExportedElements);
        Assert.Equal(20, scene.Completeness.InstancedElements);
        Assert.Equal(20, scene.Instances.Count);

        // Exactly 2 prototypes: 1 normal + 1 mirrored variant (NOT 20 duplicated meshes!)
        Assert.Equal(2, scene.Meshes.Count);
        Assert.Single(scene.Meshes, m => !m.IsMirroredVariant && m.SourceSymbolId == 500);
        Assert.Single(scene.Meshes, m => m.IsMirroredVariant && m.SourceSymbolId == 500);

        // Distinct element identities preserved
        Assert.Equal(20, scene.Elements.Select(e => e.Id).Distinct().Count());
        Assert.Equal(20, scene.Elements.Select(e => e.UniqueId).Distinct().Count());

        // Verify EVERY placed instance (including mirrored ones!) has outward CCW geometric winding AND outward normals
        foreach (var inst in scene.Instances)
        {
            var mesh = scene.Meshes[inst.MeshIndex];
            Matrix4x4 m = ToMatrix4x4(inst.Matrix);
            Assert.True(Matrix4x4.Invert(m, out Matrix4x4 inv));
            Matrix4x4 normalMat = Matrix4x4.Transpose(inv);

            Vector3 worldCenter = Vector3.Transform(new Vector3(0, 0, (float)(2.0 * FeetToMetres)), m);

            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                int i0 = mesh.Indices[t * 3];
                int i1 = mesh.Indices[t * 3 + 1];
                int i2 = mesh.Indices[t * 3 + 2];

                Vector3 p0 = Vector3.Transform(GetVec3(mesh.Positions, i0), m);
                Vector3 p1 = Vector3.Transform(GetVec3(mesh.Positions, i1), m);
                Vector3 p2 = Vector3.Transform(GetVec3(mesh.Positions, i2), m);

                Vector3 triCenter = (p0 + p1 + p2) / 3f;
                Vector3 outwardFromCenter = Vector3.Normalize(triCenter - worldCenter);

                // Geometric CCW winding normal: (p1 - p0) x (p2 - p0) MUST point outward!
                Vector3 ccwFaceNormal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                Assert.True(Vector3.Dot(ccwFaceNormal, outwardFromCenter) > 0.25f,
                    $"Instance for element {inst.ElementId} (mirrored={mesh.IsMirroredVariant}) has inward-facing triangle winding.");

                // Inverse-transpose vertex normal MUST agree with geometric CCW face normal!
                Vector3 n0 = Vector3.Normalize(Vector3.TransformNormal(GetVec3(mesh.Normals, i0), normalMat));
                Assert.True(Vector3.Dot(n0, ccwFaceNormal) > 0.98f,
                    $"Instance for element {inst.ElementId} vertex normal disagrees with CCW face normal.");
            }
        }
    }

    [Fact]
    public void MaterialsAndTransparency_DecodePackedColorAndEmitBlendAlphaInGlb()
    {
        // Revit packed color 0x00BBGGRR: R = 64, G = 160, B = 230
        int packedBgr = (230 << 16) | (160 << 8) | 64;
        var glassMat = new Element
        {
            Id = 300,
            Name = "Architectural Low-E Glass",
            CategoryId = (int)BuiltInCategory.OST_Materials,
            Category = "OST_Materials",
            Parameters =
            [
                MakeIntParam(-1001201, "MATERIAL_PARAM_COLOR", packedBgr),
                MakeIntParam(-1001202, "MATERIAL_PARAM_TRANSPARENCY", 70),
            ],
        };

        var panelGeo = CreateBoxGeometry(0, 0, 0, 4, 0.1, 8, renderStyleId: 300);
        var panel = new Element
        {
            Id = 301,
            Name = "Glazed Curtain Panel",
            CategoryId = (int)BuiltInCategory.OST_CurtainWallPanels,
            Category = "OST_CurtainWallPanels",
            Geometry = panelGeo,
        };

        var scene = SceneExporter.Build(new RevitFileInfo { Elements = [glassMat, panel] });

        var mat = Assert.Single(scene.Materials);
        Assert.Equal(300, mat.RevitMaterialId);
        Assert.Equal("Architectural Low-E Glass", mat.Name);
        Assert.Equal(64f / 255f, mat.Color[0], precision: 2);
        Assert.Equal(160f / 255f, mat.Color[1], precision: 2);
        Assert.Equal(230f / 255f, mat.Color[2], precision: 2);
        Assert.Equal(0.30f, mat.Opacity, precision: 2);

        using var glbStream = new MemoryStream();
        SceneExporter.ExportGlb(scene, glbStream);
        using var gltfDoc = ParseGlbJson(glbStream.ToArray(), out _);
        var gltfMat = gltfDoc.RootElement.GetProperty("materials")[0];
        Assert.Equal("BLEND", gltfMat.GetProperty("alphaMode").GetString());
        var bcf = gltfMat.GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor");
        Assert.Equal(0.30, bcf[3].GetDouble(), precision: 2);
    }

    [Fact]
    public void LargeGeoreferencedCoordinates_ApplyRtcOffsetWithSubMillimetrePrecision()
    {
        // Far survey coordinates: ~152.4 km East, ~365.76 km North, ~259 m Elevation
        const double baseXFeet = 500_000.0;
        const double baseYFeet = 1_200_000.0;
        const double baseZFeet = 850.0;

        var wall = new Element
        {
            Id = 401,
            Name = "Georeferenced Wall",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Geometry = CreateBoxGeometry(baseXFeet, baseYFeet, baseZFeet, baseXFeet + 12.5, baseYFeet + 1.0, baseZFeet + 10.0),
        };

        // Also attach a shared transform shifting +100,000 ft East and +200,000 ft North
        double[] sharedTrf =
        [
            1, 0, 0,
            0, 1, 0,
            0, 0, 1,
            100_000.0, 200_000.0, 50.0,
        ];

        var info = new RevitFileInfo
        {
            SharedTransform = sharedTrf,
            Elements = [wall],
        };

        var scene = SceneExporter.Build(info, new SceneExportOptions { ApplySharedCoordinates = true });

        Assert.True(scene.RtcOffset[0] > 180_000.0);
        Assert.True(scene.RtcOffset[1] > 420_000.0);

        var mesh = Assert.Single(scene.Meshes);
        // Local Float32 positions must stay small around the RTC center (< 20 m)
        foreach (float coord in mesh.Positions)
            Assert.InRange(coord, -20f, 20f);

        // Reconstructed world min/max X in metres must match exact double-precision value within 0.05 mm!
        double exactMinWorldX = (baseXFeet + 100_000.0) * FeetToMetres;
        double exactMaxWorldX = (baseXFeet + 12.5 + 100_000.0) * FeetToMetres;
        double reconstructedMinX = scene.BoundsMin[0] + scene.RtcOffset[0];
        double reconstructedMaxX = scene.BoundsMax[0] + scene.RtcOffset[0];

        Assert.True(Math.Abs(exactMinWorldX - reconstructedMinX) < 0.00005,
            $"World MinX drift {Math.Abs(exactMinWorldX - reconstructedMinX)} m exceeded 0.05 mm.");
        Assert.True(Math.Abs(exactMaxWorldX - reconstructedMaxX) < 0.00005,
            $"World MaxX drift {Math.Abs(exactMaxWorldX - reconstructedMaxX)} m exceeded 0.05 mm.");
    }

    [Fact]
    public void SubsetFiltersAndCallerMetadataControls_FilterElementsParametersGridsAndRooms()
    {
        var level = new Element
        {
            Id = 10,
            Name = "Level 1",
            IsNativeLevel = true,
            CategoryId = (int)BuiltInCategory.OST_Levels,
            Category = "OST_Levels",
        };

        var wallType = new Element
        {
            Id = 20,
            Name = "Generic - 200mm",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Parameters =
            [
                MakeStringParam(9001, "Fire Rating", "REI 120"),
            ],
        };

        var wall = new Element
        {
            Id = 21,
            TypeId = 20,
            Name = "Core Wall",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Geometry = CreateBoxGeometry(0, 0, 0, 10, 0.65, 10),
            Parameters =
            [
                MakeElementIdParam(-1001107, "WALL_BASE_CONSTRAINT", 10),
                MakeStringParam(9002, "Comments", "Load-bearing core wall"),
                MakeStringParam(9003, "Mark", "W-01"),
            ],
        };

        var door = new Element
        {
            Id = 22,
            HostId = 21,
            Name = "Single Flush 900x2100",
            CategoryId = (int)BuiltInCategory.OST_Doors,
            Category = "OST_Doors",
            Geometry = CreateBoxGeometry(4, -0.1, 0, 7, 0.75, 7),
            Parameters =
            [
                MakeStringParam(9003, "Mark", "D-101"),
            ],
        };

        var grid = new Element
        {
            Id = 30,
            Name = "A",
            CategoryId = (int)BuiltInCategory.OST_Grids,
            Category = "OST_Grids",
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, -5, 0],
                EndPoint = [0, 25, 0],
            },
        };

        var room = new Element
        {
            Id = 40,
            Name = "Lobby 101",
            CategoryId = (int)BuiltInCategory.OST_Rooms,
            Category = "OST_Rooms",
            SerializedSpaceBoundary = CreateBoxGeometry(0, 1, 0, 10, 12, 9),
        };

        var info = new RevitFileInfo { Elements = [level, wallType, wall, door, grid, room] };

        // 1. Default export: includes Wall + Door + Grid, excludes Room and Type/Level
        var defaultScene = SceneExporter.Build(info);
        Assert.Equal([21, 22], defaultScene.Elements.Select(e => e.Id).ToArray());
        Assert.Single(defaultScene.Grids);
        Assert.Equal("Level 1", defaultScene.Elements.Single(e => e.Id == 21).Level);
        Assert.Equal(21, defaultScene.Elements.Single(e => e.Id == 22).HostId);

        // 2. IncludeRooms = true adds translucent room volume
        var withRooms = SceneExporter.Build(info, new SceneExportOptions { IncludeRooms = true });
        Assert.Equal([21, 22, 40], withRooms.Elements.Select(e => e.Id).ToArray());

        // 3. Category filter + IncludedParameterNames + IncludeGrids = false
        var filtered = SceneExporter.Build(info, new SceneExportOptions
        {
            CategoryIds = new HashSet<int> { (int)BuiltInCategory.OST_Walls },
            IncludedParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mark" },
            IncludeGrids = false,
        });
        var onlyWall = Assert.Single(filtered.Elements);
        Assert.Equal(21, onlyWall.Id);
        Assert.Empty(filtered.Grids);
        var singleProp = Assert.Single(onlyWall.PropertyGroups.SelectMany(g => g.Properties));
        Assert.Equal("Mark", singleProp.Name);
        Assert.Equal("W-01", singleProp.Value);

        // 4. IncludeParameters = false omits all property groups
        var noParams = SceneExporter.Build(info, new SceneExportOptions { ElementIds = new HashSet<int> { 21 }, IncludeParameters = false });
        Assert.Empty(Assert.Single(noParams.Elements).PropertyGroups);
    }

    [Fact]
    public void UndecodedOrPartialGeometry_PopulatesUserReadableCompletenessWithoutInternalClassLeaks()
    {
        var wallWithoutGeo = new Element
        {
            Id = 701,
            Name = "Wall Without Decoded Mesh",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
        };

        var info = new RevitFileInfo
        {
            GeometryUndecoded = true,
            Elements = [wallWithoutGeo],
        };

        var skipLog = new SkipLog();
        var scene = SceneExporter.Build(info, new SceneExportOptions { SkipLog = skipLog });

        Assert.True(scene.Completeness.IsPartialGeometry);
        Assert.Equal(0, scene.Completeness.ExportedElements);
        Assert.NotEmpty(scene.Completeness.Notes);
        Assert.NotEmpty(scene.Completeness.OmittedGroups);
        Assert.Equal(1, skipLog.Total);

        string combinedText = string.Join(" | ", scene.Completeness.Notes.Concat(scene.Completeness.OmittedGroups.Select(g => g.Reason)));
        string[] forbiddenInternalTerms = ["GRep", "TfBlock", "ElemTable", "R22Layout", "Serialized", "FaceMesher"];
        foreach (string term in forbiddenInternalTerms)
            Assert.DoesNotContain(term, combinedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SceneJsonRoundTrip_PreservesGeometryTransformsMetadataGridsAndCompleteness()
    {
        var info = CreateSampleBuildingModel();
        var original = SceneExporter.Build(info, new SceneExportOptions { Title = "RoundTrip Building", IncludeRooms = true });

        using var ms = new MemoryStream();
        SceneExporter.ExportJson(original, ms, indented: true);
        ms.Position = 0;
        var loaded = SceneExporter.LoadJson(ms);

        Assert.Equal(ModelScene.CurrentSchema, loaded.Schema);
        Assert.Equal("RoundTrip Building", loaded.Title);
        Assert.Equal(original.Units, loaded.Units);
        Assert.Equal(original.CoordinateSystem, loaded.CoordinateSystem);
        Assert.Equal(original.Materials.Count, loaded.Materials.Count);
        Assert.Equal(original.Meshes.Count, loaded.Meshes.Count);
        Assert.Equal(original.Instances.Count, loaded.Instances.Count);
        Assert.Equal(original.Elements.Count, loaded.Elements.Count);
        Assert.Equal(original.Categories.Count, loaded.Categories.Count);
        Assert.Equal(original.Grids.Count, loaded.Grids.Count);
        Assert.Equal(original.Completeness.ExportedElements, loaded.Completeness.ExportedElements);

        for (int i = 0; i < original.Meshes.Count; i++)
        {
            Assert.Equal(original.Meshes[i].Indices, loaded.Meshes[i].Indices);
            Assert.Equal(original.Meshes[i].Positions.Length, loaded.Meshes[i].Positions.Length);
            for (int k = 0; k < original.Meshes[i].Positions.Length; k++)
                Assert.Equal(original.Meshes[i].Positions[k], loaded.Meshes[i].Positions[k], precision: 5);
        }

        for (int i = 0; i < original.Elements.Count; i++)
        {
            Assert.Equal(original.Elements[i].Id, loaded.Elements[i].Id);
            Assert.Equal(original.Elements[i].UniqueId, loaded.Elements[i].UniqueId);
            Assert.Equal(original.Elements[i].Name, loaded.Elements[i].Name);
            Assert.Equal(original.Elements[i].Category, loaded.Elements[i].Category);
        }
    }

    [Fact]
    public void GlbExport_WritesSpecCompliantBinaryGltf2WithAlignedBufferViewsAndSharedMeshes()
    {
        var info = CreateSampleBuildingModel();
        var scene = SceneExporter.Build(info);

        using var ms = new MemoryStream();
        SceneExporter.ExportGlb(scene, ms, convertZUpToYUp: true);
        byte[] glb = ms.ToArray();

        using var gltfDoc = ParseGlbJson(glb, out byte[] binChunk);
        var root = gltfDoc.RootElement;

        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        Assert.Equal(0, root.GetProperty("scene").GetInt32());

        // Root node carries Z-up -> Y-up rotation quaternion
        var rootNode = root.GetProperty("nodes")[0];
        var rot = rootNode.GetProperty("rotation");
        Assert.Equal(-0.70710678, rot[0].GetDouble(), precision: 5);
        Assert.Equal(0.70710678, rot[3].GetDouble(), precision: 5);

        // Every bufferView must be 4-byte aligned and within BIN chunk bounds
        foreach (var bv in root.GetProperty("bufferViews").EnumerateArray())
        {
            int byteOffset = bv.GetProperty("byteOffset").GetInt32();
            int byteLength = bv.GetProperty("byteLength").GetInt32();
            Assert.Equal(0, byteOffset % 4);
            Assert.True(byteOffset >= 0 && byteOffset + byteLength <= binChunk.Length);
        }

        // Verify POSITION min/max and unit-length NORMAL values in the binary buffer
        var bufferViews = root.GetProperty("bufferViews");
        var accessors = root.GetProperty("accessors");
        foreach (var gltfMesh in root.GetProperty("meshes").EnumerateArray())
        {
            foreach (var prim in gltfMesh.GetProperty("primitives").EnumerateArray())
            {
                int posAccIdx = prim.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var posAcc = accessors[posAccIdx];
                var posBv = bufferViews[posAcc.GetProperty("bufferView").GetInt32()];
                int posOffset = posBv.GetProperty("byteOffset").GetInt32();
                int vCount = posAcc.GetProperty("count").GetInt32();

                float minX = float.MaxValue, maxX = float.MinValue;
                for (int v = 0; v < vCount; v++)
                {
                    float x = BinaryPrimitives.ReadSingleLittleEndian(binChunk.AsSpan(posOffset + v * 12, 4));
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
                Assert.Equal(minX, (float)posAcc.GetProperty("min")[0].GetDouble(), precision: 4);
                Assert.Equal(maxX, (float)posAcc.GetProperty("max")[0].GetDouble(), precision: 4);

                int normAccIdx = prim.GetProperty("attributes").GetProperty("NORMAL").GetInt32();
                var normAcc = accessors[normAccIdx];
                var normBv = bufferViews[normAcc.GetProperty("bufferView").GetInt32()];
                int normOffset = normBv.GetProperty("byteOffset").GetInt32();
                for (int v = 0; v < vCount; v++)
                {
                    float nx = BinaryPrimitives.ReadSingleLittleEndian(binChunk.AsSpan(normOffset + v * 12, 4));
                    float ny = BinaryPrimitives.ReadSingleLittleEndian(binChunk.AsSpan(normOffset + v * 12 + 4, 4));
                    float nz = BinaryPrimitives.ReadSingleLittleEndian(binChunk.AsSpan(normOffset + v * 12 + 8, 4));
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    Assert.InRange(len, 0.998f, 1.002f);
                }
            }
        }

        // The 3 repeated column family instances must reference the SAME glTF mesh index!
        var columnNodes = root.GetProperty("nodes").EnumerateArray()
            .Where(n => n.TryGetProperty("extras", out var ex)
                && ex.TryGetProperty("elementId", out _)
                && ex.TryGetProperty("category", out var cat)
                && cat.GetString() == "Structural Columns")
            .ToList();
        Assert.Equal(3, columnNodes.Count);
        var referencedMeshIndices = columnNodes
            .Select(n => n.TryGetProperty("mesh", out var mProp) ? mProp.GetInt32()
                : root.GetProperty("nodes")[n.GetProperty("children")[0].GetInt32()].GetProperty("mesh").GetInt32())
            .Distinct()
            .ToList();
        Assert.Single(referencedMeshIndices);
    }

    internal static RevitFileInfo CreateSampleBuildingModel()
    {
        var level1 = new Element
        {
            Id = 1,
            Name = "Level 1",
            IsNativeLevel = true,
            CategoryId = (int)BuiltInCategory.OST_Levels,
            Category = "OST_Levels",
        };

        var glassMat = new Element
        {
            Id = 50,
            Name = "Curtain Glazing",
            CategoryId = (int)BuiltInCategory.OST_Materials,
            Category = "OST_Materials",
            Parameters =
            [
                MakeIntParam(-1001201, "MATERIAL_PARAM_COLOR", (235 << 16) | (195 << 8) | 120),
                MakeIntParam(-1001202, "MATERIAL_PARAM_TRANSPARENCY", 65),
            ],
        };

        var colSymbol = new Element
        {
            Id = 60,
            Name = "UC 305x305x97",
            FamilyName = "Universal Column",
            CategoryId = (int)BuiltInCategory.OST_StructuralColumns,
            Category = "OST_StructuralColumns",
            Geometry = CreateBoxGeometry(-0.5, -0.5, 0, 0.5, 0.5, 12),
        };

        var slab = new Element
        {
            Id = 100,
            UniqueId = "bldg-slab-0100",
            Name = "Ground Floor Slab",
            CategoryId = (int)BuiltInCategory.OST_Floors,
            Category = "OST_Floors",
            Geometry = CreateBoxGeometry(0, 0, -1, 30, 20, 0),
            Parameters = [MakeElementIdParam(-1002003, "LEVEL_PARAM", 1)],
        };

        var wall = new Element
        {
            Id = 101,
            UniqueId = "bldg-wall-0101",
            Name = "North Facade Wall",
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Category = "OST_Walls",
            Geometry = CreateBoxGeometry(0, 19.3, 0, 30, 20, 12),
            Parameters =
            [
                MakeElementIdParam(-1001107, "WALL_BASE_CONSTRAINT", 1),
                MakeStringParam(8001, "Mark", "W-N1"),
            ],
        };

        var window = new Element
        {
            Id = 102,
            UniqueId = "bldg-win-0102",
            HostId = 101,
            Name = "Fixed Window 2400x1500",
            CategoryId = (int)BuiltInCategory.OST_Windows,
            Category = "OST_Windows",
            Geometry = CreateBoxGeometry(10, 19.2, 3, 18, 20.1, 8, renderStyleId: 50),
        };

        var room = new Element
        {
            Id = 103,
            UniqueId = "bldg-room-0103",
            Name = "Open Office",
            CategoryId = (int)BuiltInCategory.OST_Rooms,
            Category = "OST_Rooms",
            SerializedSpaceBoundary = CreateBoxGeometry(1, 1, 0, 29, 19, 10),
        };

        var gridA = new Element
        {
            Id = 200,
            Name = "A",
            CategoryId = (int)BuiltInCategory.OST_Grids,
            Category = "OST_Grids",
            Location = new LocationCurve { CurveType = "Line", StartPoint = [0, -4, 0], EndPoint = [0, 24, 0] },
        };

        var elements = new List<Element> { level1, glassMat, colSymbol, slab, wall, window, room, gridA };
        for (int i = 0; i < 3; i++)
        {
            double x = 5 + i * 10;
            elements.Add(new Element
            {
                Id = 300 + i,
                TypeId = 60,
                UniqueId = $"bldg-col-{300 + i}",
                Name = $"Column C{i + 1}",
                FamilyName = "Universal Column",
                CategoryId = (int)BuiltInCategory.OST_StructuralColumns,
                Category = "OST_StructuralColumns",
                Parameters = [MakeElementIdParam(-1001350, "FAMILY_LEVEL_PARAM", 1)],
                Geometry = new GeometryData
                {
                    Instances =
                    [
                        new GeoInstance
                        {
                            SymbolId = 60,
                            Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, x, 10, 0],
                        },
                    ],
                },
            });
        }

        return new RevitFileInfo
        {
            ProjectInfo = new ProjectInfo { Name = "Sample Office Pavilion" },
            Elements = elements,
        };
    }

    internal static GeometryData CreateBoxGeometry(
        double minX, double minY, double minZ,
        double maxX, double maxY, double maxZ,
        int? renderStyleId = null)
    {
        GeoFace Face(double[] normal, double[][] corners) => new()
        {
            Normal = normal,
            PlaneSurface = true,
            RenderStyleId = renderStyleId,
            OuterVertices = corners.ToList(),
        };

        return new GeometryData
        {
            BoundingBox = [minX, minY, minZ, maxX, maxY, maxZ],
            Faces =
            [
                // Bottom (-Z)
                Face([0, 0, -1], [[minX, minY, minZ], [minX, maxY, minZ], [maxX, maxY, minZ], [maxX, minY, minZ]]),
                // Top (+Z)
                Face([0, 0, 1],  [[minX, minY, maxZ], [maxX, minY, maxZ], [maxX, maxY, maxZ], [minX, maxY, maxZ]]),
                // Front (-Y)
                Face([0, -1, 0], [[minX, minY, minZ], [maxX, minY, minZ], [maxX, minY, maxZ], [minX, minY, maxZ]]),
                // Back (+Y)
                Face([0, 1, 0],  [[maxX, maxY, minZ], [minX, maxY, minZ], [minX, maxY, maxZ], [maxX, maxY, maxZ]]),
                // Left (-X)
                Face([-1, 0, 0], [[minX, maxY, minZ], [minX, minY, minZ], [minX, minY, maxZ], [minX, maxY, maxZ]]),
                // Right (+X)
                Face([1, 0, 0],  [[maxX, minY, minZ], [maxX, maxY, minZ], [maxX, maxY, maxZ], [maxX, minY, maxZ]]),
            ],
        };
    }

    internal static Parameter MakeIntParam(int id, string name, int value) => new()
    {
        Id = id,
        StorageType = StorageType.Integer,
        Definition = new Definition { Id = id, Name = name },
        Value = value,
    };

    internal static Parameter MakeElementIdParam(int id, string name, int targetId) => new()
    {
        Id = id,
        StorageType = StorageType.ElementId,
        Definition = new Definition { Id = id, Name = name },
        Value = targetId,
    };

    internal static Parameter MakeStringParam(int id, string name, string value) => new()
    {
        Id = id,
        StorageType = StorageType.String,
        Definition = new Definition { Id = id, Name = name },
        Value = value,
    };

    private static JsonDocument ParseGlbJson(byte[] glb, out byte[] binChunk)
    {
        Assert.True(glb.Length >= 20);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4, 4));
        uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8, 4));

        Assert.Equal(0x46546C67u, magic); // "glTF"
        Assert.Equal(2u, version);
        Assert.Equal((uint)glb.Length, totalLen);

        uint jsonLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4));
        uint jsonType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16, 4));
        Assert.Equal(0x4E4F534Au, jsonType); // "JSON"
        Assert.Equal(0u, jsonLen % 4u);

        int binHeaderPos = 20 + (int)jsonLen;
        Assert.True(glb.Length >= binHeaderPos + 8);
        uint binLen = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binHeaderPos, 4));
        uint binType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binHeaderPos + 4, 4));
        Assert.Equal(0x004E4942u, binType); // "BIN\0"
        Assert.Equal(0u, binLen % 4u);
        Assert.Equal(binHeaderPos + 8 + (int)binLen, glb.Length);

        binChunk = glb.AsSpan(binHeaderPos + 8, (int)binLen).ToArray();
        string jsonText = Encoding.UTF8.GetString(glb, 20, (int)jsonLen);
        return JsonDocument.Parse(jsonText);
    }

    private static double ComputeMeshTriangleArea(SceneMeshPrototype mesh)
    {
        double area = 0;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            Vector3 a = GetVec3(mesh.Positions, mesh.Indices[t * 3]);
            Vector3 b = GetVec3(mesh.Positions, mesh.Indices[t * 3 + 1]);
            Vector3 c = GetVec3(mesh.Positions, mesh.Indices[t * 3 + 2]);
            area += 0.5 * Vector3.Cross(b - a, c - a).Length();
        }
        return area;
    }

    private static Vector3 GetVec3(float[] buffer, int vertexIndex) =>
        new(buffer[vertexIndex * 3], buffer[vertexIndex * 3 + 1], buffer[vertexIndex * 3 + 2]);

    // System.Numerics.Matrix4x4 uses row-vector convention (v * M), so column-major glTF matrix [m0..m15]
    // maps its columns to rows of Matrix4x4 for Vector3.Transform(v, M)!
    private static Matrix4x4 ToMatrix4x4(float[] m) => new(
        m[0], m[1], m[2], m[3],
        m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11],
        m[12], m[13], m[14], m[15]);
}
