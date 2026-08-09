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

        [Tooltip("Surface used when the probe hits nothing, or hits something untagged. Stone is the untagged world (§ Surface), which is what a dungeon mostly is.")]
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

            SurfaceType surface = Surface.Below(feet, probeUp, probeDown, probeMask, fallbackSurface);
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
