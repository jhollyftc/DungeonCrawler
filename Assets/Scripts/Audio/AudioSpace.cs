using UnityEngine;

namespace DungeonGen
{
    /// <summary>What KIND of space the player is standing in. Reverb and ambience both key off this.</summary>
    public enum SpaceKind { None, Pit, Room, Prison, Alcove, Hallway }

    /// <summary>
    /// Which audio space the player is in — the ONE resolution shared by AmbientDirector and
    /// ReverbDirector.
    ///
    /// WHY IT IS EXTRACTED. Both consumers need exactly this answer, and the resolution has a
    /// load-bearing ORDER that is not obvious from any one call site: a pit cell ALSO resolves
    /// to a Room (RoomAt deliberately falls through PitAt so a pit is styled as part of its
    /// room, §4), and an alcove cell is CellType Hallway (§4's grid-invisible design), so the
    /// most specific space has to be asked about first. Two copies of that order would drift,
    /// and the symptom would be ambience and reverb disagreeing about what room you are in —
    /// the same class of bug the shared ComputeZones, NeedsSlabBetween and PropSnap.NearStair
    /// helpers exist to prevent.
    ///
    /// NB corridors, alcoves, prisons and pits ALL have `CurrentRoom == null`, which is why
    /// consumers must watch the resolved SPACE and never `PlayerRoomTracker.OnRoomChanged` —
    /// that event fires for exactly one of the five spaces the game has.
    /// </summary>
    public struct AudioSpace
    {
        public SpaceKind Kind;
        public AudioProfile Profile;

        /// <summary>The room, when there is one — also set for a PIT, whose owner is a room.</summary>
        public Room Room;
        public PitSpec Pit;

        /// <summary>
        /// Room size in CELLS, 0 where there is no room. This is a VOLUME, not an area:
        /// BuildFootprint writes a room's footprint at every Y within its bounds, so a
        /// two-storey room counts double. That is CORRECT for reverb and must not be
        /// "fixed" — a tall hall really does sound bigger than a low one of the same
        /// floor area. Written down because it reads like a bug.
        /// </summary>
        public int SizeCells;

        public bool Valid => Kind != SpaceKind.None;

        /// <summary>
        /// Cell count of a room, falling back to its BOUNDING BOX volume when Cells is empty.
        /// That is not a defensive nicety: Room documents an empty Cells set as "treat as a
        /// full box (legacy safety)", so a room can legitimately report 0 cells while being a
        /// perfectly ordinary room — and a reverb driver reading 0 would size the grandest
        /// hall in the dungeon as a closet.
        /// </summary>
        static int MeasureSize(Room r)
        {
            if (r == null) return 0;
            if (r.Cells != null && r.Cells.Count > 0) return r.Cells.Count;
            return Mathf.Max(0, r.Bounds.size.x * r.Bounds.size.y * r.Bounds.size.z);
        }

        public static AudioSpace Resolve(DungeonVisualizer vis, PlayerRoomTracker tracker)
        {
            var result = new AudioSpace { Kind = SpaceKind.None };
            if (vis == null || tracker == null || !tracker.HasPlayer) return result;

            var style = vis.roomStyle;
            var gen = vis.Generator;
            if (style == null || gen == null) return result;

            Vector3Int cell = tracker.CurrentCell;

            // Pit FIRST — a pit cell resolves to a Room as well, so asking about rooms
            // first would mean a chasm never gets its own acoustics.
            PitSpec pit = tracker.CurrentPit;
            if (pit != null)
            {
                RoomType owner = pit.Owner != null ? pit.Owner.Type : RoomType.Generic;
                result.Kind = SpaceKind.Pit;
                result.Profile = style.PitAudio(owner);
                result.Pit = pit;
                result.Room = pit.Owner;
                result.SizeCells = MeasureSize(pit.Owner);
                return result;
            }

            Room room = tracker.CurrentRoom;
            if (room != null)
            {
                result.Kind = SpaceKind.Room;
                result.Profile = style.AudioFor(room.Type);
                result.Room = room;
                result.SizeCells = MeasureSize(room);
                return result;
            }

            if (gen.PrisonAt(cell) != null)
            {
                result.Kind = SpaceKind.Prison;
                result.Profile = style.PrisonAudio();
                return result;
            }

            // Alcoves are typed Hallway, so this must be asked BEFORE falling through
            // to the corridor profile or a statue nook sounds like open corridor.
            AlcoveSpec alcove = gen.AlcoveAt(cell);
            if (alcove != null)
            {
                result.Kind = SpaceKind.Alcove;
                result.Profile = style.AlcoveAudio(alcove.Kind);
                return result;
            }

            result.Kind = SpaceKind.Hallway;
            result.Profile = style.HallwayAudio();
            return result;
        }
    }
}
