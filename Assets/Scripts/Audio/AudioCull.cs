using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// "Is the listener too far for this to be worth playing at all?"
    ///
    /// CULLING IS NOT ROLLOFF, and the difference is the whole reason this exists. A 3D
    /// rolloff makes a distant source QUIET; the source still starts, still holds an
    /// AudioSource slot, and still competes against every other source for the real-voice
    /// budget (32 by default). Culling returns BEFORE Play() is ever called: no source, no
    /// slot, no competition.
    ///
    /// At the target population of ~25 roamers that is the difference between every grunt
    /// in the dungeon bidding for a voice and only the ones you could plausibly hear doing
    /// so. Measured: a crowd fight peaked at 189 playing sources with 84 being stolen.
    ///
    /// SHARED because three components needed it and the third was about to be a
    /// copy-paste. Three slightly different answers to one question is how they drift.
    /// </summary>
    public static class AudioCull
    {
        static AudioListener cached;

        /// <summary>
        /// The active listener, cached. Re-found when it goes null, which is what makes this
        /// survive a dungeon regenerate: the player rig is destroyed and rebuilt on F1 and on
        /// every depth change, taking its listener with it.
        /// </summary>
        public static AudioListener Listener
        {
            get
            {
                if (cached == null) cached = Object.FindFirstObjectByType<AudioListener>();
                return cached;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => cached = null;

        /// <summary>
        /// True if `at` is further than `distance` from the listener, i.e. don't bother.
        /// A distance of 0 or less disables culling. Squared comparison - this runs per
        /// sound, and a sqrt for a threshold test is pure waste.
        /// </summary>
        public static bool TooFar(Vector3 at, float distance)
        {
            if (distance <= 0f) return false;
            var l = Listener;
            if (l == null) return false;      // no listener yet: play, don't silently mute
            return (l.transform.position - at).sqrMagnitude > distance * distance;
        }

        public static bool TooFar(Transform at, float distance) =>
            at != null && TooFar(at.position, distance);
    }

    /// <summary>
    /// AudioSource.priority values, as a scheme rather than scattered magic numbers.
    ///
    /// UNITY'S SCALE IS INVERTED: 0 is never dropped, 256 is dropped first, default 128.
    /// Every source in this project sat at that default until now, which meant that when
    /// the voice budget ran out Unity chose purely by audibility - so a barely-audible
    /// grunt across the dungeon could steal the voice from a sword hit two metres from
    /// your face. That is precisely the "combat sounds cut out during a fight" failure.
    ///
    /// The ordering is by WHAT IS LOST IF IT GOES, not by how loud it is:
    ///   - a missing ambient bed makes the world sound broken
    ///   - a missing hit on YOUR swing reads as the game failing to register it
    ///   - a missing footstep from one goblin in a crowd of twenty is unnoticeable
    /// </summary>
    public static class AudioPriority
    {
        /// <summary>Music and ambient beds: continuous, and their absence is a hole.</summary>
        public const int Bed = 32;
        /// <summary>
        /// Positional ambience: the per-torch crackle. Below a bed because a bed's absence is
        /// a hole in the whole world while one torch going quiet is local, but ABOVE player
        /// actions because it is already pooled down to a handful of voices that are, by
        /// construction, the nearest audible ones — if one of those is stolen you lose the
        /// fire you are standing next to.
        /// </summary>
        public const int AmbientPoint = 48;
        /// <summary>The player's own weapon, bow, guard. Feedback on your own input.</summary>
        public const int PlayerAction = 64;
        /// <summary>Impacts and physics - the world reacting to something.</summary>
        public const int WorldImpact = 96;
        /// <summary>NPC voices: hurt, death, effort. Character, but survivable losses.</summary>
        public const int NpcVoice = 140;
        /// <summary>NPC weapon whooshes. Numerous in a crowd, individually cheap to lose.</summary>
        public const int NpcCombat = 170;
        /// <summary>NPC footsteps. The most numerous sound in the game by a wide margin.</summary>
        public const int NpcFootstep = 200;
    }
}
