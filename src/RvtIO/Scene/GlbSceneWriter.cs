using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RvtIO.Scene;

/// <summary>
/// Writes a <see cref="ModelScene"/> as a self-contained binary glTF 2.0 (<c>.glb</c>) asset,
/// preserving reusable meshes, node transforms, PBR materials/transparency, hierarchy, RTC origin metadata,
/// and per-element identity/properties inside glTF <c>extras</c>.
/// </summary>
internal static class GlbSceneWriter
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint GlbVersion = 2;
    private const uint ChunkTypeJson = 0x4E4F534A; // "JSON"
    private const uint ChunkTypeBin = 0x004E4942;  // "BIN\0"

    private const int ComponentTypeUnsignedShort = 5123;
    private const int ComponentTypeUnsignedInt = 5125;
    private const int ComponentTypeFloat = 5126;

    private const int TargetArrayBuffer = 34962;
    private const int TargetElementArrayBuffer = 34963;

    // Exact unit quaternion rotating Revit Z-up (+Z up) by -90 deg around X to glTF Y-up (+Y up, -Z north)
    private static readonly double[] ZUpToYUpRotation = [-0.7071067811865475, 0.0, 0.0, 0.7071067811865476];

    internal static void Write(ModelScene scene, Stream output, bool convertZUpToYUp = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(output);

        using var binStream = new MemoryStream();
        var bufferViews = new List<BufferViewDef>();
        var accessors = new List<AccessorDef>();

        void AlignBin4()
        {
            int rem = (int)(binStream.Length & 3);
            if (rem != 0)
            {
                for (int i = rem; i < 4; i++)
                    binStream.WriteByte(0);
            }
        }

        // Deduplicate vertex (POSITION + NORMAL) accessors across normal and mirrored variants that share float[] arrays
        var vertexAccessorsByArray = new Dictionary<float[], (int PosAccessor, int NormAccessor)>(ReferenceEqualityComparer.Instance);
        var protoAccessors = new (int PosAccessor, int NormAccessor, int IndexAccessor)[scene.Meshes.Count];

        for (int mIdx = 0; mIdx < scene.Meshes.Count; mIdx++)
        {
            var proto = scene.Meshes[mIdx];
            if (!vertexAccessorsByArray.TryGetValue(proto.Positions, out var vAcc))
            {
                AlignBin4();
                int posOffset = (int)binStream.Length;
                WriteFloat32Buffer(binStream, proto.Positions);
                int posByteLength = proto.Positions.Length * 4;
                int posBv = bufferViews.Count;
                bufferViews.Add(new BufferViewDef(posOffset, posByteLength, TargetArrayBuffer));

                int posAcc = accessors.Count;
                accessors.Add(new AccessorDef(
                    BufferView: posBv,
                    ComponentType: ComponentTypeFloat,
                    Count: proto.VertexCount,
                    Type: "VEC3",
                    Min: proto.LocalBoundsMin,
                    Max: proto.LocalBoundsMax));

                AlignBin4();
                int normOffset = (int)binStream.Length;
                WriteFloat32Buffer(binStream, proto.Normals);
                int normByteLength = proto.Normals.Length * 4;
                int normBv = bufferViews.Count;
                bufferViews.Add(new BufferViewDef(normOffset, normByteLength, TargetArrayBuffer));

                int normAcc = accessors.Count;
                accessors.Add(new AccessorDef(
                    BufferView: normBv,
                    ComponentType: ComponentTypeFloat,
                    Count: proto.VertexCount,
                    Type: "VEC3",
                    Min: null,
                    Max: null));

                vAcc = (posAcc, normAcc);
                vertexAccessorsByArray[proto.Positions] = vAcc;
            }

            AlignBin4();
            int idxOffset = (int)binStream.Length;
            bool useU16 = proto.VertexCount <= ushort.MaxValue;
            int idxByteLength;
            if (useU16)
            {
                WriteUInt16Buffer(binStream, proto.Indices);
                idxByteLength = proto.Indices.Length * 2;
            }
            else
            {
                WriteUInt32Buffer(binStream, proto.Indices);
                idxByteLength = proto.Indices.Length * 4;
            }

            int idxBv = bufferViews.Count;
            bufferViews.Add(new BufferViewDef(idxOffset, idxByteLength, TargetElementArrayBuffer));
            int idxAcc = accessors.Count;
            accessors.Add(new AccessorDef(
                BufferView: idxBv,
                ComponentType: useU16 ? ComponentTypeUnsignedShort : ComponentTypeUnsignedInt,
                Count: proto.Indices.Length,
                Type: "SCALAR",
                Min: null,
                Max: null));

            protoAccessors[mIdx] = (vAcc.PosAccessor, vAcc.NormAccessor, idxAcc);
        }

        AlignBin4();
        byte[] binBytes = binStream.ToArray();
        bool hasBin = binBytes.Length > 0;

        // Deduplicate glTF meshes by (MeshIndex, MaterialIndex) so repeated family instances share 1 glTF mesh
        var gltfMeshes = new List<(int ProtoIndex, int MaterialIndex, string Name)>();
        var gltfMeshLookup = new Dictionary<(int ProtoIndex, int MaterialIndex), int>();

        int GetOrCreateGltfMesh(int protoIndex, int materialIndex)
        {
            var key = (protoIndex, materialIndex);
            if (gltfMeshLookup.TryGetValue(key, out int existing))
                return existing;
            int idx = gltfMeshes.Count;
            var proto = scene.Meshes[protoIndex];
            string meshName = proto.SourceSymbolId.HasValue
                ? $"Symbol_{proto.SourceSymbolId.Value}{(proto.IsMirroredVariant ? "_Mirrored" : "")}_Mat{materialIndex}"
                : $"Mesh_{protoIndex}_Mat{materialIndex}";
            gltfMeshes.Add((protoIndex, materialIndex, meshName));
            gltfMeshLookup[key] = idx;
            return idx;
        }

        // Build glTF node graph: Root (0) -> Category nodes -> Element nodes (-> Subpart nodes if multi-instance)
        var nodes = new List<NodeDef>();
        int rootNodeIndex = 0;
        nodes.Add(new NodeDef
        {
            Name = scene.Title,
            Rotation = convertZUpToYUp ? ZUpToYUpRotation : null,
            Children = new List<int>(),
            IsRoot = true,
        });

        foreach (var cat in scene.Categories)
        {
            if (cat.ElementIndices.Count == 0) continue;
            int catNodeIndex = nodes.Count;
            var catNode = new NodeDef
            {
                Name = cat.Category,
                CategoryName = cat.Category,
                CategoryId = cat.CategoryId,
                Children = new List<int>(cat.ElementIndices.Count),
            };
            nodes.Add(catNode);
            nodes[rootNodeIndex].Children!.Add(catNodeIndex);

            foreach (int elIdx in cat.ElementIndices)
            {
                var el = scene.Elements[elIdx];
                if (el.InstanceIndices.Count == 0) continue;

                int elNodeIndex = nodes.Count;
                catNode.Children.Add(elNodeIndex);

                if (el.InstanceIndices.Count == 1)
                {
                    var inst = scene.Instances[el.InstanceIndices[0]];
                    int gltfMesh = GetOrCreateGltfMesh(inst.MeshIndex, inst.MaterialIndex);
                    nodes.Add(new NodeDef
                    {
                        Name = $"{el.Name} [{el.Id}]",
                        MeshIndex = gltfMesh,
                        Matrix = IsIdentityMatrix(inst.Matrix) ? null : inst.Matrix,
                        Element = el,
                    });
                }
                else
                {
                    var elNode = new NodeDef
                    {
                        Name = $"{el.Name} [{el.Id}]",
                        Children = new List<int>(el.InstanceIndices.Count),
                        Element = el,
                    };
                    nodes.Add(elNode);

                    for (int p = 0; p < el.InstanceIndices.Count; p++)
                    {
                        var inst = scene.Instances[el.InstanceIndices[p]];
                        int gltfMesh = GetOrCreateGltfMesh(inst.MeshIndex, inst.MaterialIndex);
                        int partNodeIdx = nodes.Count;
                        elNode.Children.Add(partNodeIdx);
                        nodes.Add(new NodeDef
                        {
                            Name = $"{el.Name} [{el.Id}] Part {p + 1}",
                            MeshIndex = gltfMesh,
                            Matrix = IsIdentityMatrix(inst.Matrix) ? null : inst.Matrix,
                            LeafElementId = el.Id,
                        });
                    }
                }
            }
        }

        // Write glTF JSON chunk
        using var jsonStream = new MemoryStream();
        using (var w = new Utf8JsonWriter(jsonStream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();

            w.WritePropertyName("asset");
            w.WriteStartObject();
            w.WriteString("version", "2.0");
            w.WriteString("generator", "RvtIO");
            w.WritePropertyName("extras");
            WriteAssetExtras(w, scene, convertZUpToYUp);
            w.WriteEndObject();

            w.WriteNumber("scene", 0);
            w.WritePropertyName("scenes");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("name", scene.Title);
            w.WritePropertyName("nodes");
            w.WriteStartArray();
            w.WriteNumberValue(rootNodeIndex);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            // Nodes
            w.WritePropertyName("nodes");
            w.WriteStartArray();
            foreach (var node in nodes)
                WriteNode(w, node);
            w.WriteEndArray();

            // Materials
            if (scene.Materials.Count > 0)
            {
                w.WritePropertyName("materials");
                w.WriteStartArray();
                foreach (var mat in scene.Materials)
                {
                    w.WriteStartObject();
                    w.WriteString("name", mat.Name);
                    w.WritePropertyName("pbrMetallicRoughness");
                    w.WriteStartObject();
                    w.WritePropertyName("baseColorFactor");
                    w.WriteStartArray();
                    w.WriteNumberValue(Math.Round(mat.Color[0], 5));
                    w.WriteNumberValue(Math.Round(mat.Color[1], 5));
                    w.WriteNumberValue(Math.Round(mat.Color[2], 5));
                    w.WriteNumberValue(Math.Round(mat.Opacity, 5));
                    w.WriteEndArray();
                    w.WriteNumber("metallicFactor", Math.Round(mat.Metallic, 4));
                    w.WriteNumber("roughnessFactor", Math.Round(mat.Roughness, 4));
                    w.WriteEndObject();
                    w.WriteBoolean("doubleSided", mat.DoubleSided);
                    if (mat.Opacity < 0.999f)
                        w.WriteString("alphaMode", "BLEND");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            // Meshes
            if (gltfMeshes.Count > 0)
            {
                w.WritePropertyName("meshes");
                w.WriteStartArray();
                foreach (var (protoIndex, matIndex, meshName) in gltfMeshes)
                {
                    var acc = protoAccessors[protoIndex];
                    w.WriteStartObject();
                    w.WriteString("name", meshName);
                    w.WritePropertyName("primitives");
                    w.WriteStartArray();
                    w.WriteStartObject();
                    w.WritePropertyName("attributes");
                    w.WriteStartObject();
                    w.WriteNumber("POSITION", acc.PosAccessor);
                    w.WriteNumber("NORMAL", acc.NormAccessor);
                    w.WriteEndObject();
                    w.WriteNumber("indices", acc.IndexAccessor);
                    w.WriteNumber("material", matIndex);
                    w.WriteNumber("mode", 4); // TRIANGLES
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            // Accessors, BufferViews, Buffers
            if (hasBin)
            {
                w.WritePropertyName("accessors");
                w.WriteStartArray();
                foreach (var acc in accessors)
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", acc.BufferView);
                    w.WriteNumber("byteOffset", 0);
                    w.WriteNumber("componentType", acc.ComponentType);
                    w.WriteNumber("count", acc.Count);
                    w.WriteString("type", acc.Type);
                    if (acc.Min != null)
                    {
                        w.WritePropertyName("min");
                        w.WriteStartArray();
                        foreach (float v in acc.Min) w.WriteNumberValue(v);
                        w.WriteEndArray();
                    }
                    if (acc.Max != null)
                    {
                        w.WritePropertyName("max");
                        w.WriteStartArray();
                        foreach (float v in acc.Max) w.WriteNumberValue(v);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WritePropertyName("bufferViews");
                w.WriteStartArray();
                foreach (var bv in bufferViews)
                {
                    w.WriteStartObject();
                    w.WriteNumber("buffer", 0);
                    w.WriteNumber("byteOffset", bv.ByteOffset);
                    w.WriteNumber("byteLength", bv.ByteLength);
                    w.WriteNumber("target", bv.Target);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WritePropertyName("buffers");
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteNumber("byteLength", binBytes.Length);
                w.WriteEndObject();
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.Flush();
        }

        // Pad JSON chunk to 4-byte boundary with 0x20 (ASCII space) per glTF 2.0 specification
        while ((jsonStream.Length & 3) != 0)
            jsonStream.WriteByte(0x20);

        byte[] jsonBytes = jsonStream.ToArray();
        uint totalLength = (uint)(12 + 8 + jsonBytes.Length + (hasBin ? 8 + binBytes.Length : 0));

        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], (uint)jsonBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], ChunkTypeJson);
        output.Write(header);
        output.Write(jsonBytes, 0, jsonBytes.Length);

        if (hasBin)
        {
            Span<byte> binHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(binHeader[0..4], (uint)binBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(binHeader[4..8], ChunkTypeBin);
            output.Write(binHeader);
            output.Write(binBytes, 0, binBytes.Length);
        }
    }

    private static void WriteAssetExtras(Utf8JsonWriter w, ModelScene scene, bool convertedToYUp)
    {
        w.WriteStartObject();
        w.WriteString("schema", scene.Schema);
        w.WriteString("title", scene.Title);
        w.WriteString("units", scene.Units);
        w.WriteString("sourceCoordinateSystem", scene.CoordinateSystem);
        w.WriteBoolean("gltfYUpConverted", convertedToYUp);

        w.WritePropertyName("rtcOffset");
        w.WriteStartArray();
        foreach (double v in scene.RtcOffset) w.WriteNumberValue(v);
        w.WriteEndArray();

        if (scene.SharedTransform != null)
        {
            w.WritePropertyName("sharedTransform");
            w.WriteStartArray();
            foreach (double v in scene.SharedTransform) w.WriteNumberValue(v);
            w.WriteEndArray();
        }

        var c = scene.Completeness;
        w.WritePropertyName("completeness");
        w.WriteStartObject();
        w.WriteNumber("totalElements", c.TotalElements);
        w.WriteNumber("exportedElements", c.ExportedElements);
        w.WriteNumber("instancedElements", c.InstancedElements);
        w.WriteNumber("reusableMeshCount", c.ReusableMeshCount);
        w.WriteNumber("instanceCount", c.InstanceCount);
        w.WriteBoolean("isPartialGeometry", c.IsPartialGeometry);
        w.WritePropertyName("notes");
        w.WriteStartArray();
        foreach (string n in c.Notes) w.WriteStringValue(n);
        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter w, NodeDef node)
    {
        w.WriteStartObject();
        w.WriteString("name", node.Name);

        if (node.MeshIndex.HasValue)
            w.WriteNumber("mesh", node.MeshIndex.Value);

        if (node.Rotation != null)
        {
            w.WritePropertyName("rotation");
            w.WriteStartArray();
            foreach (double r in node.Rotation) w.WriteNumberValue(r);
            w.WriteEndArray();
        }

        if (node.Matrix != null)
        {
            w.WritePropertyName("matrix");
            w.WriteStartArray();
            foreach (float m in node.Matrix) w.WriteNumberValue(m);
            w.WriteEndArray();
        }

        if (node.Children is { Count: > 0 })
        {
            w.WritePropertyName("children");
            w.WriteStartArray();
            foreach (int c in node.Children) w.WriteNumberValue(c);
            w.WriteEndArray();
        }

        if (node.Element != null)
        {
            var el = node.Element;
            w.WritePropertyName("extras");
            w.WriteStartObject();
            w.WriteNumber("elementId", el.Id);
            if (el.UniqueId != null) w.WriteString("uniqueId", el.UniqueId);
            w.WriteString("category", el.Category);
            if (el.CategoryId.HasValue) w.WriteNumber("categoryId", el.CategoryId.Value);
            if (el.FamilyName != null) w.WriteString("familyName", el.FamilyName);
            if (el.TypeName != null) w.WriteString("typeName", el.TypeName);
            if (el.Level != null) w.WriteString("level", el.Level);
            if (el.HostId.HasValue) w.WriteNumber("hostId", el.HostId.Value);

            if (el.PropertyGroups.Count > 0)
            {
                w.WritePropertyName("properties");
                w.WriteStartObject();
                foreach (var pg in el.PropertyGroups)
                {
                    w.WritePropertyName(pg.Group);
                    w.WriteStartObject();
                    foreach (var p in pg.Properties)
                        w.WriteString(p.Name, p.Value);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        else if (node.LeafElementId.HasValue)
        {
            w.WritePropertyName("extras");
            w.WriteStartObject();
            w.WriteNumber("elementId", node.LeafElementId.Value);
            w.WriteEndObject();
        }
        else if (node.CategoryName != null)
        {
            w.WritePropertyName("extras");
            w.WriteStartObject();
            w.WriteString("kind", "category");
            w.WriteString("category", node.CategoryName);
            if (node.CategoryId.HasValue) w.WriteNumber("categoryId", node.CategoryId.Value);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static bool IsIdentityMatrix(float[] m)
    {
        if (m.Length < 16) return true;
        return MathF.Abs(m[0] - 1f) < 1e-6f && MathF.Abs(m[1]) < 1e-6f && MathF.Abs(m[2]) < 1e-6f && MathF.Abs(m[3]) < 1e-6f
            && MathF.Abs(m[4]) < 1e-6f && MathF.Abs(m[5] - 1f) < 1e-6f && MathF.Abs(m[6]) < 1e-6f && MathF.Abs(m[7]) < 1e-6f
            && MathF.Abs(m[8]) < 1e-6f && MathF.Abs(m[9]) < 1e-6f && MathF.Abs(m[10] - 1f) < 1e-6f && MathF.Abs(m[11]) < 1e-6f
            && MathF.Abs(m[12]) < 1e-6f && MathF.Abs(m[13]) < 1e-6f && MathF.Abs(m[14]) < 1e-6f && MathF.Abs(m[15] - 1f) < 1e-6f;
    }

    private static void WriteFloat32Buffer(Stream s, float[] data)
    {
        byte[] buf = new byte[data.Length * 4];
        var span = buf.AsSpan();
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * 4, 4), data[i]);
        s.Write(buf, 0, buf.Length);
    }

    private static void WriteUInt16Buffer(Stream s, int[] indices)
    {
        byte[] buf = new byte[indices.Length * 2];
        var span = buf.AsSpan();
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2, 2), (ushort)indices[i]);
        s.Write(buf, 0, buf.Length);
    }

    private static void WriteUInt32Buffer(Stream s, int[] indices)
    {
        byte[] buf = new byte[indices.Length * 4];
        var span = buf.AsSpan();
        for (int i = 0; i < indices.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(i * 4, 4), (uint)indices[i]);
        s.Write(buf, 0, buf.Length);
    }

    private sealed record BufferViewDef(int ByteOffset, int ByteLength, int Target);

    private sealed record AccessorDef(
        int BufferView,
        int ComponentType,
        int Count,
        string Type,
        float[]? Min,
        float[]? Max);

    private sealed class NodeDef
    {
        public required string Name { get; init; }
        public int? MeshIndex { get; init; }
        public double[]? Rotation { get; init; }
        public float[]? Matrix { get; init; }
        public List<int>? Children { get; init; }
        public SceneElement? Element { get; init; }
        public int? LeafElementId { get; init; }
        public string? CategoryName { get; init; }
        public int? CategoryId { get; init; }
        public bool IsRoot { get; init; }
    }
}
