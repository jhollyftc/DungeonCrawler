using UnityEngine;

namespace DungeonGen
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(HingeJoint))]
    public class PhysicsDoor : MonoBehaviour
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

        [Header("Push")]
        [Tooltip("Stop adding torque once the door is already swinging this fast (rad/s). Clamping ANGULAR speed is the point — a hinged door's LINEAR velocity is ~0 by design, so a linear clamp would never fire and pushes would compound until the joint tore.")]
        [SerializeField] private float maxSwingSpeed = 6f;
        [Tooltip("Log every push (world axis, lever, torque, constraints). Turn on when a door won't move — it pinpoints a bad hinge axis, a zero lever, or a blocking constraint immediately.")]
        [SerializeField] private bool debugPush = false;

        private Rigidbody doorBody;
        private HingeJoint hinge;

        private void Awake()
        {
            doorBody = GetComponent<Rigidbody>();
            hinge = GetComponent<HingeJoint>();

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

            // NO rotation constraints here. RigidbodyConstraints are WORLD-space,
            // but the hinge axis is LOCAL — and these FBX doors carry a Blender
            // axis-correction rotation, so "freeze world X/Z" can freeze the very
            // rotation the door needs and weld it shut. The HingeJoint already
            // constrains rotation to its own axis; that's its whole job.
            doorBody.constraints = RigidbodyConstraints.None;

            // THE fix: solve the joint far more tightly than the default 6 iterations,
            // so a CharacterController's hard depenetration can't rip it off the anchor.
            doorBody.solverIterations = 32;
            doorBody.solverVelocityIterations = 16;

            doorBody.angularDamping = angularDamping;   // Unity 6 name
        }

        private void ConfigureHinge()
        {
            JointLimits limits = hinge.limits;
            limits.min = minimumAngle;
            limits.max = maximumAngle;
            limits.bounciness = 0f;
            limits.contactDistance = 2f;

            hinge.limits = limits;
            hinge.useLimits = true;

            JointSpring spring = hinge.spring;
            spring.targetPosition = closedAngle;
            spring.spring = springStrength;
            spring.damper = springDamper;

            hinge.spring = spring;
            hinge.useSpring = false;
            hinge.useMotor = false;
            hinge.useAcceleration = useAcceleration;

            hinge.enableCollision = false;
            hinge.enablePreprocessing = false;
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

