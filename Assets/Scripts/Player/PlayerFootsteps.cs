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
        [Tooltip("Meters of ground travel per step.")]
        public float stepDistance = 2.4f;
        [Range(0f, 1f)] public float volume = 0.8f;
        [Tooltip("Random pitch variation per step (+/-).")]
        public float pitchJitter = 0.08f;
        [Tooltip("Downward speed (m/s) required for a landing thump.")]
        public float landVelocityThreshold = 4f;
        [Tooltip("Keep counting as grounded for this long after losing contact. Walking DOWN stairs or a ramp makes CharacterController.isGrounded flicker false — the capsule leaves the lip of every step — and without this grace the stride accumulator was reset every other frame, so no footstep ever fired going downstairs (it worked going up, where the controller is pressed into each step).")]
        public float groundedGrace = 0.2f;

        CharacterController cc;
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
        public float StrideProgress => Mathf.Clamp01(traveled / Mathf.Max(0.0001f, stepDistance));

        void Awake()
        {
            cc = GetComponent<CharacterController>();
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
                    traveled += speed * Time.deltaTime;
                    if (traveled >= stepDistance)
                    {
                        traveled = 0f;
                        StepCount++;
                        OnStep?.Invoke();
                        PlayStep();
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
                traveled = stepDistance * 0.5f;
            }

            wasGrounded = grounded;
            lastYVelocity = v.y;
        }

        void PlayStep()
        {
            // Surface first; it owns its own volume and pitch, because a boot on gravel and a
            // boot on stone are neither the same loudness nor the same tone.
            if (surface.TryPick(transform, out AudioClip surfaceClip, out float surfaceVol, out float surfacePitch))
            {
                src.pitch = surfacePitch;
                src.PlayOneShot(surfaceClip, surfaceVol * volume);
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
                PlayClip(clips[i], volume);
        }

        void PlayClip(AudioClip clip, float vol)
        {
            src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.PlayOneShot(clip, vol);
        }
    }
}
