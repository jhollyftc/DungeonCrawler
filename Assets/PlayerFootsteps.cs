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
    /// All-stone dungeon for now, so one clip set. When surface variety
    /// arrives (wood, water), this is where a downward raycast would pick the
    /// set per material.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerFootsteps : MonoBehaviour
    {
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
        float traveled;
        bool wasGrounded = true;
        float lastYVelocity;
        float airTime;
        int lastClipIndex = -1;

        /// <summary>Fired every stride, even with no audio clips assigned (viewmodel sway hooks this).</summary>
        public System.Action OnStep;
        /// <summary>Fired on hard touchdown, with the downward impact speed in m/s.</summary>
        public System.Action<float> OnLand;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f; // the player's own steps: no spatialization needed
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