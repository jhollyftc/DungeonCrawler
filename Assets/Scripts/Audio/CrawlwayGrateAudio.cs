using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The grinding of a grate being hauled out of stone.
    ///
    /// HOUSE PATTERN (§10b): a continuous sound is a LOOPING source whose volume and pitch track
    /// a live value, driven from state every frame — never started and stopped by events. Here
    /// that value is <see cref="CrawlwayGrate.Strain01"/>, so every exit path (let go, walk away,
    /// the grate breaks free, the player dies mid-haul, the dungeon regenerates) fades the loop
    /// out without any of them having to remember to. Same shape as PhysicsDoorAudio's creak and
    /// PlayerBowAudio's draw.
    ///
    /// THE FEEDBACK IS NOT DECORATION. Strain accumulates from how far the player hauls, which
    /// is invisible — without a sound that rises with it, the mechanic is indistinguishable from
    /// a delay and people stop pulling before the threshold. The rising pitch is what says
    /// "keep going", and the snap says why you did.
    /// </summary>
    [RequireComponent(typeof(CrawlwayGrate))]
    public class CrawlwayGrateAudio : MonoBehaviour
    {
        [Tooltip("Looping grind/creak while the grate is being worked. Volume and pitch follow strain.")]
        public AudioClip strainLoop;
        [Tooltip("One-shot when it finally gives — the crack of iron coming out of stone. The landing clang is separate and comes from ImpactAudio on the grate itself.")]
        public AudioClip breakClip;

        [Tooltip("Loop volume at full strain.")]
        [Range(0f, 1f)] public float maxVolume = 0.8f;
        [Tooltip("Loop pitch at zero strain and at full strain. Rising pitch is the 'almost there' signal, and it is doing most of the work.")]
        public float minPitch = 0.85f;
        public float maxPitch = 1.35f;
        [Tooltip("How fast the loop fades in and out. Fast enough to answer the player, slow enough that a stutter in their hauling does not chop the sound.")]
        public float fade = 6f;

        [Range(0f, 1f)] public float breakVolume = 1f;

        [Tooltip("Mixer group for both sources — the Physics bus. LEAVING THIS EMPTY IS NOT 'defaults to Master': an unassigned group bypasses the mixer entirely and goes straight to the listener, so the sound is inaudible to every meter, immune to every volume slider, and looks exactly like a source that is not playing (§10b).")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;

        CrawlwayGrate grate;
        AudioSource loopSource;
        AudioSource oneShotSource;
        bool wasOpen;

        void Awake()
        {
            grate = GetComponent<CrawlwayGrate>();

            loopSource = gameObject.AddComponent<AudioSource>();
            loopSource.clip = strainLoop;
            loopSource.loop = true;
            loopSource.volume = 0f;
            loopSource.spatialBlend = 1f;
            // playOnAwake OFF and an explicit Stop(): a source told to play with a NULL clip
            // enters a playing state that never completes and holds a real voice forever —
            // §10b's phantom voice, which was 186 of them at its worst. Setting the flag in
            // Awake is not enough on its own because the engine acts on the authored value
            // first, so both are needed.
            loopSource.playOnAwake = false;
            loopSource.Stop();
            AudioBus.Route(loopSource, mixerGroup);
            loopSource.priority = AudioPriority.WorldImpact;

            oneShotSource = gameObject.AddComponent<AudioSource>();
            oneShotSource.playOnAwake = false;
            oneShotSource.Stop();
            oneShotSource.spatialBlend = 1f;
            AudioBus.Route(oneShotSource, mixerGroup);
            oneShotSource.priority = AudioPriority.WorldImpact;
        }

        void Update()
        {
            if (grate == null) return;

            if (grate.IsOpen && !wasOpen)
            {
                wasOpen = true;
                if (breakClip != null) oneShotSource.PlayOneShot(breakClip, breakVolume);
            }

            // DERIVED FROM STATE EVERY FRAME, never toggled at transitions. `IsGripped` going
            // false for ANY reason — including ones nothing here knows about — fades this out.
            float target = grate.IsGripped && !grate.IsOpen ? grate.Strain01 : 0f;

            float want = target * maxVolume;
            loopSource.volume = Mathf.MoveTowards(loopSource.volume, want, fade * Time.deltaTime);
            loopSource.pitch = Mathf.Lerp(minPitch, maxPitch, target);

            if (loopSource.volume > 0.001f)
            {
                if (strainLoop != null && !loopSource.isPlaying) loopSource.Play();
            }
            else if (loopSource.isPlaying) loopSource.Stop();
        }

        // Swapping weapons or dying mid-haul must not leave a grind looping forever.
        void OnDisable()
        {
            if (loopSource != null) { loopSource.Stop(); loopSource.volume = 0f; }
        }
    }
}
