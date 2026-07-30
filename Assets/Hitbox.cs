using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Marks one collider as a weak point — a head, a wing joint, an exposed core — so a
    /// hit there is worth more than the same hit to the torso.
    ///
    /// Deliberately a component on the COLLIDER rather than a name/tag/layer check, and
    /// read through a static helper, exactly like Surface.Of: any damage source can ask
    /// "what did I actually hit" without learning anything about anatomy, and a new
    /// creature declares its own weak points by authoring them. Bone-name matching would
    /// break the moment a rig is renamed, and a layer would burn one of 32 on something
    /// this narrow.
    ///
    /// LOOKUP IS ON THE COLLIDER'S OWN GameObject, not GetComponentInParent. Walking up
    /// would let a Hitbox anywhere above (say, on the NPC root) silently apply to every
    /// collider beneath it, so every torso hit would read as a headshot. Put the Hitbox
    /// on the same object as the collider it describes.
    ///
    /// NPCs already carry a full set of dormant ragdoll bone colliders, so the head
    /// sphere is available as a target whether the goblin is alive or dead.
    /// </summary>
    [DisallowMultipleComponent]
    public class Hitbox : MonoBehaviour
    {
        [Tooltip("What this weak point IS — 'Head', 'Core'. Used for logs now, and it's the hook for a distinct headshot sound or VFX later.")]
        public string label = "Head";
        [Tooltip("Damage multiplier for hits landing on this collider. Applied to the damage amount only: knockback stays unscaled, because where you hit something shouldn't change how hard it's shoved.")]
        [Min(0f)] public float damageMultiplier = 2.5f;

        /// <summary>The Hitbox on this exact collider, or null.</summary>
        public static Hitbox On(Collider c) => c != null ? c.GetComponent<Hitbox>() : null;

        /// <summary>Damage multiplier for a hit on this collider — 1 if it isn't a weak point.</summary>
        public static float MultiplierOf(Collider c, float fallback = 1f)
        {
            Hitbox h = On(c);
            return h != null ? Mathf.Max(0f, h.damageMultiplier) : fallback;
        }
    }
}
