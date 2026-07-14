using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Minimal first-person controller. WASD to move, mouse to look,
    /// Space to jump, Left Shift to sprint. Escape releases the cursor,
    /// left-click recaptures it. Legacy Input Manager (same note as FlyCamera:
    /// set Active Input Handling to "Both" if you're on the new Input System).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        public Transform cam;
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 8f;
        public float jumpHeight = 1.1f;
        public float gravity = -20f;
        public float lookSensitivity = 2.2f;
        public float maxSlopeAngle = 45f;
        public float maxStepHeight = 0.5f;

        [Header("Crouch / sneak")]
        [Tooltip("Hold to crouch. Moving slowly means you shove physics objects gently — a crouched player eases a door open instead of banging it, so it stays under the door's noise threshold. Stealth falls out of the physics rather than being special-cased.")]
        public KeyCode crouchKey = KeyCode.LeftControl;
        [Tooltip("Move speed while crouched. Keep it well under walkSpeed — this is what makes doors silent.")]
        public float crouchSpeed = 1.6f;
        [Tooltip("Capsule height while crouched (standing height is taken from the CharacterController at Awake).")]
        public float crouchHeight = 1.1f;
        [Tooltip("How fast the capsule/camera ease between stand and crouch.")]
        public float crouchTransitionSpeed = 8f;
        [Tooltip("What counts as a ceiling when checking whether you can stand back up. Exclude the Player layer.")]
        public LayerMask ceilingMask = ~0;

        [Header("Ladder climbing")]
        [Tooltip("Vertical speed while inside a LadderClimbZone (W up, S down).")]
        public float climbSpeed = 3f;
        [Tooltip("Horizontal speed multiplier while climbing — enough to adjust sideways or step off, not enough to sprint mid-air.")]
        [Range(0f, 1f)] public float climbHorizontalDamp = 0.35f;

        /// <summary>True while crouched. Read by anything that cares how quiet the player is (future NPC alerting).</summary>
        public bool IsCrouching { get; private set; }
        /// <summary>Current horizontal speed (m/s). Physics pushes scale off this, so how hard you shove things follows how fast you're actually moving.</summary>
        public float HorizontalSpeed => new Vector3(cc.velocity.x, 0f, cc.velocity.z).magnitude;

        CharacterController cc;
        float pitch;
        float verticalVelocity;
        float standHeight;
        Vector3 standCenter;
        float standCamY;
        static readonly Collider[] ladderHits = new Collider[8];

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.slopeLimit = maxSlopeAngle;
            cc.stepOffset = maxStepHeight;

            standHeight = cc.height;
            standCenter = cc.center;
            if (cam != null) standCamY = cam.localPosition.y;
        }

        void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            
        }

        void Update()
        {
            // Cursor capture.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            // Look.
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                transform.Rotate(0f, Input.GetAxis("Mouse X") * lookSensitivity, 0f);
                pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
                if (cam != null)
                    cam.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }

            UpdateCrouch();

            // Move.
            Vector3 input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            input = Vector3.ClampMagnitude(input, 1f);
            float speed = IsCrouching ? crouchSpeed
                        : Input.GetKey(KeyCode.LeftShift) ? sprintSpeed
                        : walkSpeed;
            Vector3 horizontal = transform.TransformDirection(input) * speed;

            if (OnLadder())
            {
                // Climb: gravity off, W/S map to up/down, horizontal damped
                // (enough to adjust or step off at the top). Exiting the zone
                // — cresting the opening or stepping away at the bottom —
                // returns to normal movement automatically.
                verticalVelocity = input.z * climbSpeed;
                horizontal *= climbHorizontalDamp;
            }
            else
            {
                // Gravity & jump.
                if (cc.isGrounded)
                {
                    verticalVelocity = -2f; // small downward stick so isGrounded stays reliable on ramps
                    if (Input.GetKeyDown(KeyCode.Space))
                        verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
                }
                verticalVelocity += gravity * Time.deltaTime;
            }

            cc.Move((horizontal + Vector3.up * verticalVelocity) * Time.deltaTime);
        }

        /// <summary>
        /// Hold-to-crouch. Shrinks the capsule from the TOP (feet stay put) and
        /// drops the camera with it. Standing back up is blocked while something
        /// is overhead, so you can't clip through a low ceiling by releasing.
        /// </summary>
        void UpdateCrouch()
        {
            bool wantCrouch = Input.GetKey(crouchKey);

            // Can't stand up under a ceiling — stay crouched until it's clear.
            if (!wantCrouch && IsCrouching && CeilingBlocked())
                wantCrouch = true;

            IsCrouching = wantCrouch;

            float targetHeight = IsCrouching ? crouchHeight : standHeight;
            if (!Mathf.Approximately(cc.height, targetHeight))
            {
                float h = Mathf.MoveTowards(cc.height, targetHeight,
                                            crouchTransitionSpeed * Time.deltaTime * standHeight);
                float shrink = standHeight - h;

                cc.height = h;
                // Lower the centre by half the shrink so the capsule's FEET stay
                // planted and only the head comes down.
                cc.center = new Vector3(standCenter.x, standCenter.y - shrink * 0.5f, standCenter.z);

                if (cam != null)
                {
                    Vector3 p = cam.localPosition;
                    p.y = standCamY - shrink;
                    cam.localPosition = p;
                }
            }
        }

        /// <summary>Is there something directly overhead blocking a stand-up?</summary>
        bool CeilingBlocked()
        {
            float needed = standHeight - cc.height;
            if (needed <= 0.01f) return false;

            // Cast up from the top of the crouched capsule.
            float radius = Mathf.Max(0.05f, cc.radius - 0.05f);
            Vector3 top = transform.position + cc.center + Vector3.up * (cc.height * 0.5f - cc.radius);
            return Physics.SphereCast(top, radius, Vector3.up, out _, needed + 0.1f,
                                      ceilingMask, QueryTriggerInteraction.Ignore);
        }

        // Polled each frame rather than relying on OnTriggerEnter/Exit —
        // trigger callbacks can miss exits on teleports/regeneration, and a
        // small overlap probe against the capsule's center is trivially cheap.
        bool OnLadder()
        {
            Vector3 probe = transform.position + cc.center;
            int n = Physics.OverlapSphereNonAlloc(probe, cc.radius + 0.25f, ladderHits,
                                                  ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                var hit = ladderHits[i];
                if (hit != null && hit.isTrigger && hit.GetComponentInParent<LadderClimbZone>() != null)
                    return true;
            }
            return false;
        }
    }
}
