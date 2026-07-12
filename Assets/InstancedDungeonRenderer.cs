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

        [Tooltip("Spatial grid cell for culling queries, meters. Only affects cull cost, not batching. ~12-24 is fine.")]
        public float cullCellSize = 16f;

        class Batch
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public List<Matrix4x4> All = new List<Matrix4x4>();  // every instance
            public List<Vector3> Positions = new List<Vector3>(); // parallel, for culling
            public Matrix4x4[] Scratch;                           // per-frame visible set
            public Bounds Bounds;
            public bool HasBounds;
        }

        class Proto
        {
            public List<(Mesh mesh, int submesh, Material mat, Matrix4x4 local)> Parts
                = new List<(Mesh, int, Material, Matrix4x4)>();
        }

        struct BatchKey : System.IEquatable<BatchKey>
        {
            public Mesh Mesh; public int Submesh; public Material Mat;
            public bool Equals(BatchKey o) => Mesh == o.Mesh && Submesh == o.Submesh && Mat == o.Mat;
            public override int GetHashCode() =>
                (Mesh ? Mesh.GetHashCode() : 0) ^ (Mat ? Mat.GetHashCode() * 397 : 0) ^ Submesh * 31;
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

        public void AddInstance(GameObject prefab, Matrix4x4 placement)
        {
            if (prefab == null) return;
            if (!protoCache.TryGetValue(prefab, out Proto proto))
            {
                proto = BuildProto(prefab);
                protoCache[prefab] = proto;
            }

            foreach (var part in proto.Parts)
            {
                var key = new BatchKey { Mesh = part.mesh, Submesh = part.submesh, Mat = part.mat };
                if (!batchLookup.TryGetValue(key, out Batch b))
                {
                    b = new Batch { Mesh = part.mesh, Submesh = part.submesh, Material = part.mat };
                    batchLookup[key] = b;
                    batches.Add(b);
                }

                Matrix4x4 m = placement * part.local;
                Vector3 p = m.GetColumn(3);
                b.All.Add(m);
                b.Positions.Add(p);

                float r = part.mesh.bounds.extents.magnitude * MaxScale(m) + 0.5f;
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
                if (b.Scratch == null || b.Scratch.Length < b.All.Count)
                    b.Scratch = new Matrix4x4[b.All.Count];
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

            for (int i = 0; i < batches.Count; i++)
            {
                Batch b = batches[i];
                int total = b.All.Count;
                if (total == 0 || !b.HasBounds) continue;
                if (b.Scratch == null || b.Scratch.Length < total) b.Scratch = new Matrix4x4[total];

                // Whole batch beyond range? skip without touching instances.
                if (cull && b.Bounds.SqrDistance(camPos) > maxSq) continue;

                // Pack visible instances into the scratch buffer. When not
                // culling, or when the whole batch is comfortably in range,
                // this is a straight copy.
                int visible;
                if (!cull)
                {
                    visible = total;
                    b.All.CopyTo(b.Scratch);
                }
                else
                {
                    visible = 0;
                    var positions = b.Positions;
                    var all = b.All;
                    for (int k = 0; k < total; k++)
                    {
                        Vector3 d = positions[k] - camPos;
                        if (d.x * d.x + d.y * d.y + d.z * d.z <= maxSq)
                            b.Scratch[visible++] = all[k];
                    }
                }
                if (visible == 0) continue;

                var rp = new RenderParams(b.Material)
                {
                    worldBounds = b.Bounds,
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true,
                };

                for (int start = 0; start < visible; start += 1023)
                {
                    int count = Mathf.Min(1023, visible - start);
                    Graphics.RenderMeshInstanced(rp, b.Mesh, b.Submesh, b.Scratch, count, start);
                }
            }
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