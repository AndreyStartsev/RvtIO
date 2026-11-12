using System.Globalization;
using System.Reflection;
using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Meshes;
using RvtIO.Model;

namespace RvtIO.Scene;

/// <summary>
/// Turns a Revit document into a scene (<see cref="SceneDocument"/>): one model, one element per drawable element of the document
/// with its triangle meshes (<see cref="ElementMeshes"/>), its names and its parameters.
/// </summary>
/// <remarks>
/// <para>Lengths are converted from feet to metres (x 0.3048) into the internal coordinates of the document, Z up. A symbol that
/// elements place whole (<see cref="MeshSet.Symbols"/>) is one geometry per mesh, in the frame of its family, and each placement
/// is one instance per mesh with the placement's transform; a mirrored placement keeps its transform, so the winding of the
/// symbol's triangles is not changed. A mesh of an element's own is a geometry used once: its vertices are relative to the
/// lowest corner of the element's own meshes, which is the translation of its instances. Normals and indices are those of
/// the meshes. A mesh with a material has the instance material of that material element: its name and, where the file
/// gives it, its colour (else a neutral grey).</para>
/// <para>An element's <c>SourceId</c> is its Revit UniqueId, <c>Category</c> the English name of its built-in category (else the
/// category the reader gives), <c>TypeName</c> the name of its type element. Its parameters are the property set
/// <c>revit:instance</c>; the parameters of its type are the property set <c>revit:type</c>, one set shared by every element of
/// the type. Values are text: the value the file stores, in Revit internal units, written with the invariant culture; the
/// key of a built-in parameter is <c>revit:bip:&lt;id&gt;</c>, other parameters have no key. Levels, typed values and IFC
/// GlobalIds are not written.</para>
/// </remarks>
public static class RvtSceneReader
{
    private const double MetresPerFoot = 0.3048;
    private const int ModelId = 1;

    /// <summary>Reads the Revit file at <paramref name="path"/> with the options the RvtIO command line tool reads it with for an
    /// IFC export (the Revit 2022 layout, English system family names, faces with distinct openings kept apart) and turns it
    /// into a scene.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, or <see cref="RvtSceneOptions.ModelKey"/> is empty.</exception>
    public static RvtSceneResult Read(string path, RvtSceneOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string key = options?.ModelKey ?? Path.GetFileName(path);
        if (key.Length == 0) throw new ArgumentException("The model key is empty.", nameof(options));
        var readDiagnostics = new RvtReadDiagnostics();
        var document = RvtDocument.Open(path, new RvtReaderOptions
        {
            R22Layout = true,
            SystemFamilyNameLocale = "ENU",
            SeparateFacesWithDistinctOpenings = true,
            Diagnostics = readDiagnostics,
        });
        // A cut read is what the command line tool refuses to export; here the scene is made and the cut is an error.
        var cut = readDiagnostics.LimitHits.Select(hit => new SceneDiagnostic(SceneSeverity.Error, "read-cut",
            $"The stream {hit.Stream} was cut by the reader limit {hit.Limit}: {hit.Detail}. Elements may be missing."));
        return Map(document, ElementMeshes.Extract(document), key, cut);
    }

    /// <summary>Turns an opened document into a scene.</summary>
    /// <param name="document">The document, opened by the caller; open a file of a release other than Revit 2022 with
    /// <see cref="RvtReaderOptions.R22Layout"/>, or its elements have no geometry.</param>
    /// <param name="options">The options; <see cref="RvtSceneOptions.ModelKey"/> is required, as a document has no file name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="RvtSceneOptions.ModelKey"/> is null or empty.</exception>
    public static RvtSceneResult Read(RvtDocument document, RvtSceneOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (options?.ModelKey is not { Length: > 0 } key)
            throw new ArgumentException("A document has no file name: give the model key in RvtSceneOptions.ModelKey.", nameof(options));
        return Map(document, ElementMeshes.Extract(document), key);
    }

