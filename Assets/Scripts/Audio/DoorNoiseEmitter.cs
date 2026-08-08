using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Bridges a PhysicsDoor's events onto the NoiseBus. Put it on the door
    /// prefabs. A door already carries its own quiet-swing gate (thunkArmAngle),
    /// so a door barely nudged in a shoving match stays silent here too — stealth
    /// is preserved for free. A sneaking player easing a door open makes little
    /// noise; someone barging through slams it and is heard across the room.
    /// </summary>
    [RequireComponent(typeof(PhysicsDoor))]
    [DisallowMultipleComponent]
    public class DoorNoiseEmitter : MonoBehaviour
    {
        [Tooltip("Faction of door noises. Neutral: a door doesn't take sides.")]
        public Faction faction = Faction.Neutral;
        [Tooltip("Swing speed (rad/s) that counts as loudness 1 for the start-of-swing creak and the closing thunk. A slam is always full loudness.")]
        public float referenceSwingSpeed = 4f;
        [Tooltip("The creak as a door starts moving is much quieter than the impacts — this scales it down. Someone easing a door open shouldn't broadcast.")]
        [Range(0f, 1f)] public float swingStartScale = 0.4f;

        PhysicsDoor door;

        void Awake() => door = GetComponent<PhysicsDoor>();

        void OnEnable()
        {
            if (door == null) return;
            door.OnSwingStart += HandleSwingStart;
            door.OnClosed += HandleClosed;
            door.OnSlamOpen += HandleSlam;
        }

        void OnDisable()
        {
            if (door == null) return;
            door.OnSwingStart -= HandleSwingStart;
            door.OnClosed -= HandleClosed;
            door.OnSlamOpen -= HandleSlam;
        }

        float Norm(float speed) => Mathf.Clamp01(speed / Mathf.Max(0.01f, referenceSwingSpeed));

        void HandleSwingStart(float speed) => NoiseBus.Emit(transform.position, Norm(speed) * swingStartScale, transform, faction);
        void HandleClosed(float speed) => NoiseBus.Emit(transform.position, Norm(speed), transform, faction);
        void HandleSlam(float speed) => NoiseBus.Emit(transform.position, 1f, transform, faction);
    }
}
