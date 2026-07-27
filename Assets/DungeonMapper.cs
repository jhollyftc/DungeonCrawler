using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DungeonGen
{
    /// <summary>
    /// Fog-of-war automap. The dungeon is ALREADY a typed integer grid, so the map
    /// isn't a new data structure — it's a filter over the generator's own output:
    /// a set of explored rooms/cells, painted into a texture one pixel-block per cell.
    ///
    /// REVEAL MODEL (deliberately not a uniform radius):
    ///   - Rooms reveal WHOLESALE on entry. Walking in shows the whole room, which is
    ///     honest (you can see it) and free — Room.Cells is the exact footprint. A
    ///     radius would instead dribble a room in as you cross it AND leak through
    ///     walls into rooms you haven't entered.
    ///   - Corridors reveal cell by cell as you walk them. They're 1-wide and winding,
    ///     so the drip-feed is right there — it's what makes the map feel earned.
    /// Big spaces you take in at a glance; tunnels you have to traverse.
    ///
    /// ONE FLOOR AT A TIME (presentation "A"). The grid is 3D and a map is 2D, so this
    /// draws only the player's current Y and marks vertical links as connectors. Three
    /// DISTINCT connector colors, because the generator really does produce one-way
    /// links: an elevated door normally gets an interior stair, falls back to a ladder,
    /// and if that also fails it "leaves a one-way drop". Drawing a one-way drop like a
    /// staircase would actively lie to a player planning a route back.
    ///
    /// Maps WALKABLE SURFACE, not occupied volume: a two-story room occupies cells on
    /// two Y levels but you only walk its floor, so it appears once, on that floor.
    ///
    /// Play-mode only, and it holds a runtime generator reference — same shape as
    /// DungeonFogController. A regenerate (F1/PgUp) swaps the generator instance, which
    /// is what this watches to wipe the map for the new dungeon.
    /// </summary>
    [DisallowMultipleComponent]
    public class DungeonMapper : MonoBehaviour
    {
        [Header("Reveal")]
        [Tooltip("Entering a room reveals its ENTIRE footprint at once. Off = rooms reveal cell by cell like corridors, which reads worse (a big hall dribbles in as you cross it) but is stingier.")]
        public bool revealWholeRoomOnEntry = true;
        [Tooltip("Extra cells revealed either side while walking a corridor. 0 = only the cell you occupy (a bare breadcrumb trail); 1 = you also 'see' the junction you're standing next to. Kept small — this is line of sight down a tunnel, not a radar.")]
        [Min(0)] public int corridorRevealRadius = 1;
        [Tooltip("DEBUG: draw the whole dungeon regardless of what's been explored. Does NOT mark anything as explored — switch it back off and the real fog-of-war state is exactly as you left it, so you can peek at the layout mid-run without spoiling the map.")]
        public bool revealAll = false;

        [Header("Display")]
        [Tooltip("Show/hide the map.")]
        public KeyCode toggleKey = KeyCode.M;
        public bool visible = true;
        [Tooltip("Texture pixels per dungeon cell. Needs to be at least ~8 now that cells draw wall edges, or the walls eat the whole cell. A 40x40 grid at 12 is a 480x480 texture — nothing, and it only rebuilds when something changes.")]
        [Range(4, 24)] public int pixelsPerCell = 12;
        [Tooltip("Optional UI RawImage to render into. Left empty, the map draws through OnGUI at overlayRect — enough to evaluate the look with no canvas setup, same dev-overlay approach FirstPersonController uses.")]
        public RawImage target;
        [Tooltip("Screen rect (pixels) for the OnGUI fallback: x, y, width, height. Ignored when a RawImage is assigned.")]
        public Rect overlayRect = new Rect(12f, 12f, 260f, 260f);
        [Tooltip("Draw the floor number under the OnGUI map. A one-floor-at-a-time map is confusing without it.")]
        public bool showFloorLabel = true;

        [Header("Ghosted neighbour floors (presentation 'B' — toggle to compare)")]
        [Tooltip("Draw the floor BELOW underneath the current one, dimmed. Restores the vertical relationship a one-floor map throws away — you can see a corridor runs over a room you already cleared. The risk is clutter: this generator packs rooms in 3D, so floors overlap in X/Z often. Turn off to get plain presentation 'A' back.")]
        public bool showFloorBelow = false;
        [Tooltip("Same for the floor ABOVE. Usually noisier and less useful than below (you're rarely planning upward), so try it on its own rather than with both.")]
        public bool showFloorAbove = false;
        [Tooltip("How faint the ghost layers are. Low enough that the current floor is never ambiguous — if you have to squint to tell which floor you're on, this is too high.")]
        [Range(0.05f, 0.8f)] public float ghostStrength = 0.28f;

        [Header("Walls and doors")]
        [Tooltip("Wall line thickness in texture pixels. 1-2 reads best; scale up with pixelsPerCell.")]
        [Range(0, 4)] public int wallThickness = 2;
        [Tooltip("Wall edge color. Walls are derived from the GRID (is the neighbour cell solid?), never from what's explored — see the note on the frontier in DrawCell.")]
        public Color wallColor = new Color(0.13f, 0.12f, 0.11f, 1f);
        [Tooltip("Mark real doors (DungeonDoor.HasDoor) across the opening they occupy. Open archways are deliberately left unmarked — they're already visibly open, and marking every opening turns a dense floor into confetti.")]
        public bool showDoors = true;
        public Color doorColor = new Color(0.85f, 0.62f, 0.25f, 1f);

        [Header("Glyphs")]
        [Tooltip("Draw vertical links as SYMBOLS inside a normally-floored cell instead of flooding the whole cell with a flat color. Now that cells have wall outlines a solid block reads as 'something is wrong here'; a glyph reads as 'something is here'. Off = the old solid-fill look, for comparison.")]
        public bool useConnectorGlyphs = true;
        [Tooltip("Mark notable rooms (Start, Exit, Merchant, Throne, Treasury) with a symbol at their floor centre once explored. This is what turns the map from a record of where you've BEEN into a tool for deciding where to GO.")]
        public bool showRoomGlyphs = true;
        public Color roomGlyphColor = new Color(0.16f, 0.14f, 0.12f, 1f);

        [Header("Colors")]
        public Color unexploredColor = new Color(0f, 0f, 0f, 0f);
        public Color roomColor = new Color(0.62f, 0.58f, 0.50f, 0.95f);
        public Color hallwayColor = new Color(0.44f, 0.41f, 0.36f, 0.95f);
        public Color prisonColor = new Color(0.40f, 0.34f, 0.40f, 0.95f);
        [Tooltip("Two-way vertical link: a staircase.")]
        public Color stairColor = new Color(0.95f, 0.80f, 0.35f, 1f);
        [Tooltip("Two-way vertical link: a climbable ladder serving a drop-in entrance.")]
        public Color ladderColor = new Color(0.45f, 0.80f, 0.95f, 1f);
        [Tooltip("ONE-WAY vertical link: a drop-in entrance with no stair and no ladder. You can go down here and NOT come back up — it must not look like a staircase.")]
        public Color oneWayDropColor = new Color(0.95f, 0.35f, 0.30f, 1f);
        public Color playerColor = new Color(1f, 1f, 1f, 1f);

        DungeonVisualizer vis;
        DungeonGenerator cachedGen;      // identity of the dungeon this map describes
        Transform player;

        readonly HashSet<int> exploredRooms = new HashSet<int>();
        readonly HashSet<Vector3Int> exploredCells = new HashSet<Vector3Int>();
        // Connector cells, resolved once per dungeon (they're generation output, not state).
        readonly HashSet<Vector3Int> ladderCells = new HashSet<Vector3Int>();
        readonly HashSet<Vector3Int> oneWayDropCells = new HashSet<Vector3Int>();
        // Explored cells on the floor being drawn, rebuilt per redraw for the glyph passes.
        readonly List<Vector3Int> floorCells = new List<Vector3Int>();

        Texture2D tex;
        Color32[] pixels;
        int texW, texH;

        Vector3Int lastPlayerCell = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        int drawnFloor = int.MinValue;
        bool dirty = true;
        GUIStyle labelStyle;

        /// <summary>The live map image, for wiring into custom UI.</summary>
        public Texture2D MapTexture => tex;
        /// <summary>Raw grid Y currently drawn.</summary>
        public int CurrentFloor { get; private set; }

        /// <summary>
        /// Human floor number: the LOWEST grid level that actually contains dungeon is
        /// Floor 1, counting up. The grid is allocated taller than any dungeon needs
        /// (gridHeight is a budget, not a floor count), so a run whose lowest room sits
        /// at y=5 would otherwise announce itself as "Floor 5" — a number that means
        /// nothing to a player and changes between seeds for no visible reason.
        /// </summary>
        public int CurrentFloorNumber => CurrentFloor - lowestOccupiedY + 1;
        /// <summary>How many grid levels this dungeon actually occupies.</summary>
        public int FloorCount => Mathf.Max(1, highestOccupiedY - lowestOccupiedY + 1);

        int lowestOccupiedY, highestOccupiedY;
        bool lastRevealAll;

        void Awake() => vis = GetComponent<DungeonVisualizer>();

        void Update()
        {
            if (!Application.isPlaying) return;
            if (Input.GetKeyDown(toggleKey)) visible = !visible;

            if (vis == null || vis.Generator == null) return;

            // A regenerate (F1 / PgUp) builds a NEW generator. Watching the instance is
            // what wipes the old dungeon's exploration instead of carrying it into a
            // completely different layout.
            if (!ReferenceEquals(vis.Generator, cachedGen)) ResetForNewDungeon();

            if (player == null)
            {
                var fpc = FindObjectOfType<FirstPersonController>();
                if (fpc == null) return;
                player = fpc.transform;
            }

            // The SAME world→cell conversion FirstPersonController.CurrentRoomLabel and
            // DungeonFogController use, so the map, the room readout and the fog color
            // can never disagree about which cell the player is in.
            Vector3Int cell = Vector3Int.FloorToInt((player.position - vis.transform.position) / vis.cellSize);
            if (cell != lastPlayerCell)
            {
                lastPlayerCell = cell;
                Reveal(cell);
                dirty = true;   // the player marker moved even if nothing new was revealed
            }

            CurrentFloor = cell.y;
            if (CurrentFloor != drawnFloor) dirty = true;
            if (revealAll != lastRevealAll) { lastRevealAll = revealAll; dirty = true; }

            if (dirty) Redraw();
        }

        void ResetForNewDungeon()
        {
            cachedGen = vis.Generator;
            exploredRooms.Clear();
            exploredCells.Clear();
            BuildConnectorSets();
            MeasureOccupiedFloors();
            lastPlayerCell = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            drawnFloor = int.MinValue;
            dirty = true;
        }

        /// <summary>
        /// Resolve the vertical links once per dungeon. Stairs come straight from the
        /// grid (StairLower/StairUpper), so only the DOOR-driven links need work:
        /// an elevated door with no interior stair is a drop-in, and the ladder pass
        /// claims the column directly beneath that door's threshold — so a ladder whose
        /// foot shares the threshold's X/Z is what makes the drop two-way. No such
        /// ladder means the ladder pass failed and the entrance is a ONE-WAY drop.
        /// </summary>
        void BuildConnectorSets()
        {
            ladderCells.Clear();
            oneWayDropCells.Clear();
            if (cachedGen == null) return;

            foreach (var lad in cachedGen.Ladders)
                ladderCells.Add(lad.BaseCell);

            foreach (var door in cachedGen.Doors)
            {
                if (!door.IsElevated || door.HasInteriorStair) continue;

                Vector3Int threshold = door.HallwayCell + door.Direction;   // the room cell the door opens onto
                bool served = false;
                foreach (var lad in cachedGen.Ladders)
                {
                    if (lad.BaseCell.x == threshold.x && lad.BaseCell.z == threshold.z) { served = true; break; }
                }
                if (!served) oneWayDropCells.Add(threshold);
            }
        }

        /// <summary>
        /// Find the lowest and highest grid levels that hold any dungeon, so floor
        /// numbers can be reported relative to the real dungeon instead of the grid
        /// allocation (see CurrentFloorNumber). One scan per dungeon — the grid is a
        /// flat array and this only runs on generate.
        /// </summary>
        void MeasureOccupiedFloors()
        {
            lowestOccupiedY = 0;
            highestOccupiedY = 0;
            if (cachedGen == null) return;

            var grid = cachedGen.Grid;
            bool found = false;
            for (int y = 0; y < grid.Height; y++)
            {
                bool occupied = false;
                for (int x = 0; x < grid.Width && !occupied; x++)
                for (int z = 0; z < grid.Depth; z++)
                {
                    if (grid[new Vector3Int(x, y, z)] == CellType.Empty) continue;
                    occupied = true;
                    break;
                }
                if (!occupied) continue;

                if (!found) { lowestOccupiedY = y; found = true; }
                highestOccupiedY = y;
            }
        }

        void Reveal(Vector3Int cell)
        {
            var gen = cachedGen;
            if (gen == null || !gen.Grid.InBounds(cell)) return;

            // In a room: take the whole footprint. Room membership is authoritative
            // (RoomAt), so this also correctly reveals an L-shaped room's real cells
            // rather than its bounding box.
            if (revealWholeRoomOnEntry)
            {
                Room room = gen.RoomAt(cell);
                if (room != null)
                {
                    int idx = gen.Rooms.IndexOf(room);
                    if (idx >= 0 && exploredRooms.Add(idx)) dirty = true;
                    return;
                }
            }

            // Corridor / stair / prison: this cell, plus a small neighbourhood so a
            // junction you're standing in doesn't read as a dead end.
            for (int dx = -corridorRevealRadius; dx <= corridorRevealRadius; dx++)
            for (int dz = -corridorRevealRadius; dz <= corridorRevealRadius; dz++)
            {
                Vector3Int c = new Vector3Int(cell.x + dx, cell.y, cell.z + dz);
                if (!gen.Grid.InBounds(c) || gen.Grid[c] == CellType.Empty) continue;
                // Never let the corridor radius bleed into an unentered room — that
                // would hand the player a room's contents through its doorway.
                if (gen.RoomAt(c) != null && !IsRoomExplored(gen, c)) continue;
                if (exploredCells.Add(c)) dirty = true;
            }
        }

        bool IsRoomExplored(DungeonGenerator gen, Vector3Int cell)
        {
            Room room = gen.RoomAt(cell);
            if (room == null) return false;
            int idx = gen.Rooms.IndexOf(room);
            return idx >= 0 && exploredRooms.Contains(idx);
        }

        void Redraw()
        {
            var gen = cachedGen;
            if (gen == null) return;

            int w = gen.Grid.Width, d = gen.Grid.Depth;
            EnsureTexture(w * pixelsPerCell, d * pixelsPerCell);

            Color32 unexplored = unexploredColor;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = unexplored;

            int floor = CurrentFloor;

            // Ghost layers FIRST so the current floor always paints over them — the
            // floor you're standing on must never be ambiguous. Ghosts are drawn flat:
            // no walls, no connector colors. A dim silhouette is the point, and a
            // ghosted wall grid plus ghosted stair/ladder/drop colors becomes noise you
            // have to decode rather than context you absorb.
            if (showFloorBelow) PaintFloor(gen, floor - 1, ghostStrength, detailed: false);
            if (showFloorAbove) PaintFloor(gen, floor + 1, ghostStrength, detailed: false);

            PaintFloor(gen, floor, 1f, detailed: true);

            // Symbols go on AFTER the floor and its walls, so they sit on top of the
            // architecture rather than being overwritten by the next cell's wall edge.
            CollectFloorCells(gen, floor);
            DrawDoorMarks(gen, floor);
            DrawConnectorGlyphs(gen, floor);
            DrawRoomGlyphs(gen, floor);

            // Player marker last, so it's never painted over. Inset so it sits INSIDE
            // the cell's walls rather than covering them.
            if (lastPlayerCell.y == floor)
                PaintCell(lastPlayerCell, playerColor, Mathf.Max(1, pixelsPerCell / 4));

            tex.SetPixels32(pixels);
            tex.Apply(false);
            if (target != null) target.texture = tex;

            drawnFloor = floor;
            dirty = false;
        }

        /// <summary>
        /// Paint one Y level's EXPLORED cells. `strength` fades it (1 = the live floor,
        /// less = a ghost); fog of war still applies to ghosts, so a neighbour floor
        /// only shows the parts you actually walked.
        /// </summary>
        void PaintFloor(DungeonGenerator gen, int floor, float strength, bool detailed)
        {
            // DEBUG reveal: paint the level straight off the grid and skip the explored
            // sets entirely — deliberately WITHOUT touching them, so turning this back
            // off restores the genuine fog-of-war state rather than a spoiled one.
            if (revealAll)
            {
                var grid = gen.Grid;
                for (int x = 0; x < grid.Width; x++)
                for (int z = 0; z < grid.Depth; z++)
                {
                    Vector3Int c = new Vector3Int(x, floor, z);
                    if (!grid.InBounds(c)) continue;
                    CellType t = grid[c];
                    if (t == CellType.Empty) continue;

                    Color baseColor = t == CellType.Prison ? prisonColor
                                    : t == CellType.Room ? roomColor
                                    : hallwayColor;
                    Color col = detailed ? ColorForCell(gen, c, baseColor) : baseColor;
                    DrawCell(gen, c, col, strength, detailed);
                }
                return;
            }

            // Explored ROOM cells. Iterating rooms (not the whole grid) keeps this
            // proportional to what's actually been found.
            foreach (int idx in exploredRooms)
            {
                if (idx < 0 || idx >= gen.Rooms.Count) continue;
                foreach (Vector3Int c in gen.Rooms[idx].Cells)
                {
                    if (c.y != floor) continue;
                    Color col = detailed ? ColorForCell(gen, c, roomColor) : roomColor;
                    DrawCell(gen, c, col, strength, detailed);
                }
            }

            // Explored corridor / stair / prison cells.
            foreach (Vector3Int c in exploredCells)
            {
                if (c.y != floor) continue;
                CellType t = gen.Grid.InBounds(c) ? gen.Grid[c] : CellType.Empty;
                Color baseColor = t == CellType.Prison ? prisonColor
                                : t == CellType.Room ? roomColor
                                : hallwayColor;
                Color col = detailed ? ColorForCell(gen, c, baseColor) : baseColor;
                DrawCell(gen, c, col, strength, detailed);
            }
        }

        /// <summary>Dim toward transparent AND darker — alpha alone still reads as "same
        /// floor, slightly faded", which is exactly the ambiguity a ghost must avoid.</summary>
        static Color Fade(Color c, float strength)
        {
            if (strength >= 1f) return c;
            float k = 0.5f + 0.5f * strength;   // darken as it fades
            return new Color(c.r * k, c.g * k, c.b * k, c.a * strength);
        }

        // ---------------- Glyphs ----------------
        //
        // Hand-authored pixel art rather than rendered text: a Texture2D has no font
        // rasterizer, and at ~7px a bitmap drawn on the grid stays crisp under point
        // filtering where scaled-down text turns to mush. '#' = filled. Rows read
        // top-down here and are flipped when blitted (texture Y is bottom-up).

        static readonly string[] GlyphStairUp = {
            "...#...", "..###..", ".#####.", "#######", "...#...", "...#...", "...#...",
        };
        static readonly string[] GlyphStairDown = {
            "...#...", "...#...", "...#...", "#######", ".#####.", "..###..", "...#...",
        };
        static readonly string[] GlyphLadder = {
            "#.....#", "#######", "#.....#", "#######", "#.....#", "#######", "#.....#",
        };
        // A fall onto a hard floor: you can go down, you cannot come back up.
        static readonly string[] GlyphOneWayDrop = {
            "#.....#", ".#...#.", "..#.#..", "...#...", ".......", "#######", "#######",
        };
        static readonly string[] GlyphMerchant = {   // coin
            ".#####.", "#.....#", "#..#..#", "#.###.#", "#..#..#", "#.....#", ".#####.",
        };
        static readonly string[] GlyphThrone = {     // crown
            "#.#.#.#", "#######", "#######", ".#####.", ".#####.", "#######", ".......",
        };
        static readonly string[] GlyphExit = {       // doorway
            ".#####.", "#.....#", "#.....#", "#.....#", "#..#..#", "#.....#", "#######",
        };
        static readonly string[] GlyphStart = {      // planted flag
            "##.....", "#.###..", "#.###..", "#.###..", "#......", "#......", "#......",
        };
        static readonly string[] GlyphTreasure = {   // chest
            ".......", ".#####.", "#######", "#..#..#", "#######", "#######", ".......",
        };

        static string[] GlyphForRoom(RoomType type)
        {
            switch (type)
            {
                case RoomType.Start: return GlyphStart;
                case RoomType.Exit: return GlyphExit;
                case RoomType.Merchant: return GlyphMerchant;
                case RoomType.ThroneRoom: return GlyphThrone;
                case RoomType.Treasury:
                case RoomType.ChestVault: return GlyphTreasure;
                default: return null;   // ordinary rooms stay unmarked — glyph everything and nothing stands out
            }
        }

        /// <summary>Blit a glyph centred in a cell, at the largest whole-pixel scale that fits.</summary>
        void DrawGlyph(Vector3Int cell, string[] glyph, Color32 color)
        {
            int gw = glyph[0].Length, gh = glyph.Length;
            int scale = Mathf.Max(1, pixelsPerCell / gw);
            int drawW = gw * scale, drawH = gh * scale;
            int px = cell.x * pixelsPerCell + (pixelsPerCell - drawW) / 2;
            int py = cell.z * pixelsPerCell + (pixelsPerCell - drawH) / 2;

            for (int gy = 0; gy < gh; gy++)
            {
                string row = glyph[gh - 1 - gy];   // flip: glyph rows are top-down, texture is bottom-up
                for (int gx = 0; gx < gw; gx++)
                {
                    if (row[gx] != '#') continue;
                    FillRect(px + gx * scale, py + gy * scale, scale, scale, color);
                }
            }
        }

        /// <summary>
        /// Stair / ladder / one-way-drop symbols on the live floor. Stair DIRECTION comes
        /// from the cell type: a StairLower on this floor is the bottom of a flight, so it
        /// goes UP from here; a StairUpper is the top, so it goes DOWN.
        /// </summary>
        void DrawConnectorGlyphs(DungeonGenerator gen, int floor)
        {
            if (!useConnectorGlyphs) return;

            for (int i = 0; i < floorCells.Count; i++)
            {
                Vector3Int c = floorCells[i];
                if (oneWayDropCells.Contains(c)) { DrawGlyph(c, GlyphOneWayDrop, oneWayDropColor); continue; }
                if (ladderCells.Contains(c)) { DrawGlyph(c, GlyphLadder, ladderColor); continue; }
                if (!gen.Grid.InBounds(c)) continue;

                CellType t = gen.Grid[c];
                if (t == CellType.StairLower) DrawGlyph(c, GlyphStairUp, stairColor);
                else if (t == CellType.StairUpper) DrawGlyph(c, GlyphStairDown, stairColor);
            }
        }

        /// <summary>Room-type symbols at each explored room's floor centre.</summary>
        void DrawRoomGlyphs(DungeonGenerator gen, int floor)
        {
            if (!showRoomGlyphs) return;

            for (int i = 0; i < gen.Rooms.Count; i++)
            {
                if (!revealAll && !exploredRooms.Contains(i)) continue;

                Room room = gen.Rooms[i];
                string[] glyph = GlyphForRoom(room.Type);
                if (glyph == null) continue;

                // InteriorFloorCell, not Bounds.center — an L-shaped room's bbox centre
                // can land in the bite, which would put the marker outside the room.
                Vector3Int c = room.InteriorFloorCell;
                if (c.y != floor) continue;
                DrawGlyph(c, glyph, roomGlyphColor);
            }
        }

        /// <summary>
        /// Explored cells on one floor, gathered once per redraw so the glyph passes
        /// don't each re-walk the rooms and corridor sets.
        /// </summary>
        void CollectFloorCells(DungeonGenerator gen, int floor)
        {
            floorCells.Clear();

            if (revealAll)
            {
                var grid = gen.Grid;
                for (int x = 0; x < grid.Width; x++)
                for (int z = 0; z < grid.Depth; z++)
                {
                    Vector3Int c = new Vector3Int(x, floor, z);
                    if (grid.InBounds(c) && grid[c] != CellType.Empty) floorCells.Add(c);
                }
                return;
            }

            foreach (int idx in exploredRooms)
            {
                if (idx < 0 || idx >= gen.Rooms.Count) continue;
                foreach (Vector3Int c in gen.Rooms[idx].Cells)
                    if (c.y == floor) floorCells.Add(c);
            }
            foreach (Vector3Int c in exploredCells)
                if (c.y == floor) floorCells.Add(c);
        }

        /// <summary>Connectors override the surface color — a vertical link matters more than what it's cut into.</summary>
        Color ColorForCell(DungeonGenerator gen, Vector3Int c, Color fallback)
        {
            // Glyph mode draws the marker ON a normal floor instead of replacing it, so
            // the surface color must stay untouched here or the glyph sits on a slab of
            // its own color and reads as an error rather than a landmark.
            if (useConnectorGlyphs) return fallback;

            if (oneWayDropCells.Contains(c)) return oneWayDropColor;
            if (ladderCells.Contains(c)) return ladderColor;
            if (gen.Grid.InBounds(c))
            {
                CellType t = gen.Grid[c];
                if (t == CellType.StairLower || t == CellType.StairUpper) return stairColor;
            }
            return fallback;
        }

        void PaintCell(Vector3Int c, Color32 color, int inset = 0)
        {
            int px = c.x * pixelsPerCell;
            int py = c.z * pixelsPerCell;   // grid Z → texture Y (map is top-down)
            for (int y = inset; y < pixelsPerCell - inset; y++)
            {
                int row = (py + y) * texW;
                for (int x = inset; x < pixelsPerCell - inset; x++)
                {
                    int i = row + px + x;
                    if (i >= 0 && i < pixels.Length) pixels[i] = color;
                }
            }
        }

        /// <summary>
        /// Fill a cell, then draw wall edges on each side whose NEIGHBOUR IS SOLID.
        ///
        /// The mask is deliberately built from the GRID, not from the explored set.
        /// Masking on exploration would draw a solid wall across the far end of a
        /// half-walked corridor — the map asserting a dead end where the tunnel
        /// actually continues. With solidity, a FRONTIER (neighbour open but not yet
        /// explored) simply gets no wall, so the passage reads as "continues, unknown",
        /// which is the honest answer and the single biggest readability win here.
        /// </summary>
        void DrawCell(DungeonGenerator gen, Vector3Int c, Color fill, float strength, bool walls)
        {
            PaintCell(c, Fade(fill, strength));
            if (!walls || wallThickness <= 0) return;

            Color32 w = Fade(wallColor, strength);
            int px = c.x * pixelsPerCell;
            int py = c.z * pixelsPerCell;
            int t = Mathf.Min(wallThickness, pixelsPerCell / 2);

            if (IsSolid(gen, c + new Vector3Int(0, 0, 1))) FillRect(px, py + pixelsPerCell - t, pixelsPerCell, t, w); // +Z
            if (IsSolid(gen, c + new Vector3Int(0, 0, -1))) FillRect(px, py, pixelsPerCell, t, w);                    // -Z
            if (IsSolid(gen, c + new Vector3Int(1, 0, 0))) FillRect(px + pixelsPerCell - t, py, t, pixelsPerCell, w); // +X
            if (IsSolid(gen, c + new Vector3Int(-1, 0, 0))) FillRect(px, py, t, pixelsPerCell, w);                    // -X
        }

        /// <summary>Solid = nothing walkable there. Off-grid counts as solid, so the dungeon's outer edge gets walls.</summary>
        static bool IsSolid(DungeonGenerator gen, Vector3Int c)
            => !gen.Grid.InBounds(c) || gen.Grid[c] == CellType.Empty;

        void FillRect(int x0, int y0, int w, int h, Color32 color)
        {
            for (int y = y0; y < y0 + h; y++)
            {
                if (y < 0 || y >= texH) continue;
                int row = y * texW;
                for (int x = x0; x < x0 + w; x++)
                {
                    if (x < 0 || x >= texW) continue;
                    pixels[row + x] = color;
                }
            }
        }

        /// <summary>
        /// Mark real doors across the opening they sit in. A door is an EDGE between two
        /// cells (HallwayCell → Direction), not a cell, so it's drawn straddling that
        /// boundary. Both cells are open, so no wall was drawn there — the mark is what
        /// makes a doorway distinguishable from an arbitrary gap.
        /// </summary>
        void DrawDoorMarks(DungeonGenerator gen, int floor)
        {
            if (!showDoors || wallThickness <= 0) return;

            Color32 col = doorColor;
            int t = Mathf.Max(1, Mathf.Min(wallThickness, pixelsPerCell / 2));

            foreach (var door in gen.Doors)
            {
                if (!door.HasDoor) continue;

                Vector3Int a = door.HallwayCell;
                Vector3Int b = a + door.Direction;
                if (a.y != floor && b.y != floor) continue;

                // Fog of war still applies: don't reveal a door into a room you've
                // never been in, on either side of it.
                if (!revealAll && !IsCellExplored(gen, a) && !IsCellExplored(gen, b)) continue;

                if (door.Direction.x != 0)
                {
                    int edgeX = Mathf.Max(a.x, b.x) * pixelsPerCell;
                    FillRect(edgeX - t / 2, a.z * pixelsPerCell, Mathf.Max(1, t), pixelsPerCell, col);
                }
                else if (door.Direction.z != 0)
                {
                    int edgeZ = Mathf.Max(a.z, b.z) * pixelsPerCell;
                    FillRect(a.x * pixelsPerCell, edgeZ - t / 2, pixelsPerCell, Mathf.Max(1, t), col);
                }
            }
        }

        bool IsCellExplored(DungeonGenerator gen, Vector3Int c)
            => exploredCells.Contains(c) || IsRoomExplored(gen, c);

        void EnsureTexture(int w, int h)
        {
            if (tex != null && texW == w && texH == h) return;
            texW = w; texH = h;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                // Point filtering keeps cell blocks crisp instead of smearing them —
                // this is a diagram, not a photo.
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            pixels = new Color32[w * h];
            if (target != null) target.texture = tex;
        }

        void OnGUI()
        {
            if (!visible || target != null || tex == null || !Application.isPlaying) return;

            GUI.DrawTexture(overlayRect, tex, ScaleMode.ScaleToFit, true);

            if (!showFloorLabel) return;
            if (labelStyle == null)
                labelStyle = new GUIStyle { fontSize = 14, normal = { textColor = Color.white } };
            string label = $"Floor {CurrentFloorNumber} / {FloorCount}";
            if (revealAll) label += "   [REVEALED]";
            GUI.Label(new Rect(overlayRect.x, overlayRect.yMax + 2f, overlayRect.width, 20f),
                      label, labelStyle);
        }
    }
}
