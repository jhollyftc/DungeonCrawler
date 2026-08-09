using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The bow's SOUND: the creak of the string coming back, the snap of the loose, and
    /// the softer sigh of a draw let down without shooting. Its own component subscribed
    /// to PlayerBow's events, same split as PlayerMeleeAudio riding PlayerMelee and
    /// NpcCombatAudio riding Health — the bow stays about mechanics, audio only listens.
    ///
    /// Two sources on purpose, because the draw and the loose are different KINDS of
    /// sound. The draw is CONTINUOUS and tracks a live value, so it's a loop whose pitch
    /// and volume follow Draw01 — the same continuous-vs-one-shot split PhysicsDoorAudio
    /// makes between its creak and its thunk, and it's what makes holding at full draw
    /// audibly tense rather than silent. The loose is a one-shot scaled by the draw it
    /// was released at, so a snap shot sounds feeble and a full draw cracks.
    ///
    /// 2D sources (spatialBlend 0): a first-person weapon is YOUR arms, not a position
    /// out in the world. Same reasoning as PlayerMeleeAudio.
    /// </summary>
    [RequireComponent(typeof(PlayerBow))]
    [DisallowMultipleComponent]
    public class PlayerBowAudio : MonoBehaviour
    {
        [Header("Sources (auto-added 2D if empty)")]
        [Tooltip("One-shots: nock, loose, let-down. Left empty, a 2D source is added at Awake.")]
        [SerializeField] private AudioSource source;
        [Tooltip("The LOOPING draw creak. Needs its own source because it plays continuously underneath the one-shots. Left empty, a second 2D source is added.")]
        [SerializeField] private AudioSource drawLoopSource;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;

        [Header("Clips")]
        [Tooltip("Played once as the draw begins — the arrow nocking, the first pull.")]
        [SerializeField] private AudioClip[] nockClips;
        [Tooltip("Looping creak of the string under tension. Volume and pitch follow the draw, so the tension is audible while you hold.")]
        [SerializeField] private AudioClip drawLoop;
        [Tooltip("The loose. Volume and pitch scale with the draw it fired at.")]
        [SerializeField] private AudioClip[] releaseClips;
        [Tooltip("Let-down — released below the firing threshold, string easing home. Softer than a loose; falls back to the nock clips if empty.")]
        [SerializeField] private AudioClip[] relaxClips;

        [Header("Mix")]
        [Range(0f, 1f)][SerializeField] private float nockVolume = 0.7f;
        [Range(0f, 1f)][SerializeField] private float relaxVolume = 0.5f;
        [Tooltip("Loose volume at the MINIMUM firing draw.")]
        [Range(0f, 1f)][SerializeField] private float releaseVolumeMin = 0.45f;
        [Tooltip("Loose volume at a FULL draw.")]
        [Range(0f, 1f)][SerializeField] private float releaseVolumeMax = 1f;
        [Tooltip("Loose pitch across the draw range — a weak shot sits higher and thinner.")]
        [SerializeField] private Vector2 releasePitchRange = new Vector2(1.12f, 0.95f);
        [Tooltip("Random pitch spread on every one-shot, so repeats don't sound stamped.")]
        [SerializeField] private Vector2 pitchJitter = new Vector2(0.97f, 1.03f);

        [Header("Draw loop")]
        [Tooltip("Loop volume from the start of the draw to full.")]
        [SerializeField] private Vector2 drawLoopVolume = new Vector2(0.15f, 0.75f);
        [Tooltip("Loop pitch from the start of the draw to full — rising pitch is what reads as increasing tension.")]
        [SerializeField] private Vector2 drawLoopPitch = new Vector2(0.85f, 1.25f);
        [Tooltip("How fast the loop fades out once the draw ends. A hard cut clicks.")]
        [SerializeField] private float drawLoopFadeOut = 8f;

        PlayerBow bow;
        int lastNock = -1, lastRelease = -1, lastRelax = -1;

        void Awake()
        {
            bow = GetComponent<PlayerBow>();

            if (source == null)
            {
                source = gameObject.AddComponent<AudioSource>();
                source.spatialBlend = 0f;   // 2D — the player's own bow
            }
            source.playOnAwake = false;
            // AND STOP IT. playOnAwake only governs a FUTURE start - it cannot undo one already
            // underway, and the engine acts on the authored flag before this runs. A source set to
            // play on awake with NO CLIP enters a playing state that never completes, so it reports
            // isPlaying forever while making no sound: silent, invisible, and holding a voice slot.
            // Measured: 186 phantom voices against a real-voice budget of 32.
            source.Stop();
            AudioBus.Route(source, mixerGroup);

            if (drawLoopSource == null)
            {
                drawLoopSource = gameObject.AddComponent<AudioSource>();
                drawLoopSource.spatialBlend = 0f;
            }
            drawLoopSource.playOnAwake = false;
            AudioBus.Route(drawLoopSource, mixerGroup);
            drawLoopSource.loop = true;
            drawLoopSource.clip = drawLoop;
            drawLoopSource.volume = 0f;
        }

        void OnEnable()
        {
            bow.OnDrawStarted += HandleDrawStarted;
            bow.OnShot += HandleShot;
            bow.OnDrawAborted += HandleAborted;
        }

        void OnDisable()
        {
            bow.OnDrawStarted -= HandleDrawStarted;
            bow.OnShot -= HandleShot;
            bow.OnDrawAborted -= HandleAborted;

            // Swapped weapons or disabled mid-draw — don't leave a creak looping forever.
            if (drawLoopSource != null) drawLoopSource.Stop();
        }

        void Update()
        {
            if (drawLoopSource == null || drawLoop == null) return;

            // Driven from the live draw rather than started/stopped by events, so every
            // exit path (fired, let down, weapon swapped, picked up a barrel) fades it out
            // without each needing to remember to.
            if (bow.IsDrawing)
            {
                float d = bow.Draw01;
                drawLoopSource.volume = Mathf.Lerp(drawLoopVolume.x, drawLoopVolume.y, d);
                drawLoopSource.pitch = Mathf.Lerp(drawLoopPitch.x, drawLoopPitch.y, d);
                if (!drawLoopSource.isPlaying) drawLoopSource.Play();
            }
            else if (drawLoopSource.isPlaying)
            {
                drawLoopSource.volume = Mathf.MoveTowards(drawLoopSource.volume, 0f,
                                                         drawLoopFadeOut * Time.deltaTime);
                if (drawLoopSource.volume <= 0.001f) drawLoopSource.Stop();
            }
        }

        void HandleDrawStarted() => PlayOneShot(nockClips, ref lastNock, nockVolume, 1f);

        void HandleShot(float draw)
        {
            // Draw 0..1 maps onto the loose's weight. The two ends of releasePitchRange
            // are deliberately reversible (default high→low), so a weak shot is thin and
            // a full draw is deep.
            float volume = Mathf.Lerp(releaseVolumeMin, releaseVolumeMax, draw);
            float pitch = Mathf.Lerp(releasePitchRange.x, releasePitchRange.y, draw);
            PlayOneShot(releaseClips, ref lastRelease, volume, pitch);
        }

        void HandleAborted()
        {
            if (Has(relaxClips)) PlayOneShot(relaxClips, ref lastRelax, relaxVolume, 1f);
            else PlayOneShot(nockClips, ref lastNock, relaxVolume, 0.9f);   // still make a sound
        }

        static bool Has(AudioClip[] clips) => clips != null && clips.Length > 0;

        void PlayOneShot(AudioClip[] clips, ref int lastIndex, float volume, float pitch)
        {
            if (source == null || !Has(clips)) return;

            // Random NO-REPEAT pick, house convention — a two-clip set alternating at
            // random still repeats often enough to sound mechanical.
            int i = 0;
            if (clips.Length > 1)
            {
                do { i = Random.Range(0, clips.Length); }
                while (i == lastIndex);
            }
            lastIndex = i;
            if (clips[i] == null) return;

            source.pitch = pitch * Random.Range(pitchJitter.x, pitchJitter.y);
            source.PlayOneShot(clips[i], volume);
        }
    }
}
