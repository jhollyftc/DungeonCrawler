using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The one place that says what each SurfaceType looks and sounds like when
    /// struck — the "surface material" table (flesh: wet slash + blood; stone:
    /// spark + clang; wood: crack + debris). Every attacker (melee, thrown props,
    /// future arrows) reads from a shared instance via SurfaceImpact, so impacts
    /// stay consistent and there's a single asset to author.
    ///
    /// A ScriptableObject, matching the project's authoring convention (RoomStyle,
    /// DepthProfile). Room to grow per entry: decals, per-surface hitstop scale,
    /// damage multiplier, weapon recoil — the doc's #7 fields — as later phases need.
    /// </summary>
    [CreateAssetMenu(fileName = "SurfaceLibrary", menuName = "Dungeon/Surface Library")]
    public class SurfaceLibrary : ScriptableObject
    {
        [Tooltip("Mixer group for POSITIONED impact one-shots — the hits with no AudioSource of their own (a sword striking a wall, debris from a shattered crate). Route to SFX/Physics.\n\nIt lives here rather than on each hit source because those callers have no source to carry a group on; the library is the one thing every impact already goes through. Hits that DO pass an sfxSource keep that source's own routing.\n\nEmpty = straight to Master, i.e. today's behaviour.")]
        public UnityEngine.Audio.AudioMixerGroup impactMixerGroup;

        [Serializable]
        public class Entry
        {
            public SurfaceType surface;
            [Tooltip("Spawned at the hit point in WORLD space. A VFX-Graph/particle burst — sparks, blood, wood chips.")]
            public GameObject vfx;
            [Tooltip("Auto-destroy the spawned VFX after this many seconds. 0 = self-terminating VFX cleans up itself.")]
            public float vfxLifetime = 2f;
            [Tooltip("Impact sounds for this surface. Several = free variation.")]
            public AudioClip[] sfx;
            [Range(0f, 1f)] public float volume = 0.9f;
            public Vector2 pitchRange = new Vector2(0.92f, 1.08f);
        }

        [Tooltip("One entry per surface. A surface with no entry uses Default Entry.")]
        public List<Entry> entries = new List<Entry>();
        [Tooltip("Fallback for a surface not listed above (e.g. a generic dust puff for stone).")]
        public Entry defaultEntry;

        public Entry For(SurfaceType surface)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].surface == surface) return entries[i];
            return defaultEntry;
        }
    }
}
