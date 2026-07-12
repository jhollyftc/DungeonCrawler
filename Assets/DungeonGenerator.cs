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
        public Vector2Int prisonWidthRange = new Vector2Int(1, 2); // across the door
        public Vector2Int prisonDepthRange = new Vector2Int(1, 2); // away from the hallway
        [Tooltip("No prison may be placed within this many cells (XZ) of a staircase.")]
        public int prisonStairClearance = 2;
    }

    public class Room
    {
        public BoundsInt Bounds;               // bounding box of the footprint
        public RoomType Type = RoomType.Generic;
        /// <summary>Actual floor-plan cells. For box rooms this is the full
        /// bounds; for L/T/plus/notch shapes the corner bites are absent.
        /// Empty set = treat as a full box (legacy safety).</summary>
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();

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
        public List<BoundsInt> PrisonCells { get; } = new List<BoundsInt>();
        public List<DungeonDoor> Doors { get; } = new List<DungeonDoor>();
        /// <summary>Lattice points (cell-corner coords) + floor level + height where interior columns go.</summary>
        public List<(Vector3Int latticePoint, int yFloor, int heightCells)> ColumnPoints { get; }
            = new List<(Vector3Int, int, int)>();

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
            PlacePrisons();
            AssignRoomTypes();
            PlaceSatelliteRooms();
            PlanInteriorColumns();
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

                Grid[t1] = CellType.StairLower;
                Grid[t2] = CellType.StairLower;
                Grid[u1] = CellType.StairUpper;
                Grid[u2] = CellType.StairUpper;

                var stair = new Stair { Entry = entry, Dir = cd };
                Stairs[Grid.Index(t1)] = stair;
                Stairs[Grid.Index(t2)] = stair;
                Stairs[Grid.Index(u1)] = stair;
                Stairs[Grid.Index(u2)] = stair;

                door.HasInteriorStair = true;
                door.HasDoor = true;
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

            // --- Start & Exit: the two ends of the longest shortest-path in the
            // MST (graph diameter), via double-BFS. Hop distance is the right
            // metric — "rooms to traverse", not euclidean.
            int start = BfsFarthest(adj, 0, out _);
            int exit = BfsFarthest(adj, start, out int[] distFromStart);
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
            return null;
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
                    if (t == CellType.Room) roomAdjacencies++;
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
            PrisonCells.Clear();
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
            int offset = rng.Next(0, w); // where the door column sits within the width

            Vector3Int door = h + d;                    // footprint cell behind the doorway
            Vector3Int start = door - perp * offset;    // min corner along the width axis
            if (d.x < 0 || d.z < 0) start += d * (depth - 1); // min corner along the depth axis

            var fp = new BoundsInt(start, dAbs * depth + perp * w + up);

            // --- Validate ---
            foreach (var pos in fp.allPositionsWithin)
            {
                if (!Grid.InBounds(pos) || Grid[pos] != CellType.Empty) return;

                // Cells directly above/below must be solid, or the mesher would
                // leave a hole in the prison's floor/ceiling.
                Vector3Int above = pos + up, below = pos - up;
                if (Grid.InBounds(above) && Grid[above] != CellType.Empty) return;
                if (Grid.InBounds(below) && Grid[below] != CellType.Empty) return;

                // One-opening rule: the only open cell the footprint may touch
                // is its own door hallway cell. This single check keeps prisons
                // out of rooms, off other prisons, and from punching holes into
                // parallel corridors.
                foreach (var hd in HorizontalDirs)
                {
                    Vector3Int nb = pos + hd;
                    if (nb == h || fp.Contains(nb)) continue;
                    if (Grid.InBounds(nb) && Grid[nb] != CellType.Empty) return;
                }
            }

            // --- Stair clearance: no stair cell within the configured XZ radius
            // (and one level up/down) of the footprint. The door cell h sits
            // inside this expansion, so its surroundings are covered too.
            int c = cfg.prisonStairClearance;
            var check = new BoundsInt(
                fp.position - new Vector3Int(c, 1, c),
                fp.size + new Vector3Int(2 * c, 2, 2 * c));
            foreach (var pos in check.allPositionsWithin)
            {
                if (!Grid.InBounds(pos)) continue;
                CellType t = Grid[pos];
                if (t == CellType.StairLower || t == CellType.StairUpper) return;
            }

            // --- Commit ---
            Fill(fp, CellType.Prison);
            PrisonCells.Add(fp);
        }
    }
}