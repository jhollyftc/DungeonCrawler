using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Distance-based footsteps for a CharacterController: a step fires every
    /// `stepDistance` meters of grounded horizontal travel, so cadence scales
    /// with speed automatically (sprint = faster steps, no extra logic).
    /// Random clip choice avoids immediate repeats; slight pitch jitter keeps
    /// it from sounding like a metronome. Also plays a landing thump when
    /// touching down from a fall.
    ///
    /// GAIT-AWARE: the stride LENGTH itself scales with speed, so sprinting takes
    /// longer strides and a crouched sneak takes short careful ones. Cadence was
    /// already proportional to speed for free (distance-based); scaling the stride
    /// on top is what stops a sprint reading as the walk cycle played at 2x. Volume
    /// scales the same way, matching PlayerNoiseEmitter so what you hear agrees with
    /// what NPCs hear.
    ///
    /// SURFACE-AWARE: each step probes downward and picks its clip from the
    /// SurfaceLibrary, so a wooden bridge over a pit sounds wooden with no
    /// authoring beyond the Surface the bridge already carries for sword hits.
    /// `clips` below remains the fallback for any surface the library doesn't
    /// answer for, so leaving the library empty keeps today's behaviour exactly.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerFootsteps : MonoBehaviour
    {
        [Header("Surface")]
        [Tooltip("Resolves the clip from what is underfoot. Leave its Library empty to use the fallback clips below for everything.")]
        public FootstepSurface surface = new FootstepSurface();

        [Header("Fallback clips (used when the surface has none authored)")]
        public AudioClip[] clips;
        public AudioClip landClip;
        [Tooltip("Meters of ground travel per step at WALK speed. Crouch and sprint scale off this via the strides below.")]
        public float stepDistance = 2.4f;

        [Header("Gait — stride length by speed")]
        [Tooltip("Scale stride length with how fast you're actually moving. A sprinter covers ground in LONGER strides, so cadence rises less than linearly with speed; a crouched sneak takes short careful ones. Off = one stride length at every speed, i.e. cadence purely proportional to speed.")]
        public bool scaleStrideBySpeed = true;
        [Tooltip("Stride (m) while crouched. Shorter than walking on purpose: crouching is a deliberate posture, not just a slow walk, and short quick steps are most of what sells it.")]
        public float crouchStride = 1.2f;
        [Tooltip("Stride (m) at full sprint. Longer than stepDistance. If this equals stepDistance, sprinting just machine-guns the walk cadence, which is the thing that reads as wrong.")]
        public float sprintStride = 3.2f;
        [Range(0f, 1f)] public float volume = 0.8f;

        [Header("Gait — loudness by speed")]
        [Tooltip("Scale volume with speed, so a sprint lands hard and a creep is faint. 0 = every step at full volume.\n\nDeliberately the SAME SHAPE as PlayerNoiseEmitter's step loudness, which already scales by speed and multiplies down when crouched: what you HEAR and what NPCs hear must agree, or 'quiet means unheard' stops being legible to the player.")]
        [Range(0f, 1f)] public float speedVolumeInfluence = 0.55f;
        [Tooltip("Speed (m/s) treated as full volume. Leave 0 to use the controller's sprintSpeed, which keeps it correct if that is retuned.")]
        public float fullVolumeSpeed = 0f;
        [Tooltip("Extra volume multiplier while crouched, ON TOP of the fact that crouching already slows you. This is the deliberate stealth reward — mirrors PlayerNoiseEmitter.crouchMultiplier and should roughly match it.")]
        [Range(0f, 1f)] public float crouchVolumeMultiplier = 0.55f;
        [Tooltip("Random pitch variation per step (+/-).")]
        public float pitchJitter = 0.08f;
        [Tooltip("Downward speed (m/s) required for a landing thump.")]
        public float landVelocityThreshold = 4f;
        [Tooltip("Keep counting as grounded for this long after losing contact. Walking DOWN stairs or a ramp makes CharacterController.isGrounded flicker false — the capsule leaves the lip of every step — and without this grace the stride accumulator was reset every other frame, so no footstep ever fired going downstairs (it worked going up, where the controller is pressed into each step).")]
        public float groundedGrace = 0.2f;

        CharacterController cc;
        // Optional: supplies crouch state and the authored speed bands. Absent, gait scaling
        // stands down and behaviour is exactly the old fixed-stride one - the same graceful
        // degradation as an unfilled SurfaceLibrary.
        FirstPersonController controller;
        AudioSource src;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;
        float traveled;
        bool wasGrounded = true;
        float lastYVelocity;
        float airTime;
        int lastClipIndex = -1;

        /// <summary>Fired every stride, even with no audio clips assigned (viewmodel sway hooks this).</summary>
        public System.Action OnStep;
        /// <summary>Fired on hard touchdown, with the downward impact speed in m/s.</summary>
        public System.Action<float> OnLand;

        /// <summary>Completed steps this session. Its parity (even/odd) is which foot is landing — head bob sways left/right off it.</summary>
        public int StepCount { get; private set; }
        /// <summary>How far into the current stride, 0→1, where 1 fires the next step. Head bob reads this so a foot-plant (the sound) lands exactly on the bob's dip.</summary>
        public float StrideProgress => Mathf.Clamp01(traveled / Mathf.Max(0.0001f, currentStride));

        /// <summary>
        /// The stride this step is being measured against, LATCHED at the start of each one.
        ///
        /// IT MUST NOT BE SAMPLED PER FRAME. HeadBob reads StrideProgress, which is
        /// traveled/stride — changing the denominator mid-stride makes the progress jump
        /// backwards the instant you press sprint, and the head snaps with it. Latching also
        /// means a stride is a coherent unit: it began as a walk and finishes as one, and the
        /// NEXT step picks up the new gait. Same reasoning as caching a velocity each
        /// FixedUpdate rather than reading it at the moment you need it.
        /// </summary>
        float currentStride;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            controller = GetComponent<FirstPersonController>();
            currentStride = stepDistance;
            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f; // the player's own steps: no spatialization needed
            AudioBus.Route(src, mixerGroup);
        }

        void Update()
        {
            Vector3 v = cc.velocity;

            // Coyote-time grounding. cc.isGrounded is unreliable on descents:
            // going DOWN stairs the capsule pops off each step's lip, so raw
            // isGrounded strobes false and the stride below never accumulates.
            // A short grace keeps the stride alive across those gaps while a
            // real jump/fall (longer than the grace) still reads as airborne.
            if (cc.isGrounded) airTime = 0f;
            else airTime += Time.deltaTime;
            bool grounded = cc.isGrounded || airTime < groundedGrace;

            // Landing thump on hard touchdowns.
            if (grounded && !wasGrounded && lastYVelocity < -landVelocityThreshold)
            {
                OnLand?.Invoke(-lastYVelocity);
                if (landClip != null)
                    PlayClip(landClip, volume);
                traveled = 0f;
            }

            if (grounded)
            {
                Vector3 horizontal = new Vector3(v.x, 0f, v.z);
                float speed = horizontal.magnitude;
                if (speed > 0.5f)
                {
                    // Starting from rest: adopt the current gait now rather than serving out
                    // a stale stride from before you stopped.
                    if (traveled <= 0f) currentStride = StrideFor(speed);

                    traveled += speed * Time.deltaTime;
                    if (traveled >= currentStride)
                    {
                        traveled = 0f;
                        StepCount++;
                        OnStep?.Invoke();
                        PlayStep(speed);
                        // Latch the NEXT stride from the gait as it is right now.
                        currentStride = StrideFor(speed);
                    }
                }
                else
                {
                    traveled = 0f; // standing still resets the stride
                }
            }
            else
            {
                // Airborne: prime a quick step shortly after landing.
                traveled = currentStride * 0.5f;
            }

            wasGrounded = grounded;
            lastYVelocity = v.y;
        }

        /// <summary>
        /// Stride length for the gait you are in right now.
        ///
        /// Interpolated against the CONTROLLER'S OWN authored speed bands rather than a
        /// separate set of thresholds here, so retuning walkSpeed or sprintSpeed cannot
        /// silently desync the cadence from the movement it is describing.
        ///
        /// Below walk speed the stride shortens toward the crouch value, which also handles
        /// a case nobody has to author: carrying something heavy slows you (CarrySpeedMultiplier),
        /// so a laden walk gets shorter, more laboured steps for free — the same "one mass
        /// signal drives everything that means heavy" idea as encumbrance.
        /// </summary>
        float StrideFor(float speed)
        {
            if (!scaleStrideBySpeed || controller == null) return stepDistance;
            if (controller.IsCrouching) return crouchStride;

            if (speed <= controller.walkSpeed)
                return Mathf.Lerp(crouchStride, stepDistance,
                                  Mathf.InverseLerp(controller.crouchSpeed, controller.walkSpeed, speed));

            return Mathf.Lerp(stepDistance, sprintStride,
                              Mathf.InverseLerp(controller.walkSpeed, controller.sprintSpeed, speed));
        }

        /// <summary>
        /// How loud this step is. Same shape as PlayerNoiseEmitter's loudness so the sound you
        /// hear and the noise NPCs hear cannot disagree about how quietly you are moving.
        /// </summary>
        float VolumeFor(float speed)
        {
            float vol = volume;
            if (controller == null) return vol;

            if (speedVolumeInfluence > 0f)
            {
                float full = fullVolumeSpeed > 0.01f ? fullVolumeSpeed : controller.sprintSpeed;
                float t = Mathf.Clamp01(speed / Mathf.Max(0.01f, full));
                vol *= Mathf.Lerp(1f - speedVolumeInfluence, 1f, t);
            }

            if (controller.IsCrouching) vol *= crouchVolumeMultiplier;
            return vol;
        }

        void PlayStep(float speed)
        {
            float stepVolume = VolumeFor(speed);
            // Surface first; it owns its own volume and pitch, because a boot on gravel and a
            // boot on stone are neither the same loudness nor the same tone.
            if (surface.TryPick(transform, out AudioClip surfaceClip, out float surfaceVol, out float surfacePitch))
            {
                src.pitch = surfacePitch;
                src.PlayOneShot(surfaceClip, surfaceVol * stepVolume);
                return;
            }

            if (clips == null || clips.Length == 0) return;

            int i = 0;
            if (clips.Length > 1)
            {
                do { i = Random.Range(0, clips.Length); }
                while (i == lastClipIndex);
            }
            lastClipIndex = i;

            if (clips[i] != null)
                PlayClip(clips[i], stepVolume);
        }

        void PlayClip(AudioClip clip, float vol)
        {
            src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.PlayOneShot(clip, vol);
        }
    }
}
