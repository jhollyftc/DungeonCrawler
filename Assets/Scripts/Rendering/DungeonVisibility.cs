using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Occlusion culling for a dungeon generated at RUNTIME, done by asking the generator rather
    /// than the renderer.
    ///
    /// UNITY'S OWN OCCLUSION CULLING IS UNAVAILABLE HERE, TWICE OVER, and both reasons are worth
    /// knowing before anyone tries again. Baked occlusion (Umbra) needs static scene data baked
    /// in the editor, and there is no scene to bake — the dungeon does not exist until Generate()
    /// runs. Unity 6's GPU Occlusion Culling needs the GPU Resident Drawer, which does not drive
    /// `Graphics.RenderMeshInstanced`, the path everything here renders through.
    ///
    /// SO VISIBILITY COMES FROM THE GRID, WHICH IS BETTER INFORMATION ANYWAY. The dungeon is a
    /// typed cell grid; we know exactly which cells are open and how they join. **A cell 30m away
    /// in a straight line but seventy cells away through winding corridor is, by definition,
    /// behind walls.** So visibility is a flood fill out from the player's cell through OPEN cells
    /// only — path distance, never euclidean. Same insight `DungeonMapper` runs on ("the dungeon
    /// is ALREADY the map"), and the same reasoning the crawlway detour ratio uses to tell a real
    /// shortcut from a hole in a wall.
    ///
    /// IT IS AN APPROXIMATION AND ERRS TOWARD DRAWING. It answers "could you walk there without
    /// going far", which tracks "can you see it" well in corridors and rooms and imperfectly
    /// across large open volumes. Every rule below is therefore biased toward false POSITIVES:
    /// drawing something hidden costs a few triangles, culling something visible is a hole in the
    /// world.
    ///
    /// COST IS PAID ON CELL CHANGE, NOT PER FRAME — about twice a second at running speed — and
    /// the per-instance test is one array index. That split is the whole reason this is
    /// affordable: when it was written the main thread was already the frame's floor (~3.6ms
    /// against a 3.6ms GPU), so a per-frame visibility solve would have cost more than the
    /// geometry it saved.
    /// </summary>
    public class DungeonVisibility
    {
        readonly DungeonGenerator gen;
        readonly float cellSize;
        readonly Vector3 origin;
        readonly int w, h, d;

        /// <summary>
        /// A generation stamp per cell rather than a bool array needing a clear. A dungeon is tens
        /// of thousands of cells and this reruns on every cell crossing; bumping one counter beats
        /// clearing the array each time.
        /// </summary>
        readonly int[] stamp;
        int currentStamp;

        readonly Queue<Vector3Int> frontier = new Queue<Vector3Int>();
        readonly Queue<int> frontierDepth = new Queue<int>();

        Vector3Int lastCell = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        bool everBuilt;

        /// <summary>Cells of PATH the fill reaches before stopping.</summary>
        public int maxSteps = 14;

        public int VisibleCells { get; private set; }
        public int TotalCells => stamp.Length;

        static readonly Vector3Int[] Steps =
        {
            new Vector3Int( 1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 0, 1), new Vector3Int( 0, 0,-1),
            new Vector3Int( 0, 1, 0), new Vector3Int( 0,-1, 0),
        };

        public DungeonVisibility(DungeonGenerator gen, float cellSize, Vector3 origin)
        {
            this.gen = gen;
            this.cellSize = cellSize;
            this.origin = origin;
            // Read off the grid rather than restated: Grid3D owns its dimensions AND its index
            // layout (x + z*W + y*W*D), and a second copy of either here is a bug waiting for
            // someone to change one of them.
            w = gen.Grid.Width; h = gen.Grid.Height; d = gen.Grid.Depth;
            stamp = new int[Mathf.Max(1, w * h * d)];
        }

        public Vector3Int CellOf(Vector3 worldPos) =>
            Vector3Int.FloorToInt((worldPos - origin) / cellSize);

        /// <summary>
        /// Flat index for an instance, resolved ONCE at registration rather than per frame.
        /// -1 means "outside the grid", which counts as always visible — see IsVisible.
        /// </summary>
        public int IndexOf(Vector3 worldPos)
        {
            Vector3Int c = CellOf(worldPos);
            return gen.Grid.InBounds(c) ? gen.Grid.Index(c) : -1;
        }

        /// <summary>
        /// FAILS OPEN. An index outside the grid, and a set that was never built, both report
        /// visible — the failure here is one-directional. Drawing something hidden costs
        /// triangles; not drawing something visible is a hole in the world.
        /// </summary>
        public bool IsVisible(int flatIndex)
        {
            if (!everBuilt || flatIndex < 0) return true;
            return stamp[flatIndex] == currentStamp;
        }

        /// <summary>Rebuild if the viewer crossed into a new cell. True if it rebuilt.</summary>
        public bool RefreshFor(Vector3 viewerWorldPos)
        {
            Vector3Int c = CellOf(viewerWorldPos);
            if (everBuilt && c == lastCell) return false;
            lastCell = c;
            Build(c);
            return true;
        }

        void Build(Vector3Int start)
        {
            currentStamp++;
            everBuilt = true;
            frontier.Clear();
            frontierDepth.Clear();

            // STANDING SOMEWHERE THE GRID CALLS SOLID IS A REAL STATE, NOT AN ERROR — the camera
            // sits at eye height and can resolve to a cell whose floor is a storey down. Marking
            // everything visible is the right answer, per failing open above.
            //
            // A CRAWLWAY BORE USED TO LAND HERE AND NO LONGER DOES: `IsOpen` now recognises one,
            // so crawling through a tube floods properly from inside it instead of giving up and
            // drawing the whole dungeon. Worth knowing, because this fallback firing constantly
            // was masking the bug — from inside a bore everything looked correct.
            if (!gen.Grid.InBounds(start) || !IsOpen(start))
            {
                MarkAll();
                return;
            }

            stamp[gen.Grid.Index(start)] = currentStamp;
            frontier.Enqueue(start);
            frontierDepth.Enqueue(0);

            while (frontier.Count > 0)
            {
                Vector3Int cur = frontier.Dequeue();
                int depth = frontierDepth.Dequeue();
                if (depth >= maxSteps) continue;

                for (int i = 0; i < Steps.Length; i++)
                {
                    Vector3Int n = cur + Steps[i];
                    if (!gen.Grid.InBounds(n) || !IsOpen(n)) continue;
                    int f = gen.Grid.Index(n);
                    if (stamp[f] == currentStamp) continue;
                    stamp[f] = currentStamp;
                    frontier.Enqueue(n);
                    frontierDepth.Enqueue(depth + 1);
                }
            }

            Dilate();
        }

        /// <summary>
        /// Can the fill travel through this cell — which is NOT the same question as
        /// `Grid[c] != Empty`, and assuming it was is a bug this project has now produced
        /// repeatedly.
        ///
        /// **A CRAWLWAY BORE IS `CellType.Empty`.** That is the whole point of its design (§4):
        /// its cells stay Empty so the mesher, the kit placer, `NeedsSlabBetween`, the automap and
        /// every `!= CellType.Empty` test treat it as solid rock and never emit 3m masonry into a
        /// 1.5m tube. Identity lives in `IsCrawlwayCell`, not in the grid — so a visibility fill
        /// keyed on CellType alone walls off the entire sewer network, and every pipe inside it
        /// tests as hidden. Field-reported as pipes vanishing while plainly in view and popping in
        /// only when you got close, which is the dilation reaching them or the fill's
        /// standing-in-solid-rock fallback firing once you were inside the bore.
        ///
        /// Manhole shafts are bore cells too and were broken the same way.
        ///
        /// Everything else that is "grid-invisible" happens to be fine already: alcoves and sewer
        /// chambers are typed `Hallway`, and pit interiors are typed `Room`. Crawlways are the one
        /// space whose identity the grid genuinely does not carry — which is exactly why it is the
        /// one that broke.
        /// </summary>
        bool IsOpen(Vector3Int c) => gen.Grid[c] != CellType.Empty || gen.IsCrawlwayCell(c);

        /// <summary>
        /// Grow the visible set by one cell in every direction, DIAGONALS INCLUDED.
        ///
        /// THIS IS NOT PADDING — WITHOUT IT THE WALLS OF A VISIBLE ROOM DISAPPEAR. A wall's world
        /// position sits ON THE FACE between two cells (§5; it is exactly why `PlaceCallback` has
        /// to carry the owning cell), so flooring it lands on the solid neighbour or the open one
        /// depending which way the face points. Half a room's walls would resolve to the solid
        /// side, test as hidden, and not draw — you would look into rooms through missing walls.
        /// Corner posts sit at cell CORNERS, which is why this is the 26-neighbour dilation and
        /// not the 6.
        ///
        /// A separate pass rather than marking neighbours during the fill: doing it inline would
        /// enqueue solid cells and let the flood escape through rock, which is the one thing the
        /// whole approach depends on it never doing.
        /// </summary>
        void Dilate()
        {
            int seen = currentStamp;
            currentStamp++;
            int count = 0;

            for (int y = 0; y < h; y++)
            for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
            {
                if (stamp[gen.Grid.Index(x, y, z)] != seen) continue;

                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || ny < 0 || nz < 0 || nx >= w || ny >= h || nz >= d) continue;
                    int f = gen.Grid.Index(nx, ny, nz);
                    if (stamp[f] != seen && stamp[f] != currentStamp) { stamp[f] = currentStamp; count++; }
                }
            }

            // Carry the originals onto the new stamp — the loop above deliberately left them on
            // the old one so it could tell source cells from freshly dilated ones.
            for (int i = 0; i < stamp.Length; i++)
                if (stamp[i] == seen) { stamp[i] = currentStamp; count++; }

            VisibleCells = count;
        }

        void MarkAll()
        {
            currentStamp++;
            for (int i = 0; i < stamp.Length; i++) stamp[i] = currentStamp;
            VisibleCells = stamp.Length;
        }
    }
}
