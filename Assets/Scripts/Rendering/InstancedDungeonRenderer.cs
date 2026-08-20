using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DungeonGen
{
    /// <summary>
    /// Draws dungeon geometry and props with Graphics.RenderMeshInstanced.
    ///
    /// Culling and batching are DECOUPLED (the fix for tiny fragmented batches):
    ///   - Instances are grouped into big batches by (mesh, submesh, material)
    ///     only — NOT by chunk — so draw submissions consolidate (few large
    ///     RenderMeshInstanced calls instead of thousands of ~25-instance ones).
    ///   - Culling is per-instance each frame by DISTANCE and FRUSTUM: visible
    ///     instances are packed into a reusable scratch array and drawn. No
    ///     chunk-boundary slop, so the render distance is a true radius.
    ///     (It is a linear scan, not a spatial grid — an earlier version of this
    ///     comment claimed a grid and a `cullCellSize` field was plumbed through
    ///     for one, but neither ever existed. Removed rather than left to be
    ///     believed; a spatial grid is still the right move if the scan ever
    ///     shows up in a profile.)
    ///   - BOTH culls are ours to do. Unity culls a RenderMeshInstanced call as a
    ///     UNIT against RenderParams.worldBounds, and batches here are deliberately
    ///     not chunk-keyed, so those bounds span the whole dungeon and never reject
    ///     anything. Nothing is culled by the engine on this path.
    ///
    /// Usage unchanged: AddInstance(prefab, matrix) repeatedly, then Commit()
    /// (idempotent/additive — call after each placement pass).
    ///
    /// Visual only; collision comes from the greybox mesh. Batches aren't
    /// serialized — regenerate after a recompile.
    /// </summary>
    [ExecuteAlways]
    public class InstancedDungeonRenderer : MonoBehaviour
    {
        [Tooltip("True cull radius in meters. Instances beyond this from the camera aren't drawn. Pair with fog fading to dark before this distance. 0 = draw everything.")]
        public float renderDistance = 45f;

        [Tooltip("Radius within which instances are allowed to CAST shadows, in meters. Much shorter than renderDistance on purpose — a point light's shadows are a 6-face cubemap, so every casting instance is rasterized six times per shadow-casting torch.\n\nSize it to the URP asset's Shadow Distance plus a small margin: past that nothing it casts can land anywhere visible. 0 = casters are not distance-limited (the old behaviour).")]
        public float shadowDistance = 18f;

        [Tooltip("Skip instances outside the camera frustum.\n\nThis path gets NO frustum culling from Unity: RenderMeshInstanced culls per DRAW against worldBounds, and batches here span the whole dungeon, so everything within renderDistance was submitted whether or not it was behind you. Off = that old behaviour.\n\nShadow casters are deliberately exempt — one behind the camera still throws its shadow into view.")]
        public bool frustumCull = true;

        [Tooltip("Skip instances in cells you cannot reach without walking a long way — occlusion culling, computed from the GRID rather than from geometry. In a corridor the frustum is far wider than what you can actually see, and almost everything in it is behind a wall.\n\nOff restores frustum + distance only.")]
        public bool occlusionCull = true;

        /// <summary>
        /// Set by DungeonVisualizer at generation. Null = occlusion culling is inert, which is the
        /// correct behaviour rather than a failure: it fails OPEN and everything draws.
        /// </summary>
        [System.NonSerialized] public DungeonVisibility visibility;

        class Batch
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public bool CastShadows = true;                       // receiveShadows stays true for ALL batches
            public List<Matrix4x4> All = new List<Matrix4x4>();  // every instance
            public List<Vector3> Positions = new List<Vector3>(); // parallel, for culling
            /// <summary>Flat grid cell per instance, resolved ONCE at AddInstance. -1 = off-grid.</summary>
            public List<int> Cells = new List<int>();
            public Matrix4x4[] Scratch;                           // per-frame visible set
            public Matrix4x4[] ShadowScratch;                     // per-frame CASTING subset
            public Bounds Bounds;
            public bool HasBounds;
            // Largest instance radius in this batch, used to pad the shadow
            // submission's tight bounds out from instance ORIGINS to geometry.
            public float MaxRadius;
        }

        class Proto
        {
            public List<(Mesh mesh, int submesh, Material mat, Matrix4x4 local)> Parts
                = new List<(Mesh, int, Material, Matrix4x4)>();
        }

        // CastShadows is part of the key: shadow-casting and non-casting
        // instances of the same (mesh, submesh, material) must live in
        // separate batches, since the flag maps to a per-RenderParams mode.
        struct BatchKey : System.IEquatable<BatchKey>
        {
            public Mesh Mesh; public int Submesh; public Material Mat; public bool CastShadows;
            public bool Equals(BatchKey o) =>
                Mesh == o.Mesh && Submesh == o.Submesh && Mat == o.Mat && CastShadows == o.CastShadows;
            public override int GetHashCode() =>
                (Mesh ? Mesh.GetHashCode() : 0) ^ (Mat ? Mat.GetHashCode() * 397 : 0) ^ Submesh * 31
                ^ (CastShadows ? 0x10000 : 0);
            public override bool Equals(object o) => o is BatchKey k && Equals(k);
        }

        // Reused: CalculateFrustumPlanes(Camera) allocates a fresh array per call, and the plane
        // components are unpacked into flats so the inner loop touches no struct properties.
        readonly Plane[] frustumPlanes = new Plane[6];
        readonly float[] planeNX = new float[6], planeNY = new float[6],
                         planeNZ = new float[6], planeD = new float[6];

        readonly List<Batch> batches = new List<Batch>();
        readonly Dictionary<GameObject, Proto> protoCache = new Dictionary<GameObject, Proto>();
        readonly Dictionary<BatchKey, Batch> batchLookup = new Dictionary<BatchKey, Batch>();

        public int InstanceCount { get; private set; }
        public int BatchCount => batches.Count;

        public void Clear()
        {
            batches.Clear();
            protoCache.Clear();
            batchLookup.Clear();
            InstanceCount = 0;
        }

        /// <summary>
        /// `replaceMat`/`withMat` swap ONE of the prefab's materials for this placement —
        /// the seam for per-instance appearance without giving up instancing. There's no
        /// MaterialPropertyBlock here to tint (nothing renders through the prefab's
        /// MeshRenderer; it's only harvested once into a Proto), so varying a material
        /// means varying the MATERIAL — and BatchKey already includes it, so a swapped
        /// material simply lands in its own batch and draws its own colour for free.
        /// Callers are expected to hand in a CACHED variant per distinct look, or every
        /// placement becomes its own batch.
        /// </summary>
        public void AddInstance(GameObject prefab, Matrix4x4 placement, bool castShadows = true,
                                Material replaceMat = null, Material withMat = null)
        {
            if (prefab == null) return;
            if (!protoCache.TryGetValue(prefab, out Proto proto))
            {
                proto = BuildProto(prefab);
                protoCache[prefab] = proto;
            }

            foreach (var part in proto.Parts)
            {
                Material mat = (replaceMat != null && withMat != null && part.mat == replaceMat)
                    ? withMat : part.mat;

                var key = new BatchKey { Mesh = part.mesh, Submesh = part.submesh, Mat = mat, CastShadows = castShadows };
                if (!batchLookup.TryGetValue(key, out Batch b))
                {
                    b = new Batch { Mesh = part.mesh, Submesh = part.submesh, Material = mat, CastShadows = castShadows };
                    batchLookup[key] = b;
                    batches.Add(b);
                }

                Matrix4x4 m = placement * part.local;
                Vector3 p = m.GetColumn(3);
                b.All.Add(m);
                b.Positions.Add(p);
                // Resolved once, here, rather than per frame: the world-to-cell conversion is a
                // divide and three floors, which is nothing once and ~20k times a frame is not.
                b.Cells.Add(visibility != null ? visibility.IndexOf(p) : -1);

                float r = part.mesh.bounds.extents.magnitude * MaxScale(m) + 0.5f;
                if (r > b.MaxRadius) b.MaxRadius = r;
                var bb = new Bounds(p, Vector3.one * (r * 2f));
                if (!b.HasBounds) { b.Bounds = bb; b.HasBounds = true; }
                else b.Bounds.Encapsulate(bb);
            }
            InstanceCount++;
        }

        public void Commit()
        {
            // Ensure each batch has a scratch buffer big enough for all its
            // instances (worst case: everything visible).
            foreach (var b in batches)
            {
                if (b.Scratch == null || b.Scratch.Length < b.All.Count)
                    b.Scratch = new Matrix4x4[b.All.Count];
                if (b.CastShadows && (b.ShadowScratch == null || b.ShadowScratch.Length < b.All.Count))
                    b.ShadowScratch = new Matrix4x4[b.All.Count];
            }
        }

        Proto BuildProto(GameObject prefab)
        {
            var proto = new Proto();
            Transform root = prefab.transform;
            Matrix4x4 rootCorrection =
                Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;

                Matrix4x4 local = rootCorrection * mf.transform.localToWorldMatrix;
                Material[] mats = mr.sharedMaterials;
                int subCount = Mathf.Min(mf.sharedMesh.subMeshCount, mats.Length);
                for (int s = 0; s < subCount; s++)
                {
                    Material mat = mats[s];
                    if (mat == null) continue;
                    if (!mat.enableInstancing)
                    {
                        mat.enableInstancing = true;
                        Debug.LogWarning($"[Instanced] Enabled GPU Instancing on material '{mat.name}'. Consider ticking it in the material asset.");
                    }
                    proto.Parts.Add((mf.sharedMesh, s, mat, local));
                }
            }

            if (proto.Parts.Count == 0)
                Debug.LogWarning($"[Instanced] Prefab '{prefab.name}' has no MeshRenderer parts to instance.");
            return proto;
        }

        void Update()
        {
            Vector3 camPos = Vector3.zero;
            bool haveCam = false;
            Camera cam = null;
            if (Application.isPlaying)
            {
                var mc = Camera.main;
                if (mc != null) { cam = mc; camPos = mc.transform.position; haveCam = true; }
            }
#if UNITY_EDITOR
            else
            {
                var sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null)
                { cam = sv.camera; camPos = sv.camera.transform.position; haveCam = true; }
            }
