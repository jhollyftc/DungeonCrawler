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
        [Tooltip("Wall-mounted ladder for drop-in elevated entrances (no room for a staircase). Author BASE-ORIGIN (globalVisualOffset is NOT applied), one cell (3m) tall — segments stack per story. Thin, back at the wall plane; give it a solid collider plus a trigger box with a LadderClimbZone covering the climbable front (extend the trigger ~0.5m above the opening so the player keeps climb control while cresting). Optional — skipped if empty (the entrance stays a one-way drop).")]
        public GameObject[] ladderPrefabs; // optional — skipped if empty
        [Tooltip("Nudge applied in the LADDER'S OWN frame, not world space: Z = away from the mounting wall, X = along it, Y = up. Wall-relative because that's the only way one value can be correct on all four wall directions — a world-space offset embeds the ladder in the masonry on the opposite wall.")]
        public Vector3 ladderOffset;
        [Tooltip("Bridge deck spanning a room PIT, one cell long, laid flat at the room's floor level. A KIT piece rather than a prop deliberately: the generator's connectivity check counts on the crossing existing, and a prop could decline to place while a cell-level flood-fill could never see it either way. MUST have a solid collider — that collider is what makes the deck walkable AND what bakes it into the runtime navmesh so NPCs cross it. If the collider is a MeshCollider its mesh MUST be Read/Write Enabled, or the bridge is silently skipped from the bake in a PLAYER BUILD while working perfectly in the editor (the same trap that once removed stairs from builds). Optional — skipped if empty, which leaves pits uncrossable.")]
        public GameObject[] bridgePrefabs; // optional — skipped if empty
        [Tooltip("Nudge applied to a bridge deck in world space (Y is the useful axis — sink or raise the deck relative to the floor plane). Unlike ladderOffset this is NOT rotated: a bridge lies flat and is placed axis-aligned, so there is no wall frame to be relative to.")]
        public Vector3 bridgeOffset;
        [Header("Per-room emissive tinting (optional)")]
        [Tooltip("The GLOWING material used by kit pieces with an emissive element (candle walls, braziers). When set, every instanced kit piece using this exact material gets a cached variant tinted to its room's TORCH COLOUR — so a shrine's candles burn the same cold blue as its torches, exactly like the fog and flame VFX already do. Leave empty to disable tinting entirely (pieces render with the authored material).\n\nThis is needed because a MaterialPropertyBlock can't work on the instanced path: nothing renders through the prefab's MeshRenderer, so an EmissionController on a kit wall does nothing. Cost is one extra batch per distinct colour in use — bounded by the palette, not by instance count.")]
        public Material emissiveMaterial;
        [Tooltip("Shader property tinted on the variant. _EmissionColor for URP/Lit.")]
        public string emissiveProperty = "_EmissionColor";
        [Tooltip("Log each emissive variant as it's built — shader, resolved colour, whether the property write landed, GI flags, and GPU-instancing state. Turn on when the tint applies but nothing glows.")]
        public bool debugEmissive = false;
        [Tooltip("Multiplies the room's torch colour before it's written as emission. MUST usually be > 1 to actually GLOW: the torch palette's colours are LDR-range (components <= 1), and emission only blooms above 1 — at 1 the candle is merely tinted, not lit. Bloom must also be enabled in the post-process Volume. 2.4 was the hand-tuned value on the original EmissionController.")]
        public float emissiveIntensity = 2.4f;

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
        /// <summary>
        /// `cell` is the OWNING grid cell — the open cell a piece belongs to, which
        /// `posCells` alone cannot recover: a wall's position sits ON the face between
        /// two cells, so flooring it lands on the solid neighbour or the open one
        /// depending which way the face points. Consumers need it to resolve the piece's
        /// room (and therefore its RoomStyle) for per-room visual variation — e.g.
        /// tinting a candle wall's emissive material to that room's torch colour.
        /// For pieces that sit BETWEEN cells by nature (corner pillars on a lattice
        /// edge) it's a representative adjacent cell, not an exact owner.
        /// </summary>
        public delegate void PlaceCallback(GameObject prefab, Vector3 posCells, Quaternion rot, Vector3 offsetMeters, Vector3Int cell);

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
                                     RoomStyle style = null, PlaceCallback placeWithCollider = null,
                                     WallFaceRegistry wallFaces = null)
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
            var reservations = new Dictionary<Room, Dictionary<long, RoomStyle.WallAsset>>();

            RoomStyle.WallBand BandOf(Room room, Vector3Int cell)
            {
                if (room.Bounds.size.y > 1)
                {
                    if (cell.y == room.Bounds.yMax - 1) return RoomStyle.WallBand.Top;
                    if (cell.y > room.Bounds.yMin) return RoomStyle.WallBand.Middle;
                }
                return RoomStyle.WallBand.Bottom;
            }

            Dictionary<long, RoomStyle.WallAsset> GetReservations(Room room)
            {
                if (reservations.TryGetValue(room, out var res)) return res;
                res = new Dictionary<long, RoomStyle.WallAsset>();
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
                        res[key] = a;
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

            // Returns the picked prefab (null if the slot was empty) so wall
            // emission can record per-face restrictions for it.
            GameObject Emit(GameObject[] slot, string slotName, Vector3 posCells, Quaternion rot, Vector3 offset, Vector3Int cell)
            {
                if (slot == null || slot.Length == 0) { missing.Add(slotName); return null; }
                GameObject prefab = slot[Hash(Vector3Int.RoundToInt(posCells * 4f), 11) % slot.Length];
                if (prefab == null) { missing.Add(slotName); return null; }
                place(prefab, posCells, rot, offset + kit.globalVisualOffset, cell);
                return prefab;
            }

            // Same as Emit, but for placements needing real collision even
            // when mesh-instanced (stairs, corner pillars) — the greybox
            // doesn't provide it for these. See placeWithCollider above.
            void EmitCollider(GameObject[] slot, string slotName, Vector3 posCells, Quaternion rot, Vector3 offset, Vector3Int cell)
            {
                if (slot == null || slot.Length == 0) { missing.Add(slotName); return; }
                GameObject prefab = slot[Hash(Vector3Int.RoundToInt(posCells * 4f), 11) % slot.Length];
                if (prefab == null) { missing.Add(slotName); return; }
                placeWithCollider(prefab, posCells, rot, offset + kit.globalVisualOffset, cell);
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
                // NeedsSlabBetween rather than a bare "is below solid" — an open cell
                // below only means "skip the floor" when it's the SAME space (a tall
                // room). A closet with a hallway above it is two different spaces; see
                // the generator's NeedsSlabBetween. Must match DungeonMesher exactly or
                // the visible floor and the collision floor disagree.
                // Per-space surface sets, resolved once and shared by the floor and
                // ceiling below. Owner is decided the same way stair walls and stairs are:
                // RoomAt first (a stair cell carved inside a room stays in Room.Cells, so
                // it correctly inherits that room's floor), then the cell type. Each falls
                // back to the kit's generic, so a partially authored style still renders.
                GameObject[] floorSlot = kit.floorPrefabs;
                GameObject[] ceilingSlot = kit.ceilingPrefabs;
                if (style != null)
                {
                    Room surfRoom = RoomAtCached(c);
                    // PIT FIRST. A pit cell resolves to its owning room through RoomAt's PitAt
                    // fallback (which is what styles a chasm as part of its room rather than a
                    // generic hole), so the room branch below would otherwise always win and a
                    // pit could never have a look of its own. Unauthored falls through to
                    // exactly that room styling, so this changes nothing until it's filled in.
                    if (gen.PitAt(c) != null)
                    {
                        floorSlot = style.PitFloors() ?? (surfRoom != null
                            ? style.FloorsFor(surfRoom.Type) ?? floorSlot : floorSlot);
                        // No pit ceiling: the top of a pit is the hole you fell through, and
                        // NeedsSlabBetween suppresses it. Kept as the room's so a 2-deep pit's
                        // internal levels stay consistent if that ever changes.
                        if (surfRoom != null) ceilingSlot = style.CeilingsFor(surfRoom.Type) ?? ceilingSlot;
                    }
                    else if (surfRoom != null)
                    {
                        floorSlot = style.FloorsFor(surfRoom.Type) ?? floorSlot;
                        ceilingSlot = style.CeilingsFor(surfRoom.Type) ?? ceilingSlot;
                    }
                    else if (t == CellType.Prison)
                    {
                        floorSlot = style.PrisonFloors() ?? floorSlot;
                        ceilingSlot = style.PrisonCeilings() ?? ceilingSlot;
                    }
                    else
                    {
                        floorSlot = style.HallwayFloors() ?? floorSlot;
                        ceilingSlot = style.HallwayCeilings() ?? ceilingSlot;
                    }
                }

                if (gen.NeedsSlabBetween(c + Vector3Int.down, c))
                    Emit(floorSlot, "floor", center, Yaw(c, kit.randomizeFloorYaw), kit.floorOffset, c);

                // Ceiling.
                if (t != CellType.StairLower && gen.NeedsSlabBetween(c, c + Vector3Int.up))
                    Emit(ceilingSlot, "ceiling",
                        center + Vector3.up, Yaw(c + Vector3Int.up, kit.randomizeCeilingYaw), kit.ceilingOffset, c);

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
                        GameObject placedWall = null;
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

                            // PIT INTERIOR takes its own walls if authored. Checked before the
                            // room branch because RoomAt resolves a pit cell TO its room (the
                            // PitAt fallback), so the room branch would otherwise always claim
                            // it. Unauthored (null) falls straight through to the room's walls,
                            // which is the existing behaviour — so this is inert until filled.
                            //
                            // Feature reservations and band logic are deliberately skipped: a
                            // pit has no bands (it is one course of rough wall) and a fireplace
                            // down a chasm is not a thing worth supporting.
                            var pitWalls = gen.PitAt(c) != null && style != null ? style.PitWalls() : null;
                            if (pitWalls != null)
                            {
                                placedWall = Emit(pitWalls, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                emitted = true;
                            }
                            else if (room != null)
                            {
                                var res = GetReservations(room);
                                if (res.TryGetValue(FaceKey(i, d), out var reserved))
                                {
                                    place(reserved.prefab, facePos, Quaternion.LookRotation(-(Vector3)d),
                                          kit.wallOffset + kit.globalVisualOffset, c);
                                    placedWall = reserved.prefab;
                                    emitted = true;
                                    // A labeled feature wall (fireplace etc.) —
                                    // NearWallAsset props with a matching Host
                                    // Label attach beside it. Unlabeled capped
                                    // assets are NOT hosts.
                                    if (!string.IsNullOrEmpty(reserved.featureLabel))
                                        wallFaces?.RecordFeature(c, d, reserved.featureLabel);
                                }
                                else
                                {
                                    var unlimited = UnlimitedWalls(room.Type, BandOf(room, c));
                                    if (unlimited != null)
                                    {
                                        placedWall = Emit(unlimited, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                        emitted = true;
                                    }
                                }
                            }
                            else if (t == CellType.Hallway || t == CellType.StairLower || t == CellType.StairUpper)
                            {
                                var styled = style.HallwayWalls();
                                if (styled != null)
                                {
                                    placedWall = Emit(styled, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                    emitted = true;
                                }
                            }
                            else if (t == CellType.Prison)
                            {
                                var styled = style.PrisonWalls();
                                if (styled != null)
                                {
                                    placedWall = Emit(styled, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                    emitted = true;
                                }
                            }
                        }
                        if (!emitted)
                            Emit(kit.wallPrefabs, "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);

                        // Record this face's restrictions for the torch/prop
                        // placers (wall real estate). Kit generic walls carry
                        // no WallAsset metadata — they allow everything.
                        if (wallFaces != null && style != null && placedWall != null)
                        {
                            style.WallFlagsFor(placedWall, out bool allowProps, out bool allowTorch);
                            if (!allowProps || !allowTorch)
                                wallFaces.Record(i, d, allowProps, allowTorch);
                        }
                    }

                    // Prison doorway bars: emitted once per face, owned by the
                    // prison cell, facing out into the hallway. Optional slot —
                    // silently skipped when empty, and superseded entirely by
                    // hinged prison doors when that slot is assigned.
                    if (t == CellType.Prison && grid.InBounds(nb) && grid[nb] == CellType.Hallway &&
                        kit.prisonBarsPrefabs != null && kit.prisonBarsPrefabs.Length > 0 &&
                        (kit.prisonDoorPrefabs == null || kit.prisonDoorPrefabs.Length == 0))
                        Emit(kit.prisonBarsPrefabs, "bars", facePos, Quaternion.LookRotation((Vector3)d), Vector3.zero, c);

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
                // Which staircase this is depends on WHOSE it is. Interior stairs are
                // carved inside a room — their cells never leave Room.Cells, only the
                // CellType changes — so RoomAt resolves the owner, exactly the rule stair
                // WALLS already follow (§7). A corridor stair belongs to no room and keeps
                // the kit's generic.
                //
                // The stair's own cells are E+dir and E+dir*2; E itself is the cell BEFORE
                // the flight, so it's checked last and only as a fallback — for a stair
                // rising out of a room into a corridor, E may be the corridor side.
                GameObject[] stairSlot = kit.stairPrefabs;
                if (style != null)
                {
                    Vector3Int d = stair.Dir;
                    Room best = null; int bestScore = -1;
                    void ConsiderStairRoom(Vector3Int p)
                    {
                        var r = RoomAtCached(p);
                        if (r == null) return;
                        int s = RoomStyle.Specialness(r.Type);
                        if (s > bestScore) { bestScore = s; best = r; }
                    }
                    // Specialness breaks a tie the same way pillars do at an edge touching
                    // several rooms — a flight shared between a throne room and a generic
                    // one should read as the throne room's.
                    ConsiderStairRoom(E + d);
                    ConsiderStairRoom(E + d * 2);
                    ConsiderStairRoom(E + d + Vector3Int.up);
                    ConsiderStairRoom(E + d * 2 + Vector3Int.up);
                    if (best == null) ConsiderStairRoom(E);

                    if (best != null) stairSlot = style.StairsFor(best.Type) ?? stairSlot;
                }

                EmitCollider(stairSlot, "stair", foot, Quaternion.LookRotation(cd), kit.stairOffset, E);
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

                // MUST match BuildArchways' definition of an opening, or the classifier
                // and the arches disagree: an arch appears while the corner posts still
                // think the face is a plain wall, so posts land through the arch frame.
                // Room MEMBERSHIP, not CellType — an interior staircase keeps its cells
                // in Room.Cells and only changes their CellType (§7), so a CellType test
                // misses every doorway with a stair through it.
                bool RoomMember(Vector3Int p)
                {
                    if (!grid.InBounds(p) || grid[p] == CellType.Empty) return false;
                    return gen.RoomAt(p) != null;
                }
                bool CorridorMember(Vector3Int p)
                {
                    if (!grid.InBounds(p) || grid[p] == CellType.Empty) return false;
                    return grid[p] != CellType.Prison && gen.RoomAt(p) == null;
                }

                bool FrameFace(Vector3Int pa, Vector3Int pb)
                {
                    if (!grid.InBounds(pa) || !grid.InBounds(pb)) return false;
                    return (CorridorMember(pa) && RoomMember(pb)) ||
                           (RoomMember(pa) && CorridorMember(pb));
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

                            // PIT INTERIORS never get corner posts either. A pit cell is a room
                            // cell with solid neighbours, so it forms textbook wall corners and
                            // the classifier happily posts them — but an architectural pillar
                            // down a chasm reads as a room that happens to be sunken rather than
                            // as broken ground, and it fights whatever rough stonework pitWalls
                            // is authored with. Same reasoning and same unconditional shape as
                            // the prison rule above.
                            //
                            // Fourth consumer to need this check (props/zones, InteriorFloorCell,
                            // interior columns, torches were the others): anything that reasons
                            // about room cells has to be told a pit is not ordinary floor. See
                            // §12's category rule.
                            if (gen.PitAt(q0) != null || gen.PitAt(q1) != null ||
                                gen.PitAt(q2) != null || gen.PitAt(q3) != null)
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
                                    EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset, q0);
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
                                    EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset, q0);
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
                                    EmitCollider(outerSlot, "outer corner", edge, rot, kit.pillarOffset, q0);
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
                                        EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset, q0);
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
                                    EmitCollider(carrySlot, carryName, edge, carryRot, kit.pillarOffset, q0);
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
                                       RoomStyle style = null, WallFaceRegistry wallFaces = null)
        {
            var root = new GameObject("DungeonKit");
            root.transform.SetParent(parent, false);
            var missing = new HashSet<string>();

            // PrefabKit mode instantiates whole prefabs, so per-room emissive tinting
            // doesn't apply here — the prefab keeps its own MeshRenderer, and an
            // EmissionController on it works normally. `cell` is unused for that reason.
            Enumerate(gen, kit, missing, (prefab, posCells, rot, offset, cell) =>
            {
                // Compose with the prefab's own root rotation — imported FBX assets
                // often carry an axis-correction rotation (e.g. -90° X from Blender).
                var go = Object.Instantiate(prefab,
                    posCells * cellSize + offset + parent.position,
                    rot * prefab.transform.rotation,
                    root.transform);
                go.isStatic = true;
            }, style, null, wallFaces);

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

            // An opening is corridor-side ↔ ROOM-MEMBER, tested by room MEMBERSHIP, not
            // by CellType. This used to require `Hallway → Room` literally, which misses
            // real doorways: an interior staircase carved inside a room keeps its cells
            // in Room.Cells and only changes their CellType to StairLower/StairUpper (§7),
            // so a doorway with a stair through it read as "not a Room" and got NO ARCH
            // (real bug — an upper-level hallway opening with a staircase below it). The
            // generator itself defines doorways this way: RecordDoor tests
            // room.Contains(hallwayCell + d), never a CellType. Prisons are excluded —
            // they frame their own openings with bars.
            var inRoom = new bool[grid.Length];
            foreach (var room in gen.Rooms)
                foreach (var p in room.Bounds.allPositionsWithin)
                    if (grid.InBounds(p) && room.Contains(p))
                        inRoom[grid.Index(p)] = true;

            for (int i = 0; i < grid.Length; i++)
            {
                CellType here = grid[i];
                if (here == CellType.Empty || here == CellType.Prison) continue;
                if (inRoom[i]) continue;                       // corridor side only

                Vector3Int c = grid.Position(i);
                foreach (var d in hDirs)
                {
                    Vector3Int nb = c + d;
                    if (!grid.InBounds(nb) || grid[nb] == CellType.Empty) continue;
                    if (!inRoom[grid.Index(nb)]) continue;     // must open INTO a room
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

        /// <summary>
        /// Wall-mounted ladders for drop-in elevated entrances (allocated by
        /// AllocateLadders). Same split as columns: mesh instanced, collider +
        /// LadderClimbZone GameObject kept (StaticCollider tier — PropInstancer
        /// preserves custom components). One prefab segment per story, stacked.
        /// Ladder prefabs are authored BASE-ORIGIN — no globalVisualOffset,
        /// same convention as props (golden rule 2).
        /// </summary>
        public static GameObject BuildLadders(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                              InstancedDungeonRenderer instancer = null)
        {
            var root = new GameObject("DungeonLadders");
            root.transform.SetParent(parent, false);
            if (kit.ladderPrefabs == null || kit.ladderPrefabs.Length == 0) return root;
            if (gen.Ladders.Count == 0) return root;

            int count = 0;
            foreach (var lad in gen.Ladders)
            {
                GameObject prefab = kit.ladderPrefabs[Hash(lad.BaseCell, 131) % kit.ladderPrefabs.Length];
                if (prefab == null) continue;

                // Foot of the ladder: on the floor, against the mount wall's
                // face; forward points away from the wall (into the room).
                Vector3 face = new Vector3(lad.BaseCell.x + 0.5f + lad.WallDir.x * 0.5f,
                                           lad.BaseCell.y,
                                           lad.BaseCell.z + 0.5f + lad.WallDir.z * 0.5f) * cellSize;
                Quaternion rot = Quaternion.LookRotation(-(Vector3)lad.WallDir);

                for (int seg = 0; seg < lad.HeightCells; seg++)
                {
                    // ladderOffset is applied in the LADDER'S OWN frame (rot * offset),
                    // not world space. A ladder's offset is inherently directional — its
                    // whole job is "how far off the wall" — so a world-space nudge only
                    // worked on walls that happened to face that world axis: the opposite
                    // wall got the ladder pushed INTO the masonry and perpendicular walls
                    // got it slid sideways along the face (real bug). rot is yaw-only, so
                    // Y still means straight up; X now means along the wall and Z away
                    // from it, which is what the field reads as.
                    Vector3 pos = face + Vector3.up * (seg * cellSize)
                                + rot * kit.ladderOffset + parent.position;
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
                }
                count++;
            }

            if (count > 0)
                Debug.Log($"[Dungeon] {count} ladder(s) placed.");
            return root;
        }

        /// <summary>
        /// Bridge decks spanning room pits, at the room's floor level.
        ///
        /// A KIT PIECE, not a prop, and that distinction is the whole reason a pit is allowed
        /// to sever a room. The generator's connectivity flood-fill treats bridge cells as
        /// walkable and proves every doorway still reachable BEFORE committing the pit — which
        /// is only sound if the crossing cannot fail to appear. A prop routes through the
        /// occupancy system and can decline; worse, cell-level connectivity can never see a
        /// prop at all (§10, cell connectivity != navmesh connectivity), so the generator would
        /// be reasoning about a crossing it has no model of.
        ///
        /// The deck's COLLIDER does double duty: it is what the player walks on, and it is what
        /// bakes the span into the runtime navmesh, so NPCs cross bridges with no AI work at
        /// all. That is the sharp difference from ladders, which are invisible to NavMeshAgent
        /// because climbing is scripted rather than walkable geometry.
        /// </summary>
        public static GameObject BuildBridges(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                              InstancedDungeonRenderer instancer = null)
        {
            var root = new GameObject("DungeonBridges");
            root.transform.SetParent(parent, false);
            if (kit.bridgePrefabs == null || kit.bridgePrefabs.Length == 0) return root;
            if (gen.Pits.Count == 0) return root;

            int count = 0;
            foreach (var pit in gen.Pits)
            {
                foreach (var c in pit.BridgeCells)
                {
                    GameObject prefab = kit.bridgePrefabs[Hash(c, 149) % kit.bridgePrefabs.Length];
                    if (prefab == null) continue;

                    // Flat at the room's floor plane, centred on the cell. NO globalVisualOffset:
                    // a bridge is placed at nominal grid coordinates like a prop, not like a kit
                    // wall (golden rule 2 — the offset is a kit-mesh quirk everything else must
                    // ignore, and double-correcting puts the deck 1.5m in the air).
                    Vector3 pos = new Vector3((c.x + 0.5f) * cellSize,
                                              c.y * cellSize,
                                              (c.z + 0.5f) * cellSize)
                                  + kit.bridgeOffset + parent.position;

                    // Oriented ACROSS the chasm. Placed with identity it was correct only when
                    // the pit happened to run the right way and lay parallel to the gap
                    // otherwise — author the deck facing +Z and this puts it right on all four
                    // orientations.
                    Quaternion rot = Quaternion.LookRotation((Vector3)pit.CrossDirection);

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
                    count++;
                }
            }

            if (count > 0)
                Debug.Log($"[Dungeon] {count} bridge deck(s) placed across {gen.Pits.Count} pit(s).");
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