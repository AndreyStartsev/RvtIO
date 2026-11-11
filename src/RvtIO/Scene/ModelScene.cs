namespace RvtIO.Scene;

/// <summary>
/// Versioned, browser-ready 3D scene representation of a Revit model, combining reusable indexed triangle meshes,
/// per-instance transforms, materials, element hierarchy, caller-selected metadata, and completeness diagnostics.
/// </summary>
public sealed class ModelScene
{
    /// <summary>
    /// Current schema identifier of the serialized scene contract.
    /// </summary>
    public const string CurrentSchema = "rvtio-scene/1";

    /// <summary>
    /// Versioned contract identifier (<see cref="CurrentSchema"/>).
    /// </summary>
    public string Schema { get; init; } = CurrentSchema;

    /// <summary>
    /// Human-readable title of the model or document.
    /// </summary>
    public string Title { get; init; } = "Revit Model";

    /// <summary>
    /// Linear unit of all scene coordinates and matrices (<c>"m"</c> for metres).
    /// </summary>
    public string Units { get; init; } = "m";

    /// <summary>
    /// Coordinate convention of the scene (<c>"right-handed-z-up"</c>).
    /// </summary>
    public string CoordinateSystem { get; init; } = "right-handed-z-up";

    /// <summary>
    /// Double-precision world origin offset <c>[x, y, z]</c> in metres subtracted from scene geometry so
    /// 32-bit floating-point vertex buffers retain sub-millimetre precision even at large survey coordinates.
    /// Add <see cref="RtcOffset"/> to any scene-space point to recover world coordinates in metres.
    /// </summary>
    public double[] RtcOffset { get; init; } = [0, 0, 0];

    /// <summary>
    /// Optional 12-value internal-to-shared coordinate transform <c>[basisX(3), basisY(3), basisZ(3), originMetres(3)]</c>,
    /// or null when the document defines no shared-coordinate offset or rotation.
    /// </summary>
    public double[]? SharedTransform { get; init; }

    /// <summary>
    /// Minimum corner <c>[minX, minY, minZ]</c> of the scene bounding box in RTC metres.
    /// </summary>
    public double[] BoundsMin { get; init; } = [0, 0, 0];

    /// <summary>
    /// Maximum corner <c>[maxX, maxY, maxZ]</c> of the scene bounding box in RTC metres.
    /// </summary>
    public double[] BoundsMax { get; init; } = [0, 0, 0];

    /// <summary>
    /// Materials referenced by <see cref="SceneMeshInstance.MaterialIndex"/>.
    /// </summary>
    public IReadOnlyList<SceneMaterial> Materials { get; init; } = [];

    /// <summary>
    /// Reusable indexed triangle mesh prototypes referenced by <see cref="SceneMeshInstance.MeshIndex"/>.
    /// Repeated family instances share a prototype rather than duplicating geometry buffers.
    /// </summary>
    public IReadOnlyList<SceneMeshPrototype> Meshes { get; init; } = [];

    /// <summary>
    /// Placed mesh instances in the scene, linking a <see cref="SceneMeshPrototype"/>, a <see cref="SceneMaterial"/>,
    /// a 4x4 transform matrix in RTC metres, and the owning <see cref="SceneElement"/>.
    /// </summary>
    public IReadOnlyList<SceneMeshInstance> Instances { get; init; } = [];

    /// <summary>
    /// Exported physical elements with stable Revit identity and selected metadata.
    /// </summary>
    public IReadOnlyList<SceneElement> Elements { get; init; } = [];

    /// <summary>
    /// Category hierarchy grouping indices into <see cref="Elements"/> for model browsing.
    /// </summary>
    public IReadOnlyList<SceneCategoryNode> Categories { get; init; } = [];

    /// <summary>
    /// Exported 3D datum grid lines in RTC metres.
    /// </summary>
    public IReadOnlyList<SceneGridLine> Grids { get; init; } = [];

    /// <summary>
    /// Summary of exported and omitted content for user-facing completeness reporting.
    /// </summary>
    public SceneCompletenessSummary Completeness { get; init; } = new();
}

/// <summary>
/// Surface appearance of scene geometry, suitable for PBR glTF 2.0 and WebGL shading.
/// </summary>
public sealed class SceneMaterial
{
    /// <summary>Zero-based index in <see cref="ModelScene.Materials"/>.</summary>
    public int Index { get; init; }

    /// <summary>Source Revit material element ID when backed by a document material, otherwise null.</summary>
    public int? RevitMaterialId { get; init; }

    /// <summary>Display name of the material.</summary>
    public string Name { get; init; } = "Default";