#endif
            bool cull = haveCam && renderDistance > 0f;
            float maxSq = renderDistance * renderDistance;

            // PER-INSTANCE FRUSTUM CULLING, WHICH THIS PATH OTHERWISE GETS NONE OF.
            //
            // Unity frustum-culls a RenderMeshInstanced call as a UNIT against
            // RenderParams.worldBounds. Batching here is deliberately not chunk-keyed, so those
            // bounds span the WHOLE DUNGEON and always intersect the frustum — meaning every
            // instance inside renderDistance was submitted regardless of whether it sat behind
            // the camera. Not "poorly culled": not culled at all. Same root cause as the shadow
            // re-submission above, and the reason renderDistance had to be generous enough for
            // the longest sightline while paying for a full sphere of geometry in every view.
            //
            // The plane components are unpacked into flat floats once per frame rather than
            // calling Plane.GetDistanceToPoint per instance: this runs over ~20k instances every
            // frame on the main thread, which is already the busier half of the frame.
            // Rebuilt only when the viewer crosses a cell boundary — RefreshFor early-outs
            // otherwise, so this is a Vector3Int compare on the frames it does nothing.
            bool occlude = haveCam && occlusionCull && visibility != null;
            if (occlude) visibility.RefreshFor(camPos);

            bool frustum = haveCam && frustumCull && cam != null;
            if (frustum)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, frustumPlanes);
                for (int p = 0; p < 6; p++)
                {
                    Vector3 n = frustumPlanes[p].normal;
                    planeNX[p] = n.x; planeNY[p] = n.y; planeNZ[p] = n.z;
                    planeD[p] = frustumPlanes[p].distance;
                }
            }

            // SHADOW CASTING IS SUBMITTED SEPARATELY, ON A SHORTER RADIUS, WITH ITS OWN
            // TIGHT BOUNDS — and both halves of that are load-bearing.
            //
            // Unity culls a RenderMeshInstanced call as a UNIT against RenderParams.worldBounds;
            // there is no per-instance culling on the engine side (the renderDistance pack loop
            // below is us doing it ourselves, and only against the CAMERA). Batching here is
            // deliberately not chunk-keyed, so a batch's bounds span the WHOLE DUNGEON — which
            // means a casting batch intersects every torch's shadow volume and cannot be culled
            // out of any of them. A point light's shadows are a 6-face cubemap, so at N casting
            // torches the entire renderDistance set was being rasterized 6N times, against a URP
            // Shadow Distance far shorter than renderDistance. Measured at 6 casters: 1479
            // instanced draws for 21.4k instances (~14.5 per call) where the lit pass alone runs
            // ~37 per call, and 166M triangles against a 114M baseline.
            //
            // So casting batches now draw twice: the lit set with casting OFF, and a much smaller
            // near-camera subset as ShadowsOnly. The tight bounds is the half that is easy to skip
            // and does most of the work — with whole-dungeon bounds the shadow draw is still
            // submitted to every face regardless of how few instances it holds.
            float shadowRadius = shadowDistance;
            // Clamped to renderDistance because the whole-batch early-out below tests against
            // renderDistance: a caster past it is skipped entirely, so a larger shadow radius
            // would silently claim a range it cannot actually serve.
            if (cull && shadowRadius > renderDistance) shadowRadius = renderDistance;
            bool splitShadows = haveCam && shadowRadius > 0f;
            float shadowSq = shadowRadius * shadowRadius;

            for (int i = 0; i < batches.Count; i++)
            {
                Batch b = batches[i];
                int total = b.All.Count;
                if (total == 0 || !b.HasBounds) continue;
                if (b.Scratch == null || b.Scratch.Length < total) b.Scratch = new Matrix4x4[total];

                // Whole batch beyond range? skip without touching instances.
                if (cull && b.Bounds.SqrDistance(camPos) > maxSq) continue;

                bool splitThis = b.CastShadows && splitShadows;
                if (splitThis && (b.ShadowScratch == null || b.ShadowScratch.Length < total))
                    b.ShadowScratch = new Matrix4x4[total];

                // Pack visible instances into the scratch buffer, and the casting subset into
                // its own. The straight copy survives only where neither cull applies.
                int visible = 0, casting = 0;
                Vector3 sMin = Vector3.zero, sMax = Vector3.zero;

                if (!cull && !splitThis && !frustum && !occlude)
                {
                    visible = total;
                    b.All.CopyTo(b.Scratch);
                }
                else
                {
                    var positions = b.Positions;
                    var all = b.All;
                    var cells = b.Cells;
                    for (int k = 0; k < total; k++)
                    {
                        Vector3 p = positions[k];
                        Vector3 d = p - camPos;
                        float dsq = d.x * d.x + d.y * d.y + d.z * d.z;
                        if (cull && dsq > maxSq) continue;

                        // SHADOW CASTERS ARE PACKED BEFORE THE FRUSTUM TEST AND ARE NEVER SUBJECT
                        // TO IT. A caster standing behind the camera still throws a shadow INTO
                        // view — that is most of what a torch behind you is doing — so frustum
                        // culling the shadow set would delete shadows whose casters simply happen
                        // to be off screen, which reads as objects losing their shadow when you
                        // turn. The two subsets are therefore no longer nested: shadow is
                        // distance-only, lit is distance AND frustum.
                        if (splitThis && dsq <= shadowSq)
                        {
                            b.ShadowScratch[casting] = all[k];
                            if (casting == 0) { sMin = p; sMax = p; }
                            else { sMin = Vector3.Min(sMin, p); sMax = Vector3.Max(sMax, p); }
                            casting++;
                        }

                        // OCCLUSION BEFORE FRUSTUM, because it is one array index against up to
                        // six plane tests, and in a corridor it rejects the larger share. Like the
                        // frustum test it applies to the LIT set only — a caster behind a wall can
                        // still throw a shadow through a doorway into view.
                        if (occlude && !visibility.IsVisible(cells[k])) continue;

                        // Sphere test against the six planes, early-out on the first that rejects
                        // — and most rejections come from the near plane on the first iteration,
                        // which is what keeps this affordable at instance counts like these.
                        if (frustum)
                        {
                            float r = b.MaxRadius;
                            bool outside = false;
                            for (int q = 0; q < 6; q++)
                            {
                                if (planeNX[q] * p.x + planeNY[q] * p.y + planeNZ[q] * p.z
                                    + planeD[q] < -r) { outside = true; break; }
                            }
                            if (outside) continue;
                        }

                        b.Scratch[visible++] = all[k];
                    }
                }

                if (visible > 0)
                {
                    var rp = new RenderParams(b.Material)
                    {
                        worldBounds = b.Bounds,
                        // Static shell batches never cast (wall-on-wall shadows are invisible
                        // but dominate the cubemap passes); everything still RECEIVES so detail
                        // shadows fall across walls and floors. A batch that DOES cast has its
                        // casting served by the ShadowsOnly draw below, so it opts out here —
                        // leaving it On would submit the full lit set to every face again and
                        // undo the whole thing.
                        shadowCastingMode = (b.CastShadows && !splitThis)
                            ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = true,
                    };
                    DrawChunked(rp, b, b.Scratch, visible);
                }

                if (splitThis && casting > 0)
                {
                    // Padded from instance ORIGINS out to geometry: sMin/sMax bound the pivots,
                    // and a mesh straddles its own pivot. Under-padding here culls a caster whose
                    // shadow was still on screen, which reads as flickering contact shadows.
                    var tight = new Bounds();
                    Vector3 pad = Vector3.one * b.MaxRadius;
                    tight.SetMinMax(sMin - pad, sMax + pad);

                    var rp = new RenderParams(b.Material)
                    {
                        worldBounds = tight,
                        shadowCastingMode = ShadowCastingMode.ShadowsOnly,
                        receiveShadows = false,
                        // NO `forceMeshLod` HERE, AND IT IS NOT AN OVERSIGHT — MESH LOD DOES NOT
                        // WORK ON THIS PATH. A shadow caster would be the ideal place to spend
                        // coarse geometry, and Unity's docs list RenderMeshInstanced as taking
                        // `forceMeshLod` since it gets no automatic screen-size selection. It is
                        // range-checked and then ignored: a mesh whose LOD 7 is visibly decimated
                        // in the Inspector renders identically at forceMeshLod 7, with no GPU
                        // time change across half the dungeon's geometry — while an out-of-range
                        // value makes geometry vanish, which is what proves the field is READ.
                        // The docs' other line, that "GPU instancing" always uses LOD0, is the
                        // one that governs. See §5 before trying this again.
                    };
                    DrawChunked(rp, b, b.ShadowScratch, casting);
                }
            }
        }

        /// <summary>
        /// Rank the meshes THIS RENDERER ACTUALLY DRAWS by their total geometry cost
        /// (vertices x instances) — the targeting tool for roadmap 28b.
        ///
        /// IT MEASURES WHAT IS DRAWN, NOT WHAT IS IN THE PROJECT, and that is the point:
        /// `BuildProto` harvests `MeshFilter.sharedMesh` off the kit prefabs, and cost is
        /// per-instance detail MULTIPLIED BY how often the generator places it — which no
        /// inspection of the Project window can tell you. The first run found a ceiling tile at
        /// 24,749 verts x 1006 instances, i.e. 38% of the dungeon's geometry on the surface
        /// players look at least, which nobody had suspected.
        ///
        /// IT ALSO SURFACES DUPLICATE MESHES. Two entries with an identical vertex count and a
        /// Blender `.001` suffix are one mesh imported twice — and since `BatchKey` includes the
        /// mesh, duplicates can NEVER merge, so that is two batches drawing the same thing.
        ///
        /// `lodCount` is reported for information only. Mesh LOD does not work on this path
        /// (see the shadow submission above), so a mesh carrying LODs gains nothing here today.
        /// </summary>
        [ContextMenu("Log Geometry Cost Report")]
        public void LogGeometryCostReport()
        {
            if (batches.Count == 0)
            {
                // NOT NECESSARILY "you forgot to generate". `batches` is a plain field and is
                // NOT serialized, so a script recompile's domain reload empties it while the
                // GameObject survives — the component is found, holds nothing, and the instanced
                // geometry stops drawing. That is this class's standing "regenerate after a
                // recompile" note arriving as a diagnostic, and it is the common case while
                // iterating on the renderer itself.
                Debug.LogWarning("[Instanced] This renderer holds no batches. If the dungeon LOOKS " +
                                 "generated, a script recompile has since wiped them (batches are not " +
                                 "serialized) — regenerate and run this again. Otherwise generate " +
                                 "first, and check geometryMode is InstancedKit.", this);
                return;
            }

            // Instances are summed PER MESH, not per batch: one mesh split across several
            // materials or submeshes is several batches, and a per-batch listing would divide its
            // true cost between rows and hide the worst offender.
            var instancesOf = new Dictionary<Mesh, int>();
            foreach (var b in batches)
            {
                if (b.Mesh == null) continue;
                instancesOf.TryGetValue(b.Mesh, out int n);
                instancesOf[b.Mesh] = n + b.All.Count;
            }

            // vertexCount rather than triangles.Length: `triangles` needs the mesh to be
            // Read/Write Enabled, which most kit pieces are not (§10's build-only navmesh trap is
            // the same import flag), and a diagnostic that throws on the assets it was written to
            // inspect is worse than one reporting a proxy.
            var rows = new List<(Mesh mesh, int instances, long cost)>();
            long total = 0;
            foreach (var kv in instancesOf)
            {
                long cost = (long)kv.Key.vertexCount * kv.Value;
                total += cost;
                rows.Add((kv.Key, kv.Value, cost));
            }
            rows.Sort((a, z) => z.cost.CompareTo(a.cost));

            var sb = new System.Text.StringBuilder();
            sb.Append($"[Instanced] Geometry cost, worst first — {rows.Count} distinct mesh(es), " +
                      $"{total / 1000000f:0.0}M verts across the dungeon:");
            foreach (var r in rows)
                sb.Append($"\n  {r.cost / 1000000f,6:0.00}M  {100f * r.cost / Mathf.Max(1, total),5:0.0}%  " +
                          $"{r.mesh.name}  ({r.mesh.vertexCount} verts x {r.instances}" +
                          $"{(r.mesh.lodCount > 1 ? $", lodCount {r.mesh.lodCount}" : "")})");

            // FILE AS WELL AS CONSOLE. The Console's log-level toggles and search filter both
            // hide output silently, and a report that never appears is indistinguishable from a
            // method that never ran — which has cost this project several rounds. A file cannot
            // be filtered.
            Debug.LogWarning(sb.ToString(), this);
            try
            {
                string path = System.IO.Path.Combine(Application.dataPath, "..", "GeometryCostReport.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Instanced] Could not write report file: {e.Message}", this);
            }
        }

        static void DrawChunked(in RenderParams rp, Batch b, Matrix4x4[] buffer, int count)
        {
            for (int start = 0; start < count; start += 1023)
                Graphics.RenderMeshInstanced(rp, b.Mesh, b.Submesh, buffer,
                                             Mathf.Min(1023, count - start), start);
        }

        static float MaxScale(Matrix4x4 m)
        {
            float sx = ((Vector3)m.GetColumn(0)).magnitude;
            float sy = ((Vector3)m.GetColumn(1)).magnitude;
            float sz = ((Vector3)m.GetColumn(2)).magnitude;
            return Mathf.Max(sx, Mathf.Max(sy, sz));
        }
    }

    /// <summary>
    /// Authoring for <see cref="InstancedDungeonRenderer"/>, held on `DungeonVisualizer`.
    ///
    /// IT LIVES THERE BECAUSE THE RENDERER ONLY EXISTS IN PLAY MODE. `DungeonVisualizer` builds
    /// `DungeonInstanced` with `AddComponent` during generation, so the component takes its C#
    /// defaults on EVERY generate and there is no serialized row anywhere to author. Anything
    /// typed into that inspector is lost on the next F1 — which made every dial here effectively
    /// a compile-time constant, and made an inspector-driven experiment silently test nothing.
    /// Same reason `FogSettings` and `OcclusionSettings` sit on the visualizer rather than on the
    /// runtime component they configure; `TorchSettings` → `TorchCullingManager` is the pattern
    /// this copies.
    /// </summary>
    [System.Serializable]
    public class InstancedSettings
    {
        [Tooltip("True cull radius in metres for instanced geometry. Pair with fog fading to dark before this distance. 0 = draw everything.")]
        public float renderDistance = 45f;

        [Tooltip("Skip instances outside the camera frustum. This path gets none from Unity — RenderMeshInstanced culls per DRAW against bounds that span the whole dungeon — so everything within renderDistance was drawn whether or not it was behind you.\n\nShadow casters stay exempt: one behind the camera still throws its shadow into view.")]
        public bool frustumCull = true;

        [Tooltip("Occlusion culling from the GRID: skip instances in cells you could not walk to without going a long way round, which is what 'behind a wall' means in a cell dungeon. Unity's own occlusion culling cannot help here — baked occlusion needs a static scene, and GPU occlusion needs the Resident Drawer, which does not drive RenderMeshInstanced.\n\nIn a corridor the frustum is far wider than what you can see, so this is where the remaining geometry goes.")]
        public bool occlusionCull = true;

        [Tooltip("Radius within which instanced geometry may CAST shadows. Much shorter than renderDistance: a point light's shadows are a 6-face cubemap, so a caster in range of N shadow-casting torches is rasterized 6N times. Size it to the URP asset's Shadow Distance plus a margin. 0 = casters are not distance-limited.")]
        public float shadowDistance = 18f;

        public void ApplyTo(InstancedDungeonRenderer ir)
        {
            if (ir == null) return;
            ir.renderDistance = renderDistance;
            ir.frustumCull = frustumCull;
            ir.occlusionCull = occlusionCull;
            ir.shadowDistance = shadowDistance;
        }
    }
}