using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The player's sword swing (melee-v1 phase 1). LMB drives a PROCEDURAL swing:
    /// a windup → slash → recovery arc authored as camera-local pose keys, played
    /// through ViewmodelSway.SetAttackPose — this component never touches the
    /// weapon transform itself, so sway/attack/collision stay one composed pose
    /// (the codebase's one-writer rule) and the blade still can't swing through a
    /// wall (the collision clamp runs after the attack pose).
    ///
    /// The sweep (MeleeAttack.DoSweep) fires ONCE at the authored impact instant,
    /// aimed from the camera — you slash where you look, pitch included. Whether
    /// it landed is captured and surfaced via OnImpact: phase 2's feel layer
    /// (hitstop, the blade catching in a body, camera kick) keys entirely off
    /// that hit/whiff divergence.
    ///
    /// Swinging is blocked while carrying (hands are full and the viewmodel is
    /// stowed) and while the cursor is unlocked (menus).
    /// </summary>
    [RequireComponent(typeof(MeleeAttack))]
    [DisallowMultipleComponent]
    public class PlayerMelee : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("The SWORD hand's ViewmodelSway — the swing pose is injected through it.")]
        public ViewmodelSway swordSway;

        [Header("Input")]
        [Tooltip("0 = left mouse.")]
        public int attackMouseButton = 0;

        [Header("Swing timing (seconds / normalized)")]
        [Tooltip("Total swing time, windup through recovery.")]
        public float duration = 0.6f;
        [Tooltip("Normalized t where the windup ends and the slash launches.")]
        [Range(0.05f, 0.9f)] public float windupEnd = 0.3f;
        [Tooltip("Normalized t of the IMPACT — the single frame the sweep fires. Just after the slash starts moving fast.")]
        [Range(0.1f, 0.95f)] public float impactT = 0.42f;
        [Tooltip("Normalized t where the slash arc ends and recovery eases back to rest.")]
        [Range(0.2f, 0.95f)] public float slashEnd = 0.55f;
        [Tooltip("Extra seconds after a swing before the next can start.")]
        public float cooldown = 0.05f;

        [Header("Swing shape (camera-local offsets from the rest pose)")]
        [Tooltip("Pose at the top of the windup — sword pulled up/back, coiled.")]
        public Vector3 windupPosition = new Vector3(0.06f, 0.07f, -0.12f);
        public Vector3 windupEuler = new Vector3(-35f, 25f, 15f);
        [Tooltip("Pose at the end of the slash — down and across the body.")]
        public Vector3 slashPosition = new Vector3(-0.10f, -0.06f, 0.16f);
        public Vector3 slashEuler = new Vector3(50f, -35f, -25f);

        [Tooltip("How much the reaction is biased toward straight-forward (into the target) vs. the swing's own lateral/vertical direction. 0 = the goblin recoils purely along the slash (a left cut shoves it right); 1 = always straight back. ~0.4 keeps a forward push while still reading the slash's direction. THE dial for 'reactions follow my swing'.")]
        [Range(0f, 1f)] public float blowForwardBias = 0.4f;
        [Tooltip("How much of the swing's VERTICAL motion becomes vertical push. Kept low on purpose: enemies are SHORT, so at melee range you look steeply down at them — if camera pitch fed the blow direction, every hit would shove them straight into the floor and look identical from every side. Camera pitch aims; it does not decide which way a body flies. Raise toward 1 for chops that visibly drive downward.")]
        [Range(0f, 1f)] public float blowVerticalScale = 0.3f;

        [Header("Feel — hit vs whiff (phase 2)")]
        [Tooltip("Seconds the SWING freezes at the contact pose when a hit lands — the blade catching in the body before the arc completes. THE core feel number. A whiff never freezes; that divergence is the whole point.")]
        public float localHitstop = 0.09f;
        [Tooltip("How far (m) the blade recoils back along the slash during the freeze — meeting resistance, not a wall.")]
        public float recoilDistance = 0.045f;
        [Tooltip("Global time-dip on a landed hit: duration (unscaled seconds). Keep SHORT — timescale slows physics too.")]
        public float globalDipDuration = 0.05f;
        [Tooltip("Global time-dip scale (0.1 = world at 10% speed for the dip).")]
        [Range(0f, 1f)] public float globalDipScale = 0.12f;
        [Tooltip("Camera punch on a LANDED hit (deg: pitch, yaw, roll). Small — this is your head.")]
        public Vector3 hitKickEuler = new Vector3(1.8f, -0.7f, -2.2f);
        [Tooltip("Tiny camera counter-motion at swing START, for weight even on a whiff.")]
        public Vector3 swingKickEuler = new Vector3(-0.5f, 0.3f, 0.7f);

        [Tooltip("Log every swing with hit/whiff.")]
        public bool debugMelee = false;

        [Header("Authoring (play mode)")]
        [Tooltip("PLAY-MODE POSE SCRUB: drag off 0 and the sword freezes at that point of the swing arc (through the sway system, so it's exactly what a real swing shows). Workflow: enter play, drag this slider while editing the windup/slash pose fields and watch the sword move live in the Game view. Set back to 0 to release. The scene gizmo (select the player) draws the whole arc + the impact point.")]
        [Range(0f, 1f)] public float previewT = 0f;

        /// <summary>A swing started (windup begins). Phase 2/3: whoosh audio, sway kick.</summary>
        public event Action OnSwingStarted;
        /// <summary>The impact frame fired; bool = did it damage anything. THE feel-layer hook.</summary>
        public event Action<bool> OnImpact;

        public bool IsSwinging => swinging;

        MeleeAttack melee;
        PlayerCarry carry;
        CameraKick cameraKick;
        float t;              // normalized swing time
        bool swinging;
        bool sweepDone;
        float readyAt;
        float freezeTimer;    // >0 = the blade is caught in a body (local hitstop)
        Vector3 recoilDir;    // direction the blade bounces back during the freeze
        bool carriedLastFrame; // LMB throws while carrying; without this, the SAME click would also swing (script order permitting) the frame the prop leaves the hands

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();
            carry = GetComponent<PlayerCarry>();
            cameraKick = GetComponentInChildren<CameraKick>(true);

            // Aim from the eye so the sweep follows pitch. Found here rather than
            // serialized so the prefab stays self-wiring like the rest of the rig.
            if (melee.aimSource == null)
            {
                var fpc = GetComponent<FirstPersonController>();
                if (fpc != null && fpc.cam != null) melee.aimSource = fpc.cam;
                else
                {
                    var c = GetComponentInChildren<Camera>();
                    if (c != null) melee.aimSource = c.transform;
                }
            }

            if (swordSway == null)
                Debug.LogWarning("[PlayerMelee] No sword ViewmodelSway assigned — the sweep will work but the sword won't visibly swing.", this);
        }

        void Update()
        {
            if (swinging)
            {
                TickSwing();
            }
            else if (previewT > 0f)
            {
                // Authoring scrub: hold the sword at previewT through the sway
                // system — the one-writer rule holds even for the preview, so what
                // you tune is exactly what a real swing renders.
                ApplyPose(previewT);
            }
            else if (Input.GetMouseButtonDown(attackMouseButton) && CanSwing())
            {
                StartSwing();
            }
            else if (previewReleased)
            {
                // Slider just returned to 0: hand the pose cleanly back to sway.
                swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);
                previewReleased = false;
            }

            if (previewT > 0f) previewReleased = true;
            carriedLastFrame = carry != null && carry.IsCarrying;
        }

        bool previewReleased;

        bool CanSwing()
        {
            if (Time.time < readyAt) return false;
            if (Cursor.lockState != CursorLockMode.Locked) return false;  // menus
            if (carry != null && carry.IsCarrying) return false;          // hands full (viewmodel stowed)
            if (carriedLastFrame) return false;                           // this click was the THROW
            return true;
        }

        void StartSwing()
        {
            swinging = true;
            sweepDone = false;
            t = 0f;
            freezeTimer = 0f;
            cameraKick?.Kick(swingKickEuler);
            OnSwingStarted?.Invoke();
        }

        void TickSwing()
        {
            // The blade is caught in a body: the swing's clock STOPS and the sword
            // holds at the contact pose with a small recoil bounce — resistance,
            // not a wall. Unscaled time, or the global dip would stretch the
            // freeze ~10x. A whiff never enters this branch: hit and whiff FEELING
            // different is the entire feel layer.
            if (freezeTimer > 0f)
            {
                freezeTimer -= Time.unscaledDeltaTime;
                float p = 1f - Mathf.Clamp01(freezeTimer / Mathf.Max(0.01f, localHitstop));
                float envelope = Mathf.Sin(p * Mathf.PI);   // out and back — settles at the contact pose

                ComputePose(t, out Vector3 fpos, out Vector3 feuler, out float fsup);
                swordSway?.SetAttackPose(fpos + recoilDir * (recoilDistance * envelope), Quaternion.Euler(feuler), fsup);
                return;
            }

            t += Time.deltaTime / Mathf.Max(0.05f, duration);

            // The impact is a single authored instant, not a phase — fire once.
            if (!sweepDone && t >= impactT)
            {
                sweepDone = true;
                melee.blowDirectionOverride = ComputeBlowDirection();
                bool hit = melee.DoSweep();
                if (debugMelee) Debug.Log($"[PlayerMelee] impact — {(hit ? "HIT" : "whiff")}.", this);

                if (hit)
                {
                    // The catch: freeze the arc, bounce the blade back along the
                    // slash, dip the world, punch the head. Layered, all brief.
                    freezeTimer = localHitstop;
                    recoilDir = -(slashPosition - windupPosition).normalized;
                    Hitstop.Request(globalDipDuration, globalDipScale);
                    cameraKick?.Kick(hitKickEuler, new Vector3(0f, 0f, -0.012f));
                }

                OnImpact?.Invoke(hit);
            }

            if (t >= 1f)
            {
                EndSwing();
                return;
            }

            ApplyPose(t);
        }

        void EndSwing()
        {
            swinging = false;
            readyAt = Time.time + cooldown;
            swordSway?.SetAttackPose(Vector3.zero, Quaternion.identity, 0f);
        }

        void ApplyPose(float nt)
        {
            ComputePose(nt, out Vector3 pos, out Vector3 euler, out float suppress);
            swordSway?.SetAttackPose(pos, Quaternion.Euler(euler), suppress);
        }

        /// <summary>
        /// The world direction the blade travels through the target — the camera-
        /// local slash motion (windup pose → slash pose) rotated into world space,
        /// then biased toward straight-forward so a weak lateral swing still drives
        /// INTO the goblin. This is what makes the recoil follow the SWING instead
        /// of always shoving straight back, and it's the hook a future left/right/
        /// overhead swing set plugs into for free (each pose delta → its own
        /// reaction direction).
        /// </summary>
        Vector3 ComputeBlowDirection()
        {
            Transform eye = melee.aimSource != null ? melee.aimSource : transform;

            Vector3 localMotion = slashPosition - windupPosition;
            localMotion.y *= blowVerticalScale;
            if (localMotion.sqrMagnitude < 1e-6f) return eye.forward;

            // YAW-ONLY frame. Enemies are short, so at melee range the camera is
            // pitched steeply down — using the full camera rotation turned every
            // blow into a downward stomp, identical from every side and mashing the
            // ragdoll into the floor. Pitch aims the sweep; yaw (plus the swing's
            // own damped vertical) decides which way the body is shoved.
            Quaternion yaw = Quaternion.Euler(0f, eye.eulerAngles.y, 0f);
            Vector3 worldMotion = (yaw * localMotion).normalized;
            Vector3 flatForward = yaw * Vector3.forward;

            return Vector3.Slerp(worldMotion, flatForward, blowForwardBias).normalized;
        }

        /// <summary>
        /// The procedural arc: rest → windup (ease, coiling) → slash (fast,
        /// committed) → rest (ease out). Pure function of t, shared by the swing,
        /// the preview scrub, and the scene gizmo so they can never disagree.
        /// </summary>
        public void ComputePose(float nt, out Vector3 pos, out Vector3 euler, out float suppress)
        {
            if (nt < windupEnd)
            {
                // Coil: ease into the windup pose.
                float k = Mathf.SmoothStep(0f, 1f, nt / windupEnd);
                pos = Vector3.Lerp(Vector3.zero, windupPosition, k);
                euler = Vector3.Lerp(Vector3.zero, windupEuler, k);
                suppress = k;
            }
            else if (nt < slashEnd)
            {
                // Slash: fast and front-loaded — most of the travel happens
                // immediately (k^0.6 rises steeply), which is what makes it read
                // as a strike rather than a wave.
                float k = Mathf.Pow((nt - windupEnd) / (slashEnd - windupEnd), 0.6f);
                pos = Vector3.LerpUnclamped(windupPosition, slashPosition, k);
                euler = Vector3.LerpUnclamped(windupEuler, slashEuler, k);
                suppress = 1f;
            }
            else
            {
                // Recovery: ease back to rest, hand sway back.
                float k = Mathf.SmoothStep(0f, 1f, (nt - slashEnd) / (1f - slashEnd));
                pos = Vector3.Lerp(slashPosition, Vector3.zero, k);
                euler = Vector3.Lerp(slashEuler, Vector3.zero, k);
                suppress = 1f - k;
            }
        }

        void OnDisable()
        {
            if (swinging) EndSwing();
        }

        Vector3 swordRestLocal;     // captured at Awake for the play-mode gizmo (sway owns the live transform)
        bool haveRestLocal;

        void Start()
        {
            if (swordSway != null)
            {
                swordRestLocal = swordSway.transform.localPosition;
                haveRestLocal = true;
            }
        }

        /// <summary>
        /// Draws the whole swing arc through the sword's actual position in the
        /// Scene view — windup yellow, slash red, recovery cyan, a sphere at the
        /// impact instant. Works in edit mode too (select the player, look at the
        /// sword), so the pose fields can be shaped before ever pressing play.
        /// </summary>
        void OnDrawGizmosSelected()
        {
            if (swordSway == null) return;
            Transform hand = swordSway.transform;
            Transform parent = hand.parent;
            if (parent == null) return;

            Vector3 restLocal = Application.isPlaying && haveRestLocal ? swordRestLocal : hand.localPosition;

            const int steps = 48;
            Vector3 prev = parent.TransformPoint(restLocal);
            for (int i = 1; i <= steps; i++)
            {
                float nt = i / (float)steps;
                ComputePose(nt, out Vector3 pos, out _, out _);
                Vector3 world = parent.TransformPoint(restLocal + pos);

                Gizmos.color = nt < windupEnd ? Color.yellow : nt < slashEnd ? Color.red : Color.cyan;
                Gizmos.DrawLine(prev, world);
                prev = world;
            }

            ComputePose(impactT, out Vector3 impactPos, out _, out _);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(parent.TransformPoint(restLocal + impactPos), 0.02f);
        }
    }
}
