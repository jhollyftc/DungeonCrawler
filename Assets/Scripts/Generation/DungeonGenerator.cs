using System;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

namespace DungeonGen
{
    [Serializable]
    public class DungeonConfig
    {
        [Header("Depth / progression")]
        [Tooltip("Run depth. When a DepthProfile is assigned, it derives room count and grid size from this and gates room types. Without a profile, the explicit values below are used.")]
        public int depth = 1;
        public DepthProfile depthProfile;

        public Vector3Int gridSize = new Vector3Int(40, 4, 40);
        public int roomCount = 14;
        public Vector3Int roomMinSize = new Vector3Int(3, 1, 3);
        public Vector3Int roomMaxSize = new Vector3Int(7, 2, 7); // inclusive
        public int placementAttempts = 300;
        public int roomPadding = 1;   // min empty cells between rooms (and grid edge)

        [Header("Graph")]
        public int maxLoopEdges = 3;          // how many cycles to add back on top of the MST
        public float minLoopDetourRatio = 2.5f; // only add a loop if the MST detour is at least this many times longer

        [Header("Hallway costs")]
        public int newHallwayCost = 5;
        public int reuseHallwayCost = 1;
        public int newStairCost = 100;
        public int reuseStairCost = 4;

        [Header("Doors")]
        [Tooltip("If false, hallways may only connect at a room's floor level. If true, tall rooms can also be entered at upper levels (drop-in balconies — no ledge geometry yet).")]
        public bool allowUpperLevelDoors = false;
        [Tooltip("Chance that a carved MST-edge entrance gets a physical door (vs. an open arch). MST doors gate required routes — good future lock-and-key targets.")]
        [Range(0f, 1f)] public float mstDoorChance = 0.25f;
        [Tooltip("Chance for loop-edge entrances. Loop doors gate shortcuts — good future knock-down targets.")]
        [Range(0f, 1f)] public float loopDoorChance = 0.6f;
        [Tooltip("If a room's ONLY opening (counting colonnade arches and all levels) is a single entrance, upgrade it to a physical door with this chance. Sole entrances are where doors — and future locks — matter most.")]
        [Range(0f, 1f)] public float singleEntranceDoorChance = 0.9f;

        [Header("Prison cells")]
        public bool placePrisonCells = true;
        [Tooltip("Chance per eligible hallway wall slot. Long hallways have more slots, so they naturally collect more cells.")]
        [Range(0f, 0.5f)] public float prisonChance = 0.06f;
        [Tooltip("Cell width in tiles, across the doorway. Anything above 1 gets a 1x1 DOORWAY tile with the cell widening behind it — a wide mouth on a corridor is geometrically impossible (it would touch the hallway along its whole length, which the one-opening rule forbids and the mesher would render as an open side). Width is rolled once and SHRINKS to fit, so a site that can't take the rolled width falls back to a narrower cell instead of producing no prison at all.")]
        public Vector2Int prisonWidthRange = new Vector2Int(1, 2); // across the door
        [Tooltip("Cell depth in tiles, away from the hallway. For a WIDE cell this is the depth of the room BEHIND the doorway tile, so total depth is this + 1; for a width-1 cell it is the whole depth. Kept meaning 'how deep is the cell you stand in' rather than 'how far from the corridor', so the number reads the same either way.")]
        public Vector2Int prisonDepthRange = new Vector2Int(1, 2); // away from the hallway
        [Tooltip("No prison may be placed within this many cells (XZ) of a staircase.")]
        public int prisonStairClearance = 2;

        [Header("Room pits")]
        [Tooltip("Cut chasms across room floors, carve the space below, span them with a bridge and mount a climb-out ladder. Rooms only — a corridor cell must have solid rock above AND below it, so open-under-open cannot exist in a hallway.")]
        public bool placePits = true;
        [Tooltip("Chance per eligible ROOM. Start, Exit and Merchant are never eligible.")]
        [Range(0f, 1f)] public float pitChance = 0.25f;
        [Tooltip("How deep, in cells (3m each). Shrinks to fit: a room with only one clear level of rock beneath it gets a 1-deep pit rather than none.")]
        public int pitDepthCells = 2;
        [Tooltip("Chance a pit is 2 cells WIDE rather than 1 — a wider chasm reads as more of an obstacle and needs a longer bridge.")]
        [Range(0f, 1f)] public float pitWideChance = 0.35f;
        [Tooltip("Smallest room edge (cells) that may take a pit. A chasm needs floor on both sides to be a crossing rather than a trap.")]
        public int pitMinRoomEdge = 5;
        [Tooltip("Fewest opening cells for a pit to be worth cutting.")]
        public int pitMinCells = 3;
        [Tooltip("Span pits with a bridge. OFF makes every pit a walk-around obstacle, which the connectivity check then has to satisfy without a crossing — expect far fewer pits.")]
        public bool pitBridges = true;
        [Tooltip("Hard ceiling on pits per run. 0 = unlimited.")]
        public int pitMaxCount = 3;

        [Header("Junction plazas")]
        [Tooltip("Open corridor junctions and bends out into 2x2 plazas. Corridors are 1-wide structurally (a cell exists iff it is on an A* path), so this is a deliberate post-pass, not a width setting.")]
        public bool widenJunctions = true;
        [Tooltip("Chance per JUNCTION OR BEND — not per corridor cell. Straight runs are never candidates, so this is a small denominator and the value can be high without flooding the dungeon.")]
        [Range(0f, 1f)] public float junctionPlazaChance = 0.35f;
        [Tooltip("Chance a plaza gets a column at its centre. Reuses the interior-column system (ColumnPoints), so it renders through the kit's existing column slot with no new authoring. A pillared junction reads as deliberate architecture; an open one reads as a wide spot.")]
        [Range(0f, 1f)] public float junctionPlazaPillarChance = 0.5f;
        [Tooltip("Hard ceiling on plazas per run. 0 = unlimited.")]
        public int junctionPlazaMaxCount = 0;

        [Header("Hallway alcoves")]
        [Tooltip("Carve small recesses off corridors — statue nooks, shrine niches, collapsed digs, storage. Frequency, legal kinds and sizes come from the DepthProfile when one is assigned; the values below are the no-profile fallback.")]
        public bool placeAlcoves = true;
        [Tooltip("FALLBACK chance per VIABLE corridor face when no DepthProfile is assigned. Viable = the cell behind the face is solid rock, so directions running along the corridor are never rolled. 1 = consider every such face — still subject to door clearance, spacing and the one-opening rule, which reject roughly half in a dense dungeon. Unlike prisonChance this is NOT capped at 0.5: prisons roll per compass direction (most of them doomed), alcoves roll per real candidate.")]
        [Range(0f, 1f)] public float alcoveChance = 0.3f;
        [Tooltip("FALLBACK width range when no DepthProfile is assigned.")]
        public Vector2Int alcoveWidthRange = new Vector2Int(1, 2);
        [Tooltip("FALLBACK depth range when no DepthProfile is assigned.")]
        public Vector2Int alcoveDepthRange = new Vector2Int(1, 2);
        [Tooltip("FALLBACK maximum alcoves per run when no DepthProfile is assigned.")]
        public int alcoveMaxCount = 10;
        [Tooltip("No alcove within this many cells (XZ) of a staircase — same guard prisons use, since the stair envelope is the most fragile geometry in the dungeon.")]
        public int alcoveStairClearance = 2;
        [Tooltip("Minimum cells between one alcove's bounding box and the next, so they don't cluster into a honeycomb. Measured as a CHEBYSHEV box, so the excluded area grows as the square — 3 already covers 49 cells around each alcove.")]
        public int alcoveMinSpacing = 3;
        [Tooltip("An alcove mouth must be this many cells from any room DOORWAY — a recess in a threshold fights the reserved-threshold rule and the corner-post classifier. Also a CHEBYSHEV box, so this is brutally expensive: at 2 it excludes 25 cells around EVERY door, and since corridors are short and mostly run between doorways that was measured eating 45% of all candidate sites. 1 is enough to keep a mouth out of a threshold and its immediate neighbours; 0 still blocks the threshold cell itself.")]
        public int alcoveDoorClearance = 1;
        [Tooltip("Log the per-rule rejection tally every generate, so you can see WHICH rule is eating alcove sites while tuning. The tally is logged unconditionally when ZERO alcoves are carved (that's a fault worth reporting on its own); this adds it for the ordinary case where some succeeded but you want more.")]
        public bool debugAlcoves = false;

        [Header("Crawlways")]
        [Tooltip("Bore 1.5m crawl passages through solid rock between two places that are otherwise a long walk apart. The cells stay Empty — a crawlway is invisible to the grid and brings its own mesh and collider (see CrawlwaySpec). Frequency comes from the DepthProfile when one is assigned; the values below are the no-profile fallback.")]
        public bool placeCrawlways = true;
        [Tooltip("FALLBACK chance per VIABLE wall face when no DepthProfile is assigned. Viable = an open cell whose neighbour is solid rock, i.e. a face a bore could actually start from. Most rolls still fail — finding a legal FAR end is far stricter than carving an alcove — so this wants to be high.")]
        [Range(0f, 1f)] public float crawlwayChance = 0.35f;
        [Tooltip("FALLBACK maximum crawlways per run when no DepthProfile is assigned.")]
        public int crawlwayMaxCount = 3;
        [Tooltip("Longest bore, in cells (3m each). crouchSpeed is 1 m/s, so each cell is about three seconds in a tight tube with no combat option — 3 is a brisk squeeze, 5 is a genuine commitment. A turn buys more tension per second than length does.")]
        public int crawlwayMaxCells = 4;
        [Tooltip("A crawlway is only carved if the EXISTING walk between its two ends is at least this many times the bore length. THE RULE THAT MAKES A CRAWLWAY MEAN SOMETHING: without it a 4-cell bore happily connects two corridor cells twelve metres apart, which is a novelty rather than a shortcut. Same shape as minLoopDetourRatio, which scores loop edges by exactly this ratio.")]
        public float crawlwayMinDetourRatio = 6f;
        [Tooltip("No crawlway mouth within this many cells (XZ) of a staircase — the same guard prisons and alcoves use, since the sealed stair envelope is the most fragile geometry in the dungeon.")]
        public int crawlwayStairClearance = 2;
        [Tooltip("Minimum cells between one crawlway mouth and any other. Measured as a CHEBYSHEV box, so the excluded area grows as the square.")]
        public int crawlwayMinSpacing = 3;
        [Tooltip("A crawlway mouth must be this many cells from any room DOORWAY — a grate in a threshold fights the reserved-threshold rule and the corner-post classifier. Also a CHEBYSHEV box: at 2 it excludes 25 cells around EVERY door, which measurably ate 45% of all alcove sites. 1 is enough to keep a mouth out of a threshold and its immediate neighbours.")]
        public int crawlwayDoorClearance = 1;
        [Tooltip("Log the per-rule rejection tally every generate. Logged unconditionally when ZERO crawlways are bored; this adds it for the ordinary case where some succeeded but you want more. Expect 'no legal far end' to dominate — that is the pairing rule working, not a fault.")]
        public bool debugCrawlways = false;
    }

    public class Room
    {
        public BoundsInt Bounds;               // bounding box of the footprint
        public RoomType Type = RoomType.Generic;
        /// <summary>Actual floor-plan cells. For box rooms this is the full
        /// bounds; for L/T/plus/notch shapes the corner bites are absent.
        /// Empty set = treat as a full box (legacy safety).</summary>
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();

        /// <summary>
        /// Floor cells with NO FLOOR — a pit's openings. They remain in <see cref="Cells"/>
        /// (they are still part of the room, just floorless) so walls and styling resolve
        /// normally, but ANYTHING THAT STANDS SOMETHING ON THE FLOOR MUST SKIP THEM: props,
        /// zone classification, spawn points, navmesh sample points. Bridge cells are included
        /// — a deck is walkable but is not somewhere to put a chest.
        ///
        /// Lives on Room rather than only in the generator's pit registry because
        /// InteriorFloorCell is a Room property with no generator reference, and it feeds the
        /// player spawn, DungeonNavBaker's sample point and DungeonPathDebug's endpoints —
        /// all of which would otherwise happily pick a hole.
        /// </summary>
        public HashSet<Vector3Int> Holes = new HashSet<Vector3Int>();

        public bool Contains(Vector3Int c) =>
            Cells.Count > 0 ? Cells.Contains(c) : Bounds.Contains(c);

        public int CellCount =>
            Cells.Count > 0 ? Cells.Count : Bounds.size.x * Bounds.size.y * Bounds.size.z;

        public Vector3Int Center => new Vector3Int(
            Bounds.xMin + Bounds.size.x / 2,
            Bounds.yMin + Bounds.size.y / 2,
            Bounds.zMin + Bounds.size.z / 2);

