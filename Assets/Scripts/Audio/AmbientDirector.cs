using System.Collections.Generic;
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
        [Tooltip("Mixer group for scattered ambient one-shots. Route to Ambient/OneShots.")]
        public AudioMixerGroup oneShotGroup;

        [Header("One-shots")]
        [Tooltip("Volume for scattered one-shots, before the mixer.")]
        [Range(0f, 1f)] public float oneShotVolume = 0.8f;
        [Tooltip("Random pitch range per one-shot. A drip that is always the same pitch stops sounding like water within about four repeats.")]
        public Vector2 oneShotPitchRange = new Vector2(0.9f, 1.1f);

        [Header("Debug")]
        [Tooltip("Scene-view gizmos: every candidate floor cell for the current space, and where recent one-shots actually fired. Turns 'they sound like they're all in the middle' into something you can look at — the distinction matters because a clip that is positioned correctly but STEREO reads as centred no matter where it is (3D panning needs mono), and only a picture separates the two.")]
        public bool debugOneShots = false;
        [Tooltip("How many recent one-shot positions to keep on screen.")]
        [Range(1, 60)] public int debugHistory = 20;
        [Tooltip("Seconds a marker stays visible.")]
        public float debugLifetime = 12f;

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
                nextOneShot = -1f;   // re-arm; see TickOneShots
                baseLayer.Play(p != null ? p.ambientBaseLayer : null);
                roomLayer.Play(p != null ? p.ambientRoomLayer : null);
                if (p != null) { baseLayer.SetPriority(p.voicePriority); roomLayer.SetPriority(p.voicePriority); }
            }

            float dt = Time.deltaTime;
            baseLayer.Tick(dt, crossfadeSeconds, volume);
            roomLayer.Tick(dt, crossfadeSeconds, volume);
            TickOneShots(dt);
        }

        // ---------------- Scattered one-shots ----------------

        float nextOneShot = -1f;
        bool warnedNoOneShotGroup;
        Room cachedFloorRoom;
        List<Vector3Int> floorCells;

        /// <summary>
        /// ONE ticker for the whole dungeon, not a coroutine per room: only the space the
        /// player is standing in is audible, so only it needs to tick. This is the cheap
        /// version of "how many ambient sources exist at once", done before it becomes a
        /// problem rather than after.
        /// </summary>
        void TickOneShots(float dt)
        {
            var p = current;
            if (p == null || p.oneShotPool == null || p.oneShotPool.Length == 0) { nextOneShot = -1f; return; }

            // Re-arm on entering a space, so a one-shot cannot fire the instant you cross a
            // threshold (which reads as caused by you rather than by the room).
            if (nextOneShot < 0f) { nextOneShot = Interval(p); return; }

            nextOneShot -= dt;
            if (nextOneShot > 0f) return;
            nextOneShot = Interval(p);

            AudioClip clip = p.oneShotPool[Random.Range(0, p.oneShotPool.Length)];
            if (clip == null) return;
            if (!TryPickPoint(out Vector3 point)) return;

            // A source with NO mixer group does not route through the mixer at all — it goes
            // straight to the AudioListener, bypassing every group INCLUDING Master. So an
            // unassigned group is not "routes to Master", it is "invisible to the mixer": the
            // sounds are plainly audible while no meter anywhere moves, and no volume slider
            // or duck can touch them. That reads as "the mixer is broken" rather than as one
            // empty field, so it says so once instead.
            if (oneShotGroup == null && !warnedNoOneShotGroup)
            {
                warnedNoOneShotGroup = true;
                Debug.LogWarning("[AmbientDirector] One Shot Group is unassigned, so ambient " +
                                 "one-shots BYPASS THE MIXER ENTIRELY (straight to the listener — " +
                                 "not even the Master meter moves, and no volume or duck applies). " +
                                 "Assign it to Ambient/OneShots.", this);
            }

            // Reuses the SAME pool the surface impacts use, deliberately. §5 wants voices
            // centrally owned, and a second private pool would be a second budget nobody is
            // tracking — the F7 overlay would show them competing without showing why.
            OneShotAudioPool.Play(clip, point, oneShotVolume,
                                  Random.Range(oneShotPitchRange.x, oneShotPitchRange.y),
                                  oneShotGroup, p.oneShotSpatial);

            if (debugOneShots)
            {
                pings.Add(new Ping { pos = point, time = Time.time });
                while (pings.Count > debugHistory) pings.RemoveAt(0);
            }
        }

        struct Ping { public Vector3 pos; public float time; }
        readonly List<Ping> pings = new List<Ping>();

        /// <summary>
        /// Draws the CANDIDATE SET and the OUTCOMES together, which is the whole point: a
        /// scatter that looks correct against a floor plan but sounds centred is a CLIP
        /// problem (a stereo clip cannot be panned meaningfully), while markers genuinely
        /// bunched in the middle would be a placement problem. Only seeing both separates them
        /// — the same instrument-before-hypothesising move that settled the crowd jitter and
        /// the alcove rejection tally, both of which had confident wrong diagnoses first.
        /// </summary>
        void OnDrawGizmos()
        {
            if (!debugOneShots || !Application.isPlaying || vis == null) return;
            float cs = vis.cellSize;

            // Every cell a one-shot COULD have picked, for the room case.
            if (floorCells != null && tracker != null && tracker.CurrentRoom != null)
            {
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.25f);
                foreach (var c in floorCells)
                    Gizmos.DrawWireCube(CellCentre(c) + Vector3.up * 0.05f,
                                        new Vector3(cs * 0.85f, 0.02f, cs * 0.85f));
            }

            // Where they actually fired, newest brightest.
            for (int i = 0; i < pings.Count; i++)
            {
                float age = Time.time - pings[i].time;
                if (age > debugLifetime) continue;
                float k = 1f - age / Mathf.Max(0.01f, debugLifetime);
                Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.25f + 0.75f * k);
                Gizmos.DrawSphere(pings[i].pos + Vector3.up * 0.3f, 0.12f + 0.25f * k);
            }

            // The listener, so "centred" can be judged relative to where you were standing.
            if (tracker != null && tracker.HasPlayer && tracker.player != null)
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.9f);
                Gizmos.DrawWireSphere(tracker.player.position + Vector3.up * 0.3f, 0.35f);
            }
        }

        static float Interval(AudioProfile p) =>
            Mathf.Max(0.25f, Random.Range(p.oneShotIntervalRange.x, p.oneShotIntervalRange.y));

        /// <summary>
        /// A world point on real FLOOR near the player.
        ///
        /// NOT a random point in the room's bounds. A bounds-random point lands in a pit
        /// opening, inside an L-room's bite, or in a wall — a pit opening in particular passes
        /// every "is this a room cell" test because it genuinely is one, it simply has no floor
        /// (§12's category rule). `RoomPropPlacer.ComputeZones` is the single source of truth
        /// for "which cells can something stand on", and it is what the prop placers already
        /// use, so a drip cannot end up somewhere a crate could not.
        ///
        /// Corridors, alcoves and prisons have no Room and therefore no zones, so those scan a
        /// small neighbourhood instead: open cell, solid directly below. Same test, less
        /// machinery.
        /// </summary>
        bool TryPickPoint(out Vector3 point)
        {
            point = default;
            var gen = vis.Generator;
            if (gen == null || tracker == null || !tracker.HasPlayer) return false;

            Room room = tracker.CurrentRoom;
            if (room != null)
            {
                if (!ReferenceEquals(room, cachedFloorRoom))
                {
                    cachedFloorRoom = room;
                    floorCells = RoomPropPlacer.ComputeZones(gen, room).Floor;
                }
                if (floorCells == null || floorCells.Count == 0) return false;

                // A few tries so an L-room's bite, or a pit rim, cannot swallow the pick.
                for (int i = 0; i < 4; i++)
                {
                    Vector3 p = PointInCell(floorCells[Random.Range(0, floorCells.Count)]);
                    if (!Audible(p)) continue;
                    point = p;
                    return true;
                }
                return false;
            }

            // Corridor-like space: sample a few cells around the player and take the first
            // that has a floor. A handful of tries beats building and caching a set for a
            // space the player is walking straight through.
            var grid = gen.Grid;
            Vector3Int at = tracker.CurrentCell;
            for (int i = 0; i < 12; i++)
            {
                var c = new Vector3Int(at.x + Random.Range(-4, 5), at.y, at.z + Random.Range(-4, 5));
                if (!grid.InBounds(c) || grid[c] == CellType.Empty) continue;
                var below = c + Vector3Int.down;
                if (grid.InBounds(below) && grid[below] != CellType.Empty) continue;  // no floor here

                Vector3 p = PointInCell(c);
                if (!Audible(p)) continue;
                point = p;
                return true;
            }
            return false;
        }

        Vector3 CellCentre(Vector3Int c) =>
            new Vector3((c.x + 0.5f) * vis.cellSize, c.y * vis.cellSize, (c.z + 0.5f) * vis.cellSize)
            + transform.position;

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
        /// Which profile applies where the player is standing. The resolution — and its
        /// load-bearing ORDER — lives in AudioSpace, shared with ReverbDirector so ambience
        /// <summary>
        /// Is a point in open earshot of the player?
        ///
        /// A ±4 CELL BOX REACHES STRAIGHT THROUGH WALLS. The corridor scan above only asks
        /// "open cell, floor below", which is true of an adjacent PRISON, a neighbouring room,
        /// or anything one cell of rock away — so drips were being placed where the player
        /// physically could not hear them. Before occlusion existed that was invisible and
        /// arguably a happy accident; with it, those one-shots muffle into nothing and the
        /// ambience quietly thins out.
        ///
        /// Asking the occlusion system directly is better than restricting by CellType: an
        /// ALCOVE is typed Hallway and a prison MOUTH is genuinely audible from the corridor,
        /// so a type rule would reject good placements and accept bad ones. "Can the player
        /// hear this" is the actual requirement, and it is one raycast.
        ///
        /// Costs at most a dozen casts per one-shot, which fire seconds apart.
        /// </summary>
        bool Audible(Vector3 worldPoint) => AudioOcclusion.Sample(worldPoint) < 0.5f;

        /// <summary>
        /// A point in the cell, raised to a random height within the OPEN COLUMN above it.
        ///
        /// Measured by walking UP from the cell until the grid closes, so a two-storey hall
        /// genuinely uses two storeys while a corridor stays inside its one — the same
        /// authored fraction means "somewhere in this space's height" in both, which an
        /// authored metre range could not. A pit's opening column resolves the same way.
        ///
        /// NB it deliberately does not check what is BELOW: the caller has already required a
        /// floor, and a bridge deck over a pit should sound like the deck, not the shaft.
        /// </summary>
        Vector3 PointInCell(Vector3Int c)
        {
            Vector3 basePos = CellCentre(c);

            var grid = vis.Generator != null ? vis.Generator.Grid : null;
            if (grid == null || current == null) return basePos;

            int open = 1;
            var probe = c + Vector3Int.up;
            while (grid.InBounds(probe) && grid[probe] != CellType.Empty && open < 8)
            {
                open++;
                probe += Vector3Int.up;
            }

            Vector2 r = current.oneShotHeightRange;
            float t = Random.Range(Mathf.Min(r.x, r.y), Mathf.Max(r.x, r.y));
            return basePos + Vector3.up * (open * vis.cellSize * Mathf.Clamp01(t));
        }

        /// and reverb can never disagree about which space you are in.
        /// </summary>
        AudioProfile Resolve() => AudioSpace.Resolve(vis, tracker).Profile;
    }
}
