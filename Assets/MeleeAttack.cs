using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A melee swing: windup → active sweep → recovery. A capability component —
    /// NpcBrain calls TryAttack() and listens to events; nothing here decides WHEN
    /// to attack. Built NPC-first but deliberately player-agnostic: the eventual
    /// player melee is this same component with its sweep origin on the camera.
    ///
    /// The sweep carries ViewmodelCollision's hard-won lesson — a cast that STARTS
    /// inside a collider reports nothing, and melee range means you're usually
    /// already touching the target — plus three differences an attack needs:
    /// CheckSphere→Overlap fallback, SphereCastAll (a swing can clip two targets,
    /// a single cast returns only the nearest), and dedupe by root so a
    /// multi-collider victim takes ONE hit per swing.
    ///
    /// Damage/range/timing are inline fields for now; Phase 6 moves them onto a
    /// WeaponDefinition and this component reads whatever is equipped.
    /// </summary>
    [DisallowMultipleComponent]
    public class MeleeAttack : MonoBehaviour
    {
        [Header("Weapon (inline until WeaponDefinition lands in phase 6)")]
        [Tooltip("Damage per landed hit.")]
        public float damage = 10f;
        [Tooltip("Reach of the sweep (m), measured from the origin forward.")]
        public float range = 1.6f;
        [Tooltip("Radius of the sweep (m) — how 'wide' the swing is.")]
        public float sweepRadius = 0.45f;
        [Tooltip("Knockback impulse (m/s) delivered to victims.")]
        public float knockback = 5f;

        [Header("Timing")]
        [Tooltip("Seconds between starting the swing and the hit landing — the victim's dodge window. THE combat-feel number.")]
        public float windup = 0.45f;
        [Tooltip("Seconds after the hit before another swing can start.")]
        public float recovery = 0.8f;

        [Header("Sweep")]
        [Tooltip("Height above the feet the sweep originates from.")]
        public float originHeight = 1.1f;
        [Tooltip("What the sweep can HIT (victims' colliders). Exclude this NPC's own layer or it can clip itself.")]
        public LayerMask hitMask = ~0;

        [Tooltip("Log swings, hits, and whiffs.")]
        public bool debugAttack = false;

        /// <summary>Swing started (windup begins). Drive animation/audio windup from this.</summary>
        public event Action OnSwingStart;
        /// <summary>Swing finished (hit or whiff, recovery begins).</summary>
        public event Action OnSwingEnd;
        /// <summary>A hit landed on a victim.</summary>
        public event Action<IDamageable, DamageInfo> OnHitLanded;

        public bool IsSwinging { get; private set; }
        public bool CanAttack => !IsSwinging && Time.time >= readyAt && !Suppressed;
        /// <summary>Set true to block attacks (stagger, death, disarm-in-progress).</summary>
        public bool Suppressed { get; set; }

        float readyAt;
        Faction ownFaction;
        readonly HashSet<Transform> hitThisSwing = new HashSet<Transform>();
        static readonly Collider[] overlapScratch = new Collider[16];
        static readonly RaycastHit[] castScratch = new RaycastHit[16];

        void Awake() => ownFaction = FactionMember.Of(transform);

        /// <summary>Begin a swing. Returns false if still recovering/suppressed.</summary>
        public bool TryAttack()
        {
            if (!CanAttack) return false;
            IsSwinging = true;
            OnSwingStart?.Invoke();
            Invoke(nameof(LandSweep), windup);
            if (debugAttack) Debug.Log($"[Melee] {name}: swing started (lands in {windup:0.00}s).", this);
            return true;
        }

        void LandSweep()
        {
            IsSwinging = false;
            readyAt = Time.time + recovery;
            hitThisSwing.Clear();

            Vector3 origin = transform.position + Vector3.up * originHeight;
            Vector3 dir = transform.forward;
            int hits;

            // Already touching the target? A cast starting inside a collider
            // reports NOTHING — melee range means this is the common case, not the
            // edge case. Overlap instead.
            if (Physics.CheckSphere(origin, sweepRadius, hitMask, QueryTriggerInteraction.Ignore))
            {
                hits = Physics.OverlapSphereNonAlloc(origin, Mathf.Max(sweepRadius, range * 0.6f), overlapScratch, hitMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++) TryHit(overlapScratch[i], origin, dir);
            }
            else
            {
                hits = Physics.SphereCastNonAlloc(origin, sweepRadius, dir, castScratch, range, hitMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++) TryHit(castScratch[i].collider, origin, dir);
            }

            if (debugAttack && hitThisSwing.Count == 0) Debug.Log($"[Melee] {name}: whiff.", this);
            OnSwingEnd?.Invoke();
        }

        void TryHit(Collider c, Vector3 origin, Vector3 dir)
        {
            if (c == null) return;
            Transform root = c.attachedRigidbody != null ? c.attachedRigidbody.transform.root : c.transform.root;

            if (root == transform.root) return;                 // never hit yourself
            if (!hitThisSwing.Add(root)) return;                // one hit per victim per swing
            if (FactionMember.Of(root) == ownFaction) return;   // no friendly fire

            var damageable = root.GetComponentInChildren<IDamageable>();
            if (damageable == null || damageable.IsDead) return;

            // Only in front — the overlap fallback is a sphere and shouldn't let a
            // swing hit someone standing behind the attacker.
            Vector3 to = root.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f && Vector3.Dot(to.normalized, dir) < 0.1f) return;

            var info = new DamageInfo
            {
                amount = damage,
                point = c.ClosestPoint(origin),
                direction = to.sqrMagnitude > 0.01f ? to.normalized : dir,
                instigator = gameObject,
                type = DamageType.Melee,
                impulse = knockback,
            };
            damageable.TakeDamage(info);
            OnHitLanded?.Invoke(damageable, info);

            if (debugAttack) Debug.Log($"[Melee] {name}: hit '{root.name}' for {damage:0.#}.", this);
        }

        void OnDrawGizmosSelected()
        {
            Vector3 origin = transform.position + Vector3.up * originHeight;
            Gizmos.color = IsSwinging ? Color.red : new Color(1f, 0.5f, 0f, 0.5f);
            Gizmos.DrawWireSphere(origin, sweepRadius);
            Gizmos.DrawWireSphere(origin + transform.forward * range, sweepRadius);
            Gizmos.DrawLine(origin, origin + transform.forward * range);
        }
    }
}
