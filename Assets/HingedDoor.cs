using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Swinging door. Put this on the door prefab ROOT and assign `hinge` — an
    /// empty transform on the hinge axis with the leaf (mesh + collider) as its
    /// child. The door opens AWAY from whoever opened it.
    ///
    /// Import-rotation proof: the swing axis is computed as WORLD up expressed
    /// in the hinge's parent space at Awake, so FBX axis-correction rotations
    /// anywhere in the hierarchy can't turn yaw into pitch.
    /// </summary>
    public class HingedDoor : MonoBehaviour, IInteractable
    {
        [Tooltip("Empty transform on the hinge axis; the leaf is its child. Code rotates this.")]
        public Transform hinge;
        public float openAngle = 110f;
        [Tooltip("Degrees per second.")]
        public float swingSpeed = 160f;
        [Tooltip("Flip if the door opens toward the player instead of away (or, with One Way Swing, flips which fixed direction it opens).")]
        public bool invertSwing;
        [Tooltip("Always swing the same direction regardless of which side the player is on — e.g. prison gates that only open outward. invertSwing picks which direction.")]
        public bool oneWaySwing;
        public bool locked;
        public bool startOpen;

        [Header("Audio (optional)")]
        public AudioClip openClip;
        public AudioClip closeClip;
        public AudioClip lockedClip;

        bool isOpen;
        float currentAngle;
        float targetAngle;
        AudioSource audioSrc;

        Quaternion hingeRestRotation; // hinge local rotation when closed
        Vector3 swingAxisLocal;       // world up, expressed in the hinge's parent space
        bool facingCached;
        Vector3 facing;               // world-space normal of the closed door plane

        public string Prompt => locked ? "Locked" : isOpen ? "Close" : "Open";

        void Awake()
        {
            if (hinge == null)
            {
                Debug.LogWarning($"[HingedDoor] '{name}' has no hinge assigned — it won't move.", this);
            }
            else
            {
                hingeRestRotation = hinge.localRotation;
                swingAxisLocal = hinge.parent != null
                    ? hinge.parent.InverseTransformDirection(Vector3.up)
                    : Vector3.up;
            }

            isOpen = startOpen;
            currentAngle = targetAngle = startOpen ? openAngle : 0f;
            ApplyAngle();
        }

        public void Interact(Transform interactor)
        {
            if (locked)
            {
                Play(lockedClip);
                return;
            }

            isOpen = !isOpen;
            if (isOpen)
            {
                float sign;
                if (oneWaySwing)
                {
                    // Fixed direction, player position irrelevant.
                    sign = invertSwing ? -1f : 1f;
                }
                else
                {
                    // Swing away from whoever opened it, using a facing that does
                    // NOT come from the imported hierarchy's axes (see GetFacing).
                    Vector3 toPlayer = interactor.position - transform.position;
                    float side = Vector3.Dot(GetFacing(), toPlayer);
                    sign = (side >= 0f ? -1f : 1f) * (invertSwing ? -1f : 1f);
                }
                targetAngle = openAngle * sign;
            }
            else
            {
                targetAngle = 0f;
            }
            Play(isOpen ? openClip : closeClip);
        }

        /// <summary>
        /// World-space normal of the closed door plane — the direction you'd
        /// walk through the doorway. Derived from trustworthy sources instead
        /// of the (import-rotated, lying) transform axes:
        ///  1) DungeonDoorMarker, when the placer spawned this door: the actual
        ///     hallway->room direction. (Read lazily — the marker is added
        ///     after Instantiate, so Awake can't see it.)
        ///  2) Door geometry: hinge axis × (hinge -> leaf) = the plane normal.
        ///     Assumes the door is closed when first computed.
        ///  3) Flat-projected transform axes, as a last resort.
        /// The SIGN of the facing is arbitrary but consistent; invertSwing
        /// resolves it once per prefab.
        /// </summary>
        Vector3 GetFacing()
        {
            if (facingCached) return facing;

            var marker = GetComponent<DungeonDoorMarker>();
            if (marker != null && marker.direction != Vector3Int.zero)
            {
                facing = -(Vector3)marker.direction;
                facingCached = true;
                return facing;
            }

            if (hinge != null)
            {
                Bounds? leafBounds = null;
                var col = hinge.GetComponentInChildren<Collider>();
                if (col != null) leafBounds = col.bounds;
                else
                {
                    var rend = hinge.GetComponentInChildren<Renderer>();
                    if (rend != null) leafBounds = rend.bounds;
                }
                if (leafBounds.HasValue)
                {
                    Vector3 leafDir = Vector3.ProjectOnPlane(
                        leafBounds.Value.center - hinge.position, Vector3.up);
                    if (leafDir.sqrMagnitude > 1e-4f)
                    {
                        facing = Vector3.Cross(Vector3.up, leafDir).normalized;
                        facingCached = true;
                        return facing;
                    }
                }
            }

            Vector3 f = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-4f)
                f = Vector3.ProjectOnPlane(transform.up, Vector3.up);
            facing = f.normalized;
            facingCached = true;
            return facing;
        }

        void Update()
        {
            if (Mathf.Approximately(currentAngle, targetAngle)) return;
            currentAngle = Mathf.MoveTowards(currentAngle, targetAngle, swingSpeed * Time.deltaTime);
            ApplyAngle();
        }

        void ApplyAngle()
        {
            if (hinge == null) return;
            hinge.localRotation =
                Quaternion.AngleAxis(currentAngle, swingAxisLocal) * hingeRestRotation;
        }

        void Play(AudioClip clip)
        {
            if (clip == null) return;
            if (audioSrc == null)
            {
                audioSrc = gameObject.AddComponent<AudioSource>();
                audioSrc.spatialBlend = 1f; // 3D
                audioSrc.maxDistance = 15f;
            }
            audioSrc.PlayOneShot(clip);
        }
    }
}