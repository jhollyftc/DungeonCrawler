using UnityEngine;
using UnityEngine.AI;

namespace DungeonGen
{
    /// <summary>
    /// The NPC's DECISION layer. Every state is a handful of lines delegating to
    /// capability components (NpcLocomotion, NpcPerception; NpcCombat/Carry/Equipment
    /// later) — the brain never touches the NavMeshAgent or CharacterController
    /// directly. That shape is what lets a Unity Behavior tree drop in later against
    /// the identical capability API, so this FSM doubles as the integration test.
    ///
    /// Phase 2 states: Wander (idle→walk), Investigate (go check a noise/last-known
    /// spot), Alerted (target in view — approach and watch; combat is Phase 4).
    /// Perception INTERRUPTS wandering: seeing the target beats hearing it beats
    /// wandering, re-evaluated every frame.
    ///
    /// DETERMINISM (golden rule 4 boundary — deliberate, do NOT "fix"): generation
    /// is deterministic, runtime AI is not. Where an NPC spawns reproduces from
    /// (seed, depth); what it decides once alive uses UnityEngine.Random, because
    /// reproducing a fight would need deterministic physics and input replay.
    /// </summary>
    [RequireComponent(typeof(NpcLocomotion))]
    [DisallowMultipleComponent]
    public class NpcBrain : MonoBehaviour
    {
        [Header("Wander")]
        [Tooltip("Seconds idling between walks (random in range).")]
        public Vector2 idleTime = new Vector2(1.5f, 4f);
        [Tooltip("Give up on a walk after this many seconds — a stall shouldn't strand the NPC.")]
        public float walkTimeout = 45f;

        [Header("Investigate")]
        [Tooltip("Seconds spent looking around at a last-known spot before giving up (if awareness has faded).")]
        public float lookAroundTime = 3f;
        [Tooltip("Close enough to count as HAVING investigated (m). The investigate target is a single point and a whole group converges on it, so without a band they all fight to stand on the same tile: separation shoves them off, arrival flips false, they repath in, forever. This is the engageDistance equivalent for suspicion — big enough that the crowd settles into a ring around the spot rather than a scrum on it. Releases at x approachHysteresis, same as the alerted bands.")]
        public float investigateRadius = 2f;

        [Header("Alerted")]
        [Tooltip("Close to within this distance of a seen target before attacking. Should be a touch under MeleeAttack.range so swings connect.")]
        public float engageDistance = 1.4f;
        [Tooltip("Back off if crowded closer than this to the target (e.g. shoved in by other NPCs). Keeps a personal-space floor instead of letting the crowd compress directly onto the player. Should be meaningfully less than engageDistance or the NPC oscillates between the two states.")]
        public float tooCloseDistance = 0.8f;
        [Tooltip("How far (m) a retreat step aims to open back up when too close.")]
        public float retreatStepDistance = 1f;
        [Tooltip("HYSTERESIS. Once holding position, don't resume approaching until the target is engageDistance x THIS away. Without a gap, an NPC that boids-separation nudges a centimetre past engageDistance instantly repaths inward, gets pushed out again, and jitters forever — a ring of attackers can't all sit at engageDistance anyway (circumference / separationRadius caps how many fit), so the surplus oscillates. Must be > 1; raise it if a packed crowd still shuffles.")]
        [Min(1f)] public float approachHysteresis = 1.35f;
        [Tooltip("Don't recompute the path to a moving target until it has moved at least this far (m) from the last point we pathed to. A fresh SetDestination every frame is both a full path recalculation and a source of micro-jitter.")]
        public float repathThreshold = 0.5f;

        [Tooltip("Log state transitions and destination picks. Great while proving out perception.")]
        public bool debugBrain = true;

        enum State { WanderIdle, WanderWalk, Investigate, Alerted }
        State state = State.WanderIdle;
        float timer;
        Vector3 investigatePoint;

        // Alerted-state latches. holdingGround is the hysteresis memory (see
        // approachHysteresis); stoppedAgent keeps us from calling Agent.ResetPath every
        // frame while parked, and lastRepathTarget throttles path recomputation.
        bool holdingGround;
        bool stoppedAgent;
        bool retreating;        // latched: backing off until comfortably clear (see approachHysteresis)
        bool retreatPathIssued; // the retreat destination is already set — don't repath every frame
        bool atInvestigatePoint; // latched: within the investigate band (see investigateRadius)
        Vector3 lastRepathTarget;

        NpcLocomotion body;
        NpcPerception senses;
        MeleeAttack melee;      // optional — an unarmed observer NPC just watches
        DungeonVisualizer vis;

        void Awake()
        {
            body = GetComponent<NpcLocomotion>();
            senses = GetComponent<NpcPerception>();
            melee = GetComponent<MeleeAttack>();
            vis = FindObjectOfType<DungeonVisualizer>();
            timer = Random.Range(idleTime.x, idleTime.y);
        }