    /// <summary>The scene of the meshes of <paramref name="document"/>; the element facts come from the document.</summary>
    internal static RvtSceneResult Map(RvtDocument document, MeshSet meshes, string modelKey, IEnumerable<SceneDiagnostic>? diagnostics = null)
    {
        var scene = new SceneDocument { Title = modelKey };
        scene.Models.Add(new SceneModel
        {
            Id = ModelId, Key = modelKey, Name = modelKey, SourceFormat = "rvt", SourceLengthUnit = "ft", Producer = Producer,
        });
        var report = new List<SceneDiagnostic>(diagnostics ?? []);
        if (meshes.GeometryUndecoded)
            report.Add(new(SceneSeverity.Error, "geometry-undecoded",
                "The geometry of the file was not decoded: open it with RvtReaderOptions.R22Layout = true. Its elements have no geometry in the scene."));

        var materials = new HashSet<int>();
        int? MaterialOf(int id)
        {
            if (id <= 0) return null;
            if (materials.Add(id)) scene.Materials.Add(Material(document, id));
            return id;
        }

        var symbols = new Dictionary<int, List<(int GeometryId, int? MaterialId)>>();
        foreach (var (symbolId, symbolMeshes) in meshes.Symbols.OrderBy(symbol => symbol.Key))
            symbols[symbolId] = symbolMeshes.Select(mesh => (AddGeometry(scene, mesh, default), MaterialOf(mesh.MaterialId))).ToList();

        var typeSets = new Dictionary<int, int?>();
        var coverage = new Dictionary<MeshCoverage, int>();
        int withGeometry = 0, placements = 0, withoutUniqueId = 0;
        foreach (var mesh in meshes.Elements)
        {
            var element = document.GetElement(mesh.ElementId);
            var sceneElement = new SceneElement
            {
                Id = mesh.ElementId,
                ModelId = ModelId,
                SourceId = element?.UniqueId,
                Kind = SceneNodeKind.Element,
                Name = element?.Name ?? string.Empty,
                Category = CategoryName(element),
                TypeName = element != null ? document.GetTypeElement(element)?.Name ?? string.Empty : string.Empty,
            };
            if (sceneElement.SourceId == null) withoutUniqueId++;
            if (element != null)
            {
                if (AddPropertySet(scene, "Instance", "revit:instance", element.Parameters) is int instanceSet)
                    sceneElement.PropertySetIds.Add(instanceSet);
                if (document.GetTypeElement(element) is { } type)
                {
                    if (!typeSets.TryGetValue(type.Id, out int? typeSet))
                        typeSets[type.Id] = typeSet = AddPropertySet(scene, "Type", "revit:type", type.Parameters);
                    if (typeSet is int set) sceneElement.PropertySetIds.Add(set);
                }
            }
            scene.Elements.Add(sceneElement);

            int instancesBefore = scene.Instances.Count;
            foreach (var placement in mesh.Placements)
            {
                placements++;
                var t = placement.Transform;
                var transform = new Transform3d(Vector(t.BasisX), Vector(t.BasisY), Vector(t.BasisZ), Vector(t.Origin) * MetresPerFoot);
                foreach (var (geometryId, materialId) in symbols[placement.SymbolId])
                    scene.Instances.Add(new SceneInstance { ElementId = mesh.ElementId, GeometryId = geometryId, Transform = transform, MaterialId = materialId });
            }
            if (mesh.Meshes.Count > 0)
            {
                var origin = LowestCorner(mesh.Meshes);
                var transform = Transform3d.Translate(Vector(origin) * MetresPerFoot);
                foreach (var own in mesh.Meshes)
                    scene.Instances.Add(new SceneInstance
                    {
                        ElementId = mesh.ElementId, GeometryId = AddGeometry(scene, own, origin), Transform = transform, MaterialId = MaterialOf(own.MaterialId),
                    });
            }
            if (scene.Instances.Count > instancesBefore) withGeometry++;

            coverage[mesh.Coverage] = coverage.GetValueOrDefault(mesh.Coverage) + 1;
            if (CoverageDiagnostic(mesh, sceneElement.SourceId) is { } diagnostic) report.Add(diagnostic);
        }
        if (withoutUniqueId > 0)
            report.Add(new(SceneSeverity.Warning, "no-source-id",
                $"{withoutUniqueId} elements have no Revit UniqueId: their SourceId is empty and they cannot be identified outside this scene."));

        return new RvtSceneResult
        {
            Document = scene,
            Diagnostics = report,
            ElementCount = scene.Elements.Count,
            ElementsWithGeometry = withGeometry,
            Coverage = coverage,
            GeometryCount = scene.Geometries.Count,
            InstanceCount = scene.Instances.Count,
            PlacementCount = placements,
        };
    }

    /// <summary>"RvtIO.Scene" and the version of this assembly without the source revision.</summary>
    internal static string Producer { get; } = "RvtIO.Scene " + (typeof(RvtSceneReader).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(RvtSceneReader).Assembly.GetName().Version?.ToString(3));

