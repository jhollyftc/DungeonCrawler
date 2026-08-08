using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// Plays the ambient beds for whatever space the player is standing in, crossfading as
    /// they move between spaces.
    ///
    /// A MANAGER, NOT A COMPONENT PER ROOM (§4 of SOUNDSYSTEM_PLAN, resolved). Three reasons,
    /// in order of weight: the voice budget needs a central owner, because you cannot allocate
    /// a bounded number of voices from inside per-room components that don't know about each
    /// other; per-room roots are per-regenerate garbage, and audio accumulating is far worse
    /// than geometry accumulating (you HEAR five copies of a drone); and TorchCullingManager is
    /// the existing precedent for exactly this shape — many similar sources, distance-gated,
    /// centrally budgeted, one manager.
    ///
    /// TWO SOURCES PER LAYER, not one. A crossfade needs the outgoing clip to keep playing
    /// while the incoming one rises; a single source can only cut. Four sources total, fixed,
    /// regardless of how many rooms the dungeon has.
    ///
    /// IT WATCHES THE RESOLVED PROFILE, NOT `OnRoomChanged` (the trap in this component).
    /// Corridors, alcoves and prisons ALL have `CurrentRoom == null` — an alcove is typed
    /// Hallway and a prison is its own CellType, neither is a Room — so walking from a corridor
    /// into an alcove fires no room-change event at all, and a director listening only to that
    /// event would never switch beds between the three. Resolving the profile per frame and
    /// comparing is a handful of dictionary lookups; the event is left for consumers whose
    /// question really is "did the ROOM change".
    /// </summary>
    [RequireComponent(typeof(DungeonVisualizer))]
    public class AmbientDirector : MonoBehaviour
    {
        [Header("Routing")]
        [Tooltip("Mixer group for the always-on drone. Route to Ambient/Base.")]
        public AudioMixerGroup baseGroup;
        [Tooltip("Mixer group for the per-space identity bed. Route to Ambient/RoomType.")]
        public AudioMixerGroup roomGroup;

        [Header("Feel")]
        [Tooltip("Seconds to crossfade when the space changes. Long enough that a doorway is a transition rather than a switch; short enough that a small room registers before you leave it. Room air spilling through a door is what this is imitating.")]
        public float crossfadeSeconds = 2.5f;
        [Tooltip("Master level for ambient beds, before the mixer. Leave at 1 and mix on the Ambient bus — this exists for a quick in-context trim while authoring.")]
        [Range(0f, 1f)] public float volume = 1f;

        DungeonVisualizer vis;
        PlayerRoomTracker tracker;
        Layer baseLayer, roomLayer;
        AudioProfile current;

        /// <summary>A crossfading pair of sources playing one looping clip.</summary>
        class Layer
        {
            AudioSource a, b;
            bool onB;
            float t = 1f;          // 1 = settled on the active source
            AudioClip target;

            public Layer(Transform parent, string name, AudioMixerGroup group)
            {
                a = Make(parent, name + "_A", group);
                b = Make(parent, name + "_B", group);
            }

            static AudioSource Make(Transform parent, string name, AudioMixerGroup group)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = true;
                s.volume = 0f;
                // 2D-ish on purpose: a bed is the air of the room, not an object in it. A
                // positional drone would swing around the head as you turn, which reads as a
                // machine somewhere rather than as atmosphere.
                s.spatialBlend = 0f;
                AudioBus.Route(s, group);
                return s;
            }

            public void SetPriority(int p) { a.priority = p; b.priority = p; }

            /// <summary>Begin a crossfade to `clip`. Null fades the layer out.</summary>
            public void Play(AudioClip clip)
            {
                if (clip == target) return;
                target = clip;

                AudioSource incoming = onB ? a : b;
                incoming.clip = clip;
                if (clip != null) incoming.Play();
                onB = !onB;
                t = 0f;
            }

            public void Tick(float dt, float fade, float master)
            {
                if (t < 1f) t = fade > 0.01f ? Mathf.Min(1f, t + dt / fade) : 1f;

                AudioSource inc = onB ? b : a;
                AudioSource outg = onB ? a : b;
                inc.volume = master * t;
                outg.volume = master * (1f - t);

                // Stop the faded-out source so it isn't holding a voice for silence — the
                // budget reasoning in §5 applies to beds as much as to one-shots.
                if (t >= 1f && outg.isPlaying) { outg.Stop(); outg.clip = null; }
            }
        }

        void Awake()
        {
            vis = GetComponent<DungeonVisualizer>();
            tracker = GetComponent<PlayerRoomTracker>();
        }

        void Update()
        {
            if (vis == null || vis.roomStyle == null) return;
            if (tracker == null) tracker = GetComponent<PlayerRoomTracker>();

            EnsureLayers();

            AudioProfile p = Resolve();
            if (!ReferenceEquals(p, current))
            {
                current = p;
                baseLayer.Play(p != null ? p.ambientBaseLayer : null);
                roomLayer.Play(p != null ? p.ambientRoomLayer : null);
                if (p != null) { baseLayer.SetPriority(p.voicePriority); roomLayer.SetPriority(p.voicePriority); }
            }

            float dt = Time.deltaTime;
            baseLayer.Tick(dt, crossfadeSeconds, volume);
            roomLayer.Tick(dt, crossfadeSeconds, volume);
        }

        /// <summary>
        /// Created ONCE and deliberately NOT registered in DungeonVisualizer.GeneratedRoots.
        /// That list is for roots rebuilt on every generate; these four sources are owned by a
        /// persistent component and are RETARGETED rather than respawned, so listing them would
        /// destroy the beds on every F1 and restart them from silence. The rule the list
        /// encodes — "anything created per generate must be cleaned per generate" — is
        /// satisfied by never creating these per generate.
        /// </summary>
        void EnsureLayers()
        {
            if (baseLayer != null) return;
            var root = new GameObject("DungeonAmbient");
            root.transform.SetParent(transform, false);
            baseLayer = new Layer(root.transform, "Base", baseGroup);
            roomLayer = new Layer(root.transform, "Room", roomGroup);
        }

        /// <summary>
        /// Which profile applies where the player is standing. Order matters: a pit cell also
        /// resolves to a Room (RoomAt falls through PitAt), and an alcove cell is CellType
        /// Hallway, so the most specific space has to be asked about first.
        /// </summary>
        AudioProfile Resolve()
        {
            var style = vis.roomStyle;
            var gen = vis.Generator;
            if (gen == null || tracker == null || !tracker.HasPlayer) return null;

            Vector3Int cell = tracker.CurrentCell;

            PitSpec pit = tracker.CurrentPit;
            if (pit != null) return style.PitAudio(pit.Owner != null ? pit.Owner.Type : RoomType.Generic);

            Room room = tracker.CurrentRoom;
            if (room != null) return style.AudioFor(room.Type);

            if (gen.PrisonAt(cell) != null) return style.PrisonAudio();

            AlcoveSpec alcove = gen.AlcoveAt(cell);
            if (alcove != null) return style.AlcoveAudio(alcove.Kind);

            return style.HallwayAudio();
        }
    }
}
