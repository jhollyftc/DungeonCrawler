using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Add to the Main Camera. Hold RIGHT MOUSE to look; WASD to move,
    /// Q/E down/up, Left Shift for speed boost.
    /// Uses the legacy Input Manager — if your project is set to the new
    /// Input System only, switch Project Settings > Player > Active Input
    /// Handling to "Both".
    /// </summary>
    public class FlyCamera : MonoBehaviour
    {
        public float speed = 10f;
        public float fastMultiplier = 4f;
        public float lookSensitivity = 2.5f;

        float yaw, pitch;

        void Start()
        {
            Vector3 e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x;
        }

        void Update()
        {
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * lookSensitivity;
                pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            Vector3 move = transform.TransformDirection(
                new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical")));
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

            float s = speed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);
            transform.position += move * (s * Time.deltaTime);
        }
    }
}
