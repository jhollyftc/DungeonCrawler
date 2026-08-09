using System.Collections;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Sound for an NPC getting hurt and dying. Its own component subscribed to
    /// Health's events — the same split as PhysicsDoorAudio riding PhysicsDoor:
    /// Health stays a number with edges, audio is a listener.
    ///
    /// House conventions throughout: one-shots on a 3D source, random no-repeat
    /// clip pick, pitch jitter so repeats don't sound stamped, volume scaled by
    /// how hard the hit actually was (the momentum-derived impulse — the same
    /// number driving knockback and the bone flinch, so what you HEAR agrees with
    /// what you SEE), and a retrigger interval so a rapid flurry doesn't
    /// machine-gun the grunt.
    ///
    /// Death is two sounds because the death ANIMATION is two moments: the cry at
    /// the instant of death, and the body-fall thud timed to when the clip puts
    /// the goblin on the floor (bodyFallDelay — eyeball it against your clip).
    /// </summary>
    [RequireComponent(typeof(Health))]
    [DisallowMultipleComponent]
    public class NpcCombatAudio : MonoBehaviour
    {
        [Header("Source (auto-added 3D if empty)")]
        [Tooltip("One-shot source. Left empty, a 3D source (linear rolloff, 25m) is added at Awake.")]
        [SerializeField] private AudioSource source;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;

        [Header("Crowd control")]
        [Tooltip("Do not play at all beyond this distance from the listener (m). Rolloff only makes a distant goblin QUIET — the source still starts, still holds a voice slot, and still competes for the real-voice budget. This was the only NPC audio component WITHOUT a cull, so at ~25 roamers every grunt and death cry in the dungeon was bidding for a voice.\n\nSet a little higher than the melee/footstep culls: a death cry carries further than a footstep, and hearing something die off-screen is information worth keeping.")]
        [SerializeField] private float cullDistance = 22f;

        /// <summary>The voice source (grunts/death cry) — NpcFace follows its amplitude to open the jaw.</summary>
        public AudioSource Source => source;

        [Header("Hurt")]
        [Tooltip("Grunts/snarls when damaged. Several = free variation.")]
        [SerializeField] private AudioClip[] hurtClips;
        [Tooltip("Hit impulse (m/s) that plays a hurt grunt at FULL volume. Same scale as knockback — a barrel at full tilt is ~7.")]
        [SerializeField] private float hurtFullImpulse = 6f;
        [Tooltip("Quietest an audible hurt grunt gets (gentle pokes still read).")]
        [Range(0f, 1f)][SerializeField] private float hurtMinimumVolume = 0.35f;
        [Tooltip("Minimum seconds between hurt grunts, so a flurry of hits doesn't machine-gun the voice.")]
        [SerializeField] private float hurtInterval = 0.25f;
        [Tooltip("Chance (0..1) a hit plays a grunt at all — a grunt EVERY hit is too much. 0.4 ≈ grunts on roughly 2 of 5 hits. The impact SFX (SurfaceLibrary) still plays every hit; this only thins the VOICE. The killing blow's death cry is separate and always plays.")]
        [Range(0f, 1f)][SerializeField] private float hurtChance = 0.4f;

        [Header("Attack voice")]
        [Tooltip("Effort grunts / battle cries as the goblin swings. Lives HERE rather than in NpcMeleeAudio because this is the VOICE source — NpcFace follows its amplitude to open the jaw, so a grunt played through it moves the goblin's mouth for free, while the weapon whoosh (correctly) does not.")]
        [SerializeField] private AudioClip[] attackClips;
        [Range(0f, 1f)][SerializeField] private float attackVolume = 0.8f;
        [Tooltip("Chance (0..1) a swing is vocalized. Same reasoning as hurtChance — a cry on EVERY swing is exhausting, and at the target population a crowd of attackers would be a wall of shouting. The whoosh still plays every swing; this only thins the voice.")]
        [Range(0f, 1f)][SerializeField] private float attackChance = 0.35f;
        [Tooltip("Minimum seconds between attack cries from this NPC.")]
        [SerializeField] private float attackInterval = 1.2f;

        [Header("Death")]
        [Tooltip("The death cry, played the instant HP hits zero.")]
        [SerializeField] private AudioClip[] deathClips;
        [Tooltip("Body-hits-the-floor thud, played bodyFallDelay seconds into the death animation. Leave empty to skip.")]
        [SerializeField] private AudioClip[] bodyFallClips;
        [Tooltip("Seconds after death before the body-fall thud — match the moment your death clip actually puts the goblin down.")]
        [SerializeField] private float bodyFallDelay = 1.1f;
        [Range(0f, 1f)][SerializeField] private float deathVolume = 1f;

        [Header("Variation")]
        [Tooltip("Random pitch spread on every clip.")]
        [SerializeField] private Vector2 pitchRange = new Vector2(0.92f, 1.08f);

        Health health;
        MeleeAttack melee;      // optional — an unarmed NPC never cries out swinging
        float nextHurtTime, nextAttackTime;
        int lastHurtIndex = -1, lastDeathIndex = -1, lastFallIndex = -1, lastAttackIndex = -1;

        void Awake()
        {
            health = GetComponent<Health>();
            melee = GetComponent<MeleeAttack>();
            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 1f;                       // a goblin yelps from WHERE IT IS
                source.rolloffMode = AudioRolloffMode.Linear;
                source.maxDistance = 25f;
            }
            source.playOnAwake = false;
            // AND STOP IT. playOnAwake only governs a FUTURE start - it cannot undo one already
            // underway, and the engine acts on the authored flag before this runs. A source set to
            // play on awake with NO CLIP enters a playing state that never completes, so it reports
            // isPlaying forever while making no sound: silent, invisible, and holding a voice slot.
            // Measured: 186 phantom voices against a real-voice budget of 32.
            source.Stop();
            source.priority = AudioPriority.NpcVoice;

            AudioBus.Route(source, mixerGroup);
        }

        void OnEnable()
        {
            health.OnDamaged += HandleDamaged;
            health.OnDied += HandleDied;
            if (melee != null) melee.OnSwingStart += HandleSwing;
        }

        void OnDisable()
        {
            health.OnDamaged -= HandleDamaged;
            health.OnDied -= HandleDied;
            if (melee != null) melee.OnSwingStart -= HandleSwing;
        }

        /// <summary>
        /// The effort grunt on a swing. Fired at OnSwingStart — the START of the windup —
        /// which is right for a VOICE even though it would be wrong for the whoosh: you draw
        /// breath as you begin the swing, not as the blade lands.
        /// </summary>
        void HandleSwing()
        {
            if (health.IsDead) return;
            if (Time.time < nextAttackTime) return;
            if (Random.value > attackChance) return;
            nextAttackTime = Time.time + attackInterval;

            PlayOneShot(attackClips, ref lastAttackIndex, attackVolume);
        }

        void HandleDamaged(DamageInfo info)
        {
            if (health.IsDead) return;                          // the killing blow gets the death cry, not a grunt
            if (Time.time < nextHurtTime) return;
            if (Random.value > hurtChance) return;              // not every hit grunts — the impact SFX still plays
            nextHurtTime = Time.time + hurtInterval;

            float force = Mathf.Clamp01(info.impulse / Mathf.Max(0.01f, hurtFullImpulse));
            PlayOneShot(hurtClips, ref lastHurtIndex, Mathf.Lerp(hurtMinimumVolume, 1f, force));
        }

        void HandleDied(DamageInfo info)
        {
            PlayOneShot(deathClips, ref lastDeathIndex, deathVolume);
            if (bodyFallClips != null && bodyFallClips.Length > 0)
                StartCoroutine(BodyFall());
        }

        IEnumerator BodyFall()
        {
            yield return new WaitForSeconds(bodyFallDelay);
            PlayOneShot(bodyFallClips, ref lastFallIndex, deathVolume);
        }

        void PlayOneShot(AudioClip[] clips, ref int lastIndex, float volume)
        {
            if (source == null || clips == null || clips.Length == 0) return;

            // CULL BEFORE PLAYING, not by rolloff. maxDistance only makes a distant goblin
            // QUIET - the source still starts, still holds a slot, and still competes for the
            // real-voice budget. This component was the only NPC audio without a cull, so at
            // ~25 roamers every grunt and death cry in the dungeon was bidding for a voice.
            // Measured: a crowd fight peaked at 189 playing with 84 stolen.
            //
            // NB this also stops NpcFace opening the jaw for that goblin, since the face
            // follows this source's amplitude. That is correct rather than a side effect: a
            // goblin too far to be heard is too far for its mouth to be read.
            if (AudioCull.TooFar(transform, cullDistance)) return;

            int i = 0;
            if (clips.Length > 1)
            {
                do { i = Random.Range(0, clips.Length); }
                while (i == lastIndex);
            }
            lastIndex = i;
            if (clips[i] == null) return;

            source.pitch = Random.Range(pitchRange.x, pitchRange.y);
            source.PlayOneShot(clips[i], volume);
        }
    }
}
