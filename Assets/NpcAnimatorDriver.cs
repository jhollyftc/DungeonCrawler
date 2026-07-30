using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Bridges the NPC's capability layer to an Animator. The AI never knows the
    /// Animator exists — this driver reads NpcLocomotion (and, in later phases,
    /// combat/health events) and writes standard parameters. That one-way flow is
    /// what makes a better-rigged model a drop-in: swap the mesh + controller,
    /// keep this component, done.
    ///
    /// Parameters written (create the ones your controller uses; missing ones are
    /// skipped with a one-time notice, so a minimal walk-only controller is fine):
    ///   Speed       (float) — actual horizontal m/s, damped. Blend idle→walk on this.
    ///   MotionSpeed (float) — playback-rate multiplier so foot cycles match ground
    ///                 speed: actual speed / walkAnimationSpeed. Wire it to the walk
    ///                 state's Speed Multiplier to kill foot-sliding, especially when
    ///                 SpeedMultiplier (carry/injury) slows an NPC below its authored
    ///                 walk pace.
    ///   VelocityX/VelocityZ (float) — NpcLocomotion.LocalVelocity (right/forward, m/s),
    ///                 damped. Drive a 2D directional blend tree (forward/back/strafe
    ///                 clips) so a crowd shove that pushes an NPC sideways plays a strafe
    ///                 clip instead of sliding the feet through a forward-walk pose that
    ///                 doesn't match the actual direction of travel.
    ///
    /// NEVER use root motion — NpcLocomotion's CharacterController drives all
    /// movement. The Animator is a puppet, not a pilot.
    /// </summary>
    [RequireComponent(typeof(NpcLocomotion))]
    [DisallowMultipleComponent]
    public class NpcAnimatorDriver : MonoBehaviour
    {
        [Tooltip("The Animator on the model. Left empty, found in children (an animated FBX brings its own Animator on the model root).")]
        public Animator animator;

        [Tooltip("Ground speed (m/s) the walk clip was authored for — the speed at which the feet neither slide nor skate when the clip plays at 1x. Match your agent speed (3.5) as a starting point, then eyeball: feet sliding forward = raise this, treadmilling = lower it.")]
        public float walkAnimationSpeed = 3.5f;

        [Tooltip("Smoothing time (s) for the Speed parameter so the blend doesn't snap when the agent starts/stops.")]
        public float speedDampTime = 0.12f;

        [Tooltip("Ground speed (m/s) at which the directional blend reaches a FULL walk clip. The blend tree's poles are unit vectors, so feeding it raw m/s means a slow NPC sits PARTWAY between idle and walk — at 0.4 m/s that's ~60% idle, so the feet barely cycle while the body slides. Normalizing to this speed instead makes any real movement play a full-strength walk, and MotionSpeed (wired to the blend state's Speed Multiplier) slows the CYCLE to match ground speed — which is what actually keeps feet planted. Keep it low; it only sets how quickly the idle→walk blend saturates.")]
        public float fullBlendSpeed = 0.75f;

        [Tooltip("Movement below this speed (m/s) is reported as a dead stop. A settled crowd never sits at exactly zero — boids separation and agent repathing nudge an NPC a few cm/s indefinitely — and in a 2D blend tree (idle at 0,0, walk clips at the poles) that hovering velocity flickers the pose between idle and a walk direction every frame. It reads as the NPC jittering in place even though its POSITION is barely moving; the cause is the blend, not the movement. This deadzone snaps that micro-motion to a clean idle. Raise it if a standing crowd still shimmers, lower it if NPCs look frozen while genuinely creeping.")]
        public float movementDeadzone = 0.15f;

        static readonly int SpeedParam = Animator.StringToHash("Speed");
        static readonly int MotionSpeedParam = Animator.StringToHash("MotionSpeed");
        static readonly int VelocityXParam = Animator.StringToHash("VelocityX");
        static readonly int VelocityZParam = Animator.StringToHash("VelocityZ");
        static readonly int DieParam = Animator.StringToHash("Die");
        static readonly int AttackParam = Animator.StringToHash("Attack");
        static readonly int CancelAttackParam = Animator.StringToHash("CancelAttack");

        NpcLocomotion body;
        MeleeAttack melee;      // optional — an unarmed NPC just walks
        bool hasSpeed, hasMotionSpeed, hasVelocityX, hasVelocityZ, hasDie;
        bool hasAttack, hasCancelAttack;

        void Awake()
        {
            body = GetComponent<NpcLocomotion>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);

            if (animator == null)
            {
                Debug.LogWarning($"[NPC] {name}: NpcAnimatorDriver found no Animator in children — " +
                                 "add one to the model (an animated FBX usually brings its own) and assign a controller.", this);
                enabled = false;
                return;
            }
            if (animator.runtimeAnimatorController == null)
            {
                Debug.LogWarning($"[NPC] {name}: the Animator has NO controller assigned — " +
                                 "create one (Assets > Create > Animator Controller), add the walk state, and assign it on the Animator.", this);
                enabled = false;
                return;
            }
            if (animator.applyRootMotion)
            {
                // Root motion would fight the CharacterController for the transform.
                animator.applyRootMotion = false;
                Debug.LogWarning($"[NPC] {name}: Apply Root Motion was ON — disabled. NpcLocomotion drives movement; the Animator only poses.", this);
            }

            // Only write parameters the controller actually declares, so a minimal
            // walk-only controller doesn't spam warnings every frame.
            foreach (var p in animator.parameters)
            {
                if (p.nameHash == SpeedParam) hasSpeed = true;
                if (p.nameHash == MotionSpeedParam) hasMotionSpeed = true;
                if (p.nameHash == VelocityXParam) hasVelocityX = true;
                if (p.nameHash == VelocityZParam) hasVelocityZ = true;
                if (p.nameHash == DieParam) hasDie = true;
                if (p.nameHash == AttackParam) hasAttack = true;
                if (p.nameHash == CancelAttackParam) hasCancelAttack = true;
            }
            if (!hasSpeed)
                Debug.Log($"[NPC] {name}: controller has no 'Speed' float parameter — add one to blend idle/walk by movement.", this);

            // The attack animation hookup. MeleeAttack itself has NO Animator reference —
            // it decides WHEN a swing happens and knows nothing about how it looks, exactly
            // as the AI knows nothing about the Animator. This driver stays the single seam.
            melee = GetComponent<MeleeAttack>();
            if (melee != null && !hasAttack)
                Debug.Log($"[NPC] {name}: has a MeleeAttack but the controller declares no 'Attack' trigger — " +
                          "swings will still hit, they just won't be animated.", this);
            if (melee != null && melee.sweepFromAnimationEvent && !hasCancelAttack)
                Debug.Log($"[NPC] {name}: sweepFromAnimationEvent is ON but the controller declares no " +
                          "'CancelAttack' trigger — an interrupted or PARRIED swing will keep playing its clip " +
                          "to the end, since cancelling in code can't pull the Animator out of the state.", this);
        }

        void OnEnable()
        {
            if (melee == null) return;
            melee.OnSwingStart += HandleSwingStart;
            melee.OnSwingCancelled += HandleSwingCancelled;
        }

        void OnDisable()
        {
            if (melee == null) return;
            melee.OnSwingStart -= HandleSwingStart;
            melee.OnSwingCancelled -= HandleSwingCancelled;
        }

        void HandleSwingStart()
        {
            if (animator == null || !hasAttack) return;
            // Clear any stale cancel before starting, or a CancelAttack that no transition
            // consumed sits armed and yanks the Animator straight back out of this swing.
            if (hasCancelAttack) animator.ResetTrigger(CancelAttackParam);
            animator.SetTrigger(AttackParam);
        }

        void HandleSwingCancelled()
        {
            if (animator == null) return;
            // Consume an Attack that hasn't been taken yet, so a cancelled swing can't be
            // resurrected by a trigger still sitting in the queue.
            if (hasAttack) animator.ResetTrigger(AttackParam);
            if (hasCancelAttack) animator.SetTrigger(CancelAttackParam);
        }

        /// <summary>
        /// Play the death animation. Returns false if the controller has no 'Die'
        /// trigger (or no Animator) — the caller falls back to the code topple, so
        /// a controller authored before the death clip existed still degrades
        /// gracefully. Freezes locomotion params first so the death state isn't
        /// fighting a lingering walk blend, then stops driving entirely.
        /// </summary>
        public bool TriggerDeath()
        {
            if (animator == null || !hasDie) return false;
            if (hasSpeed) animator.SetFloat(SpeedParam, 0f);
            if (hasMotionSpeed) animator.SetFloat(MotionSpeedParam, 1f);
            if (hasVelocityX) animator.SetFloat(VelocityXParam, 0f);
            if (hasVelocityZ) animator.SetFloat(VelocityZParam, 0f);
            animator.SetTrigger(DieParam);
            enabled = false;   // corpse: nothing left to drive
            return true;
        }

        void Update()
        {
            float speed = body.CurrentSpeed;

            // Below the deadzone the NPC is standing, as far as the Animator is
            // concerned — see movementDeadzone. Applied to the RAW speed before any
            // damping so the damping eases cleanly to a true zero instead of
            // asymptotically approaching a small nonzero hover.
            bool moving = speed >= movementDeadzone;
            if (!moving) speed = 0f;

            if (hasSpeed)
                animator.SetFloat(SpeedParam, speed, speedDampTime, Time.deltaTime);

            if (hasMotionSpeed)
            {
                // Feet match the floor: play the walk cycle faster/slower in
                // proportion to how fast the body is actually moving.
                float rate = speed > 0.05f ? speed / Mathf.Max(0.1f, walkAnimationSpeed) : 1f;
                animator.SetFloat(MotionSpeedParam, rate);
            }

            if (hasVelocityX || hasVelocityZ)
            {
                // Zeroed as a PAIR when under the deadzone: killing each axis on its own
                // threshold would let a diagonal creep collapse onto one axis first and
                // swing the 2D blend toward a pure strafe on the way to idle.
                Vector2 local = moving ? body.LocalVelocity : Vector2.zero;

                // Rescale so the blend reaches a full walk clip at fullBlendSpeed rather
                // than at 1 m/s (the poles' literal coordinates). DIRECTION is preserved
                // exactly — only the magnitude is remapped — so a diagonal still blends
                // between the right two clips. See fullBlendSpeed.
                float mag = local.magnitude;
                if (mag > 0.0001f)
                    local *= Mathf.Clamp01(mag / Mathf.Max(0.01f, fullBlendSpeed)) / mag;
                if (hasVelocityX) animator.SetFloat(VelocityXParam, local.x, speedDampTime, Time.deltaTime);
                if (hasVelocityZ) animator.SetFloat(VelocityZParam, local.y, speedDampTime, Time.deltaTime);
            }
        }
    }
}
