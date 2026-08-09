using UnityEngine;
using UnityEngine.Audio;

namespace DungeonGen
{
    /// <summary>
    /// Routing helper — the one place a source is attached to a mixer group.
    ///
    /// WHY A HELPER AND NOT JUST THE INSPECTOR. Eleven components create their AudioSource
    /// at RUNTIME when the prefab hasn't got one (`if (source == null) source =
    /// AddComponent&lt;AudioSource&gt;()`). Assigning Output in the inspector covers only the
    /// authored case, so routing would work on prefabs where somebody happened to place a
    /// source and silently not on the rest — presenting later as "the SFX slider doesn't
    /// affect goblin footsteps, sometimes". Each component now carries a serialized
    /// AudioMixerGroup instead and applies it to whichever source it ends up using, so the
    /// runtime path is covered by construction and there is ONE assignment per prefab
    /// rather than one per AudioSource.
    ///
    /// A NULL GROUP IS NOT AN ERROR: Unity routes an ungrouped source straight to the
    /// mixer's Master, which is exactly today's behaviour. That is what lets this land
    /// before any prefab has been wired up, and what makes a forgotten assignment merely
    /// unrouted rather than silent.
    /// </summary>
    public static class AudioBus
    {
        /// <summary>
        /// Point an OWNED source at a group, leaving it alone when the group is unassigned.
        ///
        /// The null tolerance is deliberate HERE: the source belongs to one component for its
        /// whole life, so an empty field means "don't override whatever the inspector already
        /// set on the AudioSource" rather than "route to Master".
        /// </summary>
        public static void Route(AudioSource src, AudioMixerGroup group)
        {
            if (src != null && group != null) src.outputAudioMixerGroup = group;
        }

        /// <summary>
        /// Point a SHARED/POOLED source at a group, INCLUDING clearing it to null.
        ///
        /// A pooled voice is reused by unrelated callers, so "leave it as it was" means
        /// "inherit whoever used this voice last" — an ambient drip would play through
        /// SFX/Physics because that voice previously served a sword impact, and a caller with
        /// no group assigned would silently keep someone else's routing. The symptom is a mixer
        /// group whose meter never moves while its sounds are plainly audible, which reads as
        /// "the mixer isn't working" rather than as a routing bug.
        ///
        /// Assign every time, unconditionally. Null here genuinely means Master.
        /// </summary>
        public static void Assign(AudioSource src, AudioMixerGroup group)
        {
            if (src != null) src.outputAudioMixerGroup = group;
        }
    }
}
