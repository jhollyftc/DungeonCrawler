using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Stagger pool, separate from Health — the mechanism that makes light vs heavy
    /// MEAN something and stops stun-lock (the reference doc's #20-21). Health
    /// decides whether you DIE; poise decides whether your current action is
    /// INTERRUPTED. Light hits chip poise; enough at once (or one heavy) empties it
    /// → a POISE BREAK → a major stagger. After a break, a brief resistance window
    /// ignores further poise damage so a flurry of light hits can't perma-stun.
    ///
    /// Subscribes to Health.OnDamaged (same pattern as NpcHitReactions), so Health
    /// stays a plain number with edges and any body gains poise just by adding this.
    /// </summary>
    [RequireComponent(typeof(Health))]
    [DisallowMultipleComponent]
    public class Poise : MonoBehaviour
    {
        [Tooltip("Maximum poise. Light hits chip DamageInfo.poiseDamage off this; at 0 it breaks. Size it so a few lights OR one heavy break it.")]
        public float max = 60f;
        [Tooltip("Poise regained per second, once regen kicks in.")]
        public float regenPerSecond = 20f;
        [Tooltip("Seconds of no poise damage before poise starts regenerating.")]
        public float regenDelay = 1.5f;
        [Tooltip("Seconds after a break during which incoming poise damage is IGNORED — the anti-stun-lock window. A staggered enemy can't be instantly re-broken by light hits.")]
        public float breakResistance = 0.8f;
        [Tooltip("Log poise chips and breaks.")]
        public bool debugPoise = false;

        /// <summary>Poise hit 0 — a major stagger should fire. Carries the breaking hit.</summary>
        public event Action<DamageInfo> OnPoiseBreak;

        public float Current { get; private set; }
        public float Poise01 => max > 0f ? Current / max : 0f;
        /// <summary>In the post-break resistance window (also useful as a "recently staggered" flag).</summary>
        public bool Resisting => Time.time < resistUntil;

        Health health;
        float lastDamageTime;
        float resistUntil;

        void Awake()
        {
            health = GetComponent<Health>();
            Current = max;
        }

        void OnEnable() { if (health != null) health.OnDamaged += HandleDamaged; }
        void OnDisable() { if (health != null) health.OnDamaged -= HandleDamaged; }

        void HandleDamaged(DamageInfo info)
        {
            if (health.IsDead || info.poiseDamage <= 0f) return;
            Chip(info.poiseDamage, info);
        }

        /// <summary>
        /// Take poise damage from something OTHER than a landed blow — a block absorbing a
        /// hit, a future stamina drain. Public because the guard removes `poiseDamage` from
        /// the blow (so this can't also arrive via OnDamaged and be counted twice) and then
        /// applies its own, usually larger, cost here.
        /// </summary>
        public void Chip(float amount, DamageInfo cause = default)
        {
            if (health != null && health.IsDead) return;
            if (amount <= 0f) return;
            lastDamageTime = Time.time;

            if (Resisting)
            {
                if (debugPoise) Debug.Log($"[Poise] {name}: {amount:0} poise ignored (resistance window).", this);
                return;
            }

            Current -= amount;
            if (debugPoise) Debug.Log($"[Poise] {name}: -{amount:0} → {Current:0}/{max:0}.", this);

            if (Current <= 0f) Break(cause);
        }

        /// <summary>
        /// Empty the pool outright — a guaranteed stagger. The shield bash uses this shape for
        /// its guaranteed break, and a PARRY uses it on the attacker, so being parried reuses
        /// the same major-stagger reaction NpcHitReactions already produces instead of needing
        /// a bespoke stun state.
        ///
        /// Respects the resistance window, which is what stops a crowd chain-stunning
        /// something forever — including via repeated parries.
        /// </summary>
        public void Break(DamageInfo cause = default)
        {
            if (health != null && health.IsDead) return;
            if (Resisting)
            {
                if (debugPoise) Debug.Log($"[Poise] {name}: break ignored (resistance window).", this);
                return;
            }

            Current = max;                       // reset after the break
            resistUntil = Time.time + breakResistance;
            lastDamageTime = Time.time;
            if (debugPoise) Debug.Log($"[Poise] {name}: POISE BREAK → major stagger.", this);
            OnPoiseBreak?.Invoke(cause);
        }

        void Update()
        {
            if (Current >= max || health.IsDead) return;
            if (Time.time - lastDamageTime < regenDelay) return;
            Current = Mathf.Min(max, Current + regenPerSecond * Time.deltaTime);
        }
    }
}
