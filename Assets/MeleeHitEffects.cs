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
    /// </summary>
    [RequireComponent(typeof(MeleeAttack))]
    [DisallowMultipleComponent]
    public class MeleeHitEffects : MonoBehaviour
    {
        [Tooltip("Shared surface table — what each material looks/sounds like when struck.")]
        public SurfaceLibrary surfaceLibrary;
        [Tooltip("Surface used when the victim has no Surface component. A melee target is almost always Flesh, so that's the sensible default here (the WORLD defaults to Stone elsewhere).")]
        public SurfaceType defaultTargetSurface = SurfaceType.Flesh;
        [Tooltip("3D source for pitched impact SFX. Left empty, a positioned one-shot is used (no pitch variation).")]
        public AudioSource hitSource;

        MeleeAttack melee;

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();
            if (surfaceLibrary == null)
                Debug.LogWarning("[MeleeHitEffects] No SurfaceLibrary assigned — hits will land but spawn no impact effect.", this);
        }

        void OnEnable() => melee.OnHitLanded += HandleHit;
        void OnDisable() => melee.OnHitLanded -= HandleHit;

        void HandleHit(IDamageable victim, DamageInfo info)
        {
            SurfaceType surface = Surface.Of(victim.Transform, defaultTargetSurface);
            SurfaceImpact.Spawn(surfaceLibrary, surface, info.point, info.direction, hitSource);
        }
    }
}
