using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Places the global corridor prop set (RoomStyle.hallwayProps) — debris,
    /// cobwebs, roots. Hallways aren't rooms: no zones, no centroid, no
    /// entrance, so this is a separate pass from RoomPropPlacer. It scans
    /// CellType.Hallway cells directly and supports the anchors that make
    /// sense in a corridor:
    ///   - FloorScatter  (snapToWall + facing rules; zone fields ignored)
    ///   - CeilingHung   (scatter or Grid stride ALONG the corridor)
    ///   - WallMounted   (torch-negotiated via WallFaceRegistry)
    /// Feature is a room concept and is skipped.
    ///
    /// Safety: a corridor is the through-route. Décor never blocks. A blocking
    /// (collider-tier) prop runs a connectivity check over the hallway+stair
    /// network — if it would sever any door from another, it's rolled back.
    /// In a 1-wide corridor every cell is a chokepoint, so blocking props
    /// naturally only land in wide spots / junctions.
    ///
    /// Deterministic: one global HashStream per pass (anchored at origin, so
    /// distinct from RoomPropPlacer's per-room 110xx streams).
    /// </summary>
    public static class HallwayPropPlacer
    {
        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        public static GameObject Build(DungeonGenerator gen, RoomStyle style, float cellSize, Transform parent,
                                       InstancedDungeonRenderer instancer, WallFaceRegistry wallFaces = null)
        {
            var root = new GameObject("DungeonHallwayProps");
            root.transform.SetParent(parent, false);
            if (style == null) return root;
            PropSet set = style.HallwayProps();
            if (set == null || set.entries == null || set.entries.Count == 0) return root;

            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;
            bool Walkable(Vector3Int p) => grid.InBounds(p) &&
                (grid[p] == CellType.Hallway || grid[p] == CellType.StairLower || grid[p] == CellType.StairUpper);

            // All corridor floor cells, stable order.
            var cells = new List<Vector3Int>();
            for (int i = 0; i < grid.Length; i++)
                if (grid[i] == CellType.Hallway) cells.Add(grid.Position(i));
            if (cells.Count == 0) return root;
            cells.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z != b.z ? a.z.CompareTo(b.z) : a.y.CompareTo(b.y));

            // Door hallway cells: reserved (no debris in a doorway) AND the
            // connectivity anchors the passability check must keep joined.
            var doorCells = new HashSet<Vector3Int>();
            foreach (var d in gen.Doors) doorCells.Add(d.HallwayCell);
            var reserved = new HashSet<Vector3Int>(doorCells);

            var usedFloor = new HashSet<Vector3Int>();
            var usedCeiling = new HashSet<Vector3Int>();
            var blocked = new HashSet<Vector3Int>();

            // All doors mutually reachable across the walkable network minus
            // blocked cells. The hallway net is small; a BFS per blocking
            // placement is fine (and only blocking tiers pay for it).
            bool Connected()
            {
                if (doorCells.Count <= 1) return true;
                Vector3Int start = default; bool have = false;
                foreach (var c in doorCells) { start = c; have = true; break; }
                if (!have) return true;
                var seen = new HashSet<Vector3Int> { start };
                var q = new Queue<Vector3Int>();
                q.Enqueue(start);
                while (q.Count > 0)
                {
                    var c = q.Dequeue();
                    foreach (var d in HDirs)
                    {
                        var n = c + d;
                        if (blocked.Contains(n) || seen.Contains(n) || !Walkable(n)) continue;
                        seen.Add(n);
                        q.Enqueue(n);
                    }
                    // Vertical step through stacked stair cells (the only
                    // vertical connectors in the corridor network).
                    foreach (var d in new[] { Vector3Int.up, Vector3Int.down })
                    {
                        var n = c + d;
                        if (blocked.Contains(n) || seen.Contains(n) || !Walkable(n)) continue;
                        seen.Add(n);
                        q.Enqueue(n);
                    }
                }
                foreach (var c in doorCells)
                    if (!seen.Contains(c)) return false;
                return true;
            }

            var scatterStream = new HashStream(Vector3Int.zero, 12002);
            var ceilingStream = new HashStream(Vector3Int.zero, 12003);
            var wallStream = new HashStream(Vector3Int.zero, 12005);

            bool Blocking(PropTier t) => t != PropTier.StaticDecor;

            GameObject Pick(PropSet.PropEntry e, HashStream s) =>
                (e.prefabs == null || e.prefabs.Length == 0) ? null : e.prefabs[s.Next() % e.prefabs.Length];

            void Place(GameObject prefab, Vector3 pos, Quaternion rot, PropTier tier)
            {
                PropTier t = instancer != null ? tier : PropTier.FullGameObject;
                PropInstancer.PlaceProps(instancer, prefab,
                    new[] { new PropPlacement { position = pos, rotation = rot } },
                    t, cellSize, root.transform);
            }

            // Nearest solid wall of a corridor cell (stream-picked among ties),
            // optionally requiring the wall face allows props.
            Vector3Int? NearestWall(Vector3Int cell, HashStream s, bool requirePropsAllowed)
            {
                var solids = new List<Vector3Int>();
                foreach (var d in HDirs)
                {
                    if (Open(cell + d)) continue;
                    if (requirePropsAllowed && wallFaces != null && !wallFaces.PropsAllowed(grid.Index(cell), d)) continue;
                    solids.Add(d);
                }
                if (solids.Count == 0) return null;
                return solids[solids.Count == 1 ? 0 : s.Next() % solids.Count];
            }

            // Corridor scatter yaw: only Random and the wall rules are
            // meaningful (no room entrance/center); others fall back to Random.
            Quaternion Yaw(PropSet.PropEntry e, HashStream s, Vector3Int? wallDir)
            {
                Vector3 dir = Vector3.zero;
                if (e.facing == FacingRule.FaceAwayFromNearestWall && wallDir.HasValue)
                    dir = -(Vector3)wallDir.Value;
                else if (e.facing == FacingRule.AlignWithWall && wallDir.HasValue)
                {
                    dir = Vector3.Cross(Vector3.up, -(Vector3)wallDir.Value);
                    if (s.Next() % 2 == 1) dir = -dir;
                }
                Quaternion baseRot = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity;
                return baseRot * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);
            }

            List<Vector3Int> Shuffled(HashStream s, HashSet<Vector3Int> used, bool sharesTile)
            {
                var list = new List<Vector3Int>();
                foreach (var c in cells)
                    if (!reserved.Contains(c) && (sharesTile || !used.Contains(c))) list.Add(c);
                int salt = s.Next();
                list.Sort((a, b) => DungeonKitPlacer.Hash(a, salt).CompareTo(DungeonKitPlacer.Hash(b, salt)));
                return list;
            }

            int total = 0;

            foreach (var e in set.entries)
            {
                if (e.prefabs == null || e.prefabs.Length == 0) continue;

                if (e.anchor == PropAnchor.CeilingHung)
                {
                    bool insideCorner = e.snapToInsideCorner;
                    bool gridLayout = !insideCorner && e.ceilingLayout == CeilingLayout.Grid;
                    int stride = Mathf.Max(1, e.gridStride);
                    int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
                    foreach (var c in Shuffled(ceilingStream, usedCeiling, e.sharesTile))
                    {
                        if (placed >= want) break;
                        // Grid strides ALONG the corridor: a hallway cell is
                        // wall-adjacent, so step by the axis that runs down the
                        // corridor (the open direction), anchored at origin.
                        if (gridLayout && stride > 1 && !OnCorridorGrid(c, stride, Open)) continue;
                        Vector3Int ca = default, cb = default;
                        if (insideCorner && !PropSnap.TryInsideCorner(grid, c, null, false, ceilingStream.Next(), out ca, out cb))
                            continue;
                        if (!e.guaranteed)
                        {
                            if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                            if (ceilingStream.Next01() >= e.chancePerCell) continue;
                        }
                        bool cornerSnap = !insideCorner && !gridLayout && e.snapToCeilingWall;
                        Vector3Int? wallDir = cornerSnap ? NearestWall(c, ceilingStream, true) : null;
                        if (cornerSnap && !wallDir.HasValue) continue;
                        float ceilWorldY = (c.y + 1) * cellSize; // corridors are single-story
                        float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                        Vector3 pos;
                        Quaternion rot;
                        if (insideCorner)
                        {
                            pos = new Vector3((c.x + 0.5f) * cellSize, ceilWorldY, (c.z + 0.5f) * cellSize)
                                  + PropSnap.CornerOffset(ca, cb, cellSize, e.wallGap) + parent.position;
                            rot = Quaternion.LookRotation(PropSnap.CornerFacing(ca, cb).normalized)
                                  * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, ceilingStream.Next01()), 0f);
                        }
                        else if (cornerSnap)
                        {
                            Vector3 tan = new Vector3(-wallDir.Value.z, 0f, wallDir.Value.x);
                            pos = new Vector3((c.x + 0.5f) * cellSize, ceilWorldY, (c.z + 0.5f) * cellSize)
                                  + (Vector3)wallDir.Value * (cellSize * 0.5f - e.wallGap)
                                  + tan * ((ceilingStream.Next01() - 0.5f) * 2f * range) + parent.position;
                            rot = Yaw(e, ceilingStream, wallDir);
                        }
                        else
                        {
                            pos = new Vector3((c.x + 0.5f) * cellSize + (ceilingStream.Next01() - 0.5f) * 2f * range,
                                              ceilWorldY,
                                              (c.z + 0.5f) * cellSize + (ceilingStream.Next01() - 0.5f) * 2f * range) + parent.position;
                            rot = Yaw(e, ceilingStream, wallDir);
                        }
                        GameObject prefab = Pick(e, ceilingStream);
                        if (prefab == null) continue;
                        Place(prefab, pos, rot, e.tier);
                        if (!e.sharesTile) usedCeiling.Add(c);
                        total++; placed++;
                    }
                }
                else if (e.anchor == PropAnchor.WallMounted)
                {
                    // Corridor wall faces, torch-negotiated (one occupant/face).
                    var faces = new List<(Vector3Int c, Vector3Int d)>();
                    foreach (var c in cells)
                    {
                        if (reserved.Contains(c)) continue;
                        foreach (var d in HDirs)
                        {
                            if (Open(c + d)) continue;
                            if (wallFaces != null && (!wallFaces.PropsAllowed(grid.Index(c), d) || wallFaces.IsClaimed(grid.Index(c), d))) continue;
                            faces.Add((c, d));
                        }
                    }
                    int salt = wallStream.Next();
                    faces.Sort((a, b) => DungeonKitPlacer.Hash(a.c, salt + DirIdx(a.d)).CompareTo(DungeonKitPlacer.Hash(b.c, salt + DirIdx(b.d))));
                    int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
                    foreach (var (c, d) in faces)
                    {
                        if (placed >= want) break;
                        if (!e.guaranteed)
                        {
                            if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                            if (wallStream.Next01() >= e.chancePerCell) continue;
                        }
                        GameObject prefab = Pick(e, wallStream);
                        if (prefab == null) continue;
                        float h = e.mountHeight + (e.mountHeightJitter > 0f ? (wallStream.Next01() - 0.5f) * 2f * e.mountHeightJitter : 0f);
                        Vector3 tan = new Vector3(-d.z, 0f, d.x);
                        float latRange = e.subCellJitter * (cellSize * 0.5f - 0.3f);
                        Vector3 pos = new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize)
                                      + (Vector3)d * (cellSize * 0.5f - e.wallGap)
                                      + tan * ((wallStream.Next01() - 0.5f) * 2f * latRange)
                                      + Vector3.up * h + parent.position;
                        Quaternion rot = Quaternion.LookRotation(-(Vector3)d)
                                         * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, wallStream.Next01()), 0f);
                        Place(prefab, pos, rot, e.tier);
                        wallFaces?.Claim(grid.Index(c), d);
                        total++; placed++;
                    }
                }
                else if (e.anchor == PropAnchor.FloorScatter)
                {
                    bool insideCorner = e.snapToInsideCorner;
                    bool wantsWall = !insideCorner && (e.snapToWall ||
                                     e.facing == FacingRule.FaceAwayFromNearestWall ||
                                     e.facing == FacingRule.AlignWithWall);
                    int placed = 0, want = e.guaranteed ? e.count : int.MaxValue;
                    foreach (var c in Shuffled(scatterStream, usedFloor, e.sharesTile))
                    {
                        if (placed >= want) break;
                        Vector3Int ca = default, cb = default;
                        if (insideCorner && !PropSnap.TryInsideCorner(grid, c, null, false, scatterStream.Next(), out ca, out cb))
                            continue;
                        if (!e.guaranteed)
                        {
                            if (e.maxPerRoom > 0 && placed >= e.maxPerRoom) break;
                            if (scatterStream.Next01() >= e.chancePerCell) continue;
                        }
                        Vector3Int? wallDir = wantsWall ? NearestWall(c, scatterStream, e.snapToWall) : null;
                        if (e.snapToWall && !insideCorner && !wallDir.HasValue) continue;

                        float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                        Vector3 pos;
                        Quaternion rot;
                        if (insideCorner)
                        {
                            pos = new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize)
                                  + PropSnap.CornerOffset(ca, cb, cellSize, e.wallGap) + parent.position;
                            rot = Quaternion.LookRotation(PropSnap.CornerFacing(ca, cb).normalized)
                                  * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, scatterStream.Next01()), 0f);
                        }
                        else if (e.snapToWall)
                        {
                            Vector3 tan = new Vector3(-wallDir.Value.z, 0f, wallDir.Value.x);
                            pos = new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize)
                                  + (Vector3)wallDir.Value * (cellSize * 0.5f - e.wallGap)
                                  + tan * ((scatterStream.Next01() - 0.5f) * 2f * range) + parent.position;
                            rot = Yaw(e, scatterStream, wallDir);
                        }
                        else
                        {
                            pos = new Vector3((c.x + 0.5f) * cellSize + (scatterStream.Next01() - 0.5f) * 2f * range,
                                              c.y * cellSize,
                                              (c.z + 0.5f) * cellSize + (scatterStream.Next01() - 0.5f) * 2f * range) + parent.position;
                            rot = Yaw(e, scatterStream, wallDir);
                        }
                        GameObject prefab = Pick(e, scatterStream);
                        if (prefab == null) continue;

                        // Blocking décor must not sever the corridor.
                        if (Blocking(e.tier))
                        {
                            blocked.Add(c);
                            if (!Connected()) { blocked.Remove(c); continue; }
                        }
                        Place(prefab, pos, rot, e.tier);
                        if (!e.sharesTile) usedFloor.Add(c);
                        total++; placed++;
                    }
                }
                // Feature: room-only, skipped in corridors.
            }

            if (total > 0) Debug.Log($"[Dungeon] {total} hallway prop(s) placed.");
            return root;
        }

        static int DirIdx(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;

        // A corridor ceiling cell is on the grid if it steps by `stride` along
        // the axis the corridor RUNS (the open direction), anchored at origin.
        // A junction (open both axes) uses a 2D lattice.
        static bool OnCorridorGrid(Vector3Int c, int stride, System.Func<Vector3Int, bool> open)
        {
            bool openX = open(c + new Vector3Int(1, 0, 0)) || open(c + new Vector3Int(-1, 0, 0));
            bool openZ = open(c + new Vector3Int(0, 0, 1)) || open(c + new Vector3Int(0, 0, -1));
            if (openX && openZ) return c.x % stride == 0 && c.z % stride == 0; // junction: 2D lattice
            if (openX) return c.x % stride == 0; // runs along x
            if (openZ) return c.z % stride == 0; // runs along z
            return true; // dead-end pocket: always
        }
    }
}
