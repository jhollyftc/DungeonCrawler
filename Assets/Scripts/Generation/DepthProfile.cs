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
        [Tooltip("Chance per VIABLE wall face at crawlwayMinDepth. Viable = an open cell whose neighbour is solid rock. This wants to be MUCH higher than alcoveBaseChance for the same yield: an alcove needs only room in the rock, while a crawlway must also find a far end that is legal AND far away on foot, so the great majority of rolls are rejected by design.")]
        [Range(0f, 1f)] public float crawlwayBaseChance = 0.35f;
        [Tooltip("Added to the chance per depth above crawlwayMinDepth.")]
        public float crawlwayChancePerDepth = 0.05f;
        [Tooltip("Ceiling on the per-face chance, however deep the run gets.")]
        [Range(0f, 1f)] public float crawlwayMaxChance = 0.7f;
        [Tooltip("Hard ceiling on crawlways per run. Deliberately small — a crawlway is a landmark, and a dungeon riddled with them stops being one you learn the shape of.")]
        public int crawlwayMaxCount = 3;

        /// <summary>Per-face crawlway chance at a depth. 0 below crawlwayMinDepth.</summary>
        public float CrawlwayChanceAt(int depth)
        {
            if (depth < crawlwayMinDepth) return 0f;
            return Mathf.Clamp(crawlwayBaseChance + (depth - crawlwayMinDepth) * crawlwayChancePerDepth,
                               0f, crawlwayMaxChance);
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