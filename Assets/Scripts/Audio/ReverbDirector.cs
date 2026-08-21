using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// Reverb that follows the space you are standing in, COMPUTED from the room the generator
    /// already built rather than hand-placed. Same philosophy as the pit and lintel systems:
    /// derive from existing data instead of adding an authoring step.
    ///
    /// A MIXER BUS, NOT A LISTENER FILTER. An AudioReverbFilter on the AudioListener processes
    /// the ENTIRE final mix — the music would reverberate with the room, and so would the
    /// player's own 2D sounds (sword whoosh, bow creak, carry grunt), which are in your hands
    /// and not in the room. The mixer expresses it correctly: SFX and Ambient SEND to a Reverb
    /// bus, Music never sends and is dry by construction. This component only steers the three
    /// exposed parameters on that bus.
    ///
    /// SIZE DRIVES IT. A closet and a grand hall are the same shader, the same walls and the
    /// same torches — the tail length is most of what tells them apart by ear. Interpolated
    /// between `small` and `hall` by cell count, so a new room type sounds right with no
    /// authoring at all; `AudioProfile.overrideReverb` is the exception hatch for a crypt that
    /// wants more wet than its dimensions imply.
    ///
    /// CORRIDORS AND PITS were the gap in the first draft, and they are the two most
    /// acoustically distinctive spaces the generator makes — a corridor is tight slapback, a
    /// pit is a shaft you cannot see the bottom of. Both come from their own profiles via
    /// AudioSpace, which is also why this watches the resolved SPACE and not
    /// PlayerRoomTracker.OnRoomChanged: corridors, alcoves, prisons and pits all have
    /// CurrentRoom == null, so that event fires for one of the five spaces the game has.
    /// </summary>
    [DisallowMultipleComponent]
    public class ReverbDirector : MonoBehaviour
    {
        [Header("Mixer")]
        [Tooltip("The mixer carrying the Reverb bus. The three parameters below must be EXPOSED on its SFX Reverb effect (right-click the parameter > Expose).")]
        public AudioMixer mixer;

        [Tooltip("Exposed parameter name for reverb decay time (seconds).")]
        public string decayParam = "DecayTime";
        [Tooltip("Exposed parameter name for overall reverb level (millibels, -10000..0).")]
        public string roomParam = "Room";
        [Tooltip("Exposed parameter name for high-frequency reverb level (millibels, -10000..0). NOTE the space in the default — exposed names are literal, and a typo here fails SILENTLY (SetFloat just returns false).")]
        public string roomHFParam = "Room HF";

        [Header("Computed by room size")]
        [Tooltip("Reverb for the SMALLEST room. Below smallCells this is used outright.\n\nNOTE Room and Room HF are MILLIBELS (-10000..0), not dB — see ReverbSettings. Values in the tens read as fully wet and make every room sound the same.")]
        public ReverbSettings small = new ReverbSettings { decayTime = 0.8f, room = -1800f, roomHF = -1500f };
        [Tooltip("Reverb for a GRAND hall, reached at hallCells and beyond.")]
        public ReverbSettings hall = new ReverbSettings { decayTime = 2.6f, room = -700f, roomHF = -1100f };

        [Tooltip("Room size (in CELLS) that counts as small. NB this is a VOLUME — a two-storey room counts double, which is correct: a tall hall really does sound bigger.\n\nTHE ROOM POPULATION IS BIMODAL: satellite closets are ~2 cells and ordinary rooms are 60-150, with nothing in between. Closets therefore clamp to `small` at any value above ~3, so this dial does NOT tune them — it only decides where the SMALLEST ORDINARY room sits on the curve. Set it by listening to the smallest real room, not the closets.")]
        public int smallCells = 10;
        [Tooltip("Room size (in cells) that counts as a grand hall.")]
        public int hallCells = 165;

        [Header("Transition")]
        [Tooltip("Seconds to ease between spaces. Reverb is the slowest-moving audio cue there is, so this wants to be LONGER than the ambient crossfade — a hard switch at a threshold is the one thing that makes computed reverb read as a bug. Room air spilling through a doorway is what this imitates.")]
        public float blendSeconds = 1.5f;

        [Tooltip("Log the resolved space and its reverb every time the space changes.")]
        public bool debugReverb = false;

        DungeonVisualizer vis;
        PlayerRoomTracker tracker;
        ReverbSettings currentSettings;
        SpaceKind lastKind = SpaceKind.None;
        Room lastRoom;
        bool initialized;

        void Awake()
        {
            vis = GetComponent<DungeonVisualizer>();
            if (vis == null) vis = FindFirstObjectByType<DungeonVisualizer>();
            // A quiet first attempt: it legitimately fails when our Awake runs before the
            // visualizer's, so it must NOT set triedTracker or warn — that is Update's one retry.
            var host = vis != null ? vis.gameObject : gameObject;
            tracker = host.GetComponent<PlayerRoomTracker>();
        }

        /// <summary>
        /// FROM THE VISUALIZER'S GameObject, NOT FROM THIS ONE — and that was a real bug, not a
        /// tidy-up. `PlayerRoomTracker` self-installs from `DungeonVisualizer.Awake`, so it lives
        /// wherever the VISUALIZER is; this component only usually shares that object, and Awake
        /// above admits as much by falling back to a scene-wide search for the visualizer. When it
        /// did not share it, `GetComponent` here returned null forever, `Update` returned early on
        /// the next line, and **reverb silently never ran at all**.
        ///
        /// TRIED ONCE, THEN NEVER AGAIN, WHICH IS THE OTHER HALF. `if (x == null) x = GetComponent()`
        /// looks like a cache but only caches SUCCESS: a failure retries for the lifetime of the
        /// process. In a development build a failed GetComponent builds its null-error string
        /// every time, which showed up as the single largest source of garbage in the frame —
        /// 39.4KB, from a lookup whose result nobody could use. One retry after Awake is enough
        /// by construction, because every Awake has run before the first Update.
        /// </summary>
        void ResolveTracker()
        {
            triedTracker = true;
            var host = vis != null ? vis.gameObject : gameObject;
            tracker = host.GetComponent<PlayerRoomTracker>();
            if (tracker == null)
                Debug.LogWarning("[Reverb] No PlayerRoomTracker on the DungeonVisualizer, so reverb " +
                                 "cannot tell which space you are in and stays at its defaults.", this);
        }
        bool triedTracker;

        void Update()
        {
            if (mixer == null || vis == null) return;
            // One retry after Awake: the tracker self-installs from DungeonVisualizer.Awake, which
            // may run after ours. Bounded, because a failure that repeats is a warning, not a
            // reason to keep asking.
            if (tracker == null && !triedTracker) ResolveTracker();
            if (tracker == null) return;

            AudioSpace space = AudioSpace.Resolve(vis, tracker);
            if (!space.Valid) return;

            ReverbSettings target = SettingsFor(space);

            // SNAP on the first resolve rather than easing up from a default. Easing from
            // silence would give every run's first second a wrong-sized room, and the player
            // spawns already standing somewhere specific.
            if (!initialized)
            {
                currentSettings = target;
                initialized = true;
            }
            else
            {
                float t = blendSeconds > 0.0001f ? Time.deltaTime / blendSeconds : 1f;
                currentSettings = ReverbSettings.Lerp(currentSettings, target, Mathf.Clamp01(t));
            }

            Apply(currentSettings);

            if (debugReverb && (space.Kind != lastKind || !ReferenceEquals(space.Room, lastRoom)))
            {
                lastKind = space.Kind;
                lastRoom = space.Room;
                Debug.Log($"[ReverbDirector] {space.Kind}" +
                          (space.Room != null ? $" ({space.Room.Type}, {space.SizeCells} cells)" : "") +
                          $" -> decay {target.decayTime:0.00}s, room {target.room:0}mB, HF {target.roomHF:0}mB");
            }
        }

        /// <summary>
        /// Authored reverb wins outright; otherwise interpolate by size. A space with no room
        /// (corridor, prison, alcove) has no size to compute FROM, so its profile is the only
        /// source — which is exactly why those profiles carry reverb of their own.
        /// </summary>
        ReverbSettings SettingsFor(AudioSpace space)
        {
            if (space.Profile != null && space.Profile.overrideReverb) return space.Profile.reverb;

            if (space.SizeCells <= 0)
            {
                // No room to measure. Fall back to the profile's authored value even though
                // overrideReverb is off — for a corridor it is the only number there is, and
                // treating "not overridden" as "use the small-room default" would make every
                // corridor in the game sound like a closet.
                return space.Profile != null ? space.Profile.reverb : small;
            }

            float t = Mathf.InverseLerp(smallCells, Mathf.Max(smallCells + 1, hallCells), space.SizeCells);
            return ReverbSettings.Lerp(small, hall, t);
        }

        void Apply(ReverbSettings s)
        {
            // SetFloat returns false for an unexposed name. Warned ONCE per parameter rather
            // than every frame: a typo is otherwise completely silent, and the symptom (no
            // reverb anywhere, ever) looks identical to a mis-wired mixer chain.
            if (!mixer.SetFloat(decayParam, s.decayTime)) WarnOnce(ref warnedDecay, decayParam);
            if (!mixer.SetFloat(roomParam, s.room)) WarnOnce(ref warnedRoom, roomParam);
            if (!mixer.SetFloat(roomHFParam, s.roomHF)) WarnOnce(ref warnedHF, roomHFParam);
        }

        bool warnedDecay, warnedRoom, warnedHF;

        void WarnOnce(ref bool flag, string param)
        {
            if (flag) return;
            flag = true;
            Debug.LogWarning(
                $"[ReverbDirector] '{param}' is not an exposed parameter on {mixer.name}. " +
                "Select the SFX Reverb effect on the Reverb group, right-click the parameter and " +
                "choose 'Expose ... to script', then check the name matches EXACTLY (they can " +
                "contain spaces). Reverb will not respond until this is fixed.", this);
        }

        void OnDisable()
        {
            // Leave the bus somewhere sane rather than frozen at whatever room we were in
            // when the component was switched off or the scene reloaded.
            initialized = false;
            lastKind = SpaceKind.None;
            lastRoom = null;
        }
    }
}
