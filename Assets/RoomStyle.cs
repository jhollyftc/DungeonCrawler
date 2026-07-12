using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Per-room-type visual styling. Starts with torch lighting (color,
    /// intensity, spacing overrides) so room type reads as atmosphere before
    /// any props exist; designed to grow to hold prop palettes and ambient
    /// settings later. One asset styles the whole dungeon's room vocabulary.
    /// </summary>
    [CreateAssetMenu(fileName = "RoomStyle", menuName = "Dungeon/Room Style")]
    public class RoomStyle : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public RoomType type;
            [ColorUsage(false, true)] public Color torchColor; // HDR so it can be punchy
            [Tooltip("Multiplier on the base torch intensity for this room type.")]
            public float intensityScale;
            [Tooltip("Multiplier on torch spacing (< 1 = more torches / brighter, > 1 = darker). 1 = default.")]
            public float spacingScale;
        }

        [Tooltip("Color used for corridor torches and any room type without an entry.")]
        [ColorUsage(false, true)] public Color defaultTorchColor = new Color(1f, 0.72f, 0.42f);
        public float defaultIntensityScale = 1f;
        public float defaultSpacingScale = 1f;

        public List<Entry> entries = new List<Entry>
        {
            new Entry { type = RoomType.Start,      torchColor = new Color(0.85f, 0.95f, 1f),  intensityScale = 1.1f, spacingScale = 0.8f },  // cool, bright, welcoming
            new Entry { type = RoomType.Exit,       torchColor = new Color(0.6f, 0.5f, 1f),     intensityScale = 1.2f, spacingScale = 0.8f },  // portal violet
            new Entry { type = RoomType.ThroneRoom, torchColor = new Color(1f, 0.8f, 0.3f),     intensityScale = 1.3f, spacingScale = 0.7f },  // grand gold
            new Entry { type = RoomType.Treasury,   torchColor = new Color(1f, 0.85f, 0.2f),    intensityScale = 1.2f, spacingScale = 0.6f },  // rich gold glow
            new Entry { type = RoomType.Merchant,   torchColor = new Color(1f, 0.85f, 0.55f),   intensityScale = 1.1f, spacingScale = 0.7f },  // warm, inviting shop
            new Entry { type = RoomType.Barracks,   torchColor = new Color(1f, 0.6f, 0.35f),    intensityScale = 1f,   spacingScale = 0.9f },  // utilitarian
            new Entry { type = RoomType.Armory,     torchColor = new Color(0.95f, 0.55f, 0.4f), intensityScale = 0.95f,spacingScale = 1f },
            new Entry { type = RoomType.Kitchen,    torchColor = new Color(1f, 0.5f, 0.25f),    intensityScale = 1.1f, spacingScale = 0.8f },  // hearth fire
            new Entry { type = RoomType.Pantry,     torchColor = new Color(1f, 0.65f, 0.4f),    intensityScale = 0.9f, spacingScale = 1f },
            new Entry { type = RoomType.Library,    torchColor = new Color(0.95f, 0.85f, 0.6f), intensityScale = 0.9f, spacingScale = 1f },   // dim scholarly
            new Entry { type = RoomType.Study,      torchColor = new Color(0.9f, 0.8f, 0.6f),   intensityScale = 0.85f,spacingScale = 1.1f },
            new Entry { type = RoomType.Shrine,     torchColor = new Color(0.5f, 0.75f, 1f),    intensityScale = 0.9f, spacingScale = 1.2f },  // cold, sacred, sparse
            new Entry { type = RoomType.Reliquary,  torchColor = new Color(0.6f, 0.85f, 1f),    intensityScale = 1f,   spacingScale = 1f },
        };

        Dictionary<RoomType, Entry> lookup;

        public Entry For(RoomType type)
        {
            if (lookup == null)
            {
                lookup = new Dictionary<RoomType, Entry>();
                foreach (var e in entries) lookup[e.type] = e;
            }
            if (lookup.TryGetValue(type, out var entry) && entry.intensityScale > 0f)
                return entry;
            return new Entry
            {
                type = type,
                torchColor = defaultTorchColor,
                intensityScale = defaultIntensityScale,
                spacingScale = defaultSpacingScale,
            };
        }

        // ---------------- Walls ----------------

        /// <summary>Vertical band of a wall cell within its room. Bottom is the
        /// course touching the floor (drains, skirting), Top meets the ceiling
        /// (cornices), Middle is everything between. Single-story rooms and
        /// hallways count as Bottom.</summary>
        public enum WallBand { Bottom, Middle, Top }

        [System.Serializable]
        public class WallAsset
        {
            public GameObject prefab;
            [Tooltip("Which vertical bands this wall may appear in. A floor drain: bottom only. A plain wall: all three.")]
            public bool bottom = true;
            public bool middle = true;
            public bool top = true;
            [Tooltip("Max placements of this asset per room. 0 = unlimited. A fireplace: 1. A banner wall: maybe 2.")]
            public int maxPerRoom = 0;
            [Tooltip("Floor props (snapToWall scatter/features) may sit against this wall. Turn OFF for walls whose face must stay visible — recessed niches, murals, wall fountains.")]
            public bool allowPropsInFront = true;
            [Tooltip("Torches may mount on this wall. Turn OFF for walls with their own light sources or busy relief where a sconce reads wrong.")]
            public bool allowTorch = true;

            public bool Allows(WallBand b) =>
                (b == WallBand.Bottom && bottom) ||
                (b == WallBand.Middle && middle) ||
                (b == WallBand.Top && top);
        }

        [System.Serializable]
        public class WallSet
        {
            public RoomType type;
            public List<WallAsset> walls = new List<WallAsset>();
        }

        [Header("Walls (empty = kit's generic walls)")]
        [Tooltip("Per-room-type wall assets with band eligibility. Types without a set fall back to the kit's generic walls.")]
        public List<WallSet> roomWalls = new List<WallSet>();
        [Tooltip("Wall assets for hallways (band is always Bottom).")]
        public List<WallAsset> hallwayWalls = new List<WallAsset>();
        [Tooltip("Wall assets for prison closets (band is always Bottom). Empty = kit's generic walls.")]
        public List<WallAsset> prisonWalls = new List<WallAsset>();

        Dictionary<(RoomType, WallBand), List<WallAsset>> wallCache;
        GameObject[] hallwayWallCache;
        GameObject[] prisonWallCache;

        /// <summary>Band-eligible wall assets for a room type — STRICT: a band
        /// with no eligible assets returns null (kit generic walls fill in).
        /// Never borrows assets from other bands; a bottom-only drain must not
        /// float at mid-height because the middle band happened to be empty.</summary>
        public List<WallAsset> WallAssetsFor(RoomType type, WallBand band)
        {
            wallCache ??= new Dictionary<(RoomType, WallBand), List<WallAsset>>();
            if (wallCache.TryGetValue((type, band), out var cached)) return cached;

            List<WallAsset> result = null;
            foreach (var set in roomWalls)
            {
                if (set.type != type) continue;
                var filtered = new List<WallAsset>();
                foreach (var w in set.walls)
                    if (w.prefab != null && w.Allows(band)) filtered.Add(w);
                if (filtered.Count > 0) result = filtered;
                break;
            }
            wallCache[(type, band)] = result;
            return result;
        }

        /// <summary>The raw wall-asset list for a room type (unfiltered), or
        /// null. Used by the reservation pre-pass, which deals each capped
        /// asset ONCE across the union of its allowed bands.</summary>
        public List<WallAsset> WallSetFor(RoomType type)
        {
            foreach (var set in roomWalls)
                if (set.type == type)
                    return set.walls;
            return null;
        }

        /// <summary>Hallway wall prefabs, or null to use the kit's generic walls.</summary>
        public GameObject[] HallwayWalls()
        {
            if (hallwayWallCache != null) return hallwayWallCache.Length > 0 ? hallwayWallCache : null;
            var list = new List<GameObject>();
            foreach (var w in hallwayWalls)
                if (w.prefab != null && w.Allows(WallBand.Bottom)) list.Add(w.prefab);
            hallwayWallCache = list.ToArray();
            return hallwayWallCache.Length > 0 ? hallwayWallCache : null;
        }

        /// <summary>Prison closet wall prefabs, or null to use the kit's generic walls.</summary>
        public GameObject[] PrisonWalls()
        {
            if (prisonWallCache != null) return prisonWallCache.Length > 0 ? prisonWallCache : null;
            var list = new List<GameObject>();
            foreach (var w in prisonWalls)
                if (w.prefab != null && w.Allows(WallBand.Bottom)) list.Add(w.prefab);
            prisonWallCache = list.ToArray();
            return prisonWallCache.Length > 0 ? prisonWallCache : null;
        }

        Dictionary<GameObject, (bool props, bool torch)> wallFlagCache;

        /// <summary>Placement restrictions for a wall prefab (allowPropsInFront /
        /// allowTorch), merged most-restrictive if the prefab appears in several
        /// WallAssets. Unknown prefabs (kit generics) allow everything.</summary>
        public void WallFlagsFor(GameObject prefab, out bool allowProps, out bool allowTorch)
        {
            if (wallFlagCache == null)
            {
                wallFlagCache = new Dictionary<GameObject, (bool, bool)>();
                void Add(List<WallAsset> list)
                {
                    if (list == null) return;
                    foreach (var w in list)
                    {
                        if (w.prefab == null) continue;
                        if (wallFlagCache.TryGetValue(w.prefab, out var f))
                            wallFlagCache[w.prefab] = (f.props && w.allowPropsInFront, f.torch && w.allowTorch);
                        else
                            wallFlagCache[w.prefab] = (w.allowPropsInFront, w.allowTorch);
                    }
                }
                foreach (var set in roomWalls) Add(set.walls);
                Add(hallwayWalls);
                Add(prisonWalls);
            }
            if (wallFlagCache.TryGetValue(prefab, out var flags))
            {
                allowProps = flags.props;
                allowTorch = flags.torch;
            }
            else
            {
                allowProps = true;
                allowTorch = true;
            }
        }

        /// <summary>Clear caches after inspector edits (called on regenerate).</summary>
        public void InvalidateWallCache()
        {
            wallCache = null;
            hallwayWallCache = null;
            prisonWallCache = null;
            wallFlagCache = null;
            lookup = null;
        }

        // ---------------- Openings (archways & doors) ----------------

        [System.Serializable]
        public class OpeningSet
        {
            public RoomType type;
            [Tooltip("Archways for openings into rooms of this type. Empty = kit's generic archways.")]
            public GameObject[] archwayPrefabs;
            [Tooltip("Doors for entrances into rooms of this type (incl. satellite closet doors — a Treasury entry styles the treasury door). Empty = kit's generic doors.")]
            public GameObject[] doorPrefabs;
            [Tooltip("Outer corner posts (wall corners, arch piers) for this type. Empty = kit's generic.")]
            public GameObject[] outerPillarPrefabs;
            [Tooltip("Inner (concave) corner posts for this type. Empty = kit's generic.")]
            public GameObject[] innerPillarPrefabs;
        }

        [Header("Openings (empty = kit's generic arch/door)")]
        [Tooltip("Per-room-type archway and door prefabs, matching each type's wall set.")]
        public List<OpeningSet> roomOpenings = new List<OpeningSet>();

        /// <summary>Archway prefabs for openings into this room type, or null for the kit's.</summary>
        public GameObject[] ArchwaysFor(RoomType type)
        {
            foreach (var s in roomOpenings)
                if (s.type == type)
                    return (s.archwayPrefabs != null && s.archwayPrefabs.Length > 0) ? s.archwayPrefabs : null;
            return null;
        }

        /// <summary>Door prefabs for entrances into this room type, or null for the kit's.</summary>
        public GameObject[] DoorsFor(RoomType type)
        {
            foreach (var s in roomOpenings)
                if (s.type == type)
                    return (s.doorPrefabs != null && s.doorPrefabs.Length > 0) ? s.doorPrefabs : null;
            return null;
        }

        /// <summary>Outer corner pillars for this type, or null for the kit's.</summary>
        public GameObject[] OuterPillarsFor(RoomType type)
        {
            foreach (var s in roomOpenings)
                if (s.type == type)
                    return (s.outerPillarPrefabs != null && s.outerPillarPrefabs.Length > 0) ? s.outerPillarPrefabs : null;
            return null;
        }

        /// <summary>Inner corner pillars for this type, or null for the kit's.</summary>
        public GameObject[] InnerPillarsFor(RoomType type)
        {
            foreach (var s in roomOpenings)
                if (s.type == type)
                    return (s.innerPillarPrefabs != null && s.innerPillarPrefabs.Length > 0) ? s.innerPillarPrefabs : null;
            return null;
        }

        /// <summary>
        /// Priority ladder for edges touching multiple rooms (a pillar at a
        /// throne↔hallway corner uses the throne pillar). Higher wins.
        /// Landmarks > treasury > satellites/merchant tier > categories > generic.
        /// </summary>
        public static int Specialness(RoomType t) => t switch
        {
            RoomType.Start => 5,
            RoomType.Exit => 5,
            RoomType.ThroneRoom => 5,
            RoomType.Treasury => 4,
            RoomType.Merchant => 3,
            RoomType.Armory => 2,
            RoomType.Pantry => 2,
            RoomType.Study => 2,
            RoomType.Reliquary => 2,
            RoomType.ChestVault => 2,
            RoomType.Barracks => 1,
            RoomType.Kitchen => 1,
            RoomType.Library => 1,
            RoomType.Shrine => 1,
            _ => 0,
        };

        // ---------------- Props ----------------

        [System.Serializable]
        public class PropSetEntry
        {
            public RoomType type;
            public PropSet props;
        }

        [Header("Props (per room type; sets are shareable assets)")]
        public List<PropSetEntry> roomProps = new List<PropSetEntry>();

        /// <summary>Prop set for a room type, or null (no props).</summary>
        public PropSet PropsFor(RoomType type)
        {
            foreach (var e in roomProps)
                if (e.type == type)
                    return e.props;
            return null;
        }
    }
}