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
    /// <summary>
    /// A kit prefab with a relative FREQUENCY.
    ///
    /// The plain `GameObject[]` slots everywhere else are picked with `Hash(cell) % length`, so
    /// every entry is EXACTLY equally likely and the only way to make one rarer is to list
    /// another twice. `RoomStyle.WallAsset.weight` already exists for the same reason on walls
    /// (§7); this is the same dial for kit slots that have no WallAsset to hang it on.
    ///
    /// NB frequency and DISTRIBUTION are different questions. Weight makes a variant rarer, not
    /// CLUSTERED — a per-cell hash is white noise, so a weighted pool still scatters evenly.
    /// Clustering needs a smooth field, which is what `ValueNoise` + `noiseRange` do for walls
    /// and what these would need too if a crawlway ever wants a "flooded section".
    /// </summary>
    [System.Serializable]
    public struct WeightedPrefab
    {
        public GameObject prefab;
        [Tooltip("Relative frequency within its list. 3 against 1 means three times as often. 0 mutes this entry without deleting it.")]
        [Min(0f)] public float weight;
    }

    [System.Serializable]
    public class DungeonKit
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // ORDERED BY ORIGIN CONVENTION, not alphabetically or by when it was added.
        // The kit has TWO independent conventions and getting either backwards puts a piece
        // half a cell out — the single most common authoring bug in this project (golden
        // rule 2). The grouping below is the documentation:
        //
        //   1. Does globalVisualOffset apply?  KIT-FRAME pieces yes, BASE-ORIGIN pieces no.
        //   2. Is the per-piece nudge rotated into the piece's own frame, or world space?
        //
        // They are genuinely independent: a lintel is kit-frame with a ROTATED nudge, a
        // bridge is base-origin with a WORLD nudge. Each field below says which it is.
        // ─────────────────────────────────────────────────────────────────────────────

        [Header("Shell — every open cell gets these")]
        public GameObject[] wallPrefabs;
        public GameObject[] floorPrefabs;
        public GameObject[] ceilingPrefabs;
        public GameObject[] stairPrefabs;

        [Header("Openings — arches, doors, prison gates")]
        [Tooltip("Open arch frame at hallway↔room openings (colonnades and doorless entrances). Spawned as real GameObjects with their prefab colliders — thick arches need collision the greybox doesn't provide, so give the prefab a collider.")]
        public GameObject[] archwayPrefabs;    // optional — skipped if empty
        [Tooltip("Physical door (arch + wooden door). Placed ONLY at semantic entrances the generator flagged with HasDoor. Always spawned as real GameObjects (never instanced) so they can open/break later — give the prefab a collider.")]
        public GameObject[] doorPrefabs;       // optional — skipped if empty
        public GameObject[] prisonBarsPrefabs; // optional — skipped if empty, superseded by prisonDoorPrefabs
        [Tooltip("Hinged prison gate (needs a HingedDoor set up like the wooden door). Placed at every prison entrance as a real GameObject. When assigned, the static bars slot is ignored.")]
        public GameObject[] prisonDoorPrefabs; // optional — skipped if empty
        [Tooltip("Chance a prison gate spawns locked (deterministic per seed). Lockpicking comes later.")]
        [Range(0f, 1f)] public float prisonDoorLockChance = 0.15f;

        [Header("Pillars & columns")]
        [Tooltip("Posts wrapping convex block corners that jut into open space (corridor turns). Forward faces diagonally away from the solid corner being wrapped.")]
        public GameObject[] outerCornerPillarPrefabs; // optional — skipped if empty
        [Tooltip("Posts for concave room/corridor inside corners, if you model one later. Forward faces diagonally into the open cell.")]
        public GameObject[] innerCornerPillarPrefabs; // optional — skipped if empty
        [Tooltip("Place corner posts on edges that touch a doorway/arch face (jamb corners and meeting-arch corners). Off = arches stand alone.")]
        public bool pillarsAtDoorways = false;
        [Tooltip("Free-standing interior column segment (one cell / 3m tall, stacked to reach the ceiling). Placed at lattice points in grand rooms. Give it a collider — the floor cells stay walkable, so collision comes from the prefab.")]
        public GameObject[] interiorColumnPrefabs; // optional — skipped if empty
        [Tooltip("Nudge for a column segment. KIT-FRAME (globalVisualOffset applies) and WORLD-SPACE — a column is placed axis-aligned at a lattice point, so there is no piece frame to be relative to.")]
        public Vector3 interiorColumnOffset;

        [Tooltip("BANDED column segments — a base / shaft / capital set, tagged with the storeys each piece may occupy. Same Bottom/Middle/Top vocabulary as banded WALLS, and a single-storey space counts as Bottom.\n\nLeave EMPTY to keep the old behaviour: Interior Column Prefabs picked once and repeated all the way up. When this has entries it takes over, and a band with nothing eligible falls back to the unbanded list rather than borrowing another band's pieces — the strict-band rule that stops a capital appearing at floor level.")]
        public RoomStyle.BandedAsset[] interiorColumnBands;

        [Tooltip("BANDED corner posts, same idea as the column bands above: a piece that only reads well at ground level can be marked Bottom-only, and a decorative capital Top-only. Empty = the unbanded lists above are used at every storey, which is the old behaviour.\n\nNB a per-room-type pillar override (RoomStyle OpeningSet) still wins whole and is unbanded — banding applies to the KIT's generic posts.")]
        public RoomStyle.BandedAsset[] outerCornerPillarBands;
        public RoomStyle.BandedAsset[] innerCornerPillarBands;

        [Header("Trim — edges between two emitted surfaces")]
        [Tooltip("GENERIC lintel / cornice for the top edge of a wall inside a STAIR SHAFT, where it meets the ceiling. RoomStyle's per-type lintelPrefabs override this; this is the fallback so a partially authored style still renders. A stairwell exposes a whole storey of wall in one view, which makes that seam the most visible wall/ceiling junction in the dungeon — everywhere else the eye is much further from it.\n\nAuthor facing +Z (rotated to point INTO the shaft, same convention as a wall facing the space it is viewed from) and with the SAME ORIGIN CONVENTION AS YOUR WALLS AND CEILINGS — globalVisualOffset IS applied to this, because it is a kit piece that has to line up with them. That differs from ladders and bridges, which are authored BASE-ORIGIN and get no offset. Getting this backwards puts the piece a half-cell out, which is the classic symptom (golden rule 2).")]
        public GameObject[] lintelPrefabs; // optional — skipped if empty
        [Tooltip("Nudge for the lintel in its OWN frame: Z = further into the shaft / back into the wall, Y = up or down from the ceiling line, X = along the run. Rotated with the piece, so one value is correct on all four wall directions.")]
        public Vector3 lintelOffset;

        [Tooltip("GRATE / MOUTH piece where a crawlway breaks out of a wall — the framed 1.5m opening plus the masonry filling the rest of that 3m face.\n\nTO MAKE IT BREAKABLE, put a CrawlwayGrate component on the BARS as a child, never on the frame: the frame carries the collision standing in for the suppressed 3m wall quad, and detaching that opens a hole in the rock either side of the opening. The placer detects the component and switches this piece to FullGameObject automatically (an instanced mesh cannot be un-drawn), and tells it which way to fall. Give the bars an ImpactAudio for the landing clang.\n\nTHIS PIECE CARRIES THE WALL'S COLLISION AND THE FEATURE DOES NOT WORK WITHOUT IT. The greybox emits ONE quad per cell face, so opening a crawlway suppresses a whole 3m x 3m collider, not a 1.5m one — without a ring of colliders here (four boxes around the bore is the simple shape) the player walks through the rock either side of the grate. Suppression is GATED on this slot being filled, so leaving it empty is safe: crawlways simply stay sealed behind solid wall.\n\nAuthor facing +Z, rotated to point INTO the open cell you see it from — the same convention as a wall — and floor-aligned, matching the tube. KIT-FRAME: globalVisualOffset IS applied, because this piece has to line up with the walls it interrupts. That differs from the TUBE pieces below, which are base-origin like ladders and bridges; the two halves of one feature genuinely follow different conventions, which is why they are filed in different sections here.")]
        public GameObject[] crawlwayMouthPrefabs; // optional — skipped if empty, and suppression is skipped with it
        [Tooltip("WEIGHTED variants for the mouth / grate piece. Same rules as the tube variants: weight is relative within the list, 0 mutes an entry, and the plain list above still works with its entries counting as weight 1.")]
        public WeightedPrefab[] crawlwayMouthVariants;
        [Tooltip("Nudge for the mouth in its OWN frame: Z = further into the room / back into the wall, Y = up, X = along the face. Rotated with the piece, so one value is correct on all four wall directions (the ladderOffset lesson).")]
        public Vector3 crawlwayMouthOffset;

        [Header("BASE-ORIGIN pieces — globalVisualOffset does NOT apply")]
        [Tooltip("Wall-mounted ladder for drop-in elevated entrances (no room for a staircase). Author BASE-ORIGIN (globalVisualOffset is NOT applied), one cell (3m) tall — segments stack per story. Thin, back at the wall plane; give it a solid collider plus a trigger box with a LadderClimbZone covering the climbable front (extend the trigger ~0.5m above the opening so the player keeps climb control while cresting). Optional — skipped if empty (the entrance stays a one-way drop).")]
        public GameObject[] ladderPrefabs; // optional — skipped if empty
        [Tooltip("Nudge applied in the LADDER'S OWN frame, not world space: Z = away from the mounting wall, X = along it, Y = up. Wall-relative because that's the only way one value can be correct on all four wall directions — a world-space offset embeds the ladder in the masonry on the opposite wall.")]
        public Vector3 ladderOffset;
        [Tooltip("Bridge deck spanning a room PIT, one cell long, laid flat at the room's floor level. A KIT piece rather than a prop deliberately: the generator's connectivity check counts on the crossing existing, and a prop could decline to place while a cell-level flood-fill could never see it either way. MUST have a solid collider — that collider is what makes the deck walkable AND what bakes it into the runtime navmesh so NPCs cross it. If the collider is a MeshCollider its mesh MUST be Read/Write Enabled, or the bridge is silently skipped from the bake in a PLAYER BUILD while working perfectly in the editor (the same trap that once removed stairs from builds). Optional — skipped if empty, which leaves pits uncrossable.")]
        public GameObject[] bridgePrefabs; // optional — skipped if empty
        [Tooltip("Nudge applied to a bridge deck in world space (Y is the useful axis — sink or raise the deck relative to the floor plane). Unlike ladderOffset this is NOT rotated: a bridge lies flat and is placed axis-aligned, so there is no wall frame to be relative to.")]
        public Vector3 bridgeOffset;
        [Tooltip("BROKEN EDGE piece for a pit's rim — cracked flagstones and rubble along the lip, laid where a room's floor meets the hole. Without it the floor quad simply stops and the transition reads as MISSING GEOMETRY rather than a designed opening; the pit's own walls are a whole level below and you don't see them from across the room. Author facing +Z, which is rotated to point AWAY from the pit (toward the floor you stand on to look at it), same convention as a wall facing into the room it is viewed from. Placed as StaticDecor — no collider, so it never becomes a lip you trip on.")]
        public GameObject[] pitRimPrefabs; // optional — skipped if empty
        [Tooltip("Nudge for the rim piece in its OWN frame: Z = further over the hole / back onto the floor, Y = up, X = along the edge. Rotated like the piece, so one value is correct on all four edge directions (the ladderOffset lesson).")]
        public Vector3 pitRimOffset;

        [Tooltip("STRAIGHT crawlway tube — a 1.5m x 1.5m bore, 3.0m LONG, one piece per grid cell. NOT a 1.5m cube: the cross-section is 1.5m but the piece spans a whole cell along its run.\n\nOne CLOSED tube asset, not separate floor/wall/ceiling pieces — a crawlway is not a room and the kit's 3m faces do not apply to it. Author it FLOOR-ALIGNED (the bore's floor at the piece's base, not centred: a centred bore puts the sill 0.75m up, past the controller's 0.5m step height with no mantle mechanic, and the player could not enter their own crawlway) with the run along +Z, and BASE-ORIGIN — globalVisualOffset does NOT apply, same as ladders and bridges.\n\nCOLLISION: give it BOX colliders — floor, ceiling and two sides. Nothing exists inside solid rock, so this piece is the only collision in the bore. Boxes rather than a MeshCollider on purpose: a hollow tube is non-convex, and a non-readable MeshCollider is silently dropped from the navmesh bake in a PLAYER BUILD only (the trap that once removed stairs from builds). Put the colliders on a layer INCLUDED IN FirstPersonController.ceilingMask, or the stand-up block never fires and the player stands up through the rock.")]
        public GameObject[] crawlwayTubePrefabs; // optional — skipped if empty
        [Tooltip("CORNER crawlway tube — the same 1.5m bore turning 90 degrees within one cell. Author with its two openings on the -Z and +X faces; the placer tries all four yaw steps to match a turn, and those four rotations cover every perpendicular pair, so ONE corner asset is all that is ever needed (a tube is symmetric end to end — there is no left-hand and right-hand version).\n\nSame conventions as the straight: floor-aligned, base-origin, box colliders. Empty = corners fall back to the straight piece, which will visibly not turn — author this before raising sewerCellBudget, since turns are most of what makes a network read as winding rather than as a long pipe.")]
        public GameObject[] crawlwayCornerPrefabs; // optional — falls back to the straight piece
        [Tooltip("TEE crawlway tube — a straight run with a 1.5m opening in ONE SIDE, where the bore passes a sewer chamber. Author with the run along +Z and the side opening on +X; the placer flips the run end-for-end when the chamber is on the other side, which is invisible because a straight tube is symmetric, so ONE tee asset covers both hands.\n\nSame conventions as the straight: floor-aligned, base-origin, box colliders — and leave the side opening genuinely open, since the chamber's own grate piece frames it. Empty = the tee cell falls back to a closed straight tube, which seals the chamber off entirely (it stays carved but unreachable), so author this alongside the chamber.")]
        public GameObject[] crawlwayTeePrefabs; // optional — falls back to the closed straight piece
        [Tooltip("CROSS crawlway tube — a 4-way junction, all four faces open. Author centred with openings on every side; it needs no particular orientation, but the placer still rotates it to fit so any asymmetric detailing lands consistently.\n\nSame conventions as the straight: floor-aligned, base-origin, box colliders. Empty = a 4-way cell falls back to the straight piece, which will seal two of its four connections.")]
        public GameObject[] crawlwayCrossPrefabs;
        [Tooltip("DEAD-END CAP — a tube closed at one end, for the tips of a network's branches. Author with its single opening on -Z.\n\nSewer networks are grown as branching trees, so dead ends are normal and numerous rather than a failure: most branch tips are one. Empty = a dead end falls back to the straight piece, leaving a tunnel that visibly opens into solid rock.")]
        public GameObject[] crawlwayCapPrefabs;
        [Tooltip("WEIGHTED variants for cross tubes. Same rules as the straight variants.")]
        public WeightedPrefab[] crawlwayCrossVariants;
        [Tooltip("WEIGHTED variants for dead-end caps. Same rules as the straight variants.")]
        public WeightedPrefab[] crawlwayCapVariants;
        [Tooltip("WEIGHTED variants for straight tubes. Weight is relative within the list — 3 vs 1 means three times as often — and 0 mutes an entry without deleting it.\n\nThe plain Crawlway Tube Prefabs list above still works and its entries count as WEIGHT 1, so these EXTEND the pool rather than replacing it. Move a prefab here to change how often it appears; the alternative was listing it twice, which is the same trick the wall variants had to abandon.")]
        public WeightedPrefab[] crawlwayTubeVariants;
        [Tooltip("WEIGHTED variants for corner tubes. Same rules as the straight variants above.")]
        public WeightedPrefab[] crawlwayCornerVariants;
        [Tooltip("WEIGHTED variants for tee tubes (the cell where a bore passes a sewer chamber). Same rules as the straight variants above.")]
        public WeightedPrefab[] crawlwayTeeVariants;
        [Tooltip("Nudge applied to a crawlway tube in its OWN frame: Z = along the run, X = across it, Y = up. Rotated with the piece, so one value is correct whichever way the bore runs.")]
        public Vector3 crawlwayTubeOffset;

        [Header("Variation")]
        public bool randomizeFloorYaw = true;
        public bool randomizeCeilingYaw = true;
        [Header("Wall variant clustering")]
        [Tooltip("Feature size, in CELLS, of the smooth field that makes wall variants CLUSTER — roughly how wide a patch of damage/soot/damp tends to be. 6 is a good starting point (about 18m at the 3m cell size); larger means broader, lazier regions, smaller means blotchier.\n\n0 DISABLES the field entirely and every wall asset stays eligible everywhere, which reproduces the old behaviour exactly — variants then differ only by their Weight.\n\nOnly assets whose Noise Range is narrower than (0,1) respond to it, so turning this on changes nothing until something opts in.")]
        public float wallNoiseScale = 6f;
        [Tooltip("Salt for the clustering field. Change it to get a different arrangement of patches at the same seed — the dungeon's layout is untouched, only which regions are damaged.")]
        public int wallNoiseSalt = 7717;
        [Header("Per-room emissive tinting (optional)")]
        [Tooltip("The GLOWING material used by kit pieces with an emissive element (candle walls, braziers). When set, every instanced kit piece using this exact material gets a cached variant tinted to its room's TORCH COLOUR — so a shrine's candles burn the same cold blue as its torches, exactly like the fog and flame VFX already do. Leave empty to disable tinting entirely (pieces render with the authored material).\n\nThis is needed because a MaterialPropertyBlock can't work on the instanced path: nothing renders through the prefab's MeshRenderer, so an EmissionController on a kit wall does nothing. Cost is one extra batch per distinct colour in use — bounded by the palette, not by instance count.")]
        public Material emissiveMaterial;
        [Tooltip("Shader property tinted on the variant. _EmissionColor for URP/Lit.")]
        public string emissiveProperty = "_EmissionColor";
        [Tooltip("Log each emissive variant as it's built — shader, resolved colour, whether the property write landed, GI flags, and GPU-instancing state. Turn on when the tint applies but nothing glows.")]
        public bool debugEmissive = false;
        [Tooltip("Multiplies the room's torch colour before it's written as emission. MUST usually be > 1 to actually GLOW: the torch palette's colours are LDR-range (components <= 1), and emission only blooms above 1 — at 1 the candle is merely tinted, not lit. Bloom must also be enabled in the post-process Volume. 2.4 was the hand-tuned value on the original EmissionController.")]
        public float emissiveIntensity = 2.4f;

        [Header("Pivot correction — KIT-FRAME pieces (meters, world space)")]
        [Tooltip("Applied to EVERY kit placement (pieces, doors, gates). Use to dial the whole kit flush against the greybox collision shell when visuals sit uniformly off nominal heights. A clean value like ±1.5 or ±3 is the fingerprint of a prefab/origin offset that should eventually be fixed at the source and this zeroed.\n\nNOT applied to the BASE-ORIGIN pieces above (ladders, bridges, pit rims) — those are authored at their own base and nudged in their own frame.")]
        public Vector3 globalVisualOffset;
        [Tooltip("Use these to compensate for asset pivots that don't sit on the placement surface. The proper fix is setting the origin in Blender; these are the hotfix.\n\nAll WORLD-SPACE and all on top of globalVisualOffset. The rotated, piece-frame nudges live beside their own prefab slots above (ladder, pit rim, lintel) — a world-space value there only works on one of the four wall directions, which is the bug that produced this split.")]
        public Vector3 wallOffset;
        public Vector3 floorOffset;
        public Vector3 ceilingOffset;
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
        /// <summary>
        /// One emitted kit piece that carries PropSockets — recorded so KitSocketPlacer can
        /// fill them afterwards with full PropTier control, which `place` cannot express (a
        /// fireplace VFX must be FullGameObject, a candle StaticDecor).
        ///
        /// Record-then-consume rather than spawning inline, same shape as
        /// WallFaceRegistry.FeatureFaces feeding NearWallAsset: it keeps Enumerate free of prop
        /// concerns and lets one pass serve both the PrefabKit and InstancedKit paths.
        /// </summary>
        public struct SocketSite
        {
            public GameObject prefab;
            public Vector3 posCells;      // as handed to `place` — cell units, pre-offset
            public Quaternion rot;
            public Vector3 offset;        // includes globalVisualOffset where the piece takes it
            public Vector3Int cell;       // owning cell
            public Vector3Int faceDir;    // wall face direction; zero for floors/ceilings
            public bool isFloor;          // floor pieces may only take NON-BLOCKING children
        }

        public static void Enumerate(DungeonGenerator gen, DungeonKit kit, HashSet<string> missing, PlaceCallback place,
                                     RoomStyle style = null, PlaceCallback placeWithCollider = null,
                                     WallFaceRegistry wallFaces = null,
                                     List<SocketSite> socketSites = null)
        {
            placeWithCollider ??= place;

            // Whether a prefab carries sockets, cached: this is asked once per emitted piece
            // (thousands per dungeon) and GetComponentsInChildren allocates.
            var socketedCache = new Dictionary<GameObject, bool>();
            bool HasSockets(GameObject p)
            {
                if (p == null) return false;
                if (socketedCache.TryGetValue(p, out bool has)) return has;
                has = p.GetComponentInChildren<PropSocket>(true) != null;
                socketedCache[p] = has;
                return has;
            }

            // Recorded from INSIDE Emit below, so all of its call sites are covered at once
            // rather than each needing to remember. NB the reserved capped-asset path calls
            // `place` DIRECTLY, bypassing Emit — it is easy to miss and is handled separately.
            void RecordSocketSite(GameObject prefab, Vector3 posCells, Quaternion rot, Vector3 offset,
                                  Vector3Int cell, Vector3Int faceDir, bool isFloor)
            {
                if (socketSites == null || !HasSockets(prefab)) return;
                socketSites.Add(new SocketSite
                {
                    prefab = prefab, posCells = posCells, rot = rot, offset = offset,
                    cell = cell, faceDir = faceDir, isFloor = isFloor,
                });
            }
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

                DealCappedAssets(res, room.Cells, setAssets, cell => BandOf(room, cell));
                return res;
            }

            // Prison closets get their own reservations: a prison is a discrete enclosure with
            // its own walls, so "1 per room" has an obvious meaning there — the ONE unit for
            // which it doesn't is the hallway network (see RoomStyle.HallwayWalls). Without this
            // the cap was ignored entirely and a capped drain simply joined the general hash
            // pick, landing on face after face.
            // Keyed by the spec itself now that prisons carry one. The bbox rescan this used to
            // do — walk allPositionsWithin looking for CellType.Prison — is exactly what
            // PrisonSpec.Cells already knows, and it was also subtly wrong for a WIDE prison,
            // whose footprint is not its bounding box (the 1x1 vestibule shape).
            var prisonReservations = new Dictionary<PrisonSpec, Dictionary<long, RoomStyle.WallAsset>>();

            Dictionary<long, RoomStyle.WallAsset> GetPrisonReservations(PrisonSpec prison)
            {
                if (prisonReservations.TryGetValue(prison, out var res)) return res;
                res = new Dictionary<long, RoomStyle.WallAsset>();
                prisonReservations[prison] = res;
                if (style == null) return res;

                var setAssets = style.PrisonWallSet();
                if (setAssets == null) return res;

                // A prison closet is one course of wall — always the Bottom band, like the
                // general prison/hallway pick.
                DealCappedAssets(res, prison.Cells, setAssets, _ => RoomStyle.WallBand.Bottom);
                return res;
            }

            // Deal each capped asset ONCE, from the union of faces in its
            // allowed bands, in hash-shuffled order (never scan order). A
            // shared used-set keeps two specials off the same face. Salt
            // the shuffle per asset so co-eligible specials decorrelate.
            //
            // Shared by rooms and prisons so the two cannot drift: the ONLY differences
            // between them are which cells form the enclosure and how a cell maps to a band.
            void DealCappedAssets(Dictionary<long, RoomStyle.WallAsset> res,
                                  IEnumerable<Vector3Int> cells,
                                  List<RoomStyle.WallAsset> setAssets,
                                  System.Func<Vector3Int, RoomStyle.WallBand> bandOf)
            {
                var facesByBand = new Dictionary<RoomStyle.WallBand, List<(Vector3Int cell, int dirIdx)>>();
                foreach (var cell in cells)
                    for (int di = 0; di < HDirs.Length; di++)
                    {
                        if (Open(cell + HDirs[di])) continue;
                        var band = bandOf(cell);
                        if (!facesByBand.TryGetValue(band, out var list))
                            facesByBand[band] = list = new List<(Vector3Int, int)>();
                        list.Add((cell, di));
                    }

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
            }

            // Unlimited assets per (type, band), cached for the pass.
            var unlimitedCache = new Dictionary<(RoomType, RoomStyle.WallBand), List<RoomStyle.WallAsset>>();
            // The UNCAPPED pool for a (type, band), as WallAssets rather than bare prefabs —
            // the pick now needs each asset's weight and noise range, not just its prefab.
            List<RoomStyle.WallAsset> UnlimitedWalls(RoomType type, RoomStyle.WallBand band)
            {
                if (unlimitedCache.TryGetValue((type, band), out var cached)) return cached;
                List<RoomStyle.WallAsset> result = null;
                var assets = style.WallAssetsFor(type, band);
                if (assets != null)
                {
                    var list = new List<RoomStyle.WallAsset>();
                    foreach (var a in assets)
                        if (a.prefab != null && a.maxPerRoom <= 0) list.Add(a);
                    if (list.Count > 0) result = list;
                }
                unlimitedCache[(type, band)] = result;
                return result;
            }

            /// Pick one wall asset for a face: NOISE decides what is eligible here, WEIGHT
            /// decides the mix among those. The two answer different questions — "is this the
            /// kind of place where walls are cracked" versus "how much of the mix is cracked" —
            /// and composing them is what separates a damaged SECTION from an even sprinkle.
            ///
            /// Falls back to the full pool if the noise range excludes everything, because a
            /// face with no eligible asset would otherwise drop to the kit generic and punch a
            /// hole in a themed room. An over-narrow range should look wrong, not missing.
            GameObject PickWall(List<RoomStyle.WallAsset> pool, Vector3Int cell, Vector3 posCells)
            {
                if (pool == null || pool.Count == 0) return null;

                float n = ValueNoise.ForCell(cell, kit.wallNoiseScale, kit.wallNoiseSalt);

                float total = 0f;
                foreach (var a in pool)
                    if (a.AllowsNoise(n)) total += Mathf.Max(0f, a.weight);

                bool useNoise = total > 0f;
                if (!useNoise)
                {
                    foreach (var a in pool) total += Mathf.Max(0f, a.weight);
                    if (total <= 0f) return pool[0].prefab;   // every weight muted
                }

                // Same per-face hash the uniform pick used, read as a 0..1 value instead of an
                // index — so variety is still deterministic and still varies face to face
                // WITHIN a noise region.
                float roll = Hash(Vector3Int.RoundToInt(posCells * 4f), 11) / (float)0x7fffffff * total;
                foreach (var a in pool)
                {
                    if (useNoise && !a.AllowsNoise(n)) continue;
                    roll -= Mathf.Max(0f, a.weight);
                    if (roll <= 0f) return a.prefab;
                }
                for (int i = pool.Count - 1; i >= 0; i--)      // float drift on the last bucket
                    if (!useNoise || pool[i].AllowsNoise(n)) return pool[i].prefab;
                return pool[0].prefab;
            }

            // A RESERVED capped wall asset. Calls `place` DIRECTLY rather than going through
            // Emit — the prefab is already chosen, there is nothing to hash-pick — which is
            // exactly why it needs its own socket record and feature-label handling. It is the
            // path capped FEATURE walls land on (a fireplace's fire socket lives here), so it is
            // both the easiest to overlook and the most costly to.
            void EmitReserved(RoomStyle.WallAsset reserved, Vector3 facePos, Vector3Int c, Vector3Int d)
            {
                var rot = Quaternion.LookRotation(-(Vector3)d);
                place(reserved.prefab, facePos, rot, kit.wallOffset + kit.globalVisualOffset, c);
                RecordSocketSite(reserved.prefab, facePos, rot,
                                 kit.wallOffset + kit.globalVisualOffset, c, d, false);
                // A labeled feature wall (fireplace etc.) — NearWallAsset props with a matching
                // Host Label attach beside it. Unlabeled capped assets are NOT hosts.
                if (!string.IsNullOrEmpty(reserved.featureLabel))
                    wallFaces?.RecordFeature(c, d, reserved.featureLabel);
            }

            // Returns the picked prefab (null if the slot was empty) so wall
            // emission can record per-face restrictions for it.
            //
            // UNIFORM pick, still correct for every slot that is a plain prefab array — floors,
            // ceilings, bars, arches. WALLS no longer come through here; they carry per-asset
            // weight and noise range and go through PickWall + EmitPrefab instead.
            GameObject Emit(GameObject[] slot, string slotName, Vector3 posCells, Quaternion rot, Vector3 offset, Vector3Int cell)
            {
                if (slot == null || slot.Length == 0) { missing.Add(slotName); return null; }
                GameObject prefab = slot[Hash(Vector3Int.RoundToInt(posCells * 4f), 11) % slot.Length];
                return EmitPrefab(prefab, slotName, posCells, rot, offset, cell);
            }

            // Placement for an ALREADY-CHOSEN prefab. Split out of Emit so the weighted wall
            // pick and the reserved capped-asset path share one placement + socket-recording
            // path rather than each reimplementing it — the reserved path already diverged once
            // and had to have its socket recording added separately.
            GameObject EmitPrefab(GameObject prefab, string slotName, Vector3 posCells, Quaternion rot,
                                  Vector3 offset, Vector3Int cell)
            {
                if (prefab == null) { missing.Add(slotName); return null; }
                place(prefab, posCells, rot, offset + kit.globalVisualOffset, cell);

                // Sockets ride on the piece's ACTUAL rendered pose, globalVisualOffset included.
                // The socket was authored in the mesh's frame, which is offset — composing from
                // the un-offset pose puts every child a half-cell low (golden rule 2, the same
                // shape that once floated scatter props in the air).
                Vector3Int faceDir = slotName == "wall" ? RoundDir(rot * Vector3.back) : Vector3Int.zero;
                RecordSocketSite(prefab, posCells, rot, offset + kit.globalVisualOffset, cell,
                                 faceDir, slotName == "floor");
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
                    // SEWER CHAMBER BEFORE THE HALLWAY FALLBACK, for the same reason pits go
                    // before the room branch: a chamber's cells are TYPED Hallway (that is what
                    // earns them free walls, floors and ceilings), so the hallway branch would
                    // always win and the slot could never fire. Unauthored falls through to
                    // exactly that hallway styling, so this changes nothing until it is filled.
                    else if (gen.IsChamberCell(c))
                    {
                        floorSlot = style.ChamberFloors() ?? style.HallwayFloors() ?? floorSlot;
                        ceilingSlot = style.ChamberCeilings() ?? style.HallwayCeilings() ?? ceilingSlot;
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
                    // A crawlway grate replaces the whole wall here. Must match DungeonMesher
                    // exactly — both ask the generator, so visuals and collision cannot come to
                    // disagree about where the wall is (the NeedsSlabBetween pattern).
                    //
                    // THE FACE MUST BE RECORDED AS DENIED, NOT MERELY LEFT UNEMITTED. The
                    // registry is a DENY-LIST — `PropsAllowed`/`TorchAllowed` return TRUE for a
                    // key that was never added, because a kit generic wall carries no metadata
                    // and allows everything (§7). So skipping the emit made the grate the most
                    // permissive face in the dungeon rather than the most restricted, and a
                    // sconce or banner would be mounted straight over the opening. Absence means
                    // "no restrictions", never "nothing here".
                    //
                    // Claimed as well as denied, so the one-occupant-per-face rule covers it too
                    // and TorchPlacer's IsClaimed guard refuses it independently of the flags.
                    if (!Open(nb) && gen.IsCrawlwayMouthFace(c, d))
                    {
                        if (wallFaces != null)
                        {
                            wallFaces.Record(i, d, allowProps: false, allowTorch: false);
                            wallFaces.Claim(i, d);
                        }
                        continue;
                    }

                    if (!Open(nb))
                    {
                        bool emitted = false;
                        GameObject placedWall = null;
                        // WHICH LIST the prefab came from, tracked alongside it. The same prefab
                        // carries different flags in different lists, so reading them back needs
                        // the context and not just the prefab (see RoomStyle.WallFlagsFor).
                        var wallCtx = RoomStyle.WallContext.Hallway;
                        RoomType wallCtxType = default;
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

                            // SEWER CHAMBER, same shape and for the same reason: its cells are
                            // typed Hallway, so without checking first the hallway branch always
                            // wins and the slot can never fire. Bands and feature reservations
                            // skipped like the pit's — a chamber is one course of rough wall,
                            // and a fireplace behind a grate is not a thing worth supporting.
                            var chamberWalls = pitWalls == null && gen.IsChamberCell(c) && style != null
                                ? style.ChamberWalls() : null;

                            if (pitWalls != null)
                            {
                                placedWall = EmitPrefab(PickWall(pitWalls, c, facePos), "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                wallCtx = RoomStyle.WallContext.Pit;
                                emitted = true;
                            }
                            else if (chamberWalls != null)
                            {
                                placedWall = EmitPrefab(PickWall(chamberWalls, c, facePos), "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                wallCtx = RoomStyle.WallContext.Chamber;
                                emitted = true;
                            }
                            else if (room != null)
                            {
                                wallCtx = RoomStyle.WallContext.Room;
                                wallCtxType = room.Type;
                                var res = GetReservations(room);
                                if (res.TryGetValue(FaceKey(i, d), out var reserved))
                                {
                                    EmitReserved(reserved, facePos, c, d);
                                    placedWall = reserved.prefab;
                                    emitted = true;
                                }
                                else
                                {
                                    var unlimited = UnlimitedWalls(room.Type, BandOf(room, c));
                                    if (unlimited != null)
                                    {
                                        placedWall = EmitPrefab(PickWall(unlimited, c, facePos), "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                        emitted = true;
                                    }
                                }
                            }
                            else if (t == CellType.Hallway || t == CellType.StairLower || t == CellType.StairUpper)
                            {
                                wallCtx = RoomStyle.WallContext.Hallway;
                                var styled = style.HallwayWalls();
                                if (styled != null)
                                {
                                    placedWall = EmitPrefab(PickWall(styled, c, facePos), "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                    emitted = true;
                                }
                            }
                            else if (t == CellType.Prison)
                            {
                                wallCtx = RoomStyle.WallContext.Prison;
                                var prison = gen.PrisonAt(c);
                                var pres = prison != null ? GetPrisonReservations(prison) : null;
                                if (pres != null && pres.TryGetValue(FaceKey(i, d), out var reserved))
                                {
                                    EmitReserved(reserved, facePos, c, d);
                                    placedWall = reserved.prefab;
                                    emitted = true;
                                }
                                else
                                {
                                    var styled = style.PrisonWalls();
                                    if (styled != null)
                                    {
                                        placedWall = EmitPrefab(PickWall(styled, c, facePos), "wall", facePos, Quaternion.LookRotation(-(Vector3)d), kit.wallOffset, c);
                                        emitted = true;
                                    }
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
                            style.WallFlagsFor(placedWall, wallCtx, wallCtxType,
                                               out bool allowProps, out bool allowTorch);
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
            // BANDED LISTS COUNT AS "HAVING POSTS". Gating on the unbanded arrays alone would
            // mean that authoring bands and clearing the old list - the natural way to move
            // over - silently disabled corner posts entirely.
            bool anyOuter = (kit.outerCornerPillarPrefabs != null && kit.outerCornerPillarPrefabs.Length > 0)
                         || (kit.outerCornerPillarBands != null && kit.outerCornerPillarBands.Length > 0);
            bool anyInner = (kit.innerCornerPillarPrefabs != null && kit.innerCornerPillarPrefabs.Length > 0)
                         || (kit.innerCornerPillarBands != null && kit.innerCornerPillarBands.Length > 0);
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
                            Room bestRoom = null;   // hoisted: also used for the band below
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
                                bestRoom = best;
                                if (best != null)
                                {
                                    outerSlot = style.OuterPillarsFor(best.Type) ?? outerSlot;
                                    innerSlot = style.InnerPillarsFor(best.Type) ?? innerSlot;
                                }
                            }

                            // BAND THIS POST BY ITS STOREY. A piece that only reads well at
                            // ground level is Bottom-only; a capital is Top-only.
                            //
                            // The run comes from the owning ROOM's bounds rather than by
                            // walking the grid vertically: it is already resolved above, it is
                            // exactly the vertical extent the player perceives as "this room",
                            // and a corridor corner has no room and correctly falls to a
                            // single-storey Bottom.
                            //
                            // Deliberately does NOT override a per-room-type pillar: a style
                            // that names its own posts has made a whole-slot decision, and
                            // silently swapping one storey of it for a kit piece would be the
                            // sort of half-applied styling that is very hard to see.
                            if (kit.outerCornerPillarBands != null && kit.outerCornerPillarBands.Length > 0
                                && outerSlot == kit.outerCornerPillarPrefabs)
                            {
                                var picked = PickBandedPost(kit.outerCornerPillarBands, bestRoom, edge);
                                if (picked != null) { bandScratch[0] = picked; outerSlot = bandScratch; }
                            }
                            if (kit.innerCornerPillarBands != null && kit.innerCornerPillarBands.Length > 0
                                && innerSlot == kit.innerCornerPillarPrefabs)
                            {
                                var picked = PickBandedPost(kit.innerCornerPillarBands, bestRoom, edge);
                                if (picked != null) { bandScratch2[0] = picked; innerSlot = bandScratch2; }
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
                                       RoomStyle style = null, WallFaceRegistry wallFaces = null,
                                       List<SocketSite> socketSites = null)
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
            }, style, null, wallFaces, socketSites);

            if (missing.Count > 0)
                Debug.LogWarning($"[DungeonKit] Missing prefab slot(s): {string.Join(", ", missing)} — those pieces were skipped.");

            return root;
        }

        // Reused single-element slots for a banded post pick. The corner classifier runs over
        // every edge of every cell of every storey, so allocating a one-element array per
        // placement would be a lot of garbage for nothing; EmitCollider reads the array
        // immediately and never retains it, so reuse is safe.
        static readonly GameObject[] bandScratch = new GameObject[1];
        static readonly GameObject[] bandScratch2 = new GameObject[1];

        /// <summary>
        /// Banded pick for a corner post, using the owning room's vertical extent as the run.
        /// A corridor corner has no room and is a single storey, i.e. Bottom.
        /// </summary>
        static GameObject PickBandedPost(RoomStyle.BandedAsset[] bands, Room room, Vector3 edge)
        {
            // `edge` is a CELL-space corner coordinate held as a Vector3 (edges sit between
            // cells), built from integer x/y/z — so y is the storey directly.
            int y = Mathf.RoundToInt(edge.y);
            int count = room != null ? Mathf.Max(1, room.Bounds.size.y) : 1;
            int index = room != null ? y - room.Bounds.yMin : 0;

            // Hash key follows the convention the other pillar picks use: cell coords scaled
            // by 4 and rounded, so a half-cell edge still lands on a distinct integer key.
            return RoomStyle.PickBanded(bands, RoomStyle.BandOf(index, count),
                                        Vector3Int.RoundToInt(edge * 4f), 71);
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
                        marker.prison = gen.PrisonAt(p);
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
            // Either list is enough - see the pillar gate above for why this must not check the
            // unbanded array alone.
            bool haveUnbandedCols = kit.interiorColumnPrefabs != null && kit.interiorColumnPrefabs.Length > 0;
            bool haveBandedCols = kit.interiorColumnBands != null && kit.interiorColumnBands.Length > 0;
            if (!haveUnbandedCols && !haveBandedCols) return root;
            if (gen.ColumnPoints.Count == 0) return root;

            int segments = 0;
            foreach (var (lattice, yFloor, heightCells) in gen.ColumnPoints)
            {
                // The UNBANDED pick, resolved once per column so an unbanded set still repeats
                // one piece all the way up exactly as before. Banded sets re-pick per segment
                // below; this stays the per-band fallback.
                // Guarded: indexing an empty unbanded list would throw once bands are the only
                // source, which is the intended end state.
                GameObject fallbackPrefab = haveUnbandedCols
                    ? kit.interiorColumnPrefabs[Hash(lattice, 53) % kit.interiorColumnPrefabs.Length]
                    : null;
                bool banded = haveBandedCols;
                if (fallbackPrefab == null && !banded) continue;

                // Lattice points are cell-CORNER coordinates, so the world
                // position is lattice * cellSize directly (no half-cell shift).
                Vector3 basePos = new Vector3(lattice.x, yFloor, lattice.z) * cellSize
                                  + kit.interiorColumnOffset + kit.globalVisualOffset + parent.position;

                // Deterministic yaw variety in 90° steps — columns are usually
                // symmetric, but this hides texture repetition for free.
                Quaternion rot = Quaternion.Euler(0f, 90f * (Hash(lattice, 91) % 4), 0f);

                for (int seg = 0; seg < heightCells; seg++)
                {
                    // Which course this segment is. A 1-tall column is all Bottom; a 2-tall one
                    // is Bottom then Top with no Middle, which is what makes a base/capital
                    // pair work without authoring a special two-storey case.
                    RoomStyle.WallBand band = RoomStyle.BandOf(seg, heightCells);
                    GameObject prefab = banded
                        // Salt includes the segment so a 3-storey column can draw a DIFFERENT
                        // middle piece per course rather than the same one twice.
                        ? RoomStyle.PickBanded(kit.interiorColumnBands, band, lattice, 53 + seg * 7)
                        : null;
                    if (prefab == null) prefab = fallbackPrefab;   // strict bands: fall back, never borrow
                    if (prefab == null) continue;

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

                // Tell every climb zone on this ladder which way a climber must face. Done after
                // the segments are placed and by searching the ROOT, because the instanced path
                // hands back no GameObject — PropInstancer keeps the collider object under
                // `root`, which is where the zone ends up either way.
                foreach (var zone in root.GetComponentsInChildren<LadderClimbZone>(true))
                    if (!zone.HasFacing) zone.FaceDirection = lad.WallDir;

                count++;
            }

            if (count > 0)
                Debug.Log($"[Dungeon] {count} ladder(s) placed.");
            return root;
        }

        /// <summary>
        /// Crawlway tubes and their wall grates.
        ///
        /// THE ONLY GEOMETRY A CRAWLWAY HAS. Its cells stay CellType.Empty, so the greybox
        /// emits nothing inside the bore and the mesher, the kit placer and the automap all
        /// read the rock as solid — these prefabs are the mesh AND the collision, the same
        /// arrangement bridges and ladders use.
        ///
        /// TWO ORIGIN CONVENTIONS IN ONE FEATURE (§5). The TUBE sits on the grid like a prop:
        /// base-origin, no globalVisualOffset. The MOUTH has to line up with the kit masonry it
        /// interrupts, so it is kit-frame and the offset applies. Getting either backwards puts
        /// that piece a half-cell out, which is the classic symptom.
        /// </summary>
        public static GameObject BuildCrawlways(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                                InstancedDungeonRenderer instancer = null)
        {
            var root = new GameObject("DungeonCrawlways");
            root.transform.SetParent(parent, false);
            if (gen.Crawlways.Count == 0) return root;

            // Pools resolved ONCE per build, not per cell — the merge allocates, and a bore is
            // walked cell by cell.
            var tubePool = MergePool(kit.crawlwayTubePrefabs, kit.crawlwayTubeVariants);
            var cornerPool = MergePool(kit.crawlwayCornerPrefabs, kit.crawlwayCornerVariants);
            var teePool = MergePool(kit.crawlwayTeePrefabs, kit.crawlwayTeeVariants);
            var crossPool = MergePool(kit.crawlwayCrossPrefabs, kit.crawlwayCrossVariants);
            var capPool = MergePool(kit.crawlwayCapPrefabs, kit.crawlwayCapVariants);

            var mouthPool = MergePool(kit.crawlwayMouthPrefabs, kit.crawlwayMouthVariants);

            bool haveTube = tubePool.Count > 0;
            bool haveMouth = mouthPool.Count > 0;
            if (!haveTube && !haveMouth) return root;

            // A tube with no grate is a sealed tunnel nobody can reach, and a grate with no tube
            // is a hole into rock. Both render, because a partially authored kit should show you
            // what you HAVE rather than silently nothing (the fallback philosophy in §7) — but
            // say so, since either alone looks like a bug rather than missing authoring.
            if (haveTube != haveMouth)
                Debug.LogWarning($"[Crawlways] Only half the kit is authored — " +
                    $"{(haveTube ? "tubes are set but the MOUTH slots are empty (neither crawlwayMouthPrefabs nor crawlwayMouthVariants), so the bores have no way in — and the wall stays solid, since suppression is gated on the mouth slot" : "mouths are set but the TUBE slots are empty (neither crawlwayTubePrefabs nor crawlwayTubeVariants), so the grates open onto nothing")}.");

            int tubes = 0, mouths = 0;
            foreach (var cw in gen.Crawlways)
            {
                if (haveTube)
                {
                    foreach (var cell in cw.Cells)
                    {
                        // PIECE AND YAW BOTH COME FROM THE NEIGHBOUR MASK, which is what replaced
                        // v1's in/out direction pair when a crawlway stopped being a path and
                        // became a graph. Popcount picks the piece — 1 dead end, 2 straight or
                        // corner, 3 tee, 4 cross — and the bit pattern picks the yaw. The same
                        // bitmask approach DungeonMapper already uses for walls.
                        //
                        // A CHAMBER OPENING OVERRIDES to a tee, and does NOT set a mask bit: the
                        // chamber is a room off the side, so its opening is a hole in the tube
                        // wall rather than another length of tube. Where a bore cell has two
                        // tunnel neighbours AND a chamber it is geometrically a tee already, so
                        // the mask alone would pick a straight and seal the chamber behind it.
                        int mask = cw.NeighbourMask(cell);
                        bool hasChamber = cw.ChamberAt(cell, out Vector3Int chamberDir);

                        List<WeightedPrefab> pool = null;
                        Quaternion rot = Quaternion.identity;
                        if (!TubePieceFor(mask, hasChamber, chamberDir,
                                          tubePool, cornerPool, teePool, crossPool, capPool,
                                          ref pool, ref rot))
                            continue;

                        GameObject prefab = PickWeightedPrefab(pool, cell, 149);
                        if (prefab == null) continue;

                        // The tube is FLOOR-ALIGNED, so its origin is the cell's base corner
                        // centre — not the cell centre. Base-origin: no globalVisualOffset.
                        Vector3 pos = new Vector3(cell.x + 0.5f, cell.y, cell.z + 0.5f) * cellSize
                                    + rot * kit.crawlwayTubeOffset + parent.position;

                        PlaceOne(prefab, pos, rot, PropTier.StaticCollider);
                        tubes++;
                    }
                }

                if (haveMouth)
                {
                    foreach (var m in cw.Mouths) mouths += PlaceMouth(m.OpenCell, m.IntoRock);
                    // Each chamber's grate, seen from inside the chamber looking back at the tube.
                    foreach (var ch in cw.Chambers) mouths += PlaceMouth(ch.MouthCell, -ch.Dir);
                }
            }

            int PlaceMouth(Vector3Int openCell, Vector3Int intoRock)
            {
                GameObject prefab = PickWeightedPrefab(mouthPool, openCell, 151);
                if (prefab == null) return 0;

                // On the wall face, at floor level, facing back into the open cell — the same
                // convention every wall piece uses. KIT-FRAME, so globalVisualOffset applies.
                Vector3 face = new Vector3(openCell.x + 0.5f + intoRock.x * 0.5f,
                                           openCell.y,
                                           openCell.z + 0.5f + intoRock.z * 0.5f) * cellSize;
                Quaternion rot = Quaternion.LookRotation(-(Vector3)intoRock);
                Vector3 pos = face + kit.globalVisualOffset + rot * kit.crawlwayMouthOffset + parent.position;

                // FullGameObject, NOT StaticCollider, whenever the mouth carries a breakable
                // grate. An instanced tier bakes the mesh into a static matrix and
                // InstancedDungeonRenderer has NO REMOVAL PATH (§8), so a grate that detaches
                // would leave its mesh welded across the opening while its collider fell away —
                // the identical rule carryables and destructibles follow. Mouths are single
                // digits per run, so the batching loss is irrelevant; the check keeps the
                // instanced path for a purely decorative mouth prefab.
                bool breakable = prefab.GetComponentInChildren<CrawlwayGrate>(true) != null;
                GameObject placed = PlaceOne(prefab, pos, rot,
                    breakable ? PropTier.FullGameObject : PropTier.StaticCollider);

                // WHICH WAY IT FALLS IS A PROPERTY OF THE MOUTH, not of the player who breaks
                // it: `-intoRock` points out of the bore into the open cell, which is the only
                // place a freed grate both fits and does not block the passage. See
                // CrawlwayGrate for why "push it away from the player" is the wrong rule.
                if (placed != null)
                {
                    var g = placed.GetComponentInChildren<CrawlwayGrate>(true);
                    if (g != null) g.OutwardDirection = -(Vector3)intoRock;
                }
                return 1;
            }

            GameObject PlaceOne(GameObject prefab, Vector3 pos, Quaternion rot, PropTier tier)
            {
                if (instancer != null && tier != PropTier.FullGameObject)
                {
                    PropInstancer.PlaceProps(instancer, prefab,
                        new[] { new PropPlacement { position = pos, rotation = rot } },
                        tier, cellSize, root.transform);
                    return null;   // mesh went to the instancer; there is no GameObject to hand back
                }
                return Object.Instantiate(prefab, pos, rot * prefab.transform.rotation, root.transform);
            }

            if (tubes > 0 || mouths > 0)
                Debug.Log($"[Dungeon] {gen.Crawlways.Count} crawlway(s): {tubes} tube piece(s), {mouths} mouth(s).");
            return root;
        }

        /// <summary>
        /// Yaw for a corner tube whose authored openings are on its -Z and +X faces.
        ///
        /// A corner's two openings face <c>-inDir</c> (back the way you came) and
        /// <c>outDir</c>. There are exactly four perpendicular direction pairs and four yaw
        /// steps, so one authored corner covers every turn — and because a tube is symmetric
        /// end to end there is no left-hand/right-hand version to author. Found by TRYING the
        /// four steps rather than deriving a formula: the set comparison is obviously correct,
        /// where a closed form here is easy to get subtly wrong and hard to read.
        /// </summary>
        static Quaternion CornerYaw(Vector3Int inDir, Vector3Int outDir)
        {
            Vector3 openA = -(Vector3)inDir, openB = (Vector3)outDir;
            for (int k = 0; k < 4; k++)
            {
                Quaternion q = Quaternion.Euler(0f, k * 90f, 0f);
                Vector3 a = q * Vector3.back, b = q * Vector3.right;
                bool match = (Approx(a, openA) && Approx(b, openB)) ||
                             (Approx(a, openB) && Approx(b, openA));
                if (match) return q;
            }
            return Quaternion.LookRotation(openB); // unreachable for perpendicular pairs
        }

        /// <summary>
        /// Pick the tube piece and its yaw from a cell's 4-bit neighbour mask.
        ///
        /// ONE FUNCTION REPLACING v1's IsCorner/DirInto/DirOutOf. A path could name an "in" and
        /// an "out"; a graph cannot, so the connections themselves decide. Bits are +X, -X, +Z,
        /// -Z (CrawlwaySpec.MaskPosX and friends).
        ///
        /// Every piece is authored to ONE canonical orientation and rotated to fit, so the
        /// number of assets stays at five however tangled the network gets:
        ///   straight  run along Z            corner  openings on -Z and +X
        ///   tee       run along Z, hole +X   cross   all four
        ///   cap       single opening on -Z
        /// Yaw is found by TRYING the four 90-degree steps and comparing the resulting opening
        /// set, rather than by deriving a formula per case — the comparison is obviously correct
        /// where a closed form across five piece types is easy to get subtly wrong and hard to
        /// read.
        /// </summary>
        static bool TubePieceFor(int mask, bool hasChamber, Vector3Int chamberDir,
                                 List<WeightedPrefab> straight, List<WeightedPrefab> corner,
                                 List<WeightedPrefab> tee, List<WeightedPrefab> cross,
                                 List<WeightedPrefab> cap,
                                 ref List<WeightedPrefab> pool, ref Quaternion rot)
        {
            // Openings this cell needs, as world directions.
            var want = new List<Vector3Int>();
            for (int bit = 0; bit < 4; bit++)
                if ((mask & (1 << bit)) != 0) want.Add(CrawlwaySpec.DirOfBit(bit));

            // A chamber adds a SIDE opening for fitting purposes without being a tunnel link.
            if (hasChamber) want.Add(chamberDir);

            List<WeightedPrefab> chosen;
            Vector3[] canonical;
            switch (want.Count)
            {
                case 1: chosen = cap;      canonical = new[] { Vector3.back }; break;
                case 2:
                    // Straight if the two openings are opposite, corner if perpendicular.
                    chosen = (want[0] == -want[1]) ? straight : corner;
                    canonical = (want[0] == -want[1])
                        ? new[] { Vector3.back, Vector3.forward }
                        : new[] { Vector3.back, Vector3.right };
                    break;
                case 3: chosen = tee;      canonical = new[] { Vector3.back, Vector3.forward, Vector3.right }; break;
                case 4: chosen = cross;    canonical = new[] { Vector3.back, Vector3.forward, Vector3.right, Vector3.left }; break;
                default: return false;     // isolated cell — nothing sensible to place
            }

            // Fall back to the straight piece rather than dropping the cell: a hole in the tunnel
            // is far worse than a piece that does not quite match, and an unauthored cross or cap
            // is a likely state while a kit is being built up.
            if (chosen == null || chosen.Count == 0) chosen = straight;
            if (chosen == null || chosen.Count == 0) return false;

            for (int k = 0; k < 4; k++)
            {
                Quaternion q = Quaternion.Euler(0f, k * 90f, 0f);
                bool all = true;
                foreach (var c in canonical)
                {
                    Vector3 world = q * c;
                    bool matched = false;
                    foreach (var w in want)
                        if (Approx(world, (Vector3)w)) { matched = true; break; }
                    if (!matched) { all = false; break; }
                }
                if (all) { pool = chosen; rot = q; return true; }
            }

            // Unreachable for well-formed masks; place unrotated rather than leaving a gap.
            pool = chosen;
            rot = Quaternion.identity;
            return true;
        }

        /// <summary>
        /// Yaw for a tee tube authored with its run along +Z and its side opening on +X.
        ///
        /// Only two orientations are candidates, not four: the run must lie along the bore's
        /// axis, and a STRAIGHT tube is symmetric end to end, so flipping the run 180 degrees is
        /// visually identical while moving the side opening to the other hand. That symmetry is
        /// what makes one authored tee cover both, the same argument as the corner's four steps.
        /// </summary>
        static Quaternion TeeYaw(Vector3Int run, Vector3Int side)
        {
            Quaternion q = Quaternion.LookRotation((Vector3)run);
            if (Approx(q * Vector3.right, (Vector3)side)) return q;
            return Quaternion.LookRotation(-(Vector3)run);
        }

        static bool Approx(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 0.01f;

        /// <summary>
        /// Combine a plain prefab array and a weighted list into one pool, the plain entries
        /// counting as WEIGHT 1.
        ///
        /// Merging rather than letting the weighted list REPLACE the array, because the
        /// replacing version has a nasty failure: adding one weighted variant would silently
        /// drop the three prefabs already authored in the plain slot, and the symptom is a
        /// dungeon that suddenly uses one tube everywhere. Same reasoning as RoomStyle's prison
        /// and alcove pools, where the original single slot survives as a weight-1 entry so
        /// variants EXTEND rather than supersede.
        /// </summary>
        static List<WeightedPrefab> MergePool(GameObject[] plain, WeightedPrefab[] weighted)
        {
            var pool = new List<WeightedPrefab>();
            if (plain != null)
                foreach (var p in plain)
                    if (p != null) pool.Add(new WeightedPrefab { prefab = p, weight = 1f });
            if (weighted != null)
                foreach (var w in weighted)
                    if (w.prefab != null) pool.Add(w);
            return pool;
        }

        /// <summary>
        /// Deterministic weighted pick, using the SAME per-cell hash the uniform pick used —
        /// read as a 0..1 value instead of an index, so variety stays stable per (seed, depth)
        /// and still varies cell to cell. Identical shape to PickWall's weighting, deliberately:
        /// two ways of turning a weight into a choice would drift.
        /// </summary>
        static GameObject PickWeightedPrefab(List<WeightedPrefab> pool, Vector3Int cell, int salt)
        {
            if (pool == null || pool.Count == 0) return null;

            float total = 0f;
            foreach (var w in pool) total += Mathf.Max(0f, w.weight);
            if (total <= 0f) return pool[0].prefab;   // every weight muted — still render something

            float roll = Hash(cell, salt) / (float)0x7fffffff * total;
            foreach (var w in pool)
            {
                roll -= Mathf.Max(0f, w.weight);
                if (roll <= 0f) return w.prefab;
            }
            return pool[pool.Count - 1].prefab;       // float drift on the last bucket
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

        /// <summary>
        /// The broken edge around a pit's mouth, where the room's floor stops.
        ///
        /// NEEDS ITS OWN PASS because no existing rule covers this face. The wall emitter only
        /// fires on OPEN→SOLID faces, and at the opening level BOTH cells are open — the floor
        /// cell and the hole are both CellType.Room — so it skips the edge entirely. The pit's
        /// real walls exist a whole level below, which you cannot see from across the room. The
        /// result was a floor quad that simply stopped: correct geometry that reads as MISSING
        /// geometry rather than as a designed opening.
        ///
        /// Closest existing analogue is FrameFace, which likewise handles a face between two
        /// open cells rather than an open/solid boundary.
        /// </summary>
        public static GameObject BuildPitRims(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                              InstancedDungeonRenderer instancer = null)
        {
            var root = new GameObject("DungeonPitRims");
            root.transform.SetParent(parent, false);
            if (kit.pitRimPrefabs == null || kit.pitRimPrefabs.Length == 0) return root;
            if (gen.Pits.Count == 0) return root;

            int count = 0;
            foreach (var pit in gen.Pits)
            {
                foreach (var c in pit.Openings)
                {
                    foreach (var d in HDirs)
                    {
                        Vector3Int nb = c + d;

                        // Another opening of the same hole: an interior edge, not a rim.
                        if (gen.IsPitOpening(nb)) continue;
                        // Solid rock: the ordinary wall emitter already put a wall on this face.
                        if (!gen.Grid.InBounds(nb) || gen.Grid[nb] == CellType.Empty) continue;

                        // Bridge landings are NOT skipped, by choice — the edge continues
                        // unbroken under the deck, so the crossing looks laid ACROSS a broken
                        // hole rather than the hole being tidily finished where the bridge
                        // happens to meet it.

                        GameObject prefab = kit.pitRimPrefabs[Hash(c, 167 + DirIndex(d)) % kit.pitRimPrefabs.Length];
                        if (prefab == null) continue;

                        // On the face between the two cells, at the room's floor level.
                        Vector3 face = new Vector3(c.x + 0.5f + d.x * 0.5f,
                                                   c.y,
                                                   c.z + 0.5f + d.z * 0.5f) * cellSize;

                        // Forward points AWAY from the pit, toward the floor you stand on to
                        // look at it — the same convention as a wall facing into the room it is
                        // viewed from. `d` runs opening → floor, so it already is that vector.
                        Quaternion rot = Quaternion.LookRotation((Vector3)d);

                        // Offset in the piece's OWN frame, not world space: an edge offset is
                        // inherently directional ("how far over the hole"), so a world-space
                        // nudge would only be correct on the edges that happen to face that
                        // world axis and would push the others sideways along the lip — exactly
                        // the bug ladderOffset had.
                        Vector3 pos = face + rot * kit.pitRimOffset + parent.position;

                        if (instancer != null)
                        {
                            PropInstancer.PlaceProps(instancer, prefab,
                                new[] { new PropPlacement { position = pos, rotation = rot } },
                                PropTier.StaticDecor, cellSize, root.transform);
                        }
                        else
                        {
                            Object.Instantiate(prefab, pos, rot * prefab.transform.rotation, root.transform);
                        }
                        count++;
                    }
                }
            }

            if (count > 0)
                Debug.Log($"[Dungeon] {count} pit rim piece(s) placed.");
            return root;
        }

        static int DirIndex(Vector3Int d) => d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;

        /// <summary>Snap a world direction back to the grid axis it came from — walls are
        /// emitted with LookRotation(-d), so this recovers `d` for the socket record.</summary>
        static Vector3Int RoundDir(Vector3 v)
        {
            if (Mathf.Abs(v.x) >= Mathf.Abs(v.z))
                return new Vector3Int(v.x >= 0f ? 1 : -1, 0, 0);
            return new Vector3Int(0, 0, v.z >= 0f ? 1 : -1);
        }

        /// <summary>
        /// Lintel trim along the top edge of stair-shaft walls, where they meet the shaft's
        /// ceiling.
        ///
        /// A stairwell is the one place the player sees a FULL STOREY of wall in a single view,
        /// so its wall/ceiling seam is far more visible than the same junction anywhere else —
        /// everywhere else the eye is further away and the ceiling is read at a glancing angle.
        /// Left bare it is a hard 90 degree edge between two flat surfaces.
        ///
        /// THE EDGE IS THE UNDERSIDE OF A CEILING SLAB, NOT THE TOP OF A WALL. Descending a
        /// staircase you look at the ceiling over the space at the BOTTOM of the stairs, and it
        /// ends in a horizontal line where the shaft's wall carries on upward. That line is the
        /// slab's edge — at the BOTTOM of the upper stair cell, not the top.
        ///
        /// The test that finds exactly that face and nothing else: a wall face (solid
        /// neighbour) whose BELOW-AND-OUTWARD cell is OPEN. Open below-outward means there is a
        /// ceiling slab over that space and this face is where it stops.
        ///
        /// It also excludes the shaft's SIDE walls for free, which is what makes it the right
        /// test rather than a filtered version of the wrong one: beside a staircase the sealed
        /// envelope keeps the cell below-outward solid, so no slab edge exists there and no trim
        /// is emitted. Trimming the tops of those side walls — the first attempt — put a line
        /// along the shaft's shoulders, including on the outside faces you never see.
        /// </summary>
        public static GameObject BuildLintels(DungeonGenerator gen, DungeonKit kit, float cellSize, Transform parent,
                                              RoomStyle style = null, InstancedDungeonRenderer instancer = null)
        {
            var root = new GameObject("DungeonLintels");
            root.transform.SetParent(parent, false);

            bool anyGeneric = kit.lintelPrefabs != null && kit.lintelPrefabs.Length > 0;
            if (!anyGeneric && style == null) return root;
            if (gen.Stairs.Count == 0) return root;

            var grid = gen.Grid;
            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            // Stairs is keyed by cell index with all four footprint cells mapping to the same
            // Stair, so iterate the KEYS to visit each cell once rather than each stair once.
            int count = 0;
            foreach (var kv in gen.Stairs)
            {
                Vector3Int c = grid.Position(kv.Key);
                if (!grid.InBounds(c)) continue;

                // Style by the room the stair belongs to. An interior staircase keeps its cells
                // in Room.Cells and only changes their CellType, so RoomAt resolves it (§7) —
                // a corridor stair belongs to no room and falls back to the hallway set.
                GameObject[] slot = kit.lintelPrefabs;
                if (style != null)
                {
                    Room room = gen.RoomAt(c);
                    slot = (room != null ? style.LintelsFor(room.Type) : style.HallwayLintels()) ?? slot;
                }
                if (slot == null || slot.Length == 0) continue;

                foreach (var d in HDirs)
                {
                    if (Open(c + d)) continue;                       // only where a WALL exists
                    // ...and only where that wall is the EDGE OF A CEILING SLAB: the cell
                    // below-and-outward must be open, meaning there is a roofed space out there
                    // whose ceiling stops at this face. Solid below-outward (the sealed envelope
                    // flanking a staircase) means no slab edge, so no trim.
                    if (!Open(c + d - Vector3Int.up)) continue;

                    GameObject prefab = slot[Hash(c, 173 + DirIndex(d)) % slot.Length];
                    if (prefab == null) continue;

                    // On the wall face at the BOTTOM of this cell — which is the ceiling plane
                    // of the space below-outward, i.e. the slab's underside edge.
                    Vector3 face = new Vector3(c.x + 0.5f + d.x * 0.5f,
                                               c.y,
                                               c.z + 0.5f + d.z * 0.5f) * cellSize;

                    // Forward points into the shaft, matching how a wall faces the space it is
                    // viewed from. Offset is applied in the piece's OWN frame for the same
                    // reason ladderOffset is: "further into the shaft" is directional, so a
                    // world-space nudge is only correct on walls facing that world axis.
                    Quaternion rot = Quaternion.LookRotation(-(Vector3)d);
                    Vector3 pos = face + rot * kit.lintelOffset + kit.globalVisualOffset + parent.position;

                    if (instancer != null)
                    {
                        PropInstancer.PlaceProps(instancer, prefab,
                            new[] { new PropPlacement { position = pos, rotation = rot } },
                            PropTier.StaticDecor, cellSize, root.transform);
                    }
                    else
                    {
                        Object.Instantiate(prefab, pos, rot * prefab.transform.rotation, root.transform);
                    }
                    count++;
                }
            }

            if (count > 0)
                Debug.Log($"[Dungeon] {count} stair lintel piece(s) placed.");
            return root;
        }
    }

    /// <summary>
    /// Attached to every spawned prison gate — the future lockpick system's
    /// hook. Locked state itself lives on the HingedDoor component.
    /// </summary>
    public class PrisonDoorMarker : MonoBehaviour
    {
        // The spec itself, not an index. The old `prisonIndex` was resolved by BOUNDING-BOX
        // containment, which is the wrong test for a wide prison (its footprint is a 1x1
        // vestibule plus a wider pocket behind, so bboxes can enclose cells that aren't the
        // prison's). Nothing read the index yet, so this was cheap to correct before it grew a
        // consumer. Runtime-assigned; PrisonSpec is a plain class and does not serialize.
        [System.NonSerialized] public PrisonSpec prison;
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