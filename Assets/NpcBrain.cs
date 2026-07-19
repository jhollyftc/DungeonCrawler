using UnityEngine;
using UnityEngine.AI;

namespace DungeonGen
{
    /// <summary>
    /// The NPC's DECISION layer. Phase 1 is deliberately just Idle/Wander —
    /// Patrol/Investigate/Chase/Attack/Flee/Rearm land once perception and combat
    /// exist. What matters now is the SHAPE: every state is a handful of lines
    /// that delegate to capability components (NpcLocomotion today; NpcPerception,
    /// NpcCombat, NpcCarry, NpcEquipment later). The brain never touches the
    /// NavMeshAgent or the CharacterController directly.
    ///
    /// That split is what makes this FSM non-throwaway. A behavior tree swapped in
    /// later calls the IDENTICAL capability API, so this doubles as the integration
    /// test proving that API is complete before committing to a graph format.
    ///
    /// DETERMINISM (golden rule 4 boundary — deliberate, do not "fix"):
    /// generation is deterministic, runtime AI is NOT. Where an NPC spawns comes
    /// from the seeded pipeline and reproduces from (seed, depth). What it decides
    /// once alive uses UnityEngine.Random on purpose — reproducing a fight would
    /// need deterministic physics and input replay, which is not a goal.
    ///
    /// Destinations are whole ROOMS from the generator rather than points near the
    /// NPC: that forces long cross-dungeon paths through corridors, stairs, and
    /// doors, which is exactly what needs stress-testing. Unreachable rooms are
    /// logged and rerolled so nav gaps surface in the Console instead of as an NPC
    /// frozen in a corner.
    /// </summary>
    [RequireComponent(typeof(NpcLocomotion))]
    [DisallowMultipleComponent]
    public class NpcBrain : MonoBehaviour
    {
        [Header("Wander")]
        [Tooltip("Seconds idling between walks (random in range) — a 'pausing to look around' rhythm instead of relentless pacing.")]
        public Vector2 idleTime = new Vector2(1.5f, 4f);
        [Tooltip("Give up on a walk after this many seconds. A door shoved shut or a physics pile can stall a path; rerolling beats standing there forever.")]
        public float walkTimeout = 45f;

        [Tooltip("Log destination picks, unreachable rooms, and stalls. Leave ON while proving out navigation — an unreachable room is a nav bug worth knowing about.")]
        public bool debugBrain = true;

        enum State { Idle, Walking }
        State state = State.Idle;
        float timer;

        NpcLocomotion body;
        DungeonVisualizer vis;

        void Awake()
        {
            body = GetComponent<NpcLocomotion>();
            vis = FindObjectOfType<DungeonVisualizer>();
            timer = Random.Range(idleTime.x, idleTime.y);
        }

        void Update()
        {
            switch (state)
            {
                case State.Idle: TickIdle(); break;
                case State.Walking: TickWalking(); break;
            }
        }

        void TickIdle()
        {
            timer -= Time.deltaTime;
            if (timer <= 0f) PickDestination();
        }

        void TickWalking()
        {
            timer += Time.deltaTime;

            if (body.Agent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                if (debugBrain) Debug.LogWarning($"[NPC] {name}: path went INVALID — rerolling.", this);
                EnterIdle();
                return;
            }

            // Blocked is NOT a failure — it usually means the NPC is leaning on a
            // door, which is exactly what we want it to keep doing. Only the
            // timeout gives up, so a genuinely stuck NPC recovers while a
            // door-shoving one is left alone to finish the job.
            if (timer > walkTimeout)
            {
                if (debugBrain)
                    Debug.LogWarning($"[NPC] {name}: walk timed out after {walkTimeout}s at {transform.position} " +
                                     $"(blocked={body.IsBlocked}) — rerolling.", this);
                EnterIdle();
                return;
            }

            if (body.HasArrived) EnterIdle();
        }

        void EnterIdle()
        {
            state = State.Idle;
            timer = Random.Range(idleTime.x, idleTime.y);
            body.Stop();
        }

        void PickDestination()
        {
            var gen = vis != null ? vis.Generator : null;
            if (gen == null || gen.Rooms.Count == 0)
            {
                timer = 1f; // dungeon not built yet — check back shortly
                return;
            }

            for (int i = 0; i < 6; i++)
            {
                Room room = gen.Rooms[Random.Range(0, gen.Rooms.Count)];
                Vector3Int fc = room.InteriorFloorCell;
                Vector3 target = vis.transform.position +
                                 new Vector3(fc.x + 0.5f, fc.y, fc.z + 0.5f) * vis.cellSize;

                if (!NavMesh.SamplePosition(target, out NavMeshHit hit, vis.cellSize, NavMesh.AllAreas))
                {
                    if (debugBrain)
                        Debug.LogWarning($"[NPC] {name}: no navmesh under {room.Type} at {fc} — is that room connected?", this);
                    continue;
                }
                if (!body.SetDestination(hit.position)) continue;

                if (debugBrain) Debug.Log($"[NPC] {name}: wandering to {room.Type} at {fc}.", this);
                state = State.Walking;
                timer = 0f;
                return;
            }

            timer = 2f; // nothing reachable this round — idle and retry
        }

        void OnDrawGizmosSelected()
        {
            if (body == null || body.Agent == null || !body.Agent.hasPath) return;
            Gizmos.color = Color.cyan;
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
