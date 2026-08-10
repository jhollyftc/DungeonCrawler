using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// THE ONE OWNER OF THE WORLD CAMERA'S FIELD OF VIEW. Everything that wants to widen or
    /// narrow it asks here instead of writing `Camera.fieldOfView` itself.
    ///
    /// WHY THIS EXISTS AT ALL. `PlayerBow` (draw zoom) and `PlayerMelee` (bash bump) each
    /// cached their own `baseFov` LAZILY and wrote the camera directly. That was safe only
    /// because `PlayerLoadout` guarantees exactly one of them is enabled, and it was already
    /// documented as a convention rather than a structure — with the explicit note that a
    /// THIRD consumer is the point to extract an owner.
    ///
    /// The throw heave is that third consumer, and it is the one that breaks the convention:
    /// carrying does NOT disable melee, so both would drive the FOV in the same frame. Worse,
    /// a lazily-captured base taken WHILE another effect is active adopts the offset value as
    /// "normal" — after which every bash is measured from the wrong number and the FOV
    /// ratchets. That failure is silent, permanent, and miserable to trace.
    ///
    /// FRAME-STAMPED REQUESTS, NOT LATCHED STATE. Call `AddOffset` every frame you want an
    /// offset; stop calling and the FOV eases home on its own. Same contract as
    /// `CameraKick.SetSustained`, and for the same reason — a driver that dies mid-effect
    /// (weapon swapped, prop destroyed, player killed) must not strand the camera zoomed.
    /// Offsets SUM, so a caller cannot silently stomp another's contribution.
    ///
    /// WORLD CAMERA ONLY. The viewmodel overlay keeps its own FOV, or the weapon in your hands
    /// would distort along with the world (§10).
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerFov : MonoBehaviour
    {
        [Tooltip("The WORLD camera. Left empty, it is taken from FirstPersonController.cam at Awake — never the viewmodel overlay camera.")]
        public Camera worldCamera;

        [Tooltip("Default degrees-per-second the FOV eases at. A caller may request faster; the fastest request in a frame wins, so a snappy bash is not slowed by a lazy zoom that happens to be active.")]
        public float defaultSpeed = 90f;

        /// <summary>The unmodified FOV every offset is measured from.</summary>
        public float BaseFov { get; private set; }

        float pendingOffset;
        float pendingSpeed;
        int pendingFrame = -1;

        /// <summary>
        /// Find the player's FOV owner, creating it if nobody has yet.
        ///
        /// EVERY CONSUMER MUST USE THIS rather than GetComponentInParent, because Awake order
        /// between sibling components is undefined: whichever of the bow, melee or carry ran
        /// first would find nothing and cache a null, and its effect would then silently never
        /// happen. A missing FOV owner does not throw — the effect just does not occur, which
        /// reads as "this setting does nothing" and costs an evening tuning a number that is
        /// not being used. That is exactly how it was found.
        ///
        /// Installed on the FirstPersonController's GameObject so all consumers, wherever they
        /// live in the rig, resolve to the same instance.
        /// </summary>
        public static PlayerFov Ensure(Component owner)
        {
            if (owner == null) return null;
            var found = owner.GetComponentInParent<PlayerFov>();
            if (found != null) return found;

            var controller = owner.GetComponentInParent<FirstPersonController>();
            GameObject host = controller != null ? controller.gameObject : owner.gameObject;
            return host.AddComponent<PlayerFov>();
        }

        void Awake()
        {
            if (worldCamera == null)
            {
                var c = GetComponent<FirstPersonController>();
                if (c != null && c.cam != null) worldCamera = c.cam.GetComponent<Camera>();
            }
            if (worldCamera == null) worldCamera = GetComponentInChildren<Camera>();

            // CAPTURED ONCE, EAGERLY. The whole point of owning this is that the base can
            // never be sampled while some effect is already displacing it.
            if (worldCamera != null) BaseFov = worldCamera.fieldOfView;
        }

        /// <summary>
        /// Request an FOV offset in degrees for THIS FRAME. Positive widens, negative narrows.
        /// Call it every frame the effect is active.
        /// </summary>
        public void AddOffset(float degrees, float speed = 0f)
        {
            if (pendingFrame != Time.frameCount)
            {
                pendingFrame = Time.frameCount;
                pendingOffset = 0f;
                pendingSpeed = 0f;
            }
            pendingOffset += degrees;
            pendingSpeed = Mathf.Max(pendingSpeed, speed);
        }

        void LateUpdate()
        {
            if (worldCamera == null) return;

            bool driven = pendingFrame == Time.frameCount;
            float target = BaseFov + (driven ? pendingOffset : 0f);
            float speed = driven && pendingSpeed > 0f ? pendingSpeed : defaultSpeed;

            worldCamera.fieldOfView = Mathf.MoveTowards(worldCamera.fieldOfView, target, speed * Time.deltaTime);
        }

        void OnDisable()
        {
            // Leave the camera as we found it rather than wherever an effect had it.
            if (worldCamera != null) worldCamera.fieldOfView = BaseFov;
        }
    }
}
