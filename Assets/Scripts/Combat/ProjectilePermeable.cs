using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Marks a piece whose COLLIDER is solid but whose GEOMETRY has gaps — a barred prison door,
    /// a portcullis, a grille. A projectile that strikes its collider asks whether there is any
    /// actual mesh in the way; if the answer is no, it passes through and carries on to whatever
    /// is behind, still armed.
    ///
    /// WHY THIS RATHER THAN SHAPING THE COLLIDER: a door is a non-kinematic Rigidbody, and Unity
    /// refuses a non-convex MeshCollider on one of those — while a CONVEX mesh collider is a
    /// hull, which shrink-wraps a grille and fills in every gap. Boxing out each individual bar
    /// works and is the right answer when you want the gaps to be physically real, but it
    /// changes what NPCs see through, what sound is occluded by, and how the capsule behaves
    /// against a thin compound. This changes NOTHING about collision: the door still blocks
    /// everything it blocked before, and only projectiles get the finer question asked.
    ///
    /// OPT-IN, because the assumption is dangerous in general. Most props have a collider
    /// slightly larger than their mesh, and letting arrows through the difference would make
    /// them silently permeable at the edges. A piece has to declare that its gaps are real.
    ///
    /// The meshes MUST be Read/Write Enabled — `MeshRay` warns once per mesh naming it, since
    /// that import flag fails silently and in a player build only (§10).
    /// </summary>
    [DisallowMultipleComponent]
    public class ProjectilePermeable : MonoBehaviour
    {
        [Tooltip("How far past the contact point to look for real geometry, in metres. Size it to the piece's THICKNESS plus a little — a bar door only needs to see through its own depth.\n\nToo small and an arrow entering at a steep angle reports 'nothing there' while a bar sits just beyond the probe, so it passes through something it should have hit. Too large and it can find geometry belonging to the far side of a thick piece, which merely makes it stop, so err generous.")]
        public float maxPassDepth = 0.6f;

        [Tooltip("Fraction of speed a projectile keeps when it passes through a gap. 1 = unimpeded, which is right for an arrow through open air between bars. Lower it for something that should clip and slow — a lattice, foliage, a grate with a mesh backing.")]
        [Range(0.1f, 1f)] public float speedRetained = 1f;

        [Tooltip("Log every pass-through and every blocked shot, with the distance to the geometry that stopped it. The two outcomes are hard to tell apart in play — an arrow that stopped ON a bar and one that stopped on the box collider look the same from across a room.")]
        public bool debugPermeable = false;

        /// <summary>
        /// Gathered ONCE. `GetComponentsInChildren` allocates, and the alternative is doing it
        /// per impact for something that cannot change — a door's meshes are fixed for its life.
        /// </summary>
        readonly List<MeshFilter> filters = new List<MeshFilter>();
        readonly List<Collider> colliders = new List<Collider>();

        public IReadOnlyList<Collider> Colliders => colliders;

        void Awake()
        {
            GetComponentsInChildren(true, filters);
            GetComponentsInChildren(true, colliders);
        }

        /// <summary>
        /// Resolved from the exact collider struck, walking UP to the piece that owns it —
        /// a door's colliders routinely sit on a child of the leaf that carries the marker.
        /// </summary>
        public static ProjectilePermeable On(Collider c) =>
            c == null ? null : c.GetComponentInParent<ProjectilePermeable>();

        /// <summary>
        /// True when real geometry lies between `origin` and `maxPassDepth` along `dir`, with
        /// `distance` set to how far. False means the projectile is looking through a gap.
        ///
        /// FAILS CLOSED — an unreadable mesh, no MeshFilters, or a piece somehow marked but
        /// empty all report BLOCKED, so the projectile behaves exactly as it does today. The
        /// error is one-directional and deliberately the opposite of `KitSurface`'s: there, a
        /// failure leaves an arrow slightly proud, which is cosmetic; here, a failure would let
        /// arrows through a solid door, which is a gameplay hole.
        /// </summary>
        public bool Blocks(Vector3 origin, Vector3 dir, out float distance)
        {
            distance = 0f;
            if (filters.Count == 0)
            {
                WarnUntestable("it has no MeshFilters to test");
                return true;
            }

            bool hit = MeshRay.CastWorld(filters, origin, dir.normalized, maxPassDepth, out float t, out bool tested);

            // COULD NOT LOOK is not the same answer as LOOKED AND SAW A GAP, and collapsing the
            // two is what would make a door with unreadable meshes read as entirely
            // see-through. The first version returned `false` here and did exactly that.
            if (!tested)
            {
                WarnUntestable("none of its meshes are Read/Write Enabled");
                return true;
            }

            if (!hit) return false;
            distance = t;
            return true;
        }

        bool warned;

        void WarnUntestable(string why)
        {
            if (warned) return;
            warned = true;
            Debug.LogWarning($"[ProjectilePermeable] '{name}' cannot be tested because {why}, so it is treated as SOLID and projectiles behave exactly as they did before. Tick Read/Write Enabled on its model importer to enable shooting through the gaps.", this);
        }
    }
}
