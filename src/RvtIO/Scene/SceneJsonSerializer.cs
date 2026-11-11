using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RvtIO.Scene;

/// <summary>
/// Serializes and deserializes <see cref="ModelScene"/> instances using the versioned <c>rvtio-scene/1</c> JSON contract
/// with compact Base64 little-endian typed geometry buffers.
/// </summary>
internal static class SceneJsonSerializer
{
    internal static void Write(ModelScene scene, Stream output, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(output);

        var writerOptions = new JsonWriterOptions
        {
            Indented = indented,
            // Default encoder escapes '<', '>', '&', '+' as \uXXXX, making output safe inside HTML <script> blocks too.
            Encoder = JavaScriptEncoder.Default,
        };

        using var w = new Utf8JsonWriter(output, writerOptions);
        w.WriteStartObject();

        w.WriteString("schema", scene.Schema);
        w.WriteString("title", scene.Title);
        w.WriteString("units", scene.Units);
        w.WriteString("coordinateSystem", scene.CoordinateSystem);

        WriteDoubleArray(w, "rtcOffset", scene.RtcOffset);
        if (scene.SharedTransform != null)
            WriteDoubleArray(w, "sharedTransform", scene.SharedTransform);
        WriteDoubleArray(w, "boundsMin", scene.BoundsMin);
        WriteDoubleArray(w, "boundsMax", scene.BoundsMax);

        // Materials
        w.WritePropertyName("materials");
        w.WriteStartArray();
        foreach (var mat in scene.Materials)
        {
            w.WriteStartObject();
            w.WriteNumber("index", mat.Index);
            if (mat.RevitMaterialId.HasValue)
                w.WriteNumber("revitMaterialId", mat.RevitMaterialId.Value);
            w.WriteString("name", mat.Name);
            WriteFloatArray(w, "color", mat.Color);
            w.WriteNumber("opacity", Math.Round(mat.Opacity, 4));
            w.WriteNumber("metallic", Math.Round(mat.Metallic, 4));
            w.WriteNumber("roughness", Math.Round(mat.Roughness, 4));
            w.WriteBoolean("doubleSided", mat.DoubleSided);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Reusable mesh prototypes
        w.WritePropertyName("meshes");
        w.WriteStartArray();
        foreach (var mesh in scene.Meshes)
        {
            w.WriteStartObject();
            w.WriteNumber("index", mesh.Index);
            if (mesh.SourceSymbolId.HasValue)
                w.WriteNumber("sourceSymbolId", mesh.SourceSymbolId.Value);
            if (mesh.IsMirroredVariant)
                w.WriteBoolean("isMirroredVariant", true);
            w.WriteNumber("vertexCount", mesh.VertexCount);
            w.WriteNumber("triangleCount", mesh.TriangleCount);
            WriteFloatArray(w, "localBoundsMin", mesh.LocalBoundsMin);
            WriteFloatArray(w, "localBoundsMax", mesh.LocalBoundsMax);

            bool useUint16 = mesh.VertexCount <= ushort.MaxValue;
            w.WriteString("indexType", useUint16 ? "uint16" : "uint32");
            w.WriteString("positionsB64", EncodeFloat32Base64(mesh.Positions));
            w.WriteString("normalsB64", EncodeFloat32Base64(mesh.Normals));
            w.WriteString("indicesB64", useUint16 ? EncodeUInt16Base64(mesh.Indices) : EncodeUInt32Base64(mesh.Indices));
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Placed mesh instances
        w.WritePropertyName("instances");
        w.WriteStartArray();
        foreach (var inst in scene.Instances)
        {
            w.WriteStartObject();
            w.WriteNumber("meshIndex", inst.MeshIndex);
            w.WriteNumber("elementIndex", inst.ElementIndex);
            w.WriteNumber("elementId", inst.ElementId);
            w.WriteNumber("materialIndex", inst.MaterialIndex);
            WriteFloatArray(w, "matrix", inst.Matrix);
            WriteFloatArray(w, "boundsMin", inst.BoundsMin);
            WriteFloatArray(w, "boundsMax", inst.BoundsMax);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Elements & selected metadata
        w.WritePropertyName("elements");
        w.WriteStartArray();
        foreach (var el in scene.Elements)
        {
            w.WriteStartObject();
            w.WriteNumber("id", el.Id);
            if (el.UniqueId != null) w.WriteString("uniqueId", el.UniqueId);
            w.WriteString("name", el.Name);
            w.WriteString("category", el.Category);
            if (el.CategoryId.HasValue) w.WriteNumber("categoryId", el.CategoryId.Value);
            if (el.FamilyName != null) w.WriteString("familyName", el.FamilyName);
            if (el.TypeName != null) w.WriteString("typeName", el.TypeName);
            if (el.Level != null) w.WriteString("level", el.Level);
            if (el.HostId.HasValue) w.WriteNumber("hostId", el.HostId.Value);
            if (el.IsInstanced) w.WriteBoolean("isInstanced", true);

            w.WritePropertyName("instanceIndices");
            w.WriteStartArray();
            foreach (int ii in el.InstanceIndices) w.WriteNumberValue(ii);
            w.WriteEndArray();

            if (el.PropertyGroups.Count > 0)
            {
                w.WritePropertyName("propertyGroups");
                w.WriteStartArray();
                foreach (var pg in el.PropertyGroups)
                {
                    w.WriteStartObject();
                    w.WriteString("group", pg.Group);
                    w.WritePropertyName("properties");
                    w.WriteStartArray();
                    foreach (var prop in pg.Properties)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", prop.Name);
                        w.WriteString("value", prop.Value);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Category hierarchy
        w.WritePropertyName("categories");
        w.WriteStartArray();
        foreach (var cat in scene.Categories)
        {
            w.WriteStartObject();
            w.WriteString("category", cat.Category);
            if (cat.CategoryId.HasValue) w.WriteNumber("categoryId", cat.CategoryId.Value);
            w.WritePropertyName("elementIndices");
            w.WriteStartArray();
            foreach (int ei in cat.ElementIndices) w.WriteNumberValue(ei);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Datum grids
        w.WritePropertyName("grids");
        w.WriteStartArray();
        foreach (var g in scene.Grids)
        {
            w.WriteStartObject();
            w.WriteNumber("id", g.Id);
            w.WriteString("name", g.Name);
            WriteFloatArray(w, "start", g.Start);
            WriteFloatArray(w, "end", g.End);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Completeness summary
        var comp = scene.Completeness;
        w.WritePropertyName("completeness");
        w.WriteStartObject();
        w.WriteNumber("totalElements", comp.TotalElements);
        w.WriteNumber("consideredElements", comp.ConsideredElements);
        w.WriteNumber("exportedElements", comp.ExportedElements);
        w.WriteNumber("instancedElements", comp.InstancedElements);
        w.WriteNumber("reusableMeshCount", comp.ReusableMeshCount);
        w.WriteNumber("instanceCount", comp.InstanceCount);
        w.WriteNumber("vertexCount", comp.VertexCount);
        w.WriteNumber("triangleCount", comp.TriangleCount);
        w.WriteBoolean("isPartialGeometry", comp.IsPartialGeometry);

        w.WritePropertyName("notes");
        w.WriteStartArray();
        foreach (string note in comp.Notes) w.WriteStringValue(note);
        w.WriteEndArray();

        w.WritePropertyName("omittedGroups");
        w.WriteStartArray();
        foreach (var og in comp.OmittedGroups)
        {
            w.WriteStartObject();
            w.WriteString("code", og.Code);
            w.WriteString("reason", og.Reason);
            w.WriteNumber("count", og.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteEndObject();
        w.Flush();
    }

    internal static string ToJsonString(ModelScene scene, bool indented = false)
    {
        using var ms = new MemoryStream();
        Write(scene, ms, indented);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    internal static ModelScene Read(Stream input)
    {
        using var doc = JsonDocument.Parse(input);
        return ReadFromElement(doc.RootElement);
    }

    internal static ModelScene ReadFromString(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadFromElement(doc.RootElement);
    }

    private static ModelScene ReadFromElement(JsonElement root)
    {
        string schema = root.GetProperty("schema").GetString() ?? "";
        if (schema != ModelScene.CurrentSchema)
            throw new InvalidDataException($"Unsupported scene schema '{schema}'. Expected '{ModelScene.CurrentSchema}'.");

        var materials = new List<SceneMaterial>();
        if (root.TryGetProperty("materials", out var matsEl))
        {
            foreach (var m in matsEl.EnumerateArray())
            {
                materials.Add(new SceneMaterial
                {
                    Index = m.GetProperty("index").GetInt32(),
                    RevitMaterialId = m.TryGetProperty("revitMaterialId", out var rmid) ? rmid.GetInt32() : null,
                    Name = m.GetProperty("name").GetString() ?? "Default",
                    Color = ReadFloatArray(m.GetProperty("color")),
                    Opacity = m.GetProperty("opacity").GetSingle(),
                    Metallic = m.GetProperty("metallic").GetSingle(),
                    Roughness = m.GetProperty("roughness").GetSingle(),
                    DoubleSided = !m.TryGetProperty("doubleSided", out var ds) || ds.GetBoolean(),
                });
            }
        }

        var meshes = new List<SceneMeshPrototype>();
        if (root.TryGetProperty("meshes", out var meshesEl))
        {
            foreach (var m in meshesEl.EnumerateArray())
            {
                string idxType = m.GetProperty("indexType").GetString() ?? "uint32";
                string idxB64 = m.GetProperty("indicesB64").GetString() ?? "";
                meshes.Add(new SceneMeshPrototype
                {
                    Index = m.GetProperty("index").GetInt32(),
                    SourceSymbolId = m.TryGetProperty("sourceSymbolId", out var sid) ? sid.GetInt32() : null,
                    IsMirroredVariant = m.TryGetProperty("isMirroredVariant", out var mir) && mir.GetBoolean(),
                    LocalBoundsMin = ReadFloatArray(m.GetProperty("localBoundsMin")),
                    LocalBoundsMax = ReadFloatArray(m.GetProperty("localBoundsMax")),
                    Positions = DecodeFloat32Base64(m.GetProperty("positionsB64").GetString() ?? ""),
                    Normals = DecodeFloat32Base64(m.GetProperty("normalsB64").GetString() ?? ""),
                    Indices = idxType == "uint16" ? DecodeUInt16Base64(idxB64) : DecodeUInt32Base64(idxB64),
                });
            }
        }

        var instances = new List<SceneMeshInstance>();
        if (root.TryGetProperty("instances", out var instsEl))
        {
            foreach (var inst in instsEl.EnumerateArray())
            {
                instances.Add(new SceneMeshInstance
                {
                    MeshIndex = inst.GetProperty("meshIndex").GetInt32(),
                    ElementIndex = inst.GetProperty("elementIndex").GetInt32(),
                    ElementId = inst.GetProperty("elementId").GetInt32(),
                    MaterialIndex = inst.GetProperty("materialIndex").GetInt32(),
                    Matrix = ReadFloatArray(inst.GetProperty("matrix")),
                    BoundsMin = ReadFloatArray(inst.GetProperty("boundsMin")),
                    BoundsMax = ReadFloatArray(inst.GetProperty("boundsMax")),
                });
            }
        }

        var elements = new List<SceneElement>();
        if (root.TryGetProperty("elements", out var elsEl))
        {
            foreach (var el in elsEl.EnumerateArray())
            {
                var groups = new List<ScenePropertyGroup>();
                if (el.TryGetProperty("propertyGroups", out var pgsEl))
                {
                    foreach (var pg in pgsEl.EnumerateArray())
                    {
                        var props = new List<SceneProperty>();
                        foreach (var pr in pg.GetProperty("properties").EnumerateArray())
                        {
                            props.Add(new SceneProperty
                            {
                                Name = pr.GetProperty("name").GetString() ?? "",
                                Value = pr.GetProperty("value").GetString() ?? "",
                            });
                        }
                        groups.Add(new ScenePropertyGroup
                        {
                            Group = pg.GetProperty("group").GetString() ?? "Properties",
                            Properties = props,
                        });
                    }
                }

                elements.Add(new SceneElement
                {
                    Id = el.GetProperty("id").GetInt32(),
                    UniqueId = el.TryGetProperty("uniqueId", out var uid) ? uid.GetString() : null,
                    Name = el.GetProperty("name").GetString() ?? "",
                    Category = el.GetProperty("category").GetString() ?? "Uncategorized",
                    CategoryId = el.TryGetProperty("categoryId", out var cid) ? cid.GetInt32() : null,
                    FamilyName = el.TryGetProperty("familyName", out var fn) ? fn.GetString() : null,
                    TypeName = el.TryGetProperty("typeName", out var tn) ? tn.GetString() : null,
                    Level = el.TryGetProperty("level", out var lvl) ? lvl.GetString() : null,
                    HostId = el.TryGetProperty("hostId", out var hid) ? hid.GetInt32() : null,
                    IsInstanced = el.TryGetProperty("isInstanced", out var isInst) && isInst.GetBoolean(),
                    InstanceIndices = el.GetProperty("instanceIndices").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                    PropertyGroups = groups,
                });
            }
        }

        var categories = new List<SceneCategoryNode>();
        if (root.TryGetProperty("categories", out var catsEl))
        {
            foreach (var c in catsEl.EnumerateArray())
            {
                categories.Add(new SceneCategoryNode
                {
                    Category = c.GetProperty("category").GetString() ?? "",
                    CategoryId = c.TryGetProperty("categoryId", out var cid) ? cid.GetInt32() : null,
                    ElementIndices = c.GetProperty("elementIndices").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                });
            }
        }

        var grids = new List<SceneGridLine>();
        if (root.TryGetProperty("grids", out var gridsEl))
        {
            foreach (var g in gridsEl.EnumerateArray())
            {
                grids.Add(new SceneGridLine
                {
                    Id = g.GetProperty("id").GetInt32(),
                    Name = g.GetProperty("name").GetString() ?? "",
                    Start = ReadFloatArray(g.GetProperty("start")),
                    End = ReadFloatArray(g.GetProperty("end")),
                });
            }
        }

        var compEl = root.GetProperty("completeness");
        var omitted = new List<SceneOmittedGroup>();
        if (compEl.TryGetProperty("omittedGroups", out var ogsEl))
        {
            foreach (var og in ogsEl.EnumerateArray())
            {
                omitted.Add(new SceneOmittedGroup
                {
                    Code = og.GetProperty("code").GetString() ?? "",
                    Reason = og.GetProperty("reason").GetString() ?? "",
                    Count = og.GetProperty("count").GetInt32(),
                });
            }
        }

        var completeness = new SceneCompletenessSummary
        {
            TotalElements = compEl.GetProperty("totalElements").GetInt32(),
            ConsideredElements = compEl.GetProperty("consideredElements").GetInt32(),
            ExportedElements = compEl.GetProperty("exportedElements").GetInt32(),
            InstancedElements = compEl.GetProperty("instancedElements").GetInt32(),
            ReusableMeshCount = compEl.GetProperty("reusableMeshCount").GetInt32(),
            InstanceCount = compEl.GetProperty("instanceCount").GetInt32(),
            VertexCount = compEl.GetProperty("vertexCount").GetInt32(),
            TriangleCount = compEl.GetProperty("triangleCount").GetInt32(),
            IsPartialGeometry = compEl.GetProperty("isPartialGeometry").GetBoolean(),
            Notes = compEl.GetProperty("notes").EnumerateArray().Select(n => n.GetString() ?? "").ToList(),
            OmittedGroups = omitted,
        };

        return new ModelScene
        {
            Schema = schema,
            Title = root.GetProperty("title").GetString() ?? "Revit Model",
            Units = root.GetProperty("units").GetString() ?? "m",
            CoordinateSystem = root.GetProperty("coordinateSystem").GetString() ?? "right-handed-z-up",
            RtcOffset = ReadDoubleArray(root.GetProperty("rtcOffset")),
            SharedTransform = root.TryGetProperty("sharedTransform", out var stEl) ? ReadDoubleArray(stEl) : null,
            BoundsMin = ReadDoubleArray(root.GetProperty("boundsMin")),
            BoundsMax = ReadDoubleArray(root.GetProperty("boundsMax")),
            Materials = materials,
            Meshes = meshes,
            Instances = instances,
            Elements = elements,
            Categories = categories,
            Grids = grids,
            Completeness = completeness,
        };
    }

    private static void WriteDoubleArray(Utf8JsonWriter w, string name, double[] values)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (double v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static void WriteFloatArray(Utf8JsonWriter w, string name, float[] values)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (float v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static float[] ReadFloatArray(JsonElement el) =>
        el.EnumerateArray().Select(x => x.GetSingle()).ToArray();

    private static double[] ReadDoubleArray(JsonElement el) =>
        el.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    private static string EncodeFloat32Base64(float[] data)
    {
        byte[] bytes = new byte[data.Length * 4];
        var span = bytes.AsSpan();
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * 4, 4), data[i]);
        return Convert.ToBase64String(bytes);
    }

    private static string EncodeUInt16Base64(int[] indices)
    {
        byte[] bytes = new byte[indices.Length * 2];
        var span = bytes.AsSpan();
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2, 2), (ushort)indices[i]);
        return Convert.ToBase64String(bytes);
    }

    private static string EncodeUInt32Base64(int[] indices)
    {
        byte[] bytes = new byte[indices.Length * 4];
        var span = bytes.AsSpan();
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(i * 4, 4), (uint)indices[i]);
        return Convert.ToBase64String(bytes);
    }

    private static float[] DecodeFloat32Base64(string b64)
    {
        byte[] bytes = Convert.FromBase64String(b64);
        float[] res = new float[bytes.Length / 4];
        for (int i = 0; i < res.Length; i++)
            res[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
        return res;
    }

    private static int[] DecodeUInt16Base64(string b64)
    {
        byte[] bytes = Convert.FromBase64String(b64);
        int[] res = new int[bytes.Length / 2];
        for (int i = 0; i < res.Length; i++)
            res[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return res;
    }

    private static int[] DecodeUInt32Base64(string b64)
    {
        byte[] bytes = Convert.FromBase64String(b64);
        int[] res = new int[bytes.Length / 4];
        for (int i = 0; i < res.Length; i++)
            res[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        return res;
    }
}
