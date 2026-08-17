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

        [Header("NPC standoff")]
        [Tooltip("Metres an NPC is held back from the door face while it is locked. Enough that the MESH stops short — a weapon held forward will still reach, which wants lowering the weapon rather than a bigger number here.\n\n0 disables the standoff entirely and NPCs press against the leaf as before.")]
        public float npcStandoff = 0.18f;
        [Tooltip("Layer for the standoff collider. It MUST collide with the NPC layer and NOTHING else — the player has to be able to walk up and shove the door to learn it is locked, and a standoff that blocks them would silently remove that.\n\nThe collision matrix row is the setup step; if the layer does not exist the standoff is skipped with a warning.\n\nNOT 'DoorJam' — that layer already means something else (it limits the door's swing so a shove cannot drive it past its hinge limits) and reusing it would put NPC standoff geometry into the swing constraint.")]
        public string standoffLayer = "DoorStandoff";

        PhysicsDoor door;
        GameObject standoff;

        bool standoffDone;

        void Awake()
        {
            door = GetComponent<PhysicsDoor>();
            door.Lock();
            door.OnLockedRattle += HandleRattle;
            door.OnUnlocked += HandleUnlocked;
        }

        /// <summary>
        /// Push the kit's authored values in and build the standoff.
        ///
        /// SEPARATE FROM Awake BECAUSE AddComponent RUNS Awake SYNCHRONOUSLY. GatePlacer adds
        /// this component at runtime, so anything it assigns afterwards would land AFTER Awake
        /// had already read the defaults — the standoff would silently use 0.18m and
        /// "DoorStandoff" whatever the kit said. Called explicitly rather than deferred to Start
        /// so it also works when generation is run from the editor's context menu, where Start
        /// never fires.
        /// </summary>
        public void Configure(float standoffMetres, string layerName, string message)
        {
            npcStandoff = standoffMetres;
            standoffLayer = layerName;
            if (!string.IsNullOrEmpty(message)) lockedMessage = message;
            BuildStandoff();
            standoffDone = true;
        }

        // A DoorLock placed by hand in a scene never gets Configure, so it builds its own from
        // the inspector values instead. Start, not Awake, so Configure has had its chance first.
        void Start()
        {
            if (!standoffDone) BuildStandoff();
        }

        void OnDestroy()
        {
            if (door != null)
            {
                door.OnLockedRattle -= HandleRattle;
                door.OnUnlocked -= HandleUnlocked;
            }
        }

        /// <summary>
        /// A slightly-proud collider that stops NPCs before their mesh reaches the door.
        ///
        /// A CHILD OF THE DOOR'S OWN COLLIDER, WHICH IS WHAT KEEPS THE RATTLE. Contact resolves
        /// through `hit.collider.attachedRigidbody`, so a child with no rigidbody of its own
        /// still reports the DOOR — the NPC presses the standoff and PhysicsDoor still shakes and
        /// still sounds. A separate free-standing blocker would have stopped them silently, which
        /// loses the whole reason the pushing is worth keeping.
        ///
        /// NOT A BIGGER CAPSULE. The controller radius is coupled to the agent radius (NpcLocomotion
        /// warns when they disagree), so raising it re-tunes pathing clearance and crowd packing
        /// to fix a purely visual problem — and it still would not help, because a weapon held
        /// forward reaches further than any capsule you would give a goblin.
        ///
        /// BUILT AT RUNTIME rather than authored, so it exists only on the handful of doors that
        /// are actually locked and needs no change to seven door prefabs.
        /// </summary>
        void BuildStandoff()
        {
            if (npcStandoff <= 0f) return;

            int layer = LayerMask.NameToLayer(standoffLayer);
            if (layer < 0)
            {
                Debug.LogWarning($"[DoorLock] Layer '{standoffLayer}' does not exist, so NPCs will " +
                                 "press their mesh into this door. Create it and set the collision " +
                                 "matrix so it hits ONLY the NPC layer.", this);
                return;
            }

            Collider src = door.GetComponent<Collider>() ?? door.GetComponentInChildren<Collider>();
            if (src == null) return;

            Vector3 centre, size;
            if (src is BoxCollider box) { centre = box.center; size = box.size; }
            else if (src is MeshCollider mesh && mesh.sharedMesh != null)
            { centre = mesh.sharedMesh.bounds.center; size = mesh.sharedMesh.bounds.size; }
            else return;

            // GROW THE THINNEST AXIS — that is the door's thickness, whichever way the leaf was
            // authored, so this needs no per-prefab setup. Converted through lossyScale because
            // these kit prefabs carry non-unit scale, and an unscaled offset would stand the
            // collider metres proud on one prefab and flush on another (the same trap the hinge
            // anchor carries).
            Vector3 scale = src.transform.lossyScale;
            int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            float axisScale = Mathf.Abs(thin == 0 ? scale.x : thin == 1 ? scale.y : scale.z);
            float grow = axisScale > 0.0001f ? (npcStandoff * 2f) / axisScale : 0f;
            size[thin] += grow;

            standoff = new GameObject("NpcStandoff") { layer = layer };
            standoff.transform.SetParent(src.transform, false);
            var col = standoff.AddComponent<BoxCollider>();
            col.center = centre;
            col.size = size;
        }

        void HandleUnlocked()
        {
            // Gone the moment it opens: an unlocked door should take an NPC's shoulder exactly
            // like any other, and a lingering blocker would keep them politely off a door they
            // are free to walk through.
            if (standoff != null) Destroy(standoff);
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
