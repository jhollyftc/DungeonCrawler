using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A prison closet carved off a corridor. Deliberately the same shape as AlcoveSpec,
    /// because a prison and an alcove ARE the same generator primitive — both come out of
    /// `RecessFits`, both are validated dead-end pockets hanging off one hallway cell. They
    /// differ only in the CellType they commit (Prison vs Hallway) and in what content they
    /// take. `RecessPropPlacer` consumes both through one code path for that reason.
    ///
    /// WHY THIS REPLACED `List&lt;BoundsInt&gt; PrisonCells`: a bare bounding box cannot support
    /// a `Feature` prop. Feature placement needs the recess's intrinsic back/left/right frame,
    /// which comes from `Direction` — without it a bunk lands on a random wall and the cell
    /// reads as scattered junk rather than a place someone was kept. Every field here was
    /// already computed by `RecessFits` at carve time and simply thrown away.
    ///
    /// A WIDE cell is NOT a rectangle: the one-opening rule forbids a wide mouth on a corridor,
    /// so a wide prison gets a 1x1 doorway tile with the cell widening BEHIND it. `Cells` is
    /// therefore the true footprint and `Bounds` merely encloses it — the same relationship
    /// AlcoveSpec documents, and the reason nothing should iterate the bbox expecting cells.
    /// </summary>
    public class PrisonSpec
    {
        /// <summary>Enclosing box. Kept for spatial queries and the log; NOT the footprint.</summary>
        public BoundsInt Bounds;

        /// <summary>The exact cells, including the 1x1 doorway tile of a wide cell.</summary>
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();

        /// <summary>The corridor cell this opens off. NOT part of Cells.</summary>
        public Vector3Int HallCell;

        /// <summary>HallCell -> cell. The back/left/right frame every Feature anchor needs.</summary>
        public Vector3Int Direction;

        /// <summary>
        /// The doorway tile — where the bars or the door are emitted.
        ///
        /// NOTHING BLOCKING MAY BE PLACED HERE, and it is what makes skipping the connectivity
        /// flood-fill safe: a dead end cannot sever the dungeon, but it CAN seal itself, and a
        /// crate in the doorway of a locked cell is indistinguishable from a generation bug.
        /// Alcoves need no equivalent because they have no door.
        /// </summary>
        public Vector3Int MouthCell;

        public int Width;
        public int Depth;

        /// <summary>Can something stand in here, or is it a view-only slot? Informational —
        /// blocking props are legal at any depth in a dead end (see AlcoveSpec.IsEnterable for
        /// the reasoning and the bug that came from enforcing it).</summary>
        public bool IsEnterable => Depth + (Width > 1 ? 1 : 0) >= 2;

        public bool Contains(Vector3Int c) => Cells.Contains(c);
    }
}
