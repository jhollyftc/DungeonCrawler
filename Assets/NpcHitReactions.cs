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

        [Header("Reaction routing")]
        [Tooltip("ON: living hits use the blended RAGDOLL (NpcRagdollReaction) — the tuning-heavy one. OFF (default): living hits use the directional spring FLINCH (NpcFlinch), and the ragdoll is used only for DEATH. Lets you shelve ragdoll REACTIONS while keeping ragdoll DEATH.")]
        public bool useRagdollForReactions = false;

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
        NpcFlinch flinch;
        NpcRagdollReaction ragdoll;
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
            flinch = GetComponent<NpcFlinch>();
            ragdoll = GetComponent<NpcRagdollReaction>();
        }

        // Directional flinch preferred; plain spring is the fallback if no NpcFlinch.
        void Flinch(Vector3 point, Vector3 impulse)
        {
            if (flinch != null) flinch.ApplyHit(point, impulse);
            else boneReaction?.ApplyHit(point, impulse);
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

            // Living hits: the directional spring FLINCH by default; the blended
            // ragdoll only if opted in (useRagdollForReactions) and enabled. A
            // ragdoll reaction that returns false (too light) falls through to the
            // flinch path below.
            bool ragdolled = useRagdollForReactions && ragdoll != null && ragdoll.isActiveAndEnabled
                             && ragdoll.ReactToHit(info.point, info.direction * info.impulse);

            if (!ragdolled)
            {
                // Capsule knockback through the locomotion capability so it composes
                // with pathing. Thrown hits redirect part of the shove UPWARD onto a
                // ballistic arc — a body blow, not an ice-slide.
                Vector3 flat = new Vector3(info.direction.x, 0f, info.direction.z).normalized;
                float pop = info.type == DamageType.Thrown ? thrownVerticalPop : 0f;
                body.AddImpulse(flat * info.impulse * (1f - pop) + Vector3.up * info.impulse * pop);

                Flinch(info.point, info.direction * info.impulse);

                if (stagger != null) StopCoroutine(stagger);
                stagger = StartCoroutine(Stagger(staggerDuration + info.impulse * staggerPerImpulse));
            }

            // Being hit is impossible to miss: full alert toward whoever did it.
            if (senses != null && info.instigator != null)
                senses.ForceAlert(info.instigator.transform.position, awarenessOnHit);
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

            // Full ragdoll death (physical + directional) if the goblin has one —
            // the killing blow throws it, and NpcRagdollReaction owns linger + sink
            // + destroy. Otherwise the death animation, then the code topple as a
            // last resort.
            // Full ragdoll death if the goblin has one AND the component is enabled
            // (a dormant/disabled ragdoll can't run its despawn coroutine — using it
            // anyway left the body half-ragdolled and stuck).
            if (ragdoll != null && ragdoll.isActiveAndEnabled && ragdoll.HasRagdoll)
            {
                ragdoll.Die(info.point, info.direction * (info.impulse + 3f));  // a little extra so a killing tap still topples
                return;
            }

            bool animated = animDriver != null && animDriver.TriggerDeath();
            Flinch(info.point, info.direction * (info.impulse * 1.5f));
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
