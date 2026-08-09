using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Muffles and quietens sounds with geometry between them and the listener.
    ///
    /// NOT VIA THE ROOM GRAPH, and this is the one thing to get right. The Delaunay/MST graph
    /// encodes "a corridor was carved between these two rooms", not "sound can travel between
    /// them": two rooms sharing a wall are usually NOT graph-connected, and two that ARE can
    /// be thirty metres apart through winding corridor. Using it would muffle the room you can
    /// practically hear through the wall while passing sound freely down a corridor that
    /// should attenuate it — wrong in both directions. A raycast asks the actual question.
    ///
    /// TWO PATHS, because a one-shot and a loop need different things:
    ///   - LOOPS register once and are re-tested on a round-robin slice, smoothed over time,
    ///     with this owning their volume from then on. A door creak has to stop being muffled
    ///     as you round the corner, so it needs re-testing; smoothing is what stops a body
    ///     crossing the line of sight from making it stutter.
    ///   - ONE-SHOTS are resolved by a SINGLE raycast at the moment they play, and scale the
    ///     volume ARGUMENT rather than the source. There is no later tick that could correct
    ///     an impact that has already finished, and scaling the argument means this never
    ///     fights whoever owns the source's volume.
    ///
    /// EXEMPTION BY `spatialBlend`, not by a list. Anything 2D — the player's own footsteps,
    /// sword, bow, and the ambient beds — is by definition not in the world, so it cannot be
    /// occluded by the world. One rule, and a new 2D sound gets it right without being told.
    /// </summary>
    public static class AudioOcclusion
    {
        /// <summary>
        /// A source counts as positional (and therefore occludable) above this blend. Below it
        /// the sound is in your head or in your hands, and muffling it as you stand behind a
        /// wall would be muffling YOU.
        /// </summary>
        public const float PositionalBlend = 0.5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => manager = null;

        static AudioOcclusionManager manager;

        public static AudioOcclusionManager Manager
        {
            get
            {
                if (manager == null) manager = Object.FindFirstObjectByType<AudioOcclusionManager>();
                if (manager == null)
                {
                    // Auto-install, same as OneShotAudioPool: nothing should have to remember
                    // to place this, and a scene-authored one wins if there is one to tune.
                    var go = new GameObject("AudioOcclusion");
                    Object.DontDestroyOnLoad(go);
                    manager = go.AddComponent<AudioOcclusionManager>();
                }
                return manager;
            }
        }

        /// <summary>
        /// How blocked `worldPos` is from the listener, 0 (clear) to 1 (fully blocked).
        /// A single raycast; callers that need this every frame should register instead.
        /// </summary>
        public static float Sample(Vector3 worldPos) => Manager.SampleAt(worldPos);

        /// <summary>
        /// Play a one-shot with occlusion applied. Returns immediately if there is no clip.
        ///
        /// The volume ARGUMENT is scaled and the source's lowpass is set for this shot, so
        /// nothing here writes `source.volume` and no ownership is taken. `at` is where the
        /// sound happens, which for a source that follows a moving object is its own position
        /// but for an impact is the contact point.
        /// </summary>
        public static void PlayOneShot(AudioSource source, AudioClip clip, float volume, Vector3 at)
        {
            if (source == null || clip == null) return;

            var m = Manager;
            if (!m.occlude || source.spatialBlend < PositionalBlend)
            {
                source.PlayOneShot(clip, volume);
                return;
            }

            float o = m.SampleAt(at);
            m.ApplyLowpass(source, o);
            source.PlayOneShot(clip, volume * m.VolumeScale(o));
        }

        /// <summary>Overload for a source that IS at the sound's position.</summary>
        public static void PlayOneShot(AudioSource source, AudioClip clip, float volume) =>
            PlayOneShot(source, clip, volume, source != null ? source.transform.position : Vector3.zero);

        /// <summary>
        /// Register a LOOPING source. From here on this owns its volume, flickering it around
        /// `baseVolume` as occlusion changes — the same "one owner per property" contract
        /// TorchFlicker has with Light.intensity, and for the same reason.
        /// </summary>
        public static void Register(AudioSource source, float baseVolume) =>
            Manager.Register(source, baseVolume, true);

        /// <summary>
        /// Register a loop for MUFFLING ONLY, leaving its volume to whoever already drives it.
        ///
        /// For sources whose `volume` is their own state — PhysicsDoorAudio and HangingCageAudio
        /// MoveTowards on it each frame and then read it back to decide whether to play at all —
        /// so a second writer would not merely fight them, it would corrupt the accumulator and
        /// the play/stop threshold with it.
        ///
        /// Muffling alone is a perfectly good result here, and arguably the better half: losing
        /// the highs is what reads as BLOCKED, where attenuation alone just reads as further
        /// away.
        /// </summary>
        public static void RegisterFilterOnly(AudioSource source) =>
            Manager.Register(source, 0f, false);

        /// <summary>Change the volume a registered loop is scaled FROM.</summary>
        public static void SetBaseVolume(AudioSource source, float baseVolume) =>
            Manager.SetBaseVolume(source, baseVolume);

        public static void Unregister(AudioSource source)
        {
            if (manager != null) manager.Unregister(source);
        }
    }

    /// <summary>
    /// Authorable occlusion tuning. Lives on DungeonVisualizer beside FogSettings and
    /// TorchSettings and is pushed into the runtime manager at generation — the manager
    /// auto-installs on first use and so cannot be configured in the inspector before play,
    /// which is exactly the gap this closes.
    /// </summary>
    [System.Serializable]
    public class OcclusionSettings
    {
        [Tooltip("Master switch. Off = every source plays unoccluded.")]
        public bool occlude = true;

        [Tooltip("What blocks sound. MUST exclude the NPC layer — a goblin standing between you and a torch is not a wall, and a crowd walking through the line would make every sound behind it flutter. Exclude Viewmodel too. Same rule as FootstepSurface.probeMask and NpcFootIK.groundMask.")]
        public LayerMask blockerMask = ~0;

        [Tooltip("Volume multiplier at FULL occlusion. Not 0 on purpose: a sound that vanishes behind a wall reads as the game switching it off, and it removes the cue that something is happening nearby.")]
        [Range(0f, 1f)] public float occludedVolume = 0.4f;

        [Tooltip("Lowpass cutoff (Hz) at full occlusion. THIS is what reads as 'through a wall' — attenuation alone just sounds further away, losing the highs sounds BLOCKED. 700-1200 is stone. Tune this before touching the volume.")]
        public float occludedCutoff = 900f;

        [Tooltip("Metres of the path next to the SOURCE that are ignored, so a sound emitted from a surface - an impact at its contact point, a torch 0.3m off a wall - is not occluded by that surface. 0.5-1.0.")]
        public float sourceSkin = 0.75f;

        [Tooltip("Seconds for a registered LOOP to ease between states. Stops a body crossing the line of sight making a door creak stutter. One-shots ignore it — they resolve once, when they play.")]
        public float smoothing = 0.25f;

        [Tooltip("Registered loops re-tested per frame, round-robin. Silent sources are skipped, so this only has to keep up with the ones actually sounding.")]
        public int checksPerFrame = 4;

        public void ApplyTo(AudioOcclusionManager m)
        {
            if (m == null) return;
            m.occlude = occlude;
            m.blockerMask = blockerMask;
            m.occludedVolume = occludedVolume;
            m.occludedCutoff = occludedCutoff;
            m.sourceSkin = sourceSkin;
            m.smoothing = smoothing;
            m.checksPerFrame = checksPerFrame;
        }
    }

    /// <summary>
    /// The registry and the raycast budget behind <see cref="AudioOcclusion"/>. A MANAGER
    /// rather than a component per source, matching TorchCullingManager: a shared round-robin
    /// keeps the per-frame cost flat regardless of how many sources exist, where per-source
    /// Update methods would scale with them.
    /// </summary>
    [DisallowMultipleComponent]
    public class AudioOcclusionManager : MonoBehaviour
    {
        [Tooltip("Master switch. Off = every source plays unoccluded, and registered loops are restored to their base volume.")]
        public bool occlude = true;

        [Tooltip("What blocks sound. Should be the world's solid geometry and NOT the NPC layer — a goblin standing between you and a torch is not a wall, and a crowd would make every sound behind it flutter. Also exclude Viewmodel and triggers.")]
        public LayerMask blockerMask = ~0;

        [Tooltip("Volume multiplier at FULL occlusion. Not 0: a sound you cannot hear at all through a wall reads as the game switching it off, and it removes the directional cue that tells you something is happening nearby. 0.3-0.5 keeps presence while making the wall obvious.")]
        [Range(0f, 1f)] public float occludedVolume = 0.4f;

        [Tooltip("Lowpass cutoff (Hz) when clear. 22000 is effectively no filtering.")]
        public float clearCutoff = 22000f;
        [Tooltip("Lowpass cutoff (Hz) at full occlusion. THIS is what actually reads as 'through a wall' — attenuation alone just sounds further away, while losing the highs sounds BLOCKED. 700-1200 is a stone wall.")]
        public float occludedCutoff = 900f;

        [Tooltip("Metres of the path NEXT TO THE SOURCE that are ignored. Sound is emitted from surfaces constantly here - an impact plays AT its contact point, and a wall torch sits only 0.3m off a wall the greybox collider may be inset in front of - so a ray run all the way to the source hits the geometry the sound is coming off and reads as fully blocked. 0.5-1.0; walls are a 3m cell thick, so nothing legitimate hides inside it.")]
        public float sourceSkin = 0.75f;

        [Tooltip("Seconds for a registered LOOP to ease between occlusion states. Stops a body crossing the line of sight from making a door creak stutter. One-shots ignore this — they resolve once, at the instant they play.")]
        public float smoothing = 0.25f;

        [Tooltip("Registered loops re-tested per frame, round-robin. Torches and doors do not move, so this only has to keep up with the LISTENER walking.")]
        public int checksPerFrame = 4;

        class Entry
        {
            public AudioSource src;
            public AudioLowPassFilter lp;
            public float baseVolume;
            public bool ownsVolume;
            public float current;      // smoothed occlusion 0..1
            public float target;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly Dictionary<AudioSource, Entry> bySource = new Dictionary<AudioSource, Entry>();
        int cursor;

        /// <summary>0 = clear line to the listener, 1 = blocked.</summary>
        public float SampleAt(Vector3 worldPos)
        {
            if (!occlude) return 0f;
            var l = AudioCull.Listener;
            if (l == null) return 0f;

            Vector3 lp = l.transform.position;
            Vector3 delta = worldPos - lp;
            float dist = delta.magnitude;

            // STOP SHORT OF THE SOURCE. Sound in this game is emitted FROM surfaces far more
            // often than from open air, and a ray that runs all the way to the source hits the
            // very geometry the sound is coming off:
            //   - SurfaceImpact plays at the CONTACT POINT, which is by definition exactly on a
            //     collider. Every arrow and sword hit on a wall would read as fully occluded.
            //   - A wall torch sits only `wallGap` (0.3m) off the wall plane, and `wallMargin`
            //     insets the greybox collider TOWARD the room - so with a non-zero margin the
            //     collision surface ends up in FRONT of the torch and occludes it from
            //     everywhere, including the corridor it is lighting.
            // Cheaper and more robust than special-casing either: ask whether anything blocks
            // the path UP TO the last few centimetres, not whether the source is touching
            // something. Walls here are a 3m cell thick, so nothing legitimate hides inside the
            // skin.
            float reach = dist - Mathf.Max(0.01f, sourceSkin);
            if (reach <= 0.01f) return 0f;   // basically on top of the listener

            // Binary per cast, smoothed over time by the caller for loops. Counting hits to
            // gauge WALL THICKNESS was considered and rejected: RaycastAll costs more, and the
            // dungeon's walls are all one cell thick, so the extra number would be noise.
            return Physics.Raycast(lp, delta / dist, reach, blockerMask,
                                   QueryTriggerInteraction.Ignore) ? 1f : 0f;
        }

        public float VolumeScale(float occlusion) => Mathf.Lerp(1f, occludedVolume, occlusion);

        public void ApplyLowpass(AudioSource src, float occlusion)
        {
            if (src == null) return;
            float cutoff = Mathf.Lerp(clearCutoff, occludedCutoff, occlusion);

            // Add the filter LAZILY, and only once a source is actually occluded. Most sources
            // never are, and an AudioLowPassFilter on every one is DSP paid for nothing.
            var lp = src.GetComponent<AudioLowPassFilter>();
            if (lp == null)
            {
                if (occlusion <= 0.001f) return;      // nothing to do, don't add the component
                lp = src.gameObject.AddComponent<AudioLowPassFilter>();
            }
            lp.cutoffFrequency = cutoff;
        }

        public void Register(AudioSource src, float baseVolume, bool ownsVolume)
        {
            if (src == null || bySource.ContainsKey(src)) return;
            var e = new Entry { src = src, baseVolume = baseVolume, ownsVolume = ownsVolume, current = 0f, target = 0f };
            entries.Add(e);
            bySource[src] = e;
        }

        public void SetBaseVolume(AudioSource src, float baseVolume)
        {
            if (src != null && bySource.TryGetValue(src, out var e)) e.baseVolume = baseVolume;
        }

        /// <summary>
        /// Hand a registered voice a fresh sound: set what it should be scaled from, and SNAP
        /// its occlusion rather than easing.
        ///
        /// SNAPPING IS THE POINT. A pooled voice carries the smoothed state of whatever it
        /// played last, so a drip starting in the open would audibly un-muffle over the
        /// smoothing window because the previous user of that voice was behind a wall. Easing
        /// is right for a CONTINUING sound whose occlusion changes; it is wrong for a NEW one,
        /// which should simply start correct.
        /// </summary>
        public void Begin(AudioSource src, float baseVolume, float occlusion)
        {
            if (src == null || !bySource.TryGetValue(src, out var e)) return;
            e.baseVolume = baseVolume;
            e.current = occlusion;
            e.target = occlusion;
            if (e.ownsVolume) src.volume = baseVolume * VolumeScale(occlusion);
            ApplyLowpass(src, occlusion);
        }

        public void Unregister(AudioSource src)
        {
            if (src == null || !bySource.TryGetValue(src, out var e)) return;
            bySource.Remove(src);
            entries.Remove(e);
        }

        void Update()
        {
            if (entries.Count == 0) return;

            // Round-robin a slice, so cost is flat in the number of registered loops.
            if (occlude)
            {
                int n = Mathf.Min(checksPerFrame, entries.Count);
                for (int i = 0; i < n; i++)
                {
                    cursor = (cursor + 1) % entries.Count;
                    var e = entries[cursor];
                    if (e.src == null) continue;

                    // Don't pay a raycast for a source that isn't making a sound. Doors and
                    // cages are silent until something swings them, and a dungeon holds far
                    // more of them than are ever audible at once — without this the round-robin
                    // spends most of its budget testing silence, and the sources that ARE
                    // playing get re-tested that much slower.
                    if (!e.src.isPlaying) continue;

                    e.target = e.src.spatialBlend < AudioOcclusion.PositionalBlend
                        ? 0f : SampleAt(e.src.transform.position);
                }
            }

            float step = smoothing > 0.0001f ? Time.deltaTime / smoothing : 1f;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.src == null)    // the dungeon regenerated out from under it
                {
                    entries.RemoveAt(i);
                    continue;
                }

                float want = occlude ? e.target : 0f;
                e.current = Mathf.MoveTowards(e.current, want, step);
                if (e.ownsVolume) e.src.volume = e.baseVolume * VolumeScale(e.current);
                ApplyLowpass(e.src, e.current);
            }
        }
    }
}
