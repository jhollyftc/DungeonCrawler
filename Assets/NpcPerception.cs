using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The NPC's SENSES: hearing (via the NoiseBus) and sight (view cone + line of
    /// sight). A capability component — it reports what the NPC knows and never
    /// decides what to do about it; NpcBrain reads CurrentTarget / LastKnownPosition
    /// / Awareness01 and acts.
    ///
    /// Awareness01 is a METER, not a boolean, which is what gives suspicious →
    /// investigate → hunt from a single number: a distant noise nudges it, a clear
    /// line of sight drives it up fast, and it decays when nothing is sensed. Sight
    /// is TICKED on a stagger (never every frame) so a room full of goblins isn't a
    /// room full of per-frame raycasts.
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcPerception : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("This NPC's faction. It reacts to noises/targets NOT of this faction — so goblins ignore each other's footsteps (Faction.Dungeon) but hear the player and thrown props.")]
        public Faction faction = Faction.Dungeon;

        [Header("Hearing")]
        [Tooltip("Hearing radius (m) for a FULL-loudness (1.0) noise. A quieter noise is heard proportionally closer: a 0.25 crouch-step only carries a quarter as far. This is the whole stealth dial.")]
        public float maxHearRadius = 20f;

        [Header("Sight")]
        [Tooltip("How far the NPC can see (m).")]
        public float viewRadius = 14f;
        [Tooltip("Full field-of-view angle (deg). A target outside this cone is unseen however close.")]
        public float viewAngle = 110f;
        [Tooltip("Eye height (m) the view cone and line-of-sight ray originate from.")]
        public float eyeHeight = 1.5f;
        [Tooltip("What BLOCKS line of sight — walls, doors, big props. Leave as-is and it uses everything except the NPC and Viewmodel layers; set it to your world geometry for fewer surprises.")]
        public LayerMask sightBlockMask = ~0;

        [Header("Awareness")]
        [Tooltip("Awareness gained per second while the target is in clear view. High = spots you fast.")]
        public float sightGainPerSecond = 2.5f;
        [Tooltip("Awareness added instantly by a heard noise, scaled by its loudness.")]
        public float hearGain = 0.6f;
        [Tooltip("Awareness lost per second when nothing is being sensed. Low = stays suspicious and keeps investigating for a while.")]
        public float decayPerSecond = 0.35f;
        [Tooltip("At/above this awareness the NPC will investigate its last-known position. Below it, it forgets and returns to normal.")]
        [Range(0f, 1f)] public float investigateThreshold = 0.3f;

        [Header("Performance")]
        [Tooltip("Seconds between sight checks. Phase-offset per NPC so their raycasts don't bunch on one frame.")]
        public float tickInterval = 0.15f;

        /// <summary>Raised the frame a noise is heard (already faction-filtered and in range).</summary>
        public event Action<NoiseEvent> OnHeard;
        /// <summary>Raised when a target enters clear view (edge-triggered, not every tick).</summary>
        public event Action<Transform> OnSpotted;

        /// <summary>The target currently in direct view, or null. Non-null = "I can see them right now."</summary>
        public Transform CurrentTarget { get; private set; }
        /// <summary>Where the NPC last saw/heard something worth checking.</summary>
        public Vector3 LastKnownPosition { get; private set; }
        public bool HasLastKnown { get; private set; }
        /// <summary>0 oblivious → 1 fully alert.</summary>
        public float Awareness01 { get; private set; }

        Transform player;      // root, for occlusion comparison
        Transform playerEye;   // where to aim the sight test (the camera — lowers when crouched, so cover works)
        CharacterController body;
        float nextTick;
        bool wasVisible;

        void Awake() => body = GetComponent<CharacterController>();

        void OnEnable() => NoiseBus.OnNoise += HandleNoise;
        void OnDisable() => NoiseBus.OnNoise -= HandleNoise;

        void Start()
        {
            // Stagger the first tick randomly per NPC, so N NPCs spread their
            // raycasts across the interval instead of all firing on the same frame.
            // (Random, not GetInstanceID — that API is deprecated in Unity 6.5, and
            // runtime AI is deliberately non-deterministic anyway.)
            nextTick = Time.time + UnityEngine.Random.value * tickInterval;
        }

        void Update()
        {
            if (Time.time < nextTick) return;
            nextTick = Time.time + tickInterval;

            EnsurePlayer();
            TickSight(tickInterval);
        }

        void TickSight(float dt)
        {
            bool sees = playerEye != null && CanSee(playerEye.position, player);

            if (sees)
            {
                CurrentTarget = player;
                LastKnownPosition = player.position;
                HasLastKnown = true;
                Awareness01 = Mathf.Min(1f, Awareness01 + sightGainPerSecond * dt);
                if (!wasVisible) OnSpotted?.Invoke(player);
            }
            else
            {
                // Lost sight: remember where they were, then decay.
                if (CurrentTarget != null) LastKnownPosition = CurrentTarget.position;
                CurrentTarget = null;
                Awareness01 = Mathf.Max(0f, Awareness01 - decayPerSecond * dt);
                if (Awareness01 <= 0f) HasLastKnown = false;
            }
            wasVisible = sees;
        }

        void HandleNoise(NoiseEvent e)
        {
            // Ignore own faction (a goblin doesn't investigate another goblin's
            // footsteps). Shouts that deliberately cross factions come in Phase 5.
            if (e.faction == faction) return;

            float audibleRange = maxHearRadius * e.loudness;
            if ((e.position - transform.position).sqrMagnitude > audibleRange * audibleRange) return;

            LastKnownPosition = e.alertTarget;
            HasLastKnown = true;
            Awareness01 = Mathf.Min(1f, Awareness01 + hearGain * e.loudness);
            OnHeard?.Invoke(e);
        }

        /// <summary>In view radius, inside the cone, and no geometry blocking the line to it.</summary>
        public bool CanSee(Vector3 worldPoint, Transform targetRoot)
        {
            // Start the ray just OUTSIDE the NPC's own capsule so it can't self-hit.
            float skin = (body != null ? body.radius : 0.35f) + 0.1f;
            Vector3 eye = transform.position + Vector3.up * eyeHeight;

            Vector3 to = worldPoint - eye;
            float dist = to.magnitude;
            if (dist > viewRadius || dist < 0.01f) return false;

            Vector3 dir = to / dist;
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude > 0.0001f &&
                Vector3.Angle(transform.forward, flat) > viewAngle * 0.5f) return false;

            Vector3 origin = eye + dir * skin;
            float rayLen = dist - skin;
            if (rayLen <= 0f) return true; // essentially on top of it

            if (Physics.Raycast(origin, dir, out RaycastHit hit, rayLen, sightBlockMask, QueryTriggerInteraction.Ignore))
                return targetRoot != null && hit.transform.root == targetRoot; // hit something first — visible only if it IS the target

            return true; // nothing in the way
        }

        /// <summary>External alert (a squadmate's shout, a scripted trigger). Jumps awareness and points the NPC at a threat.</summary>
        public void ForceAlert(Vector3 threatPosition, float awareness)
        {
            LastKnownPosition = threatPosition;
            HasLastKnown = true;
            Awareness01 = Mathf.Clamp01(Mathf.Max(Awareness01, awareness));
        }

        void EnsurePlayer()
        {
            if (player != null) return;
            var fpc = FindObjectOfType<FirstPersonController>();
            if (fpc == null) return;
            player = fpc.transform;
            playerEye = fpc.cam != null ? fpc.cam : fpc.transform;
        }

        void OnDrawGizmosSelected()
        {
            Vector3 eye = transform.position + Vector3.up * eyeHeight;

            // View cone.
            Gizmos.color = CurrentTarget != null ? Color.red : new Color(1f, 0.9f, 0.2f, 0.6f);
            Vector3 fwd = transform.forward;
            Quaternion l = Quaternion.AngleAxis(-viewAngle * 0.5f, Vector3.up);
            Quaternion r = Quaternion.AngleAxis(viewAngle * 0.5f, Vector3.up);
            Gizmos.DrawRay(eye, l * fwd * viewRadius);
            Gizmos.DrawRay(eye, r * fwd * viewRadius);

            // Hearing radius (at full loudness).
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, maxHearRadius);

            // Last-known marker + awareness bar.
            if (HasLastKnown)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(LastKnownPosition, 0.4f);
            }
            Gizmos.color = Color.Lerp(Color.green, Color.red, Awareness01);
            Gizmos.DrawRay(transform.position + Vector3.up * 2.2f, Vector3.up * Awareness01);
        }
    }
}
