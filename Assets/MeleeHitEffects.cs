using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Spawns a SURFACE-SPECIFIC impact effect where a melee hit lands — the thing
    /// that SELLS contact when the weapon is a depth-cleared overlay viewmodel: the
    /// blade can't geometrically touch the enemy, so a world-space burst on the
    /// enemy bridges the gap and the eye reads it as a strike.
    ///
    /// It resolves what was hit (Surface.Of the victim) and hands off to
    /// SurfaceImpact, so flesh sprays, bone clatters, a metal shield sparks — all
    /// from the one shared SurfaceLibrary. Put it on the attacker (the player,
    /// beside MeleeAttack); it listens to OnHitLanded, which carries the world
    /// contact point and blow direction.
    ///
    /// ALSO listens to OnEnvironmentHit — a swing that found no living target but
    /// connected with a wall, door, or prop still sparks off THAT surface, same
    /// SurfaceLibrary, same visual language as a landed blow. Without this, whiffing
    /// against the world was silent and invisible even when the blade clearly caught
    /// stone or wood.
    /// </summary>
    [RequireComponent(typeof(MeleeAttack))]
    [DisallowMultipleComponent]
    public class MeleeHitEffects : MonoBehaviour
    {
        [Tooltip("Shared surface table — what each material looks/sounds like when struck.")]
        public SurfaceLibrary surfaceLibrary;
        [Tooltip("Surface used when the victim has no Surface component. A melee target is almost always Flesh, so that's the sensible default here (the WORLD defaults to Stone elsewhere).")]
        public SurfaceType defaultTargetSurface = SurfaceType.Flesh;
        [Tooltip("Surface used when a struck wall/door/prop has no Surface component. The world default is Stone; tag doors/props with a Surface component (Wood/Metal/...) to get their real material instead.")]
        public SurfaceType defaultEnvironmentSurface = SurfaceType.Stone;
        [Tooltip("3D source for pitched impact SFX. Left empty, a positioned one-shot is used (no pitch variation).")]
        public AudioSource hitSource;

        MeleeAttack melee;

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();
            if (surfaceLibrary == null)
                Debug.LogWarning("[MeleeHitEffects] No SurfaceLibrary assigned — hits will land but spawn no impact effect.", this);
        }

        void OnEnable()
        {
            melee.OnHitLanded += HandleHit;
            melee.OnEnvironmentHit += HandleEnvironmentHit;
        }

        void OnDisable()
        {
            melee.OnHitLanded -= HandleHit;
            melee.OnEnvironmentHit -= HandleEnvironmentHit;
        }

        void HandleHit(IDamageable victim, DamageInfo info)
        {
            SurfaceType surface = Surface.Of(victim.Transform, defaultTargetSurface);
            SurfaceImpact.Spawn(surfaceLibrary, surface, info.point, info.direction, hitSource);
        }

        void HandleEnvironmentHit(Vector3 point, Vector3 direction, Collider hit)
        {
            SurfaceType surface = Surface.Of(hit, defaultEnvironmentSurface);
            SurfaceImpact.Spawn(surfaceLibrary, surface, point, direction, hitSource);
        }
    }
}
