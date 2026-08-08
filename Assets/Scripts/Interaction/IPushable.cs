using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Something the player can shove by walking into it.
    ///
    /// The split that matters: the PLAYER decides how hard it pushes (a
    /// speed-scaled impulse — sprint shoves hard, crouch barely nudges), and the
    /// OBJECT decides what that force MEANS for it. A door converts the shove into
    /// torque about its hinge; a barrel rolls; a heavy statue could ignore most of
    /// it. None of that leaks back into the player's tuning, which is already
    /// dialled in for doors.
    ///
    /// A plain Rigidbody with no IPushable still gets a sensible default shove
    /// from CharacterControllerPhysicsPush — implement this only when an object
    /// needs to interpret the push differently, or wants its own strength.
    /// </summary>
    public interface IPushable
    {
        /// <param name="contactPoint">World-space point of contact.</param>
        /// <param name="pushDirection">Normalised, horizontal.</param>
        /// <param name="force">Impulse magnitude the player is delivering, already scaled by how fast they're moving.</param>
        void Push(Vector3 contactPoint, Vector3 pushDirection, float force);

        /// <summary>
        /// Which speed the player's push scales by. FALSE (default, props) = ACHIEVED
        /// velocity: leaning on a heavy prop stalls you, so the push collapses and the
        /// prop resists and slows you — the momentum feel. TRUE (doors) = INTENDED speed:
        /// a door you shoulder blocks you too, but it SHOULD open, so the push stays
        /// strong while you lean rather than collapsing to nothing. The two want opposite
        /// things from the same stall, so the object picks.
        /// </summary>
        bool PreferIntentPush => false;

        /// <summary>
        /// A ONE-SHOT deliberate blow — a shield bash's cone shove — as opposed to the
        /// per-frame contact push Push() is built for.
        ///
        /// The distinction exists because of the SPEED CAP. Push() refuses once the prop is
        /// already moving at maxPushSpeed, which is essential for contact: that fires every
        /// frame you lean on something and would otherwise accelerate it without limit. A
        /// burst fires once per attack (the sweep dedupes), so the cap protects nothing and
        /// instead silently EATS the blow — the capsule's contact push has usually already
        /// pushed the prop past the cap by the time the attack's cone resolves, so the bash
        /// appeared to do nothing except when it reached the prop before the player did.
        ///
        /// Defaults to Push(), so an implementer that doesn't care (a door — hinge torque has
        /// its own clamps) needs no changes.
        /// </summary>
        void PushBurst(Vector3 contactPoint, Vector3 pushDirection, float impulse)
            => Push(contactPoint, pushDirection, impulse);
    }
}
