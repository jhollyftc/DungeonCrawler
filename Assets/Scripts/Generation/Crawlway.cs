using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>One access point: a grate in a wall between an open cell and a bore cell.</summary>
    public struct CrawlwayMouth
    {
        /// <summary>The OPEN dungeon cell the grate is seen from. Not part of the network.</summary>
        public Vector3Int OpenCell;
        /// <summary>From <see cref="OpenCell"/> into the rock — the face the grate replaces.</summary>
        public Vector3Int IntoRock;
        /// <summary>The bore cell behind the grate. <c>OpenCell + IntoRock</c>.</summary>
        public Vector3Int BoreCell => OpenCell + IntoRock;
    }

    /// <summary>
    /// A MANHOLE: a drain in a prison cell's floor, dropping into the network below.
    ///
    /// ONE-WAY, AND THAT IS THE POINT. The drop is a storey with no mantle mechanic and a 0.5m
    /// step height, so there is no climbing back out — going down commits you to finding a wall
    /// grate. Which makes it a HARD CONSTRAINT on generation rather than flavour: a network may
    /// only take a manhole if it already has at least one ordinary mouth, or the player is
    /// sealed in a tunnel system with no exit and the run is over.
    /// </summary>
    public struct CrawlwayManhole
    {
        /// <summary>The network cell directly beneath the floor.</summary>
        public Vector3Int BoreCell;
        /// <summary>The prison cell you drop THROUGH — the floor tile that gets suppressed.</summary>
        public Vector3Int OpenCell => BoreCell + Vector3Int.up;
    }

    /// <summary>
    /// A sewer chamber: a full-height room carved off one bore cell, sealed but for its grate.
    /// </summary>
    /// <summary>One grate between a chamber cell and a bore cell.</summary>
    public struct ChamberOpening
    {
        /// <summary>The chamber cell (typed Hallway) the grate is seen from.</summary>
        public Vector3Int ChamberCell;
        /// <summary>Chamber → bore. The face the grate replaces.</summary>
        public Vector3Int IntoBore;
        public Vector3Int BoreCell => ChamberCell + IntoBore;

        /// <summary>
        /// Does this opening actually have BARS to wrench off, or is it simply a hole?
        ///
        /// Once chambers gained through-routes the number of grates per run multiplied, and
        /// wrenching each one stopped being a moment and became a toll. Rarity is what makes the
        /// mechanic mean anything — an opening you can just crawl through costs nothing to pass
        /// and makes the barred one worth noticing.
        /// </summary>
        public bool HasGrate;
    }

    public class CrawlwayChamber
    {
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();
        public BoundsInt Bounds;
        /// <summary>The bore cell it was carved off. Its PRIMARY entrance.</summary>
        public Vector3Int BoreCell;
        /// <summary>Bore → chamber. The frame RecessPropPlacer orients its hero prop by, so it
        /// stays the ORIGINAL entrance even once the chamber has several.</summary>
        public Vector3Int Dir;
        /// <summary>Entry tile, <c>BoreCell + Dir</c>. On a wide chamber this is the vestibule.</summary>
        public Vector3Int MouthCell;

        /// <summary>
        /// Every grate into this chamber, primary first.
        ///
        /// A CHAMBER CAN TOUCH SEVERAL TUNNELS WITHOUT KNOWING IT. `RecessFits`' one-opening rule
        /// forbids a footprint touching any OPEN cell but its host — and bore cells are
        /// `CellType.Empty`, so nothing stops a chamber being carved hard against two or three
        /// other tunnels. Small ones in particular end up wedged between passages with a single
        /// way in, which makes them a detour you back out of rather than something you pass
        /// through. Extra grates turn that into a route.
        /// </summary>
        public List<ChamberOpening> Openings = new List<ChamberOpening>();
    }

    /// <summary>
    /// A SEWER NETWORK — a branching system of 1.5m bores through solid rock, with chambers
    /// hanging off it and a small number of grates connecting it to the dungeon.
    ///
    /// GROWN FROM THE ROCK, NOT BETWEEN TWO POINTS, and that inversion is the whole v2 design.
    /// The first version defined a crawlway AS a pair of mouths with a bore between them, so
    /// mouths were what the generator chose — and "two corridors four metres apart through one
    /// cell of rock" is a perfectly valid answer to that question. Every constraint bolted on to
    /// stop that (a minimum length, a detour ratio, one crawlway per pair of spaces) was a patch
    /// on a badly framed question. Growing the network first and spending a small budget of
    /// mouths on it afterwards makes the degenerate case INEXPRESSIBLE: there is no step at
    /// which two nearby mouths are ever considered as a pair.
    ///
    /// CELLS STAY <see cref="CellType.Empty"/>, exactly as in v1 — the mesher, the kit placer
    /// and every `!= CellType.Empty` test read solid rock, and the network brings its own mesh
    /// and collision. Chamber cells are the exception and are typed Hallway, because a chamber
    /// is a real 3m space that wants the kit's walls and floors.
    ///
    /// THE CELL SET IS A GRAPH, NOT A PATH. v1 carried an ordered list because a crawlway was a
    /// route; a network branches, so piece selection comes from each cell's 4-bit
    /// <see cref="NeighbourMask"/> instead of from an in/out direction pair. That collapsed
    /// IsCorner/DirInto/DirOutOf into one mask→(piece, yaw) lookup — the same bitmask approach
    /// DungeonMapper already uses for walls.
    /// </summary>
    public class CrawlwaySpec
    {
        /// <summary>Every bore cell. Unordered — this is a graph.</summary>
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();

        /// <summary>Grates into the dungeon. Kept small on purpose: the interior is generous,
        /// the way in is not.</summary>
        public List<CrawlwayMouth> Mouths = new List<CrawlwayMouth>();

        /// <summary>Sewer rooms hanging off the network.</summary>
        public List<CrawlwayChamber> Chambers = new List<CrawlwayChamber>();

        /// <summary>One-way drains dropping in from prison floors. Only ever populated on a
        /// network that already has a <see cref="Mouths"/> entry — see CrawlwayManhole.</summary>
        public List<CrawlwayManhole> Manholes = new List<CrawlwayManhole>();

        /// <summary>Does a manhole drop into this bore cell?</summary>
        public bool ManholeAt(Vector3Int c)
        {
            foreach (var m in Manholes) if (m.BoreCell == c) return true;
            return false;
        }

        /// <summary>Longest walk between any two mouths through the OPEN dungeon, in cells —
        /// what the network short-circuits. Informational; used for the gizmo label and for
        /// judging whether a network earned its mouths.</summary>
        public int BestDetour;

        public bool HasChamber => Chambers.Count > 0;

        // Bit per horizontal direction, matching HorizontalDirs' order in DungeonGenerator:
        // +X, -X, +Z, -Z.
        public const int MaskPosX = 1 << 0;
        public const int MaskNegX = 1 << 1;
        public const int MaskPosZ = 1 << 2;
        public const int MaskNegZ = 1 << 3;

        static readonly Vector3Int[] MaskDirs =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };

        /// <summary>Direction for a mask bit index (0-3).</summary>
        public static Vector3Int DirOfBit(int bit) => MaskDirs[bit];

        /// <summary>
        /// Which of this cell's four horizontal neighbours are also bore.
        ///
        /// THE ONE INPUT PIECE SELECTION NEEDS. Popcount gives the piece — 1 dead end, 2 either
        /// straight or corner, 3 tee, 4 cross — and the bit pattern gives the yaw. A chamber
        /// opening does NOT set a bit: the chamber is a room off the side, and its opening is a
        /// grate in the tube wall rather than another length of tube, so it is a tee's SIDE
        /// hole and not a fifth connection.
        /// </summary>
        public int NeighbourMask(Vector3Int c)
        {
            int mask = 0;
            for (int i = 0; i < MaskDirs.Length; i++)
                if (Cells.Contains(c + MaskDirs[i])) mask |= 1 << i;
            return mask;
        }

        /// <summary>Is this cell an access point, and which way does its grate face out?</summary>
        public bool MouthAt(Vector3Int boreCell, out Vector3Int outward)
        {
            foreach (var m in Mouths)
                if (m.BoreCell == boreCell) { outward = -m.IntoRock; return true; }
            outward = Vector3Int.zero;
            return false;
        }

        /// <summary>
        /// Every opening a bore cell has that is NOT another length of tube, as world directions
        /// out of the cell — chamber grates and wall grates alike.
        ///
        /// ONE LIST, BECAUSE THE MASK'S BLIND SPOT IS ONE PROBLEM. `NeighbourMask` counts tunnel
        /// adjacency, so anything else is invisible to piece selection: a chamber gets walled off
        /// behind a straight, a grate gets a dead-end cap over the entrance. Three separate
        /// things were sealed shut before this was collected in one place, each fixed
        /// individually — so a fourth kind of opening should be added HERE and become correct
        /// everywhere at once.
        /// </summary>
        public void NonTunnelOpenings(Vector3Int boreCell, List<Vector3Int> into)
        {
            into.Clear();
            foreach (var ch in Chambers)
                foreach (var op in ch.Openings)
                    if (op.BoreCell == boreCell && !into.Contains(-op.IntoBore))
                        into.Add(-op.IntoBore);          // bore -> chamber
            foreach (var m in Mouths)
                if (m.BoreCell == boreCell && !into.Contains(-m.IntoRock))
                    into.Add(-m.IntoRock);               // bore -> open cell
        }

        /// <summary>World-space centre of the bore, for gizmos and distance tests.</summary>
        public Vector3 CenterCell
        {
            get
            {
                if (Cells.Count == 0) return Vector3.zero;
                Vector3 sum = Vector3.zero;
                foreach (var c in Cells) sum += c;
                return sum / Cells.Count;
            }
        }
    }
}
