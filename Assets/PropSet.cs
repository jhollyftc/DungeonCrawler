using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>Placement anchor — determines the algorithm that positions a prop.</summary>
    public enum PropAnchor
    {
        /// <summary>Scattered on the floor: density-driven, spaced, jittered. Rubble, crates, barrels.</summary>
        FloorScatter,
        /// <summary>Hung from the ceiling plane. Chandeliers, chains, cobwebs. Prefab origin = attachment point.</summary>
        CeilingHung,
        /// <summary>Mounted ON a wall face at a set height. Banners, shields, mirrors, sconces. Negotiates faces with torches (one occupant per face). Prefab forward = away from the wall.</summary>
        WallMounted,
        /// <summary>THE feature: placed at a specific spot in the room (a named wall, or room center). Throne, altar, merchant counter. Use guaranteed + count 1.</summary>
        Feature,
        /// <summary>Placed on a free cell BESIDE an already-placed prop whose Label matches Host Label (a bucket beside a crate). Runs after all other floor props. Chance-gated per host; cell-adjacency prevents overlap.</summary>
        NearPropAsset,
    }

    /// <summary>Top-level placement mode for a Feature entry.</summary>
    public enum FeaturePositionMode
    {
        /// <summary>Against a named wall — see FeatureWallSide/FeatureSpot.</summary>
        WallSide,
        /// <summary>Middle of the room, not wall-adjacent. Free-standing altars, ritual circles, dais props.</summary>
        RoomCenter,
    }

    /// <summary>Which wall, named relative to the room's primary entrance —
    /// not a world-cardinal direction, since a room's orientation in the grid
    /// varies. "Which way you'd face walking in the door."</summary>
    public enum FeatureWallSide
    {
        /// <summary>The wall roughly opposite the entrance (formerly "far wall"). Thrones, altars.</summary>
        Back,
        /// <summary>The wall to your left as you walk in.</summary>
        Left,
        /// <summary>The wall to your right as you walk in.</summary>
        Right,
        /// <summary>The wall the entrance itself sits on. Rare — a plaque or sconce beside the door.</summary>
        Front,
    }

    /// <summary>Where along the chosen wall's run a Feature entry lands.</summary>
    public enum FeatureSpot
    {
        /// <summary>Middle of the wall run.</summary>
        Center,
        /// <summary>One end of the run — a real room corner.</summary>
        Corner,
    }

    /// <summary>Coarse floor-cell zones within a room, relative to its primary
    /// entrance. Scatter entries can prefer a zone; DungeonVisualizer's
    /// colorCellsByZone gizmo shows the classification for verification.
    /// Evaluated in order — first match wins: Entrance (thresholds + cells one
    /// step from one), Perimeter (wall-adjacent), then Back/Center split by
    /// distance along the entrance axis.</summary>
    public enum RoomZone
    {
        /// <summary>Threshold cells and cells within 1 of one. Reserved-adjacent — keep clear-ish.</summary>
        Entrance,
        /// <summary>Non-perimeter cells in the far third of the room (relative to the entrance).</summary>
        Back,
        /// <summary>Non-perimeter cells in the near two-thirds.</summary>
        Center,
        /// <summary>Wall-adjacent cells that aren't Entrance. The classic scatter wall-bias.</summary>
        Perimeter,
    }

    /// <summary>Multi-select over RoomZone for a scatter/ceiling entry's
    /// preferred cells. Bit values are 1 &lt;&lt; (int)RoomZone, so
    /// <c>1 &lt;&lt; (int)zone</c> tests membership.</summary>
    [System.Flags]
    public enum RoomZoneMask
    {
        None = 0,
        Entrance = 1 << (int)RoomZone.Entrance,   // 1
        Back = 1 << (int)RoomZone.Back,           // 2
        Center = 1 << (int)RoomZone.Center,       // 4
        Perimeter = 1 << (int)RoomZone.Perimeter, // 8
    }

    /// <summary>How CeilingHung entries distribute across the ceiling.</summary>
    public enum CeilingLayout
    {
        /// <summary>Random cells by chance/zone — cobwebs, hanging chains, clutter.</summary>
        Scatter,
        /// <summary>A regular lattice (every Grid Stride cells) — hanging lights, chandeliers in rows. The chance roll still applies, so a grid can have occasional gaps.</summary>
        Grid,
    }

    /// <summary>How a scatter placement's yaw is computed (FloorScatter entries).</summary>
    public enum FacingRule
    {
        /// <summary>Random within yawRange — the classic scatter behavior. Rubble, crates.</summary>
        Random,
        /// <summary>Faces back toward the room's primary entrance.</summary>
        FaceEntrance,
        /// <summary>Faces the room's footprint centroid.</summary>
        FaceRoomCenter,
        /// <summary>Back against the nearest wall, facing into the room. Shelves, cabinets. Interior cells (no wall) fall back to Random.</summary>
        FaceAwayFromNearestWall,
        /// <summary>Forward along the nearest wall's tangent (direction picked per placement). Beds, benches. Interior cells fall back to Random.</summary>
        AlignWithWall,
    }

    /// <summary>How a Feature entry's yaw is computed (before featureYaw is added).</summary>
    public enum FeatureFacing
    {
        /// <summary>Faces away from its own wall, into the room. Default —
        /// always well-defined regardless of which wall/side was chosen.
        /// For RoomCenter placements (no wall), falls back to FaceEntrance.</summary>
        Outward,
        /// <summary>Faces its own wall (a mirror, a "facing the wall" prop).
        /// For RoomCenter placements (no wall), falls back to FaceAwayFromEntrance.</summary>
        Inward,
        /// <summary>Faces back toward the primary entrance, regardless of which wall it's on.</summary>
        FaceEntrance,
        /// <summary>Faces away from the primary entrance (e.g. a counter with its back to the door).</summary>
        FaceAwayFromEntrance,
        /// <summary>Ignores wall/entrance entirely; featureYaw is used as an absolute world yaw.</summary>
        Fixed,
    }

    /// <summary>
    /// A reusable set of prop entries for one room flavor. RoomStyle maps room
    /// types to PropSets; sets can be shared (Barracks and Armory pointing at
    /// one "military junk" set). Phase 1 anchors: floor scatter, ceiling hung,
    /// feature (named wall or room center). Wall-mounted and clusters come later.
    /// </summary>
    [CreateAssetMenu(fileName = "PropSet", menuName = "Dungeon/Prop Set")]
    public class PropSet : ScriptableObject
    {
        [System.Serializable]
        public class PropEntry
        {
            [Tooltip("This prop's KIND. Names the inspector entry AND acts as a tag: a Near Prop Asset entry attaches to props with a matching Host Label, and Min Spacing keeps same-Label props apart. Give matching props (e.g. all statues) the same Label to space them from each other.")]
            public string label;
            [Tooltip("Variants — deterministic hash-pick per placement.")]
            public GameObject[] prefabs;

            [Header("Placement anchor type")]
            public PropAnchor anchor = PropAnchor.FloorScatter;
            [Tooltip("StaticDecor: mesh only, never blocks. StaticCollider: mesh + collider (blocks movement — occupancy-checked). InstancedMeshWithLight: mesh + light GameObject (candles!). FullGameObject: everything (future interactives).")]
            public PropTier tier = PropTier.StaticDecor;
            
            [Header("Feature placement")]
            [Tooltip("Feature only: a named wall, or the middle of the room.")]
            public FeaturePositionMode featurePositionMode = FeaturePositionMode.WallSide;
            [Tooltip("Feature + WallSide only: which wall, relative to the entrance.")]
            public FeatureWallSide featureWallSide = FeatureWallSide.Back;
            [Tooltip("Feature + WallSide only: middle of that wall, or one of its corners.")]
            public FeatureSpot featureSpot = FeatureSpot.Center;
            
            [Header("Feature orientation")]
            [Tooltip("Feature only: how its base yaw is computed before featureYaw is added.")]
            public FeatureFacing featureFacing = FeatureFacing.Outward;
            [Tooltip("Feature only: degrees added on top of featureFacing (or the absolute yaw when featureFacing = Fixed).")]
            public float featureYaw = 0f;

            [Header("Near-prop / spacing")]
            [Tooltip("NearPropAsset only: attach beside already-placed props whose Label equals this (case-sensitive).")]
            public string hostLabel = "";
            [Tooltip("NearPropAsset only: chance to place a prop beside each matching host.")]
            [Range(0f, 1f)] public float chancePerHost = 0.6f;
            [Tooltip("Keep same-Label props at least this many cells apart (0 = off). E.g. two statue entries sharing Label 'Statue' won't clump. Floor/feature props only.")]
            public int minSpacing = 0;


            [Header("Count")]
            [Tooltip("Guaranteed: place exactly Count (cells permitting). Otherwise scatter by chance per eligible cell.")]
            public bool guaranteed;
            public int count = 1;
            [Tooltip("Scatter density: chance per eligible floor cell (non-guaranteed entries).")]
            [Range(0f, 1f)] public float chancePerCell = 0.06f;
            [Tooltip("Scatter cap per room. 0 = unlimited.")]
            public int maxPerRoom = 0;

            [Header("Placement feel")]
            [Tooltip("FloorScatter / CeilingHung: which zone(s) this entry places into — multi-select (e.g. Center + Back). Perimeter alone reproduces the classic wall-bias. Ignored when Allow Center is on.")]
            public RoomZoneMask preferredZones = RoomZoneMask.Perimeter;
            [Tooltip("FloorScatter only: how each placement's yaw is computed. Random uses yawRange; the wall rules read the cell's nearest solid wall.")]
            public FacingRule facing = FacingRule.Random;
          
            [Tooltip("Yaw variation in degrees, applied ON TOP of the facing direction (Random facing: this IS the yaw). Wall-aligned/facing entries want a NARROW range like (-5, 5) — the (0, 360) default will spin them randomly. Features use featureFacing/featureYaw instead.")]
            public Vector2 yawRange = new Vector2(0f, 360f);
            [Tooltip("Free positioning within the cell, 0 = dead center, 1 = full safe range. With Snap To Wall, jitter runs along the wall only.")]
            [Range(0f, 1f)] public float subCellJitter = 0.9f;
            [Tooltip("Pull the placement flush to its wall: the prop origin sits Wall Gap meters off the nominal wall face instead of floating at the cell center. FloorScatter uses the cell's nearest wall (same wall the facing rules read); Feature + WallSide uses its chosen wall. No wall at the cell = normal placement.")]
            public bool snapToWall = false;
            [Tooltip("FloorScatter / CeilingHung: place ONLY at inside corners (where two perpendicular walls meet), tucked into the corner. Cobwebs, corner debris. Rooms + hallways (hallway corners = corridor bends/junctions). Ignores zones and the wall's Allow Props In Front flag. Takes precedence over Snap To Wall / Snap To Ceiling Wall. Uses Wall Gap.")]
            public bool snapToInsideCorner = false;
            [Tooltip("FloorScatter / CeilingHung: this prop does NOT reserve its tile — another prop may occupy the same tile, and this one may sit on an already-used tile. E.g. a corner cobweb that shouldn't block a hanging lantern on that tile. Physical collision (blocking tiers) still applies; this only affects the one-prop-per-tile visual rule.")]
            public bool sharesTile = false;
            [Tooltip("Meters between the nominal wall plane and the prop origin when Snap To Wall is on. Tune per asset (account for the wall kit's relief depth). WallMounted also uses this as its distance off the wall face.")]
            public float wallGap = 0.1f;

            [Header("Wall / Ceiling mount")]
            [Tooltip("WallMounted: meters above the floor the prop mounts. Torches default ~2.2; hang banners higher, shields lower.")]
            public float mountHeight = 2.2f;
            [Tooltip("WallMounted: +/- meters of deterministic height variation on top of Mount Height. 0 = every instance at the same height.")]
            public float mountHeightJitter = 0f;
            [Tooltip("CeilingHung: Scatter = random cells; Grid = a regular lattice (hanging lights). The chance roll applies either way, so a Grid can have gaps.")]
            public CeilingLayout ceilingLayout = CeilingLayout.Scatter;
            [Tooltip("CeilingHung + Grid: cells between placements. 1 = every tile, 2 = every other tile, 3 = every third. Anchored to the room's corner so it's stable across regens.")]
            public int gridStride = 2;
            [Tooltip("CeilingHung + Scatter: snap flush to the nearest wall at the ceiling plane (the ceiling equivalent of Snap To Wall — one wall, not a corner). Uses Wall Gap and tangent-only jitter. Ignored in Grid layout. For true two-wall corners use Snap To Inside Corner.")]
            public bool snapToCeilingWall = false;

            [Header("Legacy: Anywhere toggle")]
            [Tooltip("Legacy 'anywhere' toggle: skip the zone filter entirely — scatter may use ANY free cell.")]
            public bool allowCenter = false;
        }

        public List<PropEntry> entries = new List<PropEntry>();
    }
}
