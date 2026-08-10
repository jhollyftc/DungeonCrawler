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

        [Header("Sustained lean")]
        [Tooltip("Max HELD rotation (deg), separate from and larger than the impulse cap. A slow, deliberate lean is far easier on the stomach than a fast punch of the same size — it is angular VELOCITY that nauseates, not angle — so a pose can go further than a jolt.")]
        public float maxSustainedRotation = 14f;
        [Tooltip("Max held positional offset (m).")]
        public float maxSustainedPosition = 0.16f;
        [Tooltip("How fast the held pose eases toward what the driver asked for, and back to rest when nothing is driving it. Lower = a slower, weightier lean.")]
        public float sustainSpeed = 9f;

        Vector3 rot, rotVel;       // euler degrees
        Vector3 pos, posVel;
        Vector3 appliedPos;        // stripped before re-applying, same pattern as HeadBob

        Vector3 sustainRot, sustainPos;              // current, eased
        Vector3 wantSustainRot, wantSustainPos;      // what the driver asked for this frame
        int sustainFrame = -1;

        /// <summary>
        /// A HELD camera pose — a lean, a brace, a heave — as opposed to <see cref="Kick"/>,
        /// which is a punch that springs straight back to zero.
        ///
        /// THE SPRING CANNOT DO THIS. An impulse system starts returning the instant it fires,
        /// so "lean back and hold it while the throw loads" comes out as a twitch no matter how
        /// large the impulse: raising the numbers makes the twitch bigger, not the lean longer.
        ///
        /// CALL IT EVERY FRAME YOU WANT THE POSE. It is FRAME-STAMPED, not latched — stop
        /// calling and it eases back to rest on its own. That is deliberate: `SetAttackPose` on
        /// ViewmodelSway IS a latch, and the pose it stores sticks forever unless the same
        /// system actively drives it home, which is exactly how a blocked sword got stuck
        /// mid-pose. A driver that dies mid-lean (the prop destroyed, the player killed) must
        /// not leave the camera tilted.
        /// </summary>
        public void SetSustained(Vector3 euler, Vector3 offset = default)
        {
            wantSustainRot = euler;
            wantSustainPos = offset;
            sustainFrame = Time.frameCount;
        }

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

            // The held pose. Nothing asked for one this frame => ease home, so a driver that
            // stops (or dies) cannot strand the camera leaning.
            bool driven = sustainFrame == Time.frameCount;
            Vector3 targetRot = driven ? wantSustainRot : Vector3.zero;
            Vector3 targetPos = driven ? wantSustainPos : Vector3.zero;
            float k = 1f - Mathf.Exp(-sustainSpeed * dt);
            sustainRot = Vector3.Lerp(sustainRot, targetRot, k);
            sustainPos = Vector3.Lerp(sustainPos, targetPos, k);
            if (sustainRot.magnitude > maxSustainedRotation) sustainRot = sustainRot.normalized * maxSustainedRotation;
            if (sustainPos.magnitude > maxSustainedPosition) sustainPos = sustainPos.normalized * maxSustainedPosition;

            // ONE applied offset covering both terms, so there is still exactly one
            // strip-and-reapply against the transform HeadBob and the controller also write.
            transform.localPosition -= appliedPos;
            appliedPos = pos + sustainPos;
            transform.localPosition += appliedPos;

            Vector3 totalRot = rot + sustainRot;
            if (totalRot.sqrMagnitude > 0.0001f)
                transform.localRotation *= Quaternion.Euler(totalRot);
        }

        void OnDisable()
        {
            transform.localPosition -= appliedPos;
            appliedPos = Vector3.zero;
            rot = rotVel = pos = posVel = Vector3.zero;
            sustainRot = sustainPos = wantSustainRot = wantSustainPos = Vector3.zero;
        }
    }
}
