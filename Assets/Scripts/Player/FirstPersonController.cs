using System;
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
        [Tooltip("How far off the ladder you may be looking to START climbing, in degrees. You climb a ladder facing it; without this, holding forward carried you up while looking sideways or fully backwards.\n\nMeasured on the HORIZONTAL heading only, so looking up at the opening you are climbing toward — or down at your feet — never breaks the climb. That is the natural thing to do on a ladder and gating on it would be maddening.")]
        [Range(10f, 170f)] public float ladderFacingAngle = 75f;
        [Tooltip("How far you must turn before you LET GO once climbing. Must exceed ladderFacingAngle: mouse look varies continuously, so a single threshold flaps on and off around the boundary and drops you repeatedly while you glance around — the same hysteresis NpcBrain's approach bands and TorchAudioPool's steal margin exist for.")]
        [Range(10f, 179f)] public float ladderReleaseAngle = 105f;

        [Header("Developer UI")]
        [Tooltip("Draws the control list in the top-right corner. Dev aid — turn it off for a real build.")]
        public bool showControls = true;
        [Tooltip("Font size for the dev overlay. The list has grown well past what 18 could fit; the box sizes itself to the text, so this is the only dial.")]
        [Range(8, 24)] public int overlayFontSize = 12;
        [Tooltip("Highest depth PgUp will climb to. A cap because grid size scales with depth — very high depths generate huge, slow dungeons.")]
        public int maxDebugDepth = 20;

        [Header("External impulse (dash / knockback)")]
        [Tooltip("How fast an AddImpulse velocity (e.g. a shield-bash lunge) decays, per second. Higher = a shorter, snappier burst. ~10 gives a ~0.15s lunge.")]
        public float externalDamping = 10f;

        [Header("Push resistance (don't let a crowd shove the player through walls)")]
        [Tooltip("A microscopic NUMERICAL-NOISE guard only (m) — not a physical allowance. Same fix as NpcLocomotion.RejectUnwantedPush, mirrored: CharacterController.Move() always resolves whatever overlap it finds itself in, regardless of requested motion. A crowd of NPCs converging on the player each run their OWN Move() every frame, and that overlap resolution against the player's capsule accumulates displacement with nothing to do with player input — enough simultaneous pushers in one frame can exceed wall thickness and tunnel the player through. Whenever actual horizontal displacement exceeds intended (input + external impulse) by more than this tolerance, the excess is corrected out.")]
        public float pushTolerance = 0.0005f;
        [Tooltip("Log per-frame intended vs actual horizontal displacement when it gets corrected — diagnostic for crowd-push investigations.")]
        public bool debugPush = false;

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

        /// <summary>
        /// The live decaying dash/knockback velocity (horizontal). Exposed so the shield
        /// bash's plow can carry NPCs at the player's ACTUAL current lunge speed rather
        /// than a copied constant — the carry then decays with the lunge automatically,
        /// one source of truth instead of two numbers that can drift apart.
        /// </summary>
        public Vector3 ExternalVelocity => externalVelocity;

        /// <summary>
        /// A SUSTAINED external velocity, re-asserted every frame by whoever owns it — a tether,
        /// a conveyor, a current. Folded into the same cc.Move as everything else, so it still
        /// collides and still composes with WASD.
        ///
        /// SEPARATE FROM AddImpulse BECAUSE THE TWO ARE DIFFERENT QUANTITIES, and faking this
        /// one with the other is a documented trap: `externalVelocity` DECAYS, which is what
        /// makes a hit read as a hit, so re-adding an impulse every frame silently resets that
        /// decay forever and reads as a bug to whoever finds it. NpcLocomotion split
        /// SetPlowVelocity out from AddImpulse for exactly this reason; this is the player's
        /// half of the same distinction.
        ///
        /// FRAME-STAMPED, NOT LATCHED: stop calling and it stops applying. Same contract as
        /// CameraKick.SetSustained and PlayerFov.AddOffset, and for the same reason — a driver
        /// that dies mid-effect (the player lets go, dies, the dungeon regenerates) must not be
        /// able to strand the player being dragged forever.
        /// </summary>
        public void SetSustainedVelocity(Vector3 velocity)
        {
            velocity.y = 0f;
            sustainedVelocity = velocity;
            sustainedFrame = Time.frameCount;
        }

        Vector3 sustainedVelocity;
        int sustainedFrame = -1;

        Vector3 ActiveSustained =>
            sustainedFrame >= Time.frameCount - 1 ? sustainedVelocity : Vector3.zero;

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

        /// <summary>
        /// The same intent as <see cref="IntendedSpeed"/> but with its DIRECTION kept — where
        /// the player is trying to go, before the world blocks it.
        ///
        /// Exists for the same reason the scalar does, and the reason generalises: anything
        /// measuring effort against something the player is PRESSED AGAINST must read intent,
        /// because achieved displacement collapses to zero exactly when they are leaning on it.
        /// That cost a real bug on doors (shouldering a stuck door delivered almost no torque)
        /// and again on crawlway grates, where hauling from arm's length at a wall produced no
        /// measurable movement at all.
        /// </summary>
        public Vector3 IntendedVelocity { get; private set; }

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
        static readonly RoomType[] warpRoomTypes = (RoomType[])Enum.GetValues(typeof(RoomType));

        /// <summary>Warp targets that are NOT room types, appended after them. Sewer chambers
        /// and crawlways are registry entries rather than RoomTypes (§4), and they are also the
        /// two spaces you cannot reach without breaking a grate and crawling — so they are worth
        /// far more on this list than most of the rooms are.</summary>
        static readonly string[] warpSpaces = { "Sewer Chamber", "Crawlway Mouth" };

        int warpTypeIndex;

        int WarpTargetCount => warpRoomTypes.Length + warpSpaces.Length;

        string WarpTargetLabel => warpTypeIndex < warpRoomTypes.Length
            ? warpRoomTypes[warpTypeIndex].ToString()
            : warpSpaces[warpTypeIndex - warpRoomTypes.Length];

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
            style.fontSize = overlayFontSize;
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

            // Home / End: cycle the warp-target RoomType; F2: teleport there. Lets
            // you jump straight to a Throne/Shrine/etc. to check its props/lighting
            // without walking the whole generated layout to find one.
            if (Input.GetKeyDown(KeyCode.Home)) warpTypeIndex = (warpTypeIndex - 1 + WarpTargetCount) % WarpTargetCount;
            if (Input.GetKeyDown(KeyCode.End)) warpTypeIndex = (warpTypeIndex + 1) % WarpTargetCount;
            if (Input.GetKeyDown(KeyCode.F2))
            {
                if (warpTypeIndex < warpRoomTypes.Length) WarpToRoomType(warpRoomTypes[warpTypeIndex]);
                else WarpToSpace(warpTypeIndex - warpRoomTypes.Length);
            }

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
            IntendedVelocity = new Vector3(horizontal.x, 0f, horizontal.z);
            IntendedSpeed = IntendedVelocity.magnitude;

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

            Vector3 horizontalIntent = (horizontal + externalVelocity + ActiveSustained) * Time.deltaTime;
            Vector3 beforePos = transform.position;
            cc.Move(horizontalIntent + Vector3.up * verticalVelocity * Time.deltaTime);

            // Reject any horizontal displacement beyond what input/impulse actually
            // asked for this frame — see the pushTolerance tooltip for why. Vertical
            // is left alone (gravity/jump/ladder resolution is legitimate).
            Vector3 afterPos = transform.position;
            Vector3 rawHorizontal = new Vector3(afterPos.x - beforePos.x, 0f, afterPos.z - beforePos.z);
            float intendedMag = horizontalIntent.magnitude;
            float overshoot = rawHorizontal.magnitude - intendedMag;
            if (overshoot > pushTolerance)
            {
                Vector3 excess = rawHorizontal - rawHorizontal.normalized * intendedMag;
                transform.position -= excess;
                if (debugPush)
                    Debug.Log($"[PlayerPush] intended={intendedMag:0.0000}m rawActual={rawHorizontal.magnitude:0.0000}m corrected excess={excess.magnitude:0.0000}m", this);
            }

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
        /// Teleport the player into the first room of the given type this seed —
        /// jump straight to a Throne/Shrine/etc. to check its props/lighting
        /// without walking the generated layout to find one. Same ground-snap
        /// approach as DungeonPlayerSpawner (RaycastAll + nearest-to-nominal-floor),
        /// so a warp lands exactly where a fresh spawn there would have.
        /// </summary>
        void WarpToRoomType(RoomType type)
        {
            if (dungeon == null || dungeon.Generator == null) return;

            Room room = null;
            foreach (var r in dungeon.Generator.Rooms)
                if (r.Type == type) { room = r; break; }

            if (room == null)
            {
                Debug.LogWarning($"[Warp] No {type} room this seed.");
                return;
            }

            WarpToCell(room.InteriorFloorCell, $"{type} room");
        }

        /// <summary>
        /// Warp to one of the spaces that is NOT a RoomType — sewer chambers and crawlways.
        ///
        /// They need their own path precisely because they are not rooms: they are registry
        /// entries whose cells are typed Hallway or left solid (§4), which is what lets a 1.5m
        /// bore exist at all. They also happen to be the two hardest places in the dungeon to
        /// reach on foot for testing — a chamber needs a grate broken and a tunnel crawled —
        /// which is exactly why they belong on this list.
        /// </summary>
        void WarpToSpace(int spaceIndex)
        {
            var gen = dungeon != null ? dungeon.Generator : null;
            if (gen == null) return;

            foreach (var cw in gen.Crawlways)
            {
                if (spaceIndex == 0)
                {
                    if (!cw.HasChamber) continue;

                    // Prefer a cell that ISN'T the entry tile — on a wide chamber that tile is a
                    // 1x1 vestibule, and landing in it puts you nose-first against the grate you
                    // came to look past.
                    Vector3Int target = cw.ChamberMouthCell;
                    foreach (var c in cw.ChamberCells)
                        if (c != cw.ChamberMouthCell) { target = c; break; }

                    WarpToCell(target, "sewer chamber");
                    return;
                }

                // CellA, not a bore cell: the mouth puts you in the room facing the grate, which
                // is what you want to inspect. Standing IN a bore would wedge a 1.8m capsule in
                // a 1.5m tube — the crouch is what makes crawlways passable, and a warp does not
                // crouch you.
                WarpToCell(cw.CellA, "crawlway mouth");
                return;
            }

            Debug.LogWarning($"[Warp] No {(spaceIndex == 0 ? "sewer chamber" : "crawlway")} this seed.");
        }

        /// <summary>Shared teleport, so every warp target snaps to the floor the same way.</summary>
        void WarpToCell(Vector3Int fc, string what)
        {
            Vector3 floorWorld = dungeon.transform.position + new Vector3(fc.x + 0.5f, fc.y, fc.z + 0.5f) * dungeon.cellSize;
            Vector3 dest = floorWorld + Vector3.up * (cc.height * 0.5f + 0.1f);

            Vector3 rayStart = floorWorld + Vector3.up * (dungeon.cellSize * 0.9f);
            var hits = Physics.RaycastAll(rayStart, Vector3.down, dungeon.cellSize * 3f);
            if (hits.Length > 0)
            {
                RaycastHit best = hits[0];
                float bestDelta = Mathf.Abs(best.point.y - floorWorld.y);
                for (int i = 1; i < hits.Length; i++)
                {
                    float delta = Mathf.Abs(hits[i].point.y - floorWorld.y);
                    if (delta < bestDelta) { best = hits[i]; bestDelta = delta; }
                }
                dest = best.point + Vector3.up * (cc.height * 0.5f + 0.05f);
            }

            // Toggling the CharacterController off/on around the position write is
            // the safe way to teleport it — writing transform.position on an enabled
            // CC still lets its next Move() re-resolve overlap against the OLD
            // position's residual state.
            cc.enabled = false;
            transform.position = dest;
            cc.enabled = true;
            externalVelocity = Vector3.zero;
            verticalVelocity = 0f;

            Debug.Log($"[Warp] Warped to {what} at {dest}");
        }

        /// <summary>Which room (by type) the player is standing in right now, for the dev
        /// overlay. Reads PlayerRoomTracker — the SINGLE source shared with the fog, the
        /// map and the audio systems, so the readout cannot drift from what they act on.
        /// It used to compute the cell itself; that made the readout a second opinion
        /// rather than the same one.</summary>
        string CurrentRoomLabel()
        {
            if (dungeon == null || dungeon.Generator == null) return "-";
            if (roomTracker == null) roomTracker = dungeon.GetComponent<PlayerRoomTracker>();
            if (roomTracker == null || !roomTracker.HasPlayer) return "-";
            roomTracker.Refresh();
            Room room = roomTracker.CurrentRoom;
            return room != null ? room.Type.ToString() : "Hallway";
        }
        PlayerRoomTracker roomTracker;

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
                ? $"Seed: {dungeon.seed}\nDepth: {dungeon.config.depth}\nRoom: {CurrentRoomLabel()}\n" +
                  $"Warp Target: {WarpTargetLabel}\n"
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
                "1 / 2 - Sword+Shield / Bow\n" +
                "LMB (hold) - Light Attack / Draw Bow\n" +
                "LMB (carrying) - Throw (winds up)\n" +
                "RMB (hold) - Shield Block / tap to Parry\n" +
                "MMB (hold, release) - Heavy Attack\n" +
                "Q (hold, release) - Shield Bash\n" +
                "\n" +
                "Debug\n" +
                "---------\n" +
                "F1 - New Dungeon (same depth)\n" +
                "PgUp/PgDn - Depth +/- (same seed)\n" +
                "Home/End - Warp Target Room +/-\n" +
                "F2 - Warp to Target Room\n" +
                "M - Map\n" +
                "P - Path to Exit\n" +
                "F3 - NPC Awareness  F4/F5 - Sight/Hearing off\n" +
                "F6 - Melee Reticle  F7 - Audio Voices\n" +
                "K / L - Damage Nearest NPC / Self\n" +
                "Esc - Quit";

            // SIZED FROM THE TEXT, not from authored numbers. A hardcoded Rect is a second
            // thing to keep in sync with the list, and it fails in the same silent way: add
            // a key, and the last line simply vanishes off the bottom. CalcSize honours the
            // font size too, so the size dial needs no matching box change.
            //
            // THE LIST ITSELF IS STILL AUTHORED TEXT with no link to the input code, so
            // nothing catches it drifting - it had already fallen behind the melee rebind
            // once, and by the next check was missing the weapon swap (a real gameplay
            // binding) plus seven debug keys that existed and were undiscoverable. Add the
            // line when you add the key.
            Vector2 size = style.CalcSize(new GUIContent(text));
            GUI.Label(new Rect(Screen.width - size.x - 10f, 10f, size.x, size.y), text, style);
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

            // Any zone we are FACING wins. A pit can put two ladders near each other, so pick
            // the one the player is actually addressing rather than whichever the overlap
            // returned first.
            LadderClimbZone best = null;
            float bestAngle = 999f;
            for (int i = 0; i < n; i++)
            {
                var hit = ladderHits[i];
                if (hit == null || !hit.isTrigger) continue;
                var zone = hit.GetComponentInParent<LadderClimbZone>();
                if (zone == null) continue;

                float angle = FacingAngleTo(zone);
                if (angle < bestAngle) { best = zone; bestAngle = angle; }
            }

            if (best == null) { climbing = false; return false; }

            // HYSTERESIS, not a single threshold. The heading varies continuously with the
            // mouse, so one boundary flaps on and off while you glance around — and here that
            // does not merely stutter, it DROPS YOU OFF THE LADDER. Enter strict, leave loose.
            float limit = climbing ? ladderReleaseAngle : ladderFacingAngle;
            climbing = bestAngle <= limit;
            return climbing;
        }

        /// <summary>
        /// Angle between where the player is heading and the direction a climber must face.
        ///
        /// HORIZONTAL ONLY. `transform.forward` is already yaw-only (pitch lives on the camera),
        /// but the zone's facing is flattened too so a ladder authored with any tilt still
        /// compares cleanly. Looking up at the opening above you is the natural thing to do
        /// while climbing and must never break the grip.
        /// </summary>
        float FacingAngleTo(LadderClimbZone zone)
        {
            if (!zone.HasFacing) return 0f;   // unset (hand-placed ladder): rule stands down
            Vector3 want = zone.FaceDirection;
            want.y = 0f;
            if (want.sqrMagnitude < 0.0001f) return 0f;
            return Vector3.Angle(transform.forward, want.normalized);
        }

        bool climbing;
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
