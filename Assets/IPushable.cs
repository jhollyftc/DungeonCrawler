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
    }
}
