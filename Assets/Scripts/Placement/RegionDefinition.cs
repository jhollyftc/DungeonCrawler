using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// One REGION of the dungeon — an area of influence that biases which props appear near it,
    /// so part of a run reads as infested, another as overgrown, and the same barracks is
    /// goblins-and-vines on one seed and webs-and-spiders on the next.
    ///
    /// NOT A BIOME PARTITION. Regions are scattered influence SOURCES with a radius, never a
    /// Voronoi tessellation and never normalised against each other — see <see cref="RegionField"/>
    /// for why that distinction is what keeps the dungeon vanilla by default.
    ///
    /// ADDITIVE ONLY. A region's <see cref="props"/> are placed ON TOP of whatever the space's
    /// own RoomStyle already places, at a chance scaled by how strongly this region reaches that
    /// space. A region cannot currently SUPPRESS a base prop; if that turns out to matter it
    /// wants a separate suppression list rather than reinterpreting these entries.
    ///
    /// NO NEW ART IS NEEDED to make a region read. Reweighting props that already exist —
    /// cobwebs here, ivy there — plus (later) a palette shift is enough; signature props that
    /// appear ONLY in one region are a per-region polish pass, added whenever the assets exist.
    /// </summary>
    [CreateAssetMenu(menuName = "Dungeon/Region Definition", fileName = "Region_")]
    public class RegionDefinition : ScriptableObject
    {
        [Tooltip("Shown in the generator log and gizmo. Falls back to the asset name when blank.")]
        public string displayName = "";

        [Tooltip("Props this region ADDS, at a chance scaled by its local influence.\n\nPHASE 1 SUPPORTS FloorScatter, CeilingHung and WallMounted ONLY. Feature, guaranteed and NearPropAsset entries are skipped with a warning: those ranks run BEFORE scatter in the most-constrained-first order (§8), and a region entry cannot join them without reordering the whole pass — which would shift every existing placement.")]
        public PropSet props;

        [Tooltip("How far this region reaches, in CELLS, before its influence hits zero. The primary tuning dial, and deliberately per-region: a spider nest is tight and intense, a flooded quarter broad and thin.\n\nRolled per run between these bounds.")]
        public Vector2 radiusRange = new Vector2(10f, 16f);

        [Tooltip("Shape of the falloff from the site outward — influence = (1 - distance/radius) ^ this.\n\nBELOW 1 IS USUALLY WHAT YOU WANT. Low values hold most of the radius near full strength and concentrate the change at the boundary, which is what makes a region read as an AREA. High values do the opposite: a tight hotspot at the very centre with a long weak tail, so most of the volume inside the radius is barely influenced.\n\nInfluence at HALF the radius: 0.5 -> 0.71, 1 (linear) -> 0.50, 2 -> 0.25, 4 -> 0.06.")]
        [Range(0.25f, 4f)] public float falloffPower = 0.8f;

        [Tooltip("Scales the whole influence curve. The one dial for 'this region is subtle' vs 'this region is overwhelming' without re-authoring every entry — chancePerCell is per-entry, this is per-region.\n\n1 means entries place at exactly their authored chance in the core. 2 doubles it, and since chance is 0..1 anything reaching 1 places on EVERY eligible cell.\n\nIt does NOT extend the hard edge: influence still hits zero at the radius whatever this is. It does raise the mid-range, so a strong region feels larger without actually reaching further.")]
        [Range(0f, 4f)] public float strength = 1f;

        [Tooltip("Colour for this region in the DungeonVisualizer gizmo. Cosmetic — it does NOT tint the dungeon (palette shifts are a later phase, deliberately held back until the prop path is proven neutral).")]
        public Color gizmoColor = new Color(0.6f, 0.9f, 0.5f, 1f);

        public string Label => string.IsNullOrWhiteSpace(displayName) ? name : displayName;
    }
}
