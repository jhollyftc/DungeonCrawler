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


        CharacterController cc;
        float pitch;
        float verticalVelocity;

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

            // Gravity & jump.
            if (cc.isGrounded)
            {
                verticalVelocity = -2f; // small downward stick so isGrounded stays reliable on ramps
                if (Input.GetKeyDown(KeyCode.Space))
                    verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
            }
            verticalVelocity += gravity * Time.deltaTime;

            cc.Move((horizontal + Vector3.up * verticalVelocity) * Time.deltaTime);
        }
    }
}