    /// <summary>Linear/sRGB base color channels <c>[r, g, b]</c> in <c>[0..1]</c>.</summary>
    public float[] Color { get; init; } = [0.78f, 0.80f, 0.82f];

    /// <summary>Opacity in <c>[0..1]</c> (<c>1.0</c> = opaque, <c>&lt; 1.0</c> = translucent such as glazing).</summary>
    public float Opacity { get; init; } = 1.0f;

    /// <summary>PBR metallic factor in <c>[0..1]</c>.</summary>
    public float Metallic { get; init; } = 0.05f;

    /// <summary>PBR roughness factor in <c>[0..1]</c>.</summary>
    public float Roughness { get; init; } = 0.65f;

    /// <summary>Whether both sides of triangles should be rendered.</summary>
    public bool DoubleSided { get; init; } = true;
}

/// <summary>
/// Reusable indexed triangle mesh prototype in metres.
/// </summary>
public sealed class SceneMeshPrototype
{
    /// <summary>Zero-based index in <see cref="ModelScene.Meshes"/>.</summary>
    public int Index { get; init; }

    /// <summary>Source family symbol element ID when shared across family instances; null for element-owned geometry.</summary>
    public int? SourceSymbolId { get; init; }

    /// <summary>True when triangle winding was reversed for negative-determinant (mirrored) instance transforms.</summary>
    public bool IsMirroredVariant { get; init; }

    /// <summary>Packed vertex positions <c>[x0, y0, z0, x1, y1, z1, ...]</c> in metres.</summary>
    public float[] Positions { get; init; } = [];

    /// <summary>Packed unit vertex normals <c>[nx0, ny0, nz0, ...]</c> matching <see cref="Positions"/>.</summary>
    public float[] Normals { get; init; } = [];

    /// <summary>Zero-based triangle vertex indices (<c>3 * TriangleCount</c> entries, counter-clockwise outward).</summary>
    public int[] Indices { get; init; } = [];

    /// <summary>Minimum local corner <c>[minX, minY, minZ]</c> in metres.</summary>
    public float[] LocalBoundsMin { get; init; } = [0, 0, 0];

    /// <summary>Maximum local corner <c>[maxX, maxY, maxZ]</c> in metres.</summary>
    public float[] LocalBoundsMax { get; init; } = [0, 0, 0];

    /// <summary>Number of vertices in this mesh prototype.</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles in this mesh prototype.</summary>
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Single placed occurrence of a <see cref="SceneMeshPrototype"/> belonging to a <see cref="SceneElement"/>.
/// </summary>
public sealed class SceneMeshInstance
{
    /// <summary>Index into <see cref="ModelScene.Meshes"/>.</summary>
    public int MeshIndex { get; init; }

    /// <summary>Index into <see cref="ModelScene.Elements"/>.</summary>
    public int ElementIndex { get; init; }

    /// <summary>Owning Revit element ID (<see cref="SceneElement.Id"/>).</summary>
    public int ElementId { get; init; }

    /// <summary>Index into <see cref="ModelScene.Materials"/>.</summary>
    public int MaterialIndex { get; init; }

    /// <summary>
    /// Column-major 4x4 affine transform matrix (16 floats, glTF / WebGL layout) mapping prototype-local metres
    /// into scene RTC metres.
    /// </summary>
    public float[] Matrix { get; init; } =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    /// <summary>Minimum world-aligned corner <c>[minX, minY, minZ]</c> of this placed instance in RTC metres.</summary>
    public float[] BoundsMin { get; init; } = [0, 0, 0];

    /// <summary>Maximum world-aligned corner <c>[maxX, maxY, maxZ]</c> of this placed instance in RTC metres.</summary>
    public float[] BoundsMax { get; init; } = [0, 0, 0];
}

/// <summary>
/// Physical Revit element in the exported scene with stable identity, hierarchy placement, and selected properties.
/// </summary>
public sealed class SceneElement
{
    /// <summary>Revit element ID.</summary>
    public int Id { get; init; }

    /// <summary>Stable Revit <c>UniqueId</c> when present on the element.</summary>
    public string? UniqueId { get; init; }

    /// <summary>Display name of the element.</summary>
    public string Name { get; init; } = "";

    /// <summary>English category display name (for example <c>"Walls"</c>, <c>"Doors"</c>).</summary>
    public string Category { get; init; } = "Uncategorized";

    /// <summary>Raw Revit built-in or user category ID when known.</summary>
    public int? CategoryId { get; init; }

    /// <summary>Family name when resolved.</summary>
    public string? FamilyName { get; init; }

    /// <summary>Element type / symbol name when resolved.</summary>
    public string? TypeName { get; init; }

