using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Sub-cell arbitration for DÉCOR, so several small props can share one 3m tile without
    /// intersecting each other.
    ///
    /// THE 3m GRID IS NOT THE CONSTRAINT AND DOES NOT CHANGE. Golden rule 1 makes world
    /// position literally `cell * cellSize`, and the mesher, every kit piece authored to a 3m
    /// face, `HallwayPathfinder`'s sealed stair envelopes, `RecessFits`, `NeedsSlabBetween`,
    /// `DungeonMapper`, `AudioSpace`, fog and the 1.5m crawlway bore are all written in cells.
    /// What actually blocked several props per tile was the OCCUPANCY MODEL — a HashSet of
    /// claimed cells living inside the three prop placers — and that is what this replaces,
    /// for décor only.
    ///
    /// A 1m SUB-LATTICE WAS CONSIDERED AND REJECTED: `subCellJitter` defaults to 0.9
    /// specifically to hide the 3m lattice, and quantising small props to 1m slots
    /// reintroduces exactly that artifact one scale down — worst on scattered debris, which is
    /// the case this exists for. Positions stay continuous; only OVERLAP is arbitrated.
    ///
    /// NOT `PropSocket`, which already gives exact authored poses (a fireplace's fire in its
    /// hearth). The gap this fills is "several loose items scattered naturally in one tile
    /// without colliding", not "place this precisely".
    ///
    /// DÉCOR ONLY, AND THAT BOUNDARY IS WHAT MAKES IT SAFE (see <see cref="Engaged"/>).
    /// `PropTier.StaticDecor` is the only non-blocking tier, so blocking props keep CELL-level
    /// occupancy untouched: the threshold flood-fill, the corridor connectivity BFS,
    /// `PropSnap.NearStair` and the navmesh never learn this exists and cannot be severed by
    /// it. Extending packing to collider tiers would put prop placement inside the connectivity
    /// guarantee, which is the one thing the prop system is not allowed to weaken — it is
    /// refused rather than deferred.
    ///
    /// DUNGEON-SCOPED, NOT ROOM-SCOPED, DELIBERATELY. The passes run recess → room → hallway,
    /// so a corridor prop can land in the cell next to a room prop. A per-room instance would
    /// let those two overlap across the boundary — the seam being the one place it would be
    /// noticed.
    /// </summary>
    public class DecorPacking
    {
        /// <summary>
        /// One occupancy plane. FLOOR and CEILING are separate for the same reason `usedCells`
        /// and `usedCeilingCells` are: a floor rack and a hanging lantern share a cell
        /// legitimately, and arbitrating them against each other would be wrong.
        /// </summary>
        public class Layer
        {
            readonly struct Item
            {
                public readonly Vector2 XZ;
                public readonly float Radius;
                public Item(Vector2 xz, float radius) { XZ = xz; Radius = radius; }
            }

            // Keyed by Vector3Int, so different STOREYS never interact and a tall room's two
            // levels stay independent with no extra bookkeeping.
            readonly Dictionary<Vector3Int, List<Item>> byCell = new Dictionary<Vector3Int, List<Item>>();

            /// <summary>
            /// True if a disc of `radius` at world `xz` clears everything already registered in
            /// this cell and its 8 XZ neighbours. Nine short lists; at these counts the cost is
            /// noise, which is why no coarser index is built.
            /// </summary>
            public bool Fits(Vector3Int cell, Vector2 xz, float radius)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var key = new Vector3Int(cell.x + dx, cell.y, cell.z + dz);
                        if (!byCell.TryGetValue(key, out var list)) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            float sum = radius + list[i].Radius;
                            if ((list[i].XZ - xz).sqrMagnitude < sum * sum) return false;
                        }
                    }
                return true;
            }

            /// <summary>
            /// Registers a placement. Draws NOTHING from any stream, which is what lets every
            /// décor placement register — packed or not — without shifting a single existing
            /// seed.
            /// </summary>
            public void Add(Vector3Int cell, Vector2 xz, float radius)
            {
                if (!byCell.TryGetValue(cell, out var list))
                {
                    list = new List<Item>(2);
                    byCell[cell] = list;
                }
                list.Add(new Item(xz, radius));
            }

            /// <summary>Convenience for the call sites, which all hold a world position.</summary>
            public void Add(Vector3Int cell, Vector3 world, float radius) =>
                Add(cell, new Vector2(world.x, world.z), radius);
        }

        public readonly Layer Floor = new Layer();
        public readonly Layer Ceiling = new Layer();

        /// <summary>
        /// EVERY décor placement on a plane registers, but only a PACKED entry consults.
        ///
        /// That asymmetry is deliberate and it is what stops packed rubble landing inside an
        /// ordinary crate: a non-`sharesTile` prop reserves its TILE, which a `sharesTile`
        /// entry ignores by definition, so without registration the packed entry would have no
        /// idea the crate was there. Registration costs no stream draws, so a dungeon with no
        /// packed entries places bit-identically to one built before this existed.
        /// </summary>
        public Layer For(PropAnchor anchor) => anchor == PropAnchor.CeilingHung ? Ceiling : Floor;

        // ---- Engagement ----------------------------------------------------------------

        /// <summary>
        /// `sharesTile` IS THE GATE. It already means "this entry does not reserve its tile and
        /// may sit on a used one"; it now additionally means "subject to clearance". That is the
        /// one deliberate behaviour change here — existing `sharesTile` entries gain overlap
        /// protection they did not have, so their POSITIONS shift on a fixed seed while their
        /// counts stay similar. Everything else is inert.
        ///
        /// Restricted to StaticDecor (see the class note) and to the two scatter anchors, which
        /// are the only ones that place a free position inside a cell — a WallMounted prop is
        /// arbitrated by the face registry and a Feature has exactly one authored spot.
        /// </summary>
        public static bool Engaged(PropSet.PropEntry e) =>
            e != null && e.sharesTile && e.tier == PropTier.StaticDecor &&
            (e.anchor == PropAnchor.FloorScatter || e.anchor == PropAnchor.CeilingHung);

        // A `sharesTile` collider-tier entry asking for several items per cell is an authoring
        // mistake with no visible symptom — it simply keeps today's behaviour — so it is named
        // rather than left to be discovered. Per-instance, so it resets with each dungeon.
        readonly HashSet<PropSet.PropEntry> warned = new HashSet<PropSet.PropEntry>();

        public void WarnIfRefused(PropSet.PropEntry e)
        {
            if (e == null || !e.sharesTile || Engaged(e)) return;
            if (e.itemsPerCell.y <= 1) return;          // not actually asking for packing
            if (!warned.Add(e)) return;
            string why = e.tier != PropTier.StaticDecor
                ? $"tier is {e.tier}, and packing is StaticDecor-only — a collider tier stays on cell occupancy so it can never sever the dungeon"
                : $"anchor is {e.anchor}, and packing applies to FloorScatter/CeilingHung only";
            Debug.LogWarning($"[DecorPacking] '{e.label}' asks for {e.itemsPerCell.x}-{e.itemsPerCell.y} items per cell but {why}. Placing one, as before.");
        }

        // ---- Per-entry values ----------------------------------------------------------

        /// <summary>
        /// ONE DRAW, ALWAYS, and never conditional on what is already placed. Rolling lazily —
        /// only when an entry asks for a range — would make the draw count depend on AUTHORING,
        /// so widening one entry's range would reshuffle every placement after it (golden rule
        /// 4, the lesson the recess variant roll already carries).
        /// </summary>
        public static int RollCount(PropSet.PropEntry e, HashStream s)
        {
            int lo = Mathf.Max(1, e.itemsPerCell.x);
            int hi = Mathf.Max(lo, e.itemsPerCell.y);
            float t = s.Next01();                        // drawn even when lo == hi
            return lo + Mathf.Min(hi - lo, Mathf.FloorToInt(t * (hi - lo + 1)));
        }

        /// <summary>
        /// The clearance disc for one placement: the authored override, or 0 = derive from the
        /// prefab.
        /// </summary>
        public static float RadiusFor(PropSet.PropEntry e, GameObject prefab) =>
            e != null && e.clearanceRadius > 0f ? e.clearanceRadius : DerivedRadius(prefab);

        // The answer never changes for a given prefab, and these are ASSETS, so the cache
        // outlives a regenerate on purpose.
        static readonly Dictionary<GameObject, float> radiusCache = new Dictionary<GameObject, float>();

        /// <summary>
        /// Footprint radius about the prefab's PIVOT, in metres.
        ///
        /// READS `MeshFilter.sharedMesh.bounds`, WHICH WORKS ON A PREFAB ASSET — `Renderer.bounds`
        /// does not, since it needs an instance. That distinction is the whole reason this is
        /// cheap: nothing is instantiated to measure it.
        ///
        /// ABOUT THE PIVOT, NOT ABOUT `bounds.center`, and the difference is real here — §5's
        /// standing lesson, learned when stairs and ladders were culled by a radius measured
        /// from the wrong point. A prop is placed AT its pivot and yawed about it, so the swept
        /// footprint is the disc reaching the furthest XZ corner of the bounds, and an
        /// off-centre mesh puts that much further out than its extents alone suggest.
        ///
        /// The prefab ROOT's own scale and rotation are included, because `PropInstancer`
        /// composes those internally on the mesh path (§5's double-rotation invariant) — so
        /// what lands in the world is the root's full visual pose, not the raw mesh.
        ///
        /// Conservative by construction: circumscribing a long thin prop overstates its
        /// footprint at most yaws. That errs toward FEWER props rather than toward overlap,
        /// which is the right direction for décor, and `clearanceRadius` is the override for
        /// the cases where it reads wrong.
        /// </summary>
        public static float DerivedRadius(GameObject prefab)
        {
            if (prefab == null) return 0f;
            if (radiusCache.TryGetValue(prefab, out float cached)) return cached;

            float best = 0f;
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
            var root = prefab.transform;
            foreach (var mf in filters)
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                // Child mesh space -> the prefab root's PARENT space, so the root's own
                // localScale and localRotation are part of the answer.
                Matrix4x4 m = Matrix4x4.TRS(root.localPosition, root.localRotation, root.localScale)
                              * root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                Vector3 c = mesh.bounds.center, ex = mesh.bounds.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3(
                        (i & 1) == 0 ? -ex.x : ex.x,
                        (i & 2) == 0 ? -ex.y : ex.y,
                        (i & 4) == 0 ? -ex.z : ex.z);
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    float d = new Vector2(p.x, p.z).magnitude;
                    if (d > best) best = d;
                }
            }
            radiusCache[prefab] = best;
            return best;
        }

        // ---- The attempt loop ----------------------------------------------------------

        /// <summary>
        /// Throws `attempts` darts and keeps the FIRST that fits.
        ///
        /// IT ALWAYS CONSUMES ITS FULL DRAW COUNT — that is the whole reason this lives in one
        /// method instead of being written out at five call sites. Breaking early on success
        /// would make the number of stream draws depend on what was already placed, which
        /// shifts every later placement in the dungeon (golden rule 4). `candidate` is expected
        /// to draw from a HashStream, so it is invoked exactly `attempts` times whatever
        /// happens, and it is the FIT TEST that is skipped, never the draw.
        ///
        /// Failure is SILENT PER ITEM and places nothing — it is décor, and fewer pieces in a
        /// crowded tile is the correct degradation. It is NOT silent in aggregate: see
        /// <see cref="LogSummary"/>. An entry authored with a clearance radius far too large
        /// for the space would otherwise place almost nothing and look like an authoring
        /// value that "does nothing", which is the failure shape this project keeps paying for.
        /// </summary>
        public bool TryFit(Layer layer, Vector3Int cell, float radius, int attempts,
                           System.Func<Vector3> candidate, out Vector3 chosen)
        {
            chosen = default;
            bool found = false;
            int n = Mathf.Max(1, attempts);
            for (int i = 0; i < n; i++)
            {
                Vector3 p = candidate();                 // ALWAYS drawn — see above
                if (found) continue;
                if (!layer.Fits(cell, new Vector2(p.x, p.z), radius)) continue;
                chosen = p;
                found = true;
            }
            wanted++;
            if (found) fitted++;
            return found;
        }

        int wanted, fitted;

        /// <summary>
        /// One line at the end of generation, only when packing actually ran. Starvation is a
        /// legitimate outcome, so this is a LOG rather than a warning — but a run that wanted
        /// 400 items and placed 40 is a radius or an `itemsPerCell` that wants retuning, and
        /// nothing else in the game would ever say so.
        /// </summary>
        public void LogSummary()
        {
            if (wanted == 0) return;
            Debug.Log($"[DecorPacking] {fitted}/{wanted} packed décor items placed " +
                      $"({wanted - fitted} found no room; raise Packing Attempts or lower Clearance Radius if that share looks high).");
        }
    }
}
