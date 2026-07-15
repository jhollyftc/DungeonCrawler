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
        [Tooltip("Impulse applied on contact at FULL speed. MASS-AWARE (ForceMode.Impulse): heavy things resist, light things fly.")]
        [SerializeField] private float pushForce = 15f;

        [Tooltip("Framerate the push force is calibrated against. OnControllerColliderHit fires ONCE PER FRAME, and an Impulse ignores time, so without this the momentum delivered per second is (force x framerate): a 144Hz PC shoves a door ~3x harder than a 45Hz PC, and on a slow machine the door never beats its self-closing spring. This normalizes delivery so every framerate matches what 'force' does at THIS reference rate. Leave at 60 unless you know your tuning machine's fps.")]
        [SerializeField] private float referenceFrameRate = 60f;

        [Header("Speed scaling (this is what makes sneaking work)")]
        [Tooltip("Scale the push by how fast the player is ACTUALLY moving. A crouched player eases a door open instead of banging it, so it stays under the door's noise threshold — stealth falls out of the physics instead of being special-cased. Turn off for a constant shove.")]
        [SerializeField] private bool scaleByPlayerSpeed = true;
        [Tooltip("Player speed (m/s) that delivers the FULL push force. Set to your sprint speed so sprinting slams, walking opens, and crouching barely nudges.")]
        [SerializeField] private float speedForFullPush = 8f;
        [Tooltip("Floor on the scaling, so a near-stationary lean still budges things a little.")]
        [Range(0f, 1f)][SerializeField] private float minimumPushScale = 0.05f;

        private CharacterController controller;

        private void Awake() => controller = GetComponent<CharacterController>();

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

            // How hard you shove follows how fast you're actually moving. This is
            // the whole sneak mechanic: crouch → slow → gentle push → the door
            // barely swings → it never passes the door's thunkArmAngle → silent.
            //
            // The Time.deltaTime term is the FRAMERATE FIX. This runs once per
            // frame and delivers an Impulse (instantaneous, time-agnostic), so raw
            // it would hand the door (force x framerate) of momentum per second —
            // fast PCs open doors, slow PCs can't. Multiplying by (deltaTime x
            // referenceFrameRate) makes the per-second delivery identical on every
            // machine and equal to what `force` means at the reference rate.
            float force = pushForce * CurrentPushScale() * (Time.deltaTime * referenceFrameRate);

            // If the object knows how to be pushed, let IT decide what the shove
            // means. A PhysicsDoor turns it into torque about its hinge (a linear
            // force would fight the joint and tear the door off); a PushableProp
            // applies its own multiplier and speed cap. The player just supplies
            // the force — objects own their own response, so tuning a barrel can
            // never un-tune the doors.
            IPushable pushable = body.GetComponent<IPushable>();
            if (pushable != null)
            {
                pushable.Push(hit.point, pushDirection, force);
                return;
            }

            // Plain Rigidbody, no IPushable: a sensible default shove. Mass-aware,
            // so heavy things resist without any configuration.
            Vector3 horizontalVelocity = new Vector3(body.linearVelocity.x, 0f, body.linearVelocity.z);
            if (horizontalVelocity.magnitude >= maximumPushSpeed)
                return;

            body.AddForceAtPosition(pushDirection * force, hit.point, ForceMode.Impulse);
        }

        private float CurrentPushScale()
        {
            if (!scaleByPlayerSpeed || controller == null) return 1f;

            Vector3 v = controller.velocity;
            float speed = new Vector3(v.x, 0f, v.z).magnitude;
            return Mathf.Clamp(speed / Mathf.Max(0.01f, speedForFullPush), minimumPushScale, 1f);
        }
    }
}


