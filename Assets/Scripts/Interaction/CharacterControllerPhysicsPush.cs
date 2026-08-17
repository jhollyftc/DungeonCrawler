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
        private IMoveIntent moveIntent;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            moveIntent = GetComponent<IMoveIntent>();   // player reports input×speed; null = fall back to achieved velocity

            // AM I THE PLAYER? This component is deliberately identical on the player and on
            // every NPC — that is what makes them push doors through one code path that cannot
            // drift — so nothing else in here knows or cares. The one place it matters is
            // player-facing FEEDBACK: an NPC shoving a locked door should rattle it and be
            // heard, but must not tell the player it is locked.
            //
            // FirstPersonController rather than a tag or layer: it is unambiguous, already
            // required on the player, and cannot be accidentally set on a goblin.
            isPlayer = GetComponent<FirstPersonController>() != null;
        }

        private bool isPlayer;

        [Tooltip("Don't keep accelerating a loose body once it's already moving this fast (m/s). Doors clamp their own SWING speed instead — see PhysicsDoor.")]
        [SerializeField] private float maximumPushSpeed = 3f;

        [Tooltip("Ignore contacts pointing this far downward — i.e. don't shove whatever you're standing on.")]
        [SerializeField] private float standingOnThreshold = -0.3f;

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody body = hit.collider.attachedRigidbody;
            if (body == null) return;

            // A prop that spawns asleep IS kinematic, so the filter below would discard the
            // very contact meant to wake it — the wake inside PushableProp.Push can never be
            // reached, and the prop stays inert forever while reading as "pushing is broken".
            //
            // Deliberately narrow: ONLY a PropPhysicsSleep wakes here. PhysicsDoor also goes
            // kinematic on purpose (the standoff jam, §10), and waking that would undo the
            // one thing keeping a shoved-from-both-sides door from launching the pusher
            // through it.
            if (body.isKinematic)
            {
                var sleeper = body.GetComponent<PropPhysicsSleep>();
                if (sleeper != null && !sleeper.IsAwake) sleeper.Wake();
            }
            // A LOCKED DOOR MUST STILL HEAR THE SHOVE. It is kinematic — that is what makes it
            // immovable — so the bail-out below would swallow the very contact the lock exists
            // to respond to, and the rattle, the sound and the "it's locked" message would all
            // be dead with nothing to debug. The identical shape as the PropPhysicsSleep case
            // directly above: kinematic filtered before IPushable was ever consulted.
            //
            // DELIBERATELY GATED ON IsLocked, NOT ON isKinematic. A door also goes kinematic for
            // the standoff JAM, and dispatching there would defeat the one thing that keeps a
            // door shoved from both sides from launching the pusher through it — which is
            // exactly why locking got its own flag instead of being inferred from the rigidbody
            // state.
            if (body.isKinematic)
            {
                var lockedDoor = body.GetComponent<PhysicsDoor>();
                if (lockedDoor != null && lockedDoor.IsLocked)
                {
                    Vector3 shove = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
                    if (shove.sqrMagnitude > 0.0001f)
                    {
                        // Intent, not achieved: leaning on a door you cannot move collapses
                        // achieved velocity to nothing, and the rattle would then read as a
                        // feeble tap however hard you were pressing.
                        float lockedForce = pushForce * CurrentPushScale(true)
                                          * (Time.deltaTime * referenceFrameRate);
                        // WHO pushed matters here and nowhere else in this component. NPCs run
                        // this script verbatim, so without it a goblin crowding a locked door
                        // told the PLAYER it was locked.
                        lockedDoor.ShoveLocked(lockedForce, isPlayer);
                    }
                }
            }

            if (body.isKinematic) return;   // still kinematic = genuinely not pushable

            // Don't push objects we're standing on.
            if (hit.moveDirection.y < standingOnThreshold)
                return;

            // Push horizontally only — never lift or drive things into the floor.
            Vector3 pushDirection = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            if (pushDirection.sqrMagnitude < 0.0001f)
                return;
            pushDirection.Normalize();

            // If the object knows how to be pushed, let IT decide what the shove
            // means. A PhysicsDoor turns it into torque about its hinge (a linear
            // force would fight the joint and tear the door off); a PushableProp
            // applies its own multiplier and speed cap. The player just supplies
            // the force — objects own their own response, so tuning a barrel can
            // never un-tune the doors. Resolved FIRST because the object also picks
            // whether the push scales by intent (doors) or achieved velocity (props).
            IPushable pushable = body.GetComponent<IPushable>();
            bool useIntent = pushable != null && pushable.PreferIntentPush;

            // How hard you shove follows how fast you're moving. This is the whole
            // sneak mechanic: crouch → slow → gentle push → the door barely swings →
            // it never passes the door's thunkArmAngle → silent. Doors read INTENDED
            // speed (so leaning on a stuck door still opens it); props read ACHIEVED
            // (so a heavy prop stalls you and resists) — see IPushable.PreferIntentPush.
            //
            // The Time.deltaTime term is the FRAMERATE FIX. This runs once per
            // frame and delivers an Impulse (instantaneous, time-agnostic), so raw
            // it would hand the door (force x framerate) of momentum per second —
            // fast PCs open doors, slow PCs can't. Multiplying by (deltaTime x
            // referenceFrameRate) makes the per-second delivery identical on every
            // machine and equal to what `force` means at the reference rate.
            float force = pushForce * CurrentPushScale(useIntent) * (Time.deltaTime * referenceFrameRate);

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

        private float CurrentPushScale(bool useIntent)
        {
            if (!scaleByPlayerSpeed || controller == null) return 1f;

            Vector3 v = controller.velocity;
            float achieved = new Vector3(v.x, 0f, v.z).magnitude;

            // Doors (useIntent) prefer INTENDED speed: shouldering a door stalls the
            // controller so achieved velocity collapses to ~0, which would starve the
            // push exactly when you want the door to open — intent stays high while you
            // lean. Max() so a fast free walk still reads full. Props leave useIntent
            // false and scale by ACHIEVED velocity, so a heavy prop stalls you and the
            // push collapses — it resists and slows you (the momentum feel). NPCs have no
            // IMoveIntent, so intent falls back to achieved too.
            float speed = (useIntent && moveIntent != null) ? Mathf.Max(moveIntent.IntendedSpeed, achieved) : achieved;
            return Mathf.Clamp(speed / Mathf.Max(0.01f, speedForFullPush), minimumPushScale, 1f);
        }
    }
}


