using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// What a thing is MADE OF — for surface-specific impact effects (a sword bites flesh
    /// differently than it clangs off bone or stone) AND for footsteps.
    ///
    /// ONE ENUM, DELIBERATELY. The sound plan originally proposed a second, footstep-only
    /// enum (Stone/Gravel/Water/Bone/Wood) alongside this one. Two enums that both answer
    /// "what is this made of" WILL drift, and the divergence shows up as a wooden bridge
    /// that sparks like stone under a sword but thuds like wood underfoot. Extending this
    /// one costs two values and keeps SurfaceLibrary as the single asset answering what a
    /// material does when struck AND when walked on.
    ///
    /// APPEND ONLY. `Surface.type` is serialized by INDEX on every tagged prop and NPC in
    /// the project, so inserting a value in the middle silently re-materials all of them —
    /// the same reason DamageType.Projectile was appended rather than filed tidily.
    /// </summary>
    public enum SurfaceType { Stone, Flesh, Bone, Wood, Metal, Cloth, Gravel, Water }

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

        /// <summary>
        /// What is UNDERFOOT at `feet` — a short downward ray plus the same `Of` lookup melee
        /// already uses, so a wooden bridge sounds wooden with zero extra authoring (the bridge
        /// prefab already wants a Surface for sword hits).
        ///
        /// SURFACE IS A PROPERTY OF THE CELL, NOT THE ROOM TYPE. That is why this is a probe
        /// rather than a lookup on the room's style: RoomStyle already carries hallwayFloor,
        /// pitFloor and prisonFloor prefabs separately, and a per-room-type setting simply
        /// cannot express "you just stepped onto a wooden bridge over a pit" — one of the most
        /// distinctive moments the generator produces.
        ///
        /// `mask` MUST exclude the NPC layer, or a goblin in a crowd samples the surface of
        /// whoever it is standing next to (the same rule NpcFootIK's groundMask carries).
        /// </summary>
        public static SurfaceType Below(Transform feet, float rayUp, float rayDown, LayerMask mask,
                                        SurfaceType fallback = SurfaceType.Stone)
            => TryBelow(feet, rayUp, rayDown, mask, out SurfaceType t) ? t : fallback;

        /// <summary>
        /// As <see cref="Below"/>, but reports whether a Surface component was actually FOUND
        /// rather than collapsing "hit untagged geometry" into the fallback.
        ///
        /// THE DISTINCTION IS LOAD-BEARING, because most of the dungeon cannot be tagged this
        /// way at all. `DungeonMesher.Build` emits the ENTIRE shell — every floor, wall and
        /// ceiling — as ONE GameObject with ONE MeshCollider, so a Surface on it would tag the
        /// whole dungeon a single material. Only the handful of kit pieces that get their own
        /// collider GameObject (stairs, archways, doors, columns, ladders, corner pillars) and
        /// ordinary props are reachable by this probe.
        ///
        /// So "false" means "you hit the greybox shell, ask the GENERATOR what this cell is"
        /// — which is exactly what FootstepSurface does next. Authoring a Surface onto a floor
        /// prefab looks correct and silently does nothing; this is why.
        /// </summary>
        public static bool TryBelow(Transform feet, float rayUp, float rayDown, LayerMask mask,
                                    out SurfaceType type)
        {
            type = SurfaceType.Stone;
            if (feet == null) return false;

            // Start ABOVE the feet: a capsule rests fractionally inside the floor collider, and
            // a ray starting inside a collider does not report it (the CheckSphere lesson from
            // ViewmodelCollision and MeleeAttack, in its raycast form).
            Vector3 origin = feet.position + Vector3.up * rayUp;
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayUp + rayDown,
                                 mask, QueryTriggerInteraction.Ignore))
                return false;

            var s = hit.collider.GetComponentInParent<Surface>();
            if (s == null) return false;
            type = s.type;
            return true;
        }
    }
}
