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
        /// <summary>THE feature: placed at a specific spot in the room (a named wall, or room center). Throne, altar, merchant counter. Use guaranteed + count 1.</summary>
        Feature,
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
            [Tooltip("Inspector label only.")]
            public string label;
            [Tooltip("Variants — deterministic hash-pick per placement.")]
            public GameObject[] prefabs;
            public PropAnchor anchor = PropAnchor.FloorScatter;
            [Tooltip("Feature only: a named wall, or the middle of the room.")]
            public FeaturePositionMode featurePositionMode = FeaturePositionMode.WallSide;
            [Tooltip("Feature + WallSide only: which wall, relative to the entrance.")]
            public FeatureWallSide featureWallSide = FeatureWallSide.Back;
            [Tooltip("Feature + WallSide only: middle of that wall, or one of its corners.")]
            public FeatureSpot featureSpot = FeatureSpot.Center;
            [Tooltip("Feature only: how its base yaw is computed before featureYaw is added.")]
            public FeatureFacing featureFacing = FeatureFacing.Outward;
            [Tooltip("Feature only: degrees added on top of featureFacing (or the absolute yaw when featureFacing = Fixed).")]
            public float featureYaw = 0f;
            [Tooltip("StaticDecor: mesh only, never blocks. StaticCollider: mesh + collider (blocks movement — occupancy-checked). InstancedMeshWithLight: mesh + light GameObject (candles!). FullGameObject: everything (future interactives).")]
            public PropTier tier = PropTier.StaticDecor;

            [Header("Count")]
            [Tooltip("Guaranteed: place exactly Count (cells permitting). Otherwise scatter by chance per eligible cell.")]
            public bool guaranteed;
            public int count = 1;
            [Tooltip("Scatter density: chance per eligible floor cell (non-guaranteed entries).")]
            [Range(0f, 1f)] public float chancePerCell = 0.06f;
            [Tooltip("Scatter cap per room. 0 = unlimited.")]
            public int maxPerRoom = 0;

            [Header("Placement feel")]
            [Tooltip("Scatter may use interior cells, not just wall-adjacent ones.")]
            public bool allowCenter = false;
            [Tooltip("Random yaw range in degrees (scatter/ceiling). Features use featureFacing/featureYaw instead.")]
            public Vector2 yawRange = new Vector2(0f, 360f);
            [Tooltip("Free positioning within the cell, 0 = dead center, 1 = full safe range.")]
            [Range(0f, 1f)] public float subCellJitter = 0.9f;
        }

        public List<PropEntry> entries = new List<PropEntry>();
    }
}
