using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Semantic role of a room, assigned after generation by reading the graph
    /// and geometry. Drives cosmetics now (gizmo colors; torch palette and props
    /// later) and gameplay eventually (encounters, loot). Extend freely — the
    /// assigner and budget table key off this.
    /// </summary>
    public enum RoomType
    {
        Generic,
        Start,        // singleton: one end of the longest MST path
        Exit,         // singleton: the other end — a portal-out room
        ThroneRoom,   // singleton: large, OFF the critical path (optional reward)
        Merchant,     // singleton: safe, ON the critical path (reliably found)
        Barracks,     // category
        Kitchen,      // category
        Library,      // category
        Shrine,       // category
        ChestVault,   // satellite off a Generic room (the plain closet)
        Treasury,     // satellite off a ThroneRoom (guaranteed setpiece vault)
        Armory,       // satellite off a Barracks
        Pantry,       // satellite off a Kitchen
        Study,        // satellite off a Library
        Reliquary,    // satellite off a Shrine
    }

    /// <summary>
    /// How many of a category type to place, as a depth-scaled range.
    /// </summary>
    [System.Serializable]
    public struct CategoryBudget
    {
        public RoomType type;
        public int minDepth;      // type is illegal below this run depth
        public int baseCount;     // count at minDepth
        public float countPerDepth; // additional count per depth above minDepth
        public int maxCount;      // hard ceiling

        public int CountAt(int depth)
        {
            if (depth < minDepth) return 0;
            int c = baseCount + Mathf.FloorToInt((depth - minDepth) * countPerDepth);
            return Mathf.Clamp(c, 0, maxCount);
        }
    }

    /// <summary>
    /// Turns run depth into concrete generation parameters — formula-driven for
    /// smooth infinite scaling, with authored override points for discrete
    /// content unlocks (a type becoming legal at a given depth). This table IS
    /// the game's content-progression curve.
    ///
    /// Only the typing-relevant fields exist now; loot tier, enemy budget, and
    /// torch palette will join here so every system reads one depth source.
    /// </summary>
    /// <summary>
    /// One alcove kind's legality, frequency and size. Same shape as CategoryBudget and
    /// SatelliteRule: a minDepth gate plus authored numbers, so the progression curve stays
    /// readable in one inspector rather than spread across code.
    /// </summary>
    [System.Serializable]
    public struct AlcoveRule
    {
        public AlcoveKind kind;
        [Tooltip("Run depth below which this kind is never picked.")]
        public int minDepth;
        [Tooltip("Relative pick weight among the kinds legal at the current depth. Normalized at pick time, so these are ratios, not probabilities.")]
        public float weight;
        [Tooltip("Hard ceiling on this kind per run. Enforced by REJECTING after the pick, never by skipping the draw — skipping would make the RNG draw count depend on what was already placed and break (seed, depth) determinism.")]
        public int maxPerRun;
        [Tooltip("Cell width across the mouth. Above 1 gets a 1x1 doorway tile with the space widening behind it, exactly like a wide prison cell.")]
        public Vector2Int widthRange;
        [Tooltip("Cell depth away from the corridor. 1 = a shallow look-in recess (decor only, nothing blocking); 2+ = enterable.")]
        public Vector2Int depthRange;
    }

    /// <summary>One region kind and when it may appear. Mirrors <see cref="AlcoveRule"/>.</summary>
    [System.Serializable]
    public struct RegionRule
    {
        public RegionDefinition definition;
        [Tooltip("Run depth below which this region is never picked. Staggering these is what makes deeper runs introduce KINDS the player has not seen, rather than just more of the same ones.")]
        public int minDepth;
        [Tooltip("Relative pick weight among the regions legal at the current depth. Normalized at pick time, so these are ratios, not probabilities.")]
        public float weight;
        [Tooltip("Hard ceiling on this region per run. 0 = unlimited.\n\n1 IS THE USUAL ANSWER — two spider nests in one dungeon dilute both — but note the interaction: if every legal region is capped at 1 and the run wants more sites than you have definitions, the extras have nowhere to go and are dropped with a warning. Regions are removed from the candidate pool once capped rather than being drawn and discarded, so a cap costs you a site only when the pool genuinely runs dry.")]
        public int maxPerRun;
    }

    [CreateAssetMenu(fileName = "DepthProfile", menuName = "Dungeon/Depth Profile")]
    public class DepthProfile : ScriptableObject
    {
        [Header("Room count (formula)")]
        public int baseRoomCount = 4;
        public float roomsPerDepth = 1f;
        public int maxRoomCount = 40;

        [Header("Grid scales with room count")]
        [Tooltip("Grid edge cells per room, roughly. Keeps big dungeons from cramping.")]
        public float gridCellsPerRoom = 5f;
        public int minGridEdge = 30;
        public int gridHeight = 4;

        [Header("Singleton unlock depths (hard caps of 1 each)")]
        public int throneMinDepth = 6;
        public int merchantMinDepth = 3;

        [Header("Category budgets (soft counts)")]
        public List<CategoryBudget> categories = new List<CategoryBudget>
        {
            new CategoryBudget { type = RoomType.Barracks, minDepth = 2, baseCount = 1, countPerDepth = 0.25f, maxCount = 4 },
            new CategoryBudget { type = RoomType.Kitchen,  minDepth = 3, baseCount = 1, countPerDepth = 0.15f, maxCount = 2 },
            new CategoryBudget { type = RoomType.Library,  minDepth = 5, baseCount = 1, countPerDepth = 0.1f,  maxCount = 2 },
            new CategoryBudget { type = RoomType.Shrine,   minDepth = 4, baseCount = 1, countPerDepth = 0.15f, maxCount = 3 },
        };

        public int RoomCountAt(int depth) =>
            Mathf.Clamp(baseRoomCount + Mathf.FloorToInt(depth * roomsPerDepth), baseRoomCount, maxRoomCount);

        public int GridEdgeAt(int depth) =>
            Mathf.Max(minGridEdge, Mathf.CeilToInt(RoomCountAt(depth) * gridCellsPerRoom));

        [Header("Room size classes (largest-first placement)")]
        [Tooltip("One grand room is guaranteed whenever the throne is legal at this depth — so the throne always has a hall worthy of it (and big enough for columns).")]
        public Vector2Int grandRoomEdge = new Vector2Int(7, 8);
        public int grandRoomHeight = 2;
        [Tooltip("Large rooms guaranteed per depth (barracks/library candidates).")]
        public int largeBaseCount = 1;
        public float largePerDepth = 0.25f;
        public int largeMaxCount = 4;
        public Vector2Int largeRoomEdge = new Vector2Int(5, 6);

        [Header("Irregular room shapes")]
        [Tooltip("Chance an eligible room (min edge below) is L/T/plus/notch shaped instead of a box.")]
        [Range(0f, 1f)] public float shapedRoomChance = 0.3f;
        [Tooltip("Smallest room edge that can carry a shape (small rooms stay boxes).")]
        public int shapeMinEdge = 5;

        public int LargeCountAt(int depth) =>
            Mathf.Clamp(largeBaseCount + Mathf.FloorToInt(depth * largePerDepth), 0, largeMaxCount);

        public bool ThroneLegal(int depth) => depth >= throneMinDepth;
        public bool MerchantLegal(int depth) => depth >= merchantMinDepth;

        [Header("Hallway alcoves")]
        [Tooltip("Run depth below which no alcoves are carved at all.")]
        public int alcoveMinDepth = 1;
        [Tooltip("Chance per VIABLE corridor face at alcoveMinDepth. Viable = the cell behind the face is solid rock, so directions running along a corridor are never rolled — this is a real candidate count, not a raw cell sweep, which is why it can sensibly go to 1. Roughly half of viable faces are then rejected anyway by the one-opening rule (rock only one cell thick between corridors), so even 1.0 yields occasional alcoves, not a honeycomb.")]
        [Range(0f, 1f)] public float alcoveBaseChance = 0.3f;
        [Tooltip("Added to the chance per depth above alcoveMinDepth. This is what makes deeper runs feel structurally busier rather than just larger.")]
        public float alcoveChancePerDepth = 0.05f;
        [Tooltip("Ceiling on the per-face chance, however deep the run gets.")]
        [Range(0f, 1f)] public float alcoveMaxChance = 0.8f;
        [Tooltip("Hard ceiling on alcoves per run, so a long-corridor seed can't fill with them.")]
        public int alcoveMaxCount = 12;

        [Tooltip("Which alcove kinds are legal, how often each is picked, and how big it is. A kind whose depthRange minimum is 1 is a shallow look-in recess; 2+ is enterable and may hold blocking props.")]
        public List<AlcoveRule> alcoveKinds = new List<AlcoveRule>
        {
            new AlcoveRule { kind = AlcoveKind.StatueNook,    minDepth = 1, weight = 1f,   maxPerRun = 4,
                             widthRange = new Vector2Int(1, 2), depthRange = new Vector2Int(1, 2) },
            new AlcoveRule { kind = AlcoveKind.ShrineNiche,   minDepth = 2, weight = 0.8f, maxPerRun = 3,
                             widthRange = new Vector2Int(1, 1), depthRange = new Vector2Int(1, 1) },
            new AlcoveRule { kind = AlcoveKind.CollapsedDig,  minDepth = 1, weight = 1.2f, maxPerRun = 5,
                             widthRange = new Vector2Int(1, 3), depthRange = new Vector2Int(2, 3) },
            new AlcoveRule { kind = AlcoveKind.StorageRecess, minDepth = 2, weight = 1f,   maxPerRun = 4,
                             widthRange = new Vector2Int(2, 3), depthRange = new Vector2Int(2, 3) },
        };

        /// <summary>Per-face alcove chance at a depth. 0 below alcoveMinDepth.</summary>
        public float AlcoveChanceAt(int depth)
        {
            if (depth < alcoveMinDepth) return 0f;
            return Mathf.Clamp(alcoveBaseChance + (depth - alcoveMinDepth) * alcoveChancePerDepth,
                               0f, alcoveMaxChance);
        }

        [Header("Crawlways")]
        [Tooltip("Run depth below which no crawlways are bored. Later than alcoves by default: a crawl passage is a reward for knowing the dungeon, and it reads better once the player has learned what an ordinary route costs.")]
        public int crawlwayMinDepth = 2;
        [Tooltip("Sewer networks at crawlwayMinDepth. A network is a PLACE, so this stays small — three sprawling systems read as a second dungeon rather than as a secret.")]
        public int sewerBaseNetworks = 1;
        [Tooltip("Extra networks per depth above crawlwayMinDepth, rounded down. 0.34 gives roughly one more every three depths.")]
        public float sewerNetworksPerDepth = 0.34f;
        [Tooltip("Ceiling on networks per run, however deep it gets. NB the real limiter is usually the ROCK, not this — a dense dungeon simply has nowhere to put them, and the tally says so.")]
        public int sewerMaxNetworks = 3;

        /// <summary>Sewer networks to attempt at a depth. 0 below crawlwayMinDepth.</summary>
        public int SewerNetworksAt(int depth)
        {
            if (depth < crawlwayMinDepth) return 0;
            return Mathf.Clamp(
                sewerBaseNetworks + Mathf.FloorToInt((depth - crawlwayMinDepth) * sewerNetworksPerDepth),
                0, sewerMaxNetworks);
        }

        /// <summary>Kinds legal at this depth, in authored order. Empty = carve no alcoves.</summary>
        public List<AlcoveRule> AlcoveKindsAt(int depth)
        {
            var legal = new List<AlcoveRule>();
            if (alcoveKinds == null) return legal;
            foreach (var r in alcoveKinds)
                if (depth >= r.minDepth && r.weight > 0f) legal.Add(r);
            return legal;
        }

        [Header("Gates (locked doors and portcullises)")]
        [Tooltip("Run depth below which no door is locked. Gates are a 'you must find something' mechanic, so they read better once the player knows what an ordinary route costs.")]
        public int lockedDoorMinDepth = 3;
        [Tooltip("Locked doors at lockedDoorMinDepth. Small: every locked door is a detour the player MUST take, so several at once reads as a chore rather than a landmark.")]
        public int lockedDoorBaseCount = 1;
        [Tooltip("Extra locked doors per depth beyond the minimum. Fractional — 0.25 adds roughly one every four depths.")]
        public float lockedDoorPerDepth = 0.25f;
        [Tooltip("Hard ceiling on locked doors per run.")]
        public int lockedDoorMaxCount = 3;

        [Tooltip("Run depth below which no portcullis is placed. Separate from locked doors on purpose, so a portcullis can be introduced later as a distinct thing rather than arriving alongside them.")]
        public int portcullisMinDepth = 4;
        [Tooltip("Portcullises at portcullisMinDepth.")]
        public int portcullisBaseCount = 1;
        [Tooltip("Extra portcullises per depth beyond the minimum.")]
        public float portcullisPerDepth = 0.2f;
        [Tooltip("Hard ceiling on portcullises per run.")]
        public int portcullisMaxCount = 2;
        [Tooltip("A portcullis must stand in a STRAIGHT corridor run at least this many cells long, and near its middle — that is what makes it read as dividing a hallway rather than plugging a corner.\n\nThis is the aesthetic dial only. Whether the gate actually DIVIDES anything is decided by a cut test on the walkable network, not by length: a long corridor with a loop edge running parallel gates nothing however long it is.")]
        public int portcullisMinRunLength = 7;

        [Tooltip("Closest a lever may be to its gate, in PATH steps (not straight-line — five cells euclidean can be through a wall in another room). Keeps the lever out of sight of the thing it opens, so finding it is a small search.")]
        public int leverMinDistance = 4;
        [Tooltip("Furthest a lever may be from its gate, in path steps. Too far and the player never connects the lever to the sound they heard.")]
        public int leverMaxDistance = 18;

        /// <summary>Locked doors a run at this depth gets. 0 below <c>lockedDoorMinDepth</c>.</summary>
        public int LockedDoorCountAt(int depth)
        {
            if (depth < lockedDoorMinDepth || lockedDoorMaxCount <= 0) return 0;
            int n = lockedDoorBaseCount + Mathf.FloorToInt((depth - lockedDoorMinDepth) * lockedDoorPerDepth);
            return Mathf.Clamp(n, 0, lockedDoorMaxCount);
        }

        /// <summary>Portcullises a run at this depth gets. 0 below <c>portcullisMinDepth</c>.</summary>
        public int PortcullisCountAt(int depth)
        {
            if (depth < portcullisMinDepth || portcullisMaxCount <= 0) return 0;
            int n = portcullisBaseCount + Mathf.FloorToInt((depth - portcullisMinDepth) * portcullisPerDepth);
            return Mathf.Clamp(n, 0, portcullisMaxCount);
        }

        [Header("Regions (areas of influence over prop selection)")]
        [Tooltip("Run depth below which the dungeon has NO regions at all and is entirely vanilla.\n\nABOVE 1 ON PURPOSE, and it is also the system's regression test: with no sites every influence is zero and every multiplier is 1, so depth 1 must place props bit-identically to a build without regions. That provable-inert state is what lets the plumbing ship before any content exists — the same property the crawlway bore has.")]
        public int regionMinDepth = 3;
        [Tooltip("Regions at regionMinDepth. Starting at ONE is deliberate: a single strange quarter in an otherwise ordinary dungeon reads as an anomaly, which is a stronger first impression than two competing ones.")]
        public int regionBaseCount = 1;
        [Tooltip("Extra regions per depth beyond regionMinDepth. Fractional — 0.34 adds roughly one every three depths.")]
        public float regionCountPerDepth = 0.34f;
        [Tooltip("Hard ceiling on regions in one run. KEEP IT SMALL. Regions have to be bigger than the distance a player walks between landmarks or none of them reads as a place, and past four or five the dungeon is all region and nothing is vanilla to contrast against.")]
        public int regionMaxCount = 4;
        [Tooltip("Vertical exaggeration in the region distance metric. At 1 a region is a full-height column whatever the maths says, because the grid is ~12 tall against ~40 wide. At 3, twelve storeys read as roughly as far apart as the floor is wide — so a staircase becomes a transition between regions.")]
        public float regionYScale = 3f;
        [Tooltip("The regions that may appear, with their own depth gates and caps.")]
        public List<RegionRule> regions = new List<RegionRule>();

        /// <summary>How many regions a run at this depth gets. 0 below <c>regionMinDepth</c>.</summary>
        public int RegionCountAt(int depth)
        {
            if (depth < regionMinDepth || regionMaxCount <= 0) return 0;
            int n = regionBaseCount + Mathf.FloorToInt((depth - regionMinDepth) * regionCountPerDepth);
            return Mathf.Clamp(n, 0, regionMaxCount);
        }

        /// <summary>Regions legal at this depth, for the weighted pick.</summary>
        public List<RegionRule> RegionsAt(int depth)
        {
            var legal = new List<RegionRule>();
            if (regions == null) return legal;
            foreach (var r in regions)
                if (r.definition != null && depth >= r.minDepth && r.weight > 0f) legal.Add(r);
            return legal;
        }

        [Header("Satellite (closet) rooms")]
        [Tooltip("Host room types that ALWAYS get a satellite (setpieces).")]
        public List<SatelliteRule> guaranteedSatellites = new List<SatelliteRule>
        {
            new SatelliteRule { host = RoomType.ThroneRoom, satellite = RoomType.Treasury, minDepth = 6 },
        };
        [Tooltip("Host room types that MIGHT get a satellite (rolled per eligible host).")]
        public List<SatelliteRule> chancedSatellites = new List<SatelliteRule>
        {
            new SatelliteRule { host = RoomType.Barracks, satellite = RoomType.Armory,    minDepth = 2, chance = 0.6f },
            new SatelliteRule { host = RoomType.Kitchen,  satellite = RoomType.Pantry,    minDepth = 3, chance = 0.7f },
            new SatelliteRule { host = RoomType.Library,  satellite = RoomType.Study,     minDepth = 5, chance = 0.7f },
            new SatelliteRule { host = RoomType.Shrine,   satellite = RoomType.Reliquary, minDepth = 4, chance = 0.5f },
            new SatelliteRule { host = RoomType.Generic,  satellite = RoomType.ChestVault, minDepth = 1, chance = 0.25f },
        };

        /// <summary>Returns (satellite type, guaranteed) for a host type at a depth, or null if none applies.</summary>
        public (RoomType satellite, bool guaranteed, float chance)? SatelliteFor(RoomType host, int depth)
        {
            foreach (var r in guaranteedSatellites)
                if (r.host == host && depth >= r.minDepth)
                    return (r.satellite, true, 1f);
            foreach (var r in chancedSatellites)
                if (r.host == host && depth >= r.minDepth)
                    return (r.satellite, false, r.chance);
            return null;
        }

        [Header("Interior columns (grand rooms)")]
        [Tooltip("Lattice points between columns. 2 = a column every 2 tiles (6m at 3m cells).")]
        public int columnSpacing = 2;
        [Tooltip("Tiles of clear walkway between the wall and the first column ring.")]
        public int columnWallInset = 2;
        [Tooltip("Which room types get interior columns, with chance and min room size.")]
        public List<ColumnRule> columnRules = new List<ColumnRule>
        {
            new ColumnRule { type = RoomType.ThroneRoom, chance = 1f,   minRoomEdge = 6 }, // always
            new ColumnRule { type = RoomType.Library,    chance = 0.5f, minRoomEdge = 6 },
            new ColumnRule { type = RoomType.Generic,    chance = 0.2f, minRoomEdge = 7 },
        };

        public ColumnRule? ColumnsFor(RoomType type)
        {
            foreach (var r in columnRules)
                if (r.type == type) return r;
            return null;
        }
    }

    [System.Serializable]
    public struct SatelliteRule
    {
        public RoomType host;
        public RoomType satellite;
        public int minDepth;
        [Range(0f, 1f)] public float chance; // ignored for guaranteed rules
    }

    [System.Serializable]
    public struct ColumnRule
    {
        public RoomType type;
        [Range(0f, 1f)] public float chance; // 1 = always
        public int minRoomEdge;              // smallest room edge (cells) that qualifies
    }
}