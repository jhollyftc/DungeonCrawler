using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Lets the CharacterController push dynamic rigidbodies it walks into.
    ///
    /// Hinged doors are a special case and are NOT pushed with a linear force:
    /// AddForceAtPosition injects linear velocity at the centre of mass, which a
    /// HingeJoint then has to cancel every frame — that fight is what rips a door
    /// off its hinge. PhysicsDoor.Push() converts the contact into pure torque
    /// about the hinge axis instead, so the joint never sees linear motion.
    ///
    /// Everything else (crates, barrels) is unconstrained, so a normal impulse
    /// at the contact point is correct for them.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class CharacterControllerPhysicsPush : MonoBehaviour
    {
        [Header("Push")]
        [Tooltip("Impulse applied on contact. MASS-AWARE (ForceMode.Impulse): heavy things resist, light things fly. Retune from scratch — this does not map to the old VelocityChange value.")]
        [SerializeField] private float pushForce = 15f;

        [Tooltip("Don't keep accelerating a loose body once it's already moving this fast (m/s). Doors clamp their own SWING speed instead — see PhysicsDoor.")]
        [SerializeField] private float maximumPushSpeed = 3f;

        [Tooltip("Ignore contacts pointing this far downward — i.e. don't shove whatever you're standing on.")]
        [SerializeField] private float standingOnThreshold = -0.3f;

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody body = hit.collider.attachedRigidbody;
            if (body == null || body.isKinematic)
                return;

            // Don't push objects we're standing on.
            if (hit.moveDirection.y < standingOnThreshold)
                return;

            // Push horizontally only — never lift or drive things into the floor.
            Vector3 pushDirection = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            if (pushDirection.sqrMagnitude < 0.0001f)
                return;
            pushDirection.Normalize();

            // A hinged door only rotates. Hand it the contact and let it convert
            // that into torque about its own hinge axis (it also clamps its own
            // swing speed — a door's LINEAR velocity is ~0 by design, so a linear
            // speed clamp here would never fire and the pushes would compound).
            if (body.TryGetComponent(out PhysicsDoor door))
            {
                door.Push(hit.point, pushDirection, pushForce);
                return;
            }

            // Loose bodies: unconstrained, so a linear impulse is fine.
            Vector3 horizontalVelocity = new Vector3(body.linearVelocity.x, 0f, body.linearVelocity.z);
            if (horizontalVelocity.magnitude >= maximumPushSpeed)
                return;

            body.AddForceAtPosition(pushDirection * pushForce, hit.point, ForceMode.Impulse);
        }
    }
}


