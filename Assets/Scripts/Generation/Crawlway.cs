using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A 1.5m crawl passage bored through solid rock between two places that are otherwise a
    /// long walk apart. The player crouches through; at 1.5m nothing bakes walkable, so NPCs
    /// are excluded by GEOMETRY rather than by a rule anyone has to maintain.
    ///
    /// ITS CELLS STAY <see cref="CellType.Empty"/>, and that is the whole design — the sharpest
    /// difference from <see cref="AlcoveSpec"/>, which is typed Hallway precisely so it inherits
    /// walls, floors and ceilings free from the kit and mesher. A crawlway must NOT: every kit
    /// piece is authored to a 3m face, so an open CellType would emit full-size masonry into a
    /// hole meant to be a bore through rock. To the mesher, the kit placer, NeedsSlabBetween,
    /// the automap and every `!= CellType.Empty` test in the project, a crawlway DOES NOT EXIST
    /// and the rock is solid.
    ///
    /// The price of that choice, and the entire cost of the feature: the crawlway brings its
    /// OWN mesh and its OWN collider. Precedent is the bridge deck and the ladder — generator-
    /// owned kit pieces placed at nominal grid coordinates, base-origin, carrying prefab
    /// colliders because the greybox cannot provide them (§5).
    ///
    /// Consequence worth knowing: because the cells read as solid, a crawlway is NOT a legal
    /// host for another crawlway, so it is immune to §12's self-hosting trap that alcoves and
    /// junction plazas both hit. The <c>IsCrawlwayCell</c> guard still exists — two bores must
    /// not intersect — but there is no creeping-blob failure mode here.
    /// </summary>
    public class CrawlwaySpec
    {
        /// <summary>
        /// The bored cells, IN ORDER from the A end to the B end. Every one stays
        /// <see cref="CellType.Empty"/> in the grid; this list is the only thing that knows
        /// they are a tunnel. Never empty — a crawlway is at minimum one cell of rock punched
        /// through, which is the "breach a thin wall" case.
        /// </summary>
        public List<Vector3Int> Cells = new List<Vector3Int>();

        /// <summary>The OPEN cell the A mouth opens off. NOT part of <see cref="Cells"/>.</summary>
        public Vector3Int CellA;
        /// <summary>The OPEN cell the B mouth opens into. NOT part of <see cref="Cells"/>.</summary>
        public Vector3Int CellB;

        /// <summary><c>CellA</c> → <c>Cells[0]</c>. The direction the A grate faces out of.</summary>
        public Vector3Int DirA;
        /// <summary>Last bore cell → <c>CellB</c>. The direction the B grate faces out of.</summary>
        public Vector3Int DirB;

        /// <summary>
        /// Length of the pre-existing walk between the two mouths, in cells. Always finite and
        /// always found: a pair whose ends are NOT already connected is rejected outright, which
        /// is what keeps a crawlway from ever being load-bearing for connectivity.
        /// </summary>
        public int WalkDistance;

        /// <summary>How much walking the crawlway saves — <c>WalkDistance / Cells.Count</c>.
        /// The rule that makes a crawlway MEAN something rather than being a hole between two
        /// spots you could already see each other from.</summary>
        public float DetourRatio => Cells.Count > 0 ? WalkDistance / (float)Cells.Count : 0f;

        /// <summary>Direction of travel INTO cell <paramref name="i"/>. For the first cell that
        /// is <see cref="DirA"/> — you enter through the grate, so the mouth's direction is a
        /// real part of the run and not a special case.</summary>
        public Vector3Int DirInto(int i) => i == 0 ? DirA : Cells[i] - Cells[i - 1];

        /// <summary>Direction of travel OUT OF cell <paramref name="i"/>. For the last cell that
        /// is <see cref="DirB"/>.</summary>
        public Vector3Int DirOutOf(int i) =>
            i == Cells.Count - 1 ? DirB : Cells[i + 1] - Cells[i];

        /// <summary>
        /// Does the bore turn within cell <paramref name="i"/>, so it wants a corner piece
        /// rather than a straight?
        ///
        /// THE END CELLS COUNT. A first cell entered through a grate on one face and left
        /// through another is geometrically a corner, whatever it is called — an earlier
        /// version excluded the ends on the reasoning that "their outward direction is the
        /// mouth's", which is true and irrelevant, and would have put a straight tube where
        /// the tunnel visibly turns. Defined here rather than recomputed by the placer so
        /// there is one answer (§10b's one-resolution rule).
        /// </summary>
        public bool IsCorner(int i) => DirInto(i) != DirOutOf(i);

        /// <summary>World-space centre of the bore, for gizmos and distance tests.</summary>
        public Vector3 CenterCell
        {
            get
            {
                Vector3 sum = Vector3.zero;
                foreach (var c in Cells) sum += c;
                return Cells.Count > 0 ? sum / Cells.Count : (Vector3)CellA;
            }
        }
    }
}
