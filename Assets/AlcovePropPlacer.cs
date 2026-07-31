using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Fills hallway alcoves with their per-KIND contents (RoomStyle.alcoveStyles). Alcove cells
    /// are ordinary <see cref="CellType.Hallway"/> in the grid, so their walls, floor and ceiling
    /// already came from the corridor path — this pass supplies only what makes a recess a
    /// statue nook rather than a dead end.
    ///
    /// WHY ITS OWN PASS rather than an extension of HallwayPropPlacer or RoomPropPlacer:
    /// - RoomPropPlacer hard-gates on `grid[c] == CellType.Room` and needs zones, an entrance
    ///   axis and a centroid. An alcove has none of those.
    /// - HallwayPropPlacer scatters ONE global set across every corridor cell. An alcove needs
    ///   per-kind content and a hero prop, and its cells are deliberately excluded from that
    ///   pass so generic debris can't bury the thing the alcove exists to show.
    ///
    /// It reuses everything else unchanged: PropSet/PropEntry authoring, PropAnchor, PropTier,
    /// PropInstancer, PropSnap, and the WallFaceRegistry claim system.
    ///
    /// NO FLOOD FILL, deliberately. An alcove is a dead-end pocket hanging off one corridor
    /// cell, so a blocking prop inside it cannot sever anything — the corridor network doesn't
    /// route through it. Do NOT add alcove cells to the hallway pass's BFS "for safety": that
    /// would let a blocking prop veto itself for no reason.
    ///
    /// RUNS BEFORE TorchPlacer (see DungeonVisualizer). An alcove has about three wall faces and
    /// one authored hero prop, making it the most constrained consumer of wall real estate in
    /// the dungeon — §8's most-constrained-first rule — and it must claim its feature face
    /// before torches get to pick.
    /// </summary>
    public static class AlcovePropPlacer
    {
        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        static int DirIdx(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;

        // Own salt block, never a shared counter (golden rule 4): tuning one pass must not
        // shift another's placements. 12002/12003/12005 are the hallway pass; 110xx are rooms.
        const int FeatureSalt = 12101;
        const int WallSalt = 12102;
        const int ScatterSalt = 12103;
        const int CeilingSalt = 12104;

        public static GameObject Build(DungeonGenerator gen, RoomStyle style, float cellSize, Transform parent,
                                       InstancedDungeonRenderer instancer, WallFaceRegistry wallFaces = null)
        {
            var root = new GameObject("DungeonAlcoveProps");
            root.transform.SetParent(parent, false);
            if (style == null || gen.Alcoves.Count == 0) return root;

            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            var featureStream = new HashStream(Vector3Int.zero, FeatureSalt);
            var wallStream = new HashStream(Vector3Int.zero, WallSalt);
            var scatterStream = new HashStream(Vector3Int.zero, ScatterSalt);
            var ceilingStream = new HashStream(Vector3Int.zero, CeilingSalt);

            GameObject Pick(PropSet.PropEntry e, HashStream s) =>
                (e.prefabs == null || e.prefabs.Length == 0) ? null : e.prefabs[s.Next() % e.prefabs.Length];

            void Place(GameObject prefab, Vector3 pos, Quaternion rot, PropTier tier)
            {
                PropTier t = instancer != null ? tier : PropTier.FullGameObject;
                PropInstancer.PlaceProps(instancer, prefab,
                    new[] { new PropPlacement { position = pos, rotation = rot } },
                    t, cellSize, root.transform);
            }

            Vector3 CellCentre(Vector3Int c) =>
                new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize) + parent.position;

            int total = 0;

            // Alcoves in a stable order so the streams are reproducible.
            var alcoves = new List<AlcoveSpec>(gen.Alcoves);
            alcoves.Sort((a, b) =>
            {
                var x = a.MouthCell; var y = b.MouthCell;
                return x.x != y.x ? x.x.CompareTo(y.x) : x.z != y.z ? x.z.CompareTo(y.z) : x.y.CompareTo(y.y);
            });

            foreach (var alcove in alcoves)
            {
                PropSet set = style.AlcoveProps(alcove.Kind);
                if (set == null || set.entries == null || set.entries.Count == 0) continue;

                // Cells of THIS alcove, deepest first — so a Feature lands at the back and
                // scatter fills forward toward the mouth.
                var cells = new List<Vector3Int>(alcove.Cells);
                cells.Sort((a, b) =>
                {
                    int da = Depth(a, alcove), db = Depth(b, alcove);
                    if (da != db) return db.CompareTo(da);
                    return a.x != b.x ? a.x.CompareTo(b.x) : a.z != b.z ? a.z.CompareTo(b.z) : a.y.CompareTo(b.y);
                });
                if (cells.Count == 0) continue;

                var usedFloor = new HashSet<Vector3Int>();
                var usedCeiling = new HashSet<Vector3Int>();

                foreach (var e in set.entries)
                {
                    if (e.prefabs == null || e.prefabs.Length == 0) continue;

                    // BLOCKING TIERS ARE ALLOWED AT ANY DEPTH, including a 1x1 recess. There was
                    // a guard here refusing them in shallow alcoves on the grounds that "there is
                    // nowhere to stand" — wrong, and it contradicted the no-flood-fill note
                    // above: an alcove is a DEAD END, nothing routes through it, so a collider
                    // inside one cannot sever or narrow anything. A recessed statue you cannot
                    // walk into is the whole point of a 1x1 nook, and forcing it to StaticDecor
                    // made the player walk through the statue.
                    //
                    // The only real risk is a prop whose collider spills OUT into the corridor,
                    // and that is an authoring concern (prop size and wallGap), not something a
                    // tier check can catch.

                    switch (e.anchor)
                    {
                        case PropAnchor.Feature:
                            total += PlaceFeature(e, alcove, cells, cellSize, CellCentre, Pick, Place,
                                                  featureStream, usedFloor, grid, wallFaces);
                            break;

                        case PropAnchor.WallMounted:
                            total += PlaceWallMounted(e, alcove, cellSize, CellCentre, Pick, Place,
                                                      wallStream, grid, wallFaces, Open);
                            break;

                        case PropAnchor.CeilingHung:
                            total += PlaceScatterLike(e, alcove, cells, cellSize, CellCentre, Pick, Place,
                                                      ceilingStream, usedCeiling, grid, wallFaces, Open,
                                                      ceiling: true);
                            break;

                        default: // FloorScatter, and anything room-only degrades to scatter
                            total += PlaceScatterLike(e, alcove, cells, cellSize, CellCentre, Pick, Place,
                                                      scatterStream, usedFloor, grid, wallFaces, Open,
                                                      ceiling: false);
                            break;
                    }
                }
            }

            if (total > 0) Debug.Log($"[Alcoves] {alcoves.Count} alcove(s), {total} prop(s) placed.");
            return root;
        }

        /// <summary>How many cells INTO the alcove this cell sits (mouth = 0).</summary>
        static int Depth(Vector3Int c, AlcoveSpec a)
        {
            Vector3Int rel = c - a.MouthCell;
            return Mathf.Abs(rel.x * a.Direction.x + rel.z * a.Direction.z);
        }

        /// <summary>
        /// The hero prop. This is why AlcoveSpec carries Direction: it supplies the same
        /// back/left/right frame a room's entrance axis does, so the existing Feature authoring
        /// (WallSide.Back, FeatureFacing.Outward) means exactly what an author expects — Back is
        /// the far wall, Outward looks out at the corridor.
        /// </summary>
        static int PlaceFeature(PropSet.PropEntry e, AlcoveSpec a, List<Vector3Int> cells, float cellSize,
                                System.Func<Vector3Int, Vector3> CellCentre,
                                System.Func<PropSet.PropEntry, HashStream, GameObject> Pick,
                                System.Action<GameObject, Vector3, Quaternion, PropTier> Place,
                                HashStream s, HashSet<Vector3Int> usedFloor,
                                Grid3D<CellType> grid, WallFaceRegistry wallFaces)
        {
            // Deepest free cell — cells is already sorted deepest-first.
            Vector3Int? target = null;
            foreach (var c in cells)
                if (!usedFloor.Contains(c)) { target = c; break; }
            if (!target.HasValue) return 0;

            GameObject prefab = Pick(e, s);
            if (prefab == null) return 0;

            Vector3Int cell = target.Value;

            // Outward = look back down the corridor you came from; Inward = face the dead end.
            Vector3 face = e.featureFacing == FeatureFacing.Inward ? (Vector3)a.Direction : -(Vector3)a.Direction;
            Quaternion rot = Quaternion.LookRotation(face.normalized) * Quaternion.Euler(0f, e.featureYaw, 0f);

            Vector3 pos = CellCentre(cell);
            if (e.snapToWall)
            {
                // Push back against the far wall if there is one behind this cell.
                Vector3Int behind = cell + a.Direction;
                if (!(grid.InBounds(behind) && grid[behind] != CellType.Empty))
                    pos += (Vector3)a.Direction * (cellSize * 0.5f - e.wallGap);
            }

            Place(prefab, pos, rot, e.tier);
            if (!e.sharesTile) usedFloor.Add(cell);

            // Claim the back face so a torch can't land on top of the idol.
            Vector3Int back = cell + a.Direction;
            if (wallFaces != null && !(grid.InBounds(back) && grid[back] != CellType.Empty))
                wallFaces.Claim(grid.Index(cell), a.Direction);

            return 1;
        }

        static int PlaceWallMounted(PropSet.PropEntry e, AlcoveSpec a, float cellSize,
                                    System.Func<Vector3Int, Vector3> CellCentre,
                                    System.Func<PropSet.PropEntry, HashStream, GameObject> Pick,
                                    System.Action<GameObject, Vector3, Quaternion, PropTier> Place,
                                    HashStream s, Grid3D<CellType> grid, WallFaceRegistry wallFaces,
                                    System.Func<Vector3Int, bool> Open)
        {
            var faces = new List<(Vector3Int c, Vector3Int d)>();
            foreach (var c in a.Cells)
                foreach (var d in HDirs)
                {
                    if (Open(c + d)) continue;   // not a wall
                    if (wallFaces != null && (!wallFaces.PropsAllowed(grid.Index(c), d) ||
                                              wallFaces.IsClaimed(grid.Index(c), d))) continue;
                    faces.Add((c, d));
                }
            if (faces.Count == 0) return 0;

            int salt = s.Next();
            faces.Sort((x, y) => DungeonKitPlacer.Hash(x.c, salt + DirIdx(x.d))
                                 .CompareTo(DungeonKitPlacer.Hash(y.c, salt + DirIdx(y.d))));

            int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
            foreach (var (c, d) in faces)
            {
                if (placed >= want) break;
                if (!e.guaranteed)
                {
                    if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                    if (s.Next01() >= e.chancePerCell) continue;
                }
                GameObject prefab = Pick(e, s);
                if (prefab == null) continue;

                float h = e.mountHeight + (e.mountHeightJitter > 0f ? (s.Next01() - 0.5f) * 2f * e.mountHeightJitter : 0f);
                Vector3 tan = new Vector3(-d.z, 0f, d.x);
                float latRange = e.subCellJitter * (cellSize * 0.5f - 0.3f);
                Vector3 pos = CellCentre(c)
                              + (Vector3)d * (cellSize * 0.5f - e.wallGap)
                              + tan * ((s.Next01() - 0.5f) * 2f * latRange)
                              + Vector3.up * h;
                Quaternion rot = Quaternion.LookRotation(-(Vector3)d)
                                 * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
                Place(prefab, pos, rot, e.tier);
                wallFaces?.Claim(grid.Index(c), d);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Floor scatter and ceiling props share almost all their logic here; only the height
        /// and the used-set differ. An alcove is nearly all inside corners, so snapToInsideCorner
        /// works especially well and comes free from PropSnap.
        /// </summary>
        static int PlaceScatterLike(PropSet.PropEntry e, AlcoveSpec a, List<Vector3Int> cells, float cellSize,
                                    System.Func<Vector3Int, Vector3> CellCentre,
                                    System.Func<PropSet.PropEntry, HashStream, GameObject> Pick,
                                    System.Action<GameObject, Vector3, Quaternion, PropTier> Place,
                                    HashStream s, HashSet<Vector3Int> used,
                                    Grid3D<CellType> grid, WallFaceRegistry wallFaces,
                                    System.Func<Vector3Int, bool> Open, bool ceiling)
        {
            int salt = s.Next();
            var order = new List<Vector3Int>();
            foreach (var c in cells)
                if (e.sharesTile || !used.Contains(c)) order.Add(c);
            order.Sort((x, y) => DungeonKitPlacer.Hash(x, salt).CompareTo(DungeonKitPlacer.Hash(y, salt)));

            bool insideCorner = e.snapToInsideCorner;
            bool wantsWall = !insideCorner && (e.snapToWall ||
                             e.facing == FacingRule.FaceAwayFromNearestWall ||
                             e.facing == FacingRule.AlignWithWall);

            int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
            foreach (var c in order)
            {
                if (placed >= want) break;

                Vector3Int ca = default, cb = default;
                if (insideCorner && !PropSnap.TryInsideCorner(grid, c, null, false, s.Next(), out ca, out cb))
                    continue;
                if (!e.guaranteed)
                {
                    if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                    if (s.Next01() >= e.chancePerCell) continue;
                }

                Vector3Int? wallDir = null;
                if (wantsWall)
                {
                    var solids = new List<Vector3Int>();
                    foreach (var d in HDirs)
                    {
                        if (Open(c + d)) continue;
                        if (e.snapToWall && wallFaces != null && !wallFaces.PropsAllowed(grid.Index(c), d)) continue;
                        solids.Add(d);
                    }
                    if (solids.Count > 0) wallDir = solids[solids.Count == 1 ? 0 : s.Next() % solids.Count];
                    if (e.snapToWall && !wallDir.HasValue) continue;
                }

                // CellCentre sits at the cell's FLOOR (parent offset included). A ceiling prop
                // hangs one cell higher — add the delta rather than rebuilding the vector, or
                // the parent offset gets dropped. Alcoves are single-story, like corridors.
                Vector3 baseCentre = CellCentre(c);
                if (ceiling) baseCentre.y += cellSize;

                float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                Vector3 pos;
                Quaternion rot;
                if (insideCorner)
                {
                    pos = baseCentre + PropSnap.CornerOffset(ca, cb, cellSize, e.wallGap);
                    rot = Quaternion.LookRotation(PropSnap.CornerFacing(ca, cb).normalized)
                          * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
                }
                else if (e.snapToWall && wallDir.HasValue)
                {
                    Vector3 tan = new Vector3(-wallDir.Value.z, 0f, wallDir.Value.x);
                    pos = baseCentre + (Vector3)wallDir.Value * (cellSize * 0.5f - e.wallGap)
                          + tan * ((s.Next01() - 0.5f) * 2f * range);
                    rot = Yaw(e, s, wallDir, a);
                }
                else
                {
                    pos = baseCentre + new Vector3((s.Next01() - 0.5f) * 2f * range, 0f,
                                                   (s.Next01() - 0.5f) * 2f * range);
                    rot = Yaw(e, s, wallDir, a);
                }

                GameObject prefab = Pick(e, s);
                if (prefab == null) continue;
                Place(prefab, pos, rot, e.tier);
                if (!e.sharesTile) used.Add(c);
                placed++;
            }
            return placed;
        }

        /// <summary>
        /// Scatter yaw. FaceEntrance is meaningful here where it isn't in a corridor — an
        /// alcove HAS an entrance, so it maps to looking back out at the hallway.
        /// </summary>
        static Quaternion Yaw(PropSet.PropEntry e, HashStream s, Vector3Int? wallDir, AlcoveSpec a)
        {
            Vector3 dir = Vector3.zero;
            if (e.facing == FacingRule.FaceAwayFromNearestWall && wallDir.HasValue)
                dir = -(Vector3)wallDir.Value;
            else if (e.facing == FacingRule.AlignWithWall && wallDir.HasValue)
            {
                dir = Vector3.Cross(Vector3.up, -(Vector3)wallDir.Value);
                if (s.Next() % 2 == 1) dir = -dir;
            }
            else if (e.facing == FacingRule.FaceEntrance)
                dir = -(Vector3)a.Direction;

            Quaternion baseRot = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity;
            return baseRot * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
        }
    }
}
