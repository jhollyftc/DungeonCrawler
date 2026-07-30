using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The bow: HOLD LMB to draw and hold the string, RELEASE to loose an arrow.
    ///
    /// Unlike PlayerMelee — which poses the sword procedurally through
    /// ViewmodelSway.SetAttackPose because a swing's ARC is gameplay (blow direction
    /// drives which NpcFlinch profile fires) — a bow's draw is a fixed authored motion
    /// with no directional payload. So this drives an ANIMATOR and stays a one-way
    /// bridge, exactly like NpcAnimatorDriver: the bow logic never learns what the
    /// Animator contains, so re-authoring the animation can't break the shooting.
    ///
    /// Animator parameters (create the ones your controller uses; missing ones are
    /// skipped with a one-time notice, so a stub controller still fires arrows):
    ///   Draw        (bool)  — true from the moment you press until the shot leaves.
    ///                         Drive the pull-and-hold from this.
    ///   DrawAmount  (float) — 0..1 pull progress. Wire it to a blend tree if you want
    ///                         the string to follow the hold rather than snap.
    ///   Release     (trigger) — the loose. Fired the instant the arrow spawns.
    ///
    /// Draw strength scales BOTH arrow speed and damage, so a snap shot is genuinely
    /// weaker rather than merely looking weaker.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerBow : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("Animator on the bow viewmodel. Left empty, searched in children of bowRoot.")]
        public Animator animator;
        [Tooltip("Bow viewmodel root — used to find the Animator and the muzzle if they're unset.")]
        public Transform bowRoot;
        [Tooltip("Where arrows spawn. Ideally the arrow rest on the bow. Left empty, the aim camera is used, which shoots from the eye — accurate, but the arrow appears from nowhere.")]
        public Transform muzzle;
        [Tooltip("Aim source. Left empty, FirstPersonController's camera — so arrows fly where you LOOK, not where the bow model happens to point.")]
        public Transform aimSource;

        [Header("Nocked arrow (visual)")]
        [Tooltip("Arrow MESH parented to the bow's 'string' bone in the viewmodel prefab — purely cosmetic, so the bow visibly holds an arrow while drawn. Shown when a draw starts and hidden the instant the real projectile spawns, so there's never a frame with two arrows.\n\nParent it in the prefab rather than having this spawn it: the string bone may carry the non-uniform scale an FBX axis conversion leaves behind, and authoring it means you can see and correct any distortion in the editor instead of fighting it at runtime.")]
        public GameObject nockedArrow;
        [Tooltip("Keep an arrow on the string even at rest, so the bow always looks ready. It reappears once the shot cooldown elapses, which reads as drawing a fresh arrow. Off = the string is empty until you start a draw.")]
        public bool showNockedWhileIdle = false;

        [Header("Arrow")]
        public GameObject arrowPrefab;
        [Tooltip("Launch speed (m/s) at a FULL draw. Scaled by the draw actually achieved.")]
        public float fullDrawSpeed = 38f;
        [Tooltip("Launch speed at the minimum firing draw — a snap shot still moves, just badly.")]
        public float minDrawSpeed = 14f;

        [Header("Draw")]
        [Tooltip("Seconds from press to a full draw.")]
        public float drawTime = 0.75f;
        [Tooltip("Draw (0..1) below which releasing ABORTS instead of firing — the bow relaxes and no arrow is spent. Commitment, like the heavy swing's full wind-up.")]
        [Range(0f, 1f)] public float minDrawToFire = 0.25f;
        [Tooltip("Seconds after a shot before another draw can start.")]
        public float shotCooldown = 0.35f;
        [Tooltip("Move-speed multiplier while drawn — drawing a bow should cost mobility, same as charging a heavy swing.")]
        [Range(0.1f, 1f)] public float drawMoveScale = 0.65f;

        [Header("Input")]
        public int fireMouseButton = 0;

        [Tooltip("Log draw/release/abort.")]
        public bool debugBow = false;

        /// <summary>Draw started.</summary>
        public event Action OnDrawStarted;
        /// <summary>Arrow loosed; float = the draw (0..1) it was released at.</summary>
        public event Action<float> OnShot;
        /// <summary>Draw released below minDrawToFire — relaxed, no arrow spent.</summary>
        public event Action OnDrawAborted;

        public bool IsDrawing => drawing;
        public float Draw01 => draw;

        static readonly int DrawParam = Animator.StringToHash("Draw");
        static readonly int DrawAmountParam = Animator.StringToHash("DrawAmount");
        static readonly int ReleaseParam = Animator.StringToHash("Release");

        FirstPersonController controller;
        PlayerCarry carry;
        bool hasDraw, hasDrawAmount, hasRelease;
        bool releaseIsTrigger;          // see the parameter scan in Awake
        bool clearReleaseNextUpdate;    // bool-authored Release needs manual clearing

        bool drawing;
        float draw;
        float readyAt;
        // Latched when a press was consumed by something else (a throw, or the swap TO
        // the bow) and cleared only on a full release — the same guard PlayerMelee needs,
        // for the same reason: a HELD button would otherwise immediately read as a fresh
        // draw the frame after it was used for something different.
        bool suppressUntilRelease;

        void Awake()
        {
            controller = GetComponent<FirstPersonController>();
            carry = GetComponent<PlayerCarry>();

            if (animator == null && bowRoot != null) animator = bowRoot.GetComponentInChildren<Animator>(true);
            if (aimSource == null && controller != null && controller.cam != null) aimSource = controller.cam;
            if (muzzle == null) muzzle = aimSource;

            if (animator != null && animator.runtimeAnimatorController != null)
            {
                foreach (var p in animator.parameters)
                {
                    if (p.nameHash == DrawParam) hasDraw = true;
                    if (p.nameHash == DrawAmountParam) hasDrawAmount = true;
                    if (p.nameHash == ReleaseParam)
                    {
                        hasRelease = true;
                        // Release works authored as EITHER a Trigger or a Bool, because
                        // the two behave very differently and the failure is silent:
                        // SetTrigger on a BOOL sets it true and nothing ever clears it,
                        // since only real triggers auto-consume when a transition takes
                        // them. A stuck-true Release then blocks any transition
                        // conditioned on Release == false — which is how a bow animates
                        // once and never again. Bools are cleared manually below.
                        releaseIsTrigger = p.type == AnimatorControllerParameterType.Trigger;
                    }
                }
            }
            else if (debugBow)
            {
                Debug.Log("[PlayerBow] No Animator/controller on the bow — it will still fire, just without the draw animation.", this);
            }

            if (arrowPrefab == null)
                Debug.LogWarning("[PlayerBow] No arrowPrefab assigned — the bow will draw but never shoot.", this);
        }

        void OnEnable()
        {
            // Swapping TO the bow with the button already down must not instantly draw.
            suppressUntilRelease = Input.GetMouseButton(fireMouseButton);
        }

        void OnDisable() => CancelDraw(relax: true);

        void Update()
        {
            // One frame after a bool-authored Release was raised — long enough for the
            // Animator to have evaluated the transition, so it doesn't latch true and
            // block anything conditioned on Release == false.
            if (clearReleaseNextUpdate) ClearRelease();

            // Derived from state every frame rather than toggled at each transition —
            // Update has several early returns (carrying, not drawing, still holding), so
            // an event-driven toggle would miss paths and leave the mesh stuck on.
            SyncNockedArrow();

            if (Input.GetMouseButtonUp(fireMouseButton)) suppressUntilRelease = false;

            // Hands full: PlayerCarry owns LMB for throwing while carrying.
            if (carry != null && carry.IsCarrying) { CancelDraw(relax: true); return; }

            bool held = Input.GetMouseButton(fireMouseButton) && !suppressUntilRelease;

            if (!drawing)
            {
                if (held && Time.time >= readyAt && Cursor.lockState == CursorLockMode.Locked)
                    BeginDraw();
                return;
            }

            if (held)
            {
                draw = Mathf.Min(1f, draw + Time.deltaTime / Mathf.Max(0.01f, drawTime));
                if (controller != null) controller.moveScaleOverride = drawMoveScale;
                if (hasDrawAmount) animator.SetFloat(DrawAmountParam, draw);
                return;
            }

            // Released.
            if (draw >= minDrawToFire) Shoot();
            else
            {
                if (debugBow) Debug.Log($"[PlayerBow] released at {draw:0.00} — below {minDrawToFire:0.00}, relaxed.", this);
                OnDrawAborted?.Invoke();
                CancelDraw(relax: true);
            }
        }

        void BeginDraw()
        {
            drawing = true;
            draw = 0f;

            // Clear any STALE Release. A Unity trigger that no transition consumed stays
            // armed indefinitely, so a shot whose Release was never picked up (wrong
            // condition, no transition out of the fire state) leaves it primed — and the
            // NEXT draw then fires it the instant a valid transition appears, which reads
            // as "the draw animation only worked the first time".
            ClearRelease();

            if (hasDraw) animator.SetBool(DrawParam, true);
            if (hasDrawAmount) animator.SetFloat(DrawAmountParam, 0f);
            OnDrawStarted?.Invoke();
            if (debugBow) Debug.Log("[PlayerBow] draw started.", this);
        }

        void Shoot()
        {
            float d = draw;

            // Trigger BEFORE clearing Draw. Both land in the same frame either way, but
            // if the draw state also has a "Draw == false -> idle" transition, having
            // Release already set means the fire transition is available to win rather
            // than the graph having any chance to fall through to idle.
            FireRelease();

            CancelDraw(relax: false);
            readyAt = Time.time + shotCooldown;

            // Immediately, not on the next Update's sync — a frame showing the nocked
            // mesh alongside the projectile that just left is exactly the artifact this
            // whole thing exists to avoid.
            SyncNockedArrow();
            OnShot?.Invoke(d);

            if (arrowPrefab == null) return;

            // AIM from the camera, SPAWN at the bow. Aiming from the muzzle would send
            // arrows wherever the bow model happens to point, which drifts from the
            // crosshair as the viewmodel sways.
            Transform eye = aimSource != null ? aimSource : transform;
            Vector3 dir = eye.forward;
            Vector3 spawn = muzzle != null ? muzzle.position : eye.position + dir * 0.4f;

            GameObject go = Instantiate(arrowPrefab, spawn, Quaternion.LookRotation(dir));
            var arrow = go.GetComponent<Arrow>();
            float speed = Mathf.Lerp(minDrawSpeed, fullDrawSpeed, d);

            if (arrow != null) arrow.Fire(gameObject, dir * speed, d);
            else if (go.TryGetComponent(out Rigidbody rb)) rb.linearVelocity = dir * speed;

            if (debugBow) Debug.Log($"[PlayerBow] shot at draw {d:0.00} ({speed:0.#} m/s).", this);
        }

        /// <summary>
        /// Show the cosmetic arrow on the string while it should be there. Hidden the
        /// moment a shot leaves, so the nocked mesh and the real projectile are never
        /// both visible; with showNockedWhileIdle it returns once the cooldown elapses,
        /// which reads as pulling a fresh arrow.
        /// </summary>
        void SyncNockedArrow()
        {
            if (nockedArrow == null) return;

            bool show = drawing || (showNockedWhileIdle && Time.time >= readyAt);
            if (nockedArrow.activeSelf != show) nockedArrow.SetActive(show);
        }

        /// <summary>Signal the loose, whichever way Release is authored.</summary>
        void FireRelease()
        {
            if (!hasRelease || animator == null) return;

            if (releaseIsTrigger) animator.SetTrigger(ReleaseParam);
            else
            {
                // A Bool has to be cleared by hand, and NOT this frame — the Animator
                // needs to see it true once to take the transition. Cleared at the top of
                // the next Update.
                animator.SetBool(ReleaseParam, true);
                clearReleaseNextUpdate = true;
            }
        }

        /// <summary>Drop a stale Release so it can't fire the next draw prematurely.</summary>
        void ClearRelease()
        {
            if (!hasRelease || animator == null) return;
            if (releaseIsTrigger) animator.ResetTrigger(ReleaseParam);
            else animator.SetBool(ReleaseParam, false);
            clearReleaseNextUpdate = false;
        }

        void CancelDraw(bool relax)
        {
            if (drawing && relax && debugBow) Debug.Log("[PlayerBow] draw cancelled.", this);

            drawing = false;
            draw = 0f;
            if (hasDraw && animator != null) animator.SetBool(DrawParam, false);
            if (hasDrawAmount && animator != null) animator.SetFloat(DrawAmountParam, 0f);
            if (controller != null) controller.moveScaleOverride = 1f;
        }
    }
}