    /// <summary>Associated building level name when resolved.</summary>
    public string? Level { get; init; }

    /// <summary>Host element ID (for example the wall hosting a door or window) when resolved.</summary>
    public int? HostId { get; init; }

    /// <summary>True when this element's geometry reuses shared symbol prototypes via instancing.</summary>
    public bool IsInstanced { get; init; }

    /// <summary>Indices into <see cref="ModelScene.Instances"/> rendered for this element.</summary>
    public IReadOnlyList<int> InstanceIndices { get; init; } = [];

    /// <summary>Grouped key/value metadata properties exported for this element.</summary>
    public IReadOnlyList<ScenePropertyGroup> PropertyGroups { get; init; } = [];
}

/// <summary>
/// Named group of element properties (for example <c>"Constraints"</c>, <c>"Dimensions"</c>, <c>"Identity Data"</c>).
/// </summary>
public sealed class ScenePropertyGroup
{
    /// <summary>Display title of the property group.</summary>
    public string Group { get; init; } = "Properties";

    /// <summary>Properties belonging to this group.</summary>
    public IReadOnlyList<SceneProperty> Properties { get; init; } = [];
}

/// <summary>
/// Single formatted property entry on a <see cref="SceneElement"/>.
/// </summary>
public sealed class SceneProperty
{
    /// <summary>Human-readable property name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Formatted display value.</summary>
    public string Value { get; init; } = "";
}

/// <summary>
/// Category group in the scene hierarchy.
/// </summary>
public sealed class SceneCategoryNode
{
    /// <summary>Category display name.</summary>
    public string Category { get; init; } = "";

    /// <summary>Revit category ID when known.</summary>
    public int? CategoryId { get; init; }

    /// <summary>Indices into <see cref="ModelScene.Elements"/> belonging to this category.</summary>
    public IReadOnlyList<int> ElementIndices { get; init; } = [];
}

/// <summary>
/// Datum grid line in scene RTC metres.
/// </summary>
public sealed class SceneGridLine
{
    /// <summary>Revit element ID of the grid.</summary>
    public int Id { get; init; }

    /// <summary>Grid bubble label (for example <c>"A"</c> or <c>"1"</c>).</summary>
    public string Name { get; init; } = "";

    /// <summary>Start point <c>[x, y, z]</c> in RTC metres.</summary>
    public float[] Start { get; init; } = [0, 0, 0];

    /// <summary>End point <c>[x, y, z]</c> in RTC metres.</summary>
    public float[] End { get; init; } = [0, 0, 0];
}

/// <summary>
/// User-facing completeness summary describing what was exported into the 3D scene and what was omitted.
/// </summary>
public sealed class SceneCompletenessSummary
{
    /// <summary>Total element records present in the source document.</summary>
    public int TotalElements { get; init; }

    /// <summary>Physical candidate elements evaluated after skipping internal/orphan records.</summary>
    public int ConsideredElements { get; init; }

    /// <summary>Physical elements exported with 3D geometry.</summary>
    public int ExportedElements { get; init; }

    /// <summary>Exported elements rendered via shared symbol mesh instancing.</summary>
    public int InstancedElements { get; init; }

    /// <summary>Number of unique <see cref="SceneMeshPrototype"/> buffers in the scene.</summary>
    public int ReusableMeshCount { get; init; }

    /// <summary>Total placed <see cref="SceneMeshInstance"/> occurrences in the scene.</summary>
    public int InstanceCount { get; init; }

    /// <summary>Total unique vertices across all mesh prototypes.</summary>
    public int VertexCount { get; init; }

    /// <summary>Total rendered triangles across all placed instances.</summary>
    public int TriangleCount { get; init; }

    /// <summary>
    /// True when the document had undecoded geometry (for example read without <see cref="RvtReaderOptions.R22Layout"/>
    /// on a newer file) or physical model elements lacked representable 3D meshes.
    /// </summary>
    public bool IsPartialGeometry { get; init; }

    /// <summary>Plain user-facing notes describing completeness or limitations without internal implementation details.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Breakdown of omitted elements by user-friendly category/reason.</summary>
    public IReadOnlyList<SceneOmittedGroup> OmittedGroups { get; init; } = [];
}

/// <summary>
/// Count of source elements omitted from the 3D scene for a single user-readable reason.
/// </summary>
public sealed class SceneOmittedGroup
{
    /// <summary>Stable machine-readable reason code.</summary>
    public string Code { get; init; } = "";

    /// <summary>User-friendly description suitable for display in a viewer UI.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Number of elements omitted for this reason.</summary>
    public int Count { get; init; }
}