        /// <summary>A floor cell guaranteed inside the footprint, nearest the
        /// bounding-box center — for spawn points and anything that must stand
        /// on actual room floor (an L-shape's bbox center can be in the notch).</summary>
        public Vector3Int InteriorFloorCell
        {
            get
            {
                var fallback = new Vector3Int(Bounds.xMin + Bounds.size.x / 2, Bounds.yMin, Bounds.zMin + Bounds.size.z / 2);
                if (Cells.Count == 0) return fallback;
                Vector3 c = new Vector3(Bounds.xMin + Bounds.size.x * 0.5f, Bounds.yMin, Bounds.zMin + Bounds.size.z * 0.5f);
                Vector3Int best = fallback; float bestD = float.MaxValue;
                foreach (var cell in Cells)
                {
                    if (cell.y != Bounds.yMin) continue;
                    if (Holes.Contains(cell)) continue;   // never hand out a cell with no floor
                    float d = (new Vector3(cell.x + 0.5f, cell.y, cell.z + 0.5f) - c).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = cell; }
                }
                return best;
            }
        }
    }

    /// <summary>
    /// A semantic doorway: the terminal face where a carved graph edge enters a
    /// room. Incidental hallway↔room adjacencies (colonnade runs) are NOT doors.
    /// HasDoor marks entrances that get a physical door asset; the rest render
    /// as open arches. Carries graph context for future lock-and-key or
    /// breakable-door systems.
    /// </summary>
    public class DungeonDoor
    {
        public int RoomIndex;
        public Vector3Int HallwayCell;
        public Vector3Int Direction;   // hallway -> room
        public bool OnLoopEdge;        // loop doors gate shortcuts; MST doors gate required routes
        public bool HasDoor;           // physical door vs open arch (decided at generation, deterministic)
        public bool IsElevated;        // entrance above the room's floor level
        public bool HasInteriorStair;  // elevated entrance served by an allocated interior staircase
        public int EdgeA, EdgeB;       // room indices of the graph edge this entrance belongs to
    }

    /// <summary>
    /// A wall-mounted ladder serving a drop-in elevated entrance (an elevated
    /// door that couldn't get an interior staircase). The ladder climbs the
    /// wall directly beneath the door opening, so the entrance stays two-way.
    /// </summary>
    public class LadderSpec
    {
        public Vector3Int BaseCell;   // ground-floor room cell at the ladder's foot
        public Vector3Int WallDir;    // from the ladder cell toward the wall it mounts on
        public int HeightCells;       // stories climbed (door level - room floor)
    }

    /// <summary>
    /// Pipeline owner. Each stage is a separate method so the visualizer can
    /// scrub through partial results. Deterministic: same seed, same dungeon.
    /// </summary>
    public class DungeonGenerator
    {
        public Grid3D<CellType> Grid { get; private set; }
        public List<Room> Rooms { get; } = new List<Room>();
        public List<DEdge> DelaunayEdges { get; private set; } = new List<DEdge>();
        public List<DEdge> MstEdges { get; private set; } = new List<DEdge>();
        public List<DEdge> LoopEdges { get; private set; } = new List<DEdge>();
        public Dictionary<int, Stair> Stairs { get; } = new Dictionary<int, Stair>();
        public int FailedEdges { get; private set; }
        /// <summary>Prison closets carved off corridors. See PrisonSpec for why this carries a
        /// full spec rather than the bare BoundsInt it used to: a bounding box cannot support a
        /// Feature prop, which needs the recess's Direction frame.</summary>
        public List<PrisonSpec> Prisons { get; } = new List<PrisonSpec>();
        public List<DungeonDoor> Doors { get; } = new List<DungeonDoor>();
        public List<LadderSpec> Ladders { get; } = new List<LadderSpec>();
        /// <summary>Lattice points (cell-corner coords) + floor level + height where interior columns go.</summary>
        public List<(Vector3Int latticePoint, int yFloor, int heightCells)> ColumnPoints { get; }
            = new List<(Vector3Int, int, int)>();
        /// <summary>Carved recesses off corridors. Their CELLS are ordinary Hallway in the grid
        /// (see AlcoveSpec) — this list is the only thing that knows they're alcoves.</summary>
        public List<AlcoveSpec> Alcoves { get; } = new List<AlcoveSpec>();
        /// <summary>Holes cut in room floors. See PitSpec for why these keep their own registry
        /// instead of joining Room.Cells/Bounds.</summary>
        public List<PitSpec> Pits { get; } = new List<PitSpec>();
        /// <summary>1.5m crawl passages bored through rock. Their cells stay CellType.Empty
        /// (see CrawlwaySpec) — this list is the ONLY thing that knows they exist, which is why
        /// they change nothing about how the dungeon generates or renders until Phase 2 places
        /// their geometry.</summary>
        public List<CrawlwaySpec> Crawlways { get; } = new List<CrawlwaySpec>();

        // Opening -> pit (the hole you fall through) and carved cell -> pit (the interior).
        readonly Dictionary<Vector3Int, PitSpec> pitOpenings = new Dictionary<Vector3Int, PitSpec>();
        readonly Dictionary<Vector3Int, PitSpec> pitCells = new Dictionary<Vector3Int, PitSpec>();

        /// <summary>This room-floor cell has NO floor — anything that stands things on a floor
        /// must skip it, and NeedsSlabBetween suppresses the slab beneath it.</summary>
        public bool IsPitOpening(Vector3Int c) => pitOpenings.ContainsKey(c);
        /// <summary>The pit owning a carved interior cell, or null.</summary>
        public PitSpec PitAt(Vector3Int c) => pitCells.TryGetValue(c, out var p) ? p : null;
        /// <summary>A bridge spans this opening, so it IS walkable despite having no floor.</summary>
        public bool IsBridgeCell(Vector3Int c) =>
            pitOpenings.TryGetValue(c, out var p) && p.BridgeCells.Contains(c);

        // Cell -> alcove. A dictionary rather than RoomAt's linear scan because the prop pass
        // and the hallway pass both query it per cell.
        readonly Dictionary<Vector3Int, AlcoveSpec> alcoveCells = new Dictionary<Vector3Int, AlcoveSpec>();

        /// <summary>The alcove owning this cell, or null.</summary>
        public AlcoveSpec AlcoveAt(Vector3Int c) => alcoveCells.TryGetValue(c, out var a) ? a : null;
        /// <summary>True if this cell was carved as part of an alcove. Note the cell's CellType is
        /// Hallway either way — this is the only way to tell them apart.</summary>
        public bool IsAlcoveCell(Vector3Int c) => alcoveCells.ContainsKey(c);

        // Cell -> prison, same reasoning as alcoveCells: the kit's capped-wall reservations ask
        // per face, so a linear scan over prisons would be O(prisons) per wall face.
        readonly Dictionary<Vector3Int, PrisonSpec> prisonCells = new Dictionary<Vector3Int, PrisonSpec>();

        /// <summary>The prison owning this cell, or null.</summary>
        public PrisonSpec PrisonAt(Vector3Int c) => prisonCells.TryGetValue(c, out var p) ? p : null;

        // Bore cell -> crawlway, plus the MOUTH cells (the open cells at either end) kept
        // separately: mouths are ordinary Room/Hallway cells that other systems own, and only
        // the spacing rule and Phase 2's wall suppression care about them.
        readonly Dictionary<Vector3Int, CrawlwaySpec> crawlCells = new Dictionary<Vector3Int, CrawlwaySpec>();
        readonly HashSet<Vector3Int> crawlMouths = new HashSet<Vector3Int>();

        /// <summary>The crawlway boring through this cell, or null. NOTE the cell's CellType is
        /// Empty either way — this is the only way to tell a bore from ordinary rock.</summary>
        public CrawlwaySpec CrawlwayAt(Vector3Int c) => crawlCells.TryGetValue(c, out var cw) ? cw : null;
        /// <summary>True if a crawlway bores through this (solid-typed) cell.</summary>
        public bool IsCrawlwayCell(Vector3Int c) => crawlCells.ContainsKey(c);
        /// <summary>True if this OPEN cell has a crawlway grate in one of its walls.</summary>
        public bool IsCrawlwayMouth(Vector3Int c) => crawlMouths.Contains(c);

        readonly DungeonConfig cfg;
        readonly Random rng;

        public DungeonGenerator(DungeonConfig config, int seed)
        {
            cfg = config;
            rng = new Random(seed);

            // When a depth profile is assigned, it derives room count and grid
            // size from run depth (and gates room types in the typing pass).
            if (cfg.depthProfile != null)
            {
                cfg.roomCount = cfg.depthProfile.RoomCountAt(cfg.depth);
                int edge = cfg.depthProfile.GridEdgeAt(cfg.depth);
                cfg.gridSize = new Vector3Int(edge, cfg.depthProfile.gridHeight, edge);
            }

            Grid = new Grid3D<CellType>(cfg.gridSize.x, cfg.gridSize.y, cfg.gridSize.z);
        }

        public void Generate()
        {
            PlaceRooms();
            Triangulate();
            BuildGraph();
            CarveHallways();
            AllocateInteriorStairs();
            AllocateLadders();
            PlacePrisons();
            AssignRoomTypes();
            PlaceSatelliteRooms();
            PlacePits();
            PlanInteriorColumns();
            WidenJunctions();
            PlaceAlcoves();
            PlaceCrawlways();
        }

        // ---------------- Stage 4: hallway carving ----------------

        void CarveHallways()
        {
            FailedEdges = 0;
            Doors.Clear();
            var pathfinder = new HallwayPathfinder(Grid, Stairs, new PathCosts
            {
                NewHallway = cfg.newHallwayCost,
                ReuseHallway = cfg.reuseHallwayCost,
                NewStair = cfg.newStairCost,
                ReuseStair = cfg.reuseStairCost,
            });

            // MST edges first (shortest first, so long corridors merge into
            // already-carved short ones), then loops. Deterministic tie-breaks.
            System.Comparison<DEdge> byLength = (x, y) =>
            {
                int c = EdgeLength(x).CompareTo(EdgeLength(y));
                if (c != 0) return c;
                c = x.A.CompareTo(y.A);
                return c != 0 ? c : x.B.CompareTo(y.B);
            };
            var ordered = new List<(DEdge e, bool required)>();
            var mst = new List<DEdge>(MstEdges); mst.Sort(byLength);
            var loops = new List<DEdge>(LoopEdges); loops.Sort(byLength);
            foreach (var e in mst) ordered.Add((e, true));
            foreach (var e in loops) ordered.Add((e, false));

            foreach (var (e, required) in ordered)
            {
                var seeds = DoorCandidates(Rooms[e.A]);
                var goals = DoorCandidates(Rooms[e.B]);

                // Heuristic target box should match where doors can actually be:
                // the floor level, plus floor+1 when elevated doors are enabled.
                BoundsInt gb = Rooms[e.B].Bounds;
                int gbHeight = cfg.allowUpperLevelDoors ? Mathf.Min(2, gb.size.y) : 1;
                gb = new BoundsInt(gb.position, new Vector3Int(gb.size.x, gbHeight, gb.size.z));

                var path = pathfinder.FindPath(seeds, goals, gb);

                if (path == null)
                {
                    FailedEdges++;
                    if (required)
                        Debug.LogError($"[Dungeon] MST edge {e.A}->{e.B} failed to carve — dungeon is disconnected. Regenerate with a new seed (or grow the grid / lower stair cost).");
                    continue; // failed loop edges are silently dropped
                }
                Commit(path);

                // The path's terminal cells are the SEMANTIC doorways of this
                // edge — every other hallway↔room adjacency the corridor creates
                // along the way is an incidental (colonnade) opening.
                RecordDoor(path[0].Cell, e.A, e, !required);
                RecordDoor(path[path.Count - 1].Cell, e.B, e, !required);
            }

            // Post-pass (grid is final now): suppress physical doors inside
            // colonnade runs. If an entrance has another open face right beside
            // it on the same wall, a shut door next to an open arch reads as a
            // joke — demote it to an arch. The record survives (HasDoor=false),
            // so future systems still know it's a real entrance.
            foreach (var door in Doors)
            {
                if (!door.HasDoor) continue;
                Vector3Int perp = new Vector3Int(
                    Mathf.Abs(door.Direction.z), 0, Mathf.Abs(door.Direction.x));
                if (IsRoomOpening(door.HallwayCell + perp, door.Direction) ||
                    IsRoomOpening(door.HallwayCell - perp, door.Direction))
                    door.HasDoor = false;
            }

            // Single-entrance upgrade: if a room's only opening — counting
            // colonnade arches and every level, not just recorded doorways —
            // is one lone entrance, it (almost always) deserves a door. Sole
            // entrances are where doors and future locks actually mean
            // something.
            int[] openings = CountRoomOpenings();
            foreach (var door in Doors)
            {
                if (door.HasDoor) continue;
                if (openings[door.RoomIndex] != 1) continue;
                if (rng.NextDouble() < cfg.singleEntranceDoorChance)
                    door.HasDoor = true;
            }
        }

        int[] CountRoomOpenings()
        {
            var counts = new int[Rooms.Count];
            for (int i = 0; i < Rooms.Count; i++)
            {
                BoundsInt b = Rooms[i].Bounds;
                for (int y = b.yMin; y < b.yMax; y++)
                    for (int z = b.zMin; z < b.zMax; z++)
                        for (int x = b.xMin; x < b.xMax; x++)
                            foreach (var d in HorizontalDirs)
                            {
                                var n = new Vector3Int(x, y, z) + d;
                                if (b.Contains(n) || !Grid.InBounds(n)) continue;
                                if (Grid[n] == CellType.Hallway) counts[i]++;
                            }
            }
            return counts;
        }

        bool IsRoomOpening(Vector3Int hallwayCell, Vector3Int d)
        {
            if (!Grid.InBounds(hallwayCell) || Grid[hallwayCell] != CellType.Hallway) return false;
            Vector3Int roomSide = hallwayCell + d;
            return Grid.InBounds(roomSide) && Grid[roomSide] == CellType.Room;
        }

        // ---------------- Stage 4b: interior stairs for elevated doors ----------------

        /// <summary>
        /// Every elevated entrance tries to allocate a staircase running
        /// straight through its doorway down into the room: exit = the hallway
        /// cell itself, top tread directly beneath the door cell, door cell
        /// becoming the stair's headroom. Stepping through the door puts you
        /// on the ramp — no landing needed, and it's a fully canonical Stair
        /// record, so the mesher, kit stair asset, collision, and reuse rules
        /// all apply unmodified. Success forces HasDoor (a mezzanine entrance
        /// is a formal door); failure demotes to a doorless drop-in that the
        /// future ladder pass can find via IsElevated &amp;&amp; !HasInteriorStair.
        /// </summary>
        void AllocateInteriorStairs()
        {
            Vector3Int up = Vector3Int.up;

            // Room-side threshold cell of every recorded door, by room. The
            // stair volume must never consume one, and after placement every
            // ground-level entrance (and stair foot) must still reach every
            // other — an elevated corner door above a ground-floor door
            // would otherwise wall that entrance off with its own staircase,
            // severing a possibly-required route. All doors exist by this
            // stage (satellites come later and refuse stair adjacency).
            var doorCellsByRoom = new Dictionary<int, List<Vector3Int>>();
            foreach (var d in Doors)
            {
                if (!doorCellsByRoom.TryGetValue(d.RoomIndex, out var list))
                    doorCellsByRoom[d.RoomIndex] = list = new List<Vector3Int>();
                list.Add(d.HallwayCell + d.Direction);
            }
            var stairFeetByRoom = new Dictionary<int, List<Vector3Int>>();

            foreach (var door in Doors)
            {
                if (!door.IsElevated) continue;

                BoundsInt rb = Rooms[door.RoomIndex].Bounds;
                Vector3Int h = door.HallwayCell;
                Vector3Int cd = -door.Direction;       // ascent: room interior -> door
                Vector3Int entry = h - cd * 3 - up;    // 3 cells into the room, at floor
                Vector3Int t1 = entry + cd;
                Vector3Int t2 = entry + cd * 2;        // directly beneath the door cell
                Vector3Int u1 = t1 + up;
                Vector3Int u2 = t2 + up;               // the door cell itself

                bool RoomCell(Vector3Int c) => rb.Contains(c) && Grid[c] == CellType.Room;

                if (!(RoomCell(entry) && RoomCell(t1) && RoomCell(t2) &&
                      RoomCell(u1) && RoomCell(u2)))
                {
                    // No space (another stair took it, or geometry edge case):
                    // this stays a drop-in. It must not have a physical door —
                    // a door opening onto a sheer drop is worse than an opening.
                    door.HasDoor = false;
                    continue;
                }

                // The stair volume must not consume another door's threshold
                // (u2 is this door's own). Covers both floors: a ground door
                // whose entry cell is t1/t2, or another elevated door at u1.
                Vector3Int ownThreshold = h + door.Direction;
                bool consumesThreshold = false;
                if (doorCellsByRoom.TryGetValue(door.RoomIndex, out var doorCells))
                    foreach (var dc in doorCells)
                        if (dc != ownThreshold && (dc == t1 || dc == t2 || dc == u1 || dc == u2))
                        {
                            consumesThreshold = true;
                            break;
                        }
                if (consumesThreshold)
                {
                    door.HasDoor = false; // drop-in fallback, same as no-space
                    continue;
                }

                // Tentatively place, then verify the room's ground floor is
                // still one connected space (a stair strip can pinch a small
                // room in two even without sitting on a threshold).
                Grid[t1] = CellType.StairLower;
                Grid[t2] = CellType.StairLower;
                Grid[u1] = CellType.StairUpper;
                Grid[u2] = CellType.StairUpper;

                if (!GroundFloorConnected(door.RoomIndex, entry, doorCellsByRoom, stairFeetByRoom))
                {
                    Grid[t1] = CellType.Room;
                    Grid[t2] = CellType.Room;
                    Grid[u1] = CellType.Room;
                    Grid[u2] = CellType.Room;
                    door.HasDoor = false; // drop-in fallback
                    continue;
                }

                var stair = new Stair { Entry = entry, Dir = cd };
                Stairs[Grid.Index(t1)] = stair;
                Stairs[Grid.Index(t2)] = stair;
                Stairs[Grid.Index(u1)] = stair;
                Stairs[Grid.Index(u2)] = stair;

                if (!stairFeetByRoom.TryGetValue(door.RoomIndex, out var feet))
                    stairFeetByRoom[door.RoomIndex] = feet = new List<Vector3Int>();
                feet.Add(entry);

                door.HasInteriorStair = true;
                door.HasDoor = true;
            }
        }

        /// <summary>
        /// True if every ground-level door threshold and every interior-stair
        /// foot in the room (including the candidate foot) can reach every
        /// other across the room's remaining floor cells. Elevated doors'
        /// thresholds live a story up and connect via their stair feet, so
        /// only the ground plane needs checking.
        /// </summary>
        bool GroundFloorConnected(int roomIndex, Vector3Int newFoot,
                                  Dictionary<int, List<Vector3Int>> doorCellsByRoom,
                                  Dictionary<int, List<Vector3Int>> stairFeetByRoom)
        {
            Room room = Rooms[roomIndex];
            int yFloor = room.Bounds.yMin;

            var required = new List<Vector3Int> { newFoot };
            if (doorCellsByRoom.TryGetValue(roomIndex, out var doorCells))
                foreach (var c in doorCells)
                    if (c.y == yFloor && room.Contains(c) && Grid[c] == CellType.Room)
                        required.Add(c);
            if (stairFeetByRoom.TryGetValue(roomIndex, out var feet))
                required.AddRange(feet);
            if (required.Count <= 1) return true;

            bool Walkable(Vector3Int c) =>
                c.y == yFloor && room.Contains(c) && Grid[c] == CellType.Room;

            var seen = new HashSet<Vector3Int> { required[0] };
            var queue = new Queue<Vector3Int>();
            queue.Enqueue(required[0]);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var d in HorizontalDirs)
                {
                    var n = c + d;
                    if (seen.Contains(n) || !Walkable(n)) continue;
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
            foreach (var c in required)
                if (!seen.Contains(c)) return false;
            return true;
        }

        // ---------------- Stage 4c: ladders for drop-in entrances ----------------

        /// <summary>
        /// Every elevated entrance that did NOT get an interior staircase
        /// (IsElevated &amp;&amp; !HasInteriorStair — no space, or the stair would
        /// have blocked another door) tries to claim a ladder instead: the
        /// column of room cells directly beneath its threshold, mounted on
        /// the wall below the opening (solid — the hallway behind it is
        /// elevated). Keeps the entrance two-way. Deterministic, no RNG.
        /// Failure leaves a pure one-way drop.
        /// </summary>
        void AllocateLadders()
        {
            foreach (var door in Doors)
            {
                if (!door.IsElevated || door.HasInteriorStair) continue;

                Room room = Rooms[door.RoomIndex];
                int yFloor = room.Bounds.yMin;
                Vector3Int tc = door.HallwayCell + door.Direction; // elevated threshold, room side
                int height = tc.y - yFloor;
                if (height <= 0) continue;

                // The climb column (floor up through the threshold) must be
                // open room cells, and the mount wall solid at every level
                // below the opening.
                bool ok = true;
                for (int y = yFloor; y <= tc.y && ok; y++)
                {
                    var c = new Vector3Int(tc.x, y, tc.z);
                    ok = room.Contains(c) && Grid[c] == CellType.Room;
                }
                for (int y = yFloor; y < tc.y && ok; y++)
                {
                    var w = new Vector3Int(door.HallwayCell.x, y, door.HallwayCell.z);
                    ok = !Grid.InBounds(w) || Grid[w] == CellType.Empty;
                }
                if (!ok) continue;

                Ladders.Add(new LadderSpec
                {
                    BaseCell = new Vector3Int(tc.x, yFloor, tc.z),
                    WallDir = -door.Direction,
                    HeightCells = height,
                });
            }
        }

        void RecordDoor(Vector3Int hallwayCell, int roomIndex, DEdge e, bool loopEdge)
        {
            Room room = Rooms[roomIndex];
            foreach (var d in HorizontalDirs)
            {
                if (!room.Contains(hallwayCell + d)) continue;

                // Corridor merging can land two edges on the same terminal cell
                // — dedupe by face so we never stack doors. Merge semantics
                // conservatively: the entrance counts as a loop only if EVERY
                // edge through it is a loop (a lock here would otherwise gate a
                // required route without the marker saying so).
                foreach (var existing in Doors)
                {
                    if (existing.HallwayCell == hallwayCell && existing.Direction == d)
                    {
                        existing.OnLoopEdge &= loopEdge;
                        return;
                    }
                }

                float chance = loopEdge ? cfg.loopDoorChance : cfg.mstDoorChance;
                Doors.Add(new DungeonDoor
                {
                    RoomIndex = roomIndex,
                    HallwayCell = hallwayCell,
                    Direction = d,
                    OnLoopEdge = loopEdge,
                    HasDoor = rng.NextDouble() < chance,
                    IsElevated = (hallwayCell + d).y > room.Bounds.yMin,
                    EdgeA = e.A,
                    EdgeB = e.B,
                });
                return;
            }
        }

        static readonly Vector3Int[] HorizontalDirs =
        {
            new Vector3Int( 1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 0, 1),
            new Vector3Int( 0, 0,-1),
        };

        HashSet<int> DoorCandidates(Room room)
        {
            var result = new HashSet<int>();
            var b = room.Bounds;
            // Doorways belong at the floor unless balconies are explicitly enabled.
            // Elevated candidates are floor+1 ONLY (higher would need chained
            // interior stairs) and require 2 in-footprint cells straight behind
            // the door (at door level and floor level) so the interior
            // staircase can fit — a cell-wise check, since a bite could sit
            // right behind an irregular room's wall.
            int yMax = cfg.allowUpperLevelDoors ? Mathf.Min(b.yMax, b.yMin + 2) : b.yMin + 1;
            foreach (var c in room.Cells)
            {
                if (c.y >= yMax) continue;
                foreach (var d in HorizontalDirs)
                {
                    var n = c + d;
                    if (room.Contains(n) || !Grid.InBounds(n)) continue;
                    if (c.y > b.yMin)
                    {
                        bool stairFits = true;
                        for (int k = 1; k <= 2 && stairFits; k++)
                        {
                            Vector3Int back = c - d * k;
                            if (!room.Contains(back) ||
                                !room.Contains(new Vector3Int(back.x, b.yMin, back.z)))
                                stairFits = false;
                        }
                        if (!stairFits) continue;
                    }
                    CellType t = Grid[n];
                    if ((t == CellType.Empty || t == CellType.Hallway) &&
                        HallwayPathfinder.SurroundingsOk(Grid, Stairs, n))
                        result.Add(Grid.Index(n));
                }
            }
            return result;
        }

        void Commit(List<PathStep> path)
        {
            Vector3Int up = Vector3Int.up;
            foreach (var s in path)
            {
                if (Grid[s.Cell] == CellType.Empty)
                    Grid[s.Cell] = CellType.Hallway;
                if (s.Type == StepType.Move) continue;

                // Recover the canonical up-form of the stair from the step.
                Vector3Int entryLow, cd;
                if (s.Type == StepType.StairUp) { entryLow = s.Cell - s.Dir * 3 - up; cd = s.Dir; }
                else                            { entryLow = s.Cell;                  cd = -s.Dir; }

                Vector3Int t1 = entryLow + cd, t2 = entryLow + cd * 2;
                if (Grid[t1] == CellType.StairLower) continue; // reused an existing stair

                Grid[t1] = CellType.StairLower;      Grid[t2] = CellType.StairLower;
                Grid[t1 + up] = CellType.StairUpper; Grid[t2 + up] = CellType.StairUpper;

                var stair = new Stair { Entry = entryLow, Dir = cd };
                Stairs[Grid.Index(t1)] = stair;      Stairs[Grid.Index(t2)] = stair;
                Stairs[Grid.Index(t1 + up)] = stair; Stairs[Grid.Index(t2 + up)] = stair;
            }
        }

        // ---------------- Stage 3: MST + loop edges ----------------

        float EdgeLength(DEdge e) =>
            Vector3.Distance(Rooms[e.A].Center, Rooms[e.B].Center);

        void BuildGraph()
        {
            MstEdges.Clear();
            LoopEdges.Clear();
            if (Rooms.Count < 2) return;

            // --- Kruskal. Deterministic tie-break on (A,B) so equal-length
            // edges always sort identically for a given seed.
            var sorted = new List<DEdge>(DelaunayEdges);
            sorted.Sort((x, y) =>
            {
                int c = EdgeLength(x).CompareTo(EdgeLength(y));
                if (c != 0) return c;
                c = x.A.CompareTo(y.A);
                return c != 0 ? c : x.B.CompareTo(y.B);
            });

            var parent = new int[Rooms.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int v) { while (parent[v] != v) v = parent[v] = parent[parent[v]]; return v; }

            var leftovers = new List<DEdge>();
            foreach (var e in sorted)
            {
                int ra = Find(e.A), rb = Find(e.B);
                if (ra == rb) { leftovers.Add(e); continue; }
                parent[ra] = rb;
                MstEdges.Add(e);
            }

            // --- Loop selection: score each leftover by how long the walk
            // between its endpoints is *through the MST* relative to the direct
            // edge. High ratio = the edge short-circuits a long detour = a loop
            // worth having. Random re-adding (vazgriz) mostly buys short,
            // pointless double-corridors instead.
            var adjacency = new List<(int to, float w)>[Rooms.Count];
            for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<(int, float)>();
            foreach (var e in MstEdges)
            {
                float w = EdgeLength(e);
                adjacency[e.A].Add((e.B, w));
                adjacency[e.B].Add((e.A, w));
            }

            var scored = new List<(DEdge e, float ratio)>();
            foreach (var e in leftovers)
            {
                float tree = TreeDistance(adjacency, e.A, e.B);
                float direct = EdgeLength(e);
                if (direct > 0.001f)
                    scored.Add((e, tree / direct));
            }
            scored.Sort((x, y) =>
            {
                int c = y.ratio.CompareTo(x.ratio); // descending
                if (c != 0) return c;
                c = x.e.A.CompareTo(y.e.A);
                return c != 0 ? c : x.e.B.CompareTo(y.e.B);
            });

            foreach (var (e, ratio) in scored)
            {
                if (LoopEdges.Count >= cfg.maxLoopEdges) break;
                if (ratio < cfg.minLoopDetourRatio) break; // sorted, so nothing below qualifies
                LoopEdges.Add(e);
                // Fold the loop into the adjacency so the next candidate is
                // scored against the graph as it now stands — avoids picking
                // two loops that short-circuit the same detour.
                float w = EdgeLength(e);
                adjacency[e.A].Add((e.B, w));
                adjacency[e.B].Add((e.A, w));
            }
        }

        /// <summary>Weighted shortest path through the current graph (Dijkstra, tiny n).</summary>
        static float TreeDistance(List<(int to, float w)>[] adj, int from, int to)
        {
            int n = adj.Length;
            var dist = new float[n];
            var done = new bool[n];
            for (int i = 0; i < n; i++) dist[i] = float.PositiveInfinity;
            dist[from] = 0f;

            for (int iter = 0; iter < n; iter++)
            {
                int u = -1; float best = float.PositiveInfinity;
                for (int i = 0; i < n; i++)
                    if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                if (u == -1 || u == to) break;
                done[u] = true;
                foreach (var (v, w) in adj[u])
                    if (dist[u] + w < dist[v]) dist[v] = dist[u] + w;
            }
            return dist[to];
        }

        // ---------------- Stage 2: 3D Delaunay ----------------

        void Triangulate()
        {
            // Integer room centers are frequently cospherical/coplanar, which is
            // exactly the degenerate input Bowyer-Watson hates. Deterministic
            // sub-cell jitter breaks general-position failures without moving
            // any center enough to matter for graph building.
            var pts = new List<Vector3>(Rooms.Count);
            foreach (var room in Rooms)
                pts.Add((Vector3)room.Center + new Vector3(Jitter(), Jitter(), Jitter()));

            DelaunayEdges = Delaunay3D.Triangulate(pts);

            // Safety net for tiny/degenerate cases (e.g. 3 collinear rooms):
            // fall back to the complete graph so MST always has something to chew on.
            if (DelaunayEdges.Count < Rooms.Count - 1 && Rooms.Count >= 2)
            {
                DelaunayEdges.Clear();
                for (int i = 0; i < Rooms.Count; i++)
                    for (int j = i + 1; j < Rooms.Count; j++)
                        DelaunayEdges.Add(new DEdge(i, j));
            }
        }

        float Jitter() => (float)(rng.NextDouble() - 0.5) * 0.02f;

        // ---------------- Stage 1: room scatter ----------------

        void PlaceRooms()
        {
            // --- Size plan: guaranteed grand/large slots + random fill, placed
            // largest-first (big rooms fit easiest on an empty grid and end up
            // well distributed instead of squeezed into leftovers).
            var plan = new List<Vector3Int>();
            var prof = cfg.depthProfile;
            if (prof != null)
            {
                if (prof.ThroneLegal(cfg.depth))
                {
                    int e = rng.Next(prof.grandRoomEdge.x, prof.grandRoomEdge.y + 1);
                    plan.Add(new Vector3Int(e, Mathf.Clamp(prof.grandRoomHeight, 1, Grid.Height), e));
                }
                int large = prof.LargeCountAt(cfg.depth);
                for (int i = 0; i < large && plan.Count < cfg.roomCount; i++)
                {
                    int ex = rng.Next(prof.largeRoomEdge.x, prof.largeRoomEdge.y + 1);
                    int ez = rng.Next(prof.largeRoomEdge.x, prof.largeRoomEdge.y + 1);
                    int ey = rng.Next(cfg.roomMinSize.y, cfg.roomMaxSize.y + 1);
                    plan.Add(new Vector3Int(ex, ey, ez));
                }
            }
            while (plan.Count < cfg.roomCount)
                plan.Add(new Vector3Int(
                    rng.Next(cfg.roomMinSize.x, cfg.roomMaxSize.x + 1),
                    rng.Next(cfg.roomMinSize.y, cfg.roomMaxSize.y + 1),
                    rng.Next(cfg.roomMinSize.z, cfg.roomMaxSize.z + 1)));
            plan.Sort((a, b) => (b.x * b.z).CompareTo(a.x * a.z));

            int pad = cfg.roomPadding;
            int triesPerEntry = Mathf.Max(30, cfg.placementAttempts / Mathf.Max(1, plan.Count));

            foreach (var size in plan)
            {
                int maxX = Grid.Width  - size.x - pad;
                int maxY = Grid.Height - size.y;
                int maxZ = Grid.Depth  - size.z - pad;
                if (maxX < pad || maxY < 0 || maxZ < pad) continue;

                for (int t = 0; t < triesPerEntry; t++)
                {
                    Vector3Int pos = new Vector3Int(
                        rng.Next(pad, maxX + 1),
                        rng.Next(0, maxY + 1),
                        rng.Next(pad, maxZ + 1));

                    var bounds = new BoundsInt(pos, size);
                    var cells = BuildFootprint(bounds);
                    if (FootprintBlocked(cells, pad)) continue;

                    var room = new Room { Bounds = bounds, Cells = cells };
                    Rooms.Add(room);
                    foreach (var c in cells) Grid[c] = CellType.Room;
                    break;
                }
            }
        }

        /// <summary>
        /// Builds a room footprint from its bounding box: usually the full box,
        /// but eligible rooms roll for a shape made by removing corner bites —
        /// L (one big bite), notch (one small bite), T (two bites on the same
        /// side), plus (four bites). Bites span the full room height; arms are
        /// kept at least 2 cells wide so doors and movement always fit.
        /// Straight walls only — everything stays axis-aligned.
        /// </summary>
        HashSet<Vector3Int> BuildFootprint(BoundsInt b)
        {
            var cells = new HashSet<Vector3Int>();
            for (int y = b.yMin; y < b.yMax; y++)
                for (int z = b.zMin; z < b.zMax; z++)
                    for (int x = b.xMin; x < b.xMax; x++)
                        cells.Add(new Vector3Int(x, y, z));

            var prof = cfg.depthProfile;
            if (prof == null) return cells;
            int sx = b.size.x, sz = b.size.z;
            if (Mathf.Min(sx, sz) < prof.shapeMinEdge) return cells;
            if (rng.NextDouble() >= prof.shapedRoomChance) return cells;

            int shape = rng.Next(0, 4); // 0=L, 1=notch, 2=T, 3=plus

            // Bite dimensions. Arms must stay >= 2 cells: single-bite shapes
            // can bite up to half; plus/T bite up to (edge-2)/2.
            int BiteBig(int edge)   => Mathf.Clamp(rng.Next(edge / 3, edge / 2 + 1), 1, edge - 2);
            int BiteSmall(int edge) => Mathf.Clamp(rng.Next(1, Mathf.Max(2, edge / 3)), 1, edge - 2);
            int BitePair(int edge)  => Mathf.Clamp(rng.Next(1, (edge - 2) / 2 + 1), 1, Mathf.Max(1, (edge - 2) / 2));

            void Bite(int cornerX, int cornerZ, int bx, int bz)
            {
                int x0 = cornerX == 0 ? b.xMin : b.xMax - bx;
                int z0 = cornerZ == 0 ? b.zMin : b.zMax - bz;
                for (int y = b.yMin; y < b.yMax; y++)
                    for (int z = z0; z < z0 + bz; z++)
                        for (int x = x0; x < x0 + bx; x++)
                            cells.Remove(new Vector3Int(x, y, z));
            }

            switch (shape)
            {
                case 0: // L — one big corner bite
                    Bite(rng.Next(0, 2), rng.Next(0, 2), BiteBig(sx), BiteBig(sz));
                    break;
                case 1: // notch — one small corner bite
                    Bite(rng.Next(0, 2), rng.Next(0, 2), BiteSmall(sx), BiteSmall(sz));
                    break;
                case 2: // T — two bites on the same side
                {
                    int bx = BitePair(sx), bz = BiteBig(sz);
                    int side = rng.Next(0, 2);
                    Bite(0, side, bx, bz);
                    Bite(1, side, bx, bz);
                    break;
                }
                case 3: // plus — all four corners bitten
                {
                    int bx = BitePair(sx), bz = BitePair(sz);
                    Bite(0, 0, bx, bz); Bite(1, 0, bx, bz);
                    Bite(0, 1, bx, bz); Bite(1, 1, bx, bz);
                    break;
                }
            }
            return cells;
        }

        /// <summary>Cell-wise overlap test: every footprint cell, expanded by
        /// the padding in all directions, must be Empty (no touching faces or
        /// corners with any existing room).</summary>
        bool FootprintBlocked(HashSet<Vector3Int> cells, int pad)
        {
            foreach (var c in cells)
                for (int dy = -pad; dy <= pad; dy++)
                    for (int dz = -pad; dz <= pad; dz++)
                        for (int dx = -pad; dx <= pad; dx++)
                        {
                            var p = new Vector3Int(c.x + dx, c.y + dy, c.z + dz);
                            if (!Grid.InBounds(p)) continue;
                            if (Grid[p] != CellType.Empty) return true;
                        }
            return false;
        }

        void Fill(BoundsInt b, CellType type)
        {
            for (int y = b.yMin; y < b.yMax; y++)
                for (int z = b.zMin; z < b.zMax; z++)
                    for (int x = b.xMin; x < b.xMax; x++)
                        Grid[x, y, z] = type;
        }

        // ---------------- Stage 6: room typing ----------------

        /// <summary>
        /// Labels rooms by structural role. Order: start &amp; exit (longest MST
        /// path), then budget-gated singletons (merchant on-path, throne
        /// off-path), then category counts, then generic. Deterministic per
        /// seed. Depth profile (if set) gates which types are legal.
        /// </summary>
        void AssignRoomTypes()
        {
            int n = Rooms.Count;
            for (int i = 0; i < n; i++) Rooms[i].Type = RoomType.Generic;
            if (n == 0) return;

            // MST adjacency (typing reads the required-route tree, not loops).
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (var e in MstEdges) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }

            // --- Start & Exit: a CLIMB. The player is dropped in at the bottom and
            // works their way out, so Start sits on the lowest occupied floor and Exit
            // on the highest.
            //
            // But choosing them by height ALONE would throw away what the old
            // graph-diameter choice silently guaranteed: a LONG critical path. Nothing
            // stops the deepest and topmost rooms being MST-adjacent (room Y is random
            // and the graph is built from Delaunay over room centres), and Merchant
            // placement scores rooms by how close they are to the MIDDLE of the
            // start→exit path — a two-room path has no middle, so the merchant would
            // land next to Start or Exit on those seeds. So among the extreme floors we
            // still maximize hop distance: the vertical narrative AND a path worth
            // walking. Throne ("largest room OFF the critical path") benefits for the
            // same reason.
            ChooseStartAndExit(adj, out int start, out int exit, out int[] distFromStart);
            Rooms[start].Type = RoomType.Start;
            Rooms[exit].Type = RoomType.Exit;

            // Critical path = start->exit through the MST. Mark membership.
            var onCritical = new bool[n];
            {
                int[] parent = BfsParents(adj, start);
                for (int v = exit; v != -1; v = parent[v]) onCritical[v] = true;
            }

            bool Free(int i) => Rooms[i].Type == RoomType.Generic;
            float Volume(int i) => Rooms[i].CellCount;

            bool merchantLegal = cfg.depthProfile == null ? cfg.depth >= 3 : cfg.depthProfile.MerchantLegal(cfg.depth);
            bool throneLegal   = cfg.depthProfile == null ? cfg.depth >= 6 : cfg.depthProfile.ThroneLegal(cfg.depth);

            // --- Merchant: ON the critical path (reliably found), prefer a
            // mid-path room (not adjacent to start/exit) for pacing.
            if (merchantLegal)
            {
                int best = -1; int bestScore = int.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (!Free(i) || !onCritical[i]) continue;
                    // Prefer rooms near the middle of the path: score = min
                    // distance to either end, maximized.
                    int d = Mathf.Min(distFromStart[i], distFromStart[exit] - distFromStart[i]);
                    if (d > bestScore) { bestScore = d; best = i; }
                }
                if (best != -1) Rooms[best].Type = RoomType.Merchant;
            }

            // --- Throne: OFF the critical path, the largest such room (optional
            // reward for explorers).
            if (throneLegal)
            {
                int best = -1; float bestVol = -1f;
                for (int i = 0; i < n; i++)
                {
                    if (!Free(i) || onCritical[i]) continue;
                    float v = Volume(i);
                    if (v > bestVol) { bestVol = v; best = i; }
                }
                // Fallback: if no off-path room exists (tiny dungeon), allow the
                // largest free room anywhere.
                if (best == -1)
                    for (int i = 0; i < n; i++)
                        if (Free(i) && Volume(i) > bestVol) { bestVol = Volume(i); best = i; }
                if (best != -1) Rooms[best].Type = RoomType.ThroneRoom;
            }

            // --- Categories: soft counts from the depth budget, assigned to
            // free rooms in a deterministic order. Larger categories take larger
            // rooms first where it reads (barracks big, shrine small) — but keep
            // it simple for v1: fill by descending room size, cycling types.
            var budget = new List<(RoomType type, int count)>();
            if (cfg.depthProfile != null)
                foreach (var cb in cfg.depthProfile.categories)
                {
                    int c = cb.CountAt(cfg.depth);
                    if (c > 0) budget.Add((cb.type, c));
                }
            else
            {
                // No profile: a sensible default so typing still shows something.
                budget.Add((RoomType.Barracks, 2));
                budget.Add((RoomType.Shrine, 1));
            }

            // Free rooms, largest first (deterministic tie-break on index).
            var freeRooms = new List<int>();
            for (int i = 0; i < n; i++) if (Free(i)) freeRooms.Add(i);
            freeRooms.Sort((a, b) =>
            {
                int c = Volume(b).CompareTo(Volume(a));
                return c != 0 ? c : a.CompareTo(b);
            });

            int fr = 0;
            foreach (var (type, count) in budget)
            {
                for (int k = 0; k < count && fr < freeRooms.Count; k++, fr++)
                    Rooms[freeRooms[fr]].Type = type;
            }
            // Remaining free rooms stay Generic.
        }

        /// <summary>
        /// Picks Start on the lowest occupied floor and Exit on the highest, choosing the
        /// PAIR that maximizes MST hop distance so the critical path stays long (see the
        /// call site for why that matters to Merchant/Throne). Also returns hop distances
        /// from Start, which the merchant's mid-path scoring needs.
        ///
        /// Room floor = `Bounds.yMin`: a tall room occupies several Y levels but you only
        /// walk its floor, so that's the level it belongs to.
        /// </summary>
        void ChooseStartAndExit(List<int>[] adj, out int start, out int exit, out int[] distFromStart)
        {
            int n = Rooms.Count;
            int lowY = int.MaxValue, highY = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                int y = Rooms[i].Bounds.yMin;
                if (y < lowY) lowY = y;
                if (y > highY) highY = y;
            }

            // Every room on one floor (possible at low depth / a short gridHeight):
            // there's no climb to express, so fall back to the graph diameter — exactly
            // the original behaviour.
            if (lowY == highY)
            {
                start = BfsFarthest(adj, 0, out _);
                exit = BfsFarthest(adj, start, out distFromStart);
                return;
            }

            start = -1; exit = -1; distFromStart = null;
            int bestDist = -1;
            for (int a = 0; a < n; a++)
            {
                if (Rooms[a].Bounds.yMin != lowY) continue;

                // One BFS per bottom-floor candidate. Rooms cap at 40 (DepthProfile
                // maxRoomCount) and only the bottom floor's rooms qualify, so this stays
                // trivially small — no need for anything cleverer.
                BfsFarthest(adj, a, out int[] dist);
                for (int b = 0; b < n; b++)
                {
                    if (b == a || Rooms[b].Bounds.yMin != highY) continue;
                    if (dist[b] <= bestDist) continue;
                    bestDist = dist[b];
                    start = a; exit = b; distFromStart = dist;
                }
            }

            // Safety net: no reachable bottom→top pair. The MST spans every room so this
            // shouldn't happen, but Start/Exit must never come back unassigned — the
            // spawner and the exit portal both key off them.
            if (start == -1 || exit == -1)
            {
                start = BfsFarthest(adj, 0, out _);
                exit = BfsFarthest(adj, start, out distFromStart);
            }
        }

        static int BfsFarthest(List<int>[] adj, int source, out int[] dist)
        {
            int n = adj.Length;
            dist = new int[n];
            for (int i = 0; i < n; i++) dist[i] = -1;
            var q = new Queue<int>();
            dist[source] = 0; q.Enqueue(source);
            int far = source;
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                if (dist[u] > dist[far]) far = u;
                foreach (int v in adj[u])
                    if (dist[v] == -1) { dist[v] = dist[u] + 1; q.Enqueue(v); }
            }
            return far;
        }

        static int[] BfsParents(List<int>[] adj, int source)
        {
            int n = adj.Length;
            var parent = new int[n];
            var seen = new bool[n];
            for (int i = 0; i < n; i++) parent[i] = -1;
            var q = new Queue<int>();
            seen[source] = true; q.Enqueue(source);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                foreach (int v in adj[u])
                    if (!seen[v]) { seen[v] = true; parent[v] = u; q.Enqueue(v); }
            }
            return parent;
        }

        // ---------------- Stage 7: satellite (closet) rooms ----------------

        /// <summary>
        /// Attaches small closet rooms to eligible host rooms based on host
        /// TYPE (throne->treasury, barracks->armory, ...). Satellites are real
        /// Room cells with their own type, connected by a single physical door
        /// to their host — and NOT part of the Delaunay/MST graph, so no
        /// corridor ever reaches them. Must run after typing (needs host types)
        /// and door records are added here directly.
        /// </summary>
        void PlaceSatelliteRooms()
        {
            if (cfg.depthProfile == null) return; // satellite rules live on the profile

            int n = Rooms.Count;
            for (int ri = 0; ri < n; ri++)
            {
                Room host = Rooms[ri];
                // Start/Exit/Merchant never host (kept clean); satellites can't host.
                if (host.Type == RoomType.Start || host.Type == RoomType.Exit ||
                    host.Type == RoomType.Merchant || IsSatelliteType(host.Type))
                    continue;

                var rule = cfg.depthProfile.SatelliteFor(host.Type, cfg.depth);
                if (rule == null) continue;
                if (!rule.Value.guaranteed && rng.NextDouble() >= rule.Value.chance) continue;

                TryAttachSatellite(host, rule.Value.satellite);
            }
        }

        static bool IsSatelliteType(RoomType t) =>
            t == RoomType.ChestVault || t == RoomType.Treasury || t == RoomType.Armory ||
            t == RoomType.Pantry || t == RoomType.Study || t == RoomType.Reliquary;

        /// <summary>Room containing this cell, or null if it's a hallway/stair/etc.</summary>
        public Room RoomAt(Vector3Int cell)
        {
            foreach (var room in Rooms)
                if (room.Contains(cell))
                    return room;

            // A pit's carved cells are deliberately absent from Room.Cells (see PitSpec), so
            // resolve them through the pit registry instead. Without this a pit interior has no
            // room, and the kit places GENERIC walls and floor down there while the room above
            // it is styled — a visible seam at exactly the place the player is looking.
            var pit = PitAt(cell);
            return pit?.Owner;
        }

        /// <summary>
        /// Does a horizontal slab belong between these two vertically-adjacent cells —
        /// i.e. a FLOOR for `upper` and a CEILING for `lower`?
        ///
        /// Both the greybox mesher and the kit placer used to answer this with "is the
        /// cell below solid?", which conflates two different situations:
        ///   - a MULTI-STORY ROOM, whose stacked cells are one open volume and must NOT
        ///     have a slab through the middle (why the rule existed), and
        ///   - two DIFFERENT spaces that merely happen to be stacked.
        /// The second case is a real generated layout: a satellite closet with a hallway
        /// routed directly above it. Both cells are open, so neither got a slab — the
        /// hallway lost its floor and the closet its ceiling, leaving a hole between two
        /// unrelated spaces (reported from play testing).
        ///
        /// Room identity is the discriminator: BuildFootprint writes a room's cells at
        /// every Y in its bounds, so both levels of a tall room resolve to the same Room
        /// instance, while a closet and the corridor above it never do. Interior stairs
        /// come along for free — their cells stay in Room.Cells (only the CellType
        /// changes), so a staircase's own levels still correctly get no slab.
        ///
        /// Callers keep their own cell-type guards (StairUpper takes no floor, StairLower
        /// no ceiling); this answers only the "same space or not" half.
        /// </summary>
        public bool NeedsSlabBetween(Vector3Int lower, Vector3Int upper)
        {
            if (!Grid.InBounds(lower) || Grid[lower] == CellType.Empty) return true;
            if (!Grid.InBounds(upper) || Grid[upper] == CellType.Empty) return true;

            // A staircase's two levels are ONE continuous flight: the upper cells are
            // written directly on top of the lower ones (Grid[t1 + up] = StairUpper), so
            // no slab belongs between them. This has to be explicit — a CORRIDOR stair's
            // cells aren't in any room, so the room test below wouldn't catch it, and the
            // kit placer's floor rule carries no cell-type guard at all: it relied purely
            // on the old "is below solid?" test to skip StairUpper cells. Without this,
            // every corridor staircase gets a floor tile through its middle.
            if (Grid[lower] == CellType.StairLower && Grid[upper] == CellType.StairUpper) return false;

            // A PIT OPENING is a hole: no slab beneath it, which is the entire mechanism. One
            // line here reaches collision, visuals AND the automap, because DungeonMesher,
            // DungeonKitPlacer and DungeonMapper all route their floor decision through this
            // method — so they cannot disagree about where the floor is.
            //
            // Runs before the room test on purpose: the opening and the pit cell below it are
            // NOT in the same room (pit cells keep their own registry, see PitSpec), so the
            // room-identity rule would otherwise return true and floor the hole over.
            if (IsPitOpening(upper)) return false;

            Room r = RoomAt(lower);
            return r == null || !ReferenceEquals(r, RoomAt(upper));
        }

        void TryAttachSatellite(Room host, RoomType satType)
        {
            // Satellite is 1 cell wide on the shared-wall axis (so exactly one
            // cell touches the host — a clean single doorway with no side gaps)
            // and 2 deep away from the host. A little closet.
            var dirs = new[] { Vector3Int.right, Vector3Int.left,
                               new Vector3Int(0,0,1), new Vector3Int(0,0,-1) };
            int rot = rng.Next(0, 4);

            for (int di = 0; di < 4; di++)
            {
                Vector3Int d = dirs[(di + rot) % 4];
                if (TryAttachOnSide(host, satType, d)) return;
            }
        }

        bool TryAttachOnSide(Room host, RoomType satType, Vector3Int d)
        {
            BoundsInt hb = host.Bounds;
            int y0 = hb.yMin;
            int depth2 = 2; // depth away from host wall

            bool alongX = d.z != 0; // wall runs along X when facing +/-Z
            int runMin = alongX ? hb.xMin : hb.zMin;
            int runMax = alongX ? hb.xMax : hb.zMax;

            for (int along = runMin; along < runMax; along++)
            {
                // Host-side door cell on this wall. For irregular hosts, the
                // bbox perimeter may be a bite — the door must open from an
                // actual host floor cell.
                Vector3Int doorHostCell = alongX
                    ? new Vector3Int(along, y0, d.z > 0 ? hb.zMax - 1 : hb.zMin)
                    : new Vector3Int(d.x > 0 ? hb.xMax - 1 : hb.xMin, y0, along);
                if (!host.Contains(doorHostCell)) continue;

                // Satellite footprint: 1 wide (aligned to the door cell), depth2 deep.
                Vector3Int satOrigin = alongX
                    ? new Vector3Int(along, y0, d.z > 0 ? hb.zMax : hb.zMin - depth2)
                    : new Vector3Int(d.x > 0 ? hb.xMax : hb.xMin - depth2, y0, along);
                Vector3Int satSize = alongX
                    ? new Vector3Int(1, 1, depth2)
                    : new Vector3Int(depth2, 1, 1);

                var satBounds = new BoundsInt(satOrigin, satSize);
                if (!SatelliteFits(satBounds)) continue;

                Fill(satBounds, CellType.Room);
                var satCells = new HashSet<Vector3Int>();
                foreach (var p in satBounds.allPositionsWithin) satCells.Add(p);
                Rooms.Add(new Room { Bounds = satBounds, Type = satType, Cells = satCells });

                Doors.Add(new DungeonDoor
                {
                    RoomIndex = Rooms.Count - 1,
                    HallwayCell = doorHostCell,
                    Direction = d,
                    OnLoopEdge = false,
                    HasDoor = true,
                    IsElevated = false,
                    HasInteriorStair = false,
                    EdgeA = -1, EdgeB = -1,
                });
                return true;
            }
            return false;
        }

        bool SatelliteFits(BoundsInt b)
        {
            // Every footprint cell must be in-bounds and Empty.
            foreach (var p in b.allPositionsWithin)
                if (!Grid.InBounds(p) || Grid[p] != CellType.Empty)
                    return false;

            // The satellite must touch a Room (its host) on exactly one face and
            // be surrounded by solid rock otherwise — so it can't accidentally
            // open into a second room or a corridor. Count Room-adjacencies.
            int roomAdjacencies = 0;
            var dirs = new[] { Vector3Int.right, Vector3Int.left,
                               new Vector3Int(0,0,1), new Vector3Int(0,0,-1) };
            foreach (var p in b.allPositionsWithin)
                foreach (var d in dirs)
                {
                    Vector3Int nb = p + d;
                    if (b.Contains(nb)) continue;
                    if (!Grid.InBounds(nb)) continue;
                    CellType t = Grid[nb];
                    if (t == CellType.Room)
                    {
                        // This shared face IS the closet's doorway — so it must not
                        // already be spoken for by a wall-mounted LADDER.
                        //
                        // ORDERING: AllocateLadders runs at stage 6, satellites at
                        // stage 9. The ladder pass does verify its mount wall is solid,
                        // but at stage 6 this cell is still Empty, so it passes — and
                        // three stages later a closet is carved into that exact face,
                        // putting a ladder across the closet's only door (reported from
                        // play testing: an elevated corner door laddered above a closet).
                        //
                        // The satellite yields rather than the ladder: a ladder is what
                        // keeps an elevated entrance TWO-WAY, and losing it would demote
                        // that room to a one-way drop. Satellites are chanced decoration,
                        // so skipping one costs far less than the connectivity does.
                        // (Prisons need no equivalent guard — their one-opening rule
                        // already rejects any footprint touching a Room, and their
                        // solid-above rule rejects the column under a hallway.)
                        foreach (var lad in Ladders)
                        {
                            bool inClimbColumn = lad.BaseCell.x == nb.x && lad.BaseCell.z == nb.z
                                              && nb.y >= lad.BaseCell.y
                                              && nb.y <= lad.BaseCell.y + lad.HeightCells;
                            if (inClimbColumn && nb + lad.WallDir == p) return false;
                        }
                        roomAdjacencies++;
                    }
                    else if (t != CellType.Empty) return false; // touches hallway/stair/prison -> reject
                }
            // Exactly one shared cell with the host (our 1-wide door edge).
            return roomAdjacencies == 1;
        }

        // ---------------- Stage 8: interior columns ----------------

        /// <summary>
        /// Plans free-standing column lattice points for grand rooms. Columns
        /// sit at cell-corner lattice points on a regular inset grid, span
        /// floor→ceiling (stacked segments), and are placed by the kit as
        /// prefabs — they don't occupy grid cells, so the floor stays walkable
        /// and collision comes from the prefab's collider. Skips points
        /// adjacent to doorways so a column never blocks a passage.
        /// </summary>
        void PlanInteriorColumns()
        {
            ColumnPoints.Clear();
            if (cfg.depthProfile == null) return;

            int spacing = Mathf.Max(1, cfg.depthProfile.columnSpacing);
            int inset = Mathf.Max(0, cfg.depthProfile.columnWallInset);

            // Room-side cells of every door/arch opening — a column lattice
            // point touching one of these cells would crowd the passage.
            var doorCells = new HashSet<Vector3Int>();
            foreach (var door in Doors)
                doorCells.Add(door.HallwayCell + door.Direction);

            foreach (var room in Rooms)
            {
                var rule = cfg.depthProfile.ColumnsFor(room.Type);
                if (rule == null) continue;

                BoundsInt b = room.Bounds;
                int edge = Mathf.Min(b.size.x, b.size.z);
                if (edge < rule.Value.minRoomEdge) continue;
                if (rng.NextDouble() >= rule.Value.chance) continue;

                int heightCells = b.size.y; // span the full room height

                // Lattice point (lx, lz) is the corner shared by cells
                // (lx-1, lz-1), (lx, lz-1), (lx-1, lz), (lx, lz). Interior
                // lattice points run from xMin+1..xMax-1; the inset pushes the
                // first ring further from the walls.
                for (int lx = b.xMin + inset; lx <= b.xMax - inset; lx += spacing)
                    for (int lz = b.zMin + inset; lz <= b.zMax - inset; lz += spacing)
                    {
                        // The 4 cells sharing this lattice corner. All must be
                        // real footprint cells (no columns in an L-shape's bite)
                        // and none may be a doorway cell (don't block passages).
                        var c00 = new Vector3Int(lx - 1, b.yMin, lz - 1);
                        var c10 = new Vector3Int(lx,     b.yMin, lz - 1);
                        var c01 = new Vector3Int(lx - 1, b.yMin, lz);
                        var c11 = new Vector3Int(lx,     b.yMin, lz);

                        if (!room.Contains(c00) || !room.Contains(c10) ||
                            !room.Contains(c01) || !room.Contains(c11)) continue;

                        // A pit opening is still in room.Cells (it IS a room cell, just
                        // floorless), so Contains passes and the column would hang over the
                        // chasm — stacked segments dangling in mid-air with no floor under them.
                        if (room.Holes.Contains(c00) || room.Holes.Contains(c10) ||
                            room.Holes.Contains(c01) || room.Holes.Contains(c11)) continue;

                        bool nearDoor =
                            doorCells.Contains(c00) || doorCells.Contains(c10) ||
                            doorCells.Contains(c01) || doorCells.Contains(c11);
                        if (nearDoor) continue;

                        ColumnPoints.Add((new Vector3Int(lx, b.yMin, lz), b.yMin, heightCells));
                    }
            }
        }

        // ---------------- Stage 5: prison cells ----------------

        void PlacePrisons()
        {
            Prisons.Clear();
            prisonCells.Clear();
            if (!cfg.placePrisonCells || cfg.prisonChance <= 0f) return;

            // Fixed iteration order + sequential RNG draws = deterministic per seed.
            // Rolling per wall slot is what makes density scale with hallway length:
            // long hallways simply expose more slots.
            for (int i = 0; i < Grid.Length; i++)
            {
                if (Grid[i] != CellType.Hallway) continue;
                Vector3Int h = Grid.Position(i);
                foreach (var d in HorizontalDirs)
                    if (rng.NextDouble() < cfg.prisonChance)
                        TryPlacePrison(h, d);
            }
        }

        void TryPlacePrison(Vector3Int h, Vector3Int d)
        {
            Vector3Int up = Vector3Int.up;
            Vector3Int perp = new Vector3Int(Mathf.Abs(d.z), 0, Mathf.Abs(d.x));
            Vector3Int dAbs = new Vector3Int(Mathf.Abs(d.x), 0, Mathf.Abs(d.z));

            int w = rng.Next(cfg.prisonWidthRange.x, cfg.prisonWidthRange.y + 1);
            int depth = rng.Next(cfg.prisonDepthRange.x, cfg.prisonDepthRange.y + 1);
            // Offset drawn as a NORMALIZED roll, not rng.Next(0, w). The shrink loop below
            // retries at smaller widths, and re-rolling per attempt would make the number of
            // RNG draws depend on how many attempts a site happened to need — which shifts
            // the stream for every later placement and breaks (seed, depth) determinism
            // (golden rule 4). Three draws, always, whatever happens below.
            double offsetRoll = rng.NextDouble();

            // WIDTH SHRINKS TO FIT rather than failing outright. The one-opening rule below
            // demands every footprint neighbour except the door cell be Empty, because two
            // adjacent OPEN cells get no wall between them (the mesher only walls an
            // open/solid boundary) — a prison alongside a corridor would be open to it down
            // its whole length. But `perp`, the width axis, is frequently the CORRIDOR'S OWN
            // run direction: a prison hanging off the side of an east-west hallway spans
            // east-west too, so every cell past the door sits against another hallway cell and
            // is rejected. Wide cells therefore only ever fit at dead ends and corners, and
            // authoring a width range above 2 silently produced NO prisons at all rather than
            // wider ones (field-reported). Trying narrower widths keeps the wide cells where
            // the geometry genuinely allows them and falls back to a narrow cell where it
            // doesn't, instead of throwing the whole site away.
            for (; w >= cfg.prisonWidthRange.x; w--)
            {
                int offset = Mathf.Clamp((int)(offsetRoll * w), 0, w - 1);
                if (TryPlacePrisonAt(h, d, perp, dAbs, up, w, depth, offset)) return;
            }
        }

        /// <summary>
        /// Validate and commit one candidate prison footprint. Split out of TryPlacePrison so
        /// the width-shrink loop can attempt several without duplicating the rules.
        /// </summary>
        // ---------------- Stage 10b: room pits ----------------

        /// <summary>
        /// Cut a chasm across a room's floor, carve the space beneath it, span it with a
        /// bridge and mount a ladder to climb out.
        ///
        /// PITS ARE A ROOMS-ONLY FEATURE and that is structural: HallwayPathfinder's
        /// SurroundingsOk requires solid rock above AND below every corridor cell, so
        /// open-under-open cannot exist in a hallway. A tall room is already a two-level open
        /// volume; a pit applies the same idea to a SUBSET of one room's cells.
        ///
        /// RUNS BEFORE PlanInteriorColumns, because a column planned on a lattice point over
        /// the hole would hang in mid-air. That means its rng draws shift columns, plazas and
        /// alcoves — but NOT rooms, prisons or satellites, whose layouts are already committed.
        /// </summary>
        void PlacePits()
        {
            Pits.Clear();
            pitOpenings.Clear();
            pitCells.Clear();
            if (!cfg.placePits || cfg.pitChance <= 0f) return;

            // Thresholds: a hole in a doorway would drop you through the entrance, and the
            // corner-post/archway classifiers assume a floor there.
            var doorCells = new HashSet<Vector3Int>();
            foreach (var d in Doors)
            {
                doorCells.Add(d.HallwayCell);
                doorCells.Add(d.HallwayCell + d.Direction);
            }

            for (int i = 0; i < Rooms.Count; i++)
            {
                // Fixed draws per room whatever happens (golden rule 4).
                double roll = rng.NextDouble();
                double axisRoll = rng.NextDouble();
                double posRoll = rng.NextDouble();
                double widthRoll = rng.NextDouble();
                double bridgeRoll = rng.NextDouble();

                if (Pits.Count >= cfg.pitMaxCount && cfg.pitMaxCount > 0) break;
                if (roll >= cfg.pitChance) continue;

                Room room = Rooms[i];

                // Never in the rooms the run depends on. Start and Exit must stay clean — a
                // pit at the spawn point or the portal is a bad first and last impression —
                // and the merchant is the one room the player is meant to reach easily.
                if (room.Type == RoomType.Start || room.Type == RoomType.Exit ||
                    room.Type == RoomType.Merchant) continue;

                if (room.Bounds.size.x < cfg.pitMinRoomEdge || room.Bounds.size.z < cfg.pitMinRoomEdge)
                    continue;

                TryCutPit(room, i, axisRoll, posRoll, widthRoll, bridgeRoll, doorCells);
            }

            if (Pits.Count > 0 && cfg.debugAlcoves)
                Debug.Log($"[Pits] {Pits.Count} pit(s) cut.");
        }

        void TryCutPit(Room room, int roomIndex, double axisRoll, double posRoll,
                       double widthRoll, double bridgeRoll, HashSet<Vector3Int> doorCells)
        {
            int yFloor = room.Bounds.yMin;
            var b = room.Bounds;

            // The chasm runs ACROSS the room: pick which axis it spans, then a band position on
            // the other one. A full-width strip is deliberate — a chasm you can walk around is
            // scenery, one you must cross is a decision.
            bool alongX = axisRoll < 0.5;                 // strip varies X, band fixed in Z
            int bandMin = alongX ? b.zMin : b.xMin;
            int bandMax = alongX ? b.zMax - 1 : b.xMax - 1;
            if (bandMax - bandMin < 2) return;            // no room for floor either side

            int width = widthRoll < cfg.pitWideChance ? 2 : 1;
            // Inset by 1 so there is always floor on both sides to stand on.
            int firstBand = bandMin + 1;
            int lastBand = bandMax - width;
            if (lastBand < firstBand) return;
            int band = Mathf.Clamp(firstBand + (int)(posRoll * (lastBand - firstBand + 1)),
                                   firstBand, lastBand);

            // Collect the openings — every floor cell of this room in the band.
            var openings = new List<Vector3Int>();
            foreach (var c in room.Cells)
            {
                if (c.y != yFloor) continue;
                if (Grid[c] != CellType.Room) continue;           // skip interior stairs etc.
                int v = alongX ? c.z : c.x;
                if (v < band || v >= band + width) continue;
                if (doorCells.Contains(c)) return;                // hole in a threshold — abort
                openings.Add(c);
            }
            if (openings.Count < cfg.pitMinCells) return;

            // DEPTH SHRINKS TO FIT: two cells where the rock allows it, one where it doesn't —
            // the same pattern prisons and alcoves use. Every cell for the full depth must be
            // solid rock, or the pit would open into a corridor or another room below.
            int depth = 0;
            for (int d = cfg.pitDepthCells; d >= 1; d--)
            {
                if (RockClearBelow(openings, yFloor, d)) { depth = d; break; }
            }
            if (depth == 0) return;

            // BRIDGE: one crossing, spanning the band at a chosen position along the strip.
            // Generator-owned rather than a prop precisely so the connectivity test below can
            // count on it — a prop could decline to place, and a cell-level flood-fill could
            // never see it anyway (§10, cell connectivity != navmesh connectivity).
            var bridge = new HashSet<Vector3Int>();
            if (cfg.pitBridges)
            {
                var spanValues = new List<int>();
                foreach (var c in openings)
                {
                    int s = alongX ? c.x : c.z;
                    if (!spanValues.Contains(s)) spanValues.Add(s);
                }
                spanValues.Sort();
                if (spanValues.Count > 0)
                {
                    int pick = spanValues[Mathf.Clamp((int)(bridgeRoll * spanValues.Count),
                                                      0, spanValues.Count - 1)];
                    foreach (var c in openings)
                        if ((alongX ? c.x : c.z) == pick) bridge.Add(c);
                }
            }

            // CONNECTIVITY: every doorway must still reach every other, treating openings as
            // holes and bridge cells as walkable. Without this a seed can produce a room whose
            // far half — and whatever door leads on from it — is unreachable.
            if (!PitLeavesRoomConnected(room, roomIndex, yFloor, openings, bridge)) return;

            // ---- Commit ----
            // alongX means the chasm RUNS along X (varying x, fixed z band), so you cross it
            // along Z — and vice versa. Recorded because the bridge has no other way to know
            // which way to face.
            var pit = new PitSpec
            {
                Owner = room,
                FloorY = yFloor,
                DepthCells = depth,
                CrossDirection = alongX ? new Vector3Int(0, 0, 1) : new Vector3Int(1, 0, 0),
            };
            foreach (var c in bridge) pit.BridgeCells.Add(c);

            foreach (var c in openings)
            {
                pit.Openings.Add(c);
                pitOpenings[c] = pit;

                // BRIDGE CELLS ARE NOT HOLES. Room.Holes means "no floor, nothing may stand
                // here", and it removes a cell from rz.Floor entirely — which also removes it
                // from the prop system's threshold FLOOD-FILL. Marking a deck as a hole
                // therefore made the bridge impassable to that flood-fill, so on a severing pit
                // every blocking placement failed the connectivity check and the room got no
                // props at all (real bug). A deck is walkable; it just isn't somewhere to put a
                // chest — which is what RESERVED means, and how doorways are already handled.
                if (!pit.BridgeCells.Contains(c)) room.Holes.Add(c);
            }

            for (int d = 1; d <= depth; d++)
                foreach (var c in openings)
                {
                    var p = new Vector3Int(c.x, yFloor - d, c.z);
                    Grid[p] = CellType.Room;      // room VOLUME; RoomAt resolves it via PitAt
                    pit.Cells.Add(p);
                    pitCells[p] = pit;
                }

            Pits.Add(pit);

            // Escape ladder, reusing the existing LadderSpec + LadderClimbZone machinery
            // verbatim: a pit floor cell against a pit wall, climbing back to the room floor.
            AddPitLadder(pit, alongX);
        }

        /// <summary>Every cell for `depth` levels beneath the openings must be solid rock, or
        /// the pit would break into a corridor, a stair or another room below.</summary>
        bool RockClearBelow(List<Vector3Int> openings, int yFloor, int depth)
        {
            foreach (var c in openings)
                for (int d = 1; d <= depth; d++)
                {
                    var p = new Vector3Int(c.x, yFloor - d, c.z);
                    if (!Grid.InBounds(p) || Grid[p] != CellType.Empty) return false;
                }
            return true;
        }

        /// <summary>
        /// Flood-fill at room-floor level with the pit cut, checking every door threshold still
        /// reaches every other. Same shape as GroundFloorConnected, with two differences: an
        /// opening is impassable, and a BRIDGE cell is passable despite having no floor.
        /// </summary>
        bool PitLeavesRoomConnected(Room room, int roomIndex, int yFloor,
                                    List<Vector3Int> openings, HashSet<Vector3Int> bridge)
        {
            var holes = new HashSet<Vector3Int>(openings);

            var required = new List<Vector3Int>();
            foreach (var d in Doors)
            {
                if (d.RoomIndex != roomIndex) continue;
                Vector3Int t = d.HallwayCell + d.Direction;
                if (t.y == yFloor && room.Contains(t) && Grid[t] == CellType.Room && !holes.Contains(t))
                    required.Add(t);
            }
            if (required.Count <= 1) return true;

            bool Walkable(Vector3Int c) =>
                c.y == yFloor && room.Contains(c) && Grid[c] == CellType.Room &&
                (!holes.Contains(c) || bridge.Contains(c));

            var seen = new HashSet<Vector3Int> { required[0] };
            var queue = new Queue<Vector3Int>();
            queue.Enqueue(required[0]);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var d in HorizontalDirs)
                {
                    var n = c + d;
                    if (seen.Contains(n) || !Walkable(n)) continue;
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
            foreach (var c in required)
                if (!seen.Contains(c)) return false;
            return true;
        }

        /// <summary>
        /// Mount a climb-out ladder on a pit wall. Reuses LadderSpec unchanged, so the kit's
        /// existing ladder segments and LadderClimbZone handle it with no new machinery.
        ///
        /// NB NPCs cannot use it — ladders are invisible to NavMeshAgent (§10) — so a goblin
        /// knocked into a pit is stuck there until NpcLocomotion.CheckFall warps it back. That
        /// is a known limitation of ladders generally, not something pits introduce.
        /// </summary>
        void AddPitLadder(PitSpec pit, bool alongX)
        {
            // A pit-floor cell whose neighbour ACROSS the chasm's width is solid: that wall runs
            // the length of the pit, so the ladder always has something to mount on.
            Vector3Int wallDir = alongX ? new Vector3Int(0, 0, 1) : new Vector3Int(1, 0, 0);

            foreach (var c in pit.Cells)
            {
                if (c.y != pit.BottomY) continue;                 // stand on the pit floor
                foreach (var sign in new[] { 1, -1 })
                {
                    Vector3Int wd = wallDir * sign;
                    Vector3Int against = c + wd;
                    if (Grid.InBounds(against) && Grid[against] != CellType.Empty) continue;

                    Ladders.Add(new LadderSpec
                    {
                        BaseCell = c,
                        WallDir = wd,
                        HeightCells = pit.DepthCells,
                    });
                    return;
                }
            }
        }

        // ---------------- Stage 11: junction plazas ----------------

        /// <summary>
        /// Open corridor JUNCTIONS AND BENDS out into small 2x2 plazas, optionally with a
        /// column at the centre. Corridors are 1-wide structurally — a cell exists iff it sits
        /// on an A* path and Commit writes exactly that cell — so there is no thickness dial to
        /// turn; widening has to be a deliberate post-pass.
        ///
        /// JUNCTIONS AND BENDS ONLY, deliberately. Widening straight runs just makes corridors
        /// feel like rooms and eats the rock that prisons and alcoves need. Widening where
        /// routes MEET is what reads as "this is a place" rather than "the corridor got fat" —
        /// the same effect the prison vestibule produced, for the same reason: it rewards
        /// turning a corner.
        ///
        /// POSITION IN THE PIPELINE is chosen so it costs as little as possible:
        /// - AFTER PlacePrisons/Satellites/Columns, so its rng draws shift none of them and
        ///   every existing seed keeps its rooms, prisons, satellites and columns.
        /// - BEFORE PlaceAlcoves, so alcoves can hang off a plaza's new walls. That does shift
        ///   the alcove stream, which is acceptable: alcoves are new and still being tuned.
        /// </summary>
        void WidenJunctions()
        {
            if (!cfg.widenJunctions || cfg.junctionPlazaChance <= 0f) return;

            // Snapshot the corridor cells FIRST. Widening writes new Hallway cells, and a live
            // scan would then treat those as junction candidates and grow plazas outward
            // indefinitely — the same self-hosting trap alcoves have, which is worth stating
            // because the two features are one stage apart and the failure looks identical
            // (a creeping blob instead of a place).
            var corridor = new List<Vector3Int>();
            for (int i = 0; i < Grid.Length; i++)
                if (Grid[i] == CellType.Hallway) corridor.Add(Grid.Position(i));

            var plazaCells = new HashSet<Vector3Int>();
            int placed = 0, pillars = 0;

            foreach (var c in corridor)
            {
                if (cfg.junctionPlazaMaxCount > 0 && placed >= cfg.junctionPlazaMaxCount) break;

                // Fixed draws per candidate, whatever happens next (golden rule 4).
                double roll = rng.NextDouble();
                double quadRoll = rng.NextDouble();
                double pillarRoll = rng.NextDouble();

                if (!IsJunctionOrBend(c)) continue;
                if (roll >= cfg.junctionPlazaChance) continue;
                if (plazaCells.Contains(c)) continue;   // already absorbed into a neighbouring plaza

                // Four 2x2 blocks contain c; try them from a rolled starting quadrant so the
                // plaza isn't always biased to the same side of a junction.
                int start = Mathf.Clamp((int)(quadRoll * 4), 0, 3);
                for (int q = 0; q < 4; q++)
                {
                    int quad = (start + q) & 3;
                    int ox = (quad & 1) == 0 ? 0 : -1;
                    int oz = (quad & 2) == 0 ? 0 : -1;
                    Vector3Int min = new Vector3Int(c.x + ox, c.y, c.z + oz);

                    if (!PlazaBlockFits(min, out List<Vector3Int> newCells)) continue;

                    foreach (var n in newCells)
                    {
                        Grid[n] = CellType.Hallway;
                        plazaCells.Add(n);
                    }
                    plazaCells.Add(c);
                    placed++;

                    // The lattice point shared by the block's four cells is (min + 1) in XZ —
                    // see PlanInteriorColumns for the convention. Reuses ColumnPoints wholesale,
                    // so the kit's existing interior-column path renders it with no new slot.
                    if (pillarRoll < cfg.junctionPlazaPillarChance)
                    {
                        ColumnPoints.Add((new Vector3Int(min.x + 1, c.y, min.z + 1), c.y, 1));
                        pillars++;
                    }
                    break;
                }
            }

            if (placed > 0 && cfg.debugAlcoves)
                Debug.Log($"[Plazas] {placed} junction plaza(s) opened from {corridor.Count} corridor cell(s), " +
                          $"{pillars} with a central column.");
        }

        /// <summary>A corridor cell where routes MEET — 3+ open neighbours, or a genuine corner
        /// (exactly 2 that aren't opposite each other). A straight run has 2 collinear
        /// neighbours and is deliberately not a candidate.</summary>
        bool IsJunctionOrBend(Vector3Int c)
        {
            int open = 0;
            bool xOpen = false, zOpen = false;
            foreach (var d in HorizontalDirs)
            {
                Vector3Int n = c + d;
                if (!Grid.InBounds(n) || Grid[n] == CellType.Empty) continue;
                open++;
                if (d.x != 0) xOpen = true; else zOpen = true;
            }
            if (open >= 3) return true;
            return open == 2 && xOpen && zOpen;   // a bend, not a straight run
        }

        /// <summary>
        /// Can the 2x2 block with this min corner become a plaza? Reports the cells that would
        /// need carving (already-corridor cells cost nothing).
        /// </summary>
        bool PlazaBlockFits(Vector3Int min, out List<Vector3Int> newCells)
        {
            newCells = new List<Vector3Int>();

            for (int dx = 0; dx < 2; dx++)
                for (int dz = 0; dz < 2; dz++)
                {
                    Vector3Int p = new Vector3Int(min.x + dx, min.y, min.z + dz);
                    if (!Grid.InBounds(p)) return false;

                    CellType t = Grid[p];
                    if (t == CellType.Hallway) continue;      // already corridor, free
                    if (t != CellType.Empty) return false;    // room/prison/stair — never carve

                    // Solid above and below, and legal beside any staircase. Reusing the
                    // pathfinder's own predicate rather than restating it means a widened cell
                    // obeys exactly the rule every carved corridor cell already does — including
                    // the sealed stair envelope, which is the thing most easily broken here.
                    if (!HallwayPathfinder.SurroundingsOk(Grid, Stairs, p)) return false;

                    // A new cell must not open into anything that already validated its own
                    // boundaries against the OLD grid:
                    //   - Room: would punch a doorway with no door, no arch and no reserved
                    //     threshold, bypassing RecordDoor entirely.
                    //   - Prison: its one-opening rule was checked before this ran, so a second
                    //     opening here silently turns a cell into a through-passage.
                    foreach (var d in HorizontalDirs)
                    {
                        Vector3Int n = p + d;
                        if (!Grid.InBounds(n)) continue;
                        CellType nt = Grid[n];
                        if (nt == CellType.Room || nt == CellType.Prison) return false;
                    }

                    newCells.Add(p);
                }

            return newCells.Count > 0;   // nothing to do if the block is already all corridor
        }

        // ---------------- Stage 12: hallway alcoves ----------------

        /// <summary>
        /// Carve small recesses off corridors — a statue nook, a shrine niche, a collapsed dig,
        /// a storage pocket. The same validated shape prisons use (see RecessFits), typed as
        /// ordinary Hallway so the kit and mesher need no changes at all.
        ///
        /// RUNS LAST, AND THAT ORDERING IS LOAD-BEARING IN TWO WAYS:
        ///
        /// 1. DETERMINISM. Nothing draws from `rng` after this point, so appending the stage
        ///    shifts no existing stream — every seed keeps the rooms, prisons, satellites and
        ///    columns it had before alcoves existed, and merely gains alcoves (golden rule 4).
        ///
        /// 2. PRISONS GET FIRST REFUSAL. Alcove cells are typed Hallway, which IS a legal prison
        ///    host, whereas Prison is not a legal alcove neighbour (the one-opening rule rejects
        ///    it). So prisons-then-alcoves is safe, and alcoves-then-prisons would silently let
        ///    alcoves eat prison sites. Do not reorder these.
        /// </summary>
        void PlaceAlcoves()
        {
            Alcoves.Clear();
            alcoveCells.Clear();
            if (!cfg.placeAlcoves) return;

            var profile = cfg.depthProfile;
            float chance = profile != null ? profile.AlcoveChanceAt(cfg.depth) : cfg.alcoveChance;
            int maxCount = profile != null ? profile.alcoveMaxCount : cfg.alcoveMaxCount;
            var kinds = profile != null ? profile.AlcoveKindsAt(cfg.depth) : null;

            // Say WHY nothing was carved rather than producing a silent zero. Every one of these
            // is a config fault with no other symptom, and the commonest by far is a DepthProfile
            // asset that predates these fields: the generator then reads the profile (not the
            // DungeonVisualizer fallbacks, which are ignored whenever a profile is assigned) and
            // finds an unconfigured, all-zero alcove budget.
            if (chance <= 0f || maxCount <= 0 || (profile != null && (kinds == null || kinds.Count == 0)))
            {
                Debug.LogWarning(
                    $"[Alcoves] placeAlcoves is ON but none can be carved at depth {cfg.depth}: " +
                    $"chance={chance:0.###}, maxCount={maxCount}, legalKinds={(kinds == null ? "n/a (no profile)" : kinds.Count.ToString())}. " +
                    (profile != null
                        ? $"A DepthProfile IS assigned ({profile.name}), so its alcove fields are what count — the " +
                          "alcoveChance/alcoveWidthRange/alcoveMaxCount on the DungeonVisualizer are FALLBACKS used " +
                          "only when no profile is set. Check alcoveBaseChance, alcoveMaxCount and the alcoveKinds " +
                          "list on the profile asset; a profile created before alcoves existed may have them all at zero/empty."
                        : "No DepthProfile assigned, so the DungeonVisualizer's alcove fields are in use — raise alcoveChance/alcoveMaxCount."));
                return;
            }

            // Doorway cells are off limits — an alcove mouth in or beside a threshold fights the
            // reserved-threshold rule and the corner-post classifier.
            var doorCells = new HashSet<Vector3Int>();
            foreach (var door in Doors) doorCells.Add(door.HallwayCell);

            // Per-kind tallies for AlcoveRule.maxPerRun.
            var placedPerKind = new Dictionary<AlcoveKind, int>();

            int attempts = 0, viableFaces = 0;
            var rejects = new int[System.Enum.GetValues(typeof(AlcoveReject)).Length];

            // Same per-wall-slot roll as prisons, and for the same reason: density then scales
            // with corridor LENGTH for free, because a long hallway simply exposes more slots.
            for (int i = 0; i < Grid.Length; i++)
            {
                if (Alcoves.Count >= maxCount) break;
                if (Grid[i] != CellType.Hallway) continue;

                Vector3Int h = Grid.Position(i);
                foreach (var d in HorizontalDirs)
                {
                    if (Alcoves.Count >= maxCount) break;

                    // ONLY ROLL AGAINST A SOLID FACE. Of the four directions off a corridor cell,
                    // the ones running ALONG the corridor lead to more corridor — so on a straight
                    // run half the rolls were doomed before TryPlaceAlcove saw them, and at a
                    // junction three of four were. Those wasted rolls were being counted as
                    // "no room in the rock", which is how a measured 100 rejections looked like a
                    // geometry problem when it was really an accounting one.
                    //
                    // Filtering first makes `chance` mean what an author expects — per wall face
                    // an alcove could actually occupy — and roughly doubles the density a given
                    // setting produces. Safe to change the number of draws here ONLY because
                    // alcoves are the last stage and nothing downstream reads the stream.
                    Vector3Int behind = h + d;
                    if (!Grid.InBounds(behind) || Grid[behind] != CellType.Empty) continue;
                    viableFaces++;

                    if (rng.NextDouble() < chance)
                    {
                        attempts++;
                        var why = TryPlaceAlcove(h, d, kinds, doorCells, placedPerKind);
                        if (why != AlcoveReject.None) rejects[(int)why]++;
                    }
                }
            }

            // A valid budget that still carves nothing is a DIFFERENT fault from an empty budget,
            // and the two are indistinguishable from the outside — so always warn on zero, and
            // report the same tally on demand (debugAlcoves) when SOME were carved, which is the
            // case you actually tune against. Reporting only on zero would go quiet exactly when
            // you start asking "why so few?".
            if (attempts > 0 && (Alcoves.Count == 0 || cfg.debugAlcoves))
                Debug.Log(
                    $"[Alcoves] {Alcoves.Count} carved{(Alcoves.Count >= maxCount ? $" — HIT THE CAP (alcoveMaxCount {maxCount}); remaining faces were never rolled, raise it for more" : "")}. " +
                    $"{viableFaces} solid corridor face(s) available, " +
                    $"{attempts} rolled at chance {chance:0.###}. Rejected by — " +
                    $"chained-off-another-alcove: {rejects[(int)AlcoveReject.Chained]}, " +
                    $"too near a doorway (alcoveDoorClearance {cfg.alcoveDoorClearance}): {rejects[(int)AlcoveReject.DoorClearance]}, " +
                    $"too near another alcove (alcoveMinSpacing {cfg.alcoveMinSpacing}): {rejects[(int)AlcoveReject.Spacing]}, " +
                    $"kind budget full: {rejects[(int)AlcoveReject.KindBudget]}, " +
                    $"no room in the rock / too near a stair (alcoveStairClearance {cfg.alcoveStairClearance}): {rejects[(int)AlcoveReject.Geometry]}. " +
                    $"DOMINANT: {DominantRejectAdvice(rejects)}");
        }

        enum AlcoveReject { None, Chained, DoorClearance, Spacing, KindBudget, Geometry }

        /// <summary>
        /// Name the rule that actually ate the most sites, and what to do about it. Written
        /// because the first version of this message ASSERTED geometry was the usual culprit and
        /// the very first real run disagreed — door clearance was 45%. A tally that guesses its
        /// own conclusion is worse than one that just reports.
        /// </summary>
        static string DominantRejectAdvice(int[] rejects)
        {
            int worst = 0;
            for (int i = 1; i < rejects.Length; i++)
                if (rejects[i] > rejects[worst]) worst = i;
            if (rejects[worst] == 0) return "nothing — every rolled site was carved.";

            switch ((AlcoveReject)worst)
            {
                case AlcoveReject.DoorClearance:
                    return "doorway clearance. It's a Chebyshev box around EVERY door, so cost grows as the " +
                           "square — try alcoveDoorClearance 1, or 0 if mouths beside thresholds look fine.";
                case AlcoveReject.Spacing:
                    return "alcove spacing — lower alcoveMinSpacing, or accept that alcoves are meant to be rare.";
                case AlcoveReject.Geometry:
                    return "no room in the rock. Note this is measured on faces that ARE solid, so it is mostly " +
                           "STRUCTURAL rather than tunable: the one-opening rule needs the cells behind and beside " +
                           "the recess empty too, and wherever the rock between two corridors is only one cell " +
                           "thick, carving would join them into a shortcut with no wall between. Expect roughly " +
                           "half of all solid faces to fail this in a dense dungeon. Only worth chasing if " +
                           "alcoveStairClearance is above 1 (1 already keeps alcoves off every stair flank, which " +
                           "is all the sealed envelope needs) or a kind's depthRange minimum is 2+.";
                case AlcoveReject.Chained:
                    return "chaining off existing alcoves — expected at high chance, and the guard is doing its job.";
                case AlcoveReject.KindBudget:
                    return "per-kind maxPerRun budgets on the DepthProfile are full.";
                default:
                    return "n/a";
            }
        }

        AlcoveReject TryPlaceAlcove(Vector3Int h, Vector3Int d, List<AlcoveRule> kinds,
                                    HashSet<Vector3Int> doorCells, Dictionary<AlcoveKind, int> placedPerKind)
        {
            // ---- Draws happen FIRST and UNCONDITIONALLY ----
            // Four draws every attempt, whatever the geometry turns out to be. Making the count
            // depend on which rejections fired would tie the RNG stream to layout and break
            // (seed, depth) reproducibility — the same discipline PlacePrisons' offsetRoll
            // comment describes.
            double kindRoll = rng.NextDouble();
            double widthRoll = rng.NextDouble();
            double depthRoll = rng.NextDouble();
            double offsetRoll = rng.NextDouble();

            // ---- Alcove-only rejections ----

            // THE CRITICAL ONE. Alcove cells are typed Hallway, so an alcove is itself a legal
            // host for another alcove — and this pass carves DURING a single grid scan, so later
            // flat indices would see freshly-carved cells and chain recess off recess. The result
            // is branching tunnels, not niches. Prisons never hit this because they carve Prison,
            // which their own one-opening rule then rejects.
            if (IsAlcoveCell(h)) return AlcoveReject.Chained;

            if (doorCells.Contains(h)) return AlcoveReject.DoorClearance;
            int doorClear = Mathf.Max(0, cfg.alcoveDoorClearance);
            foreach (var dc in doorCells)
            {
                if (dc.y != h.y) continue;
                if (Mathf.Abs(dc.x - h.x) <= doorClear && Mathf.Abs(dc.z - h.z) <= doorClear) return AlcoveReject.DoorClearance;
            }

            // Spacing, so alcoves read as occasional discoveries rather than a honeycomb.
            int spacing = Mathf.Max(0, cfg.alcoveMinSpacing);
            foreach (var a in Alcoves)
            {
                if (Mathf.Abs(a.MouthCell.y - h.y) > 1) continue;
                Vector3 c = a.Bounds.center;
                if (Mathf.Abs(c.x - h.x) <= spacing && Mathf.Abs(c.z - h.z) <= spacing) return AlcoveReject.Spacing;
            }

            // ---- Kind, and its size envelope ----
            AlcoveKind kind;
            Vector2Int widthRange, depthRange;
            if (kinds != null && kinds.Count > 0)
            {
                if (!PickAlcoveKind(kinds, kindRoll, placedPerKind, out AlcoveRule rule)) return AlcoveReject.KindBudget;
                kind = rule.kind;
                widthRange = rule.widthRange;
                depthRange = rule.depthRange;
            }
            else
            {
                // No profile: one generic kind and the fallback ranges, so the feature still
                // works for quick config-only testing.
                kind = AlcoveKind.CollapsedDig;
                widthRange = cfg.alcoveWidthRange;
                depthRange = cfg.alcoveDepthRange;
            }

            int wMax = Mathf.Max(1, widthRange.y);
            int wMin = Mathf.Clamp(widthRange.x, 1, wMax);
            int dMax = Mathf.Max(1, depthRange.y);
            int dMin = Mathf.Clamp(depthRange.x, 1, dMax);

            int w = wMin + (int)(widthRoll * (wMax - wMin + 1));
            w = Mathf.Clamp(w, wMin, wMax);
            int depth = dMin + (int)(depthRoll * (dMax - dMin + 1));
            depth = Mathf.Clamp(depth, dMin, dMax);

            Vector3Int up = Vector3Int.up;
            Vector3Int perp = new Vector3Int(Mathf.Abs(d.z), 0, Mathf.Abs(d.x));
            Vector3Int dAbs = new Vector3Int(Mathf.Abs(d.x), 0, Mathf.Abs(d.z));

            // SHRINK BOTH DIMENSIONS to fit, not just width — the rock between two parallel
            // corridors is often only a cell or two thick, so a deep recess that can't fit
            // degrades to a shallow one instead of nothing.
            //
            // Honest note on why this exists: it was added believing depth was the dominant
            // cause of failed sites. It measurably was NOT (rejections went 100 -> 101). The
            // real cause was rolling against corridor-facing directions that were never solid,
            // fixed in PlaceAlcoves. This is kept because it's correct and costs nothing, but
            // don't infer from its presence that depth is what's limiting placement.
            //
            // DEPTH IS PRESERVED LONGEST (outer loop), because depth is what makes an alcove
            // somewhere you turn into rather than a dent in the wall. Width is spent first.
            // Costs at most widthRange x depthRange RecessFits calls — single digits — and draws
            // nothing further, so the RNG stream is unaffected however many shapes are tried.
            for (int tryDepth = depth; tryDepth >= dMin; tryDepth--)
            {
                for (int tryW = w; tryW >= wMin; tryW--)
                {
                    int offset = Mathf.Clamp((int)(offsetRoll * tryW), 0, tryW - 1);
                    if (!RecessFits(h, d, perp, dAbs, up, tryW, tryDepth, offset, cfg.alcoveStairClearance,
                                    out BoundsInt bbox, out List<Vector3Int> cells))
                        continue;

                    var spec = new AlcoveSpec
                    {
                        Kind = kind,
                        Bounds = bbox,
                        HallCell = h,
                        Direction = d,
                        MouthCell = h + d,
                        Width = tryW,
                        Depth = tryDepth,
                    };
                    foreach (var c in cells)
                    {
                        Grid[c] = CellType.Hallway;
                        spec.Cells.Add(c);
                        alcoveCells[c] = spec;
                    }
                    Alcoves.Add(spec);
                    placedPerKind.TryGetValue(kind, out int n);
                    placedPerKind[kind] = n + 1;
                    return AlcoveReject.None;
                }
            }

            // No width/depth combination down to the minimums fits here.
            return AlcoveReject.Geometry;
        }

        /// <summary>
        /// Weighted pick among the kinds legal at this depth, skipping any that has hit its
        /// maxPerRun. Takes the roll as a PARAMETER rather than drawing — the caller already
        /// drew it unconditionally, so a full budget rejects the attempt without changing how
        /// many numbers came off the stream.
        /// </summary>
        bool PickAlcoveKind(List<AlcoveRule> kinds, double roll,
                            Dictionary<AlcoveKind, int> placedPerKind, out AlcoveRule picked)
        {
            picked = default;

            float total = 0f;
            foreach (var r in kinds)
            {
                placedPerKind.TryGetValue(r.kind, out int used);
                if (r.maxPerRun > 0 && used >= r.maxPerRun) continue;
                total += Mathf.Max(0f, r.weight);
            }
            if (total <= 0f) return false;

            double t = roll * total;
            foreach (var r in kinds)
            {
                placedPerKind.TryGetValue(r.kind, out int used);
                if (r.maxPerRun > 0 && used >= r.maxPerRun) continue;
                t -= Mathf.Max(0f, r.weight);
                if (t <= 0d) { picked = r; return true; }
            }
            picked = kinds[kinds.Count - 1];   // float drift on the last bucket
            return true;
        }

        bool TryPlacePrisonAt(Vector3Int h, Vector3Int d, Vector3Int perp, Vector3Int dAbs,
                              Vector3Int up, int w, int depth, int offset)
        {
            if (!RecessFits(h, d, perp, dAbs, up, w, depth, offset, cfg.prisonStairClearance,
                            out BoundsInt bbox, out List<Vector3Int> cells))
                return false;

            // Every field here was already computed above and used to be discarded — recording
            // it costs nothing and is what lets prisons take authored contents (PrisonSpec).
            var spec = new PrisonSpec
            {
                Bounds = bbox,
                HallCell = h,
                Direction = d,
                MouthCell = h + d,
                Width = w,
                Depth = depth,
            };
            foreach (var c in cells)
            {
                Grid[c] = CellType.Prison;
                spec.Cells.Add(c);
                prisonCells[c] = spec;
            }
            Prisons.Add(spec);
            return true;
        }

        // ---------------- Crawlways ----------------

        enum CrawlReject { None, DoorClearance, Spacing, StairClearance, NoFarEnd, NotAShortcut, Disconnected, Floorless }

        static readonly Vector3Int[] VerticalDirs = { Vector3Int.up, Vector3Int.down };

        // Single-entry memo for the open-network BFS, which is by far the costliest part of the
        // crawlway stage and is asked the same question for all four faces of a cell.
        Dictionary<Vector3Int, int> walkCache;
        Vector3Int walkCacheFrom = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

        /// <summary>
        /// Bore 1.5m crawl passages between two places that are already connected but a long
        /// walk apart. Runs LAST, so nothing downstream reads the rng stream and appending it
        /// shifted no existing seed's rooms, prisons, satellites, columns or alcoves.
        ///
        /// THE ENDPOINTS ARE THE FEATURE; THE BORE BETWEEN THEM IS TRIVIAL. The tempting cheap
        /// version — bore blind and stop wherever you break through — needs no search and
        /// produces worthless crawlways, because a 4-cell tunnel surfacing in the same corridor
        /// twelve metres away is a novelty rather than a shortcut, and you cannot tell a good
        /// one from a bad one without knowing BOTH ends. So the far end is chosen deliberately
        /// and scored, and the scoring is one BFS that answers the only two questions that
        /// matter (see TryBoreCrawlway).
        /// </summary>
        void PlaceCrawlways()
        {
            Crawlways.Clear();
            crawlCells.Clear();
            crawlMouths.Clear();
            walkCache = null;
            walkCacheFrom = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            if (!cfg.placeCrawlways) return;

            var profile = cfg.depthProfile;
            float chance = profile != null ? profile.CrawlwayChanceAt(cfg.depth) : cfg.crawlwayChance;
            int maxCount = profile != null ? profile.crawlwayMaxCount : cfg.crawlwayMaxCount;

            // Say WHY nothing was bored rather than producing a silent zero. The commonest fault
            // by far is a DepthProfile asset that predates these fields: the generator reads the
            // profile (NOT the DungeonVisualizer fallbacks, which are ignored whenever a profile
            // is assigned) and finds an unconfigured, all-zero budget. §9's ScriptableObject
            // lesson — an asset does not reliably get a C# initializer added after it was made.
            if (chance <= 0f || maxCount <= 0 || cfg.crawlwayMaxCells <= 0)
            {
                Debug.LogWarning(
                    $"[Crawlways] placeCrawlways is ON but none can be bored at depth {cfg.depth}: " +
                    $"chance={chance:0.###}, maxCount={maxCount}, maxCells={cfg.crawlwayMaxCells}. " +
                    (profile != null
                        ? $"A DepthProfile IS assigned ({profile.name}), so ITS crawlway fields are what count — the " +
                          "crawlwayChance/crawlwayMaxCount on the DungeonVisualizer are FALLBACKS used only when no " +
                          "profile is set. Check crawlwayBaseChance and crawlwayMaxCount on the profile asset; one " +
                          "created before crawlways existed will have them at zero."
                        : "No DepthProfile assigned, so the DungeonVisualizer's crawlway fields are in use — raise crawlwayChance/crawlwayMaxCount."));
                return;
            }

            var doorCells = new HashSet<Vector3Int>();
            foreach (var door in Doors) doorCells.Add(door.HallwayCell);

            int attempts = 0, viableFaces = 0;
            var rejects = new int[System.Enum.GetValues(typeof(CrawlReject)).Length];

            for (int i = 0; i < Grid.Length; i++)
            {
                if (Crawlways.Count >= maxCount) break;

                // A mouth must sit in a space you can WALK in and that other systems treat as
                // ordinary. Stairs are excluded because the sealed 13-cell envelope makes them
                // the most fragile geometry in the dungeon; prisons because their whole
                // validation rests on having exactly one opening, and a grate would be a second
                // (an escape tunnel out of a cell is a good idea and a deliberate v2 — it needs
                // every prison consumer re-checked first, §12's category rule).
                CellType t = Grid[i];
                if (t != CellType.Room && t != CellType.Hallway) continue;

                Vector3Int a = Grid.Position(i);
                foreach (var d in HorizontalDirs)
                {
                    if (Crawlways.Count >= maxCount) break;

                    // ONLY ROLL AGAINST A SOLID FACE, for the reason the alcove pass records at
                    // length: rolling per compass direction counts doomed draws as rejections and
                    // makes `chance` mean something different from what an author expects.
                    Vector3Int first = a + d;
                    if (!Grid.InBounds(first) || Grid[first] != CellType.Empty) continue;
                    viableFaces++;

                    if (rng.NextDouble() < chance)
                    {
                        attempts++;
                        var why = TryBoreCrawlway(a, d, doorCells);
                        if (why != CrawlReject.None) rejects[(int)why]++;
                    }
                }
            }

            // Always report on zero (a valid budget that bores nothing is its own fault, and is
            // indistinguishable from an empty budget from outside); report the same tally on
            // demand when some succeeded, which is the case you actually tune against.
            if (attempts > 0 && (Crawlways.Count == 0 || cfg.debugCrawlways))
                Debug.Log(
                    $"[Crawlways] {Crawlways.Count} bored{(Crawlways.Count >= maxCount ? $" — HIT THE CAP (crawlwayMaxCount {maxCount})" : "")}. " +
                    $"{viableFaces} solid wall face(s) available, {attempts} rolled at chance {chance:0.###}. Rejected by — " +
                    $"too near a doorway (crawlwayDoorClearance {cfg.crawlwayDoorClearance}): {rejects[(int)CrawlReject.DoorClearance]}, " +
                    $"too near another crawlway (crawlwayMinSpacing {cfg.crawlwayMinSpacing}): {rejects[(int)CrawlReject.Spacing]}, " +
                    $"too near a stair (crawlwayStairClearance {cfg.crawlwayStairClearance}): {rejects[(int)CrawlReject.StairClearance]}, " +
                    $"no legal far end within crawlwayMaxCells {cfg.crawlwayMaxCells}: {rejects[(int)CrawlReject.NoFarEnd]}, " +
                    $"far end too close on foot (crawlwayMinDetourRatio {cfg.crawlwayMinDetourRatio:0.#}): {rejects[(int)CrawlReject.NotAShortcut]}, " +
                    $"a mouth would open over a pit: {rejects[(int)CrawlReject.Floorless]}, " +
                    $"ends not already connected: {rejects[(int)CrawlReject.Disconnected]}. " +
                    $"NB 'no legal far end' and 'too close on foot' dominating is the PAIRING RULE WORKING, not a fault — " +
                    $"most rock has nothing worth reaching on the other side. Raise crawlwayMaxCells to search further, " +
                    $"or lower crawlwayMinDetourRatio to accept shorter-cut crawlways.");
        }

        /// <summary>
        /// Try to bore from open cell <paramref name="a"/> in direction <paramref name="d"/>.
        ///
        /// Flood the ROCK outward from the first cell, up to crawlwayMaxCells. A cell may be
        /// EXPANDED THROUGH only if it touches no open cell but <paramref name="a"/> — that is
        /// the one-opening rule from RecessFits, and it generalises perfectly here: it stops a
        /// bore grazing a room or running alongside a corridor with a single cell of rock
        /// between. A cell that DOES touch open space is therefore TERMINAL: it can only ever be
        /// the last cell, and its open neighbour is a candidate far end.
        ///
        /// Then one BFS over the open network from <paramref name="a"/> scores every candidate,
        /// answering the only two questions that matter:
        ///   - IS THERE ALREADY A PATH? If not, these are two disconnected regions and joining
        ///     them would make the crawlway load-bearing for connectivity — papering over a
        ///     generator bug rather than adding a shortcut. Reject.
        ///   - IS THAT PATH LONG? A crawlway is worth boring only if the walk it replaces is
        ///     crawlwayMinDetourRatio times its own length.
        /// </summary>
        CrawlReject TryBoreCrawlway(Vector3Int a, Vector3Int d, HashSet<Vector3Int> doorCells)
        {
            if (!MouthOk(a, doorCells, out var mouthWhy)) return mouthWhy;

            Vector3Int up = Vector3Int.up;
            int maxCells = cfg.crawlwayMaxCells;

            // --- Flood the rock. `from` reconstructs each bore path; `openNeighbour` marks the
            // terminal cells and remembers which open cell they broke into.
            var from = new Dictionary<Vector3Int, Vector3Int>();
            var dist = new Dictionary<Vector3Int, int>();
            var candidates = new List<(Vector3Int bore, Vector3Int far)>();
            var queue = new Queue<Vector3Int>();

            bool BoreCellOk(Vector3Int c)
            {
                if (!Grid.InBounds(c) || Grid[c] != CellType.Empty) return false;
                if (crawlCells.ContainsKey(c)) return false;   // two bores must not intersect

                // SOLID BELOW IS REQUIRED, and for a concrete reason: the tube is FLOOR-ALIGNED
                // (a bore centred in the cell would put its sill 0.75m up, past maxStepHeight
                // 0.5 with no mantle mechanic — the player could not enter their own crawlway),
                // so its floor sits exactly at the cell base. An open cell below would have the
                // mesher emit that space's ceiling slab at the very same plane, and the two
                // z-fight.
                //
                // SOLID ABOVE IS DELIBERATELY NOT REQUIRED. An open cell above has its floor at
                // (y+1)*cellSize while the tube's ceiling is at y*cellSize + 1.5, so 1.5m of
                // rock separates them and nothing coincides. Crawlways may therefore run UNDER
                // rooms, which is one of the better things they can do.
                Vector3Int below = c - up;
                return !Grid.InBounds(below) || Grid[below] == CellType.Empty;
            }

            // How many open cells does `c` touch, other than `a`? Zero = safe to bore onward.
            // Any more and `c` is terminal, and each such neighbour is a candidate far end.
            void ClassifyAndEnqueue(Vector3Int c)
            {
                bool terminal = false;
                foreach (var hd in HorizontalDirs)
                {
                    Vector3Int nb = c + hd;
                    if (nb == a || !Grid.InBounds(nb) || Grid[nb] == CellType.Empty) continue;
                    terminal = true;
                    if (Grid[nb] == CellType.Room || Grid[nb] == CellType.Hallway)
                        candidates.Add((c, nb));
                }
                // Only a cell surrounded by rock may be bored THROUGH. A terminal cell that
                // yielded no legal far end (it broke into a stair or a prison) is a dead stop,
                // not a route onward — expanding past it would put an interior bore cell against
                // open space, which is exactly what the one-opening rule forbids.
                if (!terminal && dist[c] < maxCells) queue.Enqueue(c);
            }

            Vector3Int firstCell = a + d;
            if (!BoreCellOk(firstCell)) return CrawlReject.NoFarEnd;
            dist[firstCell] = 1;
            from[firstCell] = a;
            ClassifyAndEnqueue(firstCell);

            while (queue.Count > 0)
            {
                Vector3Int c = queue.Dequeue();
                foreach (var hd in HorizontalDirs)
                {
                    Vector3Int nb = c + hd;
                    if (dist.ContainsKey(nb) || !BoreCellOk(nb)) continue;
                    dist[nb] = dist[c] + 1;
                    from[nb] = c;
                    ClassifyAndEnqueue(nb);
                }
            }

            if (candidates.Count == 0) return CrawlReject.NoFarEnd;

            // --- Score. One BFS over the OPEN network gives the existing walk to every
            // candidate at once, so the cost is paid per attempt rather than per candidate —
            // and it is cached for the four faces of the same cell, which is sound ONLY because
            // this stage never mutates the grid (crawl cells stay Empty). If a future change
            // makes a crawlway carve anything, this cache must go.
            if (walkCacheFrom != a)
            {
                walkCache = OpenDistancesFrom(a);
                walkCacheFrom = a;
            }
            var walk = walkCache;

            Vector3Int bestBore = Vector3Int.zero, bestFar = Vector3Int.zero;
            int bestWalk = 0, bestLen = 0;
            float bestRatio = 0f;
            var worst = CrawlReject.NoFarEnd;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                Vector3Int bore = candidates[ci].bore, far = candidates[ci].far;

                if (!MouthOk(far, doorCells, out var why)) { if (worst == CrawlReject.NoFarEnd) worst = why; continue; }
                if (!walk.TryGetValue(far, out int w)) { worst = CrawlReject.Disconnected; continue; }

                int len = dist[bore];
                float ratio = w / (float)len;
                if (ratio < cfg.crawlwayMinDetourRatio) { worst = CrawlReject.NotAShortcut; continue; }

                // Highest ratio wins — the most walking saved per second spent crawling.
                // Deterministic tie-breaks: shorter bore, then grid order, so the same seed
                // always picks the same far end (golden rule 4).
                if (ratio > bestRatio ||
                    (ratio == bestRatio && (len < bestLen || (len == bestLen && Before(far, bestFar)))))
                {
                    bestBore = bore; bestFar = far; bestRatio = ratio; bestWalk = w; bestLen = len;
                }
            }

            if (bestRatio <= 0f) return worst;

            // --- Reconstruct the bore, A end first.
            var cells = new List<Vector3Int>();
            for (Vector3Int c = bestBore; c != a; c = from[c]) cells.Add(c);
            cells.Reverse();

            // Stair clearance over the WHOLE bore, not just its mouths — a tunnel passing
            // through the rock a staircase relies on is the same hazard as a recess dug into it.
            if (!StairClearOf(cells, cfg.crawlwayStairClearance)) return CrawlReject.StairClearance;

            var spec = new CrawlwaySpec
            {
                Cells = cells,
                CellA = a,
                CellB = bestFar,
                DirA = d,
                DirB = bestFar - bestBore,
                WalkDistance = bestWalk,
            };

            Crawlways.Add(spec);
            foreach (var c in cells) crawlCells[c] = spec;
            crawlMouths.Add(a);
            crawlMouths.Add(bestFar);
            return CrawlReject.None;
        }

        /// <summary>Deterministic ordering for tie-breaks. Vector3Int has no IComparable.</summary>
        static bool Before(Vector3Int p, Vector3Int q) =>
            p.y != q.y ? p.y < q.y : p.z != q.z ? p.z < q.z : p.x < q.x;

        /// <summary>Is this open cell a legal place to hang a grate?</summary>
        bool MouthOk(Vector3Int c, HashSet<Vector3Int> doorCells, out CrawlReject why)
        {
            why = CrawlReject.None;

            // A pit opening is a room cell in every structural respect and simply has no floor
            // (§12's category rule) — crawling out of a grate into thin air is exactly the
            // failure that rule exists to catch. Its own reject reason, not folded into
            // NoFarEnd: a tally that reports the wrong rule is worse than one that just counts.
            if (IsPitOpening(c)) { why = CrawlReject.Floorless; return false; }

            int dc = cfg.crawlwayDoorClearance;
            foreach (var door in doorCells)
                if (Chebyshev(door, c) <= dc) { why = CrawlReject.DoorClearance; return false; }

            int sp = cfg.crawlwayMinSpacing;
            foreach (var m in crawlMouths)
                if (Chebyshev(m, c) < sp) { why = CrawlReject.Spacing; return false; }

            return true;
        }

        /// <summary>XZ Chebyshev distance, with a large penalty per storey so cells on different
        /// floors never conflict — a grate directly above another is not a clustering problem,
        /// there is a whole floor slab between them.</summary>
        static int Chebyshev(Vector3Int p, Vector3Int q) =>
            Mathf.Max(Mathf.Abs(p.x - q.x), Mathf.Abs(p.z - q.z)) + Mathf.Abs(p.y - q.y) * 100;

        /// <summary>No stair cell within `c` cells (XZ, ±1 level) of any of these cells.</summary>
        bool StairClearOf(List<Vector3Int> cells, int c)
        {
            foreach (var cell in cells)
                for (int y = -1; y <= 1; y++)
                    for (int x = -c; x <= c; x++)
                        for (int z = -c; z <= c; z++)
                        {
                            Vector3Int p = cell + new Vector3Int(x, y, z);
                            if (!Grid.InBounds(p)) continue;
                            CellType t = Grid[p];
                            if (t == CellType.StairLower || t == CellType.StairUpper) return false;
                        }
            return true;
        }

        /// <summary>
        /// Walking distance in cells from `start` to every reachable open cell. Stairs count as
        /// open, so a route up a staircase is a route — which matters, because the vertical
        /// Start/Exit rule (§4) makes stair-heavy paths the long ones a crawlway most wants to
        /// short-circuit.
        /// </summary>
        Dictionary<Vector3Int, int> OpenDistancesFrom(Vector3Int start)
        {
            var dist = new Dictionary<Vector3Int, int> { [start] = 0 };
            var q = new Queue<Vector3Int>();
            q.Enqueue(start);
            while (q.Count > 0)
            {
                Vector3Int c = q.Dequeue();
                int nd = dist[c] + 1;
                foreach (var hd in HorizontalDirs)
                {
                    Vector3Int nb = c + hd;
                    if (dist.ContainsKey(nb) || !Grid.InBounds(nb) || Grid[nb] == CellType.Empty) continue;
                    dist[nb] = nd;
                    q.Enqueue(nb);
                }
                // Stairs link levels. A stair pair is written directly atop its lower cell, so
                // the vertical neighbours are what carry a route between floors.
                CellType t = Grid[c];
                Room roomHere = t == CellType.Room ? RoomAt(c) : null;
                foreach (var vd in VerticalDirs)
                {
                    Vector3Int nb = c + vd;
                    if (dist.ContainsKey(nb) || !Grid.InBounds(nb) || Grid[nb] == CellType.Empty) continue;
                    CellType tn = Grid[nb];
                    bool stairLink = t == CellType.StairLower || t == CellType.StairUpper ||
                                     tn == CellType.StairLower || tn == CellType.StairUpper;
                    // A tall room is ONE open volume (BuildFootprint writes a room's cells at
                    // every Y in its bounds), so its stacked cells connect too — the same
                    // discriminator NeedsSlabBetween uses to tell a multi-storey room from two
                    // unrelated spaces that merely happen to be stacked.
                    bool sameRoom = roomHere != null && tn == CellType.Room && roomHere == RoomAt(nb);
                    if (!stairLink && !sameRoom) continue;
                    dist[nb] = nd;
                    q.Enqueue(nb);
                }
            }
            return dist;
        }

        /// <summary>
        /// Can a validated RECESS — a dead-end pocket hanging off one hallway cell — be carved
        /// here? Shared by prisons and alcoves so the two can't drift apart on rules that took
        /// several passes to get right.
        ///
        /// Reports the cells and bounding box; commits NOTHING and draws NOTHING from `rng`, so
        /// a caller may probe several shapes without perturbing the stream (golden rule 4) and
        /// then writes whatever CellType it wants.
        /// </summary>
        /// <param name="h">The hallway cell the recess opens off. Not part of the footprint.</param>
        /// <param name="d">Direction from `h` into the recess.</param>
        /// <param name="stairClearance">XZ radius (and ±1 level) that must contain no stair cell.</param>
        bool RecessFits(Vector3Int h, Vector3Int d, Vector3Int perp, Vector3Int dAbs,
                        Vector3Int up, int w, int depth, int offset, int stairClearance,
                        out BoundsInt bbox, out List<Vector3Int> cells)
        {
            bbox = default;
            cells = null;

            Vector3Int door = h + d;                    // the DOORWAY tile, always 1x1

            // A WIDE recess gets a 1x1 doorway tile and widens BEHIND it. This is what makes
            // wide pockets possible at all: the one-opening rule forbids any footprint cell
            // touching an open cell other than `h`, and the width axis `perp` is usually the
            // CORRIDOR'S own run direction — so a wide mouth on a straight corridor always has
            // cells sitting against more corridor and is always rejected. Set back by one tile,
            // the wide part's near neighbours are `door ± perp`, the solid rock either side of
            // the doorway, so it never touches the hallway and a straight corridor becomes a
            // perfectly good host. It also simply looks right: a narrow door opening into a
            // larger cell, with the bars spanning one tile.
            //
            // Width 1 keeps the old plain rectangle — no vestibule, nothing about narrow
            // prisons changes.
            bool wide = w > 1;
            Vector3Int slabFront = wide ? door + d : door;

            Vector3Int start = slabFront - perp * offset;      // min corner along the width axis
            if (d.x < 0 || d.z < 0) start += d * (depth - 1);  // min corner along the depth axis
            var slab = new BoundsInt(start, dAbs * depth + perp * w + up);

            // The full footprint is the slab plus (when wide) the doorway tile. Membership has
            // to consider BOTH, or the one-opening rule below would treat the vestibule as an
            // intruding open cell and reject every wide prison it just enabled.
            bool InFootprint(Vector3Int p) => slab.Contains(p) || (wide && p == door);

            bool CellOk(Vector3Int pos)
            {
                if (!Grid.InBounds(pos) || Grid[pos] != CellType.Empty) return false;

                // Cells directly above/below must be solid, or the mesher would
                // leave a hole in the prison's floor/ceiling.
                Vector3Int above = pos + up, below = pos - up;
                if (Grid.InBounds(above) && Grid[above] != CellType.Empty) return false;
                if (Grid.InBounds(below) && Grid[below] != CellType.Empty) return false;

                // One-opening rule: the only open cell the footprint may touch
                // is its own door hallway cell. This single check keeps prisons
                // out of rooms, off other prisons, and from punching holes into
                // parallel corridors.
                foreach (var hd in HorizontalDirs)
                {
                    Vector3Int nb = pos + hd;
                    if (nb == h || InFootprint(nb)) continue;
                    if (Grid.InBounds(nb) && Grid[nb] != CellType.Empty) return false;
                }
                return true;
            }

            // --- Validate ---
            foreach (var pos in slab.allPositionsWithin)
                if (!CellOk(pos)) return false;
            if (wide && !CellOk(door)) return false;

            // Bounding box of the whole shape. Callers keep ONE entry per recess so consumers
            // work — the kit placer's `FindIndex(b => b.Contains(p))` for a marker's prison
            // index, and the visualizer's count. For a wide shape the bbox also covers the two
            // solid corners beside the doorway, which is harmless: nothing queries solid cells,
            // and recesses are separated by rock so boxes don't overlap.
            Vector3Int bbMin = Vector3Int.Min(slab.min, door);
            Vector3Int bbMax = Vector3Int.Max(slab.max, door + Vector3Int.one);
            bbox = new BoundsInt(bbMin, bbMax - bbMin);

            // --- Stair clearance: no stair cell within the configured XZ radius
            // (and one level up/down) of the footprint. The door cell h sits
            // inside this expansion, so its surroundings are covered too.
            int c = stairClearance;
            var check = new BoundsInt(
                bbox.position - new Vector3Int(c, 1, c),
                bbox.size + new Vector3Int(2 * c, 2, 2 * c));
            foreach (var pos in check.allPositionsWithin)
            {
                if (!Grid.InBounds(pos)) continue;
                CellType t = Grid[pos];
                if (t == CellType.StairLower || t == CellType.StairUpper) return false;
            }

            // The exact footprint. Order is irrelevant — every cell gets the same type — but the
            // SET must match what Fill(slab) plus the doorway tile used to write, or the grid
            // this produces differs from the pre-extraction version.
            cells = new List<Vector3Int>();
            foreach (var pos in slab.allPositionsWithin) cells.Add(pos);
            if (wide) cells.Add(door);
            return true;
        }
    }
}