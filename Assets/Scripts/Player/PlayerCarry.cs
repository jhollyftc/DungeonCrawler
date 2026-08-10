using System.Collections;
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
        [Tooltip("Slowest mouse turn rate while at full load, as a fraction of normal. A heavy load makes you swing the view around like you're leaning into it. Never 0 — you must still be able to look around.")]
        [Range(0.1f, 1f)] public float minTurnRateMultiplier = 0.6f;

        [Header("Release")]
        [Tooltip("Drop the object if it ends up this far from the hold point — i.e. it's wedged behind geometry or you backed into a corner. Without this, a prop can jam behind a wall and drag along with you forever.")]
        public float breakDistance = 2.5f;
        [Tooltip("Blocks an instant re-grab after dropping, so E doesn't drop-and-repick in one press.")]
        public float pickupCooldown = 0.25f;
        [Tooltip("Seconds the shield/sword stay STOWED after a THROW (not a drop) before reappearing. The viewmodel is a whole GameObject SetActive(false) while carrying, colliders included — reactivating it the instant you throw pops the shield/sword collider back into existence right where the freshly-launched prop is passing through, so it visibly 'hits' your own shield a frame after leaving your hand. This delay gives the prop time to clear the viewmodel first. 0 = old instant behaviour.")]
        public float viewmodelReturnDelay = 0.15f;

        [Header("Throw wind-up (the heave)")]
        [Tooltip("Seconds spent winding up before the prop leaves your hands, for a WEIGHTLESS prop. Short — this is a delay on your input, and beyond ~0.2s a light throw starts feeling unresponsive rather than physical.")]
        public float windupLight = 0.09f;
        [Tooltip("Wind-up seconds at full CarryLoad01. Heaving a table should visibly take effort and TIME; this is most of what separates hurling a skull from heaving a crate.")]
        public float windupHeavy = 0.32f;

        [Tooltip("How far the hold point is pulled BACK (m) during the wind-up, at full load. The prop is force-driven toward the hold point and clamped by maxCarryForce, so a heavy prop LAGS behind this — the weight reads for free out of the carry rig rather than needing its own animation.")]
        public float windupPullBack = 0.45f;
        [Tooltip("How far the hold point drops (m) during the wind-up, at full load. This is the 'bends down' half.")]
        public float windupDrop = 0.35f;

        [Tooltip("Camera pitch (deg) as you load the throw, at full load. POSITIVE looks down (bending over the load); NEGATIVE looks up (rocking back before the heave) — both read as a wind-up, so pick by taste. This is a HELD pose driven every frame, not a punch, so it can go well beyond the impulse cap.")]
        public float windupCameraPitch = -7f;
        [Tooltip("How far the camera eases BACK (m) during the wind-up, at full load. The weight-shift onto the back foot, and the part that most sells 'leaned back' — rotation alone reads as looking up rather than as loading.")]
        public float windupCameraLean = 0.09f;
        [Tooltip("How far the camera drops (m) during the wind-up, at full load — the knees bending under the load.")]
        public float windupCameraDrop = 0.05f;
        [Tooltip("Camera pitch (deg) UP on release, at full load: the snap of the body coming up and through the throw.")]
        public float releaseCameraPitch = 3.4f;
        [Tooltip("Forward camera jolt (m) on release, at full load. The lunge.")]
        public float releaseCameraPunch = 0.03f;

        [Tooltip("Degrees the WORLD FOV WIDENS at full wind-up. Buys drama with none of the clipping risk a big camera dolly carries: the camera stays inside the player's capsule, so it cannot end up inside a wall behind you. Same sign as PlayerMelee's bashFovBump (a lunge wants the world rushing at you) and the opposite of PlayerBow's aim zoom. 0 = off.")]
        public float windupFovWiden = 6f;
        [Tooltip("FOV change speed (deg/sec). Faster than the wind-up itself reads as a snap; slower reads as the world falling away as you load.")]
        public float windupFovSpeed = 60f;

        [Tooltip("Forward shove given to the PLAYER on release, at full load (m/s). A real heave moves you. Small on purpose — this goes through the same decaying external velocity the shield bash uses, so it can carry you off a ledge if overdone. 0 disables it.")]
        public float releaseSelfImpulse = 1.1f;

        [Header("Input")]
        [Tooltip("Drops the held object. Shares the interact key: PlayerInteractor stands down while something is held, so E is unambiguous — grab when empty-handed, drop when full.")]
        public KeyCode dropKey = KeyCode.E;
        [Tooltip("0 = left mouse. Throws the held object.")]
        public int throwMouseButton = 0;

        [Header("Exertion (player voice)")]
        [Tooltip("The player's voice. Left empty, a 2D source is added at Awake — the player's own grunt shouldn't attenuate with distance from itself.")]
        public AudioSource voiceSource;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;
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
        /// <summary>What's in hand, or null. Exposed so something being destroyed can
        /// check whether IT is the held prop and make the player let go first —
        /// otherwise the carry keeps gripping a destroyed object (DestructibleProp).</summary>
        public Carryable Held => held;

        /// <summary>
        /// How loaded the player is, 0 (empty-handed or a trivial load) to 1 (at or
        /// past heavyCarryMass). The single weight signal every "carrying is heavy"
        /// system reads from the held MASS — speed penalty, head bob, anything
        /// later — so they can never disagree about how heavy the load is.
        /// </summary>
        public float CarryLoad01
        {
            get
            {
                if (!IsCarrying || heldBody == null) return 0f;
                return Mathf.Clamp01(Mathf.InverseLerp(freeCarryMass, heavyCarryMass, heldBody.mass));
            }
        }

        /// <summary>
        /// Move-speed scale from what you're carrying (1 = unencumbered). The held
        /// object's MASS drives it, so the same number that makes a crate lag in
        /// your hands and thud when thrown also drags your feet — weight has one
        /// meaning across the whole system. FirstPersonController multiplies its
        /// speed by this.
        /// </summary>
        public float CarrySpeedMultiplier => Mathf.Lerp(1f, minMoveSpeedMultiplier, CarryLoad01);

        /// <summary>
        /// Mouse turn-rate scale from what you're carrying (1 = free). Same mass
        /// signal as the move-speed penalty, so a heavy load slows your look and
        /// your walk together — the whole body reads as loaded, not just the legs.
        /// FirstPersonController multiplies its look sensitivity by this.
        /// </summary>
        public float CarryTurnMultiplier => Mathf.Lerp(1f, minTurnRateMultiplier, CarryLoad01);

        CameraKick cameraKick;
        FirstPersonController moveController;
        PlayerFov fovDirector;

        void Awake()
        {
            controller = GetComponent<CharacterController>();
            viewmodel = GetComponentInChildren<ViewmodelCamera>(true);
            moveController = GetComponent<FirstPersonController>();
            // On the CAMERA, beside HeadBob — CameraKick strips its own offset before
            // reapplying, so driving it is what keeps the throw from becoming a fourth writer
            // fighting the controller's crouch and the bob for one transform.
            cameraKick = GetComponentInChildren<CameraKick>(true);

            // AUTO-INSTALLED rather than required on the prefab. Three components now ask for
            // this, and a missing one is SILENT — the effect simply never happens, which reads
            // as "the setting does nothing" and sends you tuning a number that was never being
            // used. Adding it costs nothing and removes a whole class of that.
            fovDirector = PlayerFov.Ensure(this);

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
            // AND STOP IT. playOnAwake only governs a FUTURE start - it cannot undo one already
            // underway, and the engine acts on the authored flag before this runs. A source set to
            // play on awake with NO CLIP enters a playing state that never completes, so it reports
            // isPlaying forever while making no sound: silent, invisible, and holding a voice slot.
            // Measured: 186 phantom voices against a real-voice budget of 32.
            voiceSource.Stop();
            AudioBus.Route(voiceSource, mixerGroup);
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

        /// <summary>True while the heave is loading and the prop has not left your hands.</summary>
        public bool IsWindingUp { get; private set; }
        float windupEndTime;
        float windupLoad;          // CarryLoad01 latched at the start of the heave

        /// <summary>
        /// Begin the throw. The prop does NOT leave on this frame.
        ///
        /// WHY A WIND-UP AT ALL. The viewmodel is stowed while carrying (hands full), so
        /// there are no arms to animate — the weight of a throw can only be told by the CAMERA
        /// and by the PROP. Launching instantly gave neither anything to say, and a barrel and
        /// a skull left the hands identically.
        ///
        /// The prop half is nearly free: the carry rig already drives the body toward
        /// HoldPoint() under a maxCarryForce clamp, so pulling that point back and down makes a
        /// heavy prop LAG into the wind-up and a light one snap to it. That is the same single
        /// mass signal (CarryLoad01) that already drives carry lag, move speed, turn rate and
        /// head-bob depth — set a prop's Rigidbody.mass and its whole heaviness moves together.
        ///
        /// The load is LATCHED here rather than read per frame: Release() clears the held prop
        /// at launch, so anything reading CarryLoad01 after that point would see zero and the
        /// release kick would come out weightless.
        /// </summary>
        public void Throw()
        {
            if (!IsCarrying || IsWindingUp) return;

            windupLoad = CarryLoad01;
            IsWindingUp = true;
            windupEndTime = Time.time + Mathf.Lerp(windupLight, windupHeavy, windupLoad);

            // The lean itself is HELD, and driven per frame from Update — a Kick here would
            // spring back to zero immediately and come out as a twitch no matter how large.
        }

        /// <summary>
        /// Hold the wind-up pose, ramping in over the heave.
        ///
        /// DRIVEN EVERY FRAME, not set once: `CameraKick.SetSustained` is frame-stamped and
        /// eases home the moment nothing asks for it. That is what makes an interrupted
        /// throw — the prop destroyed under you, the player killed mid-heave — leave the
        /// camera upright instead of stuck leaning, without a single explicit clear.
        ///
        /// The camera offset is applied in the CAMERA'S OWN LOCAL SPACE, so "back" means
        /// behind your eyes wherever you are looking, not a world axis.
        /// </summary>
        void DriveWindupLean()
        {
            if (cameraKick == null) return;

            float total = Mathf.Max(0.0001f, Mathf.Lerp(windupLight, windupHeavy, windupLoad));
            float t = Mathf.Clamp01(1f - (windupEndTime - Time.time) / total);
            float ease = Mathf.Sin(t * Mathf.PI * 0.5f);
            float amount = windupLoad * ease;

            cameraKick.SetSustained(
                new Vector3(windupCameraPitch * amount, 0f, 0f),
                new Vector3(0f, -windupCameraDrop * amount, -windupCameraLean * amount));

            // Requested per frame through the one FOV owner, so it composes with the bash
            // bump and the bow zoom instead of fighting them - carrying does NOT disable
            // melee, so this is the case that forced PlayerFov to exist.
            if (fovDirector != null && windupFovWiden != 0f)
                fovDirector.AddOffset(windupFovWiden * amount, windupFovSpeed);
        }

        /// <summary>
        /// The launch itself, once the wind-up has played out. Everything that was in Throw()
        /// before the heave existed.
        /// </summary>
        void ReleaseThrow()
        {
            Carryable thrown = held;
            Rigidbody body = heldBody;
            Transform eye = cam;

            // Mass has to be read BEFORE Release() lets go of the body. (The release kick uses
            // `windupLoad` instead, latched when the heave began — nothing after Release() may
            // read CarryLoad01, which reports 0 once the body is let go.)
            float mass = body != null ? body.mass : 0f;

            // Keep the shield/sword stowed a moment longer — see viewmodelReturnDelay.
            // A DROP still restores instantly (restoreViewmodel defaults true).
            Release(restoreViewmodel: false);

            if (thrown == null || body == null || eye == null)
            {
                if (viewmodel != null) viewmodel.SetViewmodelVisible(true);   // nothing actually launched
                return;
            }

            // Launch SPEED is authored per prop, not derived from mass — see the
            // note on Carryable.throwSpeed.
            body.linearVelocity = eye.forward * thrown.throwSpeed;
            body.angularVelocity = eye.right * thrown.throwSpin;

            // A prop is only a weapon when somebody MADE it one: arming is what
            // lets ThrownDamage hurt on this flight and never from casual shoves.
            body.GetComponent<ThrownDamage>()?.Arm(gameObject);

            // THE GRUNT BELONGS TO THE DRIVE, NOT THE COIL. You brace quietly through a
            // wind-up and vocalise on the exertion that follows it, so this fires with the
            // launch. Playing it when the heave STARTS puts the effort sound over the part
            // where the player is still loading, and the throw itself lands silent.
            PlayExertion(mass);

            // The snap through the throw: pitch UP and punch forward, opposite in sign to the
            // wind-up dip so the two read as one motion rather than two jolts.
            if (cameraKick != null)
                cameraKick.Kick(new Vector3(-releaseCameraPitch * windupLoad, 0f, 0f),
                                new Vector3(0f, 0f, releaseCameraPunch * windupLoad));

            // A real heave moves you. Routed through the controller's decaying external
            // velocity — the same channel the shield bash lunges with — so it composes into
            // the one cc.Move rather than fighting it, and so it cannot walk you through a
            // wall. Deliberately small: this is a weight cue, not a dash.
            // FORWARD, not recoil. Physically a heave shoves you back, but read from inside
            // the head that lands as being pushed rather than as throwing — the lunge is the
            // body committing through the throw, and that is what the motion has to say.
            // AddImpulse zeroes Y itself, so looking up cannot launch you.
            if (releaseSelfImpulse > 0f && moveController != null)
                moveController.AddImpulse(eye.forward * (releaseSelfImpulse * windupLoad));

            if (viewmodel != null)
            {
                if (viewmodelReturnDelay > 0f) StartCoroutine(RestoreViewmodelAfterThrow(viewmodelReturnDelay));
                else viewmodel.SetViewmodelVisible(true);
            }
        }

        /// <summary>
        /// Re-shows the shield/sword after the thrown prop has had time to clear their
        /// colliders. Checks IsCarrying before restoring — if a NEW pickup happened
        /// during the delay window, the viewmodel must stay stowed for THAT carry, not
        /// get yanked back into view by this now-stale timer.
        /// </summary>
        IEnumerator RestoreViewmodelAfterThrow(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (viewmodel != null && !IsCarrying) viewmodel.SetViewmodelVisible(true);
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

        void Release(bool restoreViewmodel = true)
        {
            // THE ONE CHOKE POINT for "no longer holding anything", so the wind-up flag is
            // cleared here rather than only at launch. It also covers the prop being destroyed
            // mid-heave and FixedUpdate's null guard calling Release() — otherwise IsWindingUp
            // stays true with nothing held, Update returns early forever, and the player can
            // never pick anything up again.
            IsWindingUp = false;

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

            if (restoreViewmodel && viewmodel != null) viewmodel.SetViewmodelVisible(true);
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

            // The heave is loading: no other action until the prop has left. Dropping or
            // re-throwing mid-wind-up would strand the camera mid-dip and let a mashed button
            // fire two launches from one pickup.
            if (IsWindingUp)
            {
                DriveWindupLean();
                if (Time.time >= windupEndTime) ReleaseThrow();
                return;
            }

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
            Vector3 p = cam.position + cam.forward * held.holdDistance + Vector3.up * holdHeightOffset;

            // THE HEAVE. Pull the hold point back and down while winding up; the carry rig
            // then does the animating, because the body is force-driven toward this point
            // under a maxCarryForce clamp. A skull snaps to it and a table lags heavily
            // behind it, so the same offset reads as two different weights with nothing
            // authored per prop.
            //
            // Eased in rather than stepped, or the prop jerks on the first wind-up frame and
            // reads as a glitch instead of a coil.
            if (IsWindingUp)
            {
                float total = Mathf.Max(0.0001f, Mathf.Lerp(windupLight, windupHeavy, windupLoad));
                float t = Mathf.Clamp01(1f - (windupEndTime - Time.time) / total);
                float ease = Mathf.Sin(t * Mathf.PI * 0.5f);   // fast out of rest, settling at full coil
                p -= cam.forward * (windupPullBack * windupLoad * ease);
                p -= Vector3.up * (windupDrop * windupLoad * ease);
            }
            return p;
        }

        Quaternion CameraYaw() => Quaternion.Euler(0f, cam.eulerAngles.y, 0f);
    }
}
