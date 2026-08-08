using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Makes a thrown prop HURT what it lands on. Sits beside ImpactAudio on
    /// carryables (barrels, skulls) but has its OWN OnCollisionEnter rather than
    /// subscribing to ImpactAudio.OnImpact — that event doesn't say WHAT was hit,
    /// and its thresholds are audio-tuned: a crate rolling into your shins should
    /// be audible but harmless. The shared curve lives in ImpactForce so the two
    /// systems can't drift.
    ///
    /// ARMED, not always-on: PlayerCarry (and later NpcCarry) arms it at the
    /// moment of the throw, and it disarms after the first damaging hit or a
    /// timeout. Two things this prevents — a barrel that bounces off a goblin and
    /// clips it again on the floor roll does NOT double-hit (the same lesson
    /// ImpactAudio's retrigger interval taught), and a prop knocked around by
    /// ordinary shoving never hurts anyone, so props are only weapons when
    /// somebody MADE them one.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class ThrownDamage : MonoBehaviour
    {
        [Tooltip("Impact speed (m/s) below which a hit does nothing. Keep above walking-shove speeds.")]
        public float minDamageSpeed = 4f;
        [Tooltip("Impact speed (m/s) that deals FULL damage. Around Carryable.throwSpeed for a hard direct hit.")]
        public float fullDamageSpeed = 10f;
        [Tooltip("Damage at fullDamageSpeed, scaled by the mass curve below. Slower hits scale down along the ImpactForce curve.")]
        public float maxDamage = 15f;
        [Tooltip("Rigidbody mass that counts as a FULL-weight projectile. A skull hurts less than a barrel at the same speed.")]
        public float fullDamageMass = 20f;
        [Header("Knockback (physics-derived)")]
        [Tooltip("Fraction of the projectile's MOMENTUM (mass x speed) converted into victim shove speed, per unit of victim reference mass. Knockback is computed from the actual collision, so a heavy barrel at full tilt bowls a goblin over while a lobbed skull barely rocks it — no per-prop knockback tuning.")]
        public float momentumTransfer = 0.06f;
        [Tooltip("Cap on the shove (m/s) so an extreme collision can't launch a victim across the room.")]
        public float maxKnockback = 7f;
        [Tooltip("Seconds after the throw before the prop stops being a weapon regardless of what it hit.")]
        public float armedDuration = 3f;

        [Tooltip("Log every armed collision with its speed and damage.")]
        public bool debugDamage = false;

        Rigidbody body;
        GameObject instigator;
        float armedUntil;
        bool spent;

        void Awake() => body = GetComponent<Rigidbody>();

        /// <summary>Called by whoever throws this (PlayerCarry today, NpcCarry later).</summary>
        public void Arm(GameObject thrower)
        {
            instigator = thrower;
            armedUntil = Time.time + armedDuration;
            spent = false;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (spent || Time.time > armedUntil) return;

            float speed = collision.relativeVelocity.magnitude;
            if (speed < minDamageSpeed)
            {
                if (debugDamage) Debug.Log($"[Thrown] {name} hit '{collision.collider.name}' at {speed:0.0} m/s — too slow to hurt.");
                return;
            }

            // Never hurt the thrower with their own projectile mid-flight (it can
            // graze the player's capsule leaving the hand).
            if (instigator != null && collision.transform.root == instigator.transform.root) return;

            var damageable = collision.collider.GetComponentInParent<IDamageable>();
            if (damageable == null || damageable.IsDead) return;

            float force = ImpactForce.Evaluate(speed, minDamageSpeed, fullDamageSpeed);
            float massScale = Mathf.Clamp01(body.mass / Mathf.Max(0.1f, fullDamageMass));
            float damage = maxDamage * force * massScale;

            // Physics-derived shove: the projectile's actual momentum at impact,
            // not an authored constant — so what the victim feels IS what hit them.
            float shove = Mathf.Min(maxKnockback, body.mass * speed * momentumTransfer);

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
            Vector3 dir = collision.relativeVelocity.sqrMagnitude > 0.01f
                ? (-collision.relativeVelocity).normalized
                : (damageable.Transform.position - transform.position).normalized;

            damageable.TakeDamage(new DamageInfo
            {
                amount = damage,
                point = point,
                direction = dir,
                instigator = instigator,
                type = DamageType.Thrown,
                impulse = shove,
            });

            spent = true;   // one hit per throw — the bounce is free

            if (debugDamage)
                Debug.Log($"[Thrown] {name} hit '{collision.collider.name}' at {speed:0.0} m/s → {damage:0.#} damage (force {force:0.00}, mass scale {massScale:0.00}).");
        }
    }
}
