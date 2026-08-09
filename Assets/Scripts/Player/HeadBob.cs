using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Subtle walking head bob: a vertical dip + lateral sway + a whisper of roll.
    /// Put it ON the player camera; it finds the controller, carry, and footsteps
    /// in its parents.
    ///
    /// LOCKED TO FOOTSTEPS. The bob doesn't run its own clock — it reads
    /// PlayerFootsteps' live stride (StrideProgress + StepCount), the same
    /// accumulator that fires the step SOUND. So the head dips exactly when the
    /// foot lands and the clip plays, and the two can never drift apart across
    /// stops, jumps, or stair descents (all of which reset that accumulator). The
    /// vertical dips once per footfall; the sway/roll run at half that (one full
    /// left-right cycle per two footfalls, i.e. alternating feet), which is what
    /// reads as a walk. Cadence therefore comes entirely from the footstep system
    /// — there is no separate frequency to keep in sync.
    ///
    /// SPEED scales AMPLITUDE, never cadence — and vertical and lateral get their
    /// own response curves, because the vertical dip is the motion-sickness axis
    /// while sway and roll read as exertion at much lower comfort cost. So a sprint
    /// gets "more bobbing around" from the lateral curve rather than from shaking
    /// the player's head harder.
    ///
    /// CARRYING A HEAVY LOAD deepens the GAIT: bigger vertical lurch and roll
    /// (PlayerCarry.CarryLoad01, the same mass signal behind the move-speed
    /// penalty). It does NOT stretch the cadence directly — a heavy load already
    /// slows your walk, which slows both the steps and the bob together, staying
    /// locked. (To lengthen the STRIDE itself under load, scale PlayerFootsteps'
    /// stepDistance by load — one shared knob keeps sound and bob aligned.)
    ///
    /// The camera is also the parent of the viewmodel + overlay camera, so both
    /// bob with the head for free.
    /// </summary>
    [DisallowMultipleComponent]
    public class HeadBob : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Transform to bob. Defaults to this transform (put the component on the camera).")]
        public Transform cameraTransform;

        [Header("Bob amounts (metres / degrees)")]
        [Tooltip("Peak vertical dip per step. Subtle: a few cm. Lowest at the moment the foot lands.")]
        public float verticalAmplitude = 0.045f;
        [Tooltip("Peak side-to-side sway (crosses zero at each foot-plant, peaks between).")]
        public float horizontalAmplitude = 0.035f;
        [Tooltip("Peak camera roll (degrees). A little goes a long way — keep under ~1°. Negate to lean the other way.")]
        public float rollAmplitude = 0.4f;

        [Header("Response")]
        [Tooltip("Horizontal speed (m/s) that counts as FULL SPEED for the response curves below. Set it to the controller's sprintSpeed: below that the curves ease down, standing still is dead calm. Leaving it under sprintSpeed means the last stretch of acceleration adds nothing.")]
        public float speedForFullBob = 4f;

        [Tooltip("VERTICAL dip vs speed (x = speed/speedForFullBob, y = amplitude multiplier).\n\nKEEP THIS ONE RESTRAINED. Vertical head motion is the motion-sickness driver, and sprinting is exactly when the player is moving fastest and turning most. Plenty of shooters deliberately REDUCE vertical bob at sprint and sell the speed with FOV instead — the bash and bow zoom are the precedent here. A value above 1 at the right-hand end is a deliberate choice, not a free one.")]
        public AnimationCurve verticalSpeedResponse = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Tooltip("LATERAL sway and ROLL vs speed (same axes).\n\nThis is where 'more bobbing around' should come from. Sway and roll read as the BODY WORKING and cost far less comfort than vertical motion, so this curve can safely end above 1 where the vertical one should not. ~1.3 at full speed is a good starting point.")]
        public AnimationCurve lateralSpeedResponse = AnimationCurve.Linear(0f, 0f, 1f, 1.3f);
        [Tooltip("How fast the applied offset chases its target (per second). Also softens the snap when the stride accumulator resets on stop/land. Higher = tighter to the footstep, lower = floatier.")]
        public float offsetSmoothing = 14f;

        [Header("Carry (heavy = deeper lurch)")]
        [Tooltip("Vertical amplitude multiplier at full load — the weight dropping your head further each step.")]
        public float heavyVerticalMultiplier = 1.9f;
        [Tooltip("Roll amplitude multiplier at full load — a heavier lean as you trudge.")]
        public float heavyRollMultiplier = 1.5f;

        [Header("Fallback (only if no PlayerFootsteps is found)")]
        [Tooltip("Stride length used to synthesize a bob when there's no footstep component to lock to. Match your footstep stepDistance.")]
        public float fallbackStepDistance = 2.4f;

        const float Tau = Mathf.PI * 2f;

        FirstPersonController controller;
        PlayerCarry carry;
        PlayerFootsteps footsteps;

        float fallbackDistance;    // only used when footsteps is null
        Vector3 smoothOffset;      // eased positional bob
        float smoothRoll;          // eased roll (deg)
        Vector3 appliedOffset;     // last frame's applied position, removed before reapplying (composes with crouch)

        void Awake()
        {
            if (cameraTransform == null) cameraTransform = transform;
            controller = GetComponentInParent<FirstPersonController>();
            carry = GetComponentInParent<PlayerCarry>();
            footsteps = GetComponentInParent<PlayerFootsteps>();
        }

        // LateUpdate: after the controller's Update has set pitch (localRotation)
        // and any crouch height (localPosition.y), so the bob layers on top.
        /// <summary>
        /// Evaluate a response curve, degrading to the old LINEAR behaviour if it is missing
        /// or empty. A curve field cleared to zero keys would otherwise silently return 0 and
        /// kill the bob entirely, which reads as "head bob is broken" rather than as an
        /// authoring slip.
        /// </summary>
        static float Response(AnimationCurve curve, float speed01)
        {
            if (curve == null || curve.length == 0) return speed01;
            return Mathf.Max(0f, curve.Evaluate(speed01));
        }

        void LateUpdate()
        {
            if (cameraTransform == null) return;

            float speed = controller != null ? controller.HorizontalSpeed : 0f;
            bool grounded = controller == null || controller.IsGrounded;
            float load = carry != null ? carry.CarryLoad01 : 0f;

            // The stride, straight from the footstep system so the dip is ON the
            // sound. progress 0→1 within a step; parity picks the swaying foot.
            float progress;
            int parity;
            if (footsteps != null)
            {
                progress = footsteps.StrideProgress;
                parity = footsteps.StepCount & 1;
            }
            else
            {
                // No footstep component: synthesize an equivalent stride so the bob
                // still works (just not locked to any sound).
                if (grounded && speed > 0.5f) fallbackDistance += speed * Time.deltaTime;
                float strides = fallbackDistance / Mathf.Max(0.0001f, fallbackStepDistance);
                progress = strides - Mathf.Floor(strides);
                parity = ((int)Mathf.Floor(strides)) & 1;
            }

            // Amplitude gate: scaled by speed, zero when stopped or airborne.
            //
            // TWO RESPONSES, NOT ONE, because vertical and lateral motion are not
            // interchangeable. The vertical dip is what causes motion sickness; sway and roll
            // read as exertion at far lower comfort cost. Driving both from a single `amount`
            // meant the only way to make a sprint feel more energetic was to raise the axis
            // that makes people ill. Frequency stays locked to `progress` either way, so
            // neither curve can desync the bob from the footstep sound.
            //
            // NB stride length now scales with speed (PlayerFootsteps), so bob FREQUENCY
            // already rises less than linearly with speed - which is what makes a larger
            // sprint amplitude read as weight rather than as frantic.
            float speed01 = grounded ? Mathf.Clamp01(speed / Mathf.Max(0.01f, speedForFullBob)) : 0f;
            float vAmount = Response(verticalSpeedResponse, speed01);
            float lAmount = Response(lateralSpeedResponse, speed01);

            float vAmp = verticalAmplitude * Mathf.Lerp(1f, heavyVerticalMultiplier, load);
            float rAmp = rollAmplitude * Mathf.Lerp(1f, heavyRollMultiplier, load);

            // Vertical: one dip per footfall, lowest at the plant (progress 0/1).
            float targetVy = -Mathf.Cos(progress * Tau) * vAmp * vAmount;
            // Sway/roll: half frequency, alternating feet — zero at each plant.
            float lateralPhase = (parity + progress) * Mathf.PI;   // spans 0..2π over two steps
            float targetHx = Mathf.Sin(lateralPhase) * horizontalAmplitude * lAmount;
            float targetRz = Mathf.Sin(lateralPhase) * rAmp * lAmount;

            // Ease toward the targets. Keeps the accumulator resets (stop/land snap
            // progress to 0) from popping the camera, at the cost of a few ms of lag.
            float k = 1f - Mathf.Exp(-offsetSmoothing * Time.deltaTime);
            smoothOffset = Vector3.Lerp(smoothOffset, new Vector3(targetHx, targetVy, 0f), k);
            smoothRoll = Mathf.Lerp(smoothRoll, targetRz, k);

            // Position composes ADDITIVELY with crouch: strip last frame's bob to
            // recover the controller's true camera position, then reapply. (Crouch
            // only writes localPosition during a height transition, so it can't be
            // assumed to reset every frame — hence the explicit undo.)
            cameraTransform.localPosition -= appliedOffset;
            appliedOffset = smoothOffset;
            cameraTransform.localPosition += appliedOffset;

            // Rotation needs no undo: the controller rewrites localRotation to pure
            // pitch every frame, so we just post-multiply this frame's roll.
            if (smoothRoll != 0f) cameraTransform.localRotation *= Quaternion.Euler(0f, 0f, smoothRoll);
        }

        void OnDisable()
        {
            if (cameraTransform != null) cameraTransform.localPosition -= appliedOffset;
            appliedOffset = Vector3.zero;
            smoothOffset = Vector3.zero;
            smoothRoll = 0f;
        }
    }
}
