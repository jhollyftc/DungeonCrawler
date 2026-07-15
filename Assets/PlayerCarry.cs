using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Picks up, carries, and throws Carryable props.
    ///
    /// The carry is VELOCITY-DRIVEN, not a kinematic parent: the object stays a
    /// fully dynamic Rigidbody and is pulled toward a hold point in front of the
    /// eye each FixedUpdate. That means it still collides with everything — it
    /// bonks off door frames, knocks other props over, swings a physics door open
    /// on contact, and physically CANNOT be walked through a wall. A kinematic
    /// parent-carry would be simpler and rock-steady, but it would also let you
    /// stroll a barrel straight through the dungeon geometry, which is exactly
    /// the wrong instinct in a game whose doors you open by shouldering into them.
    ///
    /// Mass matters through ONE knob: maxCarryForce. A heavy crate can't be
    /// accelerated as hard, so it lags behind the hold point and swings wide
    /// around corners. Nothing special-cases weight — it falls out of the clamp.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class PlayerCarry : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("The eye. Left empty, it's found from the player's camera at Awake.")]
        public Transform cam;

        [Header("Hold")]
        [Tooltip("Vertical offset from the eye, so the object sits at chest height instead of dead centre of the screen.")]
        public float holdHeightOffset = -0.35f;
        [Tooltip("How hard the object chases the hold point. Higher = snappier and more glued; lower = floatier, laggier, more 'heavy'.")]
        public float followStrength = 12f;
        [Tooltip("Cap on how fast the carry can move an object (m/s). Stops a prop slingshotting when you whip the camera around.")]
        public float maxCarrySpeed = 8f;
        [Tooltip("Cap on the force used to move the held object. THIS is what makes mass matter while carrying: a heavy prop can't be accelerated as hard, so it lags and swings wide. Lower it to make everything feel heavier.")]
        public float maxCarryForce = 600f;

        [Header("Hold rotation")]
        [Tooltip("How hard the object is torqued back to the orientation it had when you grabbed it (in camera-yaw space, so turning carries it round with you but looking up/down doesn't tumble it).")]
        public float rotateStrength = 12f;
        [Tooltip("Cap on carry spin (rad/s).")]
        public float maxAngularSpeed = 10f;

        [Header("Encumbrance")]
        [Tooltip("Carried mass at or below which you move at full speed. A torch or a bucket shouldn't slow you; only real loads should.")]
        public float freeCarryMass = 5f;
        [Tooltip("Carried mass at which you're at your SLOWEST (minMoveSpeedMultiplier). Between this and freeCarryMass the slowdown scales linearly. Kept separate from the throw grunt's heavyMass so movement and voice can be tuned independently.")]
        public float heavyCarryMass = 30f;
        [Tooltip("Slowest the player can be dragged to while carrying, as a fraction of normal speed. Never 0 — hauling something heavy should be a slog, not a full stop.")]
        [Range(0.1f, 1f)] public float minMoveSpeedMultiplier = 0.45f;

        [Header("Release")]
        [Tooltip("Drop the object if it ends up this far from the hold point — i.e. it's wedged behind geometry or you backed into a corner. Without this, a prop can jam behind a wall and drag along with you forever.")]
        public float breakDistance = 2.5f;
        [Tooltip("Blocks an instant re-grab after dropping, so E doesn't drop-and-repick in one press.")]
        public float pickupCooldown = 0.25f;

        [Header("Input")]
        [Tooltip("Drops the held object. Shares the interact key: PlayerInteractor stands down while something is held, so E is unambiguous — grab when empty-handed, drop when full.")]
        public KeyCode dropKey = KeyCode.E;
        [Tooltip("0 = left mouse. Throws the held object.")]
        public int throwMouseButton = 0;

        [Header("Exertion (player voice)")]
        [Tooltip("The player's voice. Left empty, a 2D source is added at Awake — the player's own grunt shouldn't attenuate with distance from itself.")]
        public AudioSource voiceSource;
        [Tooltip("Grunt on throwing. Several = free variation, so hurling three barrels doesn't sound like a stuck record.")]
        public AudioClip[] throwClips;
        [Range(0f, 1f)] public float voiceVolume = 0.8f;
        [Tooltip("Random pitch spread on top of the effort pitch below.")]
        public Vector2 voicePitchRange = new Vector2(0.96f, 1.04f);
        [Tooltip("Rigidbody mass that counts as a MAXIMUM-effort throw. Heaving something this heavy pitches the grunt all the way down.")]
        public float heavyMass = 30f;
        [Tooltip("Grunt pitch for a weightless throw → for a heavyMass one. Heavier pitches DOWN: the same clip reads as strain rather than as a different voice, so one grunt covers the whole weight range.")]
        public Vector2 effortPitchRange = new Vector2(1.08f, 0.88f);

        Carryable held;
        Rigidbody heldBody;
        Collider[] heldColliders;
        Quaternion holdLocalRotation;
        bool heldUsedGravity;
        int pickupFrame = -1;
        float nextPickupTime;

        CharacterController controller;
        ViewmodelCamera viewmodel;

        public bool IsCarrying => held != null;

        /// <summary>
        /// Move-speed scale from what you're carrying (1 = unencumbered). The held
        /// object's MASS drives it, so the same number that makes a crate lag in
        /// your hands and thud when thrown also drags your feet — weight has one
        /// meaning across the whole system. FirstPersonController multiplies its
        /// speed by this.
        /// </summary>
        public float CarrySpeedMultiplier
        {
            get
            {
                if (!IsCarrying || heldBody == null) return 1f;
                float t = Mathf.InverseLerp(freeCarryMass, heavyCarryMass, heldBody.mass);
                return Mathf.Lerp(1f, minMoveSpeedMultiplier, t);
            }
        }

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            viewmodel = GetComponentInChildren<ViewmodelCamera>(true);

            if (cam == null)
            {
                Camera c = GetComponentInChildren<Camera>();
                if (c != null) cam = c.transform;
            }
            if (cam == null)
                Debug.LogError("[PlayerCarry] No camera found on the player — carrying needs an eye to hold things in front of.", this);

            if (voiceSource == null)
            {
                voiceSource = gameObject.AddComponent<AudioSource>();
                voiceSource.spatialBlend = 0f;   // 2D: you don't attenuate from yourself
            }
            voiceSource.playOnAwake = false;
        }

        // ---------------- Pickup / release ----------------

        public bool TryPickUp(Carryable target)
        {
            if (target == null || cam == null) return false;
            if (IsCarrying || Time.time < nextPickupTime) return false;

            Rigidbody body = target.Body;
            if (body == null || body.isKinematic) return false;

            held = target;
            heldBody = body;
            heldColliders = target.GetComponentsInChildren<Collider>();
            pickupFrame = Time.frameCount;

            // The object floats a metre in front of the capsule, so without this
            // the player is permanently walking into what they're holding:
            // CharacterControllerPhysicsPush shoves it away while the carry force
            // drags it back, and the two fight every frame. Note this does NOT
            // affect QUERIES — the interactor's SphereCast still hits the held
            // prop, which is why PlayerInteractor suppresses itself separately.
            foreach (Collider c in heldColliders)
                if (c != null && !c.isTrigger) Physics.IgnoreCollision(controller, c, true);

            // Steady hold. Heavy props express their weight by LAGGING (via
            // maxCarryForce), not by sagging — sag looks like a bug, drag reads
            // as weight.
            heldUsedGravity = body.useGravity;
            body.useGravity = false;

            // A thrown prop moves fast enough to tunnel a 3m wall in one step.
            // Left continuous after release on purpose: it's the state we want
            // it in during flight, and the cost is nothing for the handful of
            // props a player ever touches.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Remember how it was oriented relative to where we were LOOKING, so
            // turning carries it round with us and it settles back after a knock.
            holdLocalRotation = Quaternion.Inverse(CameraYaw()) * body.rotation;

            // Hands are full.
            if (viewmodel != null) viewmodel.SetViewmodelVisible(false);
            return true;
        }

        public void Drop() => Release();

        public void Throw()
        {
            Carryable thrown = held;
            Rigidbody body = heldBody;
            Transform eye = cam;

            // Mass has to be read BEFORE Release() lets go of the body.
            float mass = body != null ? body.mass : 0f;

            Release();

            if (thrown == null || body == null || eye == null) return;

            // Launch SPEED is authored per prop, not derived from mass — see the
            // note on Carryable.throwSpeed.
            body.linearVelocity = eye.forward * thrown.throwSpeed;
            body.angularVelocity = eye.right * thrown.throwSpin;

            PlayExertion(mass);
        }

        /// <summary>
        /// The grunt. Mass drives its PITCH, which is where the weight of a throw
        /// is actually sold: launch speed is authored per prop and deliberately
        /// ignores mass, so without this a barrel and a bucket would leave your
        /// hands identically. Pitching the same clip down under load reads as
        /// strain rather than as a different voice — one grunt covers the range.
        /// </summary>
        void PlayExertion(float mass)
        {
            if (voiceSource == null || throwClips == null || throwClips.Length == 0) return;

            AudioClip clip = throwClips[Random.Range(0, throwClips.Length)];
            if (clip == null) return;

            float effort = Mathf.Clamp01(mass / Mathf.Max(0.01f, heavyMass));
            voiceSource.pitch = Mathf.Lerp(effortPitchRange.x, effortPitchRange.y, effort)
                                * Random.Range(voicePitchRange.x, voicePitchRange.y);
            voiceSource.PlayOneShot(clip, voiceVolume);
        }

        void Release()
        {
            if (heldBody != null)
            {
                heldBody.useGravity = heldUsedGravity;

                if (heldColliders != null)
                    foreach (Collider c in heldColliders)
                        if (c != null && !c.isTrigger) Physics.IgnoreCollision(controller, c, false);
            }

            held = null;
            heldBody = null;
            heldColliders = null;
            nextPickupTime = Time.time + pickupCooldown;

            if (viewmodel != null) viewmodel.SetViewmodelVisible(true);
        }

        // Regenerating the dungeon or respawning while holding a prop would
        // otherwise strand it: gravity off, floating, ignoring the player.
        void OnDisable()
        {
            if (IsCarrying) Release();
        }

        // ---------------- Carry ----------------

        void Update()
        {
            if (!IsCarrying) return;

            // The interactor fires Interact() from ITS Update, which may run before
            // ours — so on the frame we grab something, the same E keypress is still
            // down and would drop it instantly.
            if (Time.frameCount == pickupFrame) return;

            if (Input.GetMouseButtonDown(throwMouseButton)) { Throw(); return; }
            if (Input.GetKeyDown(dropKey)) { Drop(); return; }
        }

        void FixedUpdate()
        {
            if (!IsCarrying) return;
            if (heldBody == null) { Release(); return; }   // destroyed under us

            Vector3 target = HoldPoint();

            // Target the centre of mass, not the origin, or the object levers itself
            // into a spin trying to put its pivot where its middle should be.
            Vector3 toTarget = target - heldBody.worldCenterOfMass;

            // Wedged behind geometry, or we walked off and left it. Let go.
            if (toTarget.magnitude > breakDistance) { Drop(); return; }

            float dt = Time.fixedDeltaTime;

            Vector3 desiredVelocity = Vector3.ClampMagnitude(toTarget * followStrength, maxCarrySpeed);
            Vector3 force = (desiredVelocity - heldBody.linearVelocity) / dt * heldBody.mass;
            heldBody.AddForce(Vector3.ClampMagnitude(force, maxCarryForce));

            DriveRotation();
        }

        void DriveRotation()
        {
            Quaternion targetRotation = CameraYaw() * holdLocalRotation;
            Quaternion delta = targetRotation * Quaternion.Inverse(heldBody.rotation);
            delta.ToAngleAxis(out float degrees, out Vector3 axis);
            if (degrees > 180f) degrees -= 360f;

            // ToAngleAxis hands back NaN degrees and an infinite axis for a delta
            // that's ~identity. PhysicsDoor was poisoned by exactly this.
            bool usable = !float.IsNaN(degrees)
                          && !float.IsInfinity(axis.x)
                          && Mathf.Abs(degrees) > 0.01f;

            heldBody.angularVelocity = usable
                ? Vector3.ClampMagnitude(axis.normalized * (degrees * Mathf.Deg2Rad * rotateStrength), maxAngularSpeed)
                : Vector3.zero;
        }

        Vector3 HoldPoint()
        {
            // cam.forward carries pitch on purpose: look down and the object comes
            // down with you, which is what sells "I'm holding this."
            return cam.position + cam.forward * held.holdDistance + Vector3.up * holdHeightOffset;
        }

        Quaternion CameraYaw() => Quaternion.Euler(0f, cam.eulerAngles.y, 0f);
    }
}
