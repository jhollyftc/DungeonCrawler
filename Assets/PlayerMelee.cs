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

        [Header("Input")]
        [Tooltip("0 = left mouse (LIGHT combo).")]
        public int lightMouseButton = 0;
        [Tooltip("1 = right mouse (HEAVY: hold to charge, release to swing).")]
        public int heavyMouseButton = 1;

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

        [Tooltip("Log every swing with its name and hit/whiff.")]
        public bool debugMelee = false;

        [Header("Authoring (play/edit mode)")]
        [Tooltip("Which swing the preview + gizmo target: -1 = the heavy, 0..N = lightCombo index.")]
        public int previewSwingIndex = 0;
        [Tooltip("PLAY-MODE POSE SCRUB: drag off 0 to freeze the previewed swing at that point of its arc, live in the Game view. Set back to 0 to release.")]
        [Range(0f, 1f)] public float previewT = 0f;

        /// <summary>A swing started (windup begins).</summary>
        public event Action OnSwingStarted;
        /// <summary>The impact frame fired; bool = did it damage anything. THE feel-layer hook.</summary>
        public event Action<bool> OnImpact;

        public bool IsSwinging => swinging;
        public bool IsCharging => charging;

        MeleeAttack melee;
        PlayerCarry carry;
        FirstPersonController controller;
        CameraKick cameraKick;

        SwingDefinition active;   // the swing currently playing
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

        bool carriedLastFrame;
        bool previewReleased;

        // Shield counter-motion: a target set by whatever pose the sword took this
        // frame (zero when idle), smoothed toward so the off-hand trails.
        Vector3 shieldTargetPos, shieldTargetEuler;
        float shieldTargetSuppress;
        Vector3 shieldPos, shieldEuler;
        float shieldSuppress;

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

            if (swordSway == null)
                Debug.LogWarning("[PlayerMelee] No sword ViewmodelSway assigned — the sweep works but the sword won't visibly swing.", this);
            if (lightCombo.Count == 0)
                Debug.LogWarning("[PlayerMelee] lightCombo is empty — add at least one SwingDefinition for LMB.", this);
        }

        void Update()
        {
            // Default the shield to rest each frame; whichever pose runs below sets
            // its target. Idle frames therefore ease it home on their own.
            shieldTargetPos = Vector3.zero;
            shieldTargetEuler = Vector3.zero;
            shieldTargetSuppress = 0f;

            if (swinging) { TickSwing(); FinishFrame(); return; }

            // Preview scrub takes over when a swing isn't playing.
            if (previewT > 0f) { ApplyPose(PreviewSwing(), previewT); previewReleased = true; FinishFrame(); return; }
            if (previewReleased) { swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f); previewReleased = false; }

            if (returning) TickChargeReturn();   // a new charge/swing below cancels it

            HandleCharge();
            HandleLightInput();

            // Combo decays back to the first swing after a lull.
            if (comboIndex != 0 && Time.time >= comboResetAt) comboIndex = 0;

            FinishFrame();
        }

        void FinishFrame()
        {
            TickShield();   // every path ends here, so the shield always eases toward its target
            carriedLastFrame = carry != null && carry.IsCarrying;
        }

        // ---------------- Input ----------------

        void HandleLightInput()
        {
            if (charging || swinging || lightCombo.Count == 0) return;
            if (Input.GetMouseButtonDown(lightMouseButton) && CanSwing())
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
            if (debugMelee) Debug.Log($"[PlayerMelee] swing '{swing.name}'{(isHeavy ? " (HEAVY)" : "")}.", this);
        }

        void TickSwing()
        {
            // Queue the next light the moment the swing is spent — captured at the
            // TOP so a press during the freeze OR the retract counts too. On a hit the
            // clock stops at impact (t < slashEnd), so the normal "past slashEnd"
            // window never opens; the freeze/retract flags are the ending signal.
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

            // A BUFFERED chain fires immediately and skips the cooldown — that's the
            // whole point of buffering, and setting readyAt first would block the
            // very swing we just queued (CanSwing would see Time.time < readyAt).
            if (bufferedLight && lightCombo.Count > 0 && CanSwing(ignoreCooldown: true))
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
        }

        void ApplyPose(SwingDefinition swing, float nt)
        {
            swing.ComputePose(nt, out Vector3 pos, out Vector3 euler, out float suppress);
            SetHandPoses(pos, euler, suppress);
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
        }
    }
}
