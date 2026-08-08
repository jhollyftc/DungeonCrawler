using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Something on a VICTIM that gets to alter a blow before it lands — a raised shield, a
    /// parry, future armour or a magic ward.
    ///
    /// Lives on the victim and is consulted inside Health.TakeDamage, NOT in the attacker.
    /// That is the same split as IPushable: the attacker supplies force, the victim decides
    /// what it means. Doing it this way, blocking works against melee, arrows, thrown
    /// barrels and environmental damage the moment it exists, with no damage source ever
    /// learning that guarding is a thing. Putting the check in MeleeAttack instead would mean
    /// adding "is the victim blocking?" to every damage source forever, and the ones added
    /// later would quietly forget.
    ///
    /// The blow is passed BY REF so a mitigator can reduce `amount`, kill `impulse` so a
    /// blocked hit doesn't also shove you, or zero `poiseDamage`. Returning an outcome lets
    /// Health report what happened without knowing how it happened.
    ///
    /// A mitigator that PUNISHES the attacker (a parry staggering whoever swung) does that
    /// itself, reaching them through DamageInfo.instigator. That deliberately keeps the
    /// punish out of Health and out of the IDamageable contract — no return value needed
    /// anywhere, and each attacker still decides what being parried costs it
    /// (MeleeAttack.Parried).
    /// </summary>
    public interface IDamageMitigator
    {
        /// <summary>
        /// Alter the incoming blow in place. Called before any damage is applied. Mitigators
        /// run in component order and each sees the previous one's result, so they stack.
        /// </summary>
        Mitigation Mitigate(ref DamageInfo info);

        /// <summary>Lower runs FIRST. A parry should resolve before flat armour reduction.</summary>
        int MitigationOrder => 0;
    }

    /// <summary>
    /// What a mitigator did, and what the blow ACTUALLY struck.
    ///
    /// The surface override exists because a blocked hit did not land on the victim at all —
    /// it landed on their shield. Without it the attacker's MeleeHitEffects resolves
    /// `Surface.Of(victim)` and sprays blood for a hit that rang off metal. The mitigator is
    /// the only thing that knows what interposed itself, so it says so, and the surface system
    /// stays a single seam (SurfaceImpact.Spawn) rather than growing a special case.
    /// </summary>
    public struct Mitigation
    {
        public DamageOutcome outcome;
        /// <summary>True if `surface` should replace the victim's own for impact VFX/SFX.</summary>
        public bool overrideSurface;
        public SurfaceType surface;

        public static Mitigation None => default;

        public static Mitigation Of(DamageOutcome outcome, SurfaceType surface) => new Mitigation
        {
            outcome = outcome,
            overrideSurface = true,
            surface = surface,
        };
    }

    /// <summary>What a mitigator did to a blow — for feedback (VFX/SFX/UI), not for logic.</summary>
    public enum DamageOutcome
    {
        /// <summary>Untouched — the hit lands in full.</summary>
        None,
        /// <summary>Absorbed in part. The victim still pays something (poise, chip damage).</summary>
        Blocked,
        /// <summary>Turned aside cleanly, and the attacker is punished for it.</summary>
        Parried,
    }
}
