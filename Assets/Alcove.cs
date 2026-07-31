using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// What an alcove IS, which decides what goes in it. Kinds are authored on RoomStyle
    /// (a PropSet each) and gated/weighted by depth on DepthProfile, the same shape RoomType
    /// already follows — so a kind is a content slot, not a behaviour.
    /// </summary>
    public enum AlcoveKind
    {
        /// <summary>A statue or idol facing out at the corridor. Shallow, look-in-only.</summary>
        StatueNook,
        /// <summary>Candles and an altar. Shallow, and the natural home for a coloured light.</summary>
        ShrineNiche,
        /// <summary>A broken-through pocket of rubble and bones. Enterable.</summary>
        CollapsedDig,
        /// <summary>Crates, barrels and sacks — enterable, and smashable since those props already are.</summary>
        StorageRecess,
    }

    /// <summary>
    /// One carved recess off a corridor.
    ///
    /// Alcoves are GRID-INVISIBLE: their cells are written as ordinary <see cref="CellType.Hallway"/>,
    /// so walls, floors, ceilings and corridor wall-sets all come free from the existing kit and
    /// mesher paths with no changes. A dedicated CellType would instead be read as solid-adjacent
    /// by every `!= CellType.Empty` test across the mesher and kit, and would render as nothing.
    /// Everything that makes an alcove an alcove therefore lives HERE, in metadata, exactly as
    /// PrisonCells / Ladders / ColumnPoints do.
    ///
    /// The consequence to remember: because the cells read as plain hallway, an alcove is a
    /// legal host for ANOTHER alcove. DungeonGenerator.TryPlaceAlcove guards against that
    /// explicitly; without the guard the pass carves branching tunnels rather than niches.
    /// </summary>
    public class AlcoveSpec
    {
        public AlcoveKind Kind;

        /// <summary>Bounding box of the whole shape, including the doorway tile. Mirrors the
        /// semantics of DungeonGenerator.PrisonCells entries — on a wide alcove it also covers
        /// the two solid corners beside the mouth, which nothing queries.</summary>
        public BoundsInt Bounds;

        /// <summary>The exact carved footprint. This, not Bounds, is the truth for prop
        /// placement and for testing whether a cell belongs to an alcove.</summary>
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();

        /// <summary>The corridor cell the alcove opens off. NOT part of <see cref="Cells"/> —
        /// it stays ordinary hallway, and props reserve it so debris can't pile up in front of
        /// the alcove's contents.</summary>
        public Vector3Int HallCell;

        /// <summary>
        /// HallCell → alcove. Load-bearing: it gives the alcove an intrinsic back/left/right
        /// frame, which is what lets the existing Feature anchor (authored in terms of a room's
        /// entrance direction) work here without zones, a centroid or an entrance axis.
        /// </summary>
        public Vector3Int Direction;

        /// <summary>The doorway tile, <c>HallCell + Direction</c>. On a wide alcove this is the
        /// 1x1 mouth and the space widens behind it.</summary>
        public Vector3Int MouthCell;

        public int Width;
        /// <summary>Cells deep BEHIND the mouth on a wide alcove; total depth on a width-1 one —
        /// the same convention prisonDepthRange uses.</summary>
        public int Depth;

        /// <summary>
        /// Deep enough to physically walk into, rather than a recess you only look at.
        ///
        /// INFORMATIONAL ONLY — it does NOT gate blocking props. It once did, which was a
        /// mistake: an alcove is a dead end, so a collider inside one severs nothing however
        /// shallow it is, and a 1x1 nook holding a statue you can't walk through is exactly what
        /// the shallow case is for. Currently read by the debug gizmo label.
        /// </summary>
        public bool IsEnterable => Depth + (Width > 1 ? 1 : 0) >= 2;

        /// <summary>World-space centre of the carved cells, for gizmos and distance tests.</summary>
        public Vector3 CenterCell
        {
            get
            {
                Vector3 sum = Vector3.zero;
                foreach (var c in Cells) sum += c;
                return Cells.Count > 0 ? sum / Cells.Count : (Vector3)MouthCell;
            }
        }
    }
}
