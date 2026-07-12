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