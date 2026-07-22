using UnityEngine;
using UnityEngine.SceneManagement;

namespace DungeonGen
{
    /// <summary>
    /// Minimal first-person controller. WASD to move, mouse to look,
    /// Space to jump, Left Shift to sprint. Escape releases the cursor,
    /// left-click recaptures it. Legacy Input Manager (same note as FlyCamera:
    /// set Active Input Handling to "Both" if you're on the new Input System).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour, IMoveIntent
    {
        public Transform cam;
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 8f;
        public float jumpHeight = 1.1f;
        public float gravity = -20f;
        public float lookSensitivity = 2.2f;
        public float maxSlopeAngle = 45f;
        public float maxStepHeight = 0.5f;

        [Header("Crouch / sneak")]
        [Tooltip("Hold to crouch. Moving slowly means you shove physics objects gently — a crouched player eases a door open instead of banging it, so it stays under the door's noise threshold. Stealth falls out of the physics rather than being special-cased.")]
        public KeyCode crouchKey = KeyCode.LeftControl;
        public KeyCode crouchMouseButton = KeyCode.Mouse4;
        [Tooltip("Move speed while crouched. Keep it well under walkSpeed — this is what makes doors silent.")]
        public float crouchSpeed = 1.6f;
        [Tooltip("Capsule height while crouched (standing height is taken from the CharacterController at Awake).")]
        public float crouchHeight = 1.1f;
        [Tooltip("How fast the capsule/camera ease between stand and crouch.")]
        public float crouchTransitionSpeed = 8f;
        [Tooltip("What counts as a ceiling when checking whether you can stand back up. Exclude the Player layer.")]
        public LayerMask ceilingMask = ~0;

        [Header("Ladder climbing")]
        [Tooltip("Vertical speed while inside a LadderClimbZone (W up, S down).")]
        public float climbSpeed = 3f;
        [Tooltip("Horizontal speed multiplier while climbing — enough to adjust sideways or step off, not enough to sprint mid-air.")]
        [Range(0f, 1f)] public float climbHorizontalDamp = 0.35f;

        [Header("Developer UI")]
        [Tooltip("Draws the control list in the top-right corner. Dev aid — turn it off for a real build.")]
        public bool showControls = true;
        [Tooltip("Highest depth PgUp will climb to. A cap because grid size scales with depth — very high depths generate huge, slow dungeons.")]
        public int maxDebugDepth = 20;

        [Header("External impulse (dash / knockback)")]
        [Tooltip("How fast an AddImpulse velocity (e.g. a shield-bash lunge) decays, per second. Higher = a shorter, snappier burst. ~10 gives a ~0.15s lunge.")]
        public float externalDamping = 10f;

        /// <summary>External move-speed multiplier (1 = normal). Set by e.g. PlayerMelee to slow the player while charging a heavy swing. Reset to 1 when done.</summary>
        public float moveScaleOverride { get; set; } = 1f;

        /// <summary>
        /// Add a one-shot horizontal velocity that decays over the next moment — a
        /// dash/lunge/knockback. Folded into the same CharacterController.Move as normal
        /// movement, so it still collides (you can't lunge through a wall) and composes
        /// with WASD. Vertical is ignored; use jump/gravity for that.
        /// </summary>
        public void AddImpulse(Vector3 velocity)
        {
            velocity.y = 0f;
            externalVelocity += velocity;
        }

        /// <summary>True while crouched. Read by anything that cares how quiet the player is (future NPC alerting).</summary>
        public bool IsCrouching { get; private set; }
        /// <summary>Current horizontal speed (m/s). Physics pushes scale off this, so how hard you shove things follows how fast you're actually moving.</summary>
        public float HorizontalSpeed => new Vector3(cc.velocity.x, 0f, cc.velocity.z).magnitude;
        /// <summary>Grounded this frame. Head bob reads it so the camera doesn't bob mid-air. Flickers on step descents (see PlayerFootsteps' coyote time), so smooth anything that keys off it.</summary>
        public bool IsGrounded => cc != null && cc.isGrounded;

        /// <summary>
        /// INTENDED horizontal speed (m/s) this frame — input direction × the current
        /// speed (walk/sprint/crouch), BEFORE the world blocks it. The push system reads
        /// this so shouldering a stuck door still delivers a real shove (see IMoveIntent):
        /// achieved velocity drops to ~0 against a door, but intent stays high while you
        /// keep walking into it. Crouch lowers it, so sneaking still eases doors gently.
        /// </summary>
        public float IntendedSpeed { get; private set; }

        CharacterController cc;
        Vector3 externalVelocity;   // decaying dash/knockback velocity (horizontal), driven by AddImpulse
        float pitch;
        float verticalVelocity;
        float standHeight;
        Vector3 standCenter;
        float standCamY;
        static readonly Collider[] ladderHits = new Collider[8];
        private GUIStyle style;
        private DungeonVisualizer dungeon;
        private PlayerCarry carry;
        private Health health;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.slopeLimit = maxSlopeAngle;
            cc.stepOffset = maxStepHeight;
            carry = GetComponent<PlayerCarry>();
            health = GetComponent<Health>();

            standHeight = cc.height;
            standCenter = cc.center;
            if (cam != null) standCamY = cam.localPosition.y;
        }

