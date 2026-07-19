using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Hit points for anything — the player root and every NPC carry one. Combat
    /// systems only ever talk to IDamageable, so what OWNS the health never
    /// matters to an attacker. Reactions (knockback, stagger, death topple, a
    /// damage vignette) subscribe to the events rather than living here: Health
    /// stays a number with edges, and each body decides how it suffers.
    /// </summary>
    [DisallowMultipleComponent]
    public class Health : MonoBehaviour, IDamageable
    {
        [Tooltip("Maximum hit points.")]
        public float max = 30f;
        [Tooltip("Log every hit with amount, source, and remaining HP. Leave on while combat is being proven out.")]
        public bool debugDamage = true;

        /// <summary>After damage is applied. Fires on every hit, including the killing one (before OnDied).</summary>
        public event Action<DamageInfo> OnDamaged;
        /// <summary>Once, when HP reaches 0.</summary>
        public event Action<DamageInfo> OnDied;

        public float Current { get; private set; }
        public float Health01 => max > 0f ? Current / max : 0f;
        public bool IsDead { get; private set; }
        public Transform Transform => transform;

        void Awake() => Current = max;

        public void TakeDamage(in DamageInfo info)
        {
            if (IsDead) return;

            Current = Mathf.Max(0f, Current - Mathf.Max(0f, info.amount));

            if (debugDamage)
                Debug.Log($"[Health] {name} took {info.amount:0.#} {info.type} damage " +
                          $"from {(info.instigator != null ? info.instigator.name : "the world")} — {Current:0.#}/{max:0.#} HP.", this);

            OnDamaged?.Invoke(info);

            if (Current <= 0f)
            {
                IsDead = true;
                OnDied?.Invoke(info);
            }
        }

        public void Heal(float amount)
        {
            if (IsDead) return;
            Current = Mathf.Min(max, Current + Mathf.Max(0f, amount));
        }
    }
}