        void Update()
        {
            // Perception interrupts. Sight > sound > wander, checked every frame so
            // a goblin snaps to a target the instant it appears and drops back to
            // investigating the moment it loses sight.
            if (senses != null)
            {
                if (senses.CurrentTarget != null)
                {
                    if (state != State.Alerted) Enter(State.Alerted);
                }
                else if (senses.Awareness01 >= senses.investigateThreshold && senses.HasLastKnown)
                {
                    if (state != State.Investigate) Enter(State.Investigate);
                }
                else if (state == State.Investigate || state == State.Alerted)
                {
                    // Lost the thread and awareness has faded — back to wandering.
                    Enter(State.WanderIdle);
                }
            }

            switch (state)
            {
                case State.WanderIdle: TickWanderIdle(); break;
                case State.WanderWalk: TickWanderWalk(); break;
                case State.Investigate: TickInvestigate(); break;
                case State.Alerted: TickAlerted(); break;
            }
        }

        // ---------------- Wander ----------------

        void TickWanderIdle()
        {
            timer -= Time.deltaTime;
            if (timer <= 0f) PickWanderDestination();
        }

        void TickWanderWalk()
        {
            timer += Time.deltaTime;

            if (body.Agent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                if (debugBrain) Debug.LogWarning($"[NPC] {name}: path invalid — rerolling.", this);
                Enter(State.WanderIdle);
                return;
            }
            // Blocked is not failure — it usually means leaning on a door, which we
            // want to continue. Only the timeout gives up.
            if (timer > walkTimeout)
            {
                if (debugBrain) Debug.LogWarning($"[NPC] {name}: walk timed out (blocked={body.IsBlocked}) — rerolling.", this);
                Enter(State.WanderIdle);
                return;
            }
            if (body.HasArrived) Enter(State.WanderIdle);
        }

        void PickWanderDestination()
        {
            var gen = vis != null ? vis.Generator : null;
            if (gen == null || gen.Rooms.Count == 0) { timer = 1f; return; }

            for (int i = 0; i < 6; i++)
            {
                Room room = gen.Rooms[Random.Range(0, gen.Rooms.Count)];
                Vector3Int fc = room.InteriorFloorCell;
                Vector3 target = vis.transform.position + new Vector3(fc.x + 0.5f, fc.y, fc.z + 0.5f) * vis.cellSize;

                if (!NavMesh.SamplePosition(target, out NavMeshHit hit, vis.cellSize, body.NavFilter))
                {
                    if (debugBrain) Debug.LogWarning($"[NPC] {name}: no navmesh under {room.Type} at {fc}.", this);
                    continue;
                }
                if (!body.SetDestination(hit.position)) continue;

                if (debugBrain) Debug.Log($"[NPC] {name}: wandering to {room.Type} at {fc}.", this);
                state = State.WanderWalk;
                timer = 0f;
                return;
            }
            timer = 2f;
        }

        // ---------------- Investigate ----------------

        void TickInvestigate()
        {
            // The last-known spot can move (a fresh noise updates it) — retarget if
            // it drifted meaningfully.
            if ((senses.LastKnownPosition - investigatePoint).sqrMagnitude > 1f)
            {
                GoToInvestigatePoint();
                atInvestigatePoint = false;
                stoppedAgent = false;
            }

            // Arrival BAND with hysteresis, for exactly the reason TickAlerted has one.
            // HasArrived is a point test, and a group all pathing to the SAME point can
            // never satisfy it together: boids separation pushes them off, arrival flips
            // false, everyone repaths inward, separation pushes them off again. A radius
            // lets them settle into a ring around the spot; the hysteresis stops a
            // centimetre of drift re-triggering the approach.
            float dist = Vector3.Distance(transform.position, investigatePoint);
            if (atInvestigatePoint)
            {
                if (dist > investigateRadius * approachHysteresis)
                {
                    atInvestigatePoint = false;
                    GoToInvestigatePoint();   // shoved clear — re-approach ONCE, not per frame
                    stoppedAgent = false;
                }
            }
            // IsBlocked still counts as "close enough": wedged on a prop or leaning on a
            // door, an NPC should look around rather than grind forever (original rule).
            else if (dist <= investigateRadius || body.IsBlocked)
            {
                atInvestigatePoint = true;
            }

            if (!atInvestigatePoint)
            {
                timer = lookAroundTime; // reset the look-around clock until we arrive
                return;
            }

            // Close enough — park (ONCE; Stop() resets the agent's path, so calling it
            // every frame churns the agent) and look around while awareness bleeds off.
            // The perception interrupt at the top of Update pulls us out early if we see
            // or hear something new.
            if (!stoppedAgent)
            {
                body.Stop();
                stoppedAgent = true;
            }

            timer -= Time.deltaTime;
            if (timer <= 0f) Enter(State.WanderIdle);
        }

        void GoToInvestigatePoint()
        {
            investigatePoint = senses.LastKnownPosition;
            if (NavMesh.SamplePosition(investigatePoint, out NavMeshHit hit, vis != null ? vis.cellSize : 3f, body.NavFilter))
                body.SetDestination(hit.position);
        }

        // ---------------- Alerted ----------------

