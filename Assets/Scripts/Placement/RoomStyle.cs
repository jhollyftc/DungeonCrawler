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
            [Tooltip("Name this a feature wall so NearWallAsset props can target it (a fireplace tagged 'Fireplace' → firewood with Host Label 'Fireplace' places beside it). Empty = not a NearWallAsset host. Usually paired with a per-room cap.")]
            public string featureLabel = "";

            [Header("Frequency (uncapped assets only)")]
            [Tooltip("Relative frequency against the other UNCAPPED assets eligible on this face. 3 is three times as likely as 1; every asset at 1 reproduces the old even split. Zero mutes it without deleting it.\n\nIGNORED when Max Per Room is set — a capped asset is dealt by the reservation pre-pass, which already fully determines its count, so frequency would mean nothing.\n\nNote weights are resolved PER BAND: an asset eligible in Bottom and Middle competes separately in each, so its share depends on what else is eligible there rather than on the set as a whole.")]
            public float weight = 1f;
            [Tooltip("Restrict this asset to part of the CLUSTER NOISE range — a smooth field over world position, so neighbouring faces read similar values.\n\n(0,1) = eligible everywhere; this is the default and behaves as if noise didn't exist. (0.6,1) = only where the field is high, which produces PATCHES rather than an even sprinkle: a damaged section of wall, a waterlogged corner, a stretch of soot near a forge.\n\nThis is the difference between 'cracked walls are rare' (frequency) and 'this part of the room is falling apart' (noise). Weight still decides the mix among everything eligible at a face, so the two compose: noise picks the character of a region, weight fills it.\n\nRequires DungeonKit.wallNoiseScale > 0; at 0 the field is disabled and every range is treated as eligible.")]
            public Vector2 noiseRange = new Vector2(0f, 1f);

            /// <summary>Is this asset eligible where the cluster field reads `n`? A full (0,1)
            /// range — the default — is always true, which is what keeps noise opt-in.</summary>
            public bool AllowsNoise(float n) =>
                (noiseRange.x <= 0f && noiseRange.y >= 1f) || (n >= noiseRange.x && n <= noiseRange.y);

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

        // ─────────────────────────────────────────────────────────────────────────────
        // GROUPED BY SPACE, NOT BY SUBSYSTEM. Authoring happens one place at a time —
        // "what does a prison look like" — while the file used to be sorted by kind of
        // thing (all walls, then all floors, then all props), which meant styling a
        // prison took three trips to three different sections and it was easy to fill in
        // its walls and forget its floor. Rooms stay subsystem-shaped because their lists
        // are per-TYPE and inherently so; every other space is one contiguous block.
        //
        // Field ORDER is presentation only — Unity serializes by name, so moving these
        // never loses authored data.
        // ─────────────────────────────────────────────────────────────────────────────

        [Header("═══ ROOMS (per type; empty = the kit's generic piece) ═══")]
        [Tooltip("Per-room-type wall assets with band eligibility. Types without a set fall back to the kit's generic walls.")]
        public List<WallSet> roomWalls = new List<WallSet>();
        [Tooltip("Per-room-type floor and ceiling tiles. A shrine can have its own flagstones and a kitchen its scorched brick without touching the walls.")]
        public List<SurfaceSet> roomSurfaces = new List<SurfaceSet>();
        [Tooltip("Per-room-type archway and door prefabs, matching each type's wall set.")]
        public List<OpeningSet> roomOpenings = new List<OpeningSet>();
        [Tooltip("Per-room-type prop sets (shareable PropSet assets).")]
        public List<PropSetEntry> roomProps = new List<PropSetEntry>();

        [Header("═══ HALLWAYS ═══")]
        [Tooltip("Wall assets for hallways (band is always Bottom).")]
        public List<WallAsset> hallwayWalls = new List<WallAsset>();
        [Tooltip("Floor tiles for corridors. Corridors are most of what the player walks over, so this is the single highest-mileage surface in the dungeon.")]
        public GameObject[] hallwayFloorPrefabs;
        [Tooltip("Ceiling tiles for corridors.")]
        public GameObject[] hallwayCeilingPrefabs;
        [Tooltip("Lintel trim for CORRIDOR stair shafts — a corridor staircase belongs to no room, so it cannot use a per-type set.")]
        public GameObject[] hallwayLintelPrefabs;
        [Tooltip("One global prop set for ALL corridors — debris, cobwebs, roots. Hallways have no zones, so zone/feature fields are ignored; scatter (snapToWall works), ceiling, and wall-mounted anchors apply. Empty = no hallway props.")]
        public PropSet hallwayProps;

        [Header("═══ HALLWAY ALCOVES (per kind) ═══")]
        [Tooltip("Contents for each alcove kind. An alcove is a small recess carved off a corridor, and its KIND is what gives it an identity — a statue nook wants a Feature facing out, a collapsed dig wants scatter, a storage recess wants crates. Alcove cells are ordinary hallway in the grid, so they inherit hallway WALLS/floors/ceilings; only the props are per-kind. A kind with no entry here simply generates as an empty recess.")]
        public List<AlcoveStyleEntry> alcoveStyles = new List<AlcoveStyleEntry>();

        [Header("═══ PRISON CELLS ═══")]
        [Tooltip("Wall assets for prison closets (band is always Bottom). Empty = kit's generic walls.")]
        public List<WallAsset> prisonWalls = new List<WallAsset>();
        [Tooltip("Floor tiles for prison closets.")]
        public GameObject[] prisonFloorPrefabs;
        [Tooltip("Ceiling tiles for prison closets.")]
        public GameObject[] prisonCeilingPrefabs;
        [Tooltip("Contents for prison closets, chosen PER CELL by a weighted roll — so one cell holds a bunk and a bucket, the next a skeleton in chains, the next nothing but straw. A prison is the same generator primitive as an alcove (both come out of RecessFits), so the same anchors work: Direction gives the back/left/right frame, WallSide.Back is the far wall, FeatureFacing.Outward looks out through the bars.\n\nEmpty = bare cells, which is what shipped before this existed.\n\nBLOCKING PROPS ARE SAFE HERE (a dead end severs nothing) EXCEPT in the doorway — the placer reserves the mouth cell and the door's swing arc automatically, so you don't have to author around them.")]
        public List<WeightedPropSet> prisonProps = new List<WeightedPropSet>();

        [Header("═══ ROOM PITS ═══")]
        [Tooltip("Wall assets for the inside of a PIT (band is always Bottom). A chasm exposes what lies BENEATH the dungeon, so raw rock, old foundations and rough stonework read better here than the room's own masonry continuing downward. Empty = the pit inherits its room's walls, which is the current behaviour, so leaving this unauthored changes nothing. Purely cosmetic — pit floors and collision already work regardless.")]
        public List<WallAsset> pitWalls = new List<WallAsset>();
        [Tooltip("Floor tiles for the BOTTOM of a pit — rubble, cracked flags, bedrock. Empty = the pit inherits its room's floor.")]
        public GameObject[] pitFloorPrefabs;
        // No pit CEILING slot on purpose: a pit's top is the hole you fell through, and
        // NeedsSlabBetween suppresses it.

        // ---------------- Floors & ceilings ----------------
        //
        // Deliberately plain prefab arrays rather than WallAssets: a floor has no bands
        // (there's one per cell, not a course), no per-room caps, and nothing mounts on
        // it, so all that machinery would be dead weight. Same fallback philosophy as
        // everything else here — empty means the kit's generic, so a partially authored
        // style still renders.

        [System.Serializable]
        public class SurfaceSet
        {
            public RoomType type;
            [Tooltip("Floor tiles for rooms of this type. Empty = kit's generic floor.")]
            public GameObject[] floorPrefabs;
            [Tooltip("Ceiling tiles for rooms of this type. Empty = kit's generic ceiling.")]
            public GameObject[] ceilingPrefabs;
            [Tooltip("LINTEL / cornice trim for the top edge of a wall inside a STAIR SHAFT, where it meets the ceiling. A stairwell exposes a full storey of wall in one view, so that seam is the most visible wall/ceiling junction in the dungeon and reads as a hard 90 degree edge without a piece to blend it. Empty = kit's generic lintel.")]
            public GameObject[] lintelPrefabs;
        }


        /// <summary>Floor tiles for this room type, or null for the kit's.</summary>
        public GameObject[] FloorsFor(RoomType type)
        {
            foreach (var s in roomSurfaces)
                if (s.type == type)
                    return (s.floorPrefabs != null && s.floorPrefabs.Length > 0) ? s.floorPrefabs : null;
            return null;
        }

        /// <summary>Ceiling tiles for this room type, or null for the kit's.</summary>
        public GameObject[] CeilingsFor(RoomType type)
        {
            foreach (var s in roomSurfaces)
                if (s.type == type)
                    return (s.ceilingPrefabs != null && s.ceilingPrefabs.Length > 0) ? s.ceilingPrefabs : null;
            return null;
        }

        /// <summary>Stair-shaft lintel trim for a room type, or null.</summary>
        public GameObject[] LintelsFor(RoomType type)
        {
            foreach (var s in roomSurfaces)
                if (s.type == type)
                    return (s.lintelPrefabs != null && s.lintelPrefabs.Length > 0) ? s.lintelPrefabs : null;
            return null;
        }

        public GameObject[] HallwayFloors() => Nullable(hallwayFloorPrefabs);
        public GameObject[] HallwayCeilings() => Nullable(hallwayCeilingPrefabs);
        public GameObject[] HallwayLintels() => Nullable(hallwayLintelPrefabs);
        public GameObject[] PrisonFloors() => Nullable(prisonFloorPrefabs);
        public GameObject[] PrisonCeilings() => Nullable(prisonCeilingPrefabs);
        /// <summary>Pit-bottom floor tiles, or null. No pit CEILING accessor exists on purpose:
        /// a pit's top is the hole you fell through, and NeedsSlabBetween suppresses it.</summary>
        public GameObject[] PitFloors() => Nullable(pitFloorPrefabs);

        /// <summary>Empty reads as "unauthored" (null) so callers can `?? kitGeneric`.</summary>
        static GameObject[] Nullable(GameObject[] a) => (a != null && a.Length > 0) ? a : null;

        Dictionary<(RoomType, WallBand), List<WallAsset>> wallCache;
        // WallAsset lists, not prefab arrays: the kit's pick reads each asset's weight and
        // noise range, so the pool has to carry the assets themselves.
        List<WallAsset> hallwayWallCache;
        List<WallAsset> prisonWallCache;
        List<WallAsset> pitWallCache;

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
        public List<WallAsset> HallwayWalls()
        {
            if (hallwayWallCache != null) return hallwayWallCache.Count > 0 ? hallwayWallCache : null;
            var list = new List<WallAsset>();
            int capped = 0;
            foreach (var w in hallwayWalls)
            {
                if (w.prefab == null || !w.Allows(WallBand.Bottom)) continue;
                // maxPerRoom CANNOT BE HONOURED HERE — a corridor network is not a room, so
                // there is no group to count against. Rooms and prisons deal capped assets in a
                // reservation pre-pass; hallways have no equivalent unit. Left in the general
                // pool rather than dropped (dropping would silently delete the asset) but warned,
                // because a cap that quietly does nothing is worse than one that says so.
                if (w.maxPerRoom > 0) capped++;
                list.Add(w);
            }
            if (capped > 0)
                Debug.LogWarning($"[RoomStyle] {capped} hallwayWalls entr(y/ies) set Max Per Room, which is " +
                                 "IGNORED for hallways — a corridor network has no room to count per. " +
                                 "Set it to 0 there, or move the asset to a room or prison wall list.");
            hallwayWallCache = list;
            return hallwayWallCache.Count > 0 ? hallwayWallCache : null;
        }

        /// <summary>Raw prison wall list (capped entries included) for the reservation pre-pass.</summary>
        public List<WallAsset> PrisonWallSet() => prisonWalls;
        /// <summary>Raw pit wall list (capped entries included) for the reservation pre-pass.</summary>
        public List<WallAsset> PitWallSet() => pitWalls;

        /// <summary>Prison closet wall prefabs, or null to use the kit's generic walls.</summary>
        public List<WallAsset> PrisonWalls()
        {
            if (prisonWallCache != null) return prisonWallCache.Count > 0 ? prisonWallCache : null;
            var list = new List<WallAsset>();
            // Capped assets are EXCLUDED from the general pool, exactly as UnlimitedWalls does
            // for rooms: they are dealt once each by the reservation pre-pass, and leaving them
            // in the pool as well is what let a "Max Per Room 1" drain land on face after face.
            foreach (var w in prisonWalls)
                if (w.prefab != null && w.maxPerRoom <= 0 && w.Allows(WallBand.Bottom)) list.Add(w);
            prisonWallCache = list;
            return prisonWallCache.Count > 0 ? prisonWallCache : null;
        }

        /// <summary>Pit-interior wall prefabs, or null to inherit the room's walls.</summary>
        public List<WallAsset> PitWalls()
        {
            if (pitWallCache != null) return pitWallCache.Count > 0 ? pitWallCache : null;
            var list = new List<WallAsset>();
            foreach (var w in pitWalls)
                if (w.prefab != null && w.maxPerRoom <= 0 && w.Allows(WallBand.Bottom)) list.Add(w);
            pitWallCache = list;
            return pitWallCache.Count > 0 ? pitWallCache : null;
        }

        /// <summary>Which wall LIST a face's prefab was picked from. Flags are authored per
        /// WallAsset — i.e. per list — so they must be read back per list too.</summary>
        public enum WallContext { Room, Hallway, Prison, Pit }

        Dictionary<(GameObject prefab, int ctx), (bool props, bool torch)> wallFlagCache;

        // Room contexts are offset past the non-room ones so a room type can never collide with
        // Hallway/Prison/Pit.
        static int ContextKey(WallContext ctx, RoomType type) =>
            ctx == WallContext.Room ? 100 + (int)type : (int)ctx;

        /// <summary>
        /// Placement restrictions for a wall prefab (allowPropsInFront / allowTorch) IN THE
        /// CONTEXT IT WAS PLACED IN. Unknown prefabs (kit generics) allow everything.
        ///
        /// KEYED BY CONTEXT, NOT BY PREFAB ALONE. The same prefab legitimately appears in
        /// several lists with different flags — Wall_Basic_P is a normal hallway wall that
        /// takes torches, and also a pit-interior wall that must not (a lit chasm reads as a
        /// room). Merging those most-restrictive by prefab meant ONE `allowTorch: 0` anywhere
        /// silently disabled torches for that prefab EVERYWHERE, which is how a fully
        /// torch-enabled hallway ended up with no torches at all. Flags are authored per
        /// WallAsset, so they are read back per WallAsset's list.
        ///
        /// Merging still happens WITHIN a list, which is correct: a prefab listed twice in one
        /// context has one effective rule there, and the restrictive reading is the safe one.
        /// </summary>
        public void WallFlagsFor(GameObject prefab, WallContext ctx, RoomType roomType,
                                 out bool allowProps, out bool allowTorch)
        {
            if (wallFlagCache == null)
            {
                wallFlagCache = new Dictionary<(GameObject, int), (bool, bool)>();
                void Add(List<WallAsset> list, int key)
                {
                    if (list == null) return;
                    foreach (var w in list)
                    {
                        if (w.prefab == null) continue;
                        var k = (w.prefab, key);
                        if (wallFlagCache.TryGetValue(k, out var f))
                            wallFlagCache[k] = (f.props && w.allowPropsInFront, f.torch && w.allowTorch);
                        else
                            wallFlagCache[k] = (w.allowPropsInFront, w.allowTorch);
                    }
                }
                foreach (var set in roomWalls) Add(set.walls, ContextKey(WallContext.Room, set.type));
                Add(hallwayWalls, ContextKey(WallContext.Hallway, default));
                Add(prisonWalls, ContextKey(WallContext.Prison, default));
                // Registered here or the per-asset allowTorch / allowPropsInFront flags on pit
                // walls silently do nothing — the same omission §7 warns about for any new
                // wall list.
                Add(pitWalls, ContextKey(WallContext.Pit, default));
            }
            if (wallFlagCache.TryGetValue((prefab, ContextKey(ctx, roomType)), out var flags))
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
            pitWallCache = null;
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
            [Tooltip("Staircases for flights INSIDE a room of this type — a throne room gets its grand stair, a shrine its worn stone steps. Corridor stairs (which belong to no room) always use the kit's generic. Empty = kit's generic.\n\nAUTHORING WARNING: the stair prefab's authored collider IS the walking surface (the greybox's approximate ramp is skipped whenever the kit has stair prefabs, so the two can't disagree). A variant with different STEP geometry therefore changes how the player moves through that room, not just how it looks — keep the tread height and run matching the generic, and vary the dressing.")]
            public GameObject[] stairPrefabs;
        }


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

        /// <summary>Staircase prefabs for flights inside this room type, or null for the kit's.</summary>
        public GameObject[] StairsFor(RoomType type)
        {
            foreach (var s in roomOpenings)
                if (s.type == type)
                    return (s.stairPrefabs != null && s.stairPrefabs.Length > 0) ? s.stairPrefabs : null;
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

        /// <summary>
        /// One candidate contents set for a RECESS (alcove or prison), with a relative weight.
        ///
        /// This is what makes two cells of the same kind look different. Without it every
        /// statue nook in a run is identical, because a single PropSet placed with the same
        /// anchors into the same-shaped pocket produces the same result every time — the
        /// per-cell hash varies WHERE things land, not WHAT is there.
        /// </summary>
        [System.Serializable]
        public class WeightedPropSet
        {
            public PropSet props;
            [Tooltip("Relative weight against the other variants for this kind. Weights need not sum to anything; 2 is simply twice as likely as 1. Zero or negative removes the variant without deleting it, which is how you mute one for testing.")]
            public float weight = 1f;
        }

        /// <summary>One alcove kind's contents. Mirrors PropSetEntry — kind instead of room type.</summary>
        [System.Serializable]
        public class AlcoveStyleEntry
        {
            public AlcoveKind kind;
            [Tooltip("Props for this kind. The Feature anchor works here: the alcove's Direction supplies the same back/left/right frame a room's entrance does, so WallSide.Back is the far face and FeatureFacing.Outward looks out at the corridor.\n\nKept as the single-set slot it always was; it now counts as one weight-1 entry in the variant pool below, so existing authoring keeps working and adding variants EXTENDS the pool rather than replacing it.")]
            public PropSet props;
            [Tooltip("Extra contents sets for this kind, chosen per alcove by a weighted roll. Leave empty for the old behaviour (always `props`). This is what stops every statue nook in a run looking identical.")]
            public List<WeightedPropSet> variants = new List<WeightedPropSet>();
        }


        /// <summary>Prop set for a room type, or null (no props).</summary>
        public PropSet PropsFor(RoomType type)
        {
            foreach (var e in roomProps)
                if (e.type == type)
                    return e.props;
            return null;
        }

        /// <summary>The global corridor prop set, or null.</summary>
        public PropSet HallwayProps() => hallwayProps;



        /// <summary>
        /// Contents for one alcove of this kind, chosen from its variant pool by `roll01`.
        /// Null = an empty recess.
        /// </summary>
        public PropSet AlcoveProps(AlcoveKind kind, float roll01)
        {
            foreach (var e in alcoveStyles)
                if (e.kind == kind)
                    return PickWeighted(e.props, e.variants, roll01);
            return null;
        }

        /// <summary>Contents for one prison cell, chosen by `roll01`. Null = a bare cell.</summary>
        public PropSet PrisonProps(float roll01) => PickWeighted(null, prisonProps, roll01);

        /// <summary>
        /// Weighted pick over `single` (an implicit weight-1 entry, may be null) plus `pool`.
        ///
        /// `roll01` must come from the recess's own hash stream, NOT from a shared counter and
        /// NOT from UnityEngine.Random: which variant a cell gets is part of what (seed, depth)
        /// reproduces (golden rule 4). One draw per recess, made unconditionally by the caller
        /// so the stream can't depend on how the pool is authored.
        /// </summary>
        public static PropSet PickWeighted(PropSet single, List<WeightedPropSet> pool, float roll01)
        {
            float total = single != null ? 1f : 0f;
            if (pool != null)
                foreach (var v in pool)
                    if (v != null && v.props != null) total += Mathf.Max(0f, v.weight);

            if (total <= 0f) return single;   // nothing authored, or every weight muted

            float t = roll01 * total;
            if (single != null)
            {
                t -= 1f;
                if (t <= 0f) return single;
            }
            if (pool != null)
                foreach (var v in pool)
                {
                    if (v == null || v.props == null) continue;
                    t -= Mathf.Max(0f, v.weight);
                    if (t <= 0f) return v.props;
                }

            // Float drift can leave t marginally positive on the last bucket.
            if (pool != null)
                for (int i = pool.Count - 1; i >= 0; i--)
                    if (pool[i] != null && pool[i].props != null) return pool[i].props;
            return single;
        }
    }
}