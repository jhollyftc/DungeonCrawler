using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Whose side this thing is on. One component, read by combat (MeleeAttack
    /// won't hit same-faction targets — goblins don't cut each other down in a
    /// doorway scrum) and eventually by perception/shouts. Reuses the Faction
    /// enum the NoiseBus already defined, so hearing and hitting can never
    /// disagree about sides.
    /// </summary>
    [DisallowMultipleComponent]
    public class FactionMember : MonoBehaviour
    {
        public Faction faction = Faction.Neutral;

        /// <summary>Faction of an object, walking up the hierarchy; Neutral if untagged.</summary>
        public static Faction Of(Transform t)
        {
            if (t == null) return Faction.Neutral;
            var m = t.GetComponentInParent<FactionMember>();
            return m != null ? m.faction : Faction.Neutral;
        }
    }
}
