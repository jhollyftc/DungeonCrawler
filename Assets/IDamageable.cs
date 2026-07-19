using UnityEngine;

namespace DungeonGen
{
    public enum DamageType { Melee, Thrown, Fall, Environment }

    /// <summary>
    /// Everything about one instance of harm. Passed by ref (in) — it's a struct
    /// so a hit allocates nothing.
    /// </summary>
    public struct DamageInfo
    {
        public float amount;
        public Vector3 point;        // world contact
        public Vector3 direction;    // normalized, from attacker toward victim
        public GameObject instigator;// who caused it (may be null for environment)
        public DamageType type;
        public float impulse;        // knockback strength (m/s) — victims decide what it means
    }

    /// <summary>
    /// Something that can be hurt. Health implements it for both the player and
    /// NPCs, so a goblin's sword and a thrown barrel damage either with no
    /// special-casing — same philosophy as IPushable: the attacker supplies the
    /// force, the victim decides what it means.
    /// </summary>
    public interface IDamageable
    {
        void TakeDamage(in DamageInfo info);
        bool IsDead { get; }
        Transform Transform { get; }
    }
}
