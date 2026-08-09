using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Picks the clip a footstep should play, from what is actually UNDERFOOT.
    ///
    /// Shared by PlayerFootsteps and NpcFootsteps because they were already the same code
    /// twice — identical no-repeat pick, identical pitch jitter — and a surface lookup in
    /// both would have made it three times. The two components keep their own CADENCE (the
    /// player's stride is distance-based off a CharacterController, the NPC's off
    /// NpcLocomotion.CurrentSpeed) and differ in nothing else that matters here.
    ///
    /// FALLBACK IS THE WHOLE REASON THIS IS SEPARATE FROM THE PROBE. A surface with no
    /// authored footsteps falls back to the component's own clips, so an unfilled
    /// SurfaceLibrary changes nothing and the dungeon keeps its existing stone steps. That
    /// matches the fallback philosophy everywhere else in the project: incomplete authoring
    /// degrades, it never goes silent.
    /// </summary>
    [System.Serializable]
    public class FootstepSurface
    {
        [Tooltip("Surface clips come from here. Empty = always use the component's own clips, i.e. exactly the pre-surface behaviour.")]
        public SurfaceLibrary library;

        [Tooltip("What the downward probe may hit. MUST exclude the NPC layer — a goblin in a crowd would otherwise sample the surface of whoever it is standing beside — and the Viewmodel layer. Default (-1) hits everything, which is wrong the moment there are two NPCs; set it.")]
        public LayerMask probeMask = ~0;

        [Tooltip("Start the probe this far ABOVE the transform. A capsule rests fractionally inside the floor, and a ray starting inside a collider does not report it.")]
        public float probeUp = 0.5f;
        [Tooltip("How far below the feet to look. Long enough to cross the capsule's skin width and any small step, short enough not to find the floor below a ledge.")]
        public float probeDown = 1.2f;

        [Tooltip("When the probe finds no Surface component, ask the GENERATOR what this cell is and use that space's AudioProfile.floorSurface. This is what makes a pit floor or a prison floor sound different, since the whole dungeon shell is ONE collider and cannot carry per-cell Surface components. Off = probe only, i.e. everything untagged is fallbackSurface.")]
        public bool useCellFallback = true;

        [Tooltip("Last resort: the probe found nothing tagged AND the cell lookup could not answer (no generator, or no AudioProfile for that space). Stone is the untagged world, which is what a dungeon mostly is.")]
        public SurfaceType fallbackSurface = SurfaceType.Stone;

        [Tooltip("Log every step's resolved surface. The fastest way to find a bridge or water plane that is missing its Surface component.")]
        public bool debugSurface = false;

        int lastIndex = -1;

        /// <summary>The surface resolved on the most recent step — for debug overlays and VFX.</summary>
        public SurfaceType LastSurface { get; private set; } = SurfaceType.Stone;

        /// <summary>
        /// Resolve the surface under `feet` and choose a clip for it. Returns false when there
        /// is nothing to play, in which case `clip` is null and the caller should fall back to
        /// its own authored set.
        /// </summary>
        public bool TryPick(Transform feet, out AudioClip clip, out float volume, out float pitch)
        {
            clip = null; volume = 1f; pitch = 1f;

            SurfaceType surface = Resolve(feet);
            LastSurface = surface;

            // The log must say WHICH SOURCE WON, not just what was underfoot. "Stepped on Stone"
            // is true whether the library answered or the component's own clips did, so on its
            // own it cannot distinguish "surfaces are working" from "surfaces are silently
            // falling back" — and those look and sound identical until you author a second
            // surface and wonder why nothing changed.
            if (library == null) return Miss(feet, surface, "no SurfaceLibrary assigned");

            SurfaceLibrary.Entry entry = library.For(surface);
            if (entry == null) return Miss(feet, surface, "no library Entry for this surface and no Default Entry");
            if (entry.footsteps == null || entry.footsteps.Length == 0)
                return Miss(feet, surface, "library Entry has no footstep clips");

            // Random NO-REPEAT pick, house convention — the same one both callers had.
            int i = 0;
            if (entry.footsteps.Length > 1)
            {
                do { i = Random.Range(0, entry.footsteps.Length); }
                while (i == lastIndex);
            }
            lastIndex = i;

            clip = entry.footsteps[i];
            if (clip == null) return Miss(feet, surface, "the chosen library clip slot is empty");

            volume = entry.footstepVolume;
            pitch = Random.Range(entry.footstepPitchRange.x, entry.footstepPitchRange.y);

            if (debugSurface)
                Debug.Log($"[FootstepSurface] {Name(feet)} on {surface} -> LIBRARY '{clip.name}' (vol {volume:0.00})");
            return true;
        }

        /// <summary>
        /// What is underfoot, in TWO LAYERS, because one alone cannot cover the dungeon.
        ///
        /// 1. THE PROBE, for anything with its own collider GameObject — stairs, bridges,
        ///    doors, columns, props. Per-CELL by construction, which is what makes "you just
        ///    stepped onto a wooden bridge over a pit" work with no extra authoring.
        /// 2. THE CELL, for everything else. `DungeonMesher` emits the entire shell as ONE
        ///    GameObject with ONE MeshCollider, so ordinary floors are physically incapable
        ///    of carrying a per-cell Surface — a Surface authored on a floor prefab looks
        ///    right and does nothing. The generator already knows what that cell IS, so the
        ///    space's `AudioProfile.floorSurface` answers instead.
        ///
        /// The original design was probe-only, on the reasoning that surface is a property of
        /// the CELL and not of the room type. That reasoning was right and is preserved: the
        /// probe still WINS wherever it can answer, so the per-space value is only ever the
        /// floor beneath everything else.
        ///
        /// Resolved from the FEET's OWN position, not the player's, so an NPC crossing a
        /// bridge sounds like wood while the player standing in the room does not.
        /// </summary>
        SurfaceType Resolve(Transform feet)
        {
            if (Surface.TryBelow(feet, probeUp, probeDown, probeMask, out SurfaceType tagged))
                return tagged;

            if (!useCellFallback || feet == null) return fallbackSurface;

            DungeonVisualizer v = Vis();
            if (v == null) return fallbackSurface;

            AudioSpace space = AudioSpace.ResolveAt(v, AudioSpace.CellOf(v, feet.position));
            if (space.Profile == null) return fallbackSurface;
            return space.Profile.floorSurface;
        }

        // Cached like AudioCull's listener, and re-found the same way: the dungeon is rebuilt
        // on F1/PgUp and the portal, so a held reference goes stale.
        static DungeonVisualizer cachedVis;
        static DungeonVisualizer Vis()
        {
            if (cachedVis == null) cachedVis = Object.FindFirstObjectByType<DungeonVisualizer>();
            return cachedVis;
        }

        /// <summary>Log why the library declined and fall back. Always returns false.</summary>
        bool Miss(Transform feet, SurfaceType surface, string reason)
        {
            if (debugSurface)
                Debug.Log($"[FootstepSurface] {Name(feet)} on {surface} -> FALLBACK clips ({reason})");
            return false;
        }

        static string Name(Transform t) => t != null ? t.name : "?";
    }
}
