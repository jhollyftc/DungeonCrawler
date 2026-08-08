using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Footstep audio for an NPC — the counterpart to PlayerFootsteps, and the main way you
    /// hear a goblin coming before you see it.
    ///
    /// DISTANCE-BASED, exactly like the player's: a step every `stepDistance` of grounded
    /// horizontal travel, so cadence scales with speed for free and a walking goblin and a
    /// charging one are audibly different without a second setting.
    ///
    /// **WHY NOT ANIMATION EVENTS, when the melee sweep deliberately uses them.** A swing is
    /// ONE clip in a dedicated state, so an event on its impact frame fires exactly once.
    /// Locomotion is a 2D BLEND TREE (forward/back/strafe), and Unity fires a clip's animation
    /// events whenever that clip contributes to the blend — so a goblin walking diagonally
    /// fires footsteps from two or three clips at once, and the double/triple-stepping gets
    /// worse the more directions blend. The distance accumulator sidesteps the whole problem
    /// and can't desync from actual movement, which is what a footstep is really reporting.
    ///
    /// Reads NpcLocomotion.CurrentSpeed, which is `trueVelocity` — never Controller.velocity,
    /// which spikes during push corrections and would fire phantom steps while an NPC is
    /// being shoved (the same reason NpcAnimatorDriver reads it, §10).
    ///
    /// Deliberately does NOT emit to the NoiseBus. That bus is how NPCs hear things, so
    /// footsteps on it would have every goblin permanently hearing every other goblin — an
    /// alert loop, the same failure the shout system has to rate-limit around. These steps
    /// are a cue for the PLAYER's ears only.
    /// </summary>
    [RequireComponent(typeof(NpcLocomotion))]
    [DisallowMultipleComponent]
    public class NpcFootsteps : MonoBehaviour
    {
        [Header("Source (auto-added 3D if empty)")]
        [Tooltip("Left empty, a 3D source (linear rolloff) is added at Awake. Separate from NpcCombatAudio's voice source — NpcFace follows that one's amplitude to move the jaw, and footsteps must not open a goblin's mouth.")]
        [SerializeField] private AudioSource source;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;
        [Tooltip("Rolloff distance (m). Shorter than the voice's 25m: you should hear a goblin shout down a corridor but only hear its feet once it is genuinely near — that gap is what makes footsteps a useful proximity cue instead of ambience.")]
        [SerializeField] private float maxDistance = 12f;

        [Header("Clips")]
        [SerializeField] private AudioClip[] clips;
        [Range(0f, 1f)][SerializeField] private float volume = 0.55f;
        [Tooltip("Random pitch spread per step, so a repeated clip doesn't sound stamped.")]
        [SerializeField] private float pitchJitter = 0.1f;

        [Header("Cadence")]
        [Tooltip("Metres of grounded travel per step. Should roughly match the walk clip's stride or the feet and the sound drift apart — the one number worth checking against the animation.")]
        [SerializeField] private float stepDistance = 1.6f;
        [Tooltip("Below this speed (m/s) no steps play at all. A settled crowd never sits at exactly zero — separation and repathing nudge NPCs indefinitely — and without a floor that residual creep accumulates into phantom footsteps from a goblin that is visibly standing still. Same reasoning as NpcAnimatorDriver.movementDeadzone.")]
        [SerializeField] private float movementDeadzone = 0.15f;
        [Tooltip("Seconds of coyote time before an NPC counts as airborne. CharacterController.isGrounded STROBES false descending stairs (the capsule pops off each step lip), which would reset the stride accumulator every frame and silence footsteps going down — exactly the bug PlayerFootsteps needed groundedGrace to fix.")]
        [SerializeField] private float groundedGrace = 0.2f;

        [Header("Crowd control")]
        [Tooltip("Don't play beyond this distance from the listener (m). At the target population, dozens of inaudible sources still consume AudioSource voices; culling is cheaper and cleaner than letting rolloff fade them.")]
        [SerializeField] private float cullDistance = 16f;
        [Tooltip("Scale volume with speed, so a creeping NPC is quieter than a charging one. 0 = every step at full volume.")]
        [Range(0f, 1f)][SerializeField] private float speedVolumeInfluence = 0.5f;
        [Tooltip("Speed (m/s) treated as full volume when speedVolumeInfluence is above 0.")]
        [SerializeField] private float fullVolumeSpeed = 3f;

        /// <summary>A step landed — the hook for dust VFX or a future surface-aware step.</summary>
        public System.Action OnStep;

        NpcLocomotion body;
        float traveled;
        float airTime;
        int lastClipIndex = -1;

        void Awake()
        {
            body = GetComponent<NpcLocomotion>();

            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 1f;               // a goblin's feet are WHERE IT IS
                source.rolloffMode = AudioRolloffMode.Linear;
            }
            source.maxDistance = maxDistance;
            source.playOnAwake = false;
            AudioBus.Route(source, mixerGroup);
        }

        void Update()
        {
            if (body == null || body.Controller == null) return;

            // Locomotion disabled = this NPC isn't walking anywhere, so neither are its feet.
            // Covers DEATH (NpcHitReactions disables the locomotion and its controller, but
            // trueVelocity keeps its last value, so without this a corpse could squeeze out a
            // step or two mid-topple) and DORMANCY (roadmap 26 parks room NPCs by disabling
            // exactly this component, which makes their footsteps free rather than something
            // dormancy has to remember to switch off separately).
            if (!body.enabled)
            {
                traveled = 0f;
                return;
            }

            // Coyote-time grounding, same fix as the player's. Without it, descending stairs
            // silences footsteps entirely while ascending them works fine — a maddeningly
            // asymmetric symptom.
            if (body.Controller.isGrounded) airTime = 0f;
            else airTime += Time.deltaTime;
            bool grounded = body.Controller.isGrounded || airTime < groundedGrace;

            if (!grounded)
            {
                traveled = 0f;
                return;
            }

            float speed = body.CurrentSpeed;
            if (speed < movementDeadzone)
            {
                // Bleed the accumulator rather than zeroing it, so an NPC that pauses briefly
                // mid-stride resumes near where it left off instead of restarting the step.
                traveled = Mathf.MoveTowards(traveled, 0f, Time.deltaTime * stepDistance);
                return;
            }

            traveled += speed * Time.deltaTime;
            if (traveled < stepDistance) return;

            traveled -= stepDistance;
            PlayStep(speed);
        }

        void PlayStep(float speed)
        {
            OnStep?.Invoke();

            if (source == null || clips == null || clips.Length == 0) return;

            // Distance cull. AudioListener rather than a cached player reference: the listener
            // is what actually decides audibility, and it survives the player rig being
            // rebuilt on a dungeon regenerate.
            if (cullDistance > 0f)
            {
                var listener = FindObjectOfType<AudioListener>();
                if (listener != null &&
                    (listener.transform.position - transform.position).sqrMagnitude > cullDistance * cullDistance)
                    return;
            }

            // Random NO-REPEAT pick, house convention.
            int i = 0;
            if (clips.Length > 1)
            {
                do { i = Random.Range(0, clips.Length); }
                while (i == lastClipIndex);
            }
            lastClipIndex = i;
            if (clips[i] == null) return;

            float vol = volume;
            if (speedVolumeInfluence > 0f)
            {
                float t = Mathf.Clamp01(speed / Mathf.Max(0.01f, fullVolumeSpeed));
                vol *= Mathf.Lerp(1f - speedVolumeInfluence, 1f, t);
            }

            source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            source.PlayOneShot(clips[i], vol);
        }
    }
}
