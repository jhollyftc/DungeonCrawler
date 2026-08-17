using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Adapts a <see cref="PhysicsDoor"/> to the <see cref="IGateLock"/> a lever drives.
    ///
    /// A THIN ADAPTER RATHER THAN AN INTERFACE ON PhysicsDoor, for the same reason the noise
    /// emitters are thin adapters onto the door and the bow: `PhysicsDoor` is about hinges,
    /// torque and swing audio, and it should not grow a dependency on the gate system to be
    /// openable by one. Added by the placer to the doors that need it; every other door in the
    /// dungeon is unaffected and unaware.
    ///
    /// ONE-SHOT, unlike the portcullis. A locked door opens once and stays unlocked — there is no
    /// re-locking, so `Toggle` is really "unlock" and repeated pulls are harmless.
    /// </summary>
    [RequireComponent(typeof(PhysicsDoor))]
    [DisallowMultipleComponent]
    public class DoorLock : MonoBehaviour, IGateLock
    {
        [Tooltip("Shown when the player shoves this door. Rate-limited by PhysicsDoor's rattle interval, not here — Push() fires every frame you lean on it.")]
        public string lockedMessage = "It's locked.";

        PhysicsDoor door;

        void Awake()
        {
            door = GetComponent<PhysicsDoor>();
            door.Lock();
            door.OnLockedRattle += HandleRattle;
        }

        void OnDestroy()
        {
            if (door != null) door.OnLockedRattle -= HandleRattle;
        }

        /// <summary>
        /// Tell the player why the door will not move.
        ///
        /// LIVES HERE, NOT IN PhysicsDoor. That component is about hinges, torque and swing
        /// audio, and every ordinary door in the dungeon carries it — giving it a dependency on
        /// the message system to serve the handful that are locked is the wrong direction. This
        /// adapter exists only on locked doors, so it is the natural place, and the same reason
        /// the noise emitters are thin adapters rather than fields on the door.
        /// </summary>
        void HandleRattle(float strength, bool fromPlayer)
        {
            // ONLY THE PLAYER GETS TOLD. The rattle and its sound still fire for an NPC — a
            // goblin shaking a bolted door is worth hearing — but a message on screen is
            // feedback about what YOU just did, and firing it for a creature crowding the far
            // side reads as the game talking nonsense.
            if (fromPlayer) PlayerMessage.Show(lockedMessage);
        }

        public bool IsOpen => door != null && !door.IsLocked;
        public Transform SoundOrigin => transform;

        public void Toggle()
        {
            if (door != null) door.Unlock();
        }
    }
}
