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
    ///   - Culling is per-instance each frame via a coarse spatial grid: only
    ///     instances within renderDistance of the camera are packed into a
    ///     reusable scratch array and drawn. No chunk-boundary slop, so the
    ///     render distance is a true radius.
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

        [Tooltip("Spatial grid cell for culling queries, meters. Only affects cull cost, not batching. ~12-24 is fine.")]
        public float cullCellSize = 16f;

        class Batch
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public bool CastShadows = true;                       // receiveShadows stays true for ALL batches
            public List<Matrix4x4> All = new List<Matrix4x4>();  // every instance
            public List<Vector3> Positions = new List<Vector3>(); // parallel, for culling
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
            if (Application.isPlaying)
            {
                var mc = Camera.main;
                if (mc != null) { camPos = mc.transform.position; haveCam = true; }
            }
#if UNITY_EDITOR
            else
            {
                var sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv != null && sv.camera != null) { camPos = sv.camera.transform.position; haveCam = true; }
            }
#endif
            bool cull = haveCam && renderDistance > 0f;
            float maxSq = renderDistance * renderDistance;

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

                if (!cull && !splitThis)
                {
                    visible = total;
                    b.All.CopyTo(b.Scratch);
                }
                else
                {
                    var positions = b.Positions;
                    var all = b.All;
                    for (int k = 0; k < total; k++)
                    {
                        Vector3 p = positions[k];
                        Vector3 d = p - camPos;
                        float dsq = d.x * d.x + d.y * d.y + d.z * d.z;
                        if (cull && dsq > maxSq) continue;

                        b.Scratch[visible++] = all[k];

                        if (splitThis && dsq <= shadowSq)
                        {
                            b.ShadowScratch[casting] = all[k];
                            if (casting == 0) { sMin = p; sMax = p; }
                            else { sMin = Vector3.Min(sMin, p); sMax = Vector3.Max(sMax, p); }
                            casting++;
                        }
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
                    };
                    DrawChunked(rp, b, b.ShadowScratch, casting);
                }
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
}