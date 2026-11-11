using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Compact;
using RvtIO.Ifc;
using RvtIO.Lookup;
using RvtIO.Model;

namespace RvtIO.Scene;

/// <summary>
/// Builds a <see cref="ModelScene"/> from a decoded <see cref="RevitFileInfo"/>.
/// </summary>
internal static class SceneBuilder
{
    private const double FeetToMetres = SceneGeometryMesher.FeetToMetres;
    private const double RtcActivationThresholdMetres = 100.0;

    private static readonly int[] LevelParamIds =
    [
        (int)BuiltInParameter.WALL_BASE_CONSTRAINT,
        (int)BuiltInParameter.FAMILY_LEVEL_PARAM,
        (int)BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
        (int)BuiltInParameter.SCHEDULE_LEVEL_PARAM,
        (int)BuiltInParameter.LEVEL_PARAM,
        (int)BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
        (int)BuiltInParameter.ROOF_CONSTRAINT_LEVEL_PARAM,
        (int)BuiltInParameter.FACEROOF_LEVEL_PARAM,
        (int)BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
        (int)BuiltInParameter.ROOM_LEVEL_ID,
        (int)BuiltInParameter.RBS_START_LEVEL_PARAM,
    ];

    private static readonly HashSet<string> LevelParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BuiltInParameter.WALL_BASE_CONSTRAINT),
        nameof(BuiltInParameter.FAMILY_LEVEL_PARAM),
        nameof(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM),
        nameof(BuiltInParameter.SCHEDULE_LEVEL_PARAM),
        nameof(BuiltInParameter.LEVEL_PARAM),
        nameof(BuiltInParameter.ROOF_BASE_LEVEL_PARAM),
        nameof(BuiltInParameter.ROOF_CONSTRAINT_LEVEL_PARAM),
        nameof(BuiltInParameter.FACEROOF_LEVEL_PARAM),
        nameof(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM),
        nameof(BuiltInParameter.ROOM_LEVEL_ID),
        nameof(BuiltInParameter.RBS_START_LEVEL_PARAM),
    };

    private static readonly HashSet<int> IgnoredIdentityParamIds =
    [
        (int)BuiltInParameter.ELEM_CATEGORY_PARAM,
        (int)BuiltInParameter.ELEM_CATEGORY_PARAM_MT,
        (int)BuiltInParameter.ELEM_TYPE_PARAM,
        (int)BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM,
        (int)BuiltInParameter.ELEM_FAMILY_PARAM,
        (int)BuiltInParameter.SYMBOL_ID_PARAM,
        (int)BuiltInParameter.ID_PARAM,
    ];

    private static readonly HashSet<string> IgnoredIdentityParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BuiltInParameter.ELEM_CATEGORY_PARAM),
        nameof(BuiltInParameter.ELEM_CATEGORY_PARAM_MT),
        nameof(BuiltInParameter.ELEM_TYPE_PARAM),
        nameof(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM),
        nameof(BuiltInParameter.ELEM_FAMILY_PARAM),
        nameof(BuiltInParameter.SYMBOL_ID_PARAM),
        nameof(BuiltInParameter.ID_PARAM),
    };

    internal static ModelScene Build(RevitFileInfo info, SceneExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        options ??= new SceneExportOptions();

        var elemById = new Dictionary<int, Element>(info.Elements.Count);
        foreach (var e in info.Elements)
            elemById.TryAdd(e.Id, e);

        // Identify all element IDs referenced as types or geometry symbols by other elements
        var typeAndSymbolIds = CollectTypeAndSymbolIds(info.Elements);
        var curtainContainerWallIds = CollectCurtainContainerWallIds(info.Elements, elemById);

        bool hasProjectPhases = info.SerializedProjectPhaseIds is { Length: > 0 }
            || info.Elements.Any(HasProjectPhaseReference);

        int[]? phaseOrder = info.SerializedProjectPhaseIds;
        int activePhaseIdx = -1;
        if (phaseOrder is { Length: > 0 } && info.SerializedDefaultExportPhaseId is int defPhase)
            activePhaseIdx = Array.IndexOf(phaseOrder, defPhase);

        var omittedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        void CountOmitted(Element e, string skipCode, string summaryCode)
        {
            options.SkipLog?.Add(e.Id, SkipLog.ClassOf(e), skipCode);
            omittedCounts[summaryCode] = omittedCounts.GetValueOrDefault(summaryCode) + 1;
        }

        var physicalCandidates = new List<Element>();
        var gridElements = new List<Element>();

        foreach (var elem in info.Elements.OrderBy(e => e.Id))
        {
            if (elem.IsBlockOnly)
            {
                CountOmitted(elem, "block-only-record", "internal-definition");
                continue;
            }
            if (elem.IsGroupTemplate)
            {
                CountOmitted(elem, "group-template", "internal-definition");
                continue;
            }
            if (info.ElemTableIsExact && elem.IsDiscovered)
            {
                CountOmitted(elem, "record-outside-exact-element-table", "internal-definition");
                continue;
            }
            if (elem.SerializedFamilyId is > 0)
            {
                CountOmitted(elem, "owned-by-family-nonspatial", "internal-definition");
                continue;
            }

            // Datum grids are exported as 3D grid lines rather than triangle meshes
            if (elem.CategoryId == (int)BuiltInCategory.OST_Grids)
            {
                if (options.IncludeGrids)
                    gridElements.Add(elem);
                else
                    CountOmitted(elem, "filtered-out", "filtered-out");
                continue;
            }

            bool transformOnlyFamily = elem.Transform != null && elem.TypeId > 0
                && elem.CategoryId is null or -2000080 or -2001000 or -2001100;

            if (typeAndSymbolIds.Contains(elem.Id) && !transformOnlyFamily
                && (!hasProjectPhases || !HasProjectPhaseReference(elem)))
            {
                CountOmitted(elem, "type-element", "type-or-symbol");
                continue;
            }

            if (IsNonPhysicalElement(elem, options.IncludeRooms, curtainContainerWallIds))
            {
                CountOmitted(elem, "non-physical-category", "non-physical");
                continue;
            }

            if (hasProjectPhases)
            {
                if (elem.CategoryId != (int)BuiltInCategory.OST_Walls
                    && !HasProjectPhaseReference(elem)
                    && !transformOnlyFamily)
                {
                    CountOmitted(elem, "no-project-phase-reference", "type-or-symbol");
                    continue;
                }

                if (activePhaseIdx >= 0 && phaseOrder != null && IsOutsideActivePhase(elem, phaseOrder, activePhaseIdx))
                {
                    CountOmitted(elem, "outside-export-phase", "phase-filtered");
                    continue;
                }
            }

            // Caller subset filters
            if (options.ElementIds != null && !options.ElementIds.Contains(elem.Id))
            {
                CountOmitted(elem, "filtered-by-element-id", "filtered-out");
                continue;
            }
            if (options.CategoryIds != null && (elem.CategoryId == null || !options.CategoryIds.Contains(elem.CategoryId.Value)))
            {
                CountOmitted(elem, "filtered-by-category", "filtered-out");
                continue;
            }
            if (options.ElementFilter != null && !options.ElementFilter(elem))
            {
                CountOmitted(elem, "filtered-by-predicate", "filtered-out");
                continue;
            }

            physicalCandidates.Add(elem);
        }

        // Shared coordinate transform setup
        double[]? sharedTrfFeet = info.SharedTransform is { Length: >= 12 } st && st.Take(12).All(double.IsFinite) ? st : null;
        double[]? exportedSharedTrfMetres = sharedTrfFeet == null ? null :
        [
            sharedTrfFeet[0], sharedTrfFeet[1], sharedTrfFeet[2],
            sharedTrfFeet[3], sharedTrfFeet[4], sharedTrfFeet[5],
            sharedTrfFeet[6], sharedTrfFeet[7], sharedTrfFeet[8],
            sharedTrfFeet[9] * FeetToMetres, sharedTrfFeet[10] * FeetToMetres, sharedTrfFeet[11] * FeetToMetres,
        ];

        bool applyShared = options.ApplySharedCoordinates && sharedTrfFeet != null;
        (double X, double Y, double Z) InternalFeetToWorldMetres(double fx, double fy, double fz)
        {
            if (applyShared && sharedTrfFeet != null)
            {
                // SharedTransform stores columns [basisX(3), basisY(3), basisZ(3), origin(3)] in feet
                double sx = sharedTrfFeet[9] + fx * sharedTrfFeet[0] + fy * sharedTrfFeet[3] + fz * sharedTrfFeet[6];
                double sy = sharedTrfFeet[10] + fx * sharedTrfFeet[1] + fy * sharedTrfFeet[4] + fz * sharedTrfFeet[7];
                double sz = sharedTrfFeet[11] + fx * sharedTrfFeet[2] + fy * sharedTrfFeet[5] + fz * sharedTrfFeet[8];
                return (sx * FeetToMetres, sy * FeetToMetres, sz * FeetToMetres);
            }
            return (fx * FeetToMetres, fy * FeetToMetres, fz * FeetToMetres);
        }

        (double X, double Y, double Z) InternalDirToWorldDir((double X, double Y, double Z) d)
        {
            if (applyShared && sharedTrfFeet != null)
            {
                return (
                    d.X * sharedTrfFeet[0] + d.Y * sharedTrfFeet[3] + d.Z * sharedTrfFeet[6],
                    d.X * sharedTrfFeet[1] + d.Y * sharedTrfFeet[4] + d.Z * sharedTrfFeet[7],
                    d.X * sharedTrfFeet[2] + d.Y * sharedTrfFeet[5] + d.Z * sharedTrfFeet[8]);
            }
            return d;
        }

        // Compute world bounding box in metres to determine Relative-To-Center (RTC) offset
        double[] rtcOffset = ComputeRtcOffsetMetres(physicalCandidates, gridElements, elemById, InternalFeetToWorldMetres);

        (double X, double Y, double Z) WorldFeetToRtcMetres((double X, double Y, double Z) ptFeet)
        {
            var wm = InternalFeetToWorldMetres(ptFeet.X, ptFeet.Y, ptFeet.Z);
            return (wm.X - rtcOffset[0], wm.Y - rtcOffset[1], wm.Z - rtcOffset[2]);
        }

        var materials = new List<SceneMaterial>();
        var materialIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        int GetOrCreateMaterialIndex(int materialElemId, Element elem, Element? typeElem)
        {
            var spec = ResolveMaterialSpec(materialElemId, elem, typeElem, elemById);
            if (materialIndexByKey.TryGetValue(spec.Key, out int existing))
                return existing;
            int idx = materials.Count;
            materials.Add(new SceneMaterial
            {
                Index = idx,
                RevitMaterialId = spec.RevitMaterialId,
                Name = spec.Name,
                Color = spec.Color,
                Opacity = spec.Opacity,
                Metallic = spec.Metallic,
                Roughness = spec.Roughness,
                DoubleSided = true,
            });
            materialIndexByKey[spec.Key] = idx;
            return idx;
        }

        var prototypes = new List<SceneMeshPrototype>();
        var instances = new List<SceneMeshInstance>();
        var sceneElements = new List<SceneElement>();

        // Symbol prototype cache: symbolId -> list of (MaterialSlotId, NormalProtoIndex, MirroredProtoIndex?)
        var symbolPrototypeCache = new Dictionary<int, List<SymbolSubmeshEntry>?>();

        List<SymbolSubmeshEntry>? GetOrBuildSymbolPrototypes(int symbolId, GeometryData? fallbackInstanceGeo, double[]? fallbackInstanceTrf)
        {
            if (symbolPrototypeCache.TryGetValue(symbolId, out var cached))
                return cached;

            GeometryData? symbolGeo = null;
            if (elemById.TryGetValue(symbolId, out var symElem) && HasRenderableGeometry(symElem.Geometry))
            {
                symbolGeo = symElem.Geometry;
            }
            else if (fallbackInstanceGeo != null && fallbackInstanceTrf != null
                     && SceneGeometryMesher.TryInvertRowMajor3x4(fallbackInstanceTrf, out var invTrf))
            {
                // Recover canonical symbol-local geometry from the first placed instance when symbol element has no own GRep
                symbolGeo = UnplaceGeometryToSymbolSpace(fallbackInstanceGeo, invTrf);
            }

            if (symbolGeo == null)
            {
                symbolPrototypeCache[symbolId] = null;
                return null;
            }

            var subparts = SceneGeometryMesher.MeshGeometry(symbolGeo, info.GraphicsStyles);
            if (subparts.Count == 0)
            {
                symbolPrototypeCache[symbolId] = null;
                return null;
            }

            var entries = new List<SymbolSubmeshEntry>(subparts.Count);
            foreach (var sp in subparts)
            {
                int protoIdx = prototypes.Count;
                prototypes.Add(new SceneMeshPrototype
                {
                    Index = protoIdx,
                    SourceSymbolId = symbolId,
                    IsMirroredVariant = false,
                    Positions = sp.Positions,
                    Normals = sp.Normals,
                    Indices = sp.Indices,
                    LocalBoundsMin = sp.BoundsMin,
                    LocalBoundsMax = sp.BoundsMax,
                });
                entries.Add(new SymbolSubmeshEntry(sp.MaterialSlotId, protoIdx));
            }

            symbolPrototypeCache[symbolId] = entries;
            return entries;
        }

        int instancedElementCount = 0;
        int missingGeometryCount = 0;

        double sceneMinX = double.MaxValue, sceneMinY = double.MaxValue, sceneMinZ = double.MaxValue;
        double sceneMaxX = double.MinValue, sceneMaxY = double.MinValue, sceneMaxZ = double.MinValue;
        void ExpandSceneBounds(float[] bMin, float[] bMax)
        {
            if (bMin[0] < sceneMinX) sceneMinX = bMin[0];
            if (bMin[1] < sceneMinY) sceneMinY = bMin[1];
            if (bMin[2] < sceneMinZ) sceneMinZ = bMin[2];
            if (bMax[0] > sceneMaxX) sceneMaxX = bMax[0];
            if (bMax[1] > sceneMaxY) sceneMaxY = bMax[1];
            if (bMax[2] > sceneMaxZ) sceneMaxZ = bMax[2];
        }

        foreach (var elem in physicalCandidates)
        {
            var effectiveGeo = ResolveEffectiveElementGeometry(elem, elemById);

            Element? typeElem = ResolveTypeElement(elem, elemById);
            int elementIndex = sceneElements.Count;
            var elemInstanceIndices = new List<int>();
            bool usedInstancing = false;

            // 1. Try symbol instancing when the element maps cleanly to reusable symbol(s)
            var mappedList = ResolveMappedInstances(elem, effectiveGeo);
            if (mappedList is { Count: > 0 } && CanUseSymbolInstancing(mappedList, effectiveGeo, elemById))
            {
                var pendingInstances = new List<SceneMeshInstance>();
                bool allResolved = true;

                foreach (var gi in mappedList)
                {
                    var symEntries = GetOrBuildSymbolPrototypes(
                        gi.SymbolId,
                        mappedList.Count == 1 ? effectiveGeo : null,
                        mappedList.Count == 1 ? gi.Transform : null);

                    if (symEntries is not { Count: > 0 })
                    {
                        allResolved = false;
                        break;
                    }

                    float[] instanceMatrix = BuildInstanceRtcMatrix(gi.Transform, applyShared ? sharedTrfFeet : null, rtcOffset, out bool isMirrored);
                    foreach (var entry in symEntries)
                    {
                        int protoIdx = entry.NormalProtoIndex;
                        if (isMirrored)
                        {
                            if (entry.MirroredProtoIndex is not { } mirIdx)
                            {
                                mirIdx = prototypes.Count;
                                prototypes.Add(SceneGeometryMesher.CreateMirroredVariant(prototypes[entry.NormalProtoIndex], mirIdx));
                                entry.MirroredProtoIndex = mirIdx;
                            }
                            protoIdx = mirIdx;
                        }

                        var proto = prototypes[protoIdx];
                        ComputeTransformedBounds(proto.LocalBoundsMin, proto.LocalBoundsMax, instanceMatrix, out var wMin, out var wMax);
                        int matIdx = GetOrCreateMaterialIndex(entry.MaterialSlotId, elem, typeElem);

                        pendingInstances.Add(new SceneMeshInstance
                        {
                            MeshIndex = protoIdx,
                            ElementIndex = elementIndex,
                            ElementId = elem.Id,
                            MaterialIndex = matIdx,
                            Matrix = instanceMatrix,
                            BoundsMin = wMin,
                            BoundsMax = wMax,
                        });
                    }
                }

                if (allResolved && pendingInstances.Count > 0)
                {
                    usedInstancing = true;
                    instancedElementCount++;
                    foreach (var inst in pendingInstances)
                    {
                        elemInstanceIndices.Add(instances.Count);
                        instances.Add(inst);
                        ExpandSceneBounds(inst.BoundsMin, inst.BoundsMax);
                    }
                }
            }

            // 2. Direct element mesh when not instanced
            if (!usedInstancing)
            {
                if (effectiveGeo == null)
                {
                    CountOmitted(elem, "no-geometry", "missing-geometry");
                    missingGeometryCount++;
                    continue;
                }

                var subparts = SceneGeometryMesher.MeshGeometry(
                    effectiveGeo,
                    info.GraphicsStyles,
                    WorldFeetToRtcMetres,
                     applyShared ? d => InternalDirToWorldDir(d) : null);

                if (subparts.Count == 0)
                {
                    CountOmitted(elem, "no-triangulated-mesh", "missing-geometry");
                    missingGeometryCount++;
                    continue;
                }

                foreach (var sp in subparts)
                {
                    int protoIdx = prototypes.Count;
                    prototypes.Add(new SceneMeshPrototype
                    {
                        Index = protoIdx,
                        SourceSymbolId = null,
                        IsMirroredVariant = false,
                        Positions = sp.Positions,
                        Normals = sp.Normals,
                        Indices = sp.Indices,
                        LocalBoundsMin = sp.BoundsMin,
                        LocalBoundsMax = sp.BoundsMax,
                    });

                    int matIdx = GetOrCreateMaterialIndex(sp.MaterialSlotId, elem, typeElem);
                    int instIdx = instances.Count;
                    var inst = new SceneMeshInstance
                    {
                        MeshIndex = protoIdx,
                        ElementIndex = elementIndex,
                        ElementId = elem.Id,
                        MaterialIndex = matIdx,
                        Matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
                        BoundsMin = (float[])sp.BoundsMin.Clone(),
                        BoundsMax = (float[])sp.BoundsMax.Clone(),
                    };
                    instances.Add(inst);
                    elemInstanceIndices.Add(instIdx);
                    ExpandSceneBounds(inst.BoundsMin, inst.BoundsMax);
                }
            }

            string categoryDisplay = ResolveCategoryDisplayName(elem, info);
            string? familyName = ResolveFamilyName(elem, typeElem);
            string? typeName = ResolveTypeName(elem, typeElem);
            string elemName = ResolveElementDisplayName(elem, typeName, familyName, categoryDisplay);
            string? levelName = ResolveLevelName(elem, elemById);
            int? hostId = elem.HostId is > 0 ? elem.HostId : elem.SerializedHostId is > 0 ? elem.SerializedHostId : null;

            var propGroups = options.IncludeParameters
                ? BuildPropertyGroups(elem, typeElem, elemById, options)
                : [];

            sceneElements.Add(new SceneElement
            {
                Id = elem.Id,
                UniqueId = string.IsNullOrWhiteSpace(elem.UniqueId) ? null : elem.UniqueId,
                Name = elemName,
                Category = categoryDisplay,
                CategoryId = elem.CategoryId,
                FamilyName = familyName,
                TypeName = typeName,
                Level = levelName,
                HostId = hostId,
                IsInstanced = usedInstancing,
                InstanceIndices = elemInstanceIndices,
                PropertyGroups = propGroups,
            });
        }

        // Build category hierarchy sorted alphabetically
        var categoryNodes = sceneElements
            .Select((el, idx) => (Element: el, Index: idx))
            .GroupBy(x => x.Element.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SceneCategoryNode
            {
                Category = g.First().Element.Category,
                CategoryId = g.First().Element.CategoryId,
                ElementIndices = g.Select(x => x.Index).ToList(),
            })
            .ToList();

        // Build 3D datum grids at the floor base elevation of the scene
        double gridZRtc = sceneMinZ <= sceneMaxZ ? sceneMinZ : 0.0;
        var sceneGrids = BuildSceneGrids(gridElements, WorldFeetToRtcMetres, gridZRtc, ref sceneMinX, ref sceneMinY, ref sceneMinZ, ref sceneMaxX, ref sceneMaxY, ref sceneMaxZ);

        if (sceneMinX > sceneMaxX)
        {
            sceneMinX = sceneMinY = sceneMinZ = 0;
            sceneMaxX = sceneMaxY = sceneMaxZ = 0;
        }

        // Compute completeness summary
        int totalVertices = prototypes.Sum(p => p.VertexCount);
        int totalTriangles = instances.Sum(inst => prototypes[inst.MeshIndex].TriangleCount);

        var omittedGroups = BuildOmittedGroups(omittedCounts);
        var notes = BuildCompletenessNotes(info, sceneElements.Count, instancedElementCount, prototypes.Count, instances.Count, missingGeometryCount);
        bool isPartial = info.GeometryUndecoded || missingGeometryCount > 0 || (physicalCandidates.Count > 0 && sceneElements.Count == 0);

        string resolvedTitle = ResolveSceneTitle(info, options.Title);

        options.DiagnosticOutput?.WriteLine(
            $"Scene built: {sceneElements.Count} elements ({instancedElementCount} instanced), " +
            $"{prototypes.Count} reusable meshes, {instances.Count} instances, {totalTriangles} triangles.");

        return new ModelScene
        {
            Schema = ModelScene.CurrentSchema,
            Title = resolvedTitle,
            Units = "m",
            CoordinateSystem = "right-handed-z-up",
            RtcOffset = rtcOffset,
            SharedTransform = exportedSharedTrfMetres,
            BoundsMin = [Math.Round(sceneMinX, 6), Math.Round(sceneMinY, 6), Math.Round(sceneMinZ, 6)],
            BoundsMax = [Math.Round(sceneMaxX, 6), Math.Round(sceneMaxY, 6), Math.Round(sceneMaxZ, 6)],
            Materials = materials,
            Meshes = prototypes,
            Instances = instances,
            Elements = sceneElements,
            Categories = categoryNodes,
            Grids = sceneGrids,
            Completeness = new SceneCompletenessSummary
            {
                TotalElements = info.Elements.Count,
                ConsideredElements = physicalCandidates.Count,
                ExportedElements = sceneElements.Count,
                InstancedElements = instancedElementCount,
                ReusableMeshCount = prototypes.Count,
                InstanceCount = instances.Count,
                VertexCount = totalVertices,
                TriangleCount = totalTriangles,
                IsPartialGeometry = isPartial,
                Notes = notes,
                OmittedGroups = omittedGroups,
            },
        };
    }

    private sealed class SymbolSubmeshEntry(int materialSlotId, int normalProtoIndex)
    {
        public int MaterialSlotId { get; } = materialSlotId;
        public int NormalProtoIndex { get; } = normalProtoIndex;
        public int? MirroredProtoIndex { get; set; }
    }

    private static GeometryData? ResolveEffectiveElementGeometry(Element elem, Dictionary<int, Element> elemById)
    {
        if (IfcExporter.HasOnlyUnfilledFacesCurrent(elem) && elem.SerializedComputedSymbolGeometry != null)
            return elem.SerializedComputedSymbolGeometry;

        if (elem.Geometry != null && (HasRenderableGeometry(elem.Geometry) || elem.Geometry.Instances is { Count: > 0 } || elem.Geometry.ExactMappedInstances is { Count: > 0 }))
            return elem.Geometry;

        if (elem.CategoryId is (int)BuiltInCategory.OST_Rooms or (int)BuiltInCategory.OST_MEPSpaces)
        {
            var boundary = elem.SerializedSpaceBoundaryCurrent ?? elem.SerializedLiteralSpaceBoundary;
            if (boundary != null && HasRenderableGeometry(boundary))
                return boundary;

            if (elem.SerializedPlanBoundary is { Count: >= 3 } planLoop)
                return ExtrudePlanLoop(planLoop, elem, elemById);
        }

        return elem.Geometry;
    }

    private static GeometryData ExtrudePlanLoop(IReadOnlyList<(double X, double Y)> loop, Element elem, Dictionary<int, Element> elemById)
    {
        double zBase = 0.0;
        if (elem.Parameters.FirstOrDefault(p => p.Id == -1002003)?.Value is int lvlId
            && elemById.TryGetValue(lvlId, out var lvl)
            && lvl.Parameters.FirstOrDefault(p => p.Definition?.Name == "LEVEL_ELEV")?.Value is double elev)
        {
            zBase = elev;
        }

        double height = elem.Parameters.FirstOrDefault(p => p.Definition?.Name is "ROOM_UPPER_OFFSET" or "ROOM_COMPUTATION_HEIGHT")?.Value is double h && h > 0.1
            ? h
            : 9.0;
        double zTop = zBase + height;

        // Ensure counter-clockwise XY plan loop
        double signedArea2 = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            signedArea2 += a.X * b.Y - b.X * a.Y;
        }

        var pts = signedArea2 >= 0 ? loop.ToList() : loop.Reverse().ToList();
        var faces = new List<GeoFace>(pts.Count + 2)
        {
            new()
            {
                Normal = [0, 0, -1],
                PlaneSurface = true,
                OuterVertices = Enumerable.Reverse(pts).Select(p => new[] { p.X, p.Y, zBase }).ToList(),
            },
            new()
            {
                Normal = [0, 0, 1],
                PlaneSurface = true,
                OuterVertices = pts.Select(p => new[] { p.X, p.Y, zTop }).ToList(),
            },
        };

        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            faces.Add(new GeoFace
            {
                Normal = [dy / len, -dx / len, 0],
                PlaneSurface = true,
                OuterVertices =
                [
                    [a.X, a.Y, zBase],
                    [b.X, b.Y, zBase],
                    [b.X, b.Y, zTop],
                    [a.X, a.Y, zTop],
                ],
            });
        }

        return new GeometryData { Faces = faces };
    }

    private static List<GeoInstance>? ResolveMappedInstances(Element elem, GeometryData? effectiveGeo)
    {
        if (effectiveGeo?.ExactMappedInstances is { Count: > 0 } exact)
            return exact;

        if (effectiveGeo?.Instances is { Count: > 0 } instances)
        {
            bool hasOwnGeometry = effectiveGeo.Faces is { Count: > 0 }
                || effectiveGeo.MeshNodes is { Count: > 0 }
                || effectiveGeo.Solids is { Count: > 0 };
            if (!hasOwnGeometry)
                return instances;
            // Single-symbol expanded instance (e.g. deserialized from public JSON where ExactMappedInstances is omitted)
            if (instances.Count == 1 && effectiveGeo.MeshNodes is not { Count: > 0 } && effectiveGeo.Solids is not { Count: > 0 })
                return instances;
        }

        if (elem.Transform is { SymbolId: > 0 } tr
            && tr.Origin is { Length: >= 3 } && tr.BasisX is { Length: >= 3 }
            && tr.BasisY is { Length: >= 3 } && tr.BasisZ is { Length: >= 3 })
        {
            return
            [
                new GeoInstance
                {
                    SymbolId = tr.SymbolId,
                    Transform =
                    [
                        tr.BasisX[0], tr.BasisY[0], tr.BasisZ[0],
                        tr.BasisX[1], tr.BasisY[1], tr.BasisZ[1],
                        tr.BasisX[2], tr.BasisY[2], tr.BasisZ[2],
                        tr.Origin[0], tr.Origin[1], tr.Origin[2],
                    ],
                },
            ];
        }

        return null;
    }

    private static bool CanUseSymbolInstancing(
        List<GeoInstance> mappedList,
        GeometryData? effectiveGeo,
        Dictionary<int, Element> elemById)
    {
        foreach (var gi in mappedList)
        {
            if (gi.SymbolId <= 0 || gi.Transform is not { Length: >= 12 })
                return false;
            if (gi.Transform.Take(12).Any(v => !double.IsFinite(v)))
                return false;
            if (Math.Abs(SceneGeometryMesher.DeterminantRowMajor3x4(gi.Transform)) < 1e-9)
                return false;
        }

        // When the instance has expanded faces AND the symbol is in elemById, ensure the instance wasn't host-clipped
        if (mappedList.Count == 1 && effectiveGeo?.Faces is { Count: > 0 } instFaces
            && elemById.TryGetValue(mappedList[0].SymbolId, out var sym)
            && sym.Geometry?.Faces is { Count: > 0 } symFaces)
        {
            if (instFaces.Count != symFaces.Count)
                return false;
        }

        return true;
    }

    private static GeometryData? UnplaceGeometryToSymbolSpace(GeometryData instanceGeo, double[] invTrf)
    {
        if (instanceGeo.Faces.Count == 0)
            return null;

        bool mirrored = SceneGeometryMesher.DeterminantRowMajor3x4(invTrf) < 0;
        double[] MapPt(double[] p) =>
        [
            invTrf[9] + p[0] * invTrf[0] + p[1] * invTrf[1] + p[2] * invTrf[2],
            invTrf[10] + p[0] * invTrf[3] + p[1] * invTrf[4] + p[2] * invTrf[5],
            invTrf[11] + p[0] * invTrf[6] + p[1] * invTrf[7] + p[2] * invTrf[8],
        ];
        double[] MapVec(double[] v) =>
        [
            v[0] * invTrf[0] + v[1] * invTrf[1] + v[2] * invTrf[2],
            v[0] * invTrf[3] + v[1] * invTrf[4] + v[2] * invTrf[5],
            v[0] * invTrf[6] + v[1] * invTrf[7] + v[2] * invTrf[8],
        ];

        var local = new GeometryData { GStyleElemId = instanceGeo.GStyleElemId };
        foreach (var f in instanceGeo.Faces)
        {
            if (f.OuterVertices is not { Count: >= 3 }) continue;
            var outer = f.OuterVertices.Select(MapPt).ToList();
            if (mirrored) outer.Reverse();
            var inner = f.InnerVertices?.Select(ring =>
            {
                var r = ring.Select(MapPt).ToList();
                if (mirrored) r.Reverse();
                return r;
            }).ToList();

            var n = f.Normal is { Length: >= 3 } ? MapVec(f.Normal) : [0.0, 0.0, 1.0];
            if (mirrored) n = [-n[0], -n[1], -n[2]];

            local.Faces.Add(new GeoFace
            {
                Origin = outer[0],
                XVector = f.XVector is { Length: >= 3 } ? MapVec(f.XVector) : [1, 0, 0],
                YVector = f.YVector is { Length: >= 3 } ? MapVec(f.YVector) : [0, 1, 0],
                Normal = n,
                OuterVertices = outer,
                InnerVertices = inner,
                RenderStyleId = f.RenderStyleId,
                NestedRenderStyleId = f.NestedRenderStyleId,
                GeometryStyle = f.GeometryStyle,
            });
        }
        return local.Faces.Count > 0 ? local : null;
    }

    private static float[] BuildInstanceRtcMatrix(
        double[] t,
        double[]? sharedTrfFeet,
        double[] rtcOffsetMetres,
        out bool isMirrored)
    {
        // t is row-major 3x4 in feet:
        // worldFeet = [t[9] + x*t[0] + y*t[1] + z*t[2], t[10] + x*t[3] + y*t[4] + z*t[5], t[11] + x*t[6] + y*t[7] + z*t[8]]
        // Note: prototype vertices are already scaled to metres (x_m = x_ft * FeetToMetres), so the 3x3 linear part is dimensionless!
        double r00 = t[0], r01 = t[1], r02 = t[2];
        double r10 = t[3], r11 = t[4], r12 = t[5];
        double r20 = t[6], r21 = t[7], r22 = t[8];
        double txFt = t[9], tyFt = t[10], tzFt = t[11];

        if (sharedTrfFeet != null)
        {
            // SharedTransform S has columns: sCol0 = st[0..2], sCol1 = st[3..5], sCol2 = st[6..8], sOrigin = st[9..11]
            // S * R where sRow0 = (st[0], st[3], st[6]), sRow1 = (st[1], st[4], st[7]), sRow2 = (st[2], st[5], st[8])
            double s00 = sharedTrfFeet[0], s01 = sharedTrfFeet[3], s02 = sharedTrfFeet[6];
            double s10 = sharedTrfFeet[1], s11 = sharedTrfFeet[4], s12 = sharedTrfFeet[7];
            double s20 = sharedTrfFeet[2], s21 = sharedTrfFeet[5], s22 = sharedTrfFeet[8];

            double nr00 = s00 * r00 + s01 * r10 + s02 * r20;
            double nr01 = s00 * r01 + s01 * r11 + s02 * r21;
            double nr02 = s00 * r02 + s01 * r12 + s02 * r22;

            double nr10 = s10 * r00 + s11 * r10 + s12 * r20;
            double nr11 = s10 * r01 + s11 * r11 + s12 * r21;
            double nr12 = s10 * r02 + s11 * r12 + s12 * r22;

            double nr20 = s20 * r00 + s21 * r10 + s22 * r20;
            double nr21 = s20 * r01 + s21 * r11 + s22 * r21;
            double nr22 = s20 * r02 + s21 * r12 + s22 * r22;

            double ntx = sharedTrfFeet[9] + s00 * txFt + s01 * tyFt + s02 * tzFt;
            double nty = sharedTrfFeet[10] + s10 * txFt + s11 * tyFt + s12 * tzFt;
            double ntz = sharedTrfFeet[11] + s20 * txFt + s21 * tyFt + s22 * tzFt;

            r00 = nr00; r01 = nr01; r02 = nr02;
            r10 = nr10; r11 = nr11; r12 = nr12;
            r20 = nr20; r21 = nr21; r22 = nr22;
            txFt = ntx; tyFt = nty; tzFt = ntz;
        }

        double det = r00 * (r11 * r22 - r12 * r21) - r01 * (r10 * r22 - r12 * r20) + r02 * (r10 * r21 - r11 * r20);
        isMirrored = det < 0;

        double txRtc = txFt * FeetToMetres - rtcOffsetMetres[0];
        double tyRtc = tyFt * FeetToMetres - rtcOffsetMetres[1];
        double tzRtc = tzFt * FeetToMetres - rtcOffsetMetres[2];

        // Column-major 4x4 (glTF 2.0 & WebGL standard):
        return
        [
            (float)r00, (float)r10, (float)r20, 0f,
            (float)r01, (float)r11, (float)r21, 0f,
            (float)r02, (float)r12, (float)r22, 0f,
            (float)txRtc, (float)tyRtc, (float)tzRtc, 1f,
        ];
    }

    private static void ComputeTransformedBounds(
        float[] localMin,
        float[] localMax,
        float[] colMajor,
        out float[] worldMin,
        out float[] worldMax)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (int mask = 0; mask < 8; mask++)
        {
            float lx = (mask & 1) == 0 ? localMin[0] : localMax[0];
            float ly = (mask & 2) == 0 ? localMin[1] : localMax[1];
            float lz = (mask & 4) == 0 ? localMin[2] : localMax[2];

            float wx = colMajor[12] + lx * colMajor[0] + ly * colMajor[4] + lz * colMajor[8];
            float wy = colMajor[13] + lx * colMajor[1] + ly * colMajor[5] + lz * colMajor[9];
            float wz = colMajor[14] + lx * colMajor[2] + ly * colMajor[6] + lz * colMajor[10];

            if (wx < minX) minX = wx; if (wx > maxX) maxX = wx;
            if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
            if (wz < minZ) minZ = wz; if (wz > maxZ) maxZ = wz;
        }

        worldMin = [minX, minY, minZ];
        worldMax = [maxX, maxY, maxZ];
    }

    private static double[] ComputeRtcOffsetMetres(
        List<Element> candidates,
        List<Element> grids,
        Dictionary<int, Element> elemById,
        Func<double, double, double, (double X, double Y, double Z)> toWorldMetres)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        bool any = false;

        void IncludeFeetPoint(double fx, double fy, double fz)
        {
            if (!double.IsFinite(fx) || !double.IsFinite(fy) || !double.IsFinite(fz)) return;
            var wm = toWorldMetres(fx, fy, fz);
            if (wm.X < minX) minX = wm.X; if (wm.X > maxX) maxX = wm.X;
            if (wm.Y < minY) minY = wm.Y; if (wm.Y > maxY) maxY = wm.Y;
            if (wm.Z < minZ) minZ = wm.Z; if (wm.Z > maxZ) maxZ = wm.Z;
            any = true;
        }

        foreach (var e in candidates)
        {
            var geo = ResolveEffectiveElementGeometry(e, elemById);
            var box = e.DecodedBox ?? geo?.BoundingBox ?? e.BoundingBox;
            if (box is { Length: >= 6 } && box.Take(6).All(double.IsFinite) && box[3] >= box[0])
            {
                IncludeFeetPoint(box[0], box[1], box[2]);
                IncludeFeetPoint(box[3], box[4], box[5]);
                continue;
            }

            if (geo?.Faces is { Count: > 0 } faces)
            {
                foreach (var f in faces)
                {
                    if (f.OuterVertices == null) continue;
                    foreach (var v in f.OuterVertices)
                        if (v is { Length: >= 3 }) IncludeFeetPoint(v[0], v[1], v[2]);
                }
            }
            else if (geo?.Solids is { Count: > 0 } solids)
            {
                foreach (var s in solids)
                    if (s.Position is { Length: >= 3 }) IncludeFeetPoint(s.Position[0], s.Position[1], s.Position[2]);
            }
            else if (ResolveMappedInstances(e, geo) is { Count: > 0 } mapped)
            {
                foreach (var gi in mapped)
                {
                    if (gi.Transform is { Length: >= 12 })
                        IncludeFeetPoint(gi.Transform[9], gi.Transform[10], gi.Transform[11]);
                }
            }
        }

        if (!any)
        {
            foreach (var g in grids)
            {
                if (g.Location is { StartPoint.Length: >= 3, EndPoint.Length: >= 3 } loc)
                {
                    IncludeFeetPoint(loc.StartPoint[0], loc.StartPoint[1], loc.StartPoint[2]);
                    IncludeFeetPoint(loc.EndPoint[0], loc.EndPoint[1], loc.EndPoint[2]);
                }
            }
        }

        if (!any) return [0, 0, 0];

        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;
        double cz = (minZ + maxZ) * 0.5;

        if (Math.Abs(cx) <= RtcActivationThresholdMetres
            && Math.Abs(cy) <= RtcActivationThresholdMetres
            && Math.Abs(cz) <= RtcActivationThresholdMetres)
        {
            return [0, 0, 0];
        }

        return [Math.Round(cx, 3), Math.Round(cy, 3), Math.Round(cz, 3)];
    }

    private static List<SceneGridLine> BuildSceneGrids(
        List<Element> gridElements,
        Func<(double X, double Y, double Z), (double X, double Y, double Z)> worldFeetToRtcMetres,
        double gridZRtc,
        ref double minX, ref double minY, ref double minZ,
        ref double maxX, ref double maxY, ref double maxZ)
    {
        var list = new List<SceneGridLine>();
        foreach (var g in gridElements)
        {
            (double X, double Y, double Z) sRtc, eRtc;
            if (g.Location is { StartPoint.Length: >= 2, EndPoint.Length: >= 2 } loc
                && double.IsFinite(loc.StartPoint[0]) && double.IsFinite(loc.StartPoint[1])
                && double.IsFinite(loc.EndPoint[0]) && double.IsFinite(loc.EndPoint[1]))
            {
                double sz = loc.StartPoint.Length >= 3 && double.IsFinite(loc.StartPoint[2]) ? loc.StartPoint[2] : 0;
                double ez = loc.EndPoint.Length >= 3 && double.IsFinite(loc.EndPoint[2]) ? loc.EndPoint[2] : 0;
                sRtc = worldFeetToRtcMetres((loc.StartPoint[0], loc.StartPoint[1], sz));
                eRtc = worldFeetToRtcMetres((loc.EndPoint[0], loc.EndPoint[1], ez));
            }
            else if (g.GridPlane is { Envelope.Length: >= 4, Origin.Length: >= 3, XVector.Length: >= 3 } gp)
            {
                double u0 = gp.Envelope[0], u1 = gp.Envelope[2];
                sRtc = worldFeetToRtcMetres((gp.Origin[0] + u0 * gp.XVector[0], gp.Origin[1] + u0 * gp.XVector[1], gp.Origin[2]));
                eRtc = worldFeetToRtcMetres((gp.Origin[0] + u1 * gp.XVector[0], gp.Origin[1] + u1 * gp.XVector[1], gp.Origin[2]));
            }
            else
            {
                continue;
            }

            double dx = eRtc.X - sRtc.X, dy = eRtc.Y - sRtc.Y;
            if (dx * dx + dy * dy < 1e-6) continue;

            float sx = (float)sRtc.X, sy = (float)sRtc.Y, z = (float)gridZRtc;
            float ex = (float)eRtc.X, ey = (float)eRtc.Y;

            if (minX > maxX)
            {
                minX = Math.Min(sx, ex); maxX = Math.Max(sx, ex);
                minY = Math.Min(sy, ey); maxY = Math.Max(sy, ey);
                minZ = maxZ = z;
            }

            list.Add(new SceneGridLine
            {
                Id = g.Id,
                Name = !string.IsNullOrWhiteSpace(g.Name) ? g.Name : $"{list.Count + 1}",
                Start = [sx, sy, z],
                End = [ex, ey, z],
            });
        }
        return list;
    }

    private sealed record MaterialSpec(
        string Key,
        int? RevitMaterialId,
        string Name,
        float[] Color,
        float Opacity,
        float Metallic,
        float Roughness);

    private static MaterialSpec ResolveMaterialSpec(
        int materialSlotId,
        Element elem,
        Element? typeElem,
        Dictionary<int, Element> elemById)
    {
        // Spatial rooms / MEP spaces get clean translucent shading
        if (elem.CategoryId == (int)BuiltInCategory.OST_Rooms)
            return new MaterialSpec("spatial:room", null, "Room Volume", [0.35f, 0.68f, 0.95f], 0.25f, 0.0f, 0.85f);
        if (elem.CategoryId == (int)BuiltInCategory.OST_MEPSpaces)
            return new MaterialSpec("spatial:space", null, "MEP Space Volume", [0.25f, 0.82f, 0.68f], 0.25f, 0.0f, 0.85f);

        int resolvedMatId = materialSlotId;
        if (resolvedMatId <= 0)
            resolvedMatId = FindElementMaterialId(elem) ?? FindElementMaterialId(typeElem) ?? 0;

        if (resolvedMatId > 0 && elemById.TryGetValue(resolvedMatId, out var matElem))
        {
            string matName = !string.IsNullOrWhiteSpace(matElem.Name) && !IsArtifactName(matElem.Name)
                ? matElem.Name
                : $"Material {resolvedMatId}";

            int? packedColor = matElem.Parameters.FirstOrDefault(p => p.Definition?.Name == "MATERIAL_PARAM_COLOR")?.Value is int pc ? pc : null;
            int transparencyPct = matElem.Parameters.FirstOrDefault(p => p.Definition?.Name == "MATERIAL_PARAM_TRANSPARENCY")?.Value is int tr
                ? Math.Clamp(tr, 0, 100) : 0;
            int shininess = matElem.Parameters.FirstOrDefault(p => p.Definition?.Name == "MATERIAL_PARAM_SHININESS")?.Value is int sh
                ? Math.Clamp(sh, 0, 128) : 32;

            float r, g, b;
            if (packedColor.HasValue)
            {
                var (dr, dg, db) = IfcExporter.DecodePackedMaterialColor(packedColor.Value);
                r = (float)dr; g = (float)dg; b = (float)db;
            }
            else
            {
                var fallback = CategoryPaletteColor(elem.CategoryId);
                r = fallback[0]; g = fallback[1]; b = fallback[2];
            }

            float opacity = matElem.SerializedMaterialTransparency is { } storedFrac && storedFrac is >= 0 and <= 1
                ? Math.Clamp(1f - (float)storedFrac, 0.08f, 1f)
                : Math.Clamp(1f - transparencyPct / 100f, 0.08f, 1f);

            // Glazing heuristic when material name clearly names glass and lacks explicit transparency param
            if (opacity >= 0.99f && IsGlassMaterialName(matName))
            {
                opacity = 0.35f;
                if (!packedColor.HasValue) { r = 0.62f; g = 0.82f; b = 0.92f; }
            }

            bool isMetal = matName.Contains("Metal", StringComparison.OrdinalIgnoreCase)
                || matName.Contains("Steel", StringComparison.OrdinalIgnoreCase)
                || matName.Contains("Aluminum", StringComparison.OrdinalIgnoreCase)
                || matName.Contains("Aluminium", StringComparison.OrdinalIgnoreCase)
                || matName.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                || matName.Contains("Copper", StringComparison.OrdinalIgnoreCase);

            float metallic = isMetal ? 0.55f : 0.04f;
            float roughness = Math.Clamp(0.85f - (shininess / 128f) * 0.65f, 0.15f, 0.9f);
            if (opacity < 0.95f) roughness = Math.Min(roughness, 0.2f);

            return new MaterialSpec($"mat:{resolvedMatId}", resolvedMatId, matName, [r, g, b], opacity, metallic, roughness);
        }

        // Category fallback palette
        int catKey = elem.CategoryId ?? 0;
        string catLabel = elem.CategoryId is int cid && EnglishCategoryNames.Get(cid) is { Length: > 0 } en
            ? en
            : elem.Category ?? "Default";
        var col = CategoryPaletteColor(elem.CategoryId);
        float catOpacity = elem.CategoryId is (int)BuiltInCategory.OST_WindowsGlassProjection
            or (int)BuiltInCategory.OST_DoorsGlassProjection ? 0.35f : 1.0f;

        return new MaterialSpec($"cat:{catKey}", null, $"{catLabel} Default", col, catOpacity, 0.05f, 0.68f);
    }

    private static int? FindElementMaterialId(Element? elem)
    {
        if (elem == null) return null;
        if (elem.Layers is { Count: > 0 })
        {
            foreach (var layer in elem.Layers)
                if (layer.MaterialId > 0) return layer.MaterialId;
        }
        foreach (var p in elem.Parameters)
        {
            if (p.StorageType == StorageType.ElementId && p.Value is int mid && mid > 0
                && p.Definition?.Name is "STRUCTURAL_MATERIAL_PARAM" or "MATERIAL_ID_PARAM" or "FINISH_MAT_ID")
            {
                return mid;
            }
        }
        return null;
    }

    private static bool IsGlassMaterialName(string name) =>
        name.Contains("Glass", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Glazing", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Стекло", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Vitrage", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Glas", StringComparison.OrdinalIgnoreCase);

    private static float[] CategoryPaletteColor(int? categoryId) => categoryId switch
    {
        (int)BuiltInCategory.OST_Walls => [0.84f, 0.83f, 0.80f],
        (int)BuiltInCategory.OST_Floors => [0.74f, 0.75f, 0.77f],
        (int)BuiltInCategory.OST_Roofs => [0.68f, 0.45f, 0.38f],
        (int)BuiltInCategory.OST_Ceilings => [0.91f, 0.91f, 0.89f],
        (int)BuiltInCategory.OST_Doors => [0.66f, 0.49f, 0.34f],
        (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_WindowsGlassProjection => [0.62f, 0.80f, 0.90f],
        (int)BuiltInCategory.OST_CurtainWallPanels => [0.58f, 0.78f, 0.88f],
        (int)BuiltInCategory.OST_CurtainWallMullions => [0.38f, 0.41f, 0.45f],
        (int)BuiltInCategory.OST_StructuralColumns or (int)BuiltInCategory.OST_Columns => [0.56f, 0.60f, 0.65f],
        (int)BuiltInCategory.OST_StructuralFraming => [0.50f, 0.57f, 0.66f],
        (int)BuiltInCategory.OST_StructuralFoundation => [0.62f, 0.63f, 0.62f],
        (int)BuiltInCategory.OST_Stairs or (int)BuiltInCategory.OST_Ramps => [0.77f, 0.74f, 0.69f],
        (int)BuiltInCategory.OST_StairsRailing or (int)BuiltInCategory.OST_Railings => [0.45f, 0.48f, 0.52f],
        (int)BuiltInCategory.OST_Furniture or (int)BuiltInCategory.OST_FurnitureSystems => [0.76f, 0.64f, 0.50f],
        (int)BuiltInCategory.OST_Casework => [0.80f, 0.73f, 0.63f],
        (int)BuiltInCategory.OST_PlumbingFixtures => [0.92f, 0.94f, 0.96f],
        (int)BuiltInCategory.OST_MechanicalEquipment => [0.46f, 0.64f, 0.56f],
        (int)BuiltInCategory.OST_DuctCurves or (int)BuiltInCategory.OST_DuctFitting or (int)BuiltInCategory.OST_DuctTerminal => [0.44f, 0.68f, 0.84f],
        (int)BuiltInCategory.OST_PipeCurves or (int)BuiltInCategory.OST_PipeFitting or (int)BuiltInCategory.OST_PipeAccessory => [0.34f, 0.72f, 0.58f],
        (int)BuiltInCategory.OST_Sprinklers => [0.86f, 0.36f, 0.34f],
        (int)BuiltInCategory.OST_CableTray or (int)BuiltInCategory.OST_CableTrayFitting
            or (int)BuiltInCategory.OST_Conduit or (int)BuiltInCategory.OST_ConduitFitting => [0.85f, 0.68f, 0.32f],
        (int)BuiltInCategory.OST_ElectricalEquipment or (int)BuiltInCategory.OST_ElectricalFixtures => [0.82f, 0.62f, 0.34f],
        (int)BuiltInCategory.OST_LightingFixtures => [0.94f, 0.88f, 0.58f],
        (int)BuiltInCategory.OST_Topography or (int)BuiltInCategory.OST_Site => [0.54f, 0.67f, 0.48f],
        (int)BuiltInCategory.OST_Planting => [0.38f, 0.62f, 0.36f],
        _ => [0.78f, 0.80f, 0.82f],
    };

    private static List<ScenePropertyGroup> BuildPropertyGroups(
        Element elem,
        Element? typeElem,
        Dictionary<int, Element> elemById,
        SceneExportOptions options)
    {
        var grouped = new Dictionary<string, List<SceneProperty>>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int limit = options.MaxParametersPerElement > 0 ? options.MaxParametersPerElement : int.MaxValue;
        int addedCount = 0;

        void AddParametersFrom(IEnumerable<Parameter> parameters, bool fromType)
        {
            foreach (var p in parameters)
            {
                if (addedCount >= limit) return;
                if (!p.HasValue) continue;

                string? rawName = p.Definition?.Name;
                if (IgnoredIdentityParamIds.Contains(p.Id)
                    || (rawName != null && IgnoredIdentityParamNames.Contains(rawName)))
                {
                    continue;
                }

                string groupName;
                string displayName;
                if (p.Id < 0 && CompactLabels.TryGetLabels(p.Id, out string cGroup, out string cLabel)
                    && !string.IsNullOrWhiteSpace(cLabel))
                {
                    bool isInvalidGroup = string.IsNullOrWhiteSpace(cGroup)
                        || cGroup.Equals("Invalid group", StringComparison.OrdinalIgnoreCase)
                        || cGroup.Equals("INVALID", StringComparison.OrdinalIgnoreCase);
                    string cleanGroup = isInvalidGroup ? "Parameters" : cGroup;
                    groupName = fromType && !cleanGroup.EndsWith(" (Type)", StringComparison.Ordinal)
                        ? (isInvalidGroup ? "Type Parameters" : $"{cleanGroup} (Type)")
                        : cleanGroup;
                    displayName = cLabel;
                }
                else
                {
                    groupName = fromType ? "Type Parameters" : "Parameters";
                    displayName = HumanizeParameterName(rawName ?? $"Parameter {p.Id}");
                }

                if (options.IncludedParameterNames != null
                    && !options.IncludedParameterNames.Contains(displayName)
                    && (rawName == null || !options.IncludedParameterNames.Contains(rawName)))
                {
                    continue;
                }

                string dedupKey = $"{groupName}::{displayName}";
                if (!seenNames.Add(dedupKey))
                    continue;

                string? formattedValue = FormatParameterValue(p, elemById);
                if (string.IsNullOrWhiteSpace(formattedValue))
                    continue;

                if (!grouped.TryGetValue(groupName, out var list))
                    grouped[groupName] = list = new List<SceneProperty>();

                list.Add(new SceneProperty { Name = displayName, Value = formattedValue });
                addedCount++;
            }
        }

        AddParametersFrom(elem.Parameters, fromType: false);
        if (options.IncludeTypeParameters && typeElem != null && addedCount < limit)
            AddParametersFrom(typeElem.Parameters, fromType: true);

        return grouped
            .OrderBy(g => g.Key.EndsWith("(Type)", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScenePropertyGroup
            {
                Group = g.Key,
                Properties = g.Value,
            })
            .ToList();
    }

    private static string? FormatParameterValue(Parameter p, Dictionary<int, Element> elemById)
    {
        if (p.StorageType == StorageType.ElementId && p.Value is int refId)
        {
            if (refId <= 0) return null;
            if (elemById.TryGetValue(refId, out var target) && !string.IsNullOrWhiteSpace(target.Name) && !IsArtifactName(target.Name))
                return target.Name;
            return $"#{refId}";
        }

        if (p.Value is string s)
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        return p.AsValueString();
    }

    private static string HumanizeParameterName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Parameter";
        if (!raw.Contains('_') || raw.Any(char.IsLower)) return raw;

        string trimmed = raw;
        if (trimmed.EndsWith("_PARAM", StringComparison.Ordinal))
            trimmed = trimmed[..^6];

        var parts = trimmed.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(w =>
            w.Length <= 2 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static HashSet<int> CollectTypeAndSymbolIds(List<Element> elements)
    {
        var ids = new HashSet<int>();
        foreach (var e in elements)
        {
            if (e.TypeId > 0 && e.TypeId != e.Id) ids.Add(e.TypeId);
            if (e.FamilyTypeId > 0 && e.FamilyTypeId != e.Id) ids.Add(e.FamilyTypeId);
            if (e.Transform is { SymbolId: > 0 } tr && tr.SymbolId != e.Id) ids.Add(tr.SymbolId);

            if (e.Geometry?.ExactMappedInstances != null)
                foreach (var gi in e.Geometry.ExactMappedInstances)
                    if (gi.SymbolId > 0 && gi.SymbolId != e.Id) ids.Add(gi.SymbolId);

            if (e.Geometry?.Instances != null)
                foreach (var gi in e.Geometry.Instances)
                    if (gi.SymbolId > 0 && gi.SymbolId != e.Id) ids.Add(gi.SymbolId);

            foreach (var p in e.Parameters)
            {
                if (p.Id is -1002050 or -1002052 && p.Value is int tid && tid > 0 && tid != e.Id)
                    ids.Add(tid);
            }
        }
        return ids;
    }

    private static HashSet<int> CollectCurtainContainerWallIds(List<Element> elements, Dictionary<int, Element> elemById)
    {
        var ids = new HashSet<int>();
        foreach (var child in elements)
        {
            if (child.CategoryId is not (-2000170 or -2000171)) continue;
            if (child.References == null) continue;
            foreach (int refId in child.References)
            {
                if (elemById.TryGetValue(refId, out var host)
                    && host.CategoryId == (int)BuiltInCategory.OST_Walls
                    && host.Layers is not { Count: > 0 })
                {
                    ids.Add(refId);
                }
            }
        }
        return ids;
    }

    private static bool HasProjectPhaseReference(Element e)
    {
        object? val = e.Parameters.FirstOrDefault(p => p.Id == -1012100)?.Value;
        if (val is not int phase) return false;
        return (phase > 0 && (e.References?.Contains(phase) == true || e.SerializedCreatedPhaseId == phase))
            || (phase == 0 && e.SerializedCreatedPhaseId == 0);
    }

    private static bool IsOutsideActivePhase(Element elem, int[] phaseOrder, int activePhaseIdx)
    {
        if (elem.SerializedCreatedPhaseId is int createdId)
        {
            int cIdx = Array.IndexOf(phaseOrder, createdId);
            if (cIdx > activePhaseIdx) return true;
        }
        if (elem.SerializedDemolishedPhaseId is int demoId && demoId >= 0)
        {
            int dIdx = Array.IndexOf(phaseOrder, demoId);
            if (dIdx >= 0 && dIdx <= activePhaseIdx) return true;
        }
        return false;
    }

    private static bool IsNonPhysicalElement(Element elem, bool includeRooms, HashSet<int> curtainContainerWallIds)
    {
        if (curtainContainerWallIds.Contains(elem.Id))
            return true;

        int? cid = elem.CategoryId;
        if (cid == (int)BuiltInCategory.OST_Rooms || cid == (int)BuiltInCategory.OST_MEPSpaces)
            return !includeRooms;

        // Known non-3D / annotation / system / helper categories
        if (cid is (int)BuiltInCategory.OST_Levels
            or (int)BuiltInCategory.OST_Views
            or (int)BuiltInCategory.OST_Sheets
            or (int)BuiltInCategory.OST_Materials
            or (int)BuiltInCategory.OST_Cameras
            or (int)BuiltInCategory.OST_Dimensions
            or (int)BuiltInCategory.OST_ProjectInformation
            or -2000182 // OST_Reveals (void sweep subtracted from wall)
            or (int)BuiltInCategory.OST_SWallRectOpening
            or (int)BuiltInCategory.OST_ArcWallRectOpening)
        {
            return true;
        }

        string? cat = elem.Category;
        if (!string.IsNullOrEmpty(cat))
        {
            if (!includeRooms && (cat.Contains("OST_Rooms", StringComparison.OrdinalIgnoreCase)
                                  || cat.Contains("OST_MEPSpaces", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (cat.Contains("Annotation", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("RvtLink", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Tag", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Schedule", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Legend", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Titleblock", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Detail", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Camera", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Analytical", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Sketch", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("Separation", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("HiddenLines", StringComparison.OrdinalIgnoreCase)
                || cat.Contains("CenterLine", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRenderableGeometry(GeometryData? geo) =>
        geo != null && (geo.Faces.Count > 0 || geo.MeshNodes is { Count: > 0 } || geo.Solids is { Count: > 0 });

    private static Element? ResolveTypeElement(Element elem, Dictionary<int, Element> elemById)
    {
        int tid = elem.FamilyTypeId > 0 ? elem.FamilyTypeId : elem.TypeId;
        if (tid > 0 && tid != elem.Id && elemById.TryGetValue(tid, out var t))
            return t;
        return null;
    }

    private static string ResolveCategoryDisplayName(Element elem, RevitFileInfo info)
    {
        if (elem.CategoryId is int cid)
        {
            if (((BuiltInCategory)cid).TryGetCategoryName(out string? bicName) && !string.IsNullOrWhiteSpace(bicName))
                return bicName;
            if (EnglishCategoryNames.Get(cid) is { Length: > 0 } lookupName)
                return lookupName;
            if (info.CategoryNames != null && info.CategoryNames.TryGetValue(cid, out string? customCat) && !string.IsNullOrWhiteSpace(customCat))
                return customCat;
        }

        if (!string.IsNullOrWhiteSpace(elem.Category))
        {
            string raw = elem.Category.Trim();
            return raw.StartsWith("OST_", StringComparison.Ordinal) ? raw[4..] : raw;
        }

        return "Uncategorized";
    }

    private static string? ResolveFamilyName(Element elem, Element? typeElem)
    {
        if (!string.IsNullOrWhiteSpace(elem.FamilyName) && !IsArtifactName(elem.FamilyName))
            return elem.FamilyName.Trim();
        if (typeElem != null && !string.IsNullOrWhiteSpace(typeElem.FamilyName) && !IsArtifactName(typeElem.FamilyName))
            return typeElem.FamilyName.Trim();
        return null;
    }

    private static string? ResolveTypeName(Element elem, Element? typeElem)
    {
        if (typeElem != null && !string.IsNullOrWhiteSpace(typeElem.Name) && !IsArtifactName(typeElem.Name))
            return typeElem.Name.Trim();
        return null;
    }

    private static string ResolveElementDisplayName(Element elem, string? typeName, string? familyName, string categoryDisplay)
    {
        if (!string.IsNullOrWhiteSpace(elem.Name) && !IsArtifactName(elem.Name))
            return elem.Name.Trim();
        if (familyName != null && typeName != null && !string.Equals(familyName, typeName, StringComparison.OrdinalIgnoreCase))
            return $"{familyName}: {typeName}";
        if (typeName != null)
            return typeName;
        if (familyName != null)
            return familyName;
        return $"{categoryDisplay} #{elem.Id.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? ResolveLevelName(Element elem, Dictionary<int, Element> elemById)
    {
        foreach (int pid in LevelParamIds)
        {
            var p = elem.Parameters.FirstOrDefault(x => x.Id == pid);
            if (p?.Value is int lid && lid > 0 && elemById.TryGetValue(lid, out var lvl) && !string.IsNullOrWhiteSpace(lvl.Name))
                return lvl.Name.Trim();
        }
        foreach (var p in elem.Parameters)
        {
            if (p.Definition?.Name != null && LevelParamNames.Contains(p.Definition.Name)
                && p.Value is int lid && lid > 0 && elemById.TryGetValue(lid, out var lvl) && !string.IsNullOrWhiteSpace(lvl.Name))
            {
                return lvl.Name.Trim();
            }
        }
        return null;
    }

    private static string ResolveSceneTitle(RevitFileInfo info, string? customTitle)
    {
        if (!string.IsNullOrWhiteSpace(customTitle))
            return customTitle.Trim();
        if (!string.IsNullOrWhiteSpace(info.ProjectInfo?.Name))
            return info.ProjectInfo.Name.Trim();
        if (!string.IsNullOrWhiteSpace(info.Metadata?.LastSavePath))
        {
            string saved = info.Metadata.LastSavePath.Trim();
            int slash = Math.Max(saved.LastIndexOf('/'), saved.LastIndexOf('\\'));
            string fileName = slash >= 0 ? saved[(slash + 1)..] : saved;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (!string.IsNullOrWhiteSpace(stem))
                return stem;
        }
        return "Revit Model";
    }

    private static bool IsArtifactName(string name) =>
        name is "Arial" or "Times New Roman" or "Courier New" or "Verdana" or "Tahoma" or "Calibri" or "Segoe UI" or "Consolas"
        || name.StartsWith("SunAndSky", StringComparison.Ordinal)
        || name.StartsWith("assetlibrary_base", StringComparison.Ordinal)
        || name.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static List<SceneOmittedGroup> BuildOmittedGroups(Dictionary<string, int> counts)
    {
        static string LabelFor(string code) => code switch
        {
            "internal-definition" => "Internal family and geometry definitions",
            "type-or-symbol" => "Element type and symbol definitions",
            "non-physical" => "Views, levels, materials, and 2D annotations",
            "phase-filtered" => "Demolished or inactive-phase elements",
            "filtered-out" => "Excluded by caller export filter",
            "missing-geometry" => "Physical elements without decoded 3D geometry",
            _ => "Other non-visual records",
        };

        return counts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SceneOmittedGroup
            {
                Code = kv.Key,
                Reason = LabelFor(kv.Key),
                Count = kv.Value,
            })
            .ToList();
    }

    private static List<string> BuildCompletenessNotes(
        RevitFileInfo info,
        int exportedElements,
        int instancedElements,
        int meshCount,
        int instanceCount,
        int missingGeometryCount)
    {
        var notes = new List<string>();
        if (exportedElements == 0)
        {
            if (info.GeometryUndecoded)
            {
                notes.Add("3D geometry was not decoded for this file configuration. Open or export with Revit 2022 layout conversion enabled to extract 3D solids.");
            }
            else
            {
                notes.Add("No representable 3D model geometry was found in the selected scope.");
            }
        }
        else
        {
            notes.Add($"Exported {exportedElements.ToString(CultureInfo.InvariantCulture)} 3D elements across {instanceCount.ToString(CultureInfo.InvariantCulture)} placed mesh instances ({meshCount.ToString(CultureInfo.InvariantCulture)} unique mesh definitions).");
            if (instancedElements > 0)
            {
                notes.Add($"{instancedElements.ToString(CultureInfo.InvariantCulture)} repeated family instances share symbol geometry buffers.");
            }
            if (info.GeometryUndecoded)
            {
                notes.Add("Some newer-format records did not convert to 3D geometry and were omitted.");
            }
        }

        if (missingGeometryCount > 0 && !(exportedElements == 0 && info.GeometryUndecoded))
        {
            notes.Add($"{missingGeometryCount.ToString(CultureInfo.InvariantCulture)} physical model elements had no representable 3D mesh and were omitted.");
        }

        return notes;
    }
}