        void TickAlerted()
        {
            Transform t = senses.CurrentTarget;
            if (t == null) return; // interrupt handler will switch us out next frame

            body.FaceTowards(t.position);

            float dist = Vector3.Distance(transform.position, t.position);

            // Hysteresis band. Entering engage range LATCHES holdingGround, and only a
            // meaningfully larger gap releases it — so separation nudging an NPC a few
            // centimetres past engageDistance no longer triggers an immediate repath
            // inward (which separation then undoes, forever). A crowd physically cannot
            // all sit at engageDistance, so without this the surplus jitters in place.
            if (holdingGround)
            {
                if (dist > engageDistance * approachHysteresis) holdingGround = false;
            }
            else if (dist <= engageDistance)
            {
                holdingGround = true;
            }

            // Same deadband on the INNER boundary: latch on crossing tooCloseDistance,
            // release only once comfortably clear, so the crowd nudging an NPC across
            // that line doesn't start/stop a retreat every frame.
            if (retreating)
            {
                if (dist >= tooCloseDistance * approachHysteresis) retreating = false;
            }
            else if (dist < tooCloseDistance)
            {
                retreating = true;
                retreatPathIssued = false;
            }

            if (!holdingGround)
            {
                // Don't repath mid-swing — a goblin that walks while its hit is
                // landing looks (and plays) like it's skating.
                if (melee == null || !melee.IsSwinging)
                    Approach(t.position);
            }
            else if (retreating)
            {
                // Crowded in past the personal-space floor — step back out to
                // engage range instead of standing there while the pile compresses
                // onto the target. Still attacks; a real fighter keeps swinging
                // while giving ground, it doesn't just idle at point-blank.
                //
                // Latched with the same deadband as the approach side, and the path is
                // issued ONCE (re-issued only on arrival). Re-pathing every frame at the
                // tooCloseDistance boundary made NPCs flap between backing off and
                // parking, which is real forward/back motion — it drove the Animator's
                // VelocityZ across zero and flickered the walk-forward/back blend.
                if (melee == null || !melee.IsSwinging)
                {
                    if (!retreatPathIssued || body.HasArrived)
                    {
                        RetreatFrom(t.position);
                        retreatPathIssued = true;
                        stoppedAgent = false;
                    }
                }
                melee?.TryAttack();   // no-op while recovering/suppressed/absent
            }
            else
            {
                // Park. Stop() resets the agent's path, so calling it every frame both
                // churns the agent and re-triggers its braking each frame — latch it.
                if (!stoppedAgent)
                {
                    body.Stop();
                    stoppedAgent = true;
                }
                melee?.TryAttack();   // no-op while recovering/suppressed/absent
            }
        }

        /// <summary>Path toward a moving target, recomputing only once it has drifted
        /// repathThreshold from the point we last pathed to — a SetDestination every
        /// frame is a full path recalculation and adds its own micro-jitter.</summary>
        void Approach(Vector3 target)
        {
            stoppedAgent = false;
            if ((target - lastRepathTarget).sqrMagnitude < repathThreshold * repathThreshold) return;
            if (body.SetDestination(target)) lastRepathTarget = target;
        }

        /// <summary>Step directly away from a point, sampled back onto the navmesh. Used
        /// to reopen personal space when the crowd shoves an NPC past tooCloseDistance.</summary>
        void RetreatFrom(Vector3 from)
        {
            Vector3 away = transform.position - from;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = -transform.forward; // degenerate: stacked exactly on the target

            Vector3 desired = transform.position + away.normalized * retreatStepDistance;
            if (NavMesh.SamplePosition(desired, out NavMeshHit hit, retreatStepDistance + 0.5f, body.NavFilter))
                body.SetDestination(hit.position);
        }

        // ---------------- Transitions ----------------

        void Enter(State next)
        {
            if (debugBrain && next != state) Debug.Log($"[NPC] {name}: {state} → {next}.", this);
            state = next;

            switch (next)
            {
                case State.WanderIdle:
                    timer = Random.Range(idleTime.x, idleTime.y);
                    body.Stop();
                    break;
                case State.Investigate:
                    timer = lookAroundTime;
                    atInvestigatePoint = false;
                    stoppedAgent = false;
                    GoToInvestigatePoint();
                    break;
                case State.Alerted:
                    // Fresh latches: a re-alert must not inherit a stale hold or a
                    // lastRepathTarget from a previous engagement (which would suppress
                    // the first approach until the target happened to move far enough).
                    holdingGround = false;
                    stoppedAgent = false;
                    retreating = false;
                    retreatPathIssued = false;
                    lastRepathTarget = new Vector3(float.MinValue, 0f, float.MinValue);
                    break;
            }
        }

        void OnDrawGizmosSelected()
        {
            if (body == null || body.Agent == null || !body.Agent.hasPath) return;
            Gizmos.color = state == State.Alerted ? Color.red : state == State.Investigate ? Color.magenta : Color.cyan;
            Vector3 prev = transform.position;
            foreach (var corner in body.Agent.path.corners)
            {
                Gizmos.DrawLine(prev, corner);
                prev = corner;
            }
            Gizmos.DrawSphere(body.Agent.destination, 0.2f);
        }
    }
}
