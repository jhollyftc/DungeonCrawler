using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A named draw sequence from the room's positional hash. Each pass
    /// (feature/scatter/ceiling/...) owns its own stream so tuning one pass's
    /// density or jitter never shifts another pass's draws.
    /// </summary>
    public class HashStream
    {
        readonly Vector3Int pos;
        readonly int seed;
        int n;

        public HashStream(Vector3Int pos, int seed)
        {
            this.pos = pos;
            this.seed = seed;
        }

        public int Next() => DungeonKitPlacer.Hash(pos, seed + n++);
        public float Next01() => (Next() % 10000) / 10000f;
    }

    /// <summary>
    /// Places per-room-type props (from RoomStyle's PropSets) with an occupancy
    /// system that guarantees props never break the dungeon:
    ///   - Threshold cells (any floor cell at a doorway/arch opening) are
    ///     reserved — nothing places there.
    ///   - Blocking props (collider tiers) claim cells; after each blocking
    ///     placement a flood-fill confirms every threshold still reaches every
    ///     other. If not, that placement is rolled back and skipped.
    ///   - Décor never blocks; everything gets sub-cell jitter + yaw so the
    ///     3m grid disappears visually.
    /// Entry order per room: features first (they claim prime cells), then
    /// guaranteed counts, then chance scatter. Fully deterministic per layout.
    /// Meshes batch through PropInstancer; with no instancer (PrefabKit mode)
    /// everything spawns as full GameObjects so nothing is invisible.
    /// </summary>
    public static class RoomPropPlacer
    {
        public static GameObject Build(DungeonGenerator gen, DungeonKit kit, RoomStyle style,
                                       float cellSize, Transform parent,
                                       InstancedDungeonRenderer instancer)
        {
            var root = new GameObject("DungeonProps");
            root.transform.SetParent(parent, false);
            if (style == null) return root;

            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;
            var hDirs = new[]
            {
                new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
                new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
            };

            // Room-side threshold cells from door records (covers satellite
            // closets, whose openings are Room↔Room and invisible to the
            // hallway-adjacency test below).
            var doorRoomCells = new HashSet<Vector3Int>();
            foreach (var door in gen.Doors)
            {
                doorRoomCells.Add(door.HallwayCell + door.Direction);
                doorRoomCells.Add(door.HallwayCell); // host side of satellite doors
            }

            int totalPlaced = 0;

            for (int ri = 0; ri < gen.Rooms.Count; ri++)
            {
                Room room = gen.Rooms[ri];
                PropSet set = style.PropsFor(room.Type);
                if (set == null || set.entries == null || set.entries.Count == 0) continue;

                int yFloor = room.Bounds.yMin;

                // Independent per-pass RNG streams (deterministic, room-local).
                // Changing one pass's tuning must never shift another's draws.
                var featureStream = new HashStream(room.Bounds.min, 11001);
                var scatterStream = new HashStream(room.Bounds.min, 11002);
                var ceilingStream = new HashStream(room.Bounds.min, 11003);

                // ---- Floor cells, in a stable order ----
                var floor = new List<Vector3Int>();
                foreach (var c in room.Cells)
                    if (c.y == yFloor) floor.Add(c);
                floor.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
                if (floor.Count == 0) continue;

                // ---- Thresholds: reserved, and the pathability anchors ----
                var reserved = new HashSet<Vector3Int>();
                foreach (var c in floor)
                {
                    if (doorRoomCells.Contains(c)) { reserved.Add(c); continue; }
                    foreach (var d in hDirs)
                    {
                        Vector3Int nb = c + d;
                        if (grid.InBounds(nb) && grid[nb] == CellType.Hallway)
                        {
                            reserved.Add(c);
                            break;
                        }
                    }
                }

                // Primary entrance (for feature facing): first threshold in
                // stable order, else the room's interior cell.
                Vector3Int entrance = room.InteriorFloorCell;
                foreach (var c in floor)
                    if (reserved.Contains(c)) { entrance = c; break; }

                // Direction from the entrance INTO the room. Feature walls
                // (Back/Left/Right/Front) are named relative to this — "which
                // way you'd face walking in the door" — not world-cardinal
                // directions, since a room's orientation on the grid varies.
                Vector3Int enterDir = new Vector3Int(0, 0, 1); // degenerate fallback (no real threshold)
                foreach (var d in hDirs)
                {
                    Vector3Int nb = entrance + d;
                    if (Open(nb) && !room.Cells.Contains(nb)) { enterDir = -d; break; }
                }

                bool WallAdjacent(Vector3Int c)
                {
                    foreach (var d in hDirs)
                        if (!Open(c + d)) return true;
                    return false;
                }

                var usedCells = new HashSet<Vector3Int>();    // one prop per cell
                var blocked = new HashSet<Vector3Int>();      // collider-tier cells

                // Pathability: all thresholds mutually reachable across
                // unblocked floor cells. Rooms are small; BFS is trivial.
                bool ThresholdsConnected()
                {
                    if (reserved.Count <= 1) return true;
                    var open = new HashSet<Vector3Int>(floor);
                    open.ExceptWith(blocked);
                    Vector3Int startCell = default; bool found = false;
                    foreach (var c in reserved) { startCell = c; found = true; break; }
                    if (!found) return true;
                    var seen = new HashSet<Vector3Int> { startCell };
                    var q = new Queue<Vector3Int>();
                    q.Enqueue(startCell);
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        foreach (var d in hDirs)
                        {
                            var n = c + d;
                            if (!open.Contains(n) || seen.Contains(n)) continue;
                            seen.Add(n);
                            q.Enqueue(n);
                        }
                    }
                    foreach (var c in reserved)
                        if (!seen.Contains(c)) return false;
                    return true;
                }

                // ---- Placement helpers ----
                GameObject PickPrefab(PropSet.PropEntry e, HashStream s) =>
                    (e.prefabs == null || e.prefabs.Length == 0) ? null
                    : e.prefabs[s.Next() % e.prefabs.Length];

                bool Blocking(PropTier t) => t != PropTier.StaticDecor;

                void Spawn(PropSet.PropEntry e, GameObject prefab, Vector3Int cell,
                           Vector3 worldPos, Quaternion rot)
                {
                    // No instancer (PrefabKit mode): full GameObjects so the
                    // mesh path never silently vanishes.
                    PropTier tier = instancer != null ? e.tier : PropTier.FullGameObject;
                    PropInstancer.PlaceProps(instancer, prefab,
                        new[] { new PropPlacement { position = worldPos, rotation = rot } },
                        tier, cellSize, root.transform);
                    usedCells.Add(cell);
                    totalPlaced++;
                }

                // NOTE: props do NOT get kit.globalVisualOffset — that offset
                // compensates the KIT assets' origin-above-base authoring; prop
                // prefabs with clean base origins sit at the nominal floor,
                // which is where the (offset-corrected) visual floor renders.
                Vector3 FloorWorld(Vector3Int cell, PropSet.PropEntry e, HashStream s)
                {
                    // Sub-cell jitter within a safe margin (keeps clear of
                    // walls, corner posts, and interior columns at lattice
                    // corners).
                    float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                    float jx = (s.Next01() - 0.5f) * 2f * range;
                    float jz = (s.Next01() - 0.5f) * 2f * range;
                    return new Vector3((cell.x + 0.5f) * cellSize + jx,
                                       cell.y * cellSize,
                                       (cell.z + 0.5f) * cellSize + jz)
                           + parent.position;
                }

                Quaternion ScatterYaw(PropSet.PropEntry e, HashStream s) =>
                    Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);

                bool TryPlaceAt(PropSet.PropEntry e, Vector3Int cell, Quaternion rot, Vector3 worldPos, HashStream s)
                {
                    GameObject prefab = PickPrefab(e, s);
                    if (prefab == null) return false;
                    if (Blocking(e.tier))
                    {
                        blocked.Add(cell);
                        if (!ThresholdsConnected())
                        {
                            blocked.Remove(cell); // would seal a door off — skip
                            return false;
                        }
                    }
                    Spawn(e, prefab, cell, worldPos, rot);
                    return true;
                }

                // Eligible cells for an entry, hash-shuffled (never scan order).
                List<Vector3Int> Eligible(PropSet.PropEntry e, HashStream s)
                {
                    var list = new List<Vector3Int>();
                    foreach (var c in floor)
                    {
                        if (reserved.Contains(c) || usedCells.Contains(c)) continue;
                        if (!e.allowCenter && e.anchor == PropAnchor.FloorScatter && !WallAdjacent(c)) continue;
                        list.Add(c);
                    }
                    int shuffleSalt = s.Next();
                    list.Sort((a, b) =>
                        DungeonKitPlacer.Hash(a, shuffleSalt).CompareTo(DungeonKitPlacer.Hash(b, shuffleSalt)));
                    return list;
                }

                // ---- Entry order: features, guaranteed, scatter ----
                var ordered = new List<PropSet.PropEntry>(set.entries);
                int Rank(PropSet.PropEntry e) =>
                    e.anchor == PropAnchor.Feature ? 0 : e.guaranteed ? 1 : 2;
                ordered.Sort((a, b) => Rank(a).CompareTo(Rank(b)));

                // Nearest free floor cell to the room's TRUE centroid (average
                // of its own floor cells) — NOT room.InteriorFloorCell, which
                // snaps to the nearest real cell to the bounding-box center.
                // For an L/T/plus/notch room the bbox center sits in the bite,
                // so that snap lands right next to the notch instead of at
                // the footprint's actual visual center.
                (Vector3Int cell, bool ok) PickRoomCenterCell()
                {
                    float sx = 0f, sz = 0f;
                    foreach (var c in floor) { sx += c.x + 0.5f; sz += c.z + 0.5f; }
                    Vector3 centroid = new Vector3(sx / floor.Count, yFloor, sz / floor.Count);

                    Vector3Int best = default; float bestDist = float.MaxValue; bool ok = false;
                    foreach (var c in floor)
                    {
                        if (reserved.Contains(c) || usedCells.Contains(c)) continue;
                        float dist = (new Vector3(c.x + 0.5f, c.y, c.z + 0.5f) - centroid).sqrMagnitude;
                        if (dist < bestDist) { bestDist = dist; best = c; ok = true; }
                    }
                    return (best, ok);
                }

                // Finds the named wall's run and a spot on it. targetNormal
                // is derived from enterDir, so Back/Left/Right/Front are
                // stable regardless of how this room is oriented on the grid.
                (Vector3Int cell, Vector3Int normal, bool ok) PickWallSideCell(FeatureWallSide side, FeatureSpot spot)
                {
                    Vector3Int targetNormal = side switch
                    {
                        FeatureWallSide.Back  => enterDir,
                        FeatureWallSide.Front => -enterDir,
                        FeatureWallSide.Right => new Vector3Int(enterDir.z, 0, -enterDir.x),
                        FeatureWallSide.Left  => new Vector3Int(-enterDir.z, 0, enterDir.x),
                        _ => enterDir,
                    };

                    // Seed: first free cell (stable order) that's solid in
                    // exactly this direction. No seed = this room has no wall
                    // on that side (e.g. an L-bite ate it) — caller skips.
                    Vector3Int seed = default; bool found = false;
                    foreach (var c in floor)
                    {
                        if (reserved.Contains(c) || usedCells.Contains(c) || Open(c + targetNormal)) continue;
                        seed = c; found = true; break;
                    }
                    if (!found) return (default, default, false);

                    Vector3Int tangent = new Vector3Int(-targetNormal.z, 0, targetNormal.x);

                    // Walk the wall run both ways from the seed (same outward
                    // normal); run ends are the room's real corners.
                    bool OnRun(Vector3Int c) =>
                        room.Cells.Contains(c) && c.y == yFloor && !Open(c + targetNormal);
                    var run = new List<Vector3Int>();
                    Vector3Int cur = seed;
                    while (true) { cur -= tangent; if (!OnRun(cur)) break; run.Add(cur); }
                    run.Reverse();
                    run.Add(seed);
                    cur = seed;
                    while (true) { cur += tangent; if (!OnRun(cur)) break; run.Add(cur); }

                    // Target index along the run: middle for Center, one end
                    // for Corner (end picked by the stream).
                    int targetIdx = spot == FeatureSpot.Corner
                        ? (featureStream.Next() % 2 == 0 ? 0 : run.Count - 1)
                        : run.Count / 2;

                    // Nearest run cell to the target that's actually free
                    // (target itself may be reserved/used).
                    Vector3Int? pick = null;
                    for (int r = 0; r < run.Count && pick == null; r++)
                    {
                        foreach (int idx in new[] { targetIdx - r, targetIdx + r })
                        {
                            if (idx < 0 || idx >= run.Count) continue;
                            var cand = run[idx];
                            if (reserved.Contains(cand) || usedCells.Contains(cand)) continue;
                            pick = cand;
                            break;
                        }
                    }
                    return pick.HasValue ? (pick.Value, targetNormal, true) : (default, default, false);
                }

                // Direction a Feature entry's base yaw should look along,
                // before featureYaw is added. wallNormal is null for
                // RoomCenter placements (no wall to reference), in which case
                // Outward/Inward fall back to entrance-relative facing.
                Vector3 FeatureFacingDir(FeatureFacing facing, Vector3Int cell, Vector3Int? wallNormal)
                {
                    switch (facing)
                    {
                        case FeatureFacing.Outward:
                            if (wallNormal.HasValue) return -(Vector3)wallNormal.Value;
                            goto case FeatureFacing.FaceEntrance;
                        case FeatureFacing.Inward:
                            if (wallNormal.HasValue) return (Vector3)wallNormal.Value;
                            goto case FeatureFacing.FaceAwayFromEntrance;
                        case FeatureFacing.FaceAwayFromEntrance:
                            return new Vector3(cell.x - entrance.x, 0f, cell.z - entrance.z);
                        case FeatureFacing.FaceEntrance:
                        default:
                            return new Vector3(entrance.x - cell.x, 0f, entrance.z - cell.z);
                    }
                }

                foreach (var e in ordered)
                {
                    if (e.prefabs == null || e.prefabs.Length == 0) continue;

                    if (e.anchor == PropAnchor.Feature)
                    {
                        Vector3Int chosen; Vector3Int? wallNormal; bool foundCell;
                        if (e.featurePositionMode == FeaturePositionMode.RoomCenter)
                        {
                            var (cell, ok) = PickRoomCenterCell();
                            chosen = cell; wallNormal = null; foundCell = ok;
                        }
                        else
                        {
                            var (cell, normal, ok) = PickWallSideCell(e.featureWallSide, e.featureSpot);
                            chosen = cell; wallNormal = ok ? normal : (Vector3Int?)null; foundCell = ok;
                        }
                        if (!foundCell) continue;

                        Vector3 pos = new Vector3((chosen.x + 0.5f) * cellSize,
                                                  chosen.y * cellSize,
                                                  (chosen.z + 0.5f) * cellSize)
                                      + parent.position;

                        Quaternion baseRot;
                        if (e.featureFacing == FeatureFacing.Fixed)
                        {
                            baseRot = Quaternion.identity;
                        }
                        else
                        {
                            Vector3 dir = FeatureFacingDir(e.featureFacing, chosen, wallNormal);
                            baseRot = dir.sqrMagnitude > 0.01f
                                ? Quaternion.LookRotation(dir.normalized)
                                : Quaternion.identity;
                        }
                        Quaternion rot = baseRot * Quaternion.Euler(0f, e.featureYaw, 0f);
                        TryPlaceAt(e, chosen, rot, pos, featureStream);
                    }
                    else if (e.anchor == PropAnchor.CeilingHung)
                    {
                        int ceilY = room.Bounds.yMax; // top plane of the room
                        var cells = Eligible(e, ceilingStream);
                        int placedCount = 0;
                        int want = e.guaranteed ? e.count : int.MaxValue;
                        foreach (var c in cells)
                        {
                            if (placedCount >= want) break;
                            if (!e.guaranteed)
                            {
                                if (e.maxPerRoom > 0 && placedCount >= e.maxPerRoom) break;
                                if (ceilingStream.Next01() >= e.chancePerCell) continue;
                            }
                            float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                            Vector3 pos = new Vector3((c.x + 0.5f) * cellSize + (ceilingStream.Next01() - 0.5f) * 2f * range,
                                                      ceilY * cellSize,
                                                      (c.z + 0.5f) * cellSize + (ceilingStream.Next01() - 0.5f) * 2f * range)
                                          + parent.position;
                            if (TryPlaceAt(e, c, ScatterYaw(e, ceilingStream), pos, ceilingStream)) placedCount++;
                        }
                    }
                    else // FloorScatter
                    {
                        var cells = Eligible(e, scatterStream);
                        int placedCount = 0;
                        int want = e.guaranteed ? e.count : int.MaxValue;
                        foreach (var c in cells)
                        {
                            if (placedCount >= want) break;
                            if (!e.guaranteed)
                            {
                                if (e.maxPerRoom > 0 && placedCount >= e.maxPerRoom) break;
                                if (scatterStream.Next01() >= e.chancePerCell) continue;
                            }
                            if (TryPlaceAt(e, c, ScatterYaw(e, scatterStream), FloorWorld(c, e, scatterStream), scatterStream)) placedCount++;
                        }
                    }
                }
            }

            if (totalPlaced > 0)
                Debug.Log($"[Dungeon] {totalPlaced} prop(s) placed.");
            return root;
        }
    }
}