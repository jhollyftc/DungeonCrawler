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

        [Header("Alerted")]
        [Tooltip("Close to within this distance of a seen target before attacking. Should be a touch under MeleeAttack.range so swings connect.")]
        public float engageDistance = 1.4f;

        [Tooltip("Log state transitions and destination picks. Great while proving out perception.")]
        public bool debugBrain = true;

        enum State { WanderIdle, WanderWalk, Investigate, Alerted }
        State state = State.WanderIdle;
        float timer;
        Vector3 investigatePoint;

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

                if (!NavMesh.SamplePosition(target, out NavMeshHit hit, vis.cellSize, NavMesh.AllAreas))
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
                GoToInvestigatePoint();

            if (!body.HasArrived && !body.IsBlocked)
            {
                timer = lookAroundTime; // reset the look-around clock until we arrive
                return;
            }

            // Arrived (or wedged close enough) — look around while awareness bleeds
            // off. The perception interrupt at the top of Update pulls us out early
            // if we see or hear something new.
            timer -= Time.deltaTime;
            if (timer <= 0f) Enter(State.WanderIdle);
        }

        void GoToInvestigatePoint()
        {
            investigatePoint = senses.LastKnownPosition;
            if (NavMesh.SamplePosition(investigatePoint, out NavMeshHit hit, vis != null ? vis.cellSize : 3f, NavMesh.AllAreas))
                body.SetDestination(hit.position);
        }

        // ---------------- Alerted ----------------

        void TickAlerted()
        {
            Transform t = senses.CurrentTarget;
            if (t == null) return; // interrupt handler will switch us out next frame

            body.FaceTowards(t.position);

            float dist = Vector3.Distance(transform.position, t.position);
            if (dist > engageDistance)
            {
                // Don't repath mid-swing — a goblin that walks while its hit is
                // landing looks (and plays) like it's skating.
                if (melee == null || !melee.IsSwinging)
                    body.SetDestination(t.position);
            }
            else
            {
                body.Stop();
                melee?.TryAttack();   // no-op while recovering/suppressed/absent
            }
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
                    GoToInvestigatePoint();
                    break;
                case State.Alerted:
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