        void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            style = new GUIStyle();
            style.fontSize = 18;
            style.normal.textColor = Color.white;
            style.alignment = TextAnchor.UpperRight;

            // For the dev overlay's seed readout. The seed is randomized at
            // generate time (randomizeSeedOnGenerate), so the overlay reads it
            // live from the visualizer rather than caching a number that goes
            // stale the moment someone presses F1 for a new dungeon.
            dungeon = FindObjectOfType<DungeonVisualizer>();
        }

        void Update()
        {
            // F1: new dungeon at the SAME depth (carry the current runtime depth
            // across the reload; seed re-randomizes per the visualizer's setting).
            if (Input.GetKeyDown(KeyCode.F1))
            {
                if (dungeon != null) DungeonVisualizer.PendingDepth = dungeon.config.depth;
                ReloadScene();
            }
            // PgUp / PgDn: change depth but keep the SAME seed, so you can watch
            // one seed grow/shrink with depth. Pinning the seed is what makes the
            // comparison meaningful rather than just a different random dungeon.
            if (Input.GetKeyDown(KeyCode.PageUp)) ChangeDepth(+1);
            if (Input.GetKeyDown(KeyCode.PageDown)) ChangeDepth(-1);

            // Cursor capture.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            // Look. A heavy carry drags the turn rate down too, so the whole body
            // reads as loaded — same mass signal as the move-speed penalty.
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                float look = lookSensitivity;
                if (carry != null) look *= carry.CarryTurnMultiplier;
                transform.Rotate(0f, Input.GetAxis("Mouse X") * look, 0f);
                pitch -= Input.GetAxis("Mouse Y") * look;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
                if (cam != null)
                    cam.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }

            UpdateCrouch();

            // Move.
            Vector3 input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            input = Vector3.ClampMagnitude(input, 1f);
            float speed = IsCrouching ? crouchSpeed
                        : Input.GetKey(KeyCode.LeftShift) ? sprintSpeed
                        : walkSpeed;
            // Carrying something heavy drags you down — mass is the one dial for
            // weight across carry lag, throw force, and now movement.
            if (carry != null) speed *= carry.CarrySpeedMultiplier;
            speed *= moveScaleOverride;   // e.g. charging a heavy swing
            Vector3 horizontal = transform.TransformDirection(input) * speed;

            // How hard we're TRYING to move (before the world blocks it) — the push
            // system scales its shove by this, not achieved velocity, so leaning on a
            // stuck door still delivers torque. Zero when giving no input.
            IntendedSpeed = new Vector3(horizontal.x, 0f, horizontal.z).magnitude;

            if (OnLadder())
            {
                // Climb: gravity off, W/S map to up/down, horizontal damped
                // (enough to adjust or step off at the top). Exiting the zone
                // — cresting the opening or stepping away at the bottom —
                // returns to normal movement automatically.
                verticalVelocity = input.z * climbSpeed;
                horizontal *= climbHorizontalDamp;
            }
            else
            {
                // Gravity & jump.
                if (cc.isGrounded)
                {
                    verticalVelocity = -2f; // small downward stick so isGrounded stays reliable on ramps
                    if (Input.GetKeyDown(KeyCode.Space))
                        verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
                }
                verticalVelocity += gravity * Time.deltaTime;
            }

            cc.Move((horizontal + externalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);

            // The dash/knockback burst bleeds off exponentially (frame-rate independent).
            externalVelocity *= Mathf.Exp(-externalDamping * Time.deltaTime);

            if (Input.GetKeyDown(KeyCode.Escape))
                Quit();
        }

        /// <summary>Reload the active scene — the dungeon rebuilds from the (possibly overridden) seed/depth.</summary>
        void ReloadScene() => SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);

        /// <summary>Bump run depth by delta, pin the current seed, and rebuild.</summary>
        void ChangeDepth(int delta)
        {
            if (dungeon == null) return;
            int next = Mathf.Clamp(dungeon.config.depth + delta, 1, Mathf.Max(1, maxDebugDepth));
            if (next == dungeon.config.depth) return; // already at the clamp — nothing to rebuild

            DungeonVisualizer.PendingDepth = next;
            DungeonVisualizer.PendingSeed = dungeon.seed; // same seed, new depth
            ReloadScene();
        }

        /// <summary>
        /// Developer control list. Unity finds OnGUI by reflecting over the CLASS,
        /// so this has to live at class scope — nested inside Update() it's just an
        /// unused local function, which compiles clean and never runs.
        /// </summary>
        void OnGUI()
        {
            if (!showControls || style == null) return;

            // Seed + depth first, so a tester who hits an edge case can read the
            // exact (seed, depth) that produced it straight off the screen — the
            // dungeon is a pure function of those two, so that pair reproduces it.
            string header = dungeon != null
                ? $"Seed: {dungeon.seed}\nDepth: {dungeon.config.depth}\n"
                : "";
            if (health != null)
                header += $"HP: {health.Current:0}/{health.max:0}\n";
            if (header.Length > 0) header += "\n";

            string text = header +
                "Controls\n" +
                "---------\n" +
                "WASD - Move\n" +
                "Mouse - Look\n" +
                "Space - Jump\n" +
                "Shift - Sprint\n" +
                "Ctrl / Mouse4 - Crouch\n" +
                "E - Interact / Pick up / Drop\n" +
                "LMB - Throw\n" +
                "F1 - New Dungeon (same depth)\n" +
                "PgUp/PgDn - Depth +/- (same seed)\n" +
                "Esc - Quit";

            GUI.Label(new Rect(Screen.width - 260f, 10f, 250f, 300f), text, style);
        }

        /// <summary>
        /// Hold-to-crouch. Shrinks the capsule from the TOP (feet stay put) and
        /// drops the camera with it. Standing back up is blocked while something
        /// is overhead, so you can't clip through a low ceiling by releasing.
        /// </summary>
        void UpdateCrouch()
        {
            bool wantCrouch = Input.GetKey(crouchKey) || Input.GetKey(crouchMouseButton);

            // Can't stand up under a ceiling — stay crouched until it's clear.
            if (!wantCrouch && IsCrouching && CeilingBlocked())
                wantCrouch = true;

            IsCrouching = wantCrouch;

            float targetHeight = IsCrouching ? crouchHeight : standHeight;
            if (!Mathf.Approximately(cc.height, targetHeight))
            {
                float h = Mathf.MoveTowards(cc.height, targetHeight,
                                            crouchTransitionSpeed * Time.deltaTime * standHeight);
                float shrink = standHeight - h;

                cc.height = h;
                // Lower the centre by half the shrink so the capsule's FEET stay
                // planted and only the head comes down.
                cc.center = new Vector3(standCenter.x, standCenter.y - shrink * 0.5f, standCenter.z);

                if (cam != null)
                {
                    Vector3 p = cam.localPosition;
                    p.y = standCamY - shrink;
                    cam.localPosition = p;
                }
            }
        }

        /// <summary>Is there something directly overhead blocking a stand-up?</summary>
        bool CeilingBlocked()
        {
            float needed = standHeight - cc.height;
            if (needed <= 0.01f) return false;

            // Cast up from the top of the crouched capsule.
            float radius = Mathf.Max(0.05f, cc.radius - 0.05f);
            Vector3 top = transform.position + cc.center + Vector3.up * (cc.height * 0.5f - cc.radius);
            return Physics.SphereCast(top, radius, Vector3.up, out _, needed + 0.1f,
                                      ceilingMask, QueryTriggerInteraction.Ignore);
        }

        // Polled each frame rather than relying on OnTriggerEnter/Exit —
        // trigger callbacks can miss exits on teleports/regeneration, and a
        // small overlap probe against the capsule's center is trivially cheap.
        bool OnLadder()
        {
            Vector3 probe = transform.position + cc.center;
            int n = Physics.OverlapSphereNonAlloc(probe, cc.radius + 0.25f, ladderHits,
                                                  ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hit = ladderHits[i];
                if (hit != null && hit.isTrigger && hit.GetComponentInParent<LadderClimbZone>() != null)
                    return true;
            }
            return false;
        }
        void Quit()
            {
        #if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
        #else
                Application.Quit();
        #endif
            }
        
    }
}
