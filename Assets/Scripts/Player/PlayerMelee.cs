using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The player's melee: an LMB LIGHT COMBO (alternating directional swings) and an
    /// RMB HEAVY (hold to charge, fires on release). Every swing is a SwingDefinition
    /// — a procedural arc played THROUGH ViewmodelSway.SetAttackPose, so one system
    /// owns the viewmodel transform (rest → sway → attack → collision clamp) and the
    /// blade still can't swing through a wall. The sweep fires once at the swing's
    /// impact instant, aimed from the camera; whether it landed drives the feel layer
    /// (hitstop, camera kick) and OnImpact.
    ///
    /// Directional payoff: each combo swing has its own arc, so its blow direction
    /// (windup→slash delta) differs → a different NpcFlinch profile fires. A right
    /// cut shoves a goblin one way, a left cut the other, the overhead down.
    /// </summary>
    [RequireComponent(typeof(MeleeAttack))]
    [DisallowMultipleComponent]
    public class PlayerMelee : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("The SWORD hand's ViewmodelSway — the swing pose is injected through it.")]
        public ViewmodelSway swordSway;
        [Tooltip("The SHIELD hand's ViewmodelSway. Its motion is DERIVED from the sword's (counter-motion), so no swing needs shield poses authored.")]
        public ViewmodelSway shieldSway;

        [Header("Shield counter-motion (derived — no authoring per swing)")]
        [Tooltip("Per-axis multiplier turning the sword's POSITION offset into the shield's. NEGATIVE = counter-motion: swinging the sword one way twists the torso and throws the off-hand the other way (the body counterbalancing). Magnitude <1 because the passive arm moves less. X = lateral (strongest), Y = vertical, Z = forward/back.")]
        public Vector3 shieldCounterPosition = new Vector3(-0.45f, -0.20f, -0.30f);
        [Tooltip("Same, for ROTATION. Yaw/roll counter strongly (the torso twist); pitch less.")]
        public Vector3 shieldCounterEuler = new Vector3(-0.20f, -0.35f, -0.40f);
        [Tooltip("Seconds the shield TRAILS the sword. A small lag is what makes it read as the body following through instead of a rigid mirror. ~0.05-0.1.")]
        public float shieldLag = 0.06f;
        [Tooltip("How much of the sword's sway-suppression the shield inherits. Below 1 so the shield keeps some of its own idle sway during a swing — it isn't the hand doing the work.")]
        [Range(0f, 1f)] public float shieldSuppressScale = 0.6f;

        [Header("Sword counter-motion (derived — during a shield bash)")]
        [Tooltip("The MIRROR of the shield counter-motion: during a BASH, the shield thrusts and the SWORD hand is thrown the opposite way (the torso twist), so the bash reads as a whole-body lunge instead of a lone arm. Per-axis multiplier on the shield's position offset. NEGATIVE = counter.")]
        public Vector3 swordCounterPosition = new Vector3(-0.40f, -0.15f, -0.25f);
        [Tooltip("Same, for ROTATION.")]
        public Vector3 swordCounterEuler = new Vector3(-0.15f, -0.30f, -0.35f);
        [Tooltip("Seconds the sword TRAILS the shield during a bash (the follow-through lag).")]
        public float swordLag = 0.06f;
        [Tooltip("How much of the shield's sway-suppression the sword inherits during a bash.")]
        [Range(0f, 1f)] public float swordSuppressScale = 0.6f;

        [Header("Input")]
        [Tooltip("0 = left mouse (LIGHT combo).")]
        public int lightMouseButton = 0;
        [Tooltip("2 = MIDDLE mouse (HEAVY: hold to charge, release to swing). Moved off RMB, which PlayerGuard now uses to raise the shield — blocking earns the more reachable button because it is reactive and heavy attacks are not. NOTE: this field is SERIALIZED, so an existing Player prefab keeps whatever it was saved with; change it in the inspector, the code default only applies to new instances.")]
        public int heavyMouseButton = 2;
        [Tooltip("SHIELD BASH key. A dedicated off-hand key, kept off the mouse so it's independent of light/heavy and of the RMB block.")]
        public KeyCode bashKey = KeyCode.Q;

        [Header("Shield block pose (driven by PlayerGuard)")]
        [Tooltip("Shield hand offset while the guard is up. This component owns the shield transform — PlayerGuard deliberately holds only STATE, so the two can't fight over the pose (the same one-owner rule as ViewmodelCollision).")]
        public Vector3 blockShieldPosition = new Vector3(-0.06f, 0.05f, 0.18f);
        [Tooltip("Shield hand rotation (Euler) while the guard is up — brace it across the body.")]
        public Vector3 blockShieldEuler = new Vector3(-6f, 28f, -10f);
        [Tooltip("Sway suppression while blocking. High: a braced shield shouldn't bob with your stride.")]
        [Range(0f, 1f)] public float blockShieldSuppress = 0.85f;
        [Tooltip("Sword hand offset while blocking — pulled back and low, out of the way behind the shield.")]
        public Vector3 blockSwordPosition = new Vector3(0.05f, -0.06f, -0.12f);
        [Tooltip("Sword hand rotation (Euler) while blocking.")]
        public Vector3 blockSwordEuler = new Vector3(10f, -14f, 6f);
        [Tooltip("Sway suppression on the SWORD hand while blocking. Lower than the shield's — the sword is tucked out of the way, not braced, so letting it keep some bob reads as a hand at rest rather than a second locked prop.")]
        [Range(0f, 1f)] public float blockSwordSuppress = 0.5f;
        [Tooltip("PLAY-MODE POSE AUTHORING: hold the block pose regardless of input, so you can drag the fields above and watch the shield move live in the Game view — the same workflow as previewT for swings. Remember to turn it off; while it's on the guard pose overrides everything and you can't swing.")]
        public bool previewBlockPose = false;

        [Header("Block impact shake")]
        [Tooltip("How far (m) the shield is jolted by a blocked blow at blockShakeFullDamage. Small — this is a judder, not a swing.")]
        public float blockShakeDistance = 0.035f;
        [Tooltip("Rotational judder (deg) on the shield at full strength. A little goes a long way; the rotation is what reads as the shield RINGING rather than sliding.")]
        public float blockShakeAngle = 4f;
        [Tooltip("Absorbed damage that produces a full-strength shake. Below it the jolt scales down, so chip hits barely register and a heavy blow really rocks you.")]
        public float blockShakeFullDamage = 12f;
        [Tooltip("Oscillations per second. High enough to read as a vibration rather than a wobble — 25-40 is the useful band.")]
        public float blockShakeFrequency = 30f;
        [Tooltip("Seconds the shake takes to die away. Short: an impact rings briefly and stops.")]
        public float blockShakeDuration = 0.22f;
        [Tooltip("Multiplier on the shake for a PARRY. Parries deflect rather than absorb, so a sharper, smaller tick reads better than the heavy jolt of a block — and it keeps the two audibly and visibly distinct.")]
        public float parryShakeScale = 0.55f;
        [Tooltip("How fast the shield eases into and out of the block pose. Separate from shieldLag, which is tuned for the SWING counter-motion: a block should snap up faster than a shield drifting after a sword.")]
        public float blockPoseLag = 0.03f;

        [Header("Light combo (LMB) — cycles per tap")]
        [Tooltip("The light swings, played in order per LMB tap and wrapping. Give each a different arc so the directions vary.")]
        public List<SwingDefinition> lightCombo = new List<SwingDefinition>();
        [Tooltip("Seconds of no attack before the combo resets to the first swing.")]
        public float comboResetWindow = 1.5f;
        [Tooltip("An LMB press within this many seconds of the current swing ending is BUFFERED and fires next — mashing chains instead of dropping inputs.")]
        public float inputBuffer = 0.15f;

        [Header("Heavy (RMB) — hold to charge, release to swing")]
        public SwingDefinition heavySwing = new SwingDefinition
        {
            name = "Heavy Overhead", duration = 0.9f, windupEnd = 0.45f, impactT = 0.6f, slashEnd = 0.72f,
            damage = 28f, knockback = 9f, poiseDamage = 100f,
            localHitstop = 0.13f, recoilDistance = 0.06f, globalDipDuration = 0.08f, globalDipScale = 0.08f,
            hitKickEuler = new Vector3(3.5f, 0f, -1f), blowVerticalScale = 0.7f,
            windupPosition = new Vector3(0.02f, 0.16f, -0.14f), windupEuler = new Vector3(-60f, 5f, 5f),
            slashPosition = new Vector3(0f, -0.14f, 0.2f), slashEuler = new Vector3(70f, -5f, 0f),
        };
        [Tooltip("Move-speed multiplier while charging the heavy (commitment). 1 = no slow.")]
        [Range(0.1f, 1f)] public float chargeMoveScale = 0.6f;
        [Tooltip("How fast a CANCELLED charge rewinds to rest, as a multiple of the wind-up speed. Releasing RMB before the sword is FULLY wound aborts the swing and lowers it back — you must commit to the full draw to get the heavy.")]
        public float chargeReturnSpeed = 2f;

        [Header("Charge tremor (held tension)")]
        [Tooltip("Positional shake (m) while holding the fully-wound pose — the strain of holding a swing back. Small: a few mm.")]
        public float chargeTremorPosition = 0.004f;
        [Tooltip("Rotational shake (deg) while holding fully wound.")]
        public float chargeTremorEuler = 0.9f;
        [Tooltip("Tremor frequency (Hz). High = a tight buzz; low = a slow wobble.")]
        public float chargeTremorFrequency = 26f;
        [Tooltip("Seconds for the tremor to ramp in once fully wound, so it doesn't pop on.")]
        public float tremorRampTime = 0.2f;

        [Header("Shield bash (bashKey) — a forward shield thrust")]
        [Tooltip("The bash arc/feel/combat, authored like a swing but played on the SHIELD hand (windup = cock the shield back, slash = punch it forward). Low damage, high knockback, huge poise damage — a CONTROL tool that guarantees a poise break, not a damage dealer. Its recovery brings the shield straight home, so no hit-retract phase is needed.")]
        public SwingDefinition shieldBash = new SwingDefinition
        {
            name = "Shield Bash", duration = 0.45f, windupEnd = 0.3f, impactT = 0.42f, slashEnd = 0.55f, cooldown = 0.2f,
            damage = 5f, knockback = 12f, poiseDamage = 130f, range = 1.7f, sweepRadius = 0.5f,
            localHitstop = 0.08f, recoilDistance = 0.05f, globalDipDuration = 0.05f, globalDipScale = 0.1f,
            hitKickEuler = new Vector3(1.2f, 0.6f, 0.8f), swingKickEuler = new Vector3(-0.6f, 0f, 0f),
            blowForwardBias = 1f, blowVerticalScale = 0.1f,
            windupPosition = new Vector3(0.04f, 0.02f, -0.10f), windupEuler = new Vector3(-8f, -12f, 6f),
            slashPosition = new Vector3(0.02f, -0.02f, 0.28f), slashEuler = new Vector3(10f, 8f, -4f),
        };
        [Header("Bash lunge + cone (HOLD to wind, RELEASE to lunge-bash)")]
        [Tooltip("Forward lunge speed (m/s) added to the player on release. Decays fast (see FirstPersonController.externalDamping) into a short step, not a slide.")]
        public float bashDashSpeed = 6.5f;
        [Tooltip("Reach (m) of the bash's CONE shove — bigger than the sweep; it's a crowd-parting AoE, not a single strike.")]
        public float bashConeRange = 3f;
        [Tooltip("Half-angle (deg) of the cone in front. 55 ≈ a 110° fan.")]
        [Range(10f, 150f)] public float bashConeHalfAngle = 55f;
        [Tooltip("How radial the shove is. 0 = everyone straight back; 1 = everyone flung fully away along their own bearing (max fan-out). ~0.8 reads as 'back and to the side'.")]
        [Range(0f, 1f)] public float bashConeSideBias = 0.8f;
        [Tooltip("Seconds the bash cone stays LIVE after it fires, sweeping every frame as the lunge carries you forward. This is what makes it a CHARGE-THROUGH: enemies are caught (and react) at the moment you actually reach them, one after another down the charge path, instead of everyone popping at once from a single snapshot cone. Dedupe runs across the whole window, so nobody is hit twice. Match it roughly to how long the lunge actually travels (~the decay of bashDashSpeed under FirstPersonController.externalDamping); 0 = the old single-instant cone.")]
        public float bashSweepDuration = 0.35f;
        [Tooltip("Degrees the world FOV widens while winding the bash (the lunge tell), easing back on release. 0 = no FOV kick. The viewmodel overlay keeps its own FOV, so only the WORLD dollies.")]
        public float bashFovBump = 8f;
        [Tooltip("FOV change speed (deg/sec) in and out of the bump.")]
        public float bashFovSpeed = 60f;
        [Tooltip("Draw the bash CONE (reach + fan angle) as a gizmo when this component is selected — see exactly what the shove will catch.")]
        public bool drawBashCone = true;

        [Header("Bash PLOW — the 'windshield' carry (narrow cone INSIDE the bash cone)")]
        [Tooltip("Half-angle (deg) of the narrow cone whose victims get CARRIED along in front of the shield instead of being flung immediately. 0 = OFF (the bash behaves exactly as it did before the plow existed). ~25-30 reads as 'whoever is dead ahead'. Deliberately much narrower than bashConeHalfAngle: carrying someone standing 50 degrees off your centreline looks like telekinesis, and you'd lose the crowd-parting role the wide cone plays. With both live, one bash plows the people in front AND fans the flanks aside.")]
        [Range(0f, 90f)] public float bashPlowHalfAngle = 0f;
        [Tooltip("Reach (m) of the plow cone. Shorter than bashConeRange — this is who the shield actually reaches, not who feels the shockwave. MUST be <= bashConeRange to do anything: the plow is a sub-cone tested only on targets the wide cone already accepted, so anything beyond the wide reach is rejected before the plow is ever consulted.")]
        public float bashPlowRange = 1.6f;
        [Tooltip("Shove applied AT CAPTURE. Keep it near zero: the real knockback is deferred to the release, and a big value here just fights the carry it's starting.")]
        public float bashPlowCaptureImpulse = 0.5f;
        [Tooltip("Fraction of the player's live lunge velocity the carried NPC is driven at. 1 = matches you exactly. Slightly BELOW 1 lets you close on them a little as you charge, which reads as pressing them back rather than them floating ahead of you.")]
        [Range(0f, 1.5f)] public float bashPlowSpeedScale = 0.95f;
        [Tooltip("Corrective pull (per second) back toward the offset the NPC was captured at. This is a WEAK spring on top of the velocity match, not the main drive — it only stops them slowly sinking into you or drifting off. High values feel kinematic, like they're welded to the shield.")]
        public float bashPlowFollowStrength = 5f;
        [Tooltip("Release everyone once the lunge decays below this speed (m/s). Without it the carry outlives the charge and NPCs get nudged along by a lunge that's visually over.")]
        public float bashPlowReleaseSpeed = 1.5f;
        [Tooltip("Apply the release fling as a zero-damage DamageInfo instead of a raw impulse. ON reuses the whole NpcHitReactions routing, so the stagger still scales with the shove — at the cost of a second damage EVENT reaching hit audio/VFX (an extra grunt as they're flung, which usually sounds right). OFF drives NpcLocomotion.AddImpulse directly: no second event, but no stagger scaling either.")]
        public bool bashPlowReleaseThroughDamage = true;
        [Tooltip("Draw the narrow PLOW cone as a gizmo alongside the wide bash cone.")]
        public bool drawBashPlowCone = true;

        [Header("Bash PROP shove (crates, barrels, tables, doors)")]
        [Tooltip("IMPULSE (N·s) delivered to props in the shove cone. 0 = OFF. NOT the same quantity as shieldBash.knockback, which is a VELOCITY for NPCs — mass turns this into speed via dv = J/m, so one value covers every prop weight: light props launch, heavy ones barely shift. SIZE IT AS mass x desired launch speed. Props here are heavy (the barrel is 45kg), so the useful range is HUNDREDS, not single digits: 45kg x 7 m/s = ~315. It must also exceed bashDashSpeed x mass or the prop can't outrun your own lunge and you just keep colliding with it. Turn on MeleeAttack.debugAttack to log the resulting m/s per prop.")]
        public float bashPropImpulse = 320f;
        [Tooltip("Reach (m) of the PROP shove. Defaults to the plow's reach rather than the wide cone's, deliberately: the wide cone is a shockwave that staggers PEOPLE, and shoving objects several metres away reads as telekinesis. Widen if you want the crowd-clearing drama.")]
        public float bashPropRange = 1.8f;
        [Tooltip("Half-angle (deg) of the PROP shove cone.")]
        [Range(5f, 150f)] public float bashPropHalfAngle = 35f;
        [Tooltip("Draw the PROP shove cone as a gizmo.")]
        public bool drawBashPropCone = true;

        [Tooltip("Log every swing with its name and hit/whiff.")]
        public bool debugMelee = false;

        [Header("Authoring (play/edit mode)")]
        [Tooltip("Which swing the preview + gizmo target: -1 = the heavy, 0..N = lightCombo index.")]
        public int previewSwingIndex = 0;
        [Tooltip("PLAY-MODE POSE SCRUB: drag off 0 to freeze the previewed swing at that point of its arc, live in the Game view. Set back to 0 to release.")]
        [Range(0f, 1f)] public float previewT = 0f;

        /// <summary>Which kind of attack — lets audio/VFX pick per-attack clips.</summary>
        public enum AttackKind { Light, Heavy, Bash }

        /// <summary>A swing started (windup begins).</summary>
        public event Action OnSwingStarted;
        /// <summary>The blade/shield LAUNCHES (slash starts / bash fires) — the whoosh moment. Drives swing SFX.</summary>
        public event Action<AttackKind> OnAttackSwung;
        /// <summary>The impact frame fired; bool = did it damage anything. THE feel-layer hook.</summary>
        public event Action<bool> OnImpact;

        public bool IsSwinging => swinging;
        public bool IsCharging => charging;
        public bool IsBashing => bashing;

        MeleeAttack melee;
        PlayerGuard guard;      // optional — holds STATE only; this component still owns the shield pose
        PlayerCarry carry;
        FirstPersonController controller;
        CameraKick cameraKick;

        SwingDefinition active;   // the swing currently playing
        bool activeIsHeavy;       // the active swing is the heavy (for whoosh SFX)
        bool whooshFired;         // the slash-launch whoosh has fired this swing
        float t;                  // normalized swing time
        bool swinging;
        bool sweepDone;
        float readyAt;
        float freezeTimer;
        Vector3 recoilDir;
        bool retractQueued;       // this swing hit → retreat home after the freeze
        bool retracting;          // easing from the contact pose back to rest
        float retractProgress;
        Vector3 retractFromPos, retractFromEuler;
        float retractFromSuppress;

        int comboIndex;
        float comboResetAt;       // combo returns to 0 after this time
        bool bufferedLight;       // an LMB press queued during recovery

        bool charging;
        float chargeT;            // normalized swing time reached while charging (ramps to windupEnd, then holds)
        bool returning;           // an aborted charge rewinding to rest
        float tension;            // 0..1 tremor ramp once fully wound

        bool bashing;             // a shield bash is playing (owns the shield hand)
        bool bashCharging;        // holding bashKey, winding the shield back
        float bashChargeT;        // normalized time reached while winding (ramps to windupEnd, holds)
        float bt;                 // normalized bash time
        bool bashSweepDone;       // the charge-through sweep window has opened
        float bashSweepTimer;     // seconds left in that live window
        bool bashHitAnyone;       // someone was caught this bash (gates the once-per-bash feel beats)
        float bashFreeze;         // bash-hit local hitstop
        Vector3 bashRecoilDir;    // shield bounce-back direction on a bash hit
        Vector3 lastShieldPose, lastShieldEuler;   // last pose the bash wrote, so counter-motion resumes without a pop
        float lastShieldSuppress;

        /// <summary>One NPC stuck to the windshield for the duration of a bash lunge.</summary>
        struct Plowed
        {
            public IDamageable target;
            public NpcLocomotion loco;
            /// <summary>Where it sat relative to the player AT CAPTURE, in the player's own
            /// flat frame — stored player-LOCAL, not world, so turning the mouse mid-charge
            /// carries them around with you instead of leaving them behind.</summary>
            public Vector3 localOffset;
            /// <summary>The plow has driven this NPC for at least one frame. Until it has,
            /// its IsBlocked still describes its OWN approach from the previous frame (it
            /// may have been pressed against a wall walking at you), and honouring that
            /// would release it before the carry ever started.</summary>
            public bool driven;
        }
        readonly List<Plowed> plowed = new List<Plowed>();

        PlayerFov fov;            // the single owner of the WORLD camera FOV

        bool carriedLastFrame;
        // Latches the moment a throw consumes an LMB press, cleared only on full
        // release — NOT a one-frame flag like carriedLastFrame. Now that light
        // attacks respond to a HELD button (not just a fresh press), a one-frame
        // guard isn't enough: if the player is still physically holding LMB even a
        // moment after the throw-click, the hold gets picked up as a fresh light-
        // attack signal the very next frame, and the sword swings from the same
        // click that threw the prop. This lasts as long as that SAME continuous
        // hold does; a genuinely new press-after-release is unaffected.
        bool suppressLightUntilRelease;
        bool previewReleased;

        // Shield counter-motion: a target set by whatever pose the sword took this
        // frame (zero when idle), smoothed toward so the off-hand trails.
        Vector3 shieldTargetPos, shieldTargetEuler;
        float shieldTargetSuppress;
        Vector3 shieldPos, shieldEuler;
        float shieldSuppress;

        // Sword counter-motion during a bash — the mirror of the above.
        Vector3 swordTargetPos, swordTargetEuler;
        float swordTargetSuppress;
        Vector3 swordPos, swordEuler;
        Vector3 swordBlockPos, swordBlockEuler;   // the block layer's own smoothed sword pose
        float swordBlockSuppress;
        Vector3 blockShakeAxis = Vector3.back;    // blow direction in viewmodel space
        float blockShakeTime, blockShakeStrength;
        float swordSuppress;

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();
            guard = GetComponent<PlayerGuard>();
            carry = GetComponent<PlayerCarry>();
            controller = GetComponent<FirstPersonController>();
            cameraKick = GetComponentInChildren<CameraKick>(true);

            if (melee.aimSource == null)
            {
                if (controller != null && controller.cam != null) melee.aimSource = controller.cam;
                else { var c = GetComponentInChildren<Camera>(); if (c != null) melee.aimSource = c.transform; }
            }

            // The WORLD camera for the bash FOV kick (not the viewmodel overlay, which
            // keeps its own FOV so only the world dollies).
            fov = PlayerFov.Ensure(this);

            if (swordSway == null)
                Debug.LogWarning("[PlayerMelee] No sword ViewmodelSway assigned — the sweep works but the sword won't visibly swing.", this);
            if (lightCombo.Count == 0)
                Debug.LogWarning("[PlayerMelee] lightCombo is empty — add at least one SwingDefinition for LMB.", this);
        }

        void Update()
        {
            // A genuinely NEW press (after a full release) always clears the throw
            // guard, checked once here so every path below sees the same answer.
            if (Input.GetMouseButtonUp(lightMouseButton)) suppressLightUntilRelease = false;

            // Default the shield to rest each frame; whichever pose runs below sets
            // its target. Idle frames therefore ease it home on their own.
            shieldTargetPos = Vector3.zero;
            shieldTargetEuler = Vector3.zero;
            shieldTargetSuppress = 0f;

            if (swinging) { TickSwing(); FinishFrame(); return; }

            // A bash OWNS the shield hand — it writes shieldSway directly, so it must
            // NOT go through TickShield (which would fight it). FinishFrameBash skips it.
            if (bashing) { TickBash(); FinishFrameBash(); return; }

            // Preview scrub takes over when a swing isn't playing.
            if (previewT > 0f) { ApplyPose(PreviewSwing(), previewT); previewReleased = true; FinishFrame(); return; }
            if (previewReleased) { swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f); previewReleased = false; }

            if (returning) TickChargeReturn();   // a new charge/swing below cancels it

            // Shield bash: HOLD bashKey to wind, RELEASE to lunge-bash. While winding OR
            // the instant it fires, the bash owns the shield hand — skip the sword's
            // charge/light input and TickShield (FinishFrameBash) this frame.
            if (HandleBashCharge()) { FinishFrameBash(); return; }

            HandleCharge();
            HandleLightInput();

            // Combo decays back to the first swing after a lull.
            if (comboIndex != 0 && Time.time >= comboResetAt) comboIndex = 0;

            FinishFrame();
        }

        void FinishFrame()
        {
            TickShield();   // every path ends here, so the shield always eases toward its target
            TickFov();
            carriedLastFrame = carry != null && carry.IsCarrying;
            if (carriedLastFrame) suppressLightUntilRelease = true;
        }

        // ---------------- Input ----------------

        void HandleLightInput()
        {
            if (charging || swinging || lightCombo.Count == 0) return;
            // HELD, not just pressed: starts the first swing from idle same as a tap
            // always did, and is also the fallback that keeps a HELD button attacking
            // if a chain ever isn't caught by the buffer below (e.g. a longer cooldown
            // than inputBuffer). The buffer is what makes the common case seamless —
            // this is what guarantees holding LMB never just stops.
            if (Input.GetMouseButton(lightMouseButton) && !suppressLightUntilRelease && CanSwing())
                StartSwing(NextLight(), isHeavy: false);
        }

        void HandleCharge()
        {
            if (Input.GetMouseButtonDown(heavyMouseButton) && CanSwing())
                BeginCharge();

            if (!charging) return;

            if (Input.GetMouseButton(heavyMouseButton))
            {
                // WIND UP, then HOLD: chargeT walks the swing's own windup at its own
                // pace and stops at windupEnd (the fully coiled pose). Not a snap —
                // the sword visibly draws back and waits there, trembling.
                if (controller != null) controller.moveScaleOverride = chargeMoveScale;
                chargeT = Mathf.Min(chargeT + Time.deltaTime / Mathf.Max(0.05f, heavySwing.duration),
                                    heavySwing.windupEnd);

                bool wound = IsFullyWound;
                tension = wound
                    ? Mathf.Min(tension + Time.deltaTime / Mathf.Max(0.01f, tremorRampTime), 1f)
                    : 0f;

                ApplyChargePose(chargeT, tension);
                return;
            }

            // Released.
            if (controller != null) controller.moveScaleOverride = 1f;
            charging = false;
            tension = 0f;

            if (IsFullyWound && CanSwing())
            {
                // CONTINUE from where the charge held, straight into the slash.
                // (Starting at t=0 would snap back to rest and replay the windup.)
                StartSwing(heavySwing, isHeavy: true, startT: chargeT);
            }
            else
            {
                // Let go too early — the swing is ABORTED. Commitment to the full
                // draw is the cost of the heavy; a half-draw just lowers the sword.
                returning = true;
                if (debugMelee) Debug.Log("[PlayerMelee] heavy aborted — released before full wind-up.", this);
            }
        }

        void BeginCharge()
        {
            charging = true;
            returning = false;
            chargeT = 0f;
            tension = 0f;
        }

        /// <summary>The draw is complete — only then can a release become a swing.</summary>
        bool IsFullyWound => chargeT >= heavySwing.windupEnd - 0.0001f;

        /// <summary>
        /// The held charge pose plus a tremor once fully wound — three incommensurate
        /// sines per axis so the shake reads as strained muscle, not a loop. Ramped in
        /// by `tension` so it doesn't pop the instant the draw completes.
        /// </summary>
        void ApplyChargePose(float nt, float amount)
        {
            heavySwing.ComputePose(nt, out Vector3 pos, out Vector3 euler, out float suppress);

            if (amount > 0f && (chargeTremorPosition > 0f || chargeTremorEuler > 0f))
            {
                float tt = Time.time * chargeTremorFrequency;
                Vector3 jitterPos = new Vector3(Mathf.Sin(tt), Mathf.Sin(tt * 1.37f + 1.1f), Mathf.Sin(tt * 0.83f + 2.3f));
                Vector3 jitterRot = new Vector3(Mathf.Sin(tt * 1.19f), Mathf.Sin(tt * 0.91f + 0.7f), Mathf.Sin(tt * 1.53f + 1.9f));
                pos += jitterPos * (chargeTremorPosition * amount);
                euler += jitterRot * (chargeTremorEuler * amount);
            }

            // Derived: the shield braces as the sword draws back, and picks up a
            // sympathetic tremor from the held tension — both for free.
            SetHandPoses(pos, euler, suppress);
        }

        /// <summary>An aborted charge rewinds along its own arc back to rest.</summary>
        void TickChargeReturn()
        {
            chargeT -= Time.deltaTime / Mathf.Max(0.05f, heavySwing.duration) * Mathf.Max(0.1f, chargeReturnSpeed);
            if (chargeT <= 0f)
            {
                chargeT = 0f;
                returning = false;
                swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);
                return;
            }
            ApplyPose(heavySwing, chargeT);
        }

        SwingDefinition NextLight()
        {
            SwingDefinition s = lightCombo[Mathf.Clamp(comboIndex, 0, lightCombo.Count - 1)];
            comboIndex = (comboIndex + 1) % lightCombo.Count;
            comboResetAt = Time.time + comboResetWindow;
            return s;
        }

        bool CanSwing(bool ignoreCooldown = false)
        {
            if (!ignoreCooldown && Time.time < readyAt) return false;
            if (Cursor.lockState != CursorLockMode.Locked) return false;
            if (carry != null && carry.IsCarrying) return false;
            if (carriedLastFrame) return false;               // this click was the THROW
            if (guard != null && guard.IsGuarding) return false;   // arms are busy holding the shield
            // Preview holds the block pose by force. Attacking through it means SetHandPoses
            // writes the shield target from the swing every frame while TickShield overrides
            // it back, so the two fight over the shield for the whole swing.
            if (previewBlockPose) return false;
            return true;
        }

        // ---------------- Swing ----------------

        /// <summary>
        /// Begin a swing. `startT` lets a charged heavy resume from the windup pose
        /// it was held at instead of replaying the windup from rest.
        /// </summary>
        void StartSwing(SwingDefinition swing, bool isHeavy, float startT = 0f)
        {
            active = swing;
            activeIsHeavy = isHeavy;
            whooshFired = false;
            swinging = true;
            sweepDone = false;
            returning = false;    // a new swing overrides an aborted charge's rewind
            retractQueued = false;
            retracting = false;
            t = Mathf.Clamp01(startT);
            freezeTimer = 0f;
            bufferedLight = false;
            cameraKick?.Kick(swing.swingKickEuler);
            OnSwingStarted?.Invoke();
            // A charged heavy resumes AT the slash launch, so its whoosh fires now;
            // a normal swing fires it when t crosses windupEnd in TickSwing.
            if (t >= swing.windupEnd) { whooshFired = true; OnAttackSwung?.Invoke(isHeavy ? AttackKind.Heavy : AttackKind.Light); }
            if (debugMelee) Debug.Log($"[PlayerMelee] swing '{swing.name}'{(isHeavy ? " (HEAVY)" : "")}.", this);
        }

        void TickSwing()
        {
            // Queue the next light the moment the swing is spent — captured at the
            // TOP so a press during the freeze OR the retract counts too. On a hit the
            // clock stops at impact (t < slashEnd), so the normal "past slashEnd"
            // window never opens; the freeze/retract flags are the ending signal.
            // A fresh PRESS only (not held) — a tap buffered slightly early should
            // still fire even if released by the time the swing actually ends, that's
            // the whole point of buffering it. Continuing a HELD button is handled
            // separately, at the point of consumption (EndSwing), by checking whether
            // the button is STILL down right then — using "held" here too would latch
            // true the instant the window opens (which happens on basically every
            // frame while fighting) and then fire regardless of having since released,
            // sneaking in an extra attack after letting go.
            if (!charging && Input.GetMouseButtonDown(lightMouseButton))
            {
                bool endingWindow = retracting || freezeTimer > 0f
                    || t >= active.slashEnd - inputBuffer / Mathf.Max(0.05f, active.duration);
                if (endingWindow) bufferedLight = true;
            }

            // Caught in a body: the clock stops, the blade holds at the contact pose
            // with a recoil bounce. Unscaled, or the global dip stretches it. When the
            // freeze ends, a HIT hands off to the RETRACT (not the rest of the arc).
            if (freezeTimer > 0f)
            {
                freezeTimer -= Time.unscaledDeltaTime;
                float p = 1f - Mathf.Clamp01(freezeTimer / Mathf.Max(0.01f, active.localHitstop));
                float envelope = Mathf.Sin(p * Mathf.PI);
                active.ComputePose(t, out Vector3 fpos, out Vector3 feuler, out float fsup);
                SetHandPoses(fpos + recoilDir * (active.recoilDistance * envelope), feuler, fsup);

                if (freezeTimer <= 0f && retractQueued)
                {
                    // Retreat from the settled contact pose (envelope ~0 now) back
                    // to rest — the blade met resistance and doesn't follow through.
                    retractQueued = false;
                    retracting = true;
                    retractProgress = 0f;
                    retractFromPos = fpos;
                    retractFromEuler = feuler;
                    retractFromSuppress = fsup;
                }
                return;
            }

            // Hit retract: ease the contact pose home instead of completing the arc.
            if (retracting)
            {
                retractProgress += Time.deltaTime / Mathf.Max(0.02f, active.hitRetractTime);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(retractProgress));
                SetHandPoses(Vector3.Lerp(retractFromPos, Vector3.zero, k),
                             Vector3.Lerp(retractFromEuler, Vector3.zero, k),
                             Mathf.Lerp(retractFromSuppress, 0f, k));
                if (retractProgress >= 1f) EndSwing();
                return;
            }

            t += Time.deltaTime / Mathf.Max(0.05f, active.duration);

            // Whoosh at the slash launch — the moment the blade actually accelerates.
            if (!whooshFired && t >= active.windupEnd)
            {
                whooshFired = true;
                OnAttackSwung?.Invoke(activeIsHeavy ? AttackKind.Heavy : AttackKind.Light);
            }

            if (!sweepDone && t >= active.impactT)
            {
                sweepDone = true;
                DoImpact();
            }

            if (t >= 1f) { EndSwing(); return; }

            ApplyPose(active, t);
        }

        void DoImpact()
        {
            // Push this swing's combat values onto the shared sweep, aim the blow.
            melee.damage = active.damage;
            melee.knockback = active.knockback;
            melee.poiseDamage = active.poiseDamage;
            if (active.range > 0f) melee.range = active.range;
            if (active.sweepRadius > 0f) melee.sweepRadius = active.sweepRadius;
            melee.blowDirectionOverride = ComputeBlowDirection(active);

            bool hit = melee.DoSweep();
            if (debugMelee) Debug.Log($"[PlayerMelee] '{active.name}' impact — {(hit ? "HIT" : "whiff")}.", this);

            if (hit)
            {
                freezeTimer = active.localHitstop;
                retractQueued = true;   // after the freeze, retreat home (don't follow through)
                recoilDir = -(active.slashPosition - active.windupPosition).normalized;
                Hitstop.Request(active.globalDipDuration, active.globalDipScale);
                cameraKick?.Kick(active.hitKickEuler, new Vector3(0f, 0f, -0.012f));
            }

            OnImpact?.Invoke(hit);
        }

        void EndSwing()
        {
            swinging = false;
            swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);

            // Chain immediately and skip the cooldown — that's the whole point of
            // buffering, and setting readyAt first would block the very swing we just
            // queued (CanSwing would see Time.time < readyAt). Two DIFFERENT reasons to
            // chain, checked separately: bufferedLight is a tap that landed slightly
            // early (fires regardless of current hold state — don't punish a fast
            // tap-release for good timing); GetMouseButton is checked fresh RIGHT NOW
            // for a held button (NOT latched — a stale "was held sometime during the
            // window" flag would keep firing one swing after release, sneaking in an
            // extra attack the instant you let go).
            bool continueChain = bufferedLight || (Input.GetMouseButton(lightMouseButton) && !suppressLightUntilRelease);
            if (continueChain && lightCombo.Count > 0 && CanSwing(ignoreCooldown: true))
            {
                bufferedLight = false;
                StartSwing(NextLight(), isHeavy: false);
                return;
            }

            bufferedLight = false;
            readyAt = Time.time + active.cooldown;

            // Still HOLDING RMB as the swing ends → roll straight into the heavy
            // draw. HandleCharge can't see the press itself (it doesn't run while a
            // swing is playing, so the button-DOWN was consumed mid-swing); testing
            // "is it held now" catches press-and-hold during a light and picks up
            // seamlessly, the same way the light chain does.
            if (Input.GetMouseButton(heavyMouseButton) && CanSwing(ignoreCooldown: true))
                BeginCharge();
            // Same idea for a held bashKey: HandleBashCharge can't see the press either
            // (it doesn't run while a swing is playing), so "is it held now" catches a
            // press-and-hold during a light/heavy and starts the bash wind immediately.
            else if (Input.GetKey(bashKey) && CanBash(ignoreCooldown: true))
            {
                bashCharging = true;
                bashChargeT = 0f;
                swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);
            }
        }

        void ApplyPose(SwingDefinition swing, float nt)
        {
            swing.ComputePose(nt, out Vector3 pos, out Vector3 euler, out float suppress);
            SetHandPoses(pos, euler, suppress);
        }

        // ---------------- Shield bash ----------------

        bool CanBash(bool ignoreCooldown = false)
        {
            if (!ignoreCooldown && Time.time < readyAt) return false;   // shares the swing cooldown gate
            if (Cursor.lockState != CursorLockMode.Locked) return false;
            if (swinging || charging) return false;
            if (carry != null && carry.IsCarrying) return false;
            if (carriedLastFrame) return false;
            if (guard != null && guard.IsGuarding) return false;   // drop the guard before bashing with it
            if (previewBlockPose) return false;                   // see CanSwing
            if (shieldSway == null) return false;                 // nothing to thrust
            return true;
        }

        /// <summary>
        /// HOLD bashKey to wind the shield back (FOV creeps up as a lunge tell), RELEASE
        /// to fire the lunge-bash from wherever the wind reached. Returns true while it
        /// owns the shield hand this frame (winding, or the frame it fires). A forgiving
        /// charge — unlike the heavy, releasing early still bashes (it just wound less);
        /// the wind-up is a tell and an FOV ramp, not a gate.
        /// </summary>
        bool HandleBashCharge()
        {
            if (!bashCharging)
            {
                // Can't start mid heavy-charge (both hands committing); otherwise clean.
                if (charging || !Input.GetKeyDown(bashKey) || !CanBash()) return false;
                bashCharging = true;
                bashChargeT = 0f;
                swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);   // sword rests; shield does the work
            }

            if (Input.GetKey(bashKey))
            {
                // Wind toward the coiled shield pose and hold there, trembling FOV aside.
                bashChargeT = Mathf.Min(bashChargeT + Time.deltaTime / Mathf.Max(0.05f, shieldBash.duration),
                                        shieldBash.windupEnd);
                shieldBash.ComputePose(bashChargeT, out Vector3 pos, out Vector3 euler, out float sup);
                ApplyShieldPose(pos, euler, sup);
                return true;
            }

            // Released → lunge and bash from the held wind-up.
            bashCharging = false;
            StartBash(bashChargeT);
            ApplyBashDash();
            TickBash();   // advance one frame immediately so the release reads instant
            return true;
        }

        /// <summary>Add the forward lunge — flat player-forward, decayed by the controller into a short step.</summary>
        void ApplyBashDash()
        {
            if (controller == null || bashDashSpeed <= 0f) return;
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 1e-4f)
                controller.AddImpulse(forward.normalized * bashDashSpeed);
        }

        /// <summary>Begin the bash play phase from the wound-up time `startT` (the slash continues from there).</summary>
        void StartBash(float startT)
        {
            bashing = true;
            bt = Mathf.Clamp01(startT);
            bashSweepDone = false;
            bashSweepTimer = 0f;
            bashHitAnyone = false;
            bashFreeze = 0f;
            plowed.Clear();   // nobody carries over from a previous bash
            returning = false;
            bufferedLight = false;
            cameraKick?.Kick(shieldBash.swingKickEuler);
            OnAttackSwung?.Invoke(AttackKind.Bash);   // release = the thrust launches
            if (debugMelee) Debug.Log("[PlayerMelee] shield bash (lunge).", this);
        }

        void TickBash()
        {
            // Same buffering as the sword (TickSwing): a fresh PRESS (not held — see
            // TickSwing's comment for why) near the end of the bash queues a light
            // attack that fires the instant the bash finishes, skipping the cooldown.
            // Continuing a HELD button is handled separately at EndBash.
            if (Input.GetMouseButtonDown(lightMouseButton))
            {
                bool endingWindow = bashFreeze > 0f
                    || bt >= 1f - inputBuffer / Mathf.Max(0.05f, shieldBash.duration);
                if (endingWindow) bufferedLight = true;
            }

            // Caught in a body: hold the thrust with a recoil bounce (unscaled, like the
            // sword's freeze). A thrust recovers straight back along its own axis, so no
            // separate retract phase is needed — just resume the arc when the freeze ends.
            if (bashFreeze > 0f)
            {
                bashFreeze -= Time.unscaledDeltaTime;
                float p = 1f - Mathf.Clamp01(bashFreeze / Mathf.Max(0.01f, shieldBash.localHitstop));
                float envelope = Mathf.Sin(p * Mathf.PI);
                shieldBash.ComputePose(bt, out Vector3 fpos, out Vector3 feuler, out float fsup);
                ApplyShieldPose(fpos + bashRecoilDir * (shieldBash.recoilDistance * envelope), feuler, fsup);
                // The carry must keep ticking through the freeze. The plow expires on a
                // timeout (NpcLocomotion.SetPlowVelocity), so skipping it here would drop
                // everyone mid-charge at the exact moment of first contact — which is when
                // the freeze fires.
                TickPlow();
                return;
            }

            bt += Time.deltaTime / Mathf.Max(0.05f, shieldBash.duration);

            if (!bashSweepDone && bt >= shieldBash.impactT)
            {
                bashSweepDone = true;
                bashSweepTimer = bashSweepDuration;
                DoBashImpact(firstFrame: true);
            }
            else if (bashSweepTimer > 0f)
            {
                // The cone rides along with the lunge: swept again every frame from the
                // player's CURRENT position, so each enemy is caught as the charge
                // actually reaches them. Dedupe carries across the window (continueSweep).
                bashSweepTimer -= Time.deltaTime;
                DoBashImpact(firstFrame: false);
            }

            // Carry runs every frame the bash is alive, INCLUDING during bashFreeze above —
            // which returns early, so TickPlow is called there too (see that block).
            TickPlow();

            if (bt >= 1f) { EndBash(); return; }

            shieldBash.ComputePose(bt, out Vector3 pos, out Vector3 euler, out float suppress);
            ApplyShieldPose(pos, euler, suppress);
        }

        /// <summary>
        /// One frame of the bash cone. Called at the impact instant and then every frame
        /// for bashSweepDuration while the lunge carries the player forward, so the shove
        /// rolls down the charge path instead of firing once from a standing snapshot.
        /// `firstFrame` opens a fresh dedupe set; later frames continue it, so each
        /// enemy is caught exactly once — whenever the charge actually reaches them.
        /// </summary>
        /// <summary>
        /// A cone victim landed in the narrow inner cone — stick it to the windshield.
        /// Its damage/flinch/grunt already fired inside the sweep (the shield DID connect);
        /// what's deferred is the shove.
        /// </summary>
        void CapturePlowed(IDamageable target, Vector3 blow)
        {
            if (target == null || bashPlowHalfAngle <= 0f) return;

            Transform t = target.Transform;
            var loco = t != null ? t.GetComponent<NpcLocomotion>() : null;
            // No locomotion, no carry. The plow drives NPCs through their CharacterController;
            // anything else (a destructible prop, a future turret) just took the hit and
            // keeps whatever knockback it was given.
            if (loco == null) return;

            Vector3 offset = t.position - transform.position;
            offset.y = 0f;
            plowed.Add(new Plowed
            {
                target = target,
                loco = loco,
                localOffset = transform.InverseTransformDirection(offset),
            });

            if (debugMelee) Debug.Log($"[PlayerMelee] plow CAPTURED '{t.name}'.", this);
        }

        /// <summary>
        /// Drive every carried NPC for one frame, and release the ones that should come off.
        /// Velocity-matched to the player's live lunge with a weak corrective pull toward the
        /// capture offset — the same philosophy as PlayerCarry's hold point: mostly physical,
        /// lightly corrected, never a rigid constraint. A hard positional weld would look
        /// kinematic and break the instant one of them was blocked.
        /// </summary>
        void TickPlow()
        {
            if (plowed.Count == 0) return;

            Vector3 dash = controller != null ? controller.ExternalVelocity : Vector3.zero;
            dash.y = 0f;

            // The lunge is spent — the charge is visually over, so stop carrying rather than
            // nudging bodies along behind a dash that's finished.
            if (dash.magnitude < bashPlowReleaseSpeed) { ReleasePlowed(); return; }

            for (int i = plowed.Count - 1; i >= 0; i--)
            {
                Plowed p = plowed[i];

                // Died or was destroyed mid-carry: drop it with no fling. A corpse is being
                // handed to the ragdoll and shouldn't also be shoved.
                if (p.loco == null || !p.loco.isActiveAndEnabled || p.target == null || p.target.IsDead)
                {
                    if (p.loco != null) p.loco.ClearPlow();
                    plowed.RemoveAt(i);
                    continue;
                }

                // Pinned against geometry. MUST release: the player doesn't collide with
                // NPCs, so continuing to drive a body that can't move means the player walks
                // straight through it and it ends up BEHIND the shield. Only trusted once
                // the plow has actually driven it for a frame — see Plowed.driven.
                if (p.driven && p.loco.IsBlocked)
                {
                    ReleaseOne(p);
                    plowed.RemoveAt(i);
                    continue;
                }

                // Where it should be: the capture offset, rebuilt in the player's CURRENT
                // frame so turning the mouse swings the carried bodies around with you.
                Vector3 wantPos = transform.position + transform.TransformDirection(p.localOffset);
                Vector3 error = wantPos - p.loco.transform.position;
                error.y = 0f;

                p.loco.SetPlowVelocity(dash * bashPlowSpeedScale + error * bashPlowFollowStrength);

                // Plowed is a STRUCT in a List, so `p` is a copy — the flag has to be
                // written back or it never sticks and the blocked check stays disabled.
                p.driven = true;
                plowed[i] = p;
            }
        }

        /// <summary>Fling one carried NPC and let it go.</summary>
        void ReleaseOne(Plowed p)
        {
            if (p.loco == null || p.target == null) return;
            p.loco.ClearPlow();
            if (p.target.IsDead) return;

            // Blow recomputed from where the body is NOW, not where it was captured — it has
            // been carried some distance since, and a fling should go where it currently is.
            // Same flatDir/bearing/sideBias construction the cone uses, so the plow release
            // and the wide cone fan the crowd the same way.
            Vector3 flatDir = transform.forward;
            flatDir.y = 0f;
            flatDir = flatDir.sqrMagnitude > 1e-4f ? flatDir.normalized : transform.forward;

            Vector3 to = p.loco.transform.position - transform.position;
            to.y = 0f;
            Vector3 toDir = to.sqrMagnitude > 1e-6f ? to.normalized : flatDir;
            Vector3 blow = Vector3.Slerp(flatDir, toDir, Mathf.Clamp01(bashConeSideBias)).normalized;

            if (bashPlowReleaseThroughDamage)
            {
                // Zero damage, real impulse: the point is to reuse NpcHitReactions' routing
                // so the stagger still scales with the shove. The cost is a second damage
                // EVENT (an extra grunt as they're flung) — see the field tooltip.
                p.target.TakeDamage(new DamageInfo
                {
                    amount = 0f,
                    point = p.loco.transform.position,
                    direction = blow,
                    instigator = gameObject,
                    type = DamageType.Melee,
                    impulse = shieldBash.knockback,
                    poiseDamage = 0f,   // poise already broke at capture
                });
            }
            else
            {
                p.loco.AddImpulse(blow * shieldBash.knockback);
            }

            if (debugMelee) Debug.Log($"[PlayerMelee] plow RELEASED '{p.loco.name}'.", this);
        }

        /// <summary>Fling everyone still on the windshield and clear the set.</summary>
        void ReleasePlowed()
        {
            for (int i = 0; i < plowed.Count; i++) ReleaseOne(plowed[i]);
            plowed.Clear();
        }

        void DoBashImpact(bool firstFrame)
        {
            melee.damage = shieldBash.damage;
            melee.knockback = shieldBash.knockback;
            melee.poiseDamage = shieldBash.poiseDamage;
            melee.range = bashConeRange;   // the cone reaches further than a sweep

            // A CONE shove, not a single blow: everyone in front is flung along their own
            // bearing (radial) — center enemies straight back, flanks out to the side.
            // The optional inner cone carves out whoever is dead ahead and hands them to
            // the plow instead (OnConeInnerHit → CapturePlowed), so a single sweep produces
            // both behaviours. Dedupe is shared, so nobody is both carried and flung.
            var inner = new MeleeAttack.InnerCone
            {
                halfAngleDeg = bashPlowHalfAngle,
                range = bashPlowRange,
                impulse = bashPlowCaptureImpulse,
            };
            // Props ride the same sweep on their own cone. They're real Rigidbodies that the
            // player COLLIDES with, so they need no plow channel — momentum carries them, and
            // forcing a velocity onto a Rigidbody every frame would fight the solver and read
            // floaty. A shove is all they want.
            var push = new MeleeAttack.ConePush
            {
                halfAngleDeg = bashPropHalfAngle,
                range = bashPropRange,
                impulse = bashPropImpulse,
            };
            bool hit = melee.DoConeSweep(bashConeHalfAngle, bashConeSideBias,
                                         continueSweep: !firstFrame, inner: inner, push: push) > 0;
            if (debugMelee && hit) Debug.Log($"[PlayerMelee] bash {(firstFrame ? "impact" : "charge-through")} — HIT.", this);

            if (hit)
            {
                // Per-victim feedback: fires each time the charge catches someone new,
                // which is what makes a run through a crowd read as a series of impacts.
                cameraKick?.Kick(shieldBash.hitKickEuler, new Vector3(0f, 0f, -0.012f));

                // Once-per-bash feel beats. The freeze and the global time dip STALL the
                // lunge, so firing them per victim would stutter the charge to a halt in
                // a crowd — exactly what a charge-through shouldn't do. First contact
                // only; after that the player ploughs on through.
                if (!bashHitAnyone)
                {
                    bashHitAnyone = true;
                    bashFreeze = shieldBash.localHitstop;
                    bashRecoilDir = -(shieldBash.slashPosition - shieldBash.windupPosition).normalized;
                    Hitstop.Request(shieldBash.globalDipDuration, shieldBash.globalDipScale);
                }
            }

            if (firstFrame || hit) OnImpact?.Invoke(hit);
        }

        /// <summary>
        /// Write the shield hand directly (the bash owns it), record the pose so
        /// TickShield's smoothed counter-motion can resume from it without a pop when
        /// the bash ends, and DERIVE the sword's counter target from it — the mirror of
        /// SetHandPoses: the shield thrusts, the sword hand is thrown the opposite way.
        /// </summary>
        void ApplyShieldPose(Vector3 pos, Vector3 euler, float suppress)
        {
            lastShieldPose = pos;
            lastShieldEuler = euler;
            lastShieldSuppress = suppress;
            shieldSway.SetAttackPose(pos, Quaternion.Euler(euler), suppress);

            swordTargetPos = Vector3.Scale(pos, swordCounterPosition);
            swordTargetEuler = Vector3.Scale(euler, swordCounterEuler);
            swordTargetSuppress = suppress * swordSuppressScale;
        }

        /// <summary>Ease the sword toward its bash-derived counter target (trailing lag), and write it.</summary>
        void TickSword()
        {
            if (swordSway == null) return;

            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, swordLag));
            swordPos = Vector3.Lerp(swordPos, swordTargetPos, k);
            swordEuler = Vector3.Lerp(swordEuler, swordTargetEuler, k);
            swordSuppress = Mathf.Lerp(swordSuppress, swordTargetSuppress, k);

            swordSway.SetAttackPose(swordPos, Quaternion.Euler(swordEuler), swordSuppress);
        }

        void EndBash()
        {
            bashing = false;
            bashSweepTimer = 0f;
            ReleasePlowed();   // the lunge is over — everyone still stuck comes off now
            // Hand the shield back to counter-motion at the pose the bash left it (≈rest
            // after recovery), so the next sword swing's derived motion eases in cleanly.
            shieldPos = lastShieldPose;
            shieldEuler = lastShieldEuler;
            shieldSuppress = lastShieldSuppress;
            // Sword returns to rest (its recovery already eased the counter to ≈0).
            swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);
            swordPos = swordEuler = Vector3.zero;
            swordSuppress = 0f;

            // Same two-reasons-to-chain split as EndSwing: a tap buffered slightly
            // early (bufferedLight) fires regardless of current hold state; a held
            // button is checked fresh right now, not latched, so releasing before the
            // bash finishes doesn't sneak in an extra attack.
            bool continueChain = bufferedLight || (Input.GetMouseButton(lightMouseButton) && !suppressLightUntilRelease);
            if (continueChain && lightCombo.Count > 0 && CanSwing(ignoreCooldown: true))
            {
                bufferedLight = false;
                StartSwing(NextLight(), isHeavy: false);
                return;
            }

            bufferedLight = false;
            readyAt = Time.time + shieldBash.cooldown;

            // Still HOLDING RMB as the bash ends → roll straight into the heavy draw,
            // same "is it held now" pattern as EndSwing (HandleCharge can't see the
            // press itself — it doesn't run while the bash owns the frame).
            if (Input.GetMouseButton(heavyMouseButton) && CanSwing(ignoreCooldown: true))
                BeginCharge();
        }

        /// <summary>Bash owns BOTH hands this frame: the shield is written directly, the sword counters it (TickSword). Skip TickShield; still tick FOV + carry latch.</summary>
        void FinishFrameBash()
        {
            TickSword();
            TickFov();
            carriedLastFrame = carry != null && carry.IsCarrying;
            if (carriedLastFrame) suppressLightUntilRelease = true;
        }

        /// <summary>
        /// Ease the WORLD FOV toward its target — bumped while winding a bash (the lunge
        /// tell), back to the captured base otherwise (including the moment of release,
        /// so the FOV drops as the lunge fires). Lazily captures the base FOV on first
        /// run (an idle frame), so it always restores whatever the camera was set to.
        /// </summary>
        void TickFov()
        {
            if (fov == null || bashFovBump == 0f) return;
            // REQUESTED per frame rather than written: PlayerFov owns the camera's FOV and
            // eases home when nothing asks, so this no longer needs a lazy base capture or a
            // restore on disable — and cannot fight the throw heave, which is active at the
            // same time because carrying does not disable melee.
            if (!bashCharging) return;
            fov.AddOffset(bashFovBump, bashFovSpeed);
        }

        /// <summary>
        /// Pose the sword, and DERIVE the shield's pose from it rather than authoring
        /// one per swing: the off-hand counter-moves (negative weights) because a
        /// swing twists the torso and throws the other arm the opposite way. The
        /// shield target is smoothed in TickShield so it trails the sword — the lag
        /// is what sells it as a body following through instead of a mirror.
        /// </summary>
        void SetHandPoses(Vector3 swordPos, Vector3 swordEuler, float suppress)
        {
            swordSway?.SetAttackPose(swordPos, Quaternion.Euler(swordEuler), suppress);

            shieldTargetPos = Vector3.Scale(swordPos, shieldCounterPosition);
            shieldTargetEuler = Vector3.Scale(swordEuler, shieldCounterEuler);
            shieldTargetSuppress = suppress * shieldSuppressScale;
        }

        /// <summary>
        /// Ease the shield toward its derived target every frame — including back to
        /// rest when idle (the target defaults to zero), so it settles naturally after
        /// a swing without anyone explicitly clearing it.
        /// </summary>
        // ^ that doc belongs to TickShield, further down. Leaving it stranded here would
        //   attach it to the wrong method in tooling and mislead the next reader.

        /// <summary>
        /// The sword's half of the block pose, as its own smoothed layer.
        ///
        /// WHY a separate layer rather than the existing swordTarget*/TickSword loop: that
        /// loop exists, but it is wired ONLY into the bash path (FinishFrameBash), where the
        /// shield leads and the sword takes derived counter-motion. On the ordinary path
        /// nothing runs it, and the sword is instead written DIRECTLY by SetHandPoses during a
        /// swing and zeroed once at EndSwing. So while idle, nothing writes the sword at all —
        /// and ViewmodelSway.SetAttackPose is a LATCH, it just stores what it was last given.
        /// Posing the sword directly for a block therefore set it instantly (a pop, no
        /// ease-in) and then left it there forever. That was the bug.
        ///
        /// Routing the block through swordTarget* instead would mean calling TickSword on the
        /// normal path too, where it would overwrite SetHandPoses' direct per-frame write and
        /// add lag to every swing — a real change to swing feel, to fix an idle-state problem.
        ///
        /// Skipped entirely while the swing system owns the hand, and held at rest during that
        /// time so the layer resumes from the pose the swing actually left behind rather than
        /// snapping back to a stale block offset the moment the swing ends.
        /// </summary>
        void TickBlockSword(bool blocking)
        {
            if (swordSway == null) return;

            bool swingOwnsSword = swinging || charging || bashing || previewT > 0f;
            if (swingOwnsSword)
            {
                swordBlockPos = Vector3.zero;
                swordBlockEuler = Vector3.zero;
                swordBlockSuppress = 0f;
                return;
            }

            Vector3 targetPos = blocking ? blockSwordPosition : Vector3.zero;
            Vector3 targetEuler = blocking ? blockSwordEuler : Vector3.zero;
            float targetSuppress = blocking ? blockSwordSuppress : 0f;

            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, blocking ? blockPoseLag : shieldLag));
            swordBlockPos = Vector3.Lerp(swordBlockPos, targetPos, k);
            swordBlockEuler = Vector3.Lerp(swordBlockEuler, targetEuler, k);
            swordBlockSuppress = Mathf.Lerp(swordBlockSuppress, targetSuppress, k);

            // Written EVERY idle frame, not only while blocking — that is what guarantees the
            // sword actually returns to rest instead of keeping whatever it was last given.
            swordSway.SetAttackPose(swordBlockPos, Quaternion.Euler(swordBlockEuler), swordBlockSuppress);
        }

        void TickShield()
        {
            if (shieldSway == null) return;

            // A raised guard overrides the derived counter-motion. Written as a TARGET rather
            // than posed directly, so it eases in and out through the smoothing below and the
            // shield can't pop when the guard drops. PlayerGuard owns the STATE; this
            // component owns the transform — one owner, as ever.
            //
            // previewBlockPose forces it on for authoring: drag the block fields and watch the
            // shield move live, the same workflow previewT gives swings.
            bool blocking = previewBlockPose || (guard != null && guard.IsGuarding);
            if (blocking)
            {
                shieldTargetPos = blockShieldPosition;
                shieldTargetEuler = blockShieldEuler;
                shieldTargetSuppress = blockShieldSuppress;
            }

            TickBlockSword(blocking);

            // Blocking uses its own lag: shieldLag is tuned for the swing counter-motion,
            // where trailing the sword is the point. A guard coming up late feels unresponsive
            // and, worse, misrepresents the parry window you're actually being judged on.
            float lag = blocking ? blockPoseLag : shieldLag;
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, lag));
            shieldPos = Vector3.Lerp(shieldPos, shieldTargetPos, k);
            shieldEuler = Vector3.Lerp(shieldEuler, shieldTargetEuler, k);
            shieldSuppress = Mathf.Lerp(shieldSuppress, shieldTargetSuppress, k);

            // Shake is added AFTER the smoothing, never folded into the target. A target is
            // what the lerp eases TOWARD, so an oscillation written there would be damped into
            // nothing by the very smoothing that makes the guard feel solid — you'd tune the
            // amplitude up and up and still see almost no movement. Same reason HeadBob
            // composes additively on top of the camera rather than driving it.
            Vector3 shakePos = shieldPos;
            Vector3 shakeEuler = shieldEuler;
            if (blockShakeTime > 0f)
            {
                blockShakeTime -= Time.deltaTime;
                float remaining = Mathf.Clamp01(blockShakeTime / Mathf.Max(0.001f, blockShakeDuration));

                // Decaying sine: rings and dies. Squaring the envelope drops it away faster
                // than it arrives, which is what an impact does — a linear fade reads as a
                // motor running down.
                float envelope = remaining * remaining;
                float wave = Mathf.Sin((blockShakeDuration - blockShakeTime) * blockShakeFrequency * Mathf.PI * 2f);
                float amount = envelope * wave * blockShakeStrength;

                shakePos += blockShakeAxis * (blockShakeDistance * amount);
                // Judder about the two axes ACROSS the blow, so the shield rocks around the
                // impact rather than spinning about the line of force.
                shakeEuler += new Vector3(-blockShakeAxis.y, blockShakeAxis.x, 0f) * (blockShakeAngle * amount);
            }

            // Diagnostic for "the shield misbehaves during swings". Prints the three things
            // that can drive the shield, so the culprit is identified rather than guessed:
            // a live SHAKE (blockShakeTime > 0), the BLOCK override, or the swing's own
            // counter-motion target. Whichever is moving is the one to look at.
            if (debugMelee && swinging)
                Debug.Log($"[Shield] target={shieldTargetEuler} posed={shakeEuler} " +
                          $"blocking={blocking} shakeT={blockShakeTime:0.000} " +
                          $"swordEulerIn={swordEuler}", this);

            shieldSway.SetAttackPose(shakePos, Quaternion.Euler(shakeEuler), shieldSuppress);
        }

        /// <summary>
        /// Jolt the shield along a blow. Called from PlayerGuard's block/parry events —
        /// PlayerGuard owns the guard STATE, this component owns the shield transform, so the
        /// feedback lives here (one owner per transform, as with ViewmodelCollision).
        /// </summary>
        void HandleGuardBlocked(Vector3 blowDir, float absorbed) => ShakeShield(blowDir, absorbed);

        // A parry deflects rather than absorbs, so it gets a sharper, smaller tick. It still
        // gets ONE — a parry with no shield reaction at all, next to a block that visibly
        // rings, would read as the parry having failed.
        void HandleGuardParried(Vector3 blowDir, GameObject attacker)
            => ShakeShield(blowDir, blockShakeFullDamage, parryShakeScale);

        public void ShakeShield(Vector3 worldBlowDirection, float absorbed, float scale = 1f)
        {
            if (blockShakeDuration <= 0f) return;

            // Into the VIEWMODEL's frame: the shield pose is a camera-local offset, so a world
            // direction has to be converted or the jolt points somewhere unrelated to where the
            // blow came from. A frontal blow travels toward the camera and so lands as -Z
            // locally, which pushes the shield back at you — exactly right, with no special
            // casing for direction.
            Transform eye = melee != null && melee.aimSource != null ? melee.aimSource : transform;
            Vector3 local = eye.InverseTransformDirection(worldBlowDirection);
            blockShakeAxis = local.sqrMagnitude > 1e-6f ? local.normalized : Vector3.back;

            float strength = blockShakeFullDamage > 0f
                           ? Mathf.Clamp01(absorbed / blockShakeFullDamage)
                           : 1f;
            // A blow absorbed to nothing still rang the shield — floor it so a fully mitigated
            // hit isn't silent and invisible, which would read as the block not registering.
            strength = Mathf.Max(0.35f, strength) * scale;

            // Restart rather than accumulate: two blows in quick succession should re-ring the
            // shield, not stack into a bigger and bigger wobble.
            blockShakeTime = blockShakeDuration;
            blockShakeStrength = strength;
        }

        /// <summary>
        /// World blow direction from the swing's own motion: the camera-local
        /// windup→slash delta, vertical damped (short enemies), rotated by camera
        /// YAW only (pitch aims, not shoves), then biased toward forward. Each swing
        /// → its own direction → its own NpcFlinch profile.
        /// </summary>
        Vector3 ComputeBlowDirection(SwingDefinition swing)
        {
            Transform eye = melee.aimSource != null ? melee.aimSource : transform;
            Vector3 localMotion = swing.slashPosition - swing.windupPosition;
            localMotion.y *= swing.blowVerticalScale;
            if (localMotion.sqrMagnitude < 1e-6f) return eye.forward;

            Quaternion yaw = Quaternion.Euler(0f, eye.eulerAngles.y, 0f);
            Vector3 worldMotion = (yaw * localMotion).normalized;
            Vector3 flatForward = yaw * Vector3.forward;
            return Vector3.Slerp(worldMotion, flatForward, swing.blowForwardBias).normalized;
        }

        SwingDefinition PreviewSwing()
        {
            if (previewSwingIndex < 0 || lightCombo.Count == 0) return heavySwing;
            return lightCombo[Mathf.Clamp(previewSwingIndex, 0, lightCombo.Count - 1)];
        }

        void OnEnable()
        {
            if (melee != null) melee.OnConeInnerHit += CapturePlowed;
            if (guard != null)
            {
                guard.OnBlocked += HandleGuardBlocked;
                guard.OnParried += HandleGuardParried;
            }
        }

        void OnDisable()
        {
            if (melee != null) melee.OnConeInnerHit -= CapturePlowed;
            if (guard != null)
            {
                guard.OnBlocked -= HandleGuardBlocked;
                guard.OnParried -= HandleGuardParried;
            }
            blockShakeTime = 0f;   // don't resume a half-finished ring after a weapon swap
            if (charging && controller != null) controller.moveScaleOverride = 1f;
            charging = false;
            returning = false;
            tension = 0f;
            if (swinging) EndSwing();
            if (bashing) EndBash();
            // Belt and braces for a loadout swap mid-lunge. EndBash above already releases,
            // and NpcLocomotion's plow times out on its own, but leaving a body being driven
            // by a component that no longer runs is not a state worth relying on a timeout
            // to exit. Second call is a no-op — ReleasePlowed clears the list.
            ReleasePlowed();

            // Don't leave a half-wound shield behind if disabled mid-charge. The FOV needs no
            // restore: PlayerFov eases home once this stops requesting an offset.
            bashCharging = false;
        }

        // ---------------- Gizmo ----------------

        Vector3 swordRestLocal;
        bool haveRestLocal;

        void Start()
        {
            if (swordSway != null) { swordRestLocal = swordSway.transform.localPosition; haveRestLocal = true; }
        }

        void OnDrawGizmosSelected()
        {
            if (swordSway == null) return;
            Transform hand = swordSway.transform;
            Transform parent = hand.parent;
            if (parent == null) return;

            SwingDefinition swing = PreviewSwing();
            if (swing == null) return;

            Vector3 restLocal = Application.isPlaying && haveRestLocal ? swordRestLocal : hand.localPosition;

            const int steps = 48;
            Vector3 prev = parent.TransformPoint(restLocal);
            for (int i = 1; i <= steps; i++)
            {
                float nt = i / (float)steps;
                swing.ComputePose(nt, out Vector3 pos, out _, out _);
                Vector3 world = parent.TransformPoint(restLocal + pos);
                Gizmos.color = nt < swing.windupEnd ? Color.yellow : nt < swing.slashEnd ? Color.red : Color.cyan;
                Gizmos.DrawLine(prev, world);
                prev = world;
            }

            swing.ComputePose(swing.impactT, out Vector3 impactPos, out _, out _);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(parent.TransformPoint(restLocal + impactPos), 0.02f);

            DrawBashConeGizmo();
        }

        /// <summary>
        /// The bash CONE on the floor plane: origin, ±halfAngle edge rays, and an arc at
        /// bashConeRange — matches DoConeSweep (flat aim, flat-distance reach). Uses the
        /// aimSource (camera) at runtime, the player transform in edit mode.
        /// </summary>
        void DrawBashConeGizmo()
        {
            if (!drawBashCone) return;

            var m = GetComponent<MeleeAttack>();
            Transform eye = m != null && m.aimSource != null ? m.aimSource : transform;
            Vector3 flat = new Vector3(eye.forward.x, 0f, eye.forward.z);
            if (flat.sqrMagnitude < 1e-4f) flat = new Vector3(transform.forward.x, 0f, transform.forward.z);
            flat.Normalize();

            float originHeight = m != null ? m.originHeight : 1.1f;
            Vector3 origin = eye == transform ? transform.position + Vector3.up * originHeight : eye.position;
            origin.y = transform.position.y + originHeight * 0.4f;   // draw near knee/waist — the shove plane

            DrawConeFan(origin, flat, bashConeHalfAngle, bashConeRange, new Color(0.3f, 0.7f, 1f, 0.9f));

            // The narrow PLOW cone nested inside it — whoever gets carried rather than
            // flung. Drawn in the same frame so the two reaches are directly comparable.
            if (drawBashPlowCone && bashPlowHalfAngle > 0f)
                DrawConeFan(origin, flat, bashPlowHalfAngle, bashPlowRange, new Color(1f, 0.5f, 0.1f, 0.95f));

            // The PROP shove cone — green, so all three reaches are comparable at a glance.
            if (drawBashPropCone && bashPropImpulse > 0f)
                DrawConeFan(origin, flat, bashPropHalfAngle, bashPropRange, new Color(0.3f, 1f, 0.4f, 0.9f));
        }

        static void DrawConeFan(Vector3 origin, Vector3 flat, float halfAngle, float range, Color color)
        {
            Gizmos.color = color;
            const int steps = 20;
            Vector3 prevEdge = origin;
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(-halfAngle, halfAngle, i / (float)steps);
                Vector3 rayDir = Quaternion.AngleAxis(a, Vector3.up) * flat;
                Vector3 end = origin + rayDir * range;
                if (i == 0 || i == steps) Gizmos.DrawLine(origin, end);   // the two edges
                if (i > 0) Gizmos.DrawLine(prevEdge, end);                // the arc
                prevEdge = end;
            }
        }
    }
}
