using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Ray-versus-triangle against a Mesh, for the systems that need to know where geometry
    /// REALLY is rather than where its collider says it is.
    ///
    /// UNITY CANNOT RAYCAST A RENDERER — `Physics` only knows colliders, and there is no
    /// trace-complex equivalent to opt into (Unreal's flag picks between two representations of
    /// one object; Unity has exactly one, whatever collider you authored). So anything wanting
    /// triangle accuracy has to test the mesh itself, which is what this is.
    ///
    /// TWO CONSUMERS, DELIBERATELY SHARING ONE CACHE. `KitSurface` pulls an impact point onto a
    /// recessed wall's real face; `ProjectilePermeable` asks whether there is any geometry
    /// between an arrow and the far side of a barred door. Both are the same question asked of
    /// different geometry, and both are affordable for the same reason: they run ONCE PER
    /// IMPACT, not per frame.
    ///
    /// **`Mesh.vertices` ALLOCATES A FRESH ARRAY ON EVERY ACCESS**, exactly as
    /// `Renderer.sharedMaterials` does (§12) — so it is read once per mesh and kept. Without
    /// the cache every arrow would allocate two arrays the size of a wall.
    /// </summary>
    public static class MeshRay
    {
        public readonly struct Data
        {
            public readonly Vector3[] Verts;
            public readonly int[] Tris;
            public Data(Vector3[] v, int[] t) { Verts = v; Tris = t; }
            public bool Valid => Verts != null && Tris != null && Tris.Length >= 3;
        }

        static readonly Dictionary<Mesh, Data> cache = new Dictionary<Mesh, Data>();
        static readonly HashSet<Mesh> warned = new HashSet<Mesh>();

        /// <summary>
        /// Cached vertex/index arrays, or false when the mesh cannot be read.
        ///
        /// READ/WRITE ENABLED IS AN IMPORT SETTING AND ITS ABSENCE IS SILENT — the same flag,
        /// and the same works-in-editor/fails-in-a-build shape, that once dropped the stairs out
        /// of the runtime navmesh (§10). Accessing `vertices` on a non-readable mesh logs an
        /// engine error rather than throwing, so the check comes FIRST and the warning names the
        /// mesh, once.
        /// </summary>
        public static bool TryGet(Mesh mesh, out Data data)
        {
            data = default;
            if (mesh == null) return false;
            if (cache.TryGetValue(mesh, out data)) return data.Valid;

            if (!mesh.isReadable)
            {
                if (warned.Add(mesh))
                    Debug.LogWarning($"[MeshRay] Mesh '{mesh.name}' is not Read/Write Enabled, so its triangles cannot be tested. Tick Read/Write on the model importer.");
                cache[mesh] = default;      // cache the FAILURE too, or every impact re-warns
                return false;
            }

            data = new Data(mesh.vertices, mesh.triangles);
            cache[mesh] = data;
            return data.Valid;
        }

        // Vertices transformed to world once per mesh per cast, reused across calls. Sized up
        // as needed and never shrunk — physics callbacks are main-thread, so one buffer is safe.
        static Vector3[] worldScratch = new Vector3[0];

        /// <summary>
        /// Möller–Trumbore over every triangle, nearest hit wins. Brute force on purpose: one
        /// ray against one piece, once per impact — `Wall_Plain` is ~6.2k verts and that is
        /// microseconds, where a BVH would cost build time and complexity to save nothing
        /// anybody is waiting on.
        ///
        /// **THE TEST RUNS IN WORLD SPACE, AND THAT IS NOT AN AESTHETIC CHOICE — THE EPSILON IS
        /// ABSOLUTE.** The first version transformed the RAY into mesh-local space instead,
        /// which is cheaper and silently broke on scaled prefabs. The parallel-rejection test
        /// compares a determinant against a fixed 1e-8, and that determinant scales as the CUBE
        /// of the local-space size: the prison door sits at scale 300, so a 0.3m edge came out
        /// at det ~ 3.3e-9 and EVERY triangle was rejected as parallel to the ray. Field-reported
        /// as every arrow passing through a barred door including through its solid parts.
        /// `KitSurface` was unaffected only by luck — kit walls are authored at scale 1.
        ///
        /// A relative epsilon would also work and is harder to reason about. Transforming the
        /// vertices instead costs one `MultiplyPoint3x4` per VERTEX (not per triangle-corner,
        /// hence the scratch buffer) and makes every quantity in here a real world-space metre,
        /// which is what the caller's distances are in anyway.
        ///
        /// `dir` must be NORMALISED, so `t` comes out in metres.
        ///
        /// No backface culling: a wall approached from the room hits its visible face first
        /// either way, and culling would make an arrow entering a recess miss its back wall.
        /// </summary>
        public static bool Cast(in Data d, Matrix4x4 localToWorld, Vector3 origin, Vector3 dir,
                                float maxDistance, out float t)
        {
            t = float.MaxValue;
            if (!d.Valid) return false;

            var v = d.Verts;
            var tri = d.Tris;
            if (worldScratch.Length < v.Length) worldScratch = new Vector3[v.Length];
            var w = worldScratch;
            for (int i = 0; i < v.Length; i++) w[i] = localToWorld.MultiplyPoint3x4(v[i]);

            bool any = false;
            for (int i = 0; i + 2 < tri.Length; i += 3)
            {
                Vector3 a = w[tri[i]], b = w[tri[i + 1]], c = w[tri[i + 2]];
                Vector3 e1 = b - a, e2 = c - a;
                Vector3 h = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, h);
                if (det > -1e-8f && det < 1e-8f) continue;          // parallel to the triangle
                float inv = 1f / det;
                Vector3 s = origin - a;
                float u = Vector3.Dot(s, h) * inv;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(s, e1);
                float ww = Vector3.Dot(dir, q) * inv;
                if (ww < 0f || u + ww > 1f) continue;
                float hitT = Vector3.Dot(e2, q) * inv;
                if (hitT <= 1e-5f || hitT > maxDistance || hitT >= t) continue;
                t = hitT;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Cast against a LIVE hierarchy — the moving case, where the matrix has to be read now
        /// rather than recorded at generation. `filters` is passed in already gathered because
        /// `GetComponentsInChildren` allocates and the caller (a door) can hold its own list for
        /// the lifetime of the object.
        ///
        /// `tested` reports whether ANY mesh could actually be examined, which the caller needs
        /// to distinguish "looked, and there is a gap" from "could not look". Collapsing those
        /// two is how a door with unreadable meshes would read as entirely see-through.
        /// </summary>
        public static bool CastWorld(IReadOnlyList<MeshFilter> filters, Vector3 origin, Vector3 dir,
                                     float maxDistance, out float t, out bool tested)
        {
            t = float.MaxValue;
            tested = false;
            if (filters == null) return false;
            bool any = false;

            for (int i = 0; i < filters.Count; i++)
            {
                var mf = filters[i];
                if (mf == null || !TryGet(mf.sharedMesh, out Data d)) continue;
                tested = true;
                if (!Cast(d, mf.transform.localToWorldMatrix, origin, dir, maxDistance, out float hitT)) continue;
                if (hitT >= t) continue;
                t = hitT;
                any = true;
            }
            return any;
        }
    }
}
