using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Smooth deterministic value noise over the grid — the field that lets kit variants
    /// CLUSTER instead of speckling.
    ///
    /// WHY THIS EXISTS AT ALL. Every kit pick in the project is `Hash(cell) % variants`, which
    /// is white noise: statistically even, spatially structureless. Weighting that changes how
    /// RARE a variant is and can never change how it is DISTRIBUTED, so a 1-in-10 cracked wall
    /// gives one damaged face here and another three cells away, forever. What reads as "this
    /// part of the room is falling apart" is several ADJACENT faces sharing a state, and no
    /// amount of per-face probability produces that. Sampling a smooth field at the face's
    /// position does, because neighbouring faces sample nearly the same value.
    ///
    /// NOT `Mathf.PerlinNoise`: Unity documents it as free to differ between platforms and
    /// versions, which would break "same (seed, depth) → same dungeon" (golden rule 4) in the
    /// one way that is invisible until someone compares two machines. This is built from
    /// `DungeonKitPlacer.Hash`, the same integer hash every placement pass already trusts.
    ///
    /// Value noise rather than gradient/Perlin deliberately: it is a few lattice hashes and two
    /// lerps, the axis-aligned artifacts that make Perlin worth the extra cost are invisible at
    /// the scales used here (a lattice cell spans several dungeon cells), and it needs no
    /// gradient table to keep deterministic.
    /// </summary>
    public static class ValueNoise
    {
        /// <summary>Lattice value in 0..1 at integer coordinates.</summary>
        static float Lattice(int x, int z, int salt) =>
            DungeonKitPlacer.Hash(new Vector3Int(x, 0, z), salt) / (float)0x7fffffff;

        /// <summary>
        /// Smooth 0..1 field at (x, z) in LATTICE units — divide world/cell coordinates by the
        /// desired feature size before calling. Bilinear between four lattice hashes, with a
        /// smoothstep on the interpolants so the field has no visible creases at cell edges
        /// (raw linear interpolation leaves a grid you can see once it drives a material).
        /// </summary>
        public static float Sample(float x, float z, int salt)
        {
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fz = z - z0;

            // Smoothstep the interpolants, not the result — that is what removes the creases.
            float u = fx * fx * (3f - 2f * fx);
            float v = fz * fz * (3f - 2f * fz);

            float n00 = Lattice(x0, z0, salt);
            float n10 = Lattice(x0 + 1, z0, salt);
            float n01 = Lattice(x0, z0 + 1, salt);
            float n11 = Lattice(x0 + 1, z0 + 1, salt);

            return Mathf.Lerp(Mathf.Lerp(n00, n10, u), Mathf.Lerp(n01, n11, u), v);
        }

        /// <summary>
        /// The field sampled for a dungeon CELL. `scale` is the feature size in cells — how wide
        /// a patch tends to be — so 6 means damage clusters roughly six cells across. Returns
        /// 0.5 (a neutral middle) when disabled, so every full-range asset stays eligible and
        /// nothing changes.
        ///
        /// EACH FLOOR GETS ITS OWN FIELD (y folded into the salt) rather than one column of
        /// noise running up through the dungeon. A shared vertical field would make a two-storey
        /// room's damage align into a stripe up the wall, which reads as deliberate masonry
        /// rather than decay. Change the salt term here if a vertical run is ever wanted.
        /// </summary>
        public static float ForCell(Vector3Int cell, float scale, int salt)
        {
            if (scale <= 0f) return 0.5f;
            return Sample(cell.x / scale, cell.z / scale, salt + cell.y * 7919);
        }
    }
}
