using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A committed staircase, stored in canonical "up-traversal" form:
    /// Entry is the walkable cell at the LOWER level, Dir the horizontal
    /// direction of ascent. Exit = Entry + 3*Dir + up. The same physical
    /// stair serves the reverse (down) traversal automatically.
    /// </summary>
    public class Stair
    {
        public Vector3Int Entry;
        public Vector3Int Dir;
    }

    public enum StepType : byte { Move, StairUp, StairDown }

    public struct PathStep
    {
        public Vector3Int Cell;  // the cell arrived at (for stairs: the exit cell)
        public StepType Type;
        public Vector3Int Dir;   // horizontal direction of the move/traversal
    }

    public class PathCosts
    {
        public int NewHallway = 5;
        public int ReuseHallway = 1;
        public int NewStair = 100;
        public int ReuseStair = 4;
    }

    /// <summary>
    /// Stair-aware A* over the dungeon grid. Nodes are cells; stairs are
    /// atomic macro-edges (u -> u + 3d ± up) with a 4-cell footprint validated
    /// at expansion time. Tentative footprints along a path are tracked in an
    /// immutable cons-list shared between nodes via parent pointers, so the
    /// self-intersection check costs O(#stairs on path) with zero copying.
    ///
    /// Known tradeoff (see design doc §5): feasibility is path-dependent, so
    /// the closed set can in rare configurations prune a viable route. Any
    /// path this returns is valid; failure just means "carve order / retry".
    /// </summary>
    public class HallwayPathfinder
    {
        const byte EDGE_SEED = 255;

        static readonly Vector3Int[] Dirs =
        {
            new Vector3Int( 1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 0, 1),
            new Vector3Int( 0, 0,-1),
        };
        static readonly Vector3Int Up = new Vector3Int(0, 1, 0);

        class FootprintNode
        {
            public int C0, C1, C2, C3;
            public int EntryIdx, ExitIdx; // legit connection cells for this tentative stair
            public FootprintNode Parent;
        }

        struct HeapEntry
        {
            public int F, H, Order, Cell, G;
        }

        readonly Grid3D<CellType> grid;
        readonly Dictionary<int, Stair> stairs;
        readonly PathCosts costs;

        // Per-search state, allocated once.
        readonly int[] gScore;
        readonly int[] cameFrom;
        readonly byte[] cameEdge;
        readonly bool[] closed;
        readonly FootprintNode[] chain;
        readonly List<HeapEntry> heap = new List<HeapEntry>();
        int pushOrder;

        // Heuristic target box (goal room bounds, expanded by 1 in XZ).
        int gMinX, gMaxX, gMinY, gMaxY, gMinZ, gMaxZ;

        public HallwayPathfinder(Grid3D<CellType> grid, Dictionary<int, Stair> stairs, PathCosts costs)
        {
            this.grid = grid;
            this.stairs = stairs;
            this.costs = costs;
            gScore = new int[grid.Length];
            cameFrom = new int[grid.Length];
            cameEdge = new byte[grid.Length];
            closed = new bool[grid.Length];
            chain = new FootprintNode[grid.Length];
        }

        public List<PathStep> FindPath(IReadOnlyCollection<int> seeds, HashSet<int> goals, BoundsInt goalBounds)
        {
            if (seeds.Count == 0 || goals.Count == 0) return null;

            for (int i = 0; i < gScore.Length; i++)
            {
                gScore[i] = int.MaxValue;
                closed[i] = false;
                chain[i] = null;
            }
            heap.Clear();
            pushOrder = 0;

            gMinX = goalBounds.xMin - 1; gMaxX = goalBounds.xMax;     // xMax exclusive -> expanded max cell
            gMinZ = goalBounds.zMin - 1; gMaxZ = goalBounds.zMax;
            gMinY = goalBounds.yMin;     gMaxY = goalBounds.yMax - 1; // door candidates sit at room floor levels

            foreach (int s in seeds)
            {
                gScore[s] = 0;
                cameEdge[s] = EDGE_SEED;
                Push(s, 0, Heuristic(grid.Position(s)));
            }

            while (heap.Count > 0)
            {
                HeapEntry top = Pop();
                int u = top.Cell;
                if (closed[u] || top.G != gScore[u]) continue; // stale entry
                closed[u] = true;

                if (goals.Contains(u))
                    return Reconstruct(u);

                Vector3Int pos = grid.Position(u);
                FootprintNode uChain = chain[u];
                int uG = gScore[u];

                for (int d = 0; d < 4; d++)
                {
                    Vector3Int dir = Dirs[d];

                    // --- Horizontal move ---
                    Vector3Int t = pos + dir;
                    if (grid.InBounds(t))
                    {
                        int ti = grid.Index(t);
                        CellType ct = grid[ti];
                        if ((ct == CellType.Empty || ct == CellType.Hallway) &&
                            !InChain(uChain, ti) &&
                            SurroundingsOk(grid, stairs, t) &&
                            ChainAdjacencyOk(uChain, t, ti))
                        {
                            int c = ct == CellType.Hallway ? costs.ReuseHallway : costs.NewHallway;
                            Relax(ti, uG + c, u, (byte)d, uChain, t);
                        }
                    }

                    // --- Stair macro-edges, up and down ---
                    TryStair(pos, u, uG, uChain, d, dir, +1);
                    TryStair(pos, u, uG, uChain, d, dir, -1);
                }
            }
            return null;
        }

        void TryStair(Vector3Int pos, int u, int uG, FootprintNode uChain, int dirIndex, Vector3Int dir, int vert)
        {
            Vector3Int exit = pos + dir * 3 + Up * vert;
            if (!grid.InBounds(exit)) return;

            int ei = grid.Index(exit);
            CellType et = grid[ei];
            if (et != CellType.Empty && et != CellType.Hallway) return;
            if (InChain(uChain, ei)) return;
            if (!SurroundingsOk(grid, stairs, exit)) return;   // solid below/above the landing, no foreign stair flanks
            if (!ChainAdjacencyOk(uChain, exit, ei)) return;

            // Canonical up-form: entryLow at the lower level, cd = ascent direction.
            Vector3Int entryLow, cd;
            if (vert > 0) { entryLow = pos;  cd = dir;  }
            else          { entryLow = exit; cd = -dir; }

            Vector3Int t1 = entryLow + cd;
            Vector3Int t2 = entryLow + cd * 2;
            int i1 = grid.Index(t1),      i2 = grid.Index(t2);
            int i3 = grid.Index(t1 + Up), i4 = grid.Index(t2 + Up);

            bool valid;
            int cost;

            if (grid[i1] == CellType.Empty && grid[i2] == CellType.Empty &&
                grid[i3] == CellType.Empty && grid[i4] == CellType.Empty)
            {
                // New stair: footprint must also miss this path's own tentative footprints.
                if (InChain(uChain, i1) || InChain(uChain, i2) ||
                    InChain(uChain, i3) || InChain(uChain, i4)) return;

                // Sealed envelope: every cell flanking the footprint, below the
                // treads, above the headroom, and above the entry must be solid
                // rock. Open neighbors here are exactly the "gap into the side
                // of a staircase" holes — forbid the configuration outright.
                Vector3Int perp = new Vector3Int(Mathf.Abs(cd.z), 0, Mathf.Abs(cd.x));
                Vector3Int u1 = t1 + Up, u2 = t2 + Up;
                bool Sealed(Vector3Int sc)
                {
                    if (!grid.InBounds(sc)) return true;                 // out of bounds = solid
                    if (grid[sc] != CellType.Empty) return false;        // open or stair
                    return !InChain(uChain, grid.Index(sc));             // or claimed by our own tentative stairs
                }
                if (!Sealed(t1 + perp) || !Sealed(t1 - perp) ||
                    !Sealed(t2 + perp) || !Sealed(t2 - perp) ||
                    !Sealed(u1 + perp) || !Sealed(u1 - perp) ||
                    !Sealed(u2 + perp) || !Sealed(u2 - perp) ||
                    !Sealed(t1 - Up)   || !Sealed(t2 - Up)   ||
                    !Sealed(u1 + Up)   || !Sealed(u2 + Up)   ||
                    !Sealed(entryLow + Up)) return;

                valid = true;
                cost = costs.NewStair;
            }
            else if (stairs.TryGetValue(i1, out Stair s) && s.Entry == entryLow && s.Dir == cd)
            {
                // Exact reuse of a committed stair, same axis and vertical sense.
                // (Committed stair cells were never Empty, so they can't be in any chain.)
                valid = true;
                cost = costs.ReuseStair;
            }
            else
            {
                valid = false;
                cost = 0;
            }

            if (!valid) return;

            byte edge = (byte)(vert > 0 ? 4 + dirIndex : 8 + dirIndex);
            FootprintNode newChain = uChain;
            if (cost == costs.NewStair)
                newChain = new FootprintNode
                {
                    C0 = i1, C1 = i2, C2 = i3, C3 = i4,
                    EntryIdx = grid.Index(entryLow),
                    ExitIdx = grid.Index(entryLow + cd * 3 + Up),
                    Parent = uChain
                };

            Relax(ei, uG + cost, u, edge, newChain, exit);
        }

        void Relax(int cell, int newG, int from, byte edge, FootprintNode c, Vector3Int cellPos)
        {
            if (closed[cell]) return;
            if (newG >= gScore[cell]) return;
            gScore[cell] = newG;
            cameFrom[cell] = from;
            cameEdge[cell] = edge;
            chain[cell] = c;
            Push(cell, newG, Heuristic(cellPos));
        }

        int Heuristic(Vector3Int p)
        {
            int dx = Mathf.Max(Mathf.Max(gMinX - p.x, p.x - gMaxX), 0);
            int dz = Mathf.Max(Mathf.Max(gMinZ - p.z, p.z - gMaxZ), 0);
            int dy = Mathf.Max(Mathf.Max(gMinY - p.y, p.y - gMaxY), 0);
            int dxz = dx + dz;
            // Each level change needs >= one stair (cheapest: reuse) and grants
            // <= 3 cells of XZ progress; remaining XZ at >= ReuseHallway each.
            return Mathf.Max(dxz - 3 * dy, 0) * costs.ReuseHallway + dy * costs.ReuseStair;
        }

        static bool InChain(FootprintNode n, int cell)
        {
            for (; n != null; n = n.Parent)
                if (cell == n.C0 || cell == n.C1 || cell == n.C2 || cell == n.C3)
                    return true;
            return false;
        }

        /// <summary>
        /// Shared carvability predicate: a corridor cell must sit on solid rock
        /// (open below = floor hole / pit) with solid above (open above =
        /// ceiling hole), and may only touch a committed staircase at that
        /// stair's entry or exit cell (anything else is a gap into the side,
        /// underside, or headroom of the stair). Also used by the generator to
        /// filter door candidates.
        /// </summary>
        public static bool SurroundingsOk(Grid3D<CellType> grid, Dictionary<int, Stair> stairs, Vector3Int t)
        {
            Vector3Int below = t - Up;
            if (grid.InBounds(below) && grid[below] != CellType.Empty) return false;
            Vector3Int above = t + Up;
            if (grid.InBounds(above) && grid[above] != CellType.Empty) return false;

            for (int d = 0; d < 4; d++)
            {
                Vector3Int n = t + Dirs[d];
                if (!grid.InBounds(n)) continue;
                CellType ct = grid[n];
                if (ct != CellType.StairLower && ct != CellType.StairUpper) continue;
                if (!stairs.TryGetValue(grid.Index(n), out Stair s)) return false;
                Vector3Int exit = s.Entry + s.Dir * 3 + Up;
                if (t != s.Entry && t != exit) return false;
            }
            return true;
        }

        /// <summary>
        /// Same stair-flank rule, but against this path's own tentative stairs
        /// (which aren't in the grid yet): the target may neighbor a tentative
        /// footprint only if it IS that stair's entry or exit cell.
        /// </summary>
        bool ChainAdjacencyOk(FootprintNode n, Vector3Int t, int ti)
        {
            for (; n != null; n = n.Parent)
            {
                if (ti == n.EntryIdx || ti == n.ExitIdx) continue;
                if (Adjacent6(t, grid.Position(n.C0)) || Adjacent6(t, grid.Position(n.C1)) ||
                    Adjacent6(t, grid.Position(n.C2)) || Adjacent6(t, grid.Position(n.C3)))
                    return false;
            }
            return true;
        }

        static bool Adjacent6(Vector3Int a, Vector3Int b)
        {
            Vector3Int d = a - b;
            return Mathf.Abs(d.x) + Mathf.Abs(d.y) + Mathf.Abs(d.z) == 1;
        }

        List<PathStep> Reconstruct(int goal)
        {
            var steps = new List<PathStep>();
            int c = goal;
            while (true)
            {
                byte e = cameEdge[c];
                Vector3Int pos = grid.Position(c);
                if (e == EDGE_SEED)
                {
                    steps.Add(new PathStep { Cell = pos, Type = StepType.Move, Dir = Vector3Int.zero });
                    break;
                }
                if (e < 4)
                    steps.Add(new PathStep { Cell = pos, Type = StepType.Move, Dir = Dirs[e] });
                else if (e < 8)
                    steps.Add(new PathStep { Cell = pos, Type = StepType.StairUp, Dir = Dirs[e - 4] });
                else
                    steps.Add(new PathStep { Cell = pos, Type = StepType.StairDown, Dir = Dirs[e - 8] });
                c = cameFrom[c];
            }
            steps.Reverse();
            return steps;
        }

        // ---------------- Binary heap, deterministic tie-break (F, H, Order) ----------------

        void Push(int cell, int g, int h)
        {
            var e = new HeapEntry { F = g + h, H = h, Order = pushOrder++, Cell = cell, G = g };
            heap.Add(e);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (Less(heap[i], heap[p])) { (heap[i], heap[p]) = (heap[p], heap[i]); i = p; }
                else break;
            }
        }

        HeapEntry Pop()
        {
            HeapEntry root = heap[0];
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, m = i;
                if (l < heap.Count && Less(heap[l], heap[m])) m = l;
                if (r < heap.Count && Less(heap[r], heap[m])) m = r;
                if (m == i) break;
                (heap[i], heap[m]) = (heap[m], heap[i]);
                i = m;
            }
            return root;
        }

        static bool Less(HeapEntry a, HeapEntry b)
        {
            if (a.F != b.F) return a.F < b.F;
            if (a.H != b.H) return a.H < b.H;
            return a.Order < b.Order;
        }
    }
}