using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Marks a prop as something the player can pick up, carry, and throw.
    ///
    /// Opt-in by design, exactly like PushableProp: a Rigidbody makes a prop
    /// SHOVABLE, but only a Carryable is LIFTABLE. That keeps "what can I take"
    /// an authored decision rather than an accident of which props happened to
    /// get a Rigidbody.
    ///
    /// IMPORTANT — TIER: a carryable prop must be authored as PropTier.FullGameObject.
    /// The instanced tiers bake the MESH into a static matrix in
    /// InstancedDungeonRenderer and only give the prop a collider GameObject, so
    /// picking up a StaticCollider barrel moves the collider while the barrel you
    /// can SEE stays welded to the floor. Carryables are low-count by nature, so
    /// the batching loss is irrelevant.
    ///
    /// Tuning lives HERE, per object, so nothing you do to a barrel can un-tune
    /// the player's carry rig — the same split that keeps PushableProp from
    /// disturbing the doors.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class Carryable : MonoBehaviour, IInteractable
    {
        [Tooltip("Name shown in the interaction prompt (\"[E] Pick up Barrel\").")]
        public string displayName = "Barrel";

        [Tooltip("Metres in front of the eye this object floats while carried. Big objects need more room or they push into the camera's near plane — size this to the prop, which is exactly why it lives on the prop and not on the player.")]
        public float holdDistance = 1.3f;

        [Tooltip("Speed (m/s) this object leaves your hands at. AUTHORED per object rather than derived from mass: mass already decides what a thrown prop does in flight and what it hits like, and deriving launch speed from it as well made heavy props read as BROKEN rather than heavy.")]
        public float throwSpeed = 9f;

        [Tooltip("Spin (rad/s) imparted on release, about the camera's right axis. A barrel tumbling end-over-end reads as THROWN; one that flies dead flat reads as slid.")]
        public float throwSpin = 4f;

        /// <summary>The body the carry rig drives. Cached — carried props are queried every FixedUpdate.</summary>
        public Rigidbody Body { get; private set; }

        void Awake() => Body = GetComponent<Rigidbody>();

        public string Prompt => $"Pick up {displayName}";

        public void Interact(Transform interactor)
        {
            if (interactor == null) return;

            // The interactor component may live on the player root OR on the eye,
            // depending on how the prefab is rigged, so look both up and out.
            PlayerCarry carry = interactor.GetComponentInParent<PlayerCarry>();
            if (carry == null) carry = interactor.root.GetComponentInChildren<PlayerCarry>();
            if (carry == null) return;

            carry.TryPickUp(this);
        }
    }
}
