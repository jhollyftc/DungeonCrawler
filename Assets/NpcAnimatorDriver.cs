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

        static readonly int SpeedParam = Animator.StringToHash("Speed");
        static readonly int MotionSpeedParam = Animator.StringToHash("MotionSpeed");
        static readonly int DieParam = Animator.StringToHash("Die");

        NpcLocomotion body;
        bool hasSpeed, hasMotionSpeed, hasDie;

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
                if (p.nameHash == DieParam) hasDie = true;
            }
            if (!hasSpeed)
                Debug.Log($"[NPC] {name}: controller has no 'Speed' float parameter — add one to blend idle/walk by movement.", this);
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
            animator.SetTrigger(DieParam);
            enabled = false;   // corpse: nothing left to drive
            return true;
        }

        void Update()
        {
            float speed = body.CurrentSpeed;

            if (hasSpeed)
                animator.SetFloat(SpeedParam, speed, speedDampTime, Time.deltaTime);

            if (hasMotionSpeed)
            {
                // Feet match the floor: play the walk cycle faster/slower in
                // proportion to how fast the body is actually moving.
                float rate = speed > 0.05f ? speed / Mathf.Max(0.1f, walkAnimationSpeed) : 1f;
                animator.SetFloat(MotionSpeedParam, rate);
            }
        }
    }
}
