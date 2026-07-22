using UnityEngine;

namespace DungeonGen
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(HingeJoint))]
    public class PhysicsDoor : MonoBehaviour, IPushable
    {
        [Header("Opening Limits")]
        [SerializeField] private float minimumAngle = -100f;
        [SerializeField] private float maximumAngle = 100f;

        [Header("Self Closing")]
        [SerializeField] private float closedAngle = 0f;
        [SerializeField] private float springStrength = 30f;
        [SerializeField] private float springDamper = 8f;
        [SerializeField] private bool useAcceleration = true;

        [Header("Door Physics")]
        [SerializeField] private float doorMass = 20f;
        [SerializeField] private float angularDamping = 2f;
        [Tooltip("Max speed (m/s) the physics engine may shove this door out of an overlapping collider — THE fix for 'the player's capsule launches the door open'. Unity's default (~10) means any real penetration ejects a mass-20 door at up to 10 m/s, so it flies open with NO push (depenetration, not torque). Clamped LOW, the capsule can't push through faster than the door separates, so the door RESISTS and slows you, and the controlled hinge torque does the opening. Also stops the door being knocked off its hinge by a penetration spike. These kit doors have a thin world-space collider (tiny mesh × large scale), so they need a very gentle 0.1; raise it only if a door ever spawns embedded and can't wriggle free.")]
        [SerializeField] private float maxDepenetrationVelocity = 0.1f;

        [Header("Push")]
        [Tooltip("Stop adding torque once the door is already swinging this fast (rad/s). Clamping ANGULAR speed is the point — a hinged door's LINEAR velocity is ~0 by design, so a linear clamp would never fire and pushes would compound until the joint tore.")]
        [SerializeField] private float maxSwingSpeed = 6f;
        [Tooltip("Log every push (world axis, lever, torque, constraints). Turn on when a door won't move — it pinpoints a bad hinge axis, a zero lever, or a blocking constraint immediately.")]
        [SerializeField] private bool debugPush = false;

        [Header("Swing (one-way-per-swing)")]
        [Tooltip("Degrees the door must open before the OPPOSITE limit snaps shut at 0. From then on the door cannot swing THROUGH closed — it stops dead there and thunks, like a real door meeting its frame. Once it settles, the full range is restored so the next push can go either way. Keep SMALL — this is the physical stop, not the sound.")]
        [SerializeField] private float commitAngle = 3f;
        [Tooltip("Degrees the door must actually swing open before a CLOSING THUNK is allowed. Guards against a shoving match (player vs NPC across the door) jittering it around 0 and machine-gunning the thunk — a door barely nudged off its frame shouldn't bang shut. Deliberately much larger than Commit Angle.")]
        [SerializeField] private float thunkArmAngle = 20f;
        [Tooltip("Within this many degrees of closed counts as shut.")]
        [SerializeField] private float closedTolerance = 1.5f;
        [Tooltip("Below this swing speed (rad/s) the door counts as settled/at rest.")]
        [SerializeField] private float settleSpeed = 0.25f;
        [Tooltip("Swing speed (rad/s) above which the door counts as actually moving (fires OnSwingStart — the creak).")]
        [SerializeField] private float swingStartSpeed = 0.6f;
        [Tooltip("Within this many degrees of the OPEN limit counts as reaching the stop.")]
        [SerializeField] private float slamTolerance = 3f;
        [Tooltip("Speed (rad/s) the door must STILL be carrying when it reaches the open stop to count as a slam. The self-closing spring decelerates the door on the way out, so it usually COASTS into the limit — reaching 90° is not the same as slamming into it. Lower this if you want slams to fire more readily; the telemetry reports the actual arrival speed.")]
        [SerializeField] private float slamMinSpeed = 1.5f;
        [Tooltip("Log peak swing speed / peak angle / close-impact speed after every swing. Turn on, sprint through the door, then nudge it gently — the numbers tell you exactly what to set Full Force Speed, Thunk Arm Angle and Silent Below Speed to. No guessing.")]
        [SerializeField] private bool logSwingTelemetry = false;

        /// <summary>Door began moving from rest. float = swing speed (rad/s).</summary>
        public event System.Action<float> OnSwingStart;
        /// <summary>Door hit the closed stop. float = impact speed (rad/s) — scale volume by this.</summary>
        public event System.Action<float> OnClosed;
        /// <summary>Door slammed against its open limit. float = impact speed (rad/s).</summary>
        public event System.Action<float> OnSlamOpen;

        private enum Side { Closed, Positive, Negative }
        private Side side = Side.Closed;
        private bool thunked;
        private bool slammed;
        private bool swinging;
        // Widest angle reached during the current swing. The closing thunk only
        // fires if this got past thunkArmAngle, so a door jittered a few degrees
        // off its frame (a shoving match) stays silent instead of machine-gunning.
        private float peakAngle;
        private float peakSpeed;        // fastest this swing got (rad/s) — telemetry
        private float closeImpactSpeed; // speed at the moment it hit the closed stop
        private bool thunkSounded;
        private bool reachedOpenLimit;  // got within slamTolerance of the open stop
        private float openArrivalSpeed; // how fast it was still going when it got there
        // reachedOpenLimit/slammed get RESET as the door swings back off the stop,
        // so they'd read false by the time the settle summary prints. These two
        // remember what actually happened during the swing, for the log.
        private bool slamSoundedThisSwing;
        private float bestOpenArrivalSpeed;

        /// <summary>Live swing speed (rad/s). Audio polls this to drive a looping creak.</summary>
        public float SwingSpeed => doorBody != null ? doorBody.angularVelocity.magnitude : 0f;
        // The joint limit kills the velocity in the same step it's hit, so the
        // impact speed must come from the PREVIOUS step or every thunk reads 0.
        private float previousSpeed;

        private Rigidbody doorBody;
        private HingeJoint hinge;

        // Closed pose + hinge axis in WORLD space, captured once. We measure the
        // door's angle from these rather than reading HingeJoint.angle, which is
        // unreliable — it was intermittently returning 0 and even NaN (NaN then
        // poisoned peakAngle through Mathf.Max and corrupted the commit/limit
        // logic). Rotating about an axis leaves that axis unchanged, so the world
        // axis stays valid for the door's whole life.
        private Quaternion closedRotation;
        private Vector3 axisWorld;

        /// <summary>
        /// Signed door angle in degrees about the hinge axis, relative to closed.
        /// Computed from the transform — never NaN.
        /// </summary>
        private float CurrentAngle
        {
            get
            {
                Quaternion delta = transform.rotation * Quaternion.Inverse(closedRotation);
                delta.ToAngleAxis(out float degrees, out Vector3 deltaAxis);

                // ToAngleAxis gives 0..360; fold the far half back with a flipped axis.
                if (degrees > 180f)
                {
                    degrees = 360f - degrees;
                    deltaAxis = -deltaAxis;
                }
                if (float.IsNaN(degrees) || degrees < 0.0001f) return 0f;

                return degrees * (Vector3.Dot(deltaAxis, axisWorld) >= 0f ? 1f : -1f);
            }
        }

        private void Awake()
        {
            doorBody = GetComponent<Rigidbody>();
            hinge = GetComponent<HingeJoint>();

            closedRotation = transform.rotation;
            axisWorld = transform.TransformDirection(hinge.axis).normalized;

            ConfigureRigidbody();
            ConfigureHinge();
        }

        private void ConfigureRigidbody()
        {
            doorBody.mass = doorMass;
            doorBody.useGravity = false;              // vertical hinge: gravity only stresses the joint
            doorBody.isKinematic = false;
            doorBody.interpolation = RigidbodyInterpolation.Interpolate;
            doorBody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // Belt-and-suspenders against off-hinge TOPPLE: freeze the two rotation axes
            // PERPENDICULAR to the hinge, leaving the hinge axis free to swing. The
            // HingeJoint already constrains rotation to its axis, but a hard
            // CharacterController depenetration spike can violate it for a step and tip
            // the door — freezing the other two axes rigidly forbids that tilt.
            // hinge.axis = local Z here, so the perpendicular pair is X + Y. Confirmed
            // via debugPush: constraints=48, worldAxis stays (0,1,0), door swings freely.
            // (This is why the OLD "freezing welds the door" bug happened — that froze a
            // pair that INCLUDED the hinge axis. Never freeze the hinge's own axis; if a
            // door is ever authored with a different hinge.axis, change this pair to match.)
            doorBody.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY;

            // THE fix: solve the joint far more tightly than the default 6 iterations,
            // so a CharacterController's hard depenetration can't rip it off the anchor.
            doorBody.solverIterations = 32;
            doorBody.solverVelocityIterations = 16;

            // Clamp how violently PhysX separates the door from an overlapping collider.
            // Unmclamped (~10 m/s default), the player's CharacterController sinking into
            // the door ejects it at up to 10 m/s — the door "flies open" with zero push
            // (it's depenetration, not torque) and can be knocked off its hinge. Clamped
            // low, the capsule can't out-push the separation, so the door RESISTS (slows
            // you) and only the controlled hinge torque opens it — the "force it open" feel.
            doorBody.maxDepenetrationVelocity = maxDepenetrationVelocity;

            doorBody.angularDamping = angularDamping;   // Unity 6 name
        }

        private void ConfigureHinge()
        {
            JointLimits limits = hinge.limits;
            limits.min = minimumAngle;
            limits.max = maximumAngle;
            limits.bounciness = 0f;
            limits.contactDistance = 0f;

            hinge.limits = limits;
            hinge.useLimits = true;

            JointSpring spring = hinge.spring;
            spring.targetPosition = closedAngle;
            spring.spring = springStrength;
            spring.damper = springDamper;

            hinge.spring = spring;
            hinge.useSpring = true;   // self-closing
            hinge.useMotor = false;
            hinge.useAcceleration = useAcceleration;

            hinge.enableCollision = false;
            hinge.enablePreprocessing = false;
        }

        private void SetLimits(float min, float max)
        {
            JointLimits limits = hinge.limits;
            limits.min = min;
            limits.max = max;
            hinge.limits = limits;
        }

        /// <summary>
        /// Swings both ways, but behaves like a one-way door on each swing:
        /// once it opens past commitAngle to one side, the OPPOSITE limit snaps
        /// shut at 0, so the returning door cannot pass through closed — it hits
        /// a hard stop there and thunks, exactly like a real door meeting its
        /// frame. When it settles the full range is restored, so the next push
        /// can go either way.
        ///
        /// A static two-way limit (-100..100) can't do this: the door would sail
        /// straight through 0 and oscillate, with nothing to latch against.
        /// </summary>
        private void FixedUpdate()
        {
            float angle = CurrentAngle;   // NOT hinge.angle — that returns 0/NaN
            float speed = doorBody.angularVelocity.magnitude;

            // Creak: door starts moving from rest.
            if (!swinging && speed > swingStartSpeed)
            {
                swinging = true;
                OnSwingStart?.Invoke(speed);
            }
            else if (swinging && speed < settleSpeed)
            {
                swinging = false;
            }

            if (side == Side.Closed)
            {
                // Free to go either way — commit to whichever side it opens to,
                // and lock 0 as a hard stop behind it.
                if (angle > commitAngle)
                {
                    side = Side.Positive;
                    SetLimits(0f, maximumAngle);
                    thunked = slammed = false;
                    peakAngle = 0f;
                    if (logSwingTelemetry) Debug.Log($"[PhysicsDoor] COMMIT +  angle={angle:0.0}°  (door should look BARELY open here)");
                }
                else if (angle < -commitAngle)
                {
                    side = Side.Negative;
                    SetLimits(minimumAngle, 0f);
                    thunked = slammed = false;
                    peakAngle = 0f;
                    if (logSwingTelemetry) Debug.Log($"[PhysicsDoor] COMMIT -  angle={angle:0.0}°  (door should look BARELY open here)");
                }
            }
            else
            {
                // How far / how fast this swing actually got.
                peakAngle = Mathf.Max(peakAngle, Mathf.Abs(angle));
                peakSpeed = Mathf.Max(peakSpeed, speed);

                // Reaching the open stop is NOT the same as slamming into it: the
                // self-closing spring bleeds off speed on the way out, so the door
                // usually coasts into the limit. Only a door still carrying real
                // speed when it arrives is a slam.
                float openLimit = side == Side.Positive ? maximumAngle : minimumAngle;
                bool atOpenStop = Mathf.Abs(angle - openLimit) <= slamTolerance;

                if (atOpenStop && !reachedOpenLimit)
                {
                    reachedOpenLimit = true;
                    openArrivalSpeed = previousSpeed; // the limit zeroes it this step
                    bestOpenArrivalSpeed = Mathf.Max(bestOpenArrivalSpeed, openArrivalSpeed);
                    if (!slammed && openArrivalSpeed > slamMinSpeed)
                    {
                        slammed = true;
                        slamSoundedThisSwing = true;
                        OnSlamOpen?.Invoke(openArrivalSpeed);
                    }
                }
                else if (reachedOpenLimit && Mathf.Abs(angle - openLimit) > slamTolerance * 2f)
                {
                    reachedOpenLimit = false; // swung back off the stop — re-arm
                    slammed = false;
                }

                // Hit the closed stop — the thunk. previousSpeed, because the
                // limit has already zeroed the velocity by the time we see this.
                // Only sounds if the door actually swung open (peakAngle): a
                // door jittered a few degrees in a shoving match closes silently.
                if (!thunked && Mathf.Abs(angle) <= closedTolerance)
                {
                    thunked = true;
                    closeImpactSpeed = previousSpeed;
                    thunkSounded = peakAngle >= thunkArmAngle;
                    if (thunkSounded)
                        OnClosed?.Invoke(previousSpeed);
                    if (logSwingTelemetry)
                        Debug.Log($"[PhysicsDoor] THUNK  angle={angle:0.0}°  impact={previousSpeed:0.00} rad/s  " +
                                  $"(door should look CLOSED here — if it looks OPEN, angle-zero is the open pose)");
                }

                // Settled shut: restore the full range so the next push can go
                // either way again.
                if (thunked && speed < settleSpeed)
                {
                    if (logSwingTelemetry)
                    {
                        string slamWhy = slamSoundedThisSwing
                            ? $"PLAYED (arrived at {bestOpenArrivalSpeed:0.00} rad/s)"
                            : bestOpenArrivalSpeed > 0f
                                ? $"muted — reached the stop but only at {bestOpenArrivalSpeed:0.00} rad/s (needs > slamMinSpeed {slamMinSpeed})"
                                : $"never reached the open stop (got {peakAngle:0.0}° of {Mathf.Abs(side == Side.Positive ? maximumAngle : minimumAngle):0}°)";

                        Debug.Log($"[PhysicsDoor] swing done — peakAngle={peakAngle:0.0}°  " +
                                  $"peakSpeed={peakSpeed:0.00} rad/s (→ creakFullSpeed)  " +
                                  $"closeImpact={closeImpactSpeed:0.00} rad/s (→ impactFullSpeed) | " +
                                  $"thunk {(thunkSounded ? "PLAYED" : $"muted (peakAngle < thunkArmAngle {thunkArmAngle})")}, " +
                                  $"slam {slamWhy}");
                    }

                    side = Side.Closed;
                    thunked = slammed = thunkSounded = reachedOpenLimit = slamSoundedThisSwing = false;
                    peakAngle = peakSpeed = closeImpactSpeed = openArrivalSpeed = bestOpenArrivalSpeed = 0f;
                    SetLimits(minimumAngle, maximumAngle);
                }
            }

            previousSpeed = speed;
        }

        /// <summary>
        /// Push the door from a world-space contact. The contact is converted to
        /// PURE TORQUE about the hinge axis — never a linear force. AddForceAtPosition
        /// would inject linear velocity at the centre of mass, which the HingeJoint
        /// then has to cancel every frame; losing that fight is what rips the door
        /// off its hinge. Torque about the axis is motion the joint already allows,
        /// so there is nothing to fight.
        ///
        /// ForceMode.Impulse respects the inertia tensor, so a heavy door genuinely
        /// feels heavy, and pushing near the hinge barely moves it while pushing at
        /// the outer edge swings it — the real-feeling leverage you want.
        /// </summary>
        public void Push(Vector3 contactPoint, Vector3 pushDirection, float strength)
        {
            // Clamp SWING speed, not linear speed (see maxSwingSpeed tooltip).
            if (doorBody.angularVelocity.magnitude >= maxSwingSpeed)
            {
                if (debugPush) Debug.Log($"[PhysicsDoor] push ignored — already swinging at {doorBody.angularVelocity.magnitude:0.00} rad/s (max {maxSwingSpeed})");
                return;
            }

            Vector3 axis = transform.TransformDirection(hinge.axis).normalized;
            Vector3 anchor = transform.TransformPoint(hinge.anchor);

            // Lever arm and force, both flattened onto the hinge's rotation plane —
            // components parallel to the axis can't spin the door, only strain it.
            Vector3 lever = Vector3.ProjectOnPlane(contactPoint - anchor, axis);
            Vector3 force = Vector3.ProjectOnPlane(pushDirection.normalized, axis) * strength;

            float torque = Vector3.Dot(Vector3.Cross(lever, force), axis);
            doorBody.AddTorque(axis * torque, ForceMode.Impulse);

            if (debugPush)
                Debug.Log($"[PhysicsDoor] PUSH  worldAxis={axis}  lever={lever.magnitude:0.00}m  torque={torque:0.00}  " +
                          $"(hinge.axis local={hinge.axis}, constraints={doorBody.constraints}, kinematic={doorBody.isKinematic})");
        }
    }
}