    private static SceneDiagnostic? CoverageDiagnostic(ElementMesh mesh, string? sourceId)
    {
        var (code, text) = mesh.Coverage switch
        {
            MeshCoverage.Partial => ("partial-geometry",
                $"may miss parts of its geometry ({mesh.UntriangulatedFaces} faces not triangulated; a placed symbol or a Geometry node may have no decoded polygon)"),
            MeshCoverage.BoxSubstitute => ("box-substitute", "places a rescaled symbol, which is drawn as its bounding box"),
            MeshCoverage.NoGeometry => ("no-geometry", "has a geometry record with nothing that could be drawn; it has no geometry in the scene"),
            MeshCoverage.Undecoded => ("undecoded", "has no geometry in the scene: the geometry of the file was not decoded"),
            _ => (null, null),
        };
        return code == null ? null : new SceneDiagnostic(SceneSeverity.Warning, code, $"Element {mesh.ElementId} {text}.", sourceId);
    }

    private static string CategoryName(Element? element) =>
        element?.CategoryId is int id && ((BuiltInCategory)id).TryGetCategoryName(out string? name) ? name : element?.Category ?? string.Empty;

    /// <summary>Adds the set of the parameters that have a value; null when none has.</summary>
    private static int? AddPropertySet(SceneDocument scene, string name, string key, IReadOnlyList<Parameter> parameters)
    {
        var properties = new List<SceneProperty>();
        foreach (var parameter in parameters)
        {
            string? text = parameter.Value switch
            {
                null => null,
                string value => value,
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                var value => value.ToString(),
            };
            if (text == null) continue;
            properties.Add(new SceneProperty
            {
                Name = parameter.Definition?.Name ?? parameter.Id.ToString(CultureInfo.InvariantCulture),
                Key = parameter.Id < 0 ? "revit:bip:" + parameter.Id.ToString(CultureInfo.InvariantCulture) : null,
                Value = SceneValue.FromText(text),
            });
        }
        if (properties.Count == 0) return null;
        int id = scene.PropertySets.Count + 1;
        scene.PropertySets.Add(new ScenePropertySet { Id = id, Name = name, Key = key, Properties = properties });
        return id;
    }

    private static SceneMaterial Material(RvtDocument document, int id)
    {
        var element = document.GetElement(id);
        var color = RgbaColor.Gray;
        if (element?.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value is int packed)
        {
            int transparency = element.LookupParameter(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY)?.Value is int percent ? Math.Clamp(percent, 0, 100) : 0;
            // Red is the low byte, as the IFC export of RvtIO reads the parameter.
            color = new RgbaColor((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)Math.Round(255 * (100 - transparency) / 100.0));
        }
        return new SceneMaterial { Id = id, Name = element?.Name ?? $"Material {id}", Color = color };
    }

    /// <summary>Adds the mesh as a geometry in metres, its vertices relative to <paramref name="origin"/> (feet).</summary>
    private static int AddGeometry(SceneDocument scene, TriangleMesh mesh, MeshVector origin)
    {
        var vertices = new Vector3d[mesh.Positions.Length / 3];
        for (int vertex = 0; vertex < vertices.Length; vertex++)
            vertices[vertex] = new Vector3d(
                (mesh.Positions[3 * vertex] - origin.X) * MetresPerFoot,
                (mesh.Positions[3 * vertex + 1] - origin.Y) * MetresPerFoot,
                (mesh.Positions[3 * vertex + 2] - origin.Z) * MetresPerFoot);
        var normals = new Vector3d[mesh.Normals.Length / 3];
        for (int vertex = 0; vertex < normals.Length; vertex++)
            normals[vertex] = new Vector3d(mesh.Normals[3 * vertex], mesh.Normals[3 * vertex + 1], mesh.Normals[3 * vertex + 2]);
        int id = scene.Geometries.Count + 1;
        scene.Geometries.Add(new SceneGeometry { Id = id, Topology = SceneTopology.Triangles, Vertices = vertices, Indices = mesh.Indices, Normals = normals });
        return id;
    }

    /// <summary>The lowest x, y and z of the vertices of <paramref name="meshes"/>, in feet.</summary>
    private static MeshVector LowestCorner(IReadOnlyList<TriangleMesh> meshes)
    {
        double x = double.PositiveInfinity, y = double.PositiveInfinity, z = double.PositiveInfinity;
        foreach (var mesh in meshes)
            for (int index = 0; index + 2 < mesh.Positions.Length; index += 3)
            {
                x = Math.Min(x, mesh.Positions[index]);
                y = Math.Min(y, mesh.Positions[index + 1]);
                z = Math.Min(z, mesh.Positions[index + 2]);
            }
        return double.IsFinite(x) ? new MeshVector(x, y, z) : default;
    }

    private static Vector3d Vector(MeshVector vector) => new(vector.X, vector.Y, vector.Z);
}
