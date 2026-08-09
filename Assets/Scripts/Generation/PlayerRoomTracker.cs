using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The ONE answer to "where is the player standing" — cell, room, and pit.
    ///
    /// WHY THIS EXISTS. Three systems already computed this independently
    /// (DungeonFogController, FirstPersonController's room readout, DungeonMapper's
    /// reveal), and the audio system wants three more: the ambient crossfade, the reverb
    /// blend and the footstep surface. Six copies of one query is six chances for the map,
    /// the fog and the sound of a room to disagree about which room you are in.
    ///
    /// AND THEY ALREADY DID. CLAUDE.md claimed fog and the room readout "can never
    /// disagree" because they share the same world-to-cell + RoomAt path — but they were
    /// sharing the CODE SHAPE, not an answer: the fog sampled `Camera.main.transform`
    /// (eye height) while the readout and the map sampled the player transform (feet).
    /// Lean through a doorway, or stand where a stairwell puts your eyes in the cell above
    /// your feet, and they genuinely differed. Copying a lookup is not sharing it.
    ///
    /// SAMPLES THE PLAYER TRANSFORM (feet), deliberately. "Which room am I in" is a
    /// question about where you are STANDING — it decides the floor you hear underfoot and
    /// the room whose air you are breathing — and feet do not poke through doorways ahead
    /// of the body the way a camera does.
    ///
    /// Lives on the DungeonVisualizer beside the fog controller and the mapper, since it
    /// needs the generator, the cell size and the dungeon origin.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(DungeonVisualizer))]
    public class PlayerRoomTracker : MonoBehaviour
    {
        [Tooltip("The player root — its position is the sample point (feet, not eyes). Left empty it is found automatically, and re-found after a regenerate since the player is respawned with the dungeon.")]
        public Transform player;

        DungeonVisualizer vis;
        DungeonGenerator cachedGen;
        int refreshedFrame = -1;

        /// <summary>The cell the player's feet are in. Meaningless when HasPlayer is false.</summary>
        public Vector3Int CurrentCell { get; private set; }
        /// <summary>The room the player is in, or NULL in a corridor — which is MEANINGFUL,
        /// not a failure: corridors have their own style, palette and audio profile.</summary>
        public Room CurrentRoom { get; private set; }
        /// <summary>The pit the player is inside, or null. Published here because a pit is a
        /// room cell that isn't quite a room cell (§12's category rule) and both reverb and
        /// ambient want to treat it differently — resolving it once beats each rediscovering it.</summary>
        public PitSpec CurrentPit { get; private set; }
        public bool HasPlayer => player != null && cachedGen != null;

        /// <summary>Fires when the player crosses into a different room, corridors included
        /// (null is a legitimate "room"). Args: (from, to).</summary>
        public event System.Action<Room, Room> OnRoomChanged;

        void Awake() => vis = GetComponent<DungeonVisualizer>();

        void Update() => Refresh();

        /// <summary>
        /// Recompute if this frame hasn't already. FRAME-STAMPED rather than relying on
        /// execution order: `DefaultExecutionOrder` gets the event out early, but a reader
        /// whose own order puts it before this component would otherwise silently read last
        /// frame's room. Same discipline as NpcLocomotion's `facingLockedFrame`.
        /// </summary>
        public void Refresh()
        {
            if (refreshedFrame == Time.frameCount) return;
            refreshedFrame = Time.frameCount;

            DungeonGenerator gen = vis != null ? vis.Generator : null;

            // The dungeon regenerates on F1/PgUp and the player is respawned with it, so a
            // cached transform goes stale. Re-find whenever the generator instance changes —
            // the same "watch the generator to know you were rebuilt" trick DungeonMapper uses.
            if (!ReferenceEquals(gen, cachedGen))
            {
                cachedGen = gen;
                player = null;
                CurrentRoom = null;
                CurrentPit = null;
            }
            if (gen == null) return;

            // Found by COMPONENT, not by tag: FirstPersonController is what the player IS
            // here, whereas a tag is a separate thing to remember to set on a respawned
            // prefab. Same lookup DungeonMapper already used.
            if (player == null)
            {
                var fpc = Object.FindFirstObjectByType<FirstPersonController>();
                if (fpc != null) player = fpc.transform;
            }
            if (player == null) return;

            CurrentCell = Vector3Int.FloorToInt(
                (player.position - vis.transform.position) / vis.cellSize);

            Room was = CurrentRoom;
            CurrentRoom = gen.RoomAt(CurrentCell);
            CurrentPit = gen.PitAt(CurrentCell);

            if (!ReferenceEquals(was, CurrentRoom)) OnRoomChanged?.Invoke(was, CurrentRoom);
        }
    }
}
