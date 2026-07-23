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
        [Tooltip("1 = right mouse (HEAVY: hold to charge, release to swing).")]
        public int heavyMouseButton = 1;
        [Tooltip("SHIELD BASH key. A dedicated off-hand key, kept off the mouse so it's independent of light/heavy (and leaves the mouse free for a future shield BLOCK bind).")]
        public KeyCode bashKey = KeyCode.Q;

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
        [Tooltip("Degrees the world FOV widens while winding the bash (the lunge tell), easing back on release. 0 = no FOV kick. The viewmodel overlay keeps its own FOV, so only the WORLD dollies.")]
        public float bashFovBump = 8f;
        [Tooltip("FOV change speed (deg/sec) in and out of the bump.")]
        public float bashFovSpeed = 60f;
        [Tooltip("Draw the bash CONE (reach + fan angle) as a gizmo when this component is selected — see exactly what the shove will catch.")]
        public bool drawBashCone = true;

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
        bool bashSweepDone;
        float bashFreeze;         // bash-hit local hitstop
        Vector3 bashRecoilDir;    // shield bounce-back direction on a bash hit
        Vector3 lastShieldPose, lastShieldEuler;   // last pose the bash wrote, so counter-motion resumes without a pop
        float lastShieldSuppress;

        Camera fovCam;            // world camera, for the bash FOV kick
        float baseFov;
        bool haveBaseFov;

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
        float swordSuppress;

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();
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
            if (controller != null && controller.cam != null) fovCam = controller.cam.GetComponent<Camera>();
            if (fovCam == null && melee.aimSource != null) fovCam = melee.aimSource.GetComponent<Camera>();

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
            bashFreeze = 0f;
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
                return;
            }

            bt += Time.deltaTime / Mathf.Max(0.05f, shieldBash.duration);

            if (!bashSweepDone && bt >= shieldBash.impactT)
            {
                bashSweepDone = true;
                DoBashImpact();
            }

            if (bt >= 1f) { EndBash(); return; }

            shieldBash.ComputePose(bt, out Vector3 pos, out Vector3 euler, out float suppress);
            ApplyShieldPose(pos, euler, suppress);
        }

        void DoBashImpact()
        {
            melee.damage = shieldBash.damage;
            melee.knockback = shieldBash.knockback;
            melee.poiseDamage = shieldBash.poiseDamage;
            melee.range = bashConeRange;   // the cone reaches further than a sweep

            // A CONE shove, not a single blow: everyone in front is flung along their own
            // bearing (radial) — center enemies straight back, flanks out to the side.
            bool hit = melee.DoConeSweep(bashConeHalfAngle, bashConeSideBias) > 0;
            if (debugMelee) Debug.Log($"[PlayerMelee] bash impact — {(hit ? "HIT" : "whiff")}.", this);

            if (hit)
            {
                bashFreeze = shieldBash.localHitstop;
                bashRecoilDir = -(shieldBash.slashPosition - shieldBash.windupPosition).normalized;
                Hitstop.Request(shieldBash.globalDipDuration, shieldBash.globalDipScale);
                cameraKick?.Kick(shieldBash.hitKickEuler, new Vector3(0f, 0f, -0.012f));
            }

            OnImpact?.Invoke(hit);
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
            if (fovCam == null || bashFovBump == 0f) return;
            if (!haveBaseFov) { baseFov = fovCam.fieldOfView; haveBaseFov = true; }
            float target = bashCharging ? baseFov + bashFovBump : baseFov;
            fovCam.fieldOfView = Mathf.MoveTowards(fovCam.fieldOfView, target, bashFovSpeed * Time.deltaTime);
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
        void TickShield()
        {
            if (shieldSway == null) return;

            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, shieldLag));
            shieldPos = Vector3.Lerp(shieldPos, shieldTargetPos, k);
            shieldEuler = Vector3.Lerp(shieldEuler, shieldTargetEuler, k);
            shieldSuppress = Mathf.Lerp(shieldSuppress, shieldTargetSuppress, k);

            shieldSway.SetAttackPose(shieldPos, Quaternion.Euler(shieldEuler), shieldSuppress);
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

        void OnDisable()
        {
            if (charging && controller != null) controller.moveScaleOverride = 1f;
            charging = false;
            returning = false;
            tension = 0f;
            if (swinging) EndSwing();
            if (bashing) EndBash();

            // Don't leave a widened FOV or a half-wound shield behind if disabled mid-charge.
            bashCharging = false;
            if (fovCam != null && haveBaseFov) fovCam.fieldOfView = baseFov;
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

            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.9f);
            const int steps = 20;
            Vector3 prevEdge = origin;
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(-bashConeHalfAngle, bashConeHalfAngle, i / (float)steps);
                Vector3 rayDir = Quaternion.AngleAxis(a, Vector3.up) * flat;
                Vector3 end = origin + rayDir * bashConeRange;
                if (i == 0 || i == steps) Gizmos.DrawLine(origin, end);   // the two edges
                if (i > 0) Gizmos.DrawLine(prevEdge, end);                // the arc
                prevEdge = end;
            }
        }
    }
}
