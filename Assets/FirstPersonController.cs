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

        [Header("Ladder climbing")]
        [Tooltip("Vertical speed while inside a LadderClimbZone (W up, S down).")]
        public float climbSpeed = 3f;
        [Tooltip("Horizontal speed multiplier while climbing — enough to adjust sideways or step off, not enough to sprint mid-air.")]
        [Range(0f, 1f)] public float climbHorizontalDamp = 0.35f;

        CharacterController cc;
        float pitch;
        float verticalVelocity;
        static readonly Collider[] ladderHits = new Collider[8];

        void Awake()
        {
            cc = GetComponent<CharacterController>();   
            cc.slopeLimit = maxSlopeAngle;
            cc.stepOffset = maxStepHeight;         
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

            // Move.
            Vector3 input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            input = Vector3.ClampMagnitude(input, 1f);
            float speed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;
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
