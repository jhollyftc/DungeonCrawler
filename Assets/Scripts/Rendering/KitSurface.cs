using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Where the kit's VISIBLE surface actually is, for the handful of systems that need a
    /// hit point accurate enough to leave something behind.
    ///
    /// THE PROBLEM IT SOLVES: collision truth is the greybox (§5), and the kit is visual only.
    /// The greybox emits ONE flat quad across a cell face, inset toward the room by
    /// `wallMargin` so it clears protruding relief — but a kit wall with a RECESS has geometry
    /// running back behind that plane with no collision counterpart at all. So an arrow into a
    /// niche stops on an invisible plane and hangs in mid-air in front of the recess, and every
    /// ordinary wall hit lands `wallMargin` proud of the masonry you can see.
    ///
    /// THIS IS NOT A COLLISION SYSTEM AND MUST NOT BECOME ONE. Colliders are ADDITIVE — you
    /// cannot subtract a recess out of the greybox — so the only real fix would be to suppress
    /// that face and let the kit carry its own collider, which needs the wall pick to happen
    /// before `DungeonMesher` runs and makes recesses physically enterable. This does none of
    /// that: the arrow still collides with the greybox exactly as before, and afterwards asks
    /// where the visible surface really was. Nothing about movement, pathing or LOS changes.
    ///
    /// UNITY CANNOT RAYCAST A RENDERER — `Physics` only knows colliders, and an instanced kit
    /// piece has no GameObject, no MeshFilter and no MeshRenderer at all; it is a Matrix4x4 in
    /// a batch. So the mesh is tested directly in script (Möller–Trumbore over `mesh.triangles`),
    /// which is affordable precisely because it runs ONCE PER IMPACT rather than per frame:
    /// `Wall_Plain` is ~6.2k verts and a single ray against it is microseconds.
    ///
    /// RECORD-THEN-CONSUME, the `KitSocketPlacer` pattern: `DungeonKitPlacer` records a site as
    /// each wall emits, the visualizer installs the lot once the kit is built, and lookups are
    /// keyed by (cell, face) so a query is O(1) plus one mesh.
    /// </summary>
    public static class KitSurface
    {
        struct Piece
        {
            public GameObject Prefab;
            public Matrix4x4 Matrix;      // the instance matrix, composed exactly as AddInstance does
        }

        struct Part
        {
            public MeshRay.Data Mesh;
            public Matrix4x4 Local;       // mesh -> prefab-root space
        }

        static readonly Dictionary<long, Piece> byFace = new Dictionary<long, Piece>();

        // Per PREFAB, not per face — a dungeon places one wall mesh thousands of times and the
        // vertex arrays are identical every time. Survives a regenerate on purpose: these are
        // asset arrays and the answer cannot change.
        static readonly Dictionary<GameObject, Part[]> partCache = new Dictionary<GameObject, Part[]>();

        static float cellSize = 3f;
        static Vector3 origin;
        static Collider shell;            // the greybox; the ONLY collider a refinement may follow
        static bool ready;

        /// <summary>
        /// Names the REASON a refinement was declined, rather than leaving an arrow on the
        /// greybox with nothing to look at. Every rejection here is silent by design and they
        /// are not distinguishable from each other on screen — an unrecorded face, an
        /// unreadable mesh, a missed ray and an over-deep hit all present identically as "the
        /// arrow stopped where the collider is". §12's instrument-before-hypothesising rule:
        /// the first angled-shot report cost a round of guessing that one line would have
        /// answered. Static so it can be flipped without finding an instance.
        /// </summary>
        public static bool debug;

        /// <summary>Fast-enter-playmode keeps statics, and a stale face map from a previous run
        /// would point at meshes for a dungeon that no longer exists — the same trap `NoiseBus`
        /// guards against.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { byFace.Clear(); shell = null; ready = false; }

        public static void Clear()
        {
            byFace.Clear();
            shell = null;
            ready = false;
        }

        /// <summary>
        /// Called once by `DungeonVisualizer` after the kit has emitted. `shellCollider` is the
        /// greybox's own collider and is what makes this SAFE: a refinement only ever follows a
        /// hit on the shell, so an arrow that struck a crate standing against a wall is never
        /// dragged through the crate into the masonry behind it.
        /// </summary>
        public static void Install(List<DungeonKitPlacer.SurfaceSite> sites, float cell, Vector3 worldOrigin,
                                   Collider shellCollider)
        {
            Clear();
            cellSize = Mathf.Max(0.01f, cell);
            origin = worldOrigin;
            shell = shellCollider;
            if (sites == null || shellCollider == null) return;

            foreach (var s in sites)
            {
                if (s.Prefab == null || s.FaceDir == Vector3Int.zero) continue;
                // Composed EXACTLY as the visualizer's PlaceCallback composes it for
                // AddInstance. Two places building this matrix differently is how the tested
                // point and the rendered point would silently drift apart.
                var m = Matrix4x4.TRS(s.PosCells * cellSize + s.OffsetMeters + origin, s.Rot, Vector3.one);
                byFace[Key(s.Cell, s.FaceDir)] = new Piece { Prefab = s.Prefab, Matrix = m };
            }
            ready = byFace.Count > 0;
        }

        /// <summary>
        /// Pulls a contact point on the greybox onto the kit's real surface.
        ///
        /// FAILS OPEN, always: an unknown face, an unreadable mesh, a ray that misses, or a
        /// correction further than `maxDistance` all leave the caller's original point alone.
        /// The error is one-directional — a slightly proud arrow is a cosmetic miss, an arrow
        /// teleported inside masonry is a bug.
        /// </summary>
        public static bool Refine(Collider hit, Vector3 point, Vector3 normal, Vector3 direction,
                                  float maxDistance, out Vector3 refined)
        {
            refined = point;
            if (!ready || hit == null || hit != shell) return false;
            if (direction.sqrMagnitude < 1e-6f) return false;
            Vector3 dir = direction.normalized;

            // The contact normal points OUT of the surface, so stepping along it lands in the
            // open cell — which is the one the wall face belongs to. Flooring the contact point
            // itself cannot work: it sits ON the boundary, so it resolves to the solid or the
            // open side depending which way the face happens to point (§5's PlaceCallback rule).
            Vector3Int cell = CellOf(point + normal * (cellSize * 0.1f));
            Vector3Int face = RoundDir(-normal);
            if (!byFace.TryGetValue(Key(cell, face), out Piece piece))
            {
                if (debug) Debug.Log($"[KitSurface] no wall recorded at cell {cell} face {face} — left on the greybox.");
                return false;
            }

            Part[] parts = PartsOf(piece.Prefab);
            if (parts == null || parts.Length == 0)
            {
                if (debug) Debug.Log($"[KitSurface] '{piece.Prefab.name}' has no readable mesh parts — left on the greybox.");
                return false;
            }

            // Start slightly BEFORE the impact so a wall whose masonry protrudes past the
            // inset greybox is still caught, rather than the ray starting behind it.
            float backOff = Mathf.Min(0.1f, maxDistance);
            Vector3 rayOrigin = point - dir * backOff;

            // Bounded loosely rather than by `maxDistance`: that clamp is on DEPTH behind the
            // face, and an oblique shot legitimately travels further than it goes deep. The
            // depth test below is the real gate.
            float best = float.MaxValue;
            float reach = maxDistance * 8f + backOff;
            for (int i = 0; i < parts.Length; i++)
                if (MeshRay.Cast(parts[i].Mesh, piece.Matrix * parts[i].Local, rayOrigin, dir, reach, out float t)
                    && t < best) best = t;
            if (best == float.MaxValue)
            {
                if (debug) Debug.Log($"[KitSurface] ray missed '{piece.Prefab.name}' at cell {cell} face {face} — left on the greybox. A very oblique shot can exit this piece sideways into the neighbouring face, which is not tested.");
                return false;
            }

            // `best` is in the part's local parameterisation of the same ray, and the local
            // direction was NOT normalised, so it is directly comparable in world units.
            Vector3 candidate = rayOrigin + dir * best;

            // CLAMP THE DEPTH BEHIND THE COLLIDER PLANE, NOT THE DISTANCE TRAVELLED — and the
            // difference is a real bug, not a refinement. A euclidean clamp conflates depth
            // with lateral travel, so an OBLIQUE shot covers the same depth over a longer path
            // (0.6m of recess needs 0.85m of travel at 45 degrees) and gets discarded while a
            // square-on shot into the identical niche passes. Field-reported exactly that way:
            // straight-in arrows landed correctly, angled ones stopped on the greybox.
            //
            // Depth is angle-independent and is the quantity that actually matters — "how far
            // behind the collider did the visible surface turn out to be". Lateral travel needs
            // no clamp of its own: it is bounded by the extent of the one piece being tested,
            // and for a legitimate oblique shot it is exactly the offset the arrow should have.
            //
            // Symmetric, because masonry relief can protrude IN FRONT of the inset greybox.
            Vector3 inward = -normal.normalized;
            float depth = Vector3.Dot(candidate - point, inward);
            if (Mathf.Abs(depth) > maxDistance)
            {
                if (debug) Debug.Log($"[KitSurface] hit '{piece.Prefab.name}' {depth:0.00}m behind the collider, past the {maxDistance:0.00}m limit — left on the greybox. Raise Max Surface Refine if that depth is legitimate.");
                return false;
            }

            if (debug) Debug.Log($"[KitSurface] refined {depth:0.00}m into '{piece.Prefab.name}' at cell {cell} face {face}.");
            refined = candidate;
            return true;
        }

        // ---- Mesh testing -----------------------------------------------------------------

        static Part[] PartsOf(GameObject prefab)
        {
            if (partCache.TryGetValue(prefab, out Part[] cached)) return cached;

            var list = new List<Part>();
            Transform root = prefab.transform;
            // Same root correction AddInstance's BuildProto applies, so a part's local matrix
            // means the same thing in both places.
            Matrix4x4 rootCorrection =
                Matrix4x4.TRS(Vector3.zero, root.rotation, root.lossyScale) * root.worldToLocalMatrix;

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                // MeshRay owns the readability check, the warning and the vertex cache — shared
                // with the door pass-through so the same mesh is never read twice.
                if (!MeshRay.TryGet(mf.sharedMesh, out MeshRay.Data data)) continue;
                list.Add(new Part
                {
                    Mesh = data,
                    Local = rootCorrection * mf.transform.localToWorldMatrix,
                });
            }

            Part[] parts = list.ToArray();
            partCache[prefab] = parts;
            return parts;
        }


        // ---- Keys and conversions ----------------------------------------------------------

        // Mirrors the world-to-cell conversion `AudioSpace.CellOf` and `DungeonVisibility.CellOf`
        // use, per §10's rule that there is ONE conversion — a second one drifts at storey
        // boundaries, which is golden rule 5's float-Y trap.
        static Vector3Int CellOf(Vector3 world)
        {
            Vector3 local = world - origin;
            return new Vector3Int(
                Mathf.FloorToInt(local.x / cellSize),
                Mathf.FloorToInt(local.y / cellSize),
                Mathf.FloorToInt(local.z / cellSize));
        }

        static Vector3Int RoundDir(Vector3 v) =>
            Mathf.Abs(v.x) >= Mathf.Abs(v.z)
                ? new Vector3Int(v.x >= 0f ? 1 : -1, 0, 0)
                : new Vector3Int(0, 0, v.z >= 0f ? 1 : -1);

        static int DirIndex(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;

        static long Key(Vector3Int cell, Vector3Int dir) =>
            (((long)(cell.x + 1024) & 0xFFFF) << 34) |
            (((long)(cell.y + 1024) & 0xFFFF) << 18) |
            (((long)(cell.z + 1024) & 0xFFFF) << 2) |
            (uint)DirIndex(dir);
    }
}
