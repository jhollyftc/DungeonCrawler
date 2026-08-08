using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Additive camera punch: a rotational (and slight positional) impulse that
    /// springs back to zero. Landed hits, blocks, big impacts call Kick(); the
    /// spring gives it snap-out/settle-back weight instead of a linear shake.
    ///
    /// Put it on the PLAYER CAMERA next to HeadBob. It composes the same way
    /// HeadBob does — position strips last frame's offset before re-applying
    /// (crouch owns the base position), rotation multiplies onto whatever the
    /// controller + bob wrote this frame — so all three camera layers stack
    /// without fighting, in any LateUpdate order.
    ///
    /// FIRST-PERSON DISCIPLINE: kicks here must be far smaller than third-person
    /// intuition suggests — the camera is the player's HEAD, and big rotations
    /// nauseate. Hard caps enforce that even if a caller goes wild.
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraKick : MonoBehaviour
    {
        [Header("Spring")]
        [Tooltip("How hard the kick springs back to zero. Higher = snappier punch.")]
        public float stiffness = 320f;
        [Tooltip("How fast the wobble dies. ~2*sqrt(stiffness) is critically damped — one clean punch, no ring.")]
        public float damping = 26f;

        [Header("Hard caps (nausea guards)")]
        [Tooltip("Max total rotational deflection (deg), whatever callers request.")]
        public float maxRotation = 6f;
        [Tooltip("Max positional deflection (m).")]
        public float maxPosition = 0.035f;

        Vector3 rot, rotVel;       // euler degrees
        Vector3 pos, posVel;
        Vector3 appliedPos;        // stripped before re-applying, same pattern as HeadBob

        /// <summary>Rotational punch (deg: x pitch, y yaw, z roll) + optional positional jolt (m, camera-local).</summary>
        public void Kick(Vector3 eulerImpulse, Vector3 positionImpulse = default)
        {
            rotVel += eulerImpulse * stiffness * 0.12f;   // impulse scaled so field values read as "about this many degrees of peak"
            posVel += positionImpulse * stiffness * 0.12f;
        }

        /// <summary>Undirected jolt — explosions, blocked hits. strength ≈ peak degrees.</summary>
        public void Shake(float strength)
        {
            Vector3 dir = Random.insideUnitSphere;
            Kick(new Vector3(dir.x, dir.y, dir.z * 0.5f) * strength);
        }

        void LateUpdate()
        {
            // Unscaled time: the kick must play THROUGH a hitstop dip — the punch
            // landing while the world hangs is most of the effect.
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            rotVel += -rot * (stiffness * dt);
            rotVel *= Mathf.Exp(-damping * dt);
            rot += rotVel * dt;
            if (rot.magnitude > maxRotation) rot = rot.normalized * maxRotation;

            posVel += -pos * (stiffness * dt);
            posVel *= Mathf.Exp(-damping * dt);
            pos += posVel * dt;
            if (pos.magnitude > maxPosition) pos = pos.normalized * maxPosition;

            transform.localPosition -= appliedPos;
            appliedPos = pos;
            transform.localPosition += appliedPos;

            if (rot.sqrMagnitude > 0.0001f)
                transform.localRotation *= Quaternion.Euler(rot);
        }

        void OnDisable()
        {
            transform.localPosition -= appliedPos;
            appliedPos = Vector3.zero;
            rot = rotVel = pos = posVel = Vector3.zero;
        }
    }
}
