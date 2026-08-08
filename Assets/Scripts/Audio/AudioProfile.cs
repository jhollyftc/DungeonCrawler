using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Reverb for one kind of space, as EXPLICIT FLOATS driving the mixer's SFX Reverb
    /// effect — not an AudioReverbPreset.
    ///
    /// TWO REASONS IT ISN'T A PRESET ENUM. `AudioReverbFilter` ignores its manual
    /// parameters unless the preset is `User`, and two enum values cannot be interpolated
    /// at all — which would make "blend between a small room and a great hall" literally
    /// unimplementable. Floats blend.
    ///
    /// AND IT IS A MIXER BUS, NOT A LISTENER FILTER (a correction to the original plan): a
    /// filter on the AudioListener processes the ENTIRE final mix, so the music would
    /// reverberate with the room, and so would the player's own 2D sounds — sword whoosh,
    /// bow creak, carry grunt. Those are in your hands, not in the room. Sending SFX and
    /// Ambient to a Reverb group leaves Music dry by construction.
    /// </summary>
    [System.Serializable]
    public struct ReverbSettings
    {
        [Tooltip("Reverberation decay, seconds. Small stone cell ~0.6, corridor ~1.0, great hall ~2.5, a pit shaft longer still.")]
        public float decayTime;
        [Tooltip("Overall reverb level, dB (mixer range is roughly -80..0). Less negative = wetter.")]
        public float room;
        [Tooltip("High-frequency reverb level, dB. More negative = darker, stone-damped tail; near 0 = bright and tiled.")]
        public float roomHF;

        public static ReverbSettings Lerp(ReverbSettings a, ReverbSettings b, float t) => new ReverbSettings
        {
            decayTime = Mathf.Lerp(a.decayTime, b.decayTime, t),
            room = Mathf.Lerp(a.room, b.room, t),
            roomHF = Mathf.Lerp(a.roomHF, b.roomHF, t),
        };
    }

    /// <summary>
    /// The RoomStyle of sound — what one kind of SPACE sounds like.
    ///
    /// Mirrors RoomStyle deliberately (§7 of CLAUDE.md): same authoring pattern, same
    /// fallback philosophy, resolved at the same point in the pipeline. Empty fields
    /// degrade gracefully rather than falling silent, exactly as an unauthored wall slot
    /// falls back to the kit's generic rather than rendering nothing.
    ///
    /// NOTE THERE IS NO `footstepSurface` HERE. Footsteps resolve through the existing
    /// `SurfaceType` / `Surface.Of` system instead — a second enum meaning "what is this
    /// made of" would drift from the first (§12's category lesson), and surface is a
    /// property of the CELL rather than the room: a bridge deck is wood inside a stone
    /// room, and a per-room-type enum could never say so.
    /// </summary>
    [CreateAssetMenu(fileName = "AudioProfile", menuName = "Dungeon/Audio Profile")]
    public class AudioProfile : ScriptableObject
    {
        [Header("Ambient beds")]
        [Tooltip("Always-on low drone. Usually the SAME clip across most profiles — it is the dungeon's floor of sound, not this space's identity. Routes to Ambient/Base.")]
        public AudioClip ambientBaseLayer;
        [Tooltip("This space's IDENTITY bed: drips for a cistern, wind for a shaft, crackle for a kitchen. Routes to Ambient/RoomType. Empty = only the base drone plays here, which is a legitimate authoring choice for a plain room.")]
        public AudioClip ambientRoomLayer;

        [Header("Ambient one-shots")]
        [Tooltip("Occasional positional sounds scattered through the space — a drip, a rat, a distant creak. Routes to Ambient/OneShots. Empty = none.")]
        public AudioClip[] oneShotPool;
        [Tooltip("Seconds between one-shots, picked per interval. Wide ranges read as natural; tight ones read as a metronome and will be noticed.")]
        public Vector2 oneShotIntervalRange = new Vector2(6f, 18f);

        [Header("Reverb")]
        [Tooltip("Author reverb for this space instead of deriving it from room SIZE. Off = the computed value, which is the normal case — a crypt wanting more wet than its dimensions imply is the exception this exists for.")]
        public bool overrideReverb = false;
        public ReverbSettings reverb = new ReverbSettings { decayTime = 1.2f, room = -12f, roomHF = -18f };

        [Header("Music")]
        [Tooltip("Floor for the tension signal in this space (0-1). A shrine can sit uneasy while empty; a merchant room can refuse to get tense at all.")]
        [Range(0f, 1f)] public float musicTensionBaseline = 0f;

        [Header("Voice budget")]
        [Tooltip("AudioSource.priority for this space's ambient sources. LOWER NUMBER = HIGHER priority (Unity's scale is inverted, 0 = never stolen, 256 = first to go). Ambient is the bed: if it drops out the world sounds broken, whereas one missing footstep does not — so it should outrank transient SFX.")]
        [Range(0, 256)] public int voicePriority = 64;
    }
}
