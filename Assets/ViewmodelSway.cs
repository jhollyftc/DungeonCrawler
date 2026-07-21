using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Procedural viewmodel motion for a held item: distance-driven walk bob,
    /// look sway (weapon lags the camera), movement sway (inertia on strafe /
    /// accelerate), idle breathing, and footstep/landing impulses — all fed
    /// through a spring so motion overshoots and settles with weight.
    ///
    /// One component per hand. Same code, different tuning = different item
    /// personality (shield: lower weight, higher damping).
    ///
    /// Runs in LateUpdate (after the controller rotates the camera) and
    /// composes offsets onto the rest pose captured at Awake — never
    /// accumulates, never drifts. `proceduralWeight` is the hook for future
    /// attack animations to suppress sway during a swing.
    /// </summary>
    public class ViewmodelSway : MonoBehaviour
    {
        [Header("Master")]
        [Tooltip("Overall motion multiplier. Sword ~1, shield ~0.45.")]
        public float weight = 1f;
        [Tooltip("Future combat hook: attack animations lerp this toward 0 during swings.")]
        [Range(0f, 1f)] public float proceduralWeight = 1f;

        [Header("Look sway (weapon lags camera)")]
        public float swayPerDegree = 0.0012f;       // meters of positional lag per degree of mouse turn
        public float rotSwayPerDegree = 0.6f;       // degrees of rotational lag per degree of mouse turn
        public float maxSway = 0.06f;               // clamp — springs overshoot, targets must not
        public float maxRotSway = 8f;

        [Header("Movement sway (inertia)")]
        public float moveSwayPerMps = 0.008f;       // meters of lean per m/s of local velocity
        public float maxMoveSway = 0.05f;
        public float strafeRollPerMps = 1.2f;       // degrees of roll per m/s of strafe

        [Header("Walk bob (distance-driven)")]
        [Tooltip("Bob cycles per meter walked. ~0.7 lines up with a 2.4m stride (two half-bobs per stride).")]
        public float bobCyclesPerMeter = 0.7f;
        public float bobVertical = 0.02f;
        public float bobHorizontal = 0.012f;

        [Header("Idle breathing")]
        public float idleAmplitude = 0.0015f;
        public float idleFrequency = 0.9f;

        [Header("Spring")]
        public float stiffness = 220f;
        public float damping = 14f;

        [Header("Impulses")]
        public float stepImpulse = 0.06f;           // downward velocity kick per footstep
        public float landImpulsePerMps = 0.05f;     // scaled by landing speed

        Vector3 restPos;
        Quaternion restRot;
        Vector3 posOffset, posVelocity;
        Vector3 rotOffset, rotVelocity;             // euler degrees
        float bobPhase;

        // Attack pose, injected per-frame by PlayerMelee/PlayerBlock. Composed
        // after sway and BEFORE the collision clamp, so a swing still can't push
        // the blade through a wall. Zero when no attack is active — idle behavior
        // is byte-identical to pre-melee.
        Vector3 attackPos;
        Quaternion attackRot = Quaternion.identity;
        float attackSuppress;                       // 0..1 — how much sway the attack mutes

        /// <summary>
        /// Drive the hand with an attack pose this frame (camera-local offset +
        /// rotation) and suppress procedural sway by `swaySuppress`. THE one
        /// sanctioned way for combat to move the viewmodel: this component stays
        /// the single writer of the transform (rest → sway → attack → collision
        /// clamp), because two systems pushing the pose independently oscillate —
        /// the lesson ViewmodelCollision already taught. Call every frame during
        /// a swing; decays to nothing when you stop calling it with zeros.
        /// </summary>
        public void SetAttackPose(Vector3 positionOffset, Quaternion rotationOffset, float swaySuppress)
        {
            attackPos = positionOffset;
            attackRot = rotationOffset;
            attackSuppress = Mathf.Clamp01(swaySuppress);
        }

        CharacterController cc;
        PlayerFootsteps footsteps;
        ViewmodelCollision collision;

        void Awake()
        {
            restPos = transform.localPosition;
            restRot = transform.localRotation;
            cc = GetComponentInParent<CharacterController>();
            footsteps = GetComponentInParent<PlayerFootsteps>();
            collision = GetComponent<ViewmodelCollision>();
            if (footsteps != null)
            {
                footsteps.OnStep += HandleStep;
                footsteps.OnLand += HandleLand;
            }
        }

        void OnDestroy()
        {
            if (footsteps != null)
            {
                footsteps.OnStep -= HandleStep;
                footsteps.OnLand -= HandleLand;
            }
        }

        void HandleStep() => posVelocity += Vector3.down * stepImpulse;
        void HandleLand(float impactSpeed) => posVelocity += Vector3.down * (impactSpeed * landImpulsePerMps);

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // ---- Inputs ----
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");
            Vector3 localVel = Vector3.zero;
            bool grounded = false;
            if (cc != null)
            {
                localVel = cc.transform.InverseTransformDirection(cc.velocity);
                grounded = cc.isGrounded;
            }

            // ---- Position target ----
            Vector3 posTarget = Vector3.zero;

            // Look sway: camera turns right, weapon lags left (and down when looking up).
            posTarget.x = Mathf.Clamp(-mouseX * swayPerDegree, -maxSway, maxSway);
            posTarget.y = Mathf.Clamp(-mouseY * swayPerDegree, -maxSway, maxSway);

            // Movement sway: strafe left -> weapon leans right; forward -> weapon drags back.
            posTarget.x += Mathf.Clamp(-localVel.x * moveSwayPerMps, -maxMoveSway, maxMoveSway);
            posTarget.z  = Mathf.Clamp(-localVel.z * moveSwayPerMps, -maxMoveSway, maxMoveSway);

            // Walk bob: phase advances with ground distance, so cadence tracks
            // walk/sprint automatically and stops dead against walls.
            float groundSpeed = new Vector2(localVel.x, localVel.z).magnitude;
            if (grounded && groundSpeed > 0.3f)
            {
                bobPhase += groundSpeed * bobCyclesPerMeter * dt * Mathf.PI * 2f;
                posTarget.x += Mathf.Sin(bobPhase) * bobHorizontal;
                posTarget.y += -Mathf.Abs(Mathf.Cos(bobPhase)) * bobVertical; // downward-biased, like real steps
            }

            // Idle breathing: two incommensurate sines so it never visibly loops.
            posTarget.y += Mathf.Sin(Time.time * idleFrequency) * idleAmplitude;
            posTarget.x += Mathf.Sin(Time.time * idleFrequency * 0.53f) * idleAmplitude * 0.6f;

            // ---- Rotation target (degrees) ----
            Vector3 rotTarget = new Vector3(
                Mathf.Clamp(mouseY * rotSwayPerDegree, -maxRotSway, maxRotSway),   // pitch lag
                Mathf.Clamp(-mouseX * rotSwayPerDegree, -maxRotSway, maxRotSway),  // yaw lag
                Mathf.Clamp(localVel.x * strafeRollPerMps, -maxRotSway, maxRotSway)); // strafe roll

            // ---- Semi-implicit springs ----
            posVelocity += (posTarget - posOffset) * (stiffness * dt);
            posVelocity *= Mathf.Exp(-damping * dt);
            posOffset += posVelocity * dt;

            rotVelocity += (rotTarget - rotOffset) * (stiffness * dt);
            rotVelocity *= Mathf.Exp(-damping * dt);
            rotOffset += rotVelocity * dt;

            // ---- Apply onto the authored rest pose ----
            // Rotation offset is PRE-multiplied: the sway happens in the
            // camera's frame, then the authored hand rotation is applied.
            // Post-multiplying would pivot the sway around the hand's rotated
            // axes — pitch lag turning into roll on an angled weapon pose.
            float w = weight * proceduralWeight * (1f - attackSuppress);
            Vector3 swayedPos = restPos + posOffset * w + attackPos;
            // Attack rotation pre-multiplies like sway does (camera-frame first,
            // authored hand rotation last) — post-multiplying would pivot the
            // swing around the hand's angled axes.
            Quaternion swayedRot = Quaternion.Euler(rotOffset * w) * attackRot * restRot;

            // Collision retraction runs as a CLAMP on this result, never an
            // independent force — see ViewmodelCollision. Optional: sway
            // behaves exactly as before if no collision component is present.
            transform.localPosition = (collision != null && collision.enabled)
                ? collision.Clamp(swayedPos, swayedRot)
                : swayedPos;
            transform.localRotation = swayedRot;
        }
    }
}