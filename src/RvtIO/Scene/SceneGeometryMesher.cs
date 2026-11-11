using RvtIO.Geometry.Meshing;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Scene;

/// <summary>
/// Converts decoded <see cref="GeometryData"/> (B-rep mesh nodes, planar faces with openings, or extruded solids)
/// into compact indexed triangle sub-meshes grouped by resolved Revit material ID.
/// </summary>
internal static class SceneGeometryMesher
{
    internal const double FeetToMetres = 0.3048;
    private const double MeshLod = 0.2;
    private const float WeldCellSizeMetres = 0.0001f; // 0.1 mm

    internal sealed record MeshedSubpart(
        int MaterialSlotId,
        float[] Positions,
        float[] Normals,
        int[] Indices,
        float[] BoundsMin,
        float[] BoundsMax);

    internal static List<MeshedSubpart> MeshGeometry(
        GeometryData geo,
        IReadOnlyDictionary<int, (int Material, int Category)>? graphicsStyles,
        Func<(double X, double Y, double Z), (double X, double Y, double Z)>? worldFeetToRtcMetres = null,
        Func<(double X, double Y, double Z), (double X, double Y, double Z)>? worldDirectionMap = null)
    {
        var builders = new Dictionary<int, SubpartBuilder>();
        SubpartBuilder GetBuilder(int matId)
        {
            if (!builders.TryGetValue(matId, out var b))
                builders[matId] = b = new SubpartBuilder(matId);
            return b;
        }

        (double X, double Y, double Z) MapPointFeet(double fx, double fy, double fz)
        {
            if (worldFeetToRtcMetres != null)
                return worldFeetToRtcMetres((fx, fy, fz));
            return (fx * FeetToMetres, fy * FeetToMetres, fz * FeetToMetres);
        }

        (double X, double Y, double Z) MapDir(double dx, double dy, double dz)
        {
            if (worldDirectionMap != null)
                return worldDirectionMap((dx, dy, dz));
            return (dx, dy, dz);
        }

        int ResolveFaceMaterial(GeoFace? face, int styleOverride = -1)
        {
            if (face != null)
            {
                int direct = face.RenderStyleId ?? face.NestedRenderStyleId ?? 0;
                if (direct > 0) return direct;
                if (face.GeometryStyle > 0 && graphicsStyles != null
                    && graphicsStyles.TryGetValue(face.GeometryStyle, out var gs) && gs.Material > 0)
                    return gs.Material;
            }
            if (styleOverride > 0 && graphicsStyles != null
                && graphicsStyles.TryGetValue(styleOverride, out var styleGs) && styleGs.Material > 0)
                return styleGs.Material;
            if (geo.GStyleElemId > 0 && graphicsStyles != null
                && graphicsStyles.TryGetValue(geo.GStyleElemId, out var rootGs) && rootGs.Material > 0)
                return rootGs.Material;
            return 0;
        }

        // 1. Curved / exact B-rep triangle nodes (FaceMesher / FreeformSolid)
        var writtenNodes = new HashSet<int>();
        if (GRepParserV2.MeshEnabled && geo.MeshNodes is { Count: > 0 } nodes)
        {
            foreach (var node in nodes)
            {
                if (TryAppendMeshNode(geo, node, ResolveFaceMaterial, GetBuilder, MapPointFeet, MapDir))
                    writtenNodes.Add(node.Node);
            }
        }

        // 2. Planar / polygon faces (including inner hole loops via GeTess)
        bool hasTransform = geo.Transform is { Length: >= 12 } && geo.Faces.Count > 0 && IsLikelyLocalFaces(geo);
        double[]? geoTrf = hasTransform ? geo.Transform : null;

        foreach (var face in geo.Faces)
        {
            if (face.GeometryNode != 0 && writtenNodes.Contains(face.GeometryNode))
                continue;
            if (face.OuterVertices is not { Count: >= 3 })
                continue;

            int matSlot = ResolveFaceMaterial(face);
            AppendPlanarFace(face, geoTrf, GetBuilder(matSlot), MapPointFeet, MapDir);
        }

        // 3. Fallback extruded solids (e.g. walls with rectangular openings when Faces is empty)
        if (builders.Values.All(b => b.Indices.Count == 0) && geo.Solids is { Count: > 0 })
        {
            int matSlot = ResolveFaceMaterial(null);
            var builder = GetBuilder(matSlot);
            foreach (var solid in geo.Solids)
                AppendExtrudedSolid(solid, builder, MapPointFeet, MapDir);
        }

        var output = new List<MeshedSubpart>(builders.Count);
        foreach (var kv in builders.OrderBy(k => k.Key))
        {
            if (kv.Value.TryBuild(out var subpart))
                output.Add(subpart);
        }
        return output;
    }

