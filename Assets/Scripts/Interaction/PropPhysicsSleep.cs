using System.Collections;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Props spawn KINEMATIC and become dynamic the first time something actually touches
    /// them - a push, a pickup, a hit.
    ///
    /// WHY. Every FullGameObject prop the placer spawns used to be a live rigidbody from
    /// frame one, so a fresh dungeon dropped and settled a hundred barrels, crates, chairs
    /// and skulls at once. Each settling contact fires ImpactAudio, and its retrigger gate
    /// is PER SOURCE, so a hundred props each settling once sails straight past it. Measured
    /// with the F7 overlay: a peak of 189 playing voices of which 111 were Physics and 84
    /// were being stolen - against a real-voice budget of 32.
    ///
    /// The audio was how it was noticed, but it is not the only win. Props now stay exactly
    /// where the generator put them instead of drifting, sinking or occasionally squeezing
    /// through geometry over a long run, and one bumped crate no longer wakes a chain of
    /// clattering neighbours.
    ///
    /// NOT A CPU OPTIMIZATION, mostly. Unity already auto-sleeps settled rigidbodies, so
    /// the steady state was never expensive - what cost was the SETTLE, and the re-settle
    /// every time a fight jostled the room.
    ///
    /// THE TRADE: a kinematic body does not fall. Physics has quietly been correcting
    /// placement all along, dropping anything spawned slightly high onto the floor. With
    /// this on, that prop hovers instead. `warnIfHovering` reports it rather than leaving
    /// you to notice visually, because these are pre-existing authoring errors that
    /// settling was hiding, not new bugs.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class PropPhysicsSleep : MonoBehaviour
    {
        [Tooltip("Start kinematic. Off = the old behaviour (live rigidbody from spawn), which is the escape hatch for a prop that genuinely needs to fall into place.")]
        public bool sleepOnStart = true;

        [Tooltip("On waking, also wake sleeping props whose colliders lie within this margin (m) of THIS prop's bounds. 0 disables it.\n\nThis is the STACKING case: if a table is destroyed out from under a bowl, a kinematic bowl would hang in mid-air. Waking contacts hands the whole stack to physics together.\n\nMeasured from the prop's BOUNDS, not a sphere at its pivot — a table's pivot is at its base, so any sphere small enough to be safe never reaches the tabletop where the props actually are. Keep the margin small: it is a contact test, not a blast radius, and a large one wakes a room from a single nudge.")]
        public float wakeNeighbourMargin = 0.15f;

        [Tooltip("After spawning, check the prop is actually resting on something and warn if it is hanging in the air. Editor and development builds only.")]
        public bool warnIfHovering = true;
        [Tooltip("Seconds to wait before the hover check — long enough for the dungeon to finish generating around it.")]
        public float hoverCheckDelay = 1.5f;
        [Tooltip("Gap (m) below the prop that counts as hovering rather than resting.")]
        public float hoverTolerance = 0.08f;

        Rigidbody body;
        Collider[] ownColliders;
        static readonly Collider[] neighbourScratch = new Collider[16];

        /// <summary>False while the prop is still kinematic and untouched.</summary>
        public bool IsAwake { get; private set; }

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            ownColliders = GetComponentsInChildren<Collider>(true);
        }

        void Start()
        {
            if (sleepOnStart && body != null)
            {
                body.isKinematic = true;
                IsAwake = false;
            }
            else IsAwake = true;

            // Damage wakes it even when nothing pushed it — an arrow into a barrel, or a
            // sword swing that connects without a shove. Optional: plenty of props have no
            // Health at all.
            var health = GetComponent<Health>();
            if (health != null) { health.OnDamaged += HandleDamaged; health.OnDied += HandleDied; }

            if (warnIfHovering && sleepOnStart && Debug.isDebugBuild) StartCoroutine(HoverCheck());
        }

        void OnDestroy()
        {
            var health = GetComponent<Health>();
            if (health != null) { health.OnDamaged -= HandleDamaged; health.OnDied -= HandleDied; }
        }

        void HandleDamaged(DamageInfo info) => Wake();

        // Death as well as damage. Whatever was RESTING ON this prop has to be handed to
        // physics when it disappears, or it hangs in the air — which is exactly what a
        // destroyed table did to the bowls on top of it. WakeNeighbours is called explicitly
        // because Wake() short-circuits if the prop was already awake (a table is damaged
        // before it dies), and it is the DEATH that removes the support.
        void HandleDied(DamageInfo info) { Wake(); WakeNeighbours(); }

        /// <summary>
        /// Hand this prop to physics. Idempotent and cheap to call from anywhere — the
        /// callers (push, carry, damage) do not need to know whether it was asleep.
        /// </summary>
        public void Wake()
        {
            if (IsAwake) return;
            IsAwake = true;
            if (body != null) body.isKinematic = false;
            WakeNeighbours();
        }

        /// <summary>
        /// Wake sleeping props in contact. The IsAwake guard above is what stops this
        /// recursing forever when two neighbours are each other's neighbour — it also means
        /// a genuine stack propagates exactly once, outward from whatever was touched.
        /// </summary>
        void WakeNeighbours()
        {
            if (wakeNeighbourMargin <= 0f) return;
            if (!TryGetBounds(out Bounds b)) return;

            int n = Physics.OverlapBoxNonAlloc(
                b.center, b.extents + Vector3.one * wakeNeighbourMargin, neighbourScratch,
                Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < n; i++)
            {
                var c = neighbourScratch[i];
                if (c == null) continue;
                var other = c.GetComponentInParent<PropPhysicsSleep>();
                if (other == null || ReferenceEquals(other, this) || other.IsAwake) continue;
                other.Wake();
            }
        }

        /// <summary>World bounds of this prop's solid colliders, or false if it has none.</summary>
        bool TryGetBounds(out Bounds b)
        {
            b = default;
            bool have = false;
            foreach (var c in ownColliders)
            {
                if (c == null || c.isTrigger) continue;
                if (!have) { b = c.bounds; have = true; } else b.Encapsulate(c.bounds);
            }
            return have;
        }

        /// <summary>
        /// Is anything under us? A kinematic prop cannot fall, so a placement that gravity
        /// used to correct silently now hangs there. Reported once, naming the prop.
        /// </summary>
        IEnumerator HoverCheck()
        {
            yield return new WaitForSeconds(hoverCheckDelay);
            if (IsAwake || body == null) yield break;
            if (!TryGetBounds(out Bounds b)) yield break;

            // Cast from the centre down past the base. RaycastAll because the first thing hit
            // is our own collider — a mask would need a layer this project does not reserve.
            float reach = b.extents.y + hoverTolerance + 0.5f;
            var hits = Physics.RaycastAll(b.center, Vector3.down, reach, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (System.Array.IndexOf(ownColliders, h.collider) >= 0) continue;
                if (h.distance < best) best = h.distance;
            }

            float gap = best - b.extents.y;
            if (best == float.MaxValue || gap > hoverTolerance)
            {
                Debug.LogWarning(
                    $"[PropPhysicsSleep] '{name}' is HOVERING {(best == float.MaxValue ? "with nothing beneath it" : $"{gap:0.00}m above the floor")}. " +
                    "It spawns kinematic, so gravity no longer hides this — the prop's pivot is probably not at its base, " +
                    "or its placement is off. Fix the prefab origin, or clear Sleep On Start for this prop.", this);
            }
        }
    }
}
