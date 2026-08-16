using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>One placed region: a definition, a site in the grid, and how far it reaches.</summary>
    public struct RegionSite
    {
        public RegionDefinition Definition;
        /// <summary>Grid cell at the centre of the region's influence.</summary>
        public Vector3Int Cell;
        /// <summary>Distance in cells at which influence reaches zero.</summary>
        public float Radius;
    }

    /// <summary>
    /// Areas of influence over the dungeon, biasing which props appear where.
    ///
    /// IT IS NOT A VORONOI PARTITION, AND THAT IS THE LOAD-BEARING DECISION. Voronoi's defining
    /// property is that every point belongs to some site; normalising influence across sites has
    /// the same effect. EITHER ONE DESTROYS THE VANILLA BASELINE — and "the dungeon starts
    /// vanilla and gains regions with depth" is the whole design. Four regions are also nowhere
    /// near enough to cover a grid this size, so a partition would stretch each one across
    /// territory it has no business owning.
    ///
    /// So: ABSOLUTE RADIAL INFLUENCE, NEVER NORMALISED. Far from every site, every influence is
    /// zero and the dungeon is exactly what it was before this existed. Near a site one region
    /// saturates. Where two overlap BOTH contribute, which is the "goblins AND vines" case
    /// arriving free rather than being authored.
    ///
    /// PLACEMENT-TIME ONLY. Nothing here draws from the generator's sequential `rng` — sites
    /// come from their own <see cref="System.Random"/> seeded off the dungeon seed, and the
    /// field is consumed by the prop placers, which run on `HashStream`. So regions cannot
    /// perturb rooms, corridors, prisons, alcoves or sewers, and a run can be re-regioned all
    /// day without reshuffling a single layout. Do not let regions influence GENERATION; that
    /// is the change that buys the whole determinism problem back (golden rule 4).
    /// </summary>
    public class RegionField
    {
        /// <summary>Salt mixed into the dungeon seed so region sites get their own stream.</summary>
        public const int SeedSalt = 0x5E61;

        public readonly List<RegionSite> Sites = new List<RegionSite>();

        /// <summary>
        /// Vertical exaggeration in the distance metric.
        ///
        /// WITHOUT IT YOU WRITE 3D CODE AND GET 2D BEHAVIOUR. `gridHeight` is ~12 against ~40
        /// cells of floor, so under isotropic distance a site at y=6 influences y=0 and y=11
        /// near-equally and every region becomes a full-height column. At 3, twelve storeys are
        /// "as far apart" as ~36 cells of floor, which is where vertical variation starts to
        /// read — and a staircase becomes a transition between regions rather than a move
        /// within one.
        /// </summary>
        public float YScale = 3f;

        public bool Any => Sites.Count > 0;

        /// <summary>
        /// How strongly region <paramref name="i"/> reaches this cell, 0..1.
        ///
        /// Not normalised against the other regions — see the class summary. Zero beyond the
        /// radius is what leaves most of the dungeon vanilla.
        /// </summary>
        public float Influence(int i, Vector3Int cell)
        {
            if (i < 0 || i >= Sites.Count) return 0f;
            RegionSite s = Sites[i];
            if (s.Radius <= 0f) return 0f;

            float dx = cell.x - s.Cell.x;
            float dy = (cell.y - s.Cell.y) * YScale;
            float dz = cell.z - s.Cell.z;
            float t = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) / s.Radius;
            if (t >= 1f) return 0f;

            float power = s.Definition != null ? s.Definition.falloffPower : 2f;
            float strength = s.Definition != null ? s.Definition.strength : 1f;
            return Mathf.Clamp(Mathf.Pow(1f - t, power) * strength, 0f, 4f);
        }

        /// <summary>
        /// Place the region sites for a run.
        ///
        /// BEST-CANDIDATE SAMPLING, because with four sites clumping is not a cosmetic problem —
        /// two landing together wastes half the run's content. Each site generates a handful of
        /// candidates and keeps the one farthest from those already placed. Deterministic, no
        /// relaxation loop.
        ///
        /// CANDIDATES ARE ROOM CENTRES, not arbitrary grid points, so every region has a real
        /// space at its core rather than centring in solid rock with only its fringes touching
        /// anything the player can walk through.
        /// </summary>
        public void Place(List<RegionSite> chosen, IReadOnlyList<Room> rooms, System.Random rng,
                          int candidatesPerSite = 8)
        {
            Sites.Clear();
            if (chosen == null || chosen.Count == 0 || rooms == null || rooms.Count == 0) return;

            foreach (var want in chosen)
            {
                Vector3Int best = default;
                float bestScore = -1f;
                for (int c = 0; c < candidatesPerSite; c++)
                {
                    Room r = rooms[rng.Next(0, rooms.Count)];
                    Vector3Int cand = r.InteriorFloorCell;

                    // Score = distance to the NEAREST already-placed site. Maximising it spreads
                    // them; the first site has nothing to avoid and takes the first candidate.
                    float score = float.MaxValue;
                    foreach (var s in Sites)
                    {
                        float dx = cand.x - s.Cell.x;
                        float dy = (cand.y - s.Cell.y) * YScale;
                        float dz = cand.z - s.Cell.z;
                        score = Mathf.Min(score, dx * dx + dy * dy + dz * dz);
                    }
                    if (Sites.Count == 0) score = 0f;

                    if (score > bestScore) { bestScore = score; best = cand; }
                }

                var site = want;
                site.Cell = best;
                Sites.Add(site);
            }
        }

        /// <summary>
        /// Append every region's prop entries to <paramref name="ordered"/>, recording each
        /// one's chance multiplier in <paramref name="mult"/>.
        ///
        /// ONE RESOLVER SHARED BY ALL THREE PLACERS, exactly as `PropTint` is — a corridor and
        /// the room it opens into must never disagree about where they are, and two copies of
        /// this logic would eventually drift.
        ///
        /// APPENDED AFTER THE SORT, NEVER MERGED INTO IT. Region entries land at the end of the
        /// list, so every base entry completes all of its draws first and base placement is
        /// bit-identical whether regions exist or not — the property the whole feature is built
        /// to preserve. Sorting them in by rank would interleave their draws with the base
        /// set's and reshuffle the entire dungeon's props the moment a region appeared.
        ///
        /// Known limitation, accepted for phase 1: region entries share the base pass streams,
        /// so adding region A shifts region B's placements. Base is unaffected either way, which
        /// is the part that matters; giving each region its own stream means threading a stream
        /// selector through every anchor branch for a cosmetic gain.
        /// </summary>
        /// <param name="at">Cell the caller resolves at, used only to skip regions that cannot
        /// reach this space at all. NULL appends every region — which is what a corridor pass
        /// wants, since it covers the whole dungeon and has no single position; the per-cell
        /// multiplier then gates each placement individually and yields the gradient.</param>
        public void AppendEntries(Vector3Int? at,
                                  List<PropSet.PropEntry> ordered,
                                  Dictionary<PropSet.PropEntry, int> regionOf)
        {
            if (Sites.Count == 0 || ordered == null || regionOf == null) return;

            for (int i = 0; i < Sites.Count; i++)
            {
                if (at.HasValue && Influence(i, at.Value) <= 0.0001f) continue;

                PropSet set = Sites[i].Definition != null ? Sites[i].Definition.props : null;
                if (set == null || set.entries == null) continue;

                foreach (var e in set.entries)
                {
                    if (e == null) continue;

                    // PHASE 1 IS SCATTER-LIKE ANCHORS ONLY. Feature, guaranteed and
                    // NearPropAsset occupy ranks that run BEFORE scatter in the
                    // most-constrained-first order (§8), so a region entry cannot join them
                    // without being sorted into the list — which is exactly what would shift
                    // every base placement. Skipped rather than silently misplaced.
                    if (e.anchor != PropAnchor.FloorScatter &&
                        e.anchor != PropAnchor.CeilingHung &&
                        e.anchor != PropAnchor.WallMounted) continue;
                    if (e.guaranteed) continue;

                    ordered.Add(e);
                    // NB an entry belongs to exactly one region, because PropEntry objects live
                    // inside one PropSet asset. Assigning the SAME PropSet to two regions would
                    // collide here and the last would win — don't; give each region its own set.
                    regionOf[e] = i;
                }
            }
        }

        /// <summary>
        /// Chance multiplier for an entry at a given cell. 1 for anything the regions did not
        /// add, so base entries are untouched.
        ///
        /// EVALUATED PER CELL, which is what gives corridors their gradient — the frequency
        /// genuinely rises as the player walks toward a region's core. Rooms call this with the
        /// same cell every time, so a room stays uniform without needing a separate path.
        /// </summary>
        public float MultiplierAt(Dictionary<PropSet.PropEntry, int> regionOf, PropSet.PropEntry e, Vector3Int cell)
        {
            if (regionOf == null || e == null || !regionOf.TryGetValue(e, out int i)) return 1f;
            return Influence(i, cell);
        }

        /// <summary>Human-readable summary for the generation log.</summary>
        public string Describe()
        {
            if (Sites.Count == 0) return "no regions (vanilla)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Sites.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{Sites[i].Definition?.Label ?? "?"} @{Sites[i].Cell} r{Sites[i].Radius:0.#}");
            }
            return sb.ToString();
        }
    }
}
