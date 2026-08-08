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
                    s.spatialBlend = 1f;                   // positional, as PlayClipAtPoint was
                    s.rolloffMode = AudioRolloffMode.Linear;
                    s.maxDistance = 25f;
                    instance.sources[i] = s;
                }
                return instance;
            }
        }

        /// <summary>
        /// Play `clip` at a world position through `group`. Pitch is honoured, which
        /// PlayClipAtPoint could not do.
        /// </summary>
        public static void Play(AudioClip clip, Vector3 point, float volume, float pitch,
                                AudioMixerGroup group)
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
            s.volume = volume;
            s.pitch = pitch;
            AudioBus.Route(s, group);
            s.Play();
        }
    }
}
