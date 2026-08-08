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

        /// <summary>
        /// What the last blow's defences did to it — for feedback (a block spark, a parry
        /// flash, a UI tell) and, via its surface override, for the ATTACKER's impact VFX.
        /// Logic lives in the mitigators, not here. Read it immediately after TakeDamage:
        /// OnHitLanded fires after TakeDamage returns, so an attacker's effects component
        /// sees the result of its own blow.
        /// </summary>
        public Mitigation LastMitigation { get; private set; }
        public DamageOutcome LastOutcome => LastMitigation.outcome;

          private Animator animator;
        IDamageMitigator[] mitigators;

        void Awake()
        {
            Current = max;

            // Cached, not fetched per hit — TakeDamage runs on every arrow, swing and barrel
            // in a crowd. Sorted so a parry resolves before flat armour reduction; a
            // mitigator added at runtime must call RefreshMitigators.
            RefreshMitigators();

            animator = GetComponent<Animator>();
            if (animator != null)
            {
                animator.SetFloat("Health", Current);
            }            
        }

        public void TakeDamage(in DamageInfo info)
        {
            if (IsDead) return;

            // Let the victim's own defences alter the blow FIRST — a raised shield, a parry,
            // future armour. Consulted here rather than in each attacker so blocking works
            // against melee, arrows, thrown props and the environment alike, and no damage
            // source ever learns that guarding exists (see IDamageMitigator).
            //
            // `info` is an `in` parameter, so it is copied once here and the copy is what
            // mitigators edit and what everything downstream sees — OnDamaged listeners
            // (NpcCombatAudio's grunt volume, NpcHitReactions' knockback, NpcFace's shock)
            // must react to the blow that ACTUALLY landed, not the one that was thrown.
            DamageInfo blow = info;
            Mitigation result = Mitigation.None;
            if (mitigators != null)
            {
                for (int i = 0; i < mitigators.Length; i++)
                {
                    Mitigation m = mitigators[i].Mitigate(ref blow);
                    // Keep the STRONGEST outcome: a parry outranks a block for feedback
                    // purposes even if a later mitigator only blocked. The surface override
                    // rides along with whichever one won, since that's the thing the blow
                    // actually struck.
                    if (m.outcome > result.outcome) result = m;
                }
            }
            LastMitigation = result;

            Current = Mathf.Max(0f, Current - Mathf.Max(0f, blow.amount));

            if (debugDamage)
                Debug.Log($"[Health] {name} took {blow.amount:0.#} {blow.type} damage " +
                          $"from {(blow.instigator != null ? blow.instigator.name : "the world")}" +
                          $"{(LastOutcome != DamageOutcome.None ? $" [{LastOutcome}, was {info.amount:0.#}]" : "")} — {Current:0.#}/{max:0.#} HP.", this);

            OnDamaged?.Invoke(blow);

            if (Current <= 0f)
            {
                IsDead = true;
                OnDied?.Invoke(blow);   // the blow that actually killed, post-mitigation
            }
            if (animator != null)
            {
                animator.SetFloat("Health", Current);
            }
        }

        /// <summary>
        /// Re-scan for IDamageMitigator components. Call after adding or removing one at
        /// runtime (equipping a shield that arrives as a component rather than a toggle).
        /// </summary>
        public void RefreshMitigators()
        {
            mitigators = GetComponents<IDamageMitigator>();
            if (mitigators.Length > 1)
                System.Array.Sort(mitigators, (a, b) => a.MitigationOrder.CompareTo(b.MitigationOrder));
        }

        public void Heal(float amount)
        {
            if (IsDead) return;
            Current = Mathf.Min(max, Current + Mathf.Max(0f, amount));
        }
    }
}
