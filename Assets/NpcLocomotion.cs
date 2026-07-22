using UnityEngine;
using UnityEngine.AI;

namespace DungeonGen
{
    /// <summary>
    /// The NPC's BODY: pathfinding brain (NavMeshAgent) driving a physical
    /// capsule (CharacterController). Movement capability only — it never
    /// decides WHERE to go. Brains (NpcBrain now, a behavior tree later) call
    /// SetDestination/Stop/FaceTowards and read HasArrived/IsBlocked.
    ///
    /// WHY THE HYBRID, and not a plain NavMeshAgent: an agent moves the transform
    /// directly, so it never fires OnControllerColliderHit — which is the callback
    /// CharacterControllerPhysicsPush uses to dispatch IPushable.Push. A bare agent
    /// therefore ghosts straight through physics doors, crates, and other NPCs.
    /// Driving a CharacterController instead means that component runs VERBATIM on
    /// NPCs: same pushForce, same speed scaling, same framerate normalization the
    /// player uses. One code path, one set of tuning, and it can never drift.
    ///
    /// THE CRUX is `agent.nextPosition = transform.position` — the agent follows
    /// the BODY, not the other way round. When the capsule is stopped by a closed
    /// door, the agent's internal position stops with it, so it keeps steering
    /// forward instead of sliding on ahead. The NPC LEANS on the door, and because
    /// push force scales with actual speed, a slow walker eases it open quietly
    /// (staying under PhysicsDoor's thunkArmAngle) while a charging one slams it.
    ///
    /// AUTHORING: set agent radius >= controller radius. The agent then plans
    /// around all BAKED geometry with margin, so the capsule only ever touches
    /// things the navmesh deliberately excludes — doors and dynamic props. Contact
    /// becomes meaningful instead of constant wall-scraping.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class NpcLocomotion : MonoBehaviour
    {
        [Header("Motion")]
        [Tooltip("Degrees per second the body turns to face its travel direction. The agent's own rotation is disabled — we steer the capsule, so we own the turn.")]
        public float turnSpeed = 540f;
        [Tooltip("Gravity (m/s²). A CharacterController does not fall on its own. Mostly it keeps the capsule pinned to stairs and ramps.")]
        public float gravity = -20f;

        [Header("Blocked detection")]
        [Tooltip("Below this actual speed (m/s), while the agent still WANTS to move, counts as blocked — leaning on a door or wedged on a prop. Brains use it to give up or shove harder.")]
        public float blockedSpeed = 0.35f;
        [Tooltip("Agent must want at least this much speed (m/s) before slow movement counts as blocked, so standing still on purpose never reads as stuck.")]
        public float blockedDesiredSpeed = 0.5f;

        [Header("Recovery")]
        [Tooltip("Gravity or a shove can drop the capsule off the navmesh. If that happens, warp back within this radius (meters). ~one cell is right.")]
        public float navRecoverRadius = 3f;
        [Tooltip("Fall this far (meters) below the last grounded height and the NPC is warped back onto the navmesh with a warning. Catches a capsule that spawned inside geometry, got shoved off a ledge, or found a hole — an NPC falling forever while still pathing is a silent, confusing failure.")]
        public float maxFallDistance = 6f;
        [Tooltip("Terminal velocity (m/s). Also stops a runaway fall from tunnelling through colliders before recovery can fire.")]
        public float maxFallSpeed = 25f;

        [Header("Knockback")]
        [Tooltip("Seconds an impulse (a hit, a blast) takes to decay to nothing.")]
        public float impulseDecay = 0.2f;

        [Header("Separation (NPC vs NPC spacing)")]
        [Tooltip("NPCs closer than this (m, center to center) push each other apart. Agent avoidance only separates agents IN TRANSIT — it does nothing for a crowd converging on the same target (everyone chasing the player) — and NPC-NPC collision is off to stop capsule-climbing, so THIS is what owns spacing. ~2x capsule radius + elbow room.")]
        public float separationRadius = 1.2f;
        [Tooltip("Push-apart speed (m/s) at full overlap, fading to zero at the radius edge. Keep below walk speed or the crowd vibrates.")]
        public float separationStrength = 2f;

        [Header("Push resistance (don't let the player shove NPCs around)")]
        [Tooltip("A microscopic NUMERICAL-NOISE guard only (m) — not a physical allowance. Whenever actual horizontal displacement exceeds intended by more than this, the FULL excess is corrected out (snap to exactly intended), never leaving this tolerance itself ungranted — so it can't compound into drift no matter how long contact continues. Keep at float-rounding scale.")]
        public float pushTolerance = 0.0005f;

        [Header("Debug")]
        [Tooltip("Log per-frame INTENDED vs ACTUAL horizontal displacement, broken down by want/impulse/separation — diagnostic for 'the player can push/jitter me' investigations. Turn on, stand pressed against this NPC, and read the console: if actual >> intended while want/impulse/sep all read ~0, that confirms CharacterController's own overlap resolution is the source, independent of anything this script asked for.")]
        public bool debugPush = false;

        public NavMeshAgent Agent { get; private set; }
        public CharacterController Controller { get; private set; }

        /// <summary>
        /// Actual horizontal speed (m/s) — what the body is really doing, not what the
        /// agent wanted. Deliberately NOT Controller.velocity: Unity computes that from
        /// the RAW pre-push-correction Move() result, so during a player-shove event it
        /// can report 20-40+ m/s for several frames even though RejectUnwantedPush has
        /// already cancelled the position back to net-zero that same frame (confirmed via
        /// debugPush logging — finalActual stayed 0.0000 throughout while ccVel spiked).
        /// NpcAnimatorDriver feeds this straight into the Animator's Speed parameter, so
        /// the phantom velocity was visually reading as the goblin "running in place" —
        /// legs cycling through a walk/run animation while the root stayed provably
        /// fixed. trueVelocity is OUR OWN measurement of actually-achieved displacement,
        /// computed after correction, so it can't lie this way.
        /// </summary>
        public float CurrentSpeed => new Vector3(trueVelocity.x, 0f, trueVelocity.z).magnitude;

        /// <summary>Reached the current destination (or has none).</summary>
        public bool HasArrived
        {
            get
            {
                if (Agent == null || !Agent.isOnNavMesh) return true;
                if (Agent.pathPending) return false;              // still computing — not an answer yet
                if (!Agent.hasPath) return true;                  // nothing to walk to
                // remainingDistance is Infinity on a partial/invalid path, so this
                // stays false rather than falsely reporting arrival.
                return Agent.remainingDistance <= Agent.stoppingDistance + arriveSlack;
            }
        }

        /// <summary>Wants to move but effectively isn't — leaning on a door, or wedged. THIS is the signal that the NPC is pushing something.</summary>
        public bool IsBlocked { get; private set; }

        /// <summary>Scales movement speed. Carry load, injury, stagger. 1 = normal.</summary>
        public float SpeedMultiplier { get; set; } = 1f;

        const float arriveSlack = 0.35f;

        float verticalVelocity;
        Vector3 impulse;
        float impulseTimer;
        float lastGroundedY;
        bool haveGroundedY;

        // Actual achieved horizontal velocity, measured AFTER RejectUnwantedPush —
        // see CurrentSpeed's doc for why this exists instead of reading
        // Controller.velocity directly.
        Vector3 trueVelocity;

        // All living NPC bodies, for separation (and, later, shouts — this is the
        // registry phase 5 wants). OnEnable/OnDisable keep it exact: death disables
        // NpcLocomotion, which removes the corpse from the crowd automatically.
        static readonly System.Collections.Generic.List<NpcLocomotion> All = new System.Collections.Generic.List<NpcLocomotion>();

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        void Awake()
        {
            Agent = GetComponent<NavMeshAgent>();
            Controller = GetComponent<CharacterController>();

            // We drive the body; the agent only plans. Set in code as well as on
            // the prefab so a fresh NPC can never be wired half-way and jitter
            // between two things both trying to move it.
            Agent.updatePosition = false;
            Agent.updateRotation = false;

            // Randomized avoidance priority: equal-priority agents split their
            // avoidance 50/50 and can deadlock or shove through each other in a
            // crowd; unequal priorities make one yield cleanly. Runtime AI —
            // deliberately not seeded.
            Agent.avoidancePriority = Random.Range(30, 70);

            if (Agent.radius < Controller.radius)
            {
                Debug.LogWarning(
                    $"[NPC] {name}: agent radius ({Agent.radius:0.00}) is smaller than the capsule " +
                    $"({Controller.radius:0.00}). The agent will plan paths the body can't fit through and " +
                    "grind along walls. Raise the agent radius to at least the controller's.", this);
            }

            // A base-origin model (feet at the transform origin — this project's
            // convention) needs center.y = height/2, or the capsule straddles the
            // origin and spawns half-buried in the floor: it never reads as
            // grounded and falls through the world while still pathing. Caught
            // here because the symptom is baffling and the cause is one field.
            float expectedCenterY = Controller.height * 0.5f;
            if (Mathf.Abs(Controller.center.y - expectedCenterY) > 0.1f)
            {
                Debug.LogWarning(
                    $"[NPC] {name}: CharacterController center.y is {Controller.center.y:0.00} but height is " +
                    $"{Controller.height:0.00}, so the capsule spans {Controller.center.y - expectedCenterY:0.00} to " +
                    $"{Controller.center.y + expectedCenterY:0.00} relative to the origin. For a base-origin model " +
                    $"set center.y = {expectedCenterY:0.00} so the capsule stands ON the origin.", this);
            }
        }

        void Update()
        {
            if (Agent == null || Controller == null) return;

            float dt = Time.deltaTime;

            // desiredVelocity already contains steering + local avoidance. It reads
            // zero while a path is still being computed — don't mistake that for
            // "blocked" or the NPC stutters at the start of every walk.
            Vector3 want = Agent.pathPending ? Vector3.zero : Agent.desiredVelocity * SpeedMultiplier;

            if (impulseTimer > 0f)
            {
                impulseTimer -= dt;
                impulse = Vector3.Lerp(Vector3.zero, impulse, Mathf.Clamp01(impulseTimer / Mathf.Max(0.01f, impulseDecay)));
                if (impulseTimer <= 0f) impulse = Vector3.zero;
            }

            // A CharacterController never falls by itself. The small downward bias
            // when grounded is the same trick the player controller uses to keep
            // isGrounded stable on stairs and ramps.
            if (Controller.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
            else verticalVelocity += gravity * dt;
            verticalVelocity = Mathf.Max(verticalVelocity, -maxFallSpeed);

            Vector3 sep = Separation();
            Vector3 horizontalIntent = (want + impulse + sep) * dt;
            Vector3 motion = horizontalIntent + Vector3.up * verticalVelocity * dt;

            Vector3 beforePos = transform.position;
            Controller.Move(motion);

            // ISOLATED-VARIABLE RETRY: the exact math from commit 919fe8b (snap fully
            // back to horizontalIntent's magnitude — never intended+tolerance, which
            // was THE compounding-drift bug) but WITHOUT 1b868da's Controller.enabled
            // toggle, which is the one variable that changed between "drift" (attempt 2,
            // no toggle) and "jitter" (attempt 3, with toggle). Testing whether the
            // toggle itself was the source of the jitter rather than the correction
            // concept. debugPush logs BOTH the pre-correction spike and the post-
            // correction result on the same line so this can be verified numerically.
            Vector3 rawAfterPos = transform.position;
            Vector3 rawActualHorizontal = new Vector3(rawAfterPos.x - beforePos.x, 0f, rawAfterPos.z - beforePos.z);
            float intendedMag = horizontalIntent.magnitude;
            float overshoot = rawActualHorizontal.magnitude - intendedMag;
            bool corrected = overshoot > pushTolerance;
            if (corrected)
            {
                Vector3 excess = rawActualHorizontal - rawActualHorizontal.normalized * intendedMag;
                transform.position -= excess;
            }

            // What ACTUALLY happened this frame, after correction — see CurrentSpeed's
            // doc comment for why this (not Controller.velocity) drives speed elsewhere.
            Vector3 finalPos = transform.position;
            Vector3 finalHorizontal = new Vector3(finalPos.x - beforePos.x, 0f, finalPos.z - beforePos.z);
            trueVelocity = dt > 0f ? finalHorizontal / dt : Vector3.zero;

            if (debugPush)
            {
                Debug.Log($"[NpcPush] {name}: intended={intendedMag:0.0000}m rawActual={rawActualHorizontal.magnitude:0.0000}m " +
                          $"finalActual={finalHorizontal.magnitude:0.0000}m corrected={corrected} " +
                          $"(want={want.magnitude:0.00} impulse={impulse.magnitude:0.00} sep={sep.magnitude:0.00}) " +
                          $"grounded={Controller.isGrounded} ccVel={Controller.velocity}", this);
            }

            IsBlocked = want.magnitude > blockedDesiredSpeed && CurrentSpeed < blockedSpeed;

            if (!CheckFall()) return;   // recovered this frame — skip the rest

            SyncAgentToBody();
            FaceMovement(want, dt);
        }

        /// <summary>
        /// Boids-style push-apart from every other living NPC within range —
        /// linear falloff, horizontal only, capped at separationStrength. Additive
        /// with pathing, so a crowd converging on the player spreads into a loose
        /// ring instead of a single occupied point. O(n) over live NPCs per NPC,
        /// which at this game's population (tens) is nothing.
        /// </summary>
        Vector3 Separation()
        {
            if (separationStrength <= 0f) return Vector3.zero;

            Vector3 push = Vector3.zero;
            for (int i = 0; i < All.Count; i++)
            {
                NpcLocomotion other = All[i];
                if (other == this) continue;

                Vector3 away = transform.position - other.transform.position;
                away.y = 0f;
                float dist = away.magnitude;
                if (dist >= separationRadius) continue;

                // Dead-center overlap (spawned inside each other): pick a stable
                // arbitrary direction rather than dividing by ~zero.
                Vector3 dir = dist > 0.01f ? away / dist : transform.right;
                push += dir * (1f - dist / separationRadius);
            }

            return Vector3.ClampMagnitude(push * separationStrength, separationStrength);
        }

        /// <summary>
        /// Catch a runaway fall. Returns false if we recovered this frame.
        ///
        /// Checking `Agent.isOnNavMesh` alone is NOT enough: we force-sync
        /// nextPosition to the body every frame, so the agent keeps believing it's
        /// on the mesh the whole way down and happily steers a falling NPC. This
        /// watches actual vertical drop instead, which catches every cause —
        /// spawning inside geometry, a shove off a ledge, a hole in the bake.
        /// </summary>
        bool CheckFall()
        {
            if (Controller.isGrounded)
            {
                lastGroundedY = transform.position.y;
                haveGroundedY = true;
                return true;
            }
            if (!haveGroundedY)
            {
                // Never touched down yet (e.g. spawned mid-air): treat the spawn
                // height as the reference so a bad spawn still recovers.
                lastGroundedY = transform.position.y;
                haveGroundedY = true;
                return true;
            }
            if (lastGroundedY - transform.position.y < maxFallDistance) return true;

            Debug.LogWarning(
                $"[NPC] {name} fell {lastGroundedY - transform.position.y:0.0}m below its last footing — " +
                "recovering onto the navmesh. Usual cause: the CharacterController's CENTER doesn't match its " +
                $"height (center.y should be height/2 = {Controller.height * 0.5f:0.00} for a base-origin model), " +
                "so the capsule starts sunk into the floor and never registers as grounded.", this);

            if (WarpToNavMesh(transform.position, Mathf.Max(navRecoverRadius, maxFallDistance)))
            {
                lastGroundedY = transform.position.y;
                return false;
            }

            // Couldn't find mesh nearby — stop accelerating downward so it doesn't
            // fall to infinity while we wait for a brain to reroute it.
            verticalVelocity = 0f;
            return true;
        }

        /// <summary>
        /// The agent follows the BODY. Without this the agent's internal position
        /// runs on ahead while the capsule is held up by a door, and it would
        /// happily consider itself already through — so it would stop pushing.
        /// </summary>
        void SyncAgentToBody()
        {
            if (Agent.isOnNavMesh)
            {
                Agent.nextPosition = transform.position;
                // The other half of driving an agent externally, and easy to miss:
                // avoidance (RVO) predicts neighbors from their VELOCITY, and an
                // externally-moved agent reports ~zero — every NPC tells every
                // other "I'm stationary", prediction collapses, and they walk
                // straight through each other. Feed the real velocity back — OUR
                // OWN measured trueVelocity, not Controller.velocity, which can
                // spike to 20-40+ m/s for several frames during a player-push event
                // even though the position was already corrected back to net-zero
                // that same frame (see CurrentSpeed's doc). Raw Controller.velocity
                // here would tell nearby NPCs' avoidance this one is sprinting away.
                Agent.velocity = trueVelocity;
                return;
            }

            // Gravity or a hard shove can drop the capsule off the mesh. Put it back
            // rather than leaving a permanently inert NPC standing in the geometry.
            if (!WarpToNavMesh(transform.position, navRecoverRadius))
                Agent.nextPosition = transform.position;
        }

        void FaceMovement(Vector3 want, float dt)
        {
            Vector3 flat = new Vector3(want.x, 0f, want.z);
            if (flat.sqrMagnitude < 0.0004f) return;

            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * dt);
        }

        // ---------------- Capability API (what brains call) ----------------

        /// <summary>Path to a world point. Returns false if it isn't on/near the navmesh.</summary>
        public bool SetDestination(Vector3 world)
        {
            if (Agent == null || !Agent.isOnNavMesh) return false;
            Agent.isStopped = false;
            return Agent.SetDestination(world);
        }

        public void Stop()
        {
            if (Agent == null || !Agent.isOnNavMesh) return;
            Agent.ResetPath();
            IsBlocked = false;
        }

        /// <summary>Turn to look at a point, ignoring pitch. For idling, talking, aiming a throw.</summary>
        public void FaceTowards(Vector3 world)
        {
            Vector3 flat = world - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0004f) return;
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Quaternion.LookRotation(flat.normalized, Vector3.up),
                turnSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Knockback etc. Decays over impulseDecay. Additive with pathing, so a hit
        /// shoves an NPC mid-stride. The VERTICAL component routes into gravity's
        /// velocity instead of the decaying impulse — an upward pop then follows a
        /// real ballistic arc (up, hang, fall) rather than being damped flat.
        /// </summary>
        public void AddImpulse(Vector3 velocity)
        {
            if (velocity.y > 0f)
            {
                verticalVelocity = Mathf.Max(verticalVelocity, 0f) + velocity.y;
                velocity.y = 0f;
            }
            impulse += velocity;
            impulseTimer = impulseDecay;
        }

        /// <summary>Teleport onto the nearest navmesh point. The CharacterController must be disabled across the move or it fights the warp.</summary>
        public bool WarpToNavMesh(Vector3 world, float maxDistance)
        {
            if (Agent == null) return false;
            if (!NavMesh.SamplePosition(world, out NavMeshHit hit, maxDistance, NavMesh.AllAreas)) return false;

            Controller.enabled = false;
            transform.position = hit.position;
            Controller.enabled = true;

            Agent.Warp(hit.position);
            verticalVelocity = 0f;
            return true;
        }

        void OnDrawGizmosSelected()
        {
            if (Agent == null || Controller == null) return;

            // Divergence between these two IS the visual signature of pushing:
            // the agent wants to go somewhere (yellow) and the body isn't (cyan).
            Vector3 p = transform.position + Vector3.up * 0.1f;
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(p, Agent.desiredVelocity);
            Gizmos.color = IsBlocked ? Color.red : Color.cyan;
            Vector3 v = Controller.velocity;
            Gizmos.DrawRay(p, new Vector3(v.x, 0f, v.z));
        }
    }
}
