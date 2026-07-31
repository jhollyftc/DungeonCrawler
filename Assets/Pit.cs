using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A hole in a room's floor, dropping into a space carved beneath it, optionally spanned
    /// by a bridge and escaped by ladder.
    ///
    /// Pits live in ROOMS, never corridors, and that is structural rather than a design
    /// preference: <see cref="HallwayPathfinder.SurroundingsOk"/> requires solid rock above
    /// AND below every corridor cell, so open-under-open cannot exist in a hallway. A tall
    /// room is already an open volume spanning two levels; a pit applies that same idea to a
    /// SUBSET of a room's cells.
    ///
    /// PIT CELLS ARE DELIBERATELY NOT IN Room.Cells OR Room.Bounds. Extending a room's bounds
    /// downward is the obvious approach and it corrupts five separate systems that read
    /// `Bounds.yMin` as "the room's floor": InteriorFloorCell, AllocateInteriorStairs,
    /// AllocateLadders, RecordDoor's IsElevated test, and ChooseStartAndExit's climb-out
    /// progression. A room with a pit would read one storey deeper than it is, its doors would
    /// become elevated, and the vertical Start/Exit rule would pick the wrong rooms.
    ///
    /// Putting the cells in Room.Cells while leaving Bounds alone very nearly works — RoomAt
    /// would resolve them and NeedsSlabBetween would drop the floor with no code change at all
    /// — but Cells is iterated by ComputeZones, CellCount and the prop placers, every one of
    /// which assumes a single floor level. Too many silent consumers to be worth the elegance.
    /// So pits keep their own registry, and NeedsSlabBetween carries one explicit override.
    /// </summary>
    public class PitSpec
    {
        /// <summary>The room this pit is cut into. Used to resolve RoomStyle for the pit's own
        /// walls and floor, via DungeonGenerator.RoomAt's fallback.</summary>
        public Room Owner;

        /// <summary>
        /// Room-FLOOR cells with no floor — the hole you fall through. These stay
        /// CellType.Room and remain part of Owner.Cells; only the slab beneath them is
        /// suppressed. Anything that places things on a floor must skip these.
        /// </summary>
        public HashSet<Vector3Int> Openings = new HashSet<Vector3Int>();

        /// <summary>The carved cells BELOW the openings — the pit's interior. Not in
        /// Owner.Cells; DungeonGenerator.PitAt is how they resolve back to a room.</summary>
        public HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();

        /// <summary>
        /// Openings that get a BRIDGE piece — walkable, and the only way across a pit that
        /// severs its room. Bridges are generator-owned kit pieces rather than props precisely
        /// so this can be true: a prop could decline to place, and the generator's cell-level
        /// flood-fill could never see it (cell connectivity != navmesh connectivity, §10).
        /// As a kit piece the crossing is guaranteed by construction and provable at
        /// generation time.
        /// </summary>
        public HashSet<Vector3Int> BridgeCells = new HashSet<Vector3Int>();

        /// <summary>Floor level of the room this pit is cut into (the level you fall FROM).</summary>
        public int FloorY;

        /// <summary>
        /// The direction you TRAVEL when crossing — perpendicular to the chasm, which runs
        /// along the other axis. A bridge deck must be oriented by this or it lies parallel to
        /// the pit instead of spanning it (real bug: the deck was placed with
        /// Quaternion.identity, so it was correct only when the chasm happened to run the right
        /// way).
        /// </summary>
        public Vector3Int CrossDirection = new Vector3Int(1, 0, 0);

        /// <summary>How many cells deep. 2 where the rock allows, 1 where it doesn't —
        /// shrink-to-fit, the same pattern prisons and alcoves use.</summary>
        public int DepthCells;

        /// <summary>Lowest level of the pit — where you land.</summary>
        public int BottomY => FloorY - DepthCells;

        /// <summary>World-ish centre of the opening, for gizmos and spans.</summary>
        public Vector3 OpeningCenter
        {
            get
            {
                if (Openings.Count == 0) return Vector3.zero;
                Vector3 sum = Vector3.zero;
                foreach (var c in Openings) sum += c;
                return sum / Openings.Count;
            }
        }
    }
}
