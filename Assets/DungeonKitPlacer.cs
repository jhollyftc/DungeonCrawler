using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Prefab slots for the modular kit. Each slot is an array so you can drop
    /// in variants; selection is a deterministic hash of the cell position, so
    /// a given seed always produces the identical dressed dungeon.
    /// Expected conventions (see dungeon-kit-spec.md):
    ///   floor   — pivot at tile center on the walking surface
    ///   ceiling — pivot at tile center on the visible (downward) surface
    ///   wall    — pivot at bottom-center of the visible face; face looks along +Z
    ///   stair   — pivot at bottom-center of the foot edge; ascends along +Z
    ///   bars    — wall-format piece for prison doorways (optional)
    /// </summary>
    [System.Serializable]
    public class DungeonKit
    {
        public GameObject[] floorPrefabs;
        public GameObject[] ceilingPrefabs;
        public GameObject[] wallPrefabs;
        public GameObject[] stairPrefabs;
        public GameObject[] prisonBarsPrefabs; // optional — skipped if empty, superseded by prisonDoorPrefabs
        [Tooltip("Hinged prison gate (needs a HingedDoor set up like the wooden door). Placed at every prison entrance as a real GameObject. When assigned, the static bars slot is ignored.")]
        public GameObject[] prisonDoorPrefabs; // optional — skipped if empty
        [Tooltip("Chance a prison gate spawns locked (deterministic per seed). Lockpicking comes later.")]
        [Range(0f, 1f)] public float prisonDoorLockChance = 0.15f;
        [Tooltip("Open arch frame at hallway↔room openings (colonnades and doorless entrances). Spawned as real GameObjects with their prefab colliders — thick arches need collision the greybox doesn't provide, so give the prefab a collider.")]
        public GameObject[] archwayPrefabs;    // optional — skipped if empty
        [Tooltip("Physical door (arch + wooden door). Placed ONLY at semantic entrances the generator flagged with HasDoor. Always spawned as real GameObjects (never instanced) so they can open/break later — give the prefab a collider.")]
        public GameObject[] doorPrefabs;       // optional — skipped if empty
        [Tooltip("Posts wrapping convex block corners that jut into open space (corridor turns). Forward faces diagonally away from the solid corner being wrapped.")]
        public GameObject[] outerCornerPillarPrefabs; // optional — skipped if empty
        [Tooltip("Posts for concave room/corridor inside corners, if you model one later. Forward faces diagonally into the open cell.")]
        public GameObject[] innerCornerPillarPrefabs; // optional — skipped if empty
        [Tooltip("Free-standing interior column segment (one cell / 3m tall, stacked to reach the ceiling). Placed at lattice points in grand rooms. Give it a collider — the floor cells stay walkable, so collision comes from the prefab.")]
        public GameObject[] interiorColumnPrefabs; // optional — skipped if empty
        public Vector3 interiorColumnOffset;
        [Tooltip("Place corner posts on edges that touch a doorway/arch face (jamb corners and meeting-arch corners). Off = arches stand alone.")]
        public bool pillarsAtDoorways = false;
        public bool randomizeFloorYaw = true;
        public bool randomizeCeilingYaw = true;

        [Header("Pivot correction (meters, world space)")]
        [Tooltip("Applied to EVERY kit placement (pieces, doors, gates). Use to dial the whole kit flush against the greybox collision shell when visuals sit uniformly off nominal heights. A clean value like ±1.5 or ±3 is the fingerprint of a prefab/origin offset that should eventually be fixed at the source and this zeroed.")]
        public Vector3 globalVisualOffset;
        [Tooltip("Use these to compensate for asset pivots that don't sit on the placement surface. The proper fix is setting the origin in Blender; these are the hotfix.")]
        public Vector3 floorOffset;
        public Vector3 ceilingOffset;
        public Vector3 wallOffset;
        public Vector3 stairOffset;
        public Vector3 archwayOffset;
        public Vector3 doorOffset;
        public Vector3 prisonDoorOffset;
        public Vector3 pillarOffset;
    }

    /// <summary>
    /// Owns the kit placement logic. Enumerate() walks every piece placement
    /// and hands (prefab, position-in-cells, rotation, offset-in-meters) to a
    /// callback; Build() consumes it to instantiate GameObjects, and the
    /// instanced renderer consumes it to collect matrices. One source of truth
    /// for what goes where.
    /// </summary>
    public static class DungeonKitPlacer
    {
        public delegate void PlaceCallback(GameObject prefab, Vector3 posCells, Quaternion rot, Vector3 offsetMeters);

        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int( 1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 0, 1),
            new Vector3Int( 0, 0,-1),
        };

        /// <summary>
        /// placeWithCollider: optional second sink for placements that need
        /// real collision (stairs, corner pillars) even in InstancedKit mode,
        /// where `place` only reaches the mesh instancer. Defaults to `place`
        /// itself, so callers who don't care (PrefabKit's Build(), which
        /// Instantiates full prefabs — colliders included — either way) see
        /// no behavior change.
        /// </summary>
        public static void Enumerate(DungeonGenerator gen, DungeonKit kit, HashSet<string> missing, PlaceCallback place,
                                     RoomStyle style = null, PlaceCallback placeWithCollider = null)
        {
            placeWithCollider ??= place;
            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            // Cached room lookup — pillar-style resolution queries up to four
            // cells per edge, and RoomAt is a linear scan.
            var roomCache = new Dictionary<Vector3Int, Room>();
            Room RoomAtCached(Vector3Int p)
            {
                if (roomCache.TryGetValue(p, out var r)) return r;
                r = gen.RoomAt(p);
                roomCache[p] = r;
                return r;
            }

            // ---- Capped wall reservations ----
            // Capped assets (fireplace: 1 per room) are assigned to faces via a
            // per-room pre-pass over ALL the room's wall faces in a
            // deterministically SHUFFLED order — never scan order, which would
            // clump every special into the room's first-scanned corner. Caps
            // thus become guaranteed counts (faces permitting), placed
            // uniformly. Unreserved faces draw from the set's unlimited assets.
            var reservations = new Dictionary<Room, Dictionary<long, GameObject>>();

            RoomStyle.WallBand BandOf(Room room, Vector3Int cell)
            {
                if (room.Bounds.size.y > 1)
                {
                    if (cell.y == room.Bounds.yMax - 1) return RoomStyle.WallBand.Top;
                    if (cell.y > room.Bounds.yMin) return RoomStyle.WallBand.Middle;
                }
                return RoomStyle.WallBand.Bottom;
            }

            Dictionary<long, GameObject> GetReservations(Room room)
            {
                if (reservations.TryGetValue(room, out var res)) return res;
                res = new Dictionary<long, GameObject>();
                reservations[room] = res;
                if (style == null) return res;

                var setAssets = style.WallSetFor(room.Type);
                if (setAssets == null) return res;

                // Gather this room's wall faces, grouped by band.
                var facesByBand = new Dictionary<RoomStyle.WallBand, List<(Vector3Int cell, int dirIdx)>>();
                foreach (var cell in room.Cells)
                    for (int di = 0; di < HDirs.Length; di++)
                    {
                        if (Open(cell + HDirs[di])) continue;
                        var band = BandOf(room, cell);
                        if (!facesByBand.TryGetValue(band, out var list))
                            facesByBand[band] = list = new List<(Vector3Int, int)>();
                        list.Add((cell, di));
                    }

                // Deal each capped asset ONCE, from the union of faces in its
                // allowed bands, in hash-shuffled order (never scan order). A
                // shared used-set keeps two specials off the same face. Salt
                // the shuffle per asset so co-eligible specials decorrelate.
                var usedFaces = new HashSet<long>();
                for (int ai = 0; ai < setAssets.Count; ai++)
                {
                    var a = setAssets[ai];
                    if (a.prefab == null || a.maxPerRoom <= 0) continue;

                    var eligible = new List<(Vector3Int cell, int dirIdx)>();
                    foreach (var kv in facesByBand)
                        if (a.Allows(kv.Key))
                            eligible.AddRange(kv.Value);
                    if (eligible.Count == 0) continue;

                    int salt = 977 + ai * 7919;
                    eligible.Sort((x, y) =>
                        Hash(x.cell, salt + x.dirIdx).CompareTo(Hash(y.cell, salt + y.dirIdx)));

                    int placedCount = 0;
                    foreach (var f in eligible)
                    {
                        if (placedCount >= a.maxPerRoom) break;
                        long key = FaceKey(grid.Index(f.cell), HDirs[f.dirIdx]);
                        if (usedFaces.Contains(key)) continue;
                        usedFaces.Add(key);
                        res[key] = a.prefab;
                        placedCount++;
                    }
                }
                return res;
            }

            // Unlimited assets per (type, band), cached for the pass.
            var unlimitedCache = new Dictionary<(RoomType, RoomStyle.WallBand), GameObject[]>();
            GameObject[] UnlimitedWalls(RoomType type, RoomStyle.WallBand band)
            {
                if (unlimitedCache.TryGetValue((type, band), out var cached)) return cached;
                GameObject[] result = null;
                var assets = style.WallAssetsFor(type, band);
                if (assets != null)
                {
                    var list = new List<GameObject>();
                    foreach (var a in assets)
                        if (a.maxPerRoom <= 0) list.Add(a.prefab);
                    if (list.Count > 0) result = list.ToArray();
                }
                unlimitedCache[(type, band)] = result;
                return result;
            }

            void Emit(GameObject[] slot, string slotName, Vector3 posCells, Quaternion rot, Vector3 offset)
            {
                if (slot == null || slot.Length == 0) { missing.Add(slotName); return; }
                GameObject prefab = slot[Hash(Vector3Int.RoundToInt(posCells * 4f), 11) % slot.Length];
                if (prefab == null) { missing.Add(slotName); return; }
                place(prefab, posCells, rot, offset + kit.globalVisualOffset);
            }

            // Same as Emit, but for placements needing real collision even
            // when mesh-instanced (stairs, corner pillars) — the greybox
            // doesn't provide it for these. See placeWithCollider above.
            void EmitCollider(GameObject[] slot, string slotName, Vector3 posCells, Quaternion rot, Vector3 offset)
            {
                if (slot == null || slot.Length == 0) { missing.Add(slotName); return; }
                GameObject prefab = slot[Hash(Vector3Int.RoundToInt(posCells * 4f), 11) % slot.Length];
                if (prefab == null) { missing.Add(slotName); return; }
                placeWithCollider(prefab, posCells, rot, offset + kit.globalVisualOffset);
            }

            Quaternion Yaw(Vector3Int c, bool randomize) =>
                randomize ? Quaternion.Euler(0f, 90f * (Hash(c, 23) % 4), 0f) : Quaternion.identity;

            for (int i = 0; i < grid.Length; i++)
            {
                CellType t = grid[i];
                if (t == CellType.Empty) continue;
                Vector3Int c = grid.Position(i);
                Vector3 center = new Vector3(c.x + 0.5f, c.y, c.z + 0.5f);

                // Floor — including under stair cells: the stair asset is open
                // underneath, so a floor tile hides the void beneath the ramp.
                // StairUpper cells sit above open StairLower cells, so the
                // solid-below check naturally excludes them.
                if (!Open(c + Vector3Int.down))
                    Emit(kit.floorPrefabs, "floor", center, Yaw(c, kit.randomizeFloorYaw), kit.floorOffset);

                // Ceiling.
                if (t != CellType.StairLower && !Open(c + Vector3Int.up))
                    Emit(kit.ceilingPrefabs, "ceiling",
                        center + Vector3.up, Yaw(c + Vector3Int.up, kit.randomizeCeilingYaw), kit.ceilingOffset);

                foreach (var d in HDirs)
                {
                    Vector3Int nb = c + d;
                    Vector3 facePos = center + (Vector3)d * 0.5f;

                    // Wall against solid, facing into the open cell. Rooms with
                    // a RoomStyle set: capped assets come from the per-room
                    // reservation (uniformly placed, guaranteed counts), other
                    // faces hash-pick among the set's unlimited assets, and if
                    // the set has no unlimited assets the kit's generic walls
                    // fill in. Hallways are single-story (Bottom); caps don't
                    // apply there.
                    if (!Open(nb))
                    {
                        bool emitted = false;
                        if (style != null)
                        {
                            // Stairs are usually carved as part of the hallway
                            // path (stair-aware A*, atomic macro-edges — see
                            // CLAUDE.md §3), but AllocateInteriorStairs also
                            // carves them INSIDE a room for elevated doorways.
                            // Those cells never leave the room's Cells set
                            // (only their CellType changes), so RoomAt(c)
                            // still finds the room — use its wall style, not
                            // the hallway's, when that's the case.
                            Room room = (t == CellType.Room || t == CellType.StairLower || t == CellType.StairUpper)
                                ? gen.RoomAt(c) : null;

                            if (room != null)
                            {
                                var res = GetReservations(room);
                                if (res.TryGetValue(FaceKey(i, d), out var reserved))
                                {
                                    place(reserved, facePos, Quaternion.LookRotation(-(Vector3)d),
                                          kit.wallOffset + kit.globalVisualOffset);
                                    emitted = true;
                                }
                                else
                                {
                                    var unlimited = UnlimitedWalls(room.Type, BandOf(room, c));
                                    if (unlimited != null)
                                    {
                                        Emit(unlimited, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset);
                                        emitted = true;
                                    }
                                }
                            }
                            else if (t == CellType.Hallway || t == CellType.StairLower || t == CellType.StairUpper)
                            {
                                var styled = style.HallwayWalls();
                                if (styled != null)
                                {
                                    Emit(styled, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset);
                                    emitted = true;
                                }
                            }
                            else if (t == CellType.Prison)
                            {
                                var styled = style.PrisonWalls();
                                if (styled != null)
                                {
                                    Emit(styled, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset);
                                    emitted = true;
                                }
                            }
                        }
                        if (!emitted)
                            Emit(kit.wallPrefabs, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset);
                    }

                    // Prison doorway bars: emitted once per face, owned by the
                    // prison cell, facing out into the hallway. Optional slot —
                    // silently skipped when empty, and superseded entirely by
                    // hinged prison doors when that slot is assigned.
                    if (t == CellType.Prison && grid.InBounds(nb) && grid[nb] == CellType.Hallway &&
                        kit.prisonBarsPrefabs != null && kit.prisonBarsPrefabs.Length > 0 &&
                        (kit.prisonDoorPrefabs == null || kit.prisonDoorPrefabs.Length == 0))
                        Emit(kit.prisonBarsPrefabs, "bars", facePos, Quaternion.LookRotation((Vector3)d), Vector3.zero);

                    // NOTE: archways are no longer emitted here. Their frames
                    // are thick enough to need collision, and the greybox
                    // collision shell has an open doorway at these faces — so
                    // archways spawn as real GameObjects (with their prefab
                    // colliders) in BuildArchways, in both kit modes. The
                    // Hallway↔Room detection lives on in BuildArchways and in
                    // the pillar frame-corner logic below.
                }
            }

            // Staircases — one prefab per stair record, at the foot, ascending
            // along Dir. EmitCollider: the prefab's own authored collider is
            // the real walking surface (real steps), not the greybox's
            // approximate ramp — see DungeonMesher's includeStairRamps.
            var seen = new HashSet<Stair>();
            foreach (var stair in gen.Stairs.Values)
            {
                if (!seen.Add(stair)) continue;
                Vector3Int E = stair.Entry;
                Vector3 cd = (Vector3)stair.Dir;
                Vector3 foot = new Vector3(E.x + 0.5f, E.y, E.z + 0.5f) + cd * 0.5f;
                EmitCollider(kit.stairPrefabs, "stair", foot, Quaternion.LookRotation(cd), kit.stairOffset);
            }

            // Corner pillars: classify each vertical lattice edge by the four
            // cells meeting at it, scanning each (x,z) edge as a COLUMN so a
            // post can continue upward through tall walls (e.g. above a doorway
            // in a 2-story room) instead of stopping mid-wall.
            // Openness here matches the wall emitters (any non-Empty cell is
            // open, stairs included) so posts land wherever wall faces truly
            // meet — including corridor turns into stairwells.
            //   1 solid / 3 open   -> OUTER corner
            //   3 solid / 1 open   -> INNER corner
            //   2 solid diagonal   -> two back-to-back outer corners
            //   4 open + two perpendicular doorway-frame faces -> outer corner
            //   2 solid adjacent   -> flat wall seam, no post
            bool anyOuter = kit.outerCornerPillarPrefabs != null && kit.outerCornerPillarPrefabs.Length > 0;
            bool anyInner = kit.innerCornerPillarPrefabs != null && kit.innerCornerPillarPrefabs.Length > 0;
            if (anyOuter || anyInner || style != null)
            {
                bool OpenCell(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;
                bool frameCapable = kit.archwayPrefabs != null && kit.archwayPrefabs.Length > 0;
                bool doorCapable = kit.doorPrefabs != null && kit.doorPrefabs.Length > 0;

                // Faces holding a physical door, by cell pair. Needed because
                // satellite (closet) doors are Room↔Room faces — FrameFace only
                // sees Hallway↔Room, so without this the closet-carve's fresh
                // jamb corners get posts clashing with the door frame.
                var doorFacePairs = new HashSet<(Vector3Int, Vector3Int)>();
                var beyondDoorCells = new HashSet<Vector3Int>();
                foreach (var door in gen.Doors)
                {
                    if (!door.HasDoor) continue;
                    var a = door.HallwayCell;
                    var b = door.HallwayCell + door.Direction;
                    doorFacePairs.Add((a, b));
                    doorFacePairs.Add((b, a));
                    // The side "behind" the door: the hallway for corridor
                    // doors, the closet for satellite doors (whose recorded
                    // cell is the host-room side).
                    beyondDoorCells.Add(grid.InBounds(a) && grid[a] == CellType.Hallway ? a : b);
                }

                bool FrameFace(Vector3Int pa, Vector3Int pb)
                {
                    if (!grid.InBounds(pa) || !grid.InBounds(pb)) return false;
                    CellType ta = grid[pa], tb = grid[pb];
                    return (ta == CellType.Hallway && tb == CellType.Room) ||
                           (ta == CellType.Room && tb == CellType.Hallway);
                }

                // Any face whose opening carries its own frame (arch or door).
                bool FramedOpening(Vector3Int pa, Vector3Int pb) =>
                    (frameCapable && FrameFace(pa, pb)) ||
                    (doorCapable && doorFacePairs.Contains((pa, pb)));

                bool PrisonFace(Vector3Int pa, Vector3Int pb)
                {
                    if (!grid.InBounds(pa) || !grid.InBounds(pb)) return false;
                    CellType ta = grid[pa], tb = grid[pb];
                    return (ta == CellType.Prison && tb == CellType.Hallway) ||
                           (ta == CellType.Hallway && tb == CellType.Prison);
                }

                Vector3[] quadDir =
                {
                    new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                    new Vector3(-0.5f, 0f,  0.5f), new Vector3(0.5f, 0f,  0.5f),
                };

                for (int z = 0; z <= grid.Depth; z++)
                    for (int x = 0; x <= grid.Width; x++)
                    {
                        // Per-column carry: the last post placed in this column,
                        // continued upward while the edge stays wall-like.
                        GameObject[] carrySlot = null;
                        Quaternion carryRot = Quaternion.identity;
                        string carryName = null;
                        // Once a framed opening suppresses this column, nothing
                        // may start above it (no posts floating over arch/door
                        // frames). Resets only when the column re-enters rock.
                        bool blockedByFrame = false;

                        for (int y = 0; y < grid.Height; y++)
                        {
                            var q0 = new Vector3Int(x - 1, y, z - 1);
                            var q1 = new Vector3Int(x,     y, z - 1);
                            var q2 = new Vector3Int(x - 1, y, z);
                            var q3 = new Vector3Int(x,     y, z);
                            bool o0 = OpenCell(q0), o1 = OpenCell(q1), o2 = OpenCell(q2), o3 = OpenCell(q3);
                            int openCount = (o0 ? 1 : 0) + (o1 ? 1 : 0) + (o2 ? 1 : 0) + (o3 ? 1 : 0);
                            Vector3 edge = new Vector3(x, y, z);
                            bool placed = false;

                            // Prison entrances never get corner posts — the
                            // opening (or bars, once modeled) stands alone.
                            // Unconditional, unlike the doorway toggle.
                            if (openCount >= 2 &&
                                (PrisonFace(q0, q1) || PrisonFace(q2, q3) ||
                                 PrisonFace(q0, q2) || PrisonFace(q1, q3)))
                            {
                                carrySlot = null;
                                continue;
                            }

                            if (blockedByFrame)
                            {
                                if (openCount == 0) blockedByFrame = false; // buried in rock: reset
                                else { carrySlot = null; continue; }
                            }

                            // Framed-opening faces are only computed when they
                            // can matter: for the meeting-arches case, or to
                            // exclude doorway edges when pillarsAtDoorways is
                            // off. Covers arches (Hallway↔Room) AND physical
                            // doors including satellite closets (Room↔Room).
                            bool f01 = false, f23 = false, f02 = false, f13 = false;
                            if ((frameCapable || doorCapable) && (openCount == 4 || !kit.pillarsAtDoorways))
                            {
                                f01 = FramedOpening(q0, q1); f23 = FramedOpening(q2, q3);
                                f02 = FramedOpening(q0, q2); f13 = FramedOpening(q1, q3);
                            }

                            // When pillarsAtDoorways is off and a framed opening
                            // (arch or physical door) touches this edge, don't
                            // just bail — that killed legitimate structural
                            // corners that merely sit BESIDE an arch (an L-bite
                            // corner at the end of a colonnade) and left their
                            // upper-story posts floating. Instead, reclassify
                            // with the opening's FAR side (hallway / closet)
                            // treated as solid: a jamb corner — one that exists
                            // only because of the opening — flattens into a
                            // plain wall seam and gets nothing, while a real
                            // corner survives and keeps its post at every
                            // story. Solidified cells shape the pattern but
                            // never anchor a post themselves — the arch frame
                            // is the trim on that side.
                            bool adjusting = !kit.pillarsAtDoorways && (f01 || f23 || f02 || f13);
                            bool fake0 = false, fake1 = false, fake2 = false, fake3 = false;
                            if (adjusting)
                            {
                                bool Far(Vector3Int p) =>
                                    grid.InBounds(p) &&
                                    (grid[p] == CellType.Hallway || beyondDoorCells.Contains(p));
                                fake0 = o0 && (f01 || f02) && Far(q0);
                                fake1 = o1 && (f01 || f13) && Far(q1);
                                fake2 = o2 && (f23 || f02) && Far(q2);
                                fake3 = o3 && (f23 || f13) && Far(q3);
                            }
                            bool e0 = o0 && !fake0, e1 = o1 && !fake1,
                                 e2 = o2 && !fake2, e3 = o3 && !fake3;
                            int effOpen = (e0 ? 1 : 0) + (e1 ? 1 : 0) + (e2 ? 1 : 0) + (e3 ? 1 : 0);

                            // Per-edge pillar style: an edge can touch up to
                            // four cells across rooms and hallways — the MOST
                            // SPECIAL adjacent room wins (a throne↔hallway
                            // corner uses the throne pillar). Falls back to the
                            // kit's generic posts.
                            GameObject[] outerSlot = kit.outerCornerPillarPrefabs;
                            GameObject[] innerSlot = kit.innerCornerPillarPrefabs;
                            if (style != null)
                            {
                                Room best = null; int bestScore = -1;
                                void Consider(Vector3Int p, bool open)
                                {
                                    if (!open) return;
                                    var r = RoomAtCached(p);
                                    if (r == null) return;
                                    int s = RoomStyle.Specialness(r.Type);
                                    if (s > bestScore) { bestScore = s; best = r; }
                                }
                                Consider(q0, o0); Consider(q1, o1);
                                Consider(q2, o2); Consider(q3, o3);
                                if (best != null)
                                {
                                    outerSlot = style.OuterPillarsFor(best.Type) ?? outerSlot;
                                    innerSlot = style.InnerPillarsFor(best.Type) ?? innerSlot;
                                }
                            }
                            bool edgeOuter = outerSlot != null && outerSlot.Length > 0;
                            bool edgeInner = innerSlot != null && innerSlot.Length > 0;

                            if (effOpen == 3 && edgeOuter)
                            {
                                int solid = !e0 ? 0 : !e1 ? 1 : !e2 ? 2 : 3;
                                bool anchorFake = solid == 0 ? fake0 : solid == 1 ? fake1 : solid == 2 ? fake2 : fake3;
                                if (!anchorFake)
                                {
                                    carrySlot = outerSlot;
                                    carryName = "outer corner";
                                    carryRot = Quaternion.LookRotation(-quadDir[solid].normalized);
                                    EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset);
                                    placed = true;
                                }
                            }
                            else if (effOpen == 1 && edgeInner)
                            {
                                // Inner post trims three genuinely solid blocks;
                                // skip if any "solid" is really an opening.
                                if (!(fake0 || fake1 || fake2 || fake3))
                                {
                                    int o = e0 ? 0 : e1 ? 1 : e2 ? 2 : 3;
                                    carrySlot = innerSlot;
                                    carryName = "inner corner";
                                    carryRot = Quaternion.LookRotation(quadDir[o].normalized);
                                    EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset);
                                    placed = true;
                                }
                            }
                            else if (effOpen == 2 && e0 == e3 && edgeOuter)
                            {
                                // Diagonal corner-touch: wrap each REAL solid block.
                                bool first = true;
                                for (int q = 0; q < 4; q++)
                                {
                                    bool eOpen = q == 0 ? e0 : q == 1 ? e1 : q == 2 ? e2 : e3;
                                    bool fake = q == 0 ? fake0 : q == 1 ? fake1 : q == 2 ? fake2 : fake3;
                                    if (eOpen || fake) continue;
                                    Quaternion rot = Quaternion.LookRotation(-quadDir[q].normalized);
                                    EmitCollider(outerSlot, "outer corner", edge, rot, kit.pillarOffset);
                                    if (first) { carrySlot = outerSlot; carryName = "outer corner"; carryRot = rot; first = false; }
                                    placed = true;
                                }
                            }
                            if (!placed && edgeOuter && (f01 || f23 || f02 || f13))
                            {
                                // Two frame faces meeting at perpendicular
                                // incident faces = two arch runs turning a
                                // corner at this edge. Either way there's a
                                // PIER here — the mass between the two arch
                                // jambs — protruding into open space as an
                                // outside corner. Post wraps it, facing the
                                // room: whichever of the shared cell / its
                                // diagonal is Room is the space the corner
                                // protrudes into.
                                int shared = f01 && f02 ? 0 : f01 && f13 ? 1 : f23 && f02 ? 2 : f23 && f13 ? 3 : -1;
                                if (shared >= 0)
                                {
                                    int diag = 3 - shared;
                                    Vector3Int qs = shared == 0 ? q0 : shared == 1 ? q1 : shared == 2 ? q2 : q3;
                                    Vector3Int qd = diag == 0 ? q0 : diag == 1 ? q1 : diag == 2 ? q2 : q3;
                                    int face =
                                        grid.InBounds(qs) && grid[qs] == CellType.Room ? shared :
                                        grid.InBounds(qd) && grid[qd] == CellType.Room ? diag : -1;
                                    if (face >= 0)
                                    {
                                        carrySlot = outerSlot;
                                        carryName = "outer corner";
                                        carryRot = Quaternion.LookRotation(quadDir[face].normalized);
                                        EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset);
                                        placed = true;
                                    }
                                }
                            }

                            if (!placed)
                            {
                                if (adjusting)
                                {
                                    // A framed opening suppressed this edge —
                                    // block the column so nothing floats above
                                    // the frame (fresh posts included, not just
                                    // carried ones).
                                    blockedByFrame = true;
                                    carrySlot = null;
                                }
                                else if (carrySlot != null && effOpen >= 1 && effOpen <= 3)
                                {
                                    // Vertical continuation: keep the post
                                    // running while the edge is still wall-like.
                                    EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset);
                                }
                                else
                                {
                                    carrySlot = null;
                                }
                            }
                        }
                    }
            }
        }

        /// <summary>GameObject mode: instantiate a prefab per placement.</summary>
        public static GameObject Build(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                       RoomStyle style = null)
        {
            var root = new GameObject("DungeonKit");
            root.transform.SetParent(parent, false);
            var missing = new HashSet<string>();

            Enumerate(gen, kit, missing, (prefab, posCells, rot, offset) =>
            {
                // Compose with the prefab's own root rotation — imported FBX assets
                // often carry an axis-correction rotation (e.g. -90° X from Blender).
                var go = Object.Instantiate(prefab,
                    posCells * cellSize + offset + parent.position,
                    rot * prefab.transform.rotation,
                    root.transform);
                go.isStatic = true;
            }, style);

            if (missing.Count > 0)
                Debug.LogWarning($"[DungeonKit] Missing prefab slot(s): {string.Join(", ", missing)} — those pieces were skipped.");

            return root;
        }

        public static int Hash(Vector3Int c, int salt)
        {
            unchecked
            {
                int h = c.x * 73856093 ^ c.y * 19349663 ^ c.z * 83492791 ^ salt * 374761393;
                h ^= h >> 13; h *= 1274126177; h ^= h >> 16;
                return h & 0x7fffffff;
            }
        }

        static long FaceKey(int cellIndex, Vector3Int dir)
        {
            int di = dir.x > 0 ? 0 : dir.x < 0 ? 1 : dir.z > 0 ? 2 : 3;
            return (long)cellIndex * 4 + di;
        }

        /// <summary>
        /// Spawns physical doors as real GameObjects (never instanced — they'll
        /// open, lock, or break someday). Each carries a DungeonDoorMarker with
        /// its full record so interaction systems have graph context. Call in
        /// both kit modes after geometry.
        /// </summary>
        public static GameObject BuildDoors(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                            RoomStyle style = null)
        {
            var root = new GameObject("DungeonDoors");
            root.transform.SetParent(parent, false);

            bool haveAsset = (kit.doorPrefabs != null && kit.doorPrefabs.Length > 0) || style != null;
            int wanted = 0, placedCount = 0;

            foreach (var door in gen.Doors)
            {
                if (!door.HasDoor) continue;
                wanted++;
                if (!haveAsset) continue;

                Vector3Int h = door.HallwayCell;
                Vector3Int d = door.Direction;
                Vector3 facePos = new Vector3(h.x + 0.5f + d.x * 0.5f, h.y, h.z + 0.5f + d.z * 0.5f);

                // Slot: styled by the room the door opens into (for satellite
                // closets, RoomIndex IS the closet — a Treasury entry gets the
                // treasury door); fall back to the kit's generic doors.
                GameObject[] slot = null;
                if (style != null && door.RoomIndex >= 0 && door.RoomIndex < gen.Rooms.Count)
                    slot = style.DoorsFor(gen.Rooms[door.RoomIndex].Type);
                slot ??= kit.doorPrefabs;
                if (slot == null || slot.Length == 0) continue;

                GameObject prefab = slot[Hash(h, 47) % slot.Length];
                if (prefab == null) continue;

                var go = Object.Instantiate(prefab,
                    facePos * cellSize + kit.doorOffset + kit.globalVisualOffset + parent.position,
                    Quaternion.LookRotation(-(Vector3)d) * prefab.transform.rotation,
                    root.transform);
                // Deliberately NOT static — doors are future interactives.

                var marker = go.AddComponent<DungeonDoorMarker>();
                marker.roomIndex = door.RoomIndex;
                marker.onLoopEdge = door.OnLoopEdge;
                marker.edgeA = door.EdgeA;
                marker.edgeB = door.EdgeB;
                marker.hallwayCell = h;
                marker.direction = d;
                placedCount++;
            }

            if (wanted > 0 && !haveAsset)
                Debug.LogWarning($"[DungeonKit] {wanted} entrance(s) want physical doors but the door prefab slot is empty — they render as open passages.");
            else if (placedCount > 0)
                Debug.Log($"[Dungeon] {placedCount} physical door(s) placed ({gen.Doors.Count} semantic entrances total).");

            // ---- Prison gates: one hinged door at every Prison↔Hallway face.
            // The one-opening placement rule guarantees exactly one face per
            // prison cell. Lock rolls are position-hashed: deterministic per
            // seed, no generator RNG consumed.
            if (kit.prisonDoorPrefabs != null && kit.prisonDoorPrefabs.Length > 0)
            {
                var grid = gen.Grid;
                var hDirs = new[]
                {
                    new Vector3Int( 1, 0, 0), new Vector3Int(-1, 0, 0),
                    new Vector3Int( 0, 0, 1), new Vector3Int( 0, 0,-1),
                };
                int gates = 0, lockedGates = 0;
                bool warnedNoHinge = false;

                for (int i = 0; i < grid.Length; i++)
                {
                    if (grid[i] != CellType.Prison) continue;
                    Vector3Int p = grid.Position(i);
                    foreach (var d in hDirs)
                    {
                        Vector3Int nb = p + d;
                        if (!grid.InBounds(nb) || grid[nb] != CellType.Hallway) continue;

                        GameObject prefab = kit.prisonDoorPrefabs[Hash(p, 53) % kit.prisonDoorPrefabs.Length];
                        if (prefab == null) continue;

                        Vector3 facePos = new Vector3(p.x + 0.5f + d.x * 0.5f, p.y, p.z + 0.5f + d.z * 0.5f);
                        var go = Object.Instantiate(prefab,
                            facePos * cellSize + kit.prisonDoorOffset + kit.globalVisualOffset + parent.position,
                            Quaternion.LookRotation((Vector3)d) * prefab.transform.rotation,
                            root.transform);
                        // Not static — gates are interactive.

                        bool locked = Hash(p, 59) % 10000 < Mathf.RoundToInt(kit.prisonDoorLockChance * 10000f);
                        var hinged = go.GetComponentInChildren<HingedDoor>();
                        if (hinged != null)
                        {
                            hinged.locked = locked;
                        }
                        else if (!warnedNoHinge)
                        {
                            Debug.LogWarning("[DungeonKit] Prison door prefab has no HingedDoor component — gates will be static decoration (and lock rolls do nothing).");
                            warnedNoHinge = true;
                        }

                        var marker = go.AddComponent<PrisonDoorMarker>();
                        marker.prisonIndex = gen.PrisonCells.FindIndex(b => b.Contains(p));
                        marker.prisonCell = p;
                        marker.direction = d;

                        gates++;
                        if (locked) lockedGates++;
                    }
                }

                if (gates > 0)
                    Debug.Log($"[Dungeon] {gates} prison gate(s) placed, {lockedGates} locked.");
            }

            return root;
        }

        /// <summary>
        /// Spawns archways as real GameObjects (with their prefab colliders) at
        /// every Hallway↔Room face that isn't already occupied by a physical
        /// door. Thick arch frames need collision the greybox shell doesn't
        /// provide (its doorway is a full open face), so — like doors and gates
        /// — they can't be instanced decoration. Call in both kit modes after
        /// geometry.
        /// </summary>
        public static GameObject BuildArchways(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                               InstancedDungeonRenderer instancer = null, RoomStyle style = null)
        {
            var root = new GameObject("DungeonArchways");
            root.transform.SetParent(parent, false);
            bool haveAny = (kit.archwayPrefabs != null && kit.archwayPrefabs.Length > 0) || style != null;
            if (!haveAny) return root;

            var grid = gen.Grid;

            // Faces already claimed by a physical door — the door asset frames
            // its own opening, so no arch there.
            var doorFaceKeys = new HashSet<long>();
            foreach (var door in gen.Doors)
                if (door.HasDoor)
                    doorFaceKeys.Add(FaceKey(grid.Index(door.HallwayCell), door.Direction));

            var hDirs = new[]
            {
                new Vector3Int( 1, 0, 0), new Vector3Int(-1, 0, 0),
                new Vector3Int( 0, 0, 1), new Vector3Int( 0, 0,-1),
            };
            int count = 0;

            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] != CellType.Hallway) continue;
                Vector3Int c = grid.Position(i);
                foreach (var d in hDirs)
                {
                    Vector3Int nb = c + d;
                    if (!grid.InBounds(nb) || grid[nb] != CellType.Room) continue;
                    if (doorFaceKeys.Contains(FaceKey(i, d))) continue;

                    // Slot: the room this opening leads into decides the style
                    // (a Throne entrance gets the throne archway); fall back to
                    // the kit's generic archways.
                    GameObject[] slot = null;
                    if (style != null)
                    {
                        var intoRoom = gen.RoomAt(nb);
                        if (intoRoom != null) slot = style.ArchwaysFor(intoRoom.Type);
                    }
                    slot ??= kit.archwayPrefabs;
                    if (slot == null || slot.Length == 0) continue;

                    GameObject prefab = slot[Hash(c, 37) % slot.Length];
                    if (prefab == null) continue;

                    Vector3 facePos = new Vector3(c.x + 0.5f + d.x * 0.5f, c.y, c.z + 0.5f + d.z * 0.5f);
                    Vector3 worldPos = facePos * cellSize + kit.archwayOffset + kit.globalVisualOffset + parent.position;
                    Quaternion worldRot = Quaternion.LookRotation(-(Vector3)d);

                    if (instancer != null)
                    {
                        // Split: arch MESH batches; a GameObject keeps the collider
                        // (thick frames need collision the greybox doesn't provide).
                        PropInstancer.PlaceProps(instancer, prefab,
                            new[] { new PropPlacement { position = worldPos, rotation = worldRot } },
                            PropTier.StaticCollider, cellSize, root.transform);
                    }
                    else
                    {
                        Object.Instantiate(prefab,
                            worldPos,
                            worldRot * prefab.transform.rotation,
                            root.transform);
                    }
                    count++;
                }
            }

            if (count > 0)
                Debug.Log($"[Dungeon] {count} archway(s) placed.");
            return root;
        }

        /// <summary>
        /// Spawns free-standing interior columns at the lattice points the
        /// generator planned (grand rooms). The column prefab is ONE CELL tall;
        /// segments are STACKED to span floor→ceiling, so a 2-story hall gets a
        /// 2-segment column with no stretching. Meshes batch through the
        /// instancer (StaticCollider tier: instanced mesh + collider GameObject);
        /// in PrefabKit mode they spawn as full GameObjects. Columns sit at
        /// cell corners and occupy no grid cells — the floor stays walkable and
        /// collision comes from the prefab's collider.
        /// </summary>
        public static GameObject BuildInteriorColumns(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                                      InstancedDungeonRenderer instancer = null)
        {
            var root = new GameObject("DungeonColumns");
            root.transform.SetParent(parent, false);
            if (kit.interiorColumnPrefabs == null || kit.interiorColumnPrefabs.Length == 0) return root;
            if (gen.ColumnPoints.Count == 0) return root;

            int segments = 0;
            foreach (var (lattice, yFloor, heightCells) in gen.ColumnPoints)
            {
                GameObject prefab = kit.interiorColumnPrefabs[Hash(lattice, 53) % kit.interiorColumnPrefabs.Length];
                if (prefab == null) continue;

                // Lattice points are cell-CORNER coordinates, so the world
                // position is lattice * cellSize directly (no half-cell shift).
                Vector3 basePos = new Vector3(lattice.x, yFloor, lattice.z) * cellSize
                                  + kit.interiorColumnOffset + kit.globalVisualOffset + parent.position;

                // Deterministic yaw variety in 90° steps — columns are usually
                // symmetric, but this hides texture repetition for free.
                Quaternion rot = Quaternion.Euler(0f, 90f * (Hash(lattice, 91) % 4), 0f);

                for (int seg = 0; seg < heightCells; seg++)
                {
                    Vector3 pos = basePos + Vector3.up * (seg * cellSize);
                    if (instancer != null)
                    {
                        PropInstancer.PlaceProps(instancer, prefab,
                            new[] { new PropPlacement { position = pos, rotation = rot } },
                            PropTier.StaticCollider, cellSize, root.transform);
                    }
                    else
                    {
                        Object.Instantiate(prefab, pos, rot * prefab.transform.rotation, root.transform);
                    }
                    segments++;
                }
            }

            if (segments > 0)
                Debug.Log($"[Dungeon] {gen.ColumnPoints.Count} interior column(s) placed ({segments} segments).");
            return root;
        }
    }

    /// <summary>
    /// Attached to every spawned prison gate — the future lockpick system's
    /// hook. Locked state itself lives on the HingedDoor component.
    /// </summary>
    public class PrisonDoorMarker : MonoBehaviour
    {
        public int prisonIndex;       // index into DungeonGenerator.PrisonCells
        public Vector3Int prisonCell; // the cell behind this gate
        public Vector3Int direction;  // prison -> hallway
    }

    /// <summary>
    /// Attached to every spawned door. Everything a future interaction system
    /// needs: which room this door guards, which graph edge it belongs to, and
    /// whether that edge is a loop (shortcut — knock-down candidate) or MST
    /// (required route — lock-and-key candidate).
    /// </summary>
    public class DungeonDoorMarker : MonoBehaviour
    {
        public int roomIndex;
        public bool onLoopEdge;
        public int edgeA, edgeB;
        public Vector3Int hallwayCell;
        public Vector3Int direction; // hallway -> room
    }
}