    private static bool IsLikelyLocalFaces(GeometryData geo)
    {
        // ElementBuilder already transforms Faces into world coordinates when expanding symbols;
        // geo.Transform is only needed when BoundingBox is absent or matches local unplaced coordinates.
        return false;
    }

    private static bool TryAppendMeshNode(
        GeometryData geo,
        GeometryNode node,
        Func<GeoFace?, int, int> resolveFaceMaterial,
        Func<int, SubpartBuilder> getBuilder,
        Func<double, double, double, (double X, double Y, double Z)> mapPointFeet,
        Func<double, double, double, (double X, double Y, double Z)> mapDir)
    {
        var nodeFaces = geo.Faces.Where(f => f.GeometryNode == node.Node).ToList();
        if (!node.Ghost && (nodeFaces.Count == 0 || nodeFaces.Any(face =>
                (face.SourceFace == 0 && face.MeshMarker == 0)
                || !node.Faces.Any(mf => (uint)mf.Marker == (face.SourceFace != 0 ? face.SourceFace : face.MeshMarker)))))
        {
            return false;
        }

        var placement = node.Placement;
        bool mirroredPlacement = placement is { Length: >= 12 } && DeterminantRowMajor3x4(placement) < 0;

        (double X, double Y, double Z) PlaceFeet(double[] p)
        {
            if (placement is not { Length: >= 12 }) return (p[0], p[1], p[2]);
            return (
                placement[9] + p[0] * placement[0] + p[1] * placement[1] + p[2] * placement[2],
                placement[10] + p[0] * placement[3] + p[1] * placement[4] + p[2] * placement[5],
                placement[11] + p[0] * placement[6] + p[1] * placement[7] + p[2] * placement[8]);
        }

        FreeformSolid? freeformSolid = null;
        if (node.Faces.Any(FreeformSolid.IsFreeform))
        {
            freeformSolid = FreeformSolid.Create(node.Faces, MeshLod, node.Diagonal);
            if (freeformSolid == null) return false;
        }

        var faceMeshes = new List<(BrFace Face, Mesh Mesh)>(node.Faces.Count);
        foreach (var brFace in node.Faces)
        {
            Mesh? mesh;
            bool ok = FreeformSolid.IsFreeform(brFace)
                ? freeformSolid!.TryMesh(brFace, out mesh)
                : FaceMesher.TryMesh(GRepParserV2.WithSamples(brFace, MeshLod, node.Diagonal, freeformSolid), MeshLod, node.Diagonal, out mesh);
            if (!ok || mesh == null) return false;
            faceMeshes.Add((brFace, mesh));
        }

        if (faceMeshes.Sum(fm => fm.Mesh.Triangles.Length) == 0)
            return false;

        // Verify bounding box consistency against polygon faces (same guard as IfcExporter.Mesh.cs)
        if (!node.Ghost && nodeFaces.Count > 0)
        {
            double[] meshMin = [double.MaxValue, double.MaxValue, double.MaxValue];
            double[] meshMax = [double.MinValue, double.MinValue, double.MinValue];
            foreach (var (_, m) in faceMeshes)
            {
                foreach (var v in m.Vertices)
                {
                    var pf = PlaceFeet(v);
                    meshMin[0] = Math.Min(meshMin[0], pf.X); meshMax[0] = Math.Max(meshMax[0], pf.X);
                    meshMin[1] = Math.Min(meshMin[1], pf.Y); meshMax[1] = Math.Max(meshMax[1], pf.Y);
                    meshMin[2] = Math.Min(meshMin[2], pf.Z); meshMax[2] = Math.Max(meshMax[2], pf.Z);
                }
            }
            double[] polyMin = [double.MaxValue, double.MaxValue, double.MaxValue];
            double[] polyMax = [double.MinValue, double.MinValue, double.MinValue];
            foreach (var v in nodeFaces.Where(f => f.OuterVertices != null).SelectMany(f => f.OuterVertices!))
            {
                polyMin[0] = Math.Min(polyMin[0], v[0]); polyMax[0] = Math.Max(polyMax[0], v[0]);
                polyMin[1] = Math.Min(polyMin[1], v[1]); polyMax[1] = Math.Max(polyMax[1], v[1]);
                polyMin[2] = Math.Min(polyMin[2], v[2]); polyMax[2] = Math.Max(polyMax[2], v[2]);
            }
            for (int k = 0; k < 3; k++)
            {
                if (Math.Abs(meshMin[k] - polyMin[k]) > 0.05 || Math.Abs(meshMax[k] - polyMax[k]) > 0.05)
                    return false;
            }
        }

        // Map marker -> GeoFace for per-face material lookup
        var faceByMarker = new Dictionary<uint, GeoFace>();
        foreach (var gf in nodeFaces)
        {
            uint marker = gf.SourceFace != 0 ? gf.SourceFace : gf.MeshMarker;
            if (marker != 0) faceByMarker.TryAdd(marker, gf);
        }

        foreach (var (brFace, mesh) in faceMeshes)
        {
            if (mesh.Triangles.Length == 0 || mesh.Vertices.Length == 0) continue;
            faceByMarker.TryGetValue((uint)brFace.Marker, out var matchingGeoFace);
            int matSlot = resolveFaceMaterial(matchingGeoFace, node.Style);
            var builder = getBuilder(matSlot);

            int vCount = mesh.Vertices.Length;
            var mappedVerts = new (double X, double Y, double Z)[vCount];
            for (int i = 0; i < vCount; i++)
            {
                var pf = PlaceFeet(mesh.Vertices[i]);
                mappedVerts[i] = mapPointFeet(pf.X, pf.Y, pf.Z);
            }

            bool smoothNormals = brFace.SurfaceKind != SurfaceKind.Plane;
            if (smoothNormals)
            {
                var accum = new (double Nx, double Ny, double Nz)[vCount];
                foreach (var tri in mesh.Triangles)
                {
                    if (tri.Length < 3) continue;
                    int i0 = tri[0], i1 = mirroredPlacement ? tri[2] : tri[1], i2 = mirroredPlacement ? tri[1] : tri[2];
                    if ((uint)i0 >= (uint)vCount || (uint)i1 >= (uint)vCount || (uint)i2 >= (uint)vCount) continue;
                    var p0 = mappedVerts[i0]; var p1 = mappedVerts[i1]; var p2 = mappedVerts[i2];
                    var (nx, ny, nz) = Cross(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z, p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
                    accum[i0] = (accum[i0].Nx + nx, accum[i0].Ny + ny, accum[i0].Nz + nz);
                    accum[i1] = (accum[i1].Nx + nx, accum[i1].Ny + ny, accum[i1].Nz + nz);
                    accum[i2] = (accum[i2].Nx + nx, accum[i2].Ny + ny, accum[i2].Nz + nz);
                }

                var outIndices = new int[vCount];
                for (int i = 0; i < vCount; i++)
                {
                    var n = NormalizeOrDefault(accum[i].Nx, accum[i].Ny, accum[i].Nz);
                    outIndices[i] = builder.AddVertex(mappedVerts[i], n);
                }

                foreach (var tri in mesh.Triangles)
                {
                    if (tri.Length < 3) continue;
                    int i0 = tri[0], i1 = mirroredPlacement ? tri[2] : tri[1], i2 = mirroredPlacement ? tri[1] : tri[2];
                    if ((uint)i0 >= (uint)vCount || (uint)i1 >= (uint)vCount || (uint)i2 >= (uint)vCount) continue;
                    builder.AddTriangleIndices(outIndices[i0], outIndices[i1], outIndices[i2]);
                }
            }
            else
            {
                foreach (var tri in mesh.Triangles)
                {
                    if (tri.Length < 3) continue;
                    int i0 = tri[0], i1 = mirroredPlacement ? tri[2] : tri[1], i2 = mirroredPlacement ? tri[1] : tri[2];
                    if ((uint)i0 >= (uint)vCount || (uint)i1 >= (uint)vCount || (uint)i2 >= (uint)vCount) continue;
                    builder.AddFlatTriangle(mappedVerts[i0], mappedVerts[i1], mappedVerts[i2], null);
                }
            }
        }

        return true;
    }

    private static void AppendPlanarFace(
        GeoFace face,
        double[]? optionalLocalTrf,
        SubpartBuilder builder,
        Func<double, double, double, (double X, double Y, double Z)> mapPointFeet,
        Func<double, double, double, (double X, double Y, double Z)> mapDir)
    {
        var outerRaw = CleanRingFeet(face.OuterVertices!, optionalLocalTrf);
        if (outerRaw.Count < 3) return;

        // Compute outward face normal in feet space
        (double Nx, double Ny, double Nz) normalFeet;
        if (face.Normal is { Length: >= 3 } fn
            && double.IsFinite(fn[0]) && double.IsFinite(fn[1]) && double.IsFinite(fn[2])
            && (fn[0] * fn[0] + fn[1] * fn[1] + fn[2] * fn[2]) > 1e-12)
        {
            normalFeet = NormalizeOrDefault(fn[0], fn[1], fn[2]);
        }
        else
        {
            normalFeet = NewellNormal(outerRaw);
        }

        var mappedNormalRaw = mapDir(normalFeet.Nx, normalFeet.Ny, normalFeet.Nz);
        var mappedNormal = NormalizeOrDefault(mappedNormalRaw.X, mappedNormalRaw.Y, mappedNormalRaw.Z);

        bool hasHoles = face.InnerVertices is { Count: > 0 };

        // Fast path for simple triangles and convex quads with no inner openings
        if (!hasHoles && outerRaw.Count == 3)
        {
            var p0 = mapPointFeet(outerRaw[0].X, outerRaw[0].Y, outerRaw[0].Z);
            var p1 = mapPointFeet(outerRaw[1].X, outerRaw[1].Y, outerRaw[1].Z);
            var p2 = mapPointFeet(outerRaw[2].X, outerRaw[2].Y, outerRaw[2].Z);
            builder.AddFlatTriangle(p0, p1, p2, mappedNormal);
            return;
        }

        if (!hasHoles && outerRaw.Count == 4 && IsConvexQuad(outerRaw, normalFeet))
        {
            var p0 = mapPointFeet(outerRaw[0].X, outerRaw[0].Y, outerRaw[0].Z);
            var p1 = mapPointFeet(outerRaw[1].X, outerRaw[1].Y, outerRaw[1].Z);
            var p2 = mapPointFeet(outerRaw[2].X, outerRaw[2].Y, outerRaw[2].Z);
            var p3 = mapPointFeet(outerRaw[3].X, outerRaw[3].Y, outerRaw[3].Z);
            // Ensure CCW wrt normalFeet
            var c = Cross(outerRaw[1].X - outerRaw[0].X, outerRaw[1].Y - outerRaw[0].Y, outerRaw[1].Z - outerRaw[0].Z,
                          outerRaw[2].X - outerRaw[0].X, outerRaw[2].Y - outerRaw[0].Y, outerRaw[2].Z - outerRaw[0].Z);
            if (Dot(c.X, c.Y, c.Z, normalFeet.Nx, normalFeet.Ny, normalFeet.Nz) >= 0)
            {
                builder.AddFlatTriangle(p0, p1, p2, mappedNormal);
                builder.AddFlatTriangle(p0, p2, p3, mappedNormal);
            }
            else
            {
                builder.AddFlatTriangle(p0, p3, p2, mappedNormal);
                builder.AddFlatTriangle(p0, p2, p1, mappedNormal);
            }
            return;
        }

        // General polygon with or without holes: project onto orthonormal 2D plane (U, V) where U x V = normalFeet
        BuildPlaneBasis(normalFeet, face.XVector, out var uAxis, out var vAxis);
        var origin = outerRaw[0];

        (double U, double V) Project2D((double X, double Y, double Z) pt)
        {
            double dx = pt.X - origin.X, dy = pt.Y - origin.Y, dz = pt.Z - origin.Z;
            return (Dot(dx, dy, dz, uAxis.X, uAxis.Y, uAxis.Z), Dot(dx, dy, dz, vAxis.X, vAxis.Y, vAxis.Z));
        }

        var outer2D = outerRaw.Select(Project2D).ToList();
        if (SignedArea2D(outer2D) < 0)
        {
            outerRaw.Reverse();
            outer2D.Reverse();
        }

        var all3D = new List<(double X, double Y, double Z)>(outerRaw);
        var all2D = new List<(double X, double Y)>(outer2D);
        var loops = new List<int[]> { Enumerable.Range(0, outerRaw.Count).ToArray() };
        var outerFlags = new List<bool> { true };

        if (hasHoles)
        {
            foreach (var holeRing in face.InnerVertices!)
            {
                var holeClean = CleanRingFeet(holeRing, optionalLocalTrf);
                if (holeClean.Count < 3) continue;
                var hole2D = holeClean.Select(Project2D).ToList();
                // GeTess expects holes oriented clockwise (negative signed area)
                if (SignedArea2D(hole2D) > 0)
                {
                    holeClean.Reverse();
                    hole2D.Reverse();
                }
                int startIdx = all3D.Count;
                all3D.AddRange(holeClean);
                all2D.AddRange(hole2D);
                loops.Add(Enumerable.Range(startIdx, holeClean.Count).ToArray());
                outerFlags.Add(false);
            }
        }

        List<(int A, int B, int C)> tris;
        try
        {
            tris = GeTess.TriangulateProfile(all2D, loops, outerFlags.ToArray());
        }
        catch
        {
            tris = [];
        }

        // Fallback fan if GeTess returned nothing on a simple polygon without holes
        if (tris.Count == 0 && loops.Count == 1 && outerRaw.Count >= 3)
        {
            for (int i = 1; i < outerRaw.Count - 1; i++)
                tris.Add((0, i, i + 1));
        }

        if (tris.Count == 0) return;

        var mappedPts = new (double X, double Y, double Z)[all3D.Count];
        for (int i = 0; i < all3D.Count; i++)
            mappedPts[i] = mapPointFeet(all3D[i].X, all3D[i].Y, all3D[i].Z);

        foreach (var (a, b, c) in tris)
        {
            if ((uint)a >= (uint)mappedPts.Length || (uint)b >= (uint)mappedPts.Length || (uint)c >= (uint)mappedPts.Length)
                continue;
            builder.AddFlatTriangle(mappedPts[a], mappedPts[b], mappedPts[c], mappedNormal);
        }
    }

    private static void AppendExtrudedSolid(
        GeoSolid solid,
        SubpartBuilder builder,
        Func<double, double, double, (double X, double Y, double Z)> mapPointFeet,
        Func<double, double, double, (double X, double Y, double Z)> mapDir)
    {
        if (solid.ProfileWidth <= 1e-6 || solid.ProfileDepth <= 1e-6 || solid.ExtrusionHeight <= 1e-6)
            return;
        if (solid.Position is not { Length: >= 3 } pos)
            return;

        var zDir = solid.ExtrusionDirection is { Length: >= 3 } ed
            ? NormalizeOrDefault(ed[0], ed[1], ed[2])
            : (Nx: 0.0, Ny: 0.0, Nz: 1.0);
        var xRaw = solid.RefDirection is { Length: >= 3 } rd && (rd[0] * rd[0] + rd[1] * rd[1] + rd[2] * rd[2]) > 1e-12
            ? NormalizeOrDefault(rd[0], rd[1], rd[2])
            : (Nx: 1.0, Ny: 0.0, Nz: 0.0);
        var yCross = Cross(zDir.Nx, zDir.Ny, zDir.Nz, xRaw.Nx, xRaw.Ny, xRaw.Nz);
        var yDir = NormalizeOrDefault(yCross.X, yCross.Y, yCross.Z);

        double hw = solid.ProfileWidth * 0.5;
        double hd = solid.ProfileDepth * 0.5;
        double h = solid.ExtrusionHeight;

        double[] Pt(double u, double v, double w) =>
        [
            pos[0] + u * xRaw.Nx + v * yDir.Nx + w * zDir.Nx,
            pos[1] + u * xRaw.Ny + v * yDir.Ny + w * zDir.Ny,
            pos[2] + u * xRaw.Nz + v * yDir.Nz + w * zDir.Nz,
        ];

        // Collect valid rectangular openings projected onto the (u, w) wall face
        var validOpenings = new List<(double u0, double w0, double u1, double w1)>();
        if (solid.Openings is { Count: > 0 })
        {
            foreach (var op in solid.Openings)
            {
                if (op.Width <= 1e-5 || op.Height <= 1e-5 || op.Position is not { Length: >= 3 }) continue;
                double cu = op.Position[0], cw = op.Position[2];
                double u0 = Math.Max(-hw + 1e-4, cu - op.Width * 0.5);
                double u1 = Math.Min(hw - 1e-4, cu + op.Width * 0.5);
                double w0 = Math.Max(0.0, cw - op.Height * 0.5);
                double w1 = Math.Min(h, cw + op.Height * 0.5);
                if (u1 - u0 > 1e-4 && w1 - w0 > 1e-4)
                    validOpenings.Add((u0, w0, u1, w1));
            }
        }

        // Front (-hd, normal -yDir) and Back (+hd, normal +yDir) faces, with opening holes if any
        var frontFace = new GeoFace
        {
            Normal = [-yDir.Nx, -yDir.Ny, -yDir.Nz],
            XVector = [xRaw.Nx, xRaw.Ny, xRaw.Nz],
            OuterVertices = [Pt(-hw, -hd, 0), Pt(hw, -hd, 0), Pt(hw, -hd, h), Pt(-hw, -hd, h)],
            InnerVertices = validOpenings.Select(o => new List<double[]>
            {
                Pt(o.u0, -hd, o.w0), Pt(o.u1, -hd, o.w0), Pt(o.u1, -hd, o.w1), Pt(o.u0, -hd, o.w1),
            }).ToList(),
        };
        AppendPlanarFace(frontFace, null, builder, mapPointFeet, mapDir);

        var backFace = new GeoFace
        {
            Normal = [yDir.Nx, yDir.Ny, yDir.Nz],
            XVector = [-xRaw.Nx, -xRaw.Ny, -xRaw.Nz],
            OuterVertices = [Pt(hw, hd, 0), Pt(-hw, hd, 0), Pt(-hw, hd, h), Pt(hw, hd, h)],
            InnerVertices = validOpenings.Select(o => new List<double[]>
            {
                Pt(o.u1, hd, o.w0), Pt(o.u0, hd, o.w0), Pt(o.u0, hd, o.w1), Pt(o.u1, hd, o.w1),
            }).ToList(),
        };
        AppendPlanarFace(backFace, null, builder, mapPointFeet, mapDir);

        // Four perimeter cap faces (bottom, top, left -hw, right +hw)
        void AddQuad(double[] a, double[] b, double[] c, double[] d, (double Nx, double Ny, double Nz) n)
        {
            var nr = mapDir(n.Nx, n.Ny, n.Nz);
            var nm = NormalizeOrDefault(nr.X, nr.Y, nr.Z);
            var pa = mapPointFeet(a[0], a[1], a[2]);
            var pb = mapPointFeet(b[0], b[1], b[2]);
            var pc = mapPointFeet(c[0], c[1], c[2]);
            var pd = mapPointFeet(d[0], d[1], d[2]);
            builder.AddFlatTriangle(pa, pb, pc, nm);
            builder.AddFlatTriangle(pa, pc, pd, nm);
        }

        AddQuad(Pt(-hw, hd, 0), Pt(hw, hd, 0), Pt(hw, -hd, 0), Pt(-hw, -hd, 0), (-zDir.Nx, -zDir.Ny, -zDir.Nz));
        AddQuad(Pt(-hw, -hd, h), Pt(hw, -hd, h), Pt(hw, hd, h), Pt(-hw, hd, h), zDir);
        AddQuad(Pt(-hw, hd, 0), Pt(-hw, -hd, 0), Pt(-hw, -hd, h), Pt(-hw, hd, h), (-xRaw.Nx, -xRaw.Ny, -xRaw.Nz));
        AddQuad(Pt(hw, -hd, 0), Pt(hw, hd, 0), Pt(hw, hd, h), Pt(hw, -hd, h), xRaw);

        // Inner reveal jambs/sill/head for each rectangular opening
        foreach (var (u0, w0, u1, w1) in validOpenings)
        {
            AddQuad(Pt(u0, -hd, w0), Pt(u1, -hd, w0), Pt(u1, hd, w0), Pt(u0, hd, w0), zDir); // sill (faces up)
            AddQuad(Pt(u0, hd, w1), Pt(u1, hd, w1), Pt(u1, -hd, w1), Pt(u0, -hd, w1), (-zDir.Nx, -zDir.Ny, -zDir.Nz)); // head (faces down)
            AddQuad(Pt(u0, -hd, w0), Pt(u0, hd, w0), Pt(u0, hd, w1), Pt(u0, -hd, w1), xRaw); // left jamb (faces +u)
            AddQuad(Pt(u1, hd, w0), Pt(u1, -hd, w0), Pt(u1, -hd, w1), Pt(u1, hd, w1), (-xRaw.Nx, -xRaw.Ny, -xRaw.Nz)); // right jamb (faces -u)
        }
    }

    internal static double DeterminantRowMajor3x4(double[] t) =>
        t[0] * (t[4] * t[8] - t[5] * t[7])
        - t[1] * (t[3] * t[8] - t[5] * t[6])
        + t[2] * (t[3] * t[7] - t[4] * t[6]);

    internal static bool TryInvertRowMajor3x4(double[] t, out double[] inv)
    {
        inv = new double[12];
        if (t.Length < 12 || t.Take(12).Any(v => !double.IsFinite(v)))
            return false;

        double det = DeterminantRowMajor3x4(t);
        if (Math.Abs(det) < 1e-12)
            return false;

        double invDet = 1.0 / det;
        inv[0] = (t[4] * t[8] - t[5] * t[7]) * invDet;
        inv[1] = (t[2] * t[7] - t[1] * t[8]) * invDet;
        inv[2] = (t[1] * t[5] - t[2] * t[4]) * invDet;

        inv[3] = (t[5] * t[6] - t[3] * t[8]) * invDet;
        inv[4] = (t[0] * t[8] - t[2] * t[6]) * invDet;
        inv[5] = (t[2] * t[3] - t[0] * t[5]) * invDet;

        inv[6] = (t[3] * t[7] - t[4] * t[6]) * invDet;
        inv[7] = (t[1] * t[6] - t[0] * t[7]) * invDet;
        inv[8] = (t[0] * t[4] - t[1] * t[3]) * invDet;

        inv[9] = -(inv[0] * t[9] + inv[1] * t[10] + inv[2] * t[11]);
        inv[10] = -(inv[3] * t[9] + inv[4] * t[10] + inv[5] * t[11]);
        inv[11] = -(inv[6] * t[9] + inv[7] * t[10] + inv[8] * t[11]);
        return true;
    }

    internal static SceneMeshPrototype CreateMirroredVariant(SceneMeshPrototype source, int newIndex)
    {
        var flipped = new int[source.Indices.Length];
        for (int i = 0; i + 2 < source.Indices.Length; i += 3)
        {
            flipped[i] = source.Indices[i];
            flipped[i + 1] = source.Indices[i + 2];
            flipped[i + 2] = source.Indices[i + 1];
        }
        return new SceneMeshPrototype
        {
            Index = newIndex,
            SourceSymbolId = source.SourceSymbolId,
            IsMirroredVariant = true,
            Positions = source.Positions,
            Normals = source.Normals,
            Indices = flipped,
            LocalBoundsMin = (float[])source.LocalBoundsMin.Clone(),
            LocalBoundsMax = (float[])source.LocalBoundsMax.Clone(),
        };
    }

    private static List<(double X, double Y, double Z)> CleanRingFeet(List<double[]> ring, double[]? trf)
    {
        var clean = new List<(double X, double Y, double Z)>(ring.Count);
        foreach (var pt in ring)
        {
            if (pt is not { Length: >= 3 } || !double.IsFinite(pt[0]) || !double.IsFinite(pt[1]) || !double.IsFinite(pt[2]))
                continue;
            double x = pt[0], y = pt[1], z = pt[2];
            if (trf != null)
            {
                double nx = trf[9] + x * trf[0] + y * trf[1] + z * trf[2];
                double ny = trf[10] + x * trf[3] + y * trf[4] + z * trf[5];
                double nz = trf[11] + x * trf[6] + y * trf[7] + z * trf[8];
                x = nx; y = ny; z = nz;
            }
            if (clean.Count > 0 && DistSq(clean[^1], (x, y, z)) < 1e-16)
                continue;
            clean.Add((x, y, z));
        }
        while (clean.Count > 2 && DistSq(clean[0], clean[^1]) < 1e-16)
            clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    private static bool IsConvexQuad(List<(double X, double Y, double Z)> q, (double Nx, double Ny, double Nz) normal)
    {
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) & 3];
            var c = q[(i + 2) & 3];
            var cr = Cross(b.X - a.X, b.Y - a.Y, b.Z - a.Z, c.X - b.X, c.Y - b.Y, c.Z - b.Z);
            double d = Dot(cr.X, cr.Y, cr.Z, normal.Nx, normal.Ny, normal.Nz);
            if (Math.Abs(d) <= 1e-12) return false;
            if (sign == 0) sign = Math.Sign(d);
            else if (Math.Sign(d) != sign) return false;
        }
        return true;
    }

    private static (double Nx, double Ny, double Nz) NewellNormal(List<(double X, double Y, double Z)> pts)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var cur = pts[i];
            var nxt = pts[(i + 1) % pts.Count];
            nx += (cur.Y - nxt.Y) * (cur.Z + nxt.Z);
            ny += (cur.Z - nxt.Z) * (cur.X + nxt.X);
            nz += (cur.X - nxt.X) * (cur.Y + nxt.Y);
        }
        return NormalizeOrDefault(nx, ny, nz);
    }

    private static void BuildPlaneBasis(
        (double Nx, double Ny, double Nz) normal,
        double[]? hintX,
        out (double X, double Y, double Z) uAxis,
        out (double X, double Y, double Z) vAxis)
    {
        if (hintX is { Length: >= 3 } hx && double.IsFinite(hx[0]) && double.IsFinite(hx[1]) && double.IsFinite(hx[2]))
        {
            double dot = Dot(hx[0], hx[1], hx[2], normal.Nx, normal.Ny, normal.Nz);
            double ux = hx[0] - dot * normal.Nx;
            double uy = hx[1] - dot * normal.Ny;
            double uz = hx[2] - dot * normal.Nz;
            double lenSq = ux * ux + uy * uy + uz * uz;
            if (lenSq > 1e-10)
            {
                double inv = 1.0 / Math.Sqrt(lenSq);
                uAxis = (ux * inv, uy * inv, uz * inv);
                var v = Cross(normal.Nx, normal.Ny, normal.Nz, uAxis.X, uAxis.Y, uAxis.Z);
                var vn = NormalizeOrDefault(v.X, v.Y, v.Z);
                vAxis = (vn.Nx, vn.Ny, vn.Nz);
                return;
            }
        }

        var seed = Math.Abs(normal.Nz) < 0.9 ? (X: 0.0, Y: 0.0, Z: 1.0) : (X: 1.0, Y: 0.0, Z: 0.0);
        var uRaw = Cross(seed.X, seed.Y, seed.Z, normal.Nx, normal.Ny, normal.Nz);
        var un = NormalizeOrDefault(uRaw.X, uRaw.Y, uRaw.Z);
        uAxis = (un.Nx, un.Ny, un.Nz);
        var vRaw = Cross(normal.Nx, normal.Ny, normal.Nz, uAxis.X, uAxis.Y, uAxis.Z);
        var vNorm = NormalizeOrDefault(vRaw.X, vRaw.Y, vRaw.Z);
        vAxis = (vNorm.Nx, vNorm.Ny, vNorm.Nz);
    }

    private static double SignedArea2D(List<(double U, double V)> poly)
    {
        double sum = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            sum += a.U * b.V - b.U * a.V;
        }
        return 0.5 * sum;
    }

    private static double DistSq((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static double Dot(double ax, double ay, double az, double bx, double by, double bz) =>
        ax * bx + ay * by + az * bz;

    private static (double X, double Y, double Z) Cross(double ax, double ay, double az, double bx, double by, double bz) =>
        (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);

    private static (double Nx, double Ny, double Nz) NormalizeOrDefault(double x, double y, double z)
    {
        double lenSq = x * x + y * y + z * z;
        if (!(lenSq > 1e-24) || !double.IsFinite(lenSq))
            return (0, 0, 1);
        double inv = 1.0 / Math.Sqrt(lenSq);
        return (x * inv, y * inv, z * inv);
    }

    private sealed class SubpartBuilder
    {
        private readonly int _materialSlotId;
        private readonly List<float> _positions = new(256);
        private readonly List<float> _normals = new(256);
        private readonly Dictionary<(long, long, long, int, int, int), int> _vertexCache = new();
        public List<int> Indices { get; } = new(256);

        private float _minX = float.MaxValue, _minY = float.MaxValue, _minZ = float.MaxValue;
        private float _maxX = float.MinValue, _maxY = float.MinValue, _maxZ = float.MinValue;

        public SubpartBuilder(int materialSlotId) => _materialSlotId = materialSlotId;

        public int AddVertex((double X, double Y, double Z) pos, (double Nx, double Ny, double Nz) norm)
        {
            float px = (float)pos.X, py = (float)pos.Y, pz = (float)pos.Z;
            float nx = (float)norm.Nx, ny = (float)norm.Ny, nz = (float)norm.Nz;
            if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz))
                return -1;

            // Normalize float normal to guarantee unit length for strict glTF 2.0 validators
            float nLen = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nLen > 1e-6f && float.IsFinite(nLen))
            {
                nx /= nLen; ny /= nLen; nz /= nLen;
            }
            else
            {
                nx = 0f; ny = 0f; nz = 1f;
            }

            long kx = (long)MathF.Round(px / WeldCellSizeMetres);
            long ky = (long)MathF.Round(py / WeldCellSizeMetres);
            long kz = (long)MathF.Round(pz / WeldCellSizeMetres);
            int knx = (int)MathF.Round(nx * 1000f);
            int kny = (int)MathF.Round(ny * 1000f);
            int knz = (int)MathF.Round(nz * 1000f);

            var key = (kx, ky, kz, knx, kny, knz);
            if (_vertexCache.TryGetValue(key, out int existing))
                return existing;

            int idx = _positions.Count / 3;
            _positions.Add(px); _positions.Add(py); _positions.Add(pz);
            _normals.Add(nx); _normals.Add(ny); _normals.Add(nz);

            if (px < _minX) _minX = px; if (px > _maxX) _maxX = px;
            if (py < _minY) _minY = py; if (py > _maxY) _maxY = py;
            if (pz < _minZ) _minZ = pz; if (pz > _maxZ) _maxZ = pz;

            _vertexCache[key] = idx;
            return idx;
        }

        public void AddTriangleIndices(int i0, int i1, int i2)
        {
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 == i1 || i1 == i2 || i0 == i2)
                return;
            Indices.Add(i0);
            Indices.Add(i1);
            Indices.Add(i2);
        }

        public void AddFlatTriangle(
            (double X, double Y, double Z) p0,
            (double X, double Y, double Z) p1,
            (double X, double Y, double Z) p2,
            (double Nx, double Ny, double Nz)? expectedNormal)
        {
            var cr = Cross(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z, p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
            double area2Sq = cr.X * cr.X + cr.Y * cr.Y + cr.Z * cr.Z;
            if (!(area2Sq > 1e-20) || !double.IsFinite(area2Sq))
                return;

            (double Nx, double Ny, double Nz) norm;
            if (expectedNormal is { } exp)
            {
                if (Dot(cr.X, cr.Y, cr.Z, exp.Nx, exp.Ny, exp.Nz) < 0)
                {
                    (p1, p2) = (p2, p1);
                }
                norm = exp;
            }
            else
            {
                double inv = 1.0 / Math.Sqrt(area2Sq);
                norm = (cr.X * inv, cr.Y * inv, cr.Z * inv);
            }

            int i0 = AddVertex(p0, norm);
            int i1 = AddVertex(p1, norm);
            int i2 = AddVertex(p2, norm);
            AddTriangleIndices(i0, i1, i2);
        }

        public bool TryBuild(out MeshedSubpart subpart)
        {
            if (Indices.Count < 3 || _positions.Count < 9)
            {
                subpart = null!;
                return false;
            }
            subpart = new MeshedSubpart(
                _materialSlotId,
                _positions.ToArray(),
                _normals.ToArray(),
                Indices.ToArray(),
                [_minX, _minY, _minZ],
                [_maxX, _maxY, _maxZ]);
            return true;
        }
    }
}
