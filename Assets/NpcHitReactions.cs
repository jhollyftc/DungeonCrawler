using System.Collections;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// How an NPC SUFFERS: knockback + stagger on damage, topple + cleanup on
    /// death. Subscribes to Health's events — Health stays a number with edges,
    /// and this component owns the body's response, the same split as
    /// IPushable/IDamageable everywhere else.
    ///
    /// Getting hit also alerts the victim (ForceAlert toward the attacker): a
    /// goblin sniped by a thrown barrel from behind snaps around and investigates
    /// the thrower's position rather than serenely wandering on.
    ///
    /// Death without a death animation (none rigged yet): AI/agent/capsule off, a
    /// short topple of the visual, then destroy. When a death clip exists, this is
    /// where NpcAnimatorDriver gets its Die trigger instead of the topple.
    /// </summary>
    [RequireComponent(typeof(Health))]
    [RequireComponent(typeof(NpcLocomotion))]
    [DisallowMultipleComponent]
    public class NpcHitReactions : MonoBehaviour
    {
        [Header("Stagger")]
        [Tooltip("Seconds of stagger after a hit: movement crawls and attacks are suppressed. The player's reward window for landing a hit.")]
        public float staggerDuration = 0.4f;
        [Tooltip("Movement multiplier while staggered.")]
        [Range(0f, 1f)] public float staggerSpeedMultiplier = 0.15f;
        [Tooltip("Extra stagger seconds per m/s of knockback, so a barrel to the chest keeps a goblin reeling longer than a graze. The shove itself is momentum-derived (see ThrownDamage), so this rides real physics.")]
        public float staggerPerImpulse = 0.08f;

        [Header("Thrown hits (physical reaction)")]
        [Tooltip("Fraction of a THROWN hit's shove redirected upward — knocks the victim slightly off its feet so the hit reads as a body blow, not an ice-slide. The pop follows a real ballistic arc via NpcLocomotion.")]
        [Range(0f, 1f)] public float thrownVerticalPop = 0.35f;

        [Header("Alert on hit")]
        [Tooltip("Being hit slams awareness to this value, pointed at the attacker — you can't barrel a goblin and stay unsuspected.")]
        [Range(0f, 1f)] public float awarenessOnHit = 1f;

        [Header("Death")]
        [Tooltip("Seconds the corpse topples before despawn (fallback path — only when the controller has no Die state).")]
        public float toppleTime = 0.5f;
        [Tooltip("Seconds the corpse lies there before it starts sinking away.")]
        public float corpseLinger = 8f;
        [Tooltip("Seconds the corpse takes to sink into the floor. The despawn a player never catches happening beats one they watch pop.")]
        public float sinkTime = 2.5f;
        [Tooltip("How far down (m) it sinks before being destroyed. Deeper than the model is tall.")]
        public float sinkDepth = 1.8f;

        Health health;
        NpcLocomotion body;
        NpcPerception senses;
        MeleeAttack melee;
        NpcBrain brain;
        NpcAnimatorDriver animDriver;
        NpcBoneReaction boneReaction;
        Coroutine stagger;

        void Awake()
        {
            health = GetComponent<Health>();
            body = GetComponent<NpcLocomotion>();
            senses = GetComponent<NpcPerception>();
            melee = GetComponent<MeleeAttack>();
            brain = GetComponent<NpcBrain>();
            animDriver = GetComponent<NpcAnimatorDriver>();
            boneReaction = GetComponent<NpcBoneReaction>();
        }

        void OnEnable()
        {
            health.OnDamaged += HandleDamaged;
            health.OnDied += HandleDied;
        }

        void OnDisable()
        {
            health.OnDamaged -= HandleDamaged;
            health.OnDied -= HandleDied;
        }

        void HandleDamaged(DamageInfo info)
        {
            if (health.IsDead) return;

            // Knockback: shove along the hit direction, through the locomotion
            // capability so it composes with pathing instead of teleporting the
            // capsule. Thrown hits redirect part of the shove UPWARD — the victim
            // gets knocked slightly off its feet and comes down on a ballistic
            // arc, which is what makes a barrel to the chest read as a body blow.
            Vector3 flat = new Vector3(info.direction.x, 0f, info.direction.z).normalized;
            float pop = info.type == DamageType.Thrown ? thrownVerticalPop : 0f;
            body.AddImpulse(flat * info.impulse * (1f - pop) + Vector3.up * info.impulse * pop);

            // Per-bone flinch at the point of impact, riding the same
            // momentum-derived impulse as the capsule shove — the skeleton and the
            // body always agree about how hard the hit was.
            boneReaction?.ApplyHit(info.point, info.direction * info.impulse);

            // Being hit is impossible to miss: full alert toward whoever did it.
            if (senses != null && info.instigator != null)
                senses.ForceAlert(info.instigator.transform.position, awarenessOnHit);

            if (stagger != null) StopCoroutine(stagger);
            stagger = StartCoroutine(Stagger(staggerDuration + info.impulse * staggerPerImpulse));
        }

        IEnumerator Stagger(float duration)
        {
            body.SpeedMultiplier = staggerSpeedMultiplier;
            if (melee != null) melee.Suppressed = true;
            yield return new WaitForSeconds(duration);
            body.SpeedMultiplier = 1f;
            if (melee != null) melee.Suppressed = false;
            stagger = null;
        }

        void HandleDied(DamageInfo info)
        {
            if (stagger != null) StopCoroutine(stagger);

            // Brain off, senses off, body inert. The agent must go before the
            // controller or it keeps steering a corpse.
            if (brain != null) brain.enabled = false;
            if (senses != null) senses.enabled = false;
            if (melee != null) melee.Suppressed = true;
            if (body.Agent != null) body.Agent.enabled = false;
            body.enabled = false;
            if (body.Controller != null) body.Controller.enabled = false;

            // Death animation if the controller has one; the code topple is the
            // fallback so a rig without a death clip still dies convincingly.
            bool animated = animDriver != null && animDriver.TriggerDeath();

            // Kick the skeleton with the killing blow — the bone springs run on
            // top of the death clip too, so the same clip falls slightly
            // differently depending on what killed it and from where.
            boneReaction?.ApplyHit(info.point, info.direction * (info.impulse * 1.5f));

            StartCoroutine(animated ? Despawn() : ToppleAndDespawn(info));
        }

        IEnumerator Despawn()
        {
            // The Die state plays itself out; we just clean up afterwards.
            yield return new WaitForSeconds(corpseLinger);
            yield return SinkAway();
        }

        /// <summary>
        /// Ease the corpse down through the floor, then destroy it. The animator
        /// must stop first or it keeps re-planting the bones at floor height every
        /// frame and the body never moves. Physical presence is already gone (the
        /// capsule was disabled at death), so nothing can stand on or collide with
        /// the sinking body.
        /// </summary>
        IEnumerator SinkAway()
        {
            var anim = GetComponentInChildren<Animator>();
            if (anim != null) anim.enabled = false;

            Vector3 from = transform.position;
            Vector3 to = from + Vector3.down * sinkDepth;
            float t = 0f;
            while (t < sinkTime)
            {
                t += Time.deltaTime;
                // Ease-in: barely moving at first (unnoticeable start), gone by the end.
                float k = t / sinkTime;
                transform.position = Vector3.Lerp(from, to, k * k);
                yield return null;
            }
            Destroy(gameObject);
        }

        IEnumerator ToppleAndDespawn(DamageInfo info)
        {
            // No death clip: fall away from the blow.
            Vector3 axis = Vector3.Cross(Vector3.up, info.direction.sqrMagnitude > 0.01f ? info.direction : transform.forward);
            Quaternion from = transform.rotation;
            Quaternion to = Quaternion.AngleAxis(90f, axis.sqrMagnitude > 0.01f ? axis.normalized : transform.right) * from;

            float t = 0f;
            while (t < toppleTime)
            {
                t += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, t / toppleTime);
                transform.rotation = Quaternion.Slerp(from, to, k);
                yield return null;
            }

            yield return new WaitForSeconds(corpseLinger);
            yield return SinkAway();
        }
    }
}
