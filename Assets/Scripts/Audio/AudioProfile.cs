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
    /// How a positional sound occupies space — the 3D settings an AudioSource would otherwise
    /// hide behind hardcoded defaults.
    ///
    /// THESE WERE HARDCODED IN `OneShotAudioPool`, inherited from `ImpactAudio`'s tuning: linear
    /// rolloff, 1m to 25m. That is wrong for ambience in two ways at once. A sword hitting stone
    /// and a drip in a cistern have no reason to share a falloff curve, and 25m linear is barely
    /// any falloff at dungeon scale — at 5m a source is still at 83% volume, so near and far
    /// sound nearly identical and everything reads as though it were on top of you.
    ///
    /// LINEAR vs LOGARITHMIC is the control that matters most. Linear spends half its range
    /// above 50% volume, which flattens distance. Logarithmic mimics how sound actually falls
    /// off (roughly inverse-square), dropping fast near the source and trailing off — it gives a
    /// far stronger sense of near and far from the same numbers. Prefer Logarithmic and tune
    /// with `minDistance`: that is the radius of "full volume", and shrinking it is what makes a
    /// sound feel like it has a place.
    ///
    /// `Custom` rolloff is deliberately unsupported — it needs an AnimationCurve set on the
    /// source itself, which a shared pooled voice cannot carry per call.
    /// </summary>
    [System.Serializable]
    public struct AudioSpatial
    {
        [Tooltip("0 = 2D (everywhere at once, no panning), 1 = fully positional. Ambient BEDS are deliberately 2D — a drone that swings around your head as you turn reads as a machine rather than as air — but one-shots want 1.")]
        [Range(0f, 1f)] public float spatialBlend;
        [Tooltip("Radius of full volume, metres. Everything inside this is equally loud, so a LARGE value is what makes a sound seem to have no location. At a 3m cell size, 1-3 gives a sound a definite place.")]
        public float minDistance;
        [Tooltip("Beyond this the sound is inaudible (Linear) or nearly so (Logarithmic). 25 was the impact-tuned default and is very far for ambience — a drip audible three rooms away is one you cannot locate.")]
        public float maxDistance;
        [Tooltip("Logarithmic falls off fast near the source and trails away — physically natural, and much stronger near/far cue. Linear is even across the whole range, which flattens distance. Custom is unsupported here (it needs a per-source curve).")]
        public AudioRolloffMode rolloff;
        [Tooltip("Angle over which the source is spread across the speakers. 0 = a hard point (sharpest direction, can feel severe on headphones), 30-60 softens it without losing the direction.")]
        [Range(0f, 360f)] public float spread;

        public static AudioSpatial Default => new AudioSpatial
        {
            spatialBlend = 1f,
            minDistance = 2f,
            maxDistance = 15f,
            rolloff = AudioRolloffMode.Logarithmic,
            spread = 30f,
        };

        public void ApplyTo(AudioSource s)
        {
            if (s == null) return;
            s.spatialBlend = spatialBlend;
            s.minDistance = Mathf.Max(0.01f, minDistance);
            s.maxDistance = Mathf.Max(s.minDistance + 0.01f, maxDistance);
            // Custom would read an AnimationCurve off the source, which a pooled voice reused
            // by every caller cannot carry — fall back rather than silently misbehave.
            s.rolloffMode = rolloff == AudioRolloffMode.Custom ? AudioRolloffMode.Logarithmic : rolloff;
            s.spread = spread;
        }
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
        [Tooltip("How this space's one-shots sit in 3D. PER SPACE on purpose: a drip in a great hall should carry further than one in a prison cell, and a pit's sounds should reach up out of it.\n\nIf everything sounds like it is on top of you, the usual causes are a large Min Distance (the radius of full volume) or Linear rolloff — see the field tooltips. The other common cause is not here at all: a STEREO clip cannot be panned meaningfully, so tick Force To Mono on positional clips.")]
        public AudioSpatial oneShotSpatial = AudioSpatial.Default;

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
