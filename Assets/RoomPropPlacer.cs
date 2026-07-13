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
    /// One room's placement context: floor cells (stable order), reserved
    /// threshold cells, primary entrance + entering direction, and the zone
    /// classification of every floor cell. Computed by
    /// RoomPropPlacer.ComputeZones — the single source of truth shared by
    /// placement and DungeonVisualizer's colorCellsByZone debug gizmo.
    /// </summary>
    public class RoomZones
    {
        public List<Vector3Int> Floor = new List<Vector3Int>();
        public HashSet<Vector3Int> Reserved = new HashSet<Vector3Int>();
        public Vector3Int Entrance;
        public Vector3Int EnterDir;
        public Dictionary<Vector3Int, RoomZone> Zones = new Dictionary<Vector3Int, RoomZone>();
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
        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        /// <summary>
        /// Classifies a room's floor cells into RoomZones (first match wins):
        ///   1. Entrance — reserved threshold cells, plus cells within 1 step.
        ///   2. Perimeter — wall-adjacent cells that aren't Entrance.
        ///   3. Back / Center — remainder, split by normalized distance along
        ///      the entrance axis (>= 0.66 of the way in = Back).
        /// Irregular rooms classify only real footprint cells; deterministic,
        /// no RNG draws.
        /// </summary>
        public static RoomZones ComputeZones(DungeonGenerator gen, Room room)
        {
            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            var rz = new RoomZones();
            int yFloor = room.Bounds.yMin;
            foreach (var c in room.Cells)
                if (c.y == yFloor) rz.Floor.Add(c);
            rz.Floor.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            if (rz.Floor.Count == 0) return rz;

            // Room-side threshold cells from door records (covers satellite
            // closets, whose openings are Room↔Room and invisible to the
            // hallway-adjacency test below). Ladder feet count as thresholds
            // too: they're access points — props must never block the climb,
            // and the flood-fill must keep them reachable.
            var doorRoomCells = new HashSet<Vector3Int>();
            foreach (var door in gen.Doors)
            {
                doorRoomCells.Add(door.HallwayCell + door.Direction);
                doorRoomCells.Add(door.HallwayCell); // host side of satellite doors
            }
            foreach (var ladder in gen.Ladders)
                doorRoomCells.Add(ladder.BaseCell);

            foreach (var c in rz.Floor)
            {
                if (doorRoomCells.Contains(c)) { rz.Reserved.Add(c); continue; }
                foreach (var d in HDirs)
                {
                    Vector3Int nb = c + d;
                    if (grid.InBounds(nb) && grid[nb] == CellType.Hallway)
                    {
                        rz.Reserved.Add(c);
                        break;
                    }
                }
            }

            // Primary entrance: first threshold in stable order, else the
            // room's interior cell.
            rz.Entrance = room.InteriorFloorCell;
            foreach (var c in rz.Floor)
                if (rz.Reserved.Contains(c)) { rz.Entrance = c; break; }

            // Direction from the entrance INTO the room. Zones and feature
            // walls (Back/Left/Right/Front) are named relative to this —
            // "which way you'd face walking in the door" — not
            // world-cardinal directions.
            rz.EnterDir = new Vector3Int(0, 0, 1); // degenerate fallback (no real threshold)
            foreach (var d in HDirs)
            {
                Vector3Int nb = rz.Entrance + d;
                if (Open(nb) && !room.Cells.Contains(nb)) { rz.EnterDir = -d; break; }
            }

            // Normalized depth along the entrance axis (0 = entrance side,
            // 1 = far side), from the floor cells' own extent — irregular
            // rooms just have no cells to classify in their bites.
            int minDot = int.MaxValue, maxDot = int.MinValue;
            foreach (var c in rz.Floor)
            {
                int dot = c.x * rz.EnterDir.x + c.z * rz.EnterDir.z;
                if (dot < minDot) minDot = dot;
                if (dot > maxDot) maxDot = dot;
            }
            float span = Mathf.Max(1, maxDot - minDot);

            foreach (var c in rz.Floor)
            {
                bool nearEntrance = rz.Reserved.Contains(c);
                if (!nearEntrance)
                    foreach (var d in HDirs)
                        if (rz.Reserved.Contains(c + d)) { nearEntrance = true; break; }

                RoomZone zone;
                if (nearEntrance) zone = RoomZone.Entrance;
                else
                {
                    bool wallAdjacent = false;
                    foreach (var d in HDirs)
                        if (!Open(c + d)) { wallAdjacent = true; break; }
                    if (wallAdjacent) zone = RoomZone.Perimeter;
                    else
                    {
                        float t = (c.x * rz.EnterDir.x + c.z * rz.EnterDir.z - minDot) / span;
                        zone = t >= 0.66f ? RoomZone.Back : RoomZone.Center;
                    }
                }
                rz.Zones[c] = zone;
            }
            return rz;
        }

        public static GameObject Build(DungeonGenerator gen, DungeonKit kit, RoomStyle style,
                                       float cellSize, Transform parent,
                                       InstancedDungeonRenderer instancer,
                                       WallFaceRegistry wallFaces = null)
        {
            var root = new GameObject("DungeonProps");
            root.transform.SetParent(parent, false);
            if (style == null) return root;

            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;
            var hDirs = HDirs;

            int totalPlaced = 0;
            // Socket diagnostics — sockets fail silently by design (skips are
            // legitimate outcomes), so the summary log says why.
            int socketFilled = 0, socketOutside = 0, socketReserved = 0, socketFlood = 0;

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
                var socketStream  = new HashStream(room.Bounds.min, 11004);
                var wallStream    = new HashStream(room.Bounds.min, 11005);

                // ---- Floor cells, thresholds, entrance, zones ----
                // One shared computation (also drives the visualizer's
                // colorCellsByZone gizmo, so debug view and placement can
                // never disagree).
                var rz = ComputeZones(gen, room);
                var floor = rz.Floor;
                if (floor.Count == 0) continue;
                var reserved = rz.Reserved;
                Vector3Int entrance = rz.Entrance;
                Vector3Int enterDir = rz.EnterDir;
                var zones = rz.Zones;

                // True footprint centroid — used by RoomCenter features and
                // FaceRoomCenter scatter. NOT room.InteriorFloorCell, whose
                // bbox-center snap lands beside the notch in L/T/plus rooms.
                float sxAll = 0f, szAll = 0f;
                foreach (var c in floor) { sxAll += c.x + 0.5f; szAll += c.z + 0.5f; }
                Vector3 centroid = new Vector3(sxAll / floor.Count, yFloor, szAll / floor.Count);

                // A cell is placeable only if it's actual room floor — NOT an
                // interior-stair cell (StairLower/StairUpper carved inside the
                // room keep their spot in room.Cells, so `floor` still lists
                // them and they'd otherwise get props under/inside the stairs).
                // Stairs stay in `floor` so the flood-fill treats them as
                // walkable; they're just excluded as placement candidates.
                bool Placeable(Vector3Int c) => grid[c] == CellType.Room;

                var usedCells = new HashSet<Vector3Int>();        // one FLOOR prop per cell
                var usedCeilingCells = new HashSet<Vector3Int>(); // one CEILING prop per cell (separate plane)
                var blocked = new HashSet<Vector3Int>();          // collider-tier FLOOR cells

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
                    if (!e.sharesTile) usedCells.Add(cell); // sharesTile doesn't reserve
                    totalPlaced++;
                    FillSockets(prefab, worldPos, rot, cell.y, 0);
                }

                // Resolves a placed prop's PropSockets and spawns children
                // (recursing one level: parent -> child -> grandchild). Reads
                // sockets from the PREFAB ASSET — décor-tier parents never
                // spawn a GameObject, so there is no instance to read from.
                // Children are independent placements (never parented), so
                // batching stays intact.
                void FillSockets(GameObject parentPrefab, Vector3 parentPos, Quaternion parentRot, int parentCellY, int depth)
                {
                    if (depth >= 2) return; // cap: grandchildren spawn, but have no children
                    var sockets = parentPrefab.GetComponentsInChildren<PropSocket>(true);
                    if (sockets.Length == 0) return;
                    // Hierarchy order is stable, but sort by name anyway —
                    // belt-and-braces for the socket stream's determinism.
                    if (sockets.Length > 1)
                        System.Array.Sort(sockets, (a, b) => string.CompareOrdinal(a.name, b.name));

                    // The socket's world pose composes from the parent's FULL
                    // visual pose: placement * the prefab root's own rotation
                    // and scale (the same correction BuildProto/BuildFunctional
                    // apply internally). The child's PlaceProps call below then
                    // passes placement-only rotation again, per the invariant —
                    // composing root rotation on the mesh path too would
                    // double-rotate (see CLAUDE.md §5).
                    Transform parentRoot = parentPrefab.transform;
                    Matrix4x4 parentWorld =
                        Matrix4x4.TRS(parentPos, parentRot * parentRoot.rotation, parentRoot.lossyScale)
                        * parentRoot.worldToLocalMatrix;

                    foreach (var s in sockets)
                    {
                        if (s.childPrefabs == null || s.childPrefabs.Length == 0) continue;
                        if (socketStream.Next01() >= s.fillChance) continue;
                        GameObject child = s.childPrefabs[socketStream.Next() % s.childPrefabs.Length];
                        if (child == null) continue;

                        Matrix4x4 m = parentWorld * s.transform.localToWorldMatrix;
                        Quaternion childRot = m.rotation
                            * Quaternion.Euler(0f, (socketStream.Next01() - 0.5f) * 2f * s.yawJitter, 0f);
                        Vector3 jitter = new Vector3(
                            (socketStream.Next01() - 0.5f) * 2f * s.positionJitter, 0f,
                            (socketStream.Next01() - 0.5f) * 2f * s.positionJitter);
                        Vector3 childPos = (Vector3)m.GetColumn(3) + childRot * jitter;

                        // Occupancy: a child that lands outside the room's
                        // footprint or on a reserved threshold is skipped (a
                        // table near a door must not push a chair into the
                        // doorway); blocking children claim their cell and
                        // respect the flood-fill like any other blocker.
                        // Cell Y comes from the PARENT's cell, never from the
                        // child's float Y: a chair socket authored exactly at
                        // floor height comes out of the matrix chain at
                        // y ≈ ±0.0001, and FloorToInt on the negative side
                        // would put it a story down — outside the footprint,
                        // silently skipping every child.
                        Vector3 local = (childPos - parent.position) / cellSize;
                        var cell = new Vector3Int(Mathf.FloorToInt(local.x), parentCellY, Mathf.FloorToInt(local.z));
                        if (!room.Cells.Contains(cell)) { socketOutside++; continue; }
                        if (reserved.Contains(cell)) { socketReserved++; continue; }
                        if (Blocking(s.childTier))
                        {
                            bool newlyBlocked = blocked.Add(cell);
                            if (newlyBlocked && !ThresholdsConnected())
                            {
                                blocked.Remove(cell); // would seal a door off — skip
                                socketFlood++;
                                continue;
                            }
                        }

                        PropTier childTier = instancer != null ? s.childTier : PropTier.FullGameObject;
                        PropInstancer.PlaceProps(instancer, child,
                            new[] { new PropPlacement { position = childPos, rotation = childRot } },
                            childTier, cellSize, root.transform);
                        totalPlaced++;
                        socketFilled++;
                        FillSockets(child, childPos, childRot, cell.y, depth + 1);
                    }
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

                // Snap-to-wall variant of FloorWorld: the prop origin sits
                // wallGap meters off the nominal wall plane; jitter applies
                // along the wall tangent only, so a row of snapped props
                // stays flush but still varies laterally.
                Vector3 FloorWorldSnapped(Vector3Int cell, PropSet.PropEntry e, HashStream s, Vector3Int wallDir)
                {
                    float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                    float jt = (s.Next01() - 0.5f) * 2f * range;
                    Vector3 tangent = new Vector3(-wallDir.z, 0f, wallDir.x);
                    return new Vector3((cell.x + 0.5f) * cellSize, cell.y * cellSize, (cell.z + 0.5f) * cellSize)
                           + (Vector3)wallDir * (cellSize * 0.5f - e.wallGap)
                           + tangent * jt
                           + parent.position;
                }

                Quaternion ScatterYaw(PropSet.PropEntry e, HashStream s) =>
                    Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, s.Next01()), 0f);

                // The cell's nearest solid wall direction (stream-picked when
                // a corner cell touches several). Shared by the wall facing
                // rules AND snapToWall so a corner shelf can never face away
                // from one wall while snapping to the other. With
                // requirePropsAllowed, faces whose wall asset forbids props
                // (recessed niches etc.) are excluded — a corner cell then
                // snaps to its OTHER wall if that one allows props.
                Vector3Int? NearestWallDir(Vector3Int cell, HashStream s, bool requirePropsAllowed = false)
                {
                    var solids = new List<Vector3Int>();
                    foreach (var d in hDirs)
                    {
                        if (Open(cell + d)) continue;
                        if (requirePropsAllowed && wallFaces != null &&
                            !wallFaces.PropsAllowed(grid.Index(cell), d)) continue;
                        solids.Add(d);
                    }
                    if (solids.Count == 0) return null;
                    return solids[solids.Count == 1 ? 0 : s.Next() % solids.Count];
                }

                // Scatter yaw per FacingRule (A3). yawRange is applied ON TOP
                // of every facing direction (Random = identity base, so the
                // range IS the yaw) — narrow ranges give wall-aligned props
                // slight natural variation. Wall rules with no wall at the
                // cell fall back to the identity base (= Random).
                Quaternion ScatterRotation(PropSet.PropEntry e, Vector3Int cell, HashStream s, Vector3Int? wallDir)
                {
                    Vector3 dir = Vector3.zero;
                    switch (e.facing)
                    {
                        case FacingRule.FaceEntrance:
                            dir = new Vector3(entrance.x - cell.x, 0f, entrance.z - cell.z);
                            break;
                        case FacingRule.FaceRoomCenter:
                            dir = new Vector3(centroid.x - (cell.x + 0.5f), 0f, centroid.z - (cell.z + 0.5f));
                            break;
                        case FacingRule.FaceAwayFromNearestWall:
                            if (wallDir.HasValue) dir = -(Vector3)wallDir.Value; // back to the wall, facing the room
                            break;
                        case FacingRule.AlignWithWall:
                            if (wallDir.HasValue)
                            {
                                // Forward along the wall tangent; either way
                                // along it, picked per placement.
                                dir = Vector3.Cross(Vector3.up, -(Vector3)wallDir.Value);
                                if (s.Next() % 2 == 1) dir = -dir;
                            }
                            break;
                    }
                    Quaternion baseRot = dir.sqrMagnitude > 0.01f
                        ? Quaternion.LookRotation(dir.normalized)
                        : Quaternion.identity;
                    return baseRot * ScatterYaw(e, s);
                }

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
                // FloorScatter and CeilingHung keep cells whose zone is in the
                // entry's preferredZones mask (multi-select), unless the
                // legacy allowCenter ("anywhere") toggle is on. A ceiling
                // cell's zone is its floor column's zone (same x/z cell).
                List<Vector3Int> Eligible(PropSet.PropEntry e, HashStream s)
                {
                    // Inside-corner is its own geometric selection (corners are
                    // inherently wall-adjacent) — don't also zone-filter it, or
                    // a non-Perimeter zone silently drops every corner.
                    bool zoneFilter = (e.anchor == PropAnchor.FloorScatter || e.anchor == PropAnchor.CeilingHung)
                                      && !e.allowCenter && !e.snapToInsideCorner;
                    // Ceiling props occupy their own plane — a floor rack and a
                    // ceiling light can share a cell.
                    var used = e.anchor == PropAnchor.CeilingHung ? usedCeilingCells : usedCells;
                    var list = new List<Vector3Int>();
                    foreach (var c in floor)
                    {
                        if (!Placeable(c) || reserved.Contains(c)) continue;
                        if (!e.sharesTile && used.Contains(c)) continue; // sharesTile ignores prop occupancy
                        if (zoneFilter && (e.preferredZones & (RoomZoneMask)(1 << (int)zones[c])) == 0) continue;
                        list.Add(c);
                    }
                    // Guaranteed entries must land somewhere — an empty zone
                    // (tiny room, L-bite ate it) falls back to any free cell.
                    // Chance scatter just places nothing: spilling it outside
                    // its authored zone would betray intent more than absence.
                    if (list.Count == 0 && zoneFilter && e.guaranteed)
                        foreach (var c in floor)
                            if (Placeable(c) && !reserved.Contains(c) && (e.sharesTile || !used.Contains(c))) list.Add(c);
                    int shuffleSalt = s.Next();
                    list.Sort((a, b) =>
                        DungeonKitPlacer.Hash(a, shuffleSalt).CompareTo(DungeonKitPlacer.Hash(b, shuffleSalt)));
                    return list;
                }

                int DirIndex(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;

                // Wall faces available to a WallMounted entry: every solid
                // face of every floor cell, minus faces whose wall asset
                // forbids props and faces already CLAIMED by a torch or an
                // earlier wall mount (one occupant per face). Hash-shuffled.
                List<(Vector3Int cell, Vector3Int dir)> WallFaces(HashStream s)
                {
                    var list = new List<(Vector3Int, Vector3Int)>();
                    foreach (var c in floor)
                    {
                        if (!Placeable(c)) continue; // no wall mounts off a stair cell
                        foreach (var d in hDirs)
                        {
                            if (Open(c + d)) continue; // need a solid wall
                            if (wallFaces != null &&
                                (!wallFaces.PropsAllowed(grid.Index(c), d) || wallFaces.IsClaimed(grid.Index(c), d)))
                                continue;
                            list.Add((c, d));
                        }
                    }
                    int salt = s.Next();
                    list.Sort((a, b) =>
                        DungeonKitPlacer.Hash(a.Item1, salt + DirIndex(a.Item2))
                            .CompareTo(DungeonKitPlacer.Hash(b.Item1, salt + DirIndex(b.Item2))));
                    return list;
                }

                // ---- Entry order: features, guaranteed, scatter ----
                var ordered = new List<PropSet.PropEntry>(set.entries);
                int Rank(PropSet.PropEntry e) =>
                    e.anchor == PropAnchor.Feature ? 0 : e.guaranteed ? 1 : 2;
                ordered.Sort((a, b) => Rank(a).CompareTo(Rank(b)));

                // Nearest free floor cell to the room's true centroid (hoisted
                // above — shared with FaceRoomCenter scatter facing).
                (Vector3Int cell, bool ok) PickRoomCenterCell()
                {
                    Vector3Int best = default; float bestDist = float.MaxValue; bool ok = false;
                    foreach (var c in floor)
                    {
                        if (!Placeable(c) || reserved.Contains(c) || usedCells.Contains(c)) continue;
                        float dist = (new Vector3(c.x + 0.5f, c.y, c.z + 0.5f) - centroid).sqrMagnitude;
                        if (dist < bestDist) { bestDist = dist; best = c; ok = true; }
                    }
                    return (best, ok);
                }

                // Finds the named wall's run and a spot on it. targetNormal
                // is derived from enterDir, so Back/Left/Right/Front are
                // stable regardless of how this room is oriented on the grid.
                (Vector3Int cell, Vector3Int normal, bool ok) PickWallSideCell(FeatureWallSide side, FeatureSpot spot,
                                                                               bool requirePropsAllowed)
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
                        if (!Placeable(c) || reserved.Contains(c) || usedCells.Contains(c) || Open(c + targetNormal)) continue;
                        seed = c; found = true; break;
                    }
                    if (!found) return (default, default, false);

                    Vector3Int tangent = new Vector3Int(-targetNormal.z, 0, targetNormal.x);

                    // Walk the wall run both ways from the seed (same outward
                    // normal); run ends are the room's real corners. Stair
                    // cells break the run (not placeable).
                    bool OnRun(Vector3Int c) =>
                        room.Cells.Contains(c) && c.y == yFloor && grid[c] == CellType.Room && !Open(c + targetNormal);
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
                    // (target itself may be reserved/used, or its wall asset
                    // may forbid props when the feature snaps to the wall).
                    Vector3Int? pick = null;
                    for (int r = 0; r < run.Count && pick == null; r++)
                    {
                        foreach (int idx in new[] { targetIdx - r, targetIdx + r })
                        {
                            if (idx < 0 || idx >= run.Count) continue;
                            var cand = run[idx];
                            if (reserved.Contains(cand) || usedCells.Contains(cand)) continue;
                            if (requirePropsAllowed && wallFaces != null &&
                                !wallFaces.PropsAllowed(grid.Index(cand), targetNormal)) continue;
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
                            var (cell, normal, ok) = PickWallSideCell(e.featureWallSide, e.featureSpot, e.snapToWall);
                            chosen = cell; wallNormal = ok ? normal : (Vector3Int?)null; foundCell = ok;
                        }
                        if (!foundCell) continue;

                        Vector3 pos = new Vector3((chosen.x + 0.5f) * cellSize,
                                                  chosen.y * cellSize,
                                                  (chosen.z + 0.5f) * cellSize)
                                      + parent.position;
                        // Snap flush against the feature's own wall (throne
                        // backed to the wall instead of floating at the cell
                        // center, 1.5m off it).
                        if (e.snapToWall && wallNormal.HasValue)
                            pos += (Vector3)wallNormal.Value * (cellSize * 0.5f - e.wallGap);

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
                        float ceilWorldY = ceilY * cellSize;
                        // Inside-corner snap wins over grid/wall-snap.
                        bool insideCorner = e.snapToInsideCorner;
                        bool gridLayout = !insideCorner && e.ceilingLayout == CeilingLayout.Grid;
                        int stride = Mathf.Max(1, e.gridStride);
                        // Corner-snap (single wall) is a Scatter feature; a grid
                        // places at cell centers on the lattice.
                        bool cornerSnap = !insideCorner && !gridLayout && e.snapToCeilingWall;
                        bool wantsWall = cornerSnap ||
                                         e.facing == FacingRule.FaceAwayFromNearestWall ||
                                         e.facing == FacingRule.AlignWithWall;

                        // Grid membership, anchored to the room corner. A
                        // PERIMETER cell steps ALONG its wall (every `stride`
                        // cells down the wall) — a 2D lattice would drop whole
                        // walls whose fixed coordinate isn't on the lattice,
                        // which is why perimeter grids came out nearly empty.
                        // An INTERIOR cell uses the true 2D lattice (light grid
                        // across the ceiling).
                        bool OnCeilingGrid(Vector3Int c)
                        {
                            if (stride <= 1) return true;
                            int dx = c.x - room.Bounds.xMin, dz = c.z - room.Bounds.zMin;
                            bool wallX = !Open(c + new Vector3Int(1, 0, 0)) || !Open(c + new Vector3Int(-1, 0, 0));
                            bool wallZ = !Open(c + new Vector3Int(0, 0, 1)) || !Open(c + new Vector3Int(0, 0, -1));
                            if (wallX && wallZ) return dx % stride == 0 || dz % stride == 0; // corner
                            if (wallX) return dz % stride == 0;  // left/right wall runs along z
                            if (wallZ) return dx % stride == 0;  // top/bottom wall runs along x
                            return dx % stride == 0 && dz % stride == 0; // interior 2D lattice
                        }

                        var cells = Eligible(e, ceilingStream);
                        int placedCount = 0;
                        int want = e.guaranteed ? e.count : int.MaxValue;
                        foreach (var c in cells)
                        {
                            if (placedCount >= want) break;
                            // Skipped BEFORE the chance roll so gaps come only
                            // from chance, not lattice misses.
                            if (gridLayout && !OnCeilingGrid(c)) continue;
                            // Inside-corner: only concave-corner cells qualify.
                            Vector3Int ca = default, cb = default;
                            if (insideCorner &&
                                !PropSnap.TryInsideCorner(grid, c, null, false, ceilingStream.Next(), out ca, out cb))
                                continue;
                            if (!e.guaranteed)
                            {
                                if (e.maxPerRoom > 0 && placedCount >= e.maxPerRoom) break;
                                if (ceilingStream.Next01() >= e.chancePerCell) continue;
                            }
                            Vector3Int? wallDir = wantsWall ? NearestWallDir(c, ceilingStream, cornerSnap) : null;
                            // A corner-snap entry belongs against a wall: no
                            // (props-allowed) wall at the cell = skip.
                            if (cornerSnap && !wallDir.HasValue) continue;
                            Quaternion rot;
                            Vector3 pos;
                            if (insideCorner)
                            {
                                pos = new Vector3((c.x + 0.5f) * cellSize, ceilWorldY, (c.z + 0.5f) * cellSize)
                                      + PropSnap.CornerOffset(ca, cb, cellSize, e.wallGap) + parent.position;
                                Vector3 f = PropSnap.CornerFacing(ca, cb);
                                rot = Quaternion.LookRotation(f.normalized)
                                      * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, ceilingStream.Next01()), 0f);
                            }
                            else if (cornerSnap)
                            {
                                rot = ScatterRotation(e, c, ceilingStream, wallDir);
                                float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                                Vector3 tangent = new Vector3(-wallDir.Value.z, 0f, wallDir.Value.x);
                                pos = new Vector3((c.x + 0.5f) * cellSize, ceilWorldY, (c.z + 0.5f) * cellSize)
                                      + (Vector3)wallDir.Value * (cellSize * 0.5f - e.wallGap)
                                      + tangent * ((ceilingStream.Next01() - 0.5f) * 2f * range)
                                      + parent.position;
                            }
                            else
                            {
                                rot = ScatterRotation(e, c, ceilingStream, wallDir);
                                float range = e.subCellJitter * (cellSize * 0.5f - 0.7f);
                                pos = new Vector3((c.x + 0.5f) * cellSize + (ceilingStream.Next01() - 0.5f) * 2f * range,
                                                  ceilWorldY,
                                                  (c.z + 0.5f) * cellSize + (ceilingStream.Next01() - 0.5f) * 2f * range)
                                      + parent.position;
                            }
                            // Ceiling plane: no floor blocking / flood-fill (a
                            // hanging prop doesn't obstruct walking), own used-set.
                            GameObject cprefab = PickPrefab(e, ceilingStream);
                            if (cprefab == null) continue;
                            PropTier ctier = instancer != null ? e.tier : PropTier.FullGameObject;
                            PropInstancer.PlaceProps(instancer, cprefab,
                                new[] { new PropPlacement { position = pos, rotation = rot } },
                                ctier, cellSize, root.transform);
                            if (!e.sharesTile) usedCeilingCells.Add(c); // sharesTile doesn't reserve
                            totalPlaced++;
                            placedCount++;
                        }
                    }
                    else if (e.anchor == PropAnchor.WallMounted)
                    {
                        // Mounted ON wall faces (banners, shields). No floor
                        // occupancy — a wall mount doesn't block movement — so
                        // no flood-fill; it just claims its face so a torch or
                        // another mount won't share it.
                        var faces = WallFaces(wallStream);
                        int placedCount = 0;
                        int want = e.guaranteed ? e.count : int.MaxValue;
                        foreach (var (c, d) in faces)
                        {
                            if (placedCount >= want) break;
                            if (!e.guaranteed)
                            {
                                if (e.maxPerRoom > 0 && placedCount >= e.maxPerRoom) break;
                                if (wallStream.Next01() >= e.chancePerCell) continue;
                            }
                            GameObject prefab = PickPrefab(e, wallStream);
                            if (prefab == null) continue;

                            float h = e.mountHeight + (e.mountHeightJitter > 0f
                                ? (wallStream.Next01() - 0.5f) * 2f * e.mountHeightJitter : 0f);
                            Vector3 tangent = new Vector3(-d.z, 0f, d.x);
                            float latRange = e.subCellJitter * (cellSize * 0.5f - 0.3f);
                            float lat = (wallStream.Next01() - 0.5f) * 2f * latRange;
                            Vector3 pos = new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize)
                                          + (Vector3)d * (cellSize * 0.5f - e.wallGap) // out to the face, then off it
                                          + tangent * lat
                                          + Vector3.up * h
                                          + parent.position;
                            Quaternion rot = Quaternion.LookRotation(-(Vector3)d) // forward = away from wall
                                             * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, wallStream.Next01()), 0f);

                            PropTier tier = instancer != null ? e.tier : PropTier.FullGameObject;
                            PropInstancer.PlaceProps(instancer, prefab,
                                new[] { new PropPlacement { position = pos, rotation = rot } },
                                tier, cellSize, root.transform);
                            wallFaces?.Claim(grid.Index(c), d);
                            totalPlaced++;
                            placedCount++;
                        }
                    }
                    else // FloorScatter
                    {
                        bool insideCorner = e.snapToInsideCorner;
                        bool wantsWall = !insideCorner && (e.snapToWall ||
                                         e.facing == FacingRule.FaceAwayFromNearestWall ||
                                         e.facing == FacingRule.AlignWithWall);
                        var cells = Eligible(e, scatterStream);
                        int placedCount = 0;
                        int want = e.guaranteed ? e.count : int.MaxValue;
                        foreach (var c in cells)
                        {
                            if (placedCount >= want) break;
                            // Inside-corner: only concave-corner cells qualify.
                            Vector3Int ca = default, cb = default;
                            if (insideCorner &&
                                !PropSnap.TryInsideCorner(grid, c, null, false, scatterStream.Next(), out ca, out cb))
                                continue;
                            if (!e.guaranteed)
                            {
                                if (e.maxPerRoom > 0 && placedCount >= e.maxPerRoom) break;
                                if (scatterStream.Next01() >= e.chancePerCell) continue;
                            }
                            Vector3Int? wallDir = wantsWall ? NearestWallDir(c, scatterStream, e.snapToWall) : null;
                            // A snap entry belongs against a wall: a cell with
                            // no (props-allowed) wall is skipped, not placed
                            // floating at its center.
                            if (e.snapToWall && !insideCorner && !wallDir.HasValue) continue;
                            Quaternion rot;
                            Vector3 pos;
                            if (insideCorner)
                            {
                                pos = new Vector3((c.x + 0.5f) * cellSize, c.y * cellSize, (c.z + 0.5f) * cellSize)
                                      + PropSnap.CornerOffset(ca, cb, cellSize, e.wallGap) + parent.position;
                                rot = Quaternion.LookRotation(PropSnap.CornerFacing(ca, cb).normalized)
                                      * Quaternion.Euler(0f, Mathf.Lerp(e.yawRange.x, e.yawRange.y, scatterStream.Next01()), 0f);
                            }
                            else
                            {
                                rot = ScatterRotation(e, c, scatterStream, wallDir);
                                pos = e.snapToWall
                                    ? FloorWorldSnapped(c, e, scatterStream, wallDir.Value)
                                    : FloorWorld(c, e, scatterStream);
                            }
                            if (TryPlaceAt(e, c, rot, pos, scatterStream)) placedCount++;
                        }
                    }
                }
            }

            if (totalPlaced > 0)
                Debug.Log($"[Dungeon] {totalPlaced} prop(s) placed. Sockets: {socketFilled} filled" +
                          (socketOutside + socketReserved + socketFlood > 0
                              ? $", skipped {socketOutside} outside footprint, {socketReserved} on thresholds, {socketFlood} flood-fill"
                              : "") + ".");
            return root;
        }
    }
}