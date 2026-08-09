using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// Pooled positional one-shots — the replacement for `AudioSource.PlayClipAtPoint`.
    ///
    /// PLAYCLIPATPOINT CANNOT BE MIXED. It spawns a hidden temporary GameObject with an
    /// AudioSource and destroys it when the clip ends; nothing ever exposes that source, so
    /// it can never be given a mixer group. Every sound played that way bypasses the mixer
    /// entirely — no bus, no volume control, and critically no DUCKING, which matters
    /// because surface impacts are exactly the event the ducking design keys off. It also
    /// cannot vary pitch, which SurfaceImpact already carried as a noted limitation.
    ///
    /// A FIXED RING rather than one source per sound, for the reason NpcMeleeAudio already
    /// documents: voices are a bounded resource (~32 real), and an unbounded spawn-per-impact
    /// steals them from footsteps and combat during exactly the busy moment that produced the
    /// impacts. Oldest-wins reuse means a burst truncates its own earliest sound rather than
    /// silencing something else.
    ///
    /// ONE CHILD OBJECT PER VOICE, not eight sources on one object: an AudioSource is
    /// positioned by its TRANSFORM, so sources sharing a transform cannot play at different
    /// places at the same time — the second sound would drag the first to its position.
    ///
    /// Self-creating and DontDestroyOnLoad, because the scene reloads on F1 and on every
    /// depth change and a pool that died with it would have to be re-found by every caller.
    /// Statics reset on play-mode entry — the fast-enter-playmode trap NoiseBus and
    /// EmissiveMaterialVariants already guard.
    /// </summary>
    public class OneShotAudioPool : MonoBehaviour
    {
        const int Voices = 8;

        static OneShotAudioPool instance;
        AudioSource[] sources;
        int next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => instance = null;

        static OneShotAudioPool Instance
        {
            get
            {
                if (instance != null) return instance;

                var root = new GameObject("OneShotAudioPool");
                Object.DontDestroyOnLoad(root);
                instance = root.AddComponent<OneShotAudioPool>();
                instance.sources = new AudioSource[Voices];

                for (int i = 0; i < Voices; i++)
                {
                    var go = new GameObject($"Voice{i}");
                    go.transform.SetParent(root.transform, false);
                    var s = go.AddComponent<AudioSource>();
                    s.playOnAwake = false;

                    // Registered so occlusion keeps TRACKING while a voice plays, rather than
                    // being resolved once and frozen. It matters here and not for a footstep
                    // or an impact: those last a few hundred ms, during which the player
                    // cannot get behind a wall, but an ambient chant or chain rattle runs for
                    // SECONDS and walking out of earshot mid-clip is entirely normal.
                    // The manager skips voices that are not playing, so the eight idle ones
                    // cost nothing between sounds.
                    AudioOcclusion.Register(s, 1f);
                    instance.sources[i] = s;
                }
                return instance;
            }
        }

        /// <summary>
        /// Play `clip` at a world position through `group`. Pitch is honoured, which
        /// PlayClipAtPoint could not do.
        ///
        /// `spatial` is applied PER CALL, not once at pool construction: the voices are shared
        /// between ambient one-shots and surface impacts, which want different falloff — a drip
        /// in a cistern and a sword on stone have no reason to share a curve. The pool used to
        /// hardcode ImpactAudio's values (linear, 1m to 25m) for both, which at dungeon scale
        /// is barely any falloff at all: 83% volume at five metres, so near and far sounded
        /// alike and everything seemed to be on top of you.
        /// </summary>
        public static void Play(AudioClip clip, Vector3 point, float volume, float pitch,
                                AudioMixerGroup group, AudioSpatial spatial)
        {
            if (clip == null) return;
            var pool = Instance;

            // Oldest-wins: step the ring rather than hunting for a free voice. A source still
            // playing gets cut, which is the right trade in a burst — the alternatives are an
            // unbounded spawn or dropping the NEW sound, and the new one is the one the player
            // just caused.
            AudioSource s = pool.sources[pool.next];
            pool.next = (pool.next + 1) % Voices;

            s.transform.position = point;
            s.clip = clip;
            s.pitch = pitch;
            spatial.ApplyTo(s);          // sets spatialBlend, which the occlusion gate reads

            // OCCLUSION SEEDED HERE, then TRACKED by the manager for as long as this plays.
            // Seeding unconditionally is what stops a pooled voice inheriting the muffling of
            // whatever it played last — same reasoning as AudioBus.Assign being unconditional
            // on this path — and `Begin` snaps rather than eases, because a NEW sound should
            // start correct instead of sliding into correctness over the smoothing window.
            float occ = AudioOcclusion.Manager.occlude && s.spatialBlend >= AudioOcclusion.PositionalBlend
                ? AudioOcclusion.Sample(point) : 0f;
            AudioOcclusion.Manager.Begin(s, volume, occ);

            AudioBus.Assign(s, group);   // pooled voice: assign unconditionally, see AudioBus
            s.Play();
        }
    }
}
