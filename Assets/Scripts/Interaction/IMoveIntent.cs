namespace DungeonGen
{
    /// <summary>
    /// A mover's INTENDED horizontal speed (m/s) — how hard it is trying to move this
    /// frame, independent of what it actually achieved. CharacterControllerPhysicsPush
    /// scales its shove by this instead of the CharacterController's achieved velocity,
    /// because a blocking obstacle (a shut door you're leaning on) zeroes the achieved
    /// velocity — which would collapse the push to nothing exactly when you need it.
    /// Intent stays high while you push, so the door swings open under controlled torque
    /// instead of being tipped off its hinge by raw depenetration.
    ///
    /// The player (FirstPersonController) reports input×speed; an NPC could report its
    /// agent's desired velocity. Sneaking still eases doors open — crouching lowers the
    /// INTENDED speed, so the push is genuinely gentle rather than merely stalled.
    /// </summary>
    public interface IMoveIntent
    {
        float IntendedSpeed { get; }
    }
}
