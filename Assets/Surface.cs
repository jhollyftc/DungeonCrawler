using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// What a hittable thing is MADE OF, for surface-specific impact effects
    /// (a sword bites flesh differently than it clangs off bone or stone).
    /// Extend freely.
    /// </summary>
    public enum SurfaceType { Stone, Flesh, Bone, Wood, Metal, Cloth }

    /// <summary>
    /// Tags an object with its SurfaceType so a hit can spawn the right VFX/SFX
    /// (via SurfaceLibrary). Put it only on the EXCEPTIONS — flesh, bone, wood,
    /// metal — because the untagged world (walls, floors, the kit shell) resolves
    /// to the default (Stone). That keeps authoring to the handful of things that
    /// aren't stone rather than tagging every wall.
    ///
    /// Resolved at hit time by walking UP from the struck collider, so one Surface
    /// on an NPC/prop root covers all its child colliders (including ragdoll bones).
    /// </summary>
    [DisallowMultipleComponent]
    public class Surface : MonoBehaviour
    {
        [Tooltip("What this object is made of. The world falls back to Stone without a Surface, so add this to flesh/bone/wood/metal things only.")]
        public SurfaceType type = SurfaceType.Flesh;

        /// <summary>Surface of whatever a collider belongs to; `fallback` if it isn't tagged (Stone for world, Flesh for a melee target, etc.).</summary>
        public static SurfaceType Of(Collider c, SurfaceType fallback = SurfaceType.Stone)
        {
            if (c == null) return fallback;
            var s = c.GetComponentInParent<Surface>();
            return s != null ? s.type : fallback;
        }

        /// <summary>Surface of an object by transform; walks up to the nearest Surface.</summary>
        public static SurfaceType Of(Transform t, SurfaceType fallback = SurfaceType.Stone)
        {
            if (t == null) return fallback;
            var s = t.GetComponentInParent<Surface>();
            return s != null ? s.type : fallback;
        }
    }
}
