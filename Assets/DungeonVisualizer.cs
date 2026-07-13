using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Drop on an empty GameObject. Right-click the component header (or use the
    /// context menu button) -> "Generate". Draws the grid with gizmos, color-coded
    /// by cell type. The stage enum is a scrubber scaffold for later pipeline steps.
    /// </summary>
    public class DungeonVisualizer : MonoBehaviour
    {
        public enum ViewStage
        {
            Rooms,
            Delaunay,
            Graph,
            Hallways,
        }

        public int seed = 12345;
        public bool randomizeSeedOnGenerate = false;
        public DungeonConfig config = new DungeonConfig();
        public ViewStage stage = ViewStage.Rooms;

        [Header("Mesh")]
        public bool buildMeshOnGenerate = true;
        public float cellSize = 3f;
        [Tooltip("Meters to inset the collision mesh's wall faces from the nominal grid boundary, so the invisible collider sits flush with (not behind) the kit's decorative wall relief. 0 = flush with the grid, the old behavior.")]
        public float wallMargin = 0f;
        public GeometryMode geometryMode = GeometryMode.GeneratedMesh;
        public DungeonKit kit = new DungeonKit();

        [Header("Torches")]
        public TorchSettings torches = new TorchSettings();
        [Tooltip("Per-room-type torch color/intensity/spacing. Leave empty for uniform warm torches.")]
        public RoomStyle roomStyle;

        [Header("Fog")]
        [Tooltip("Runtime fog color blending toward the current/approaching room's torch palette. Needs a RoomStyle and fog enabled in Lighting > Environment.")]
        public FogSettings fog = new FogSettings();

        public enum GeometryMode { GeneratedMesh, PrefabKit, InstancedKit }

        [Header("Gizmo colors")]
        public bool colorRoomsByType = false;
        [Tooltip("Debug: color room floor cells by prop-placement zone (green = Entrance, red = Back, grey = Center, blue = Perimeter). Verifies RoomPropPlacer.ComputeZones; overrides colorRoomsByType on floor cells.")]
        public bool colorCellsByZone = false;
        public Color roomColor = new Color(0.9f, 0.25f, 0.2f, 0.9f);
        public Color hallwayColor = new Color(0.2f, 0.45f, 0.95f, 0.9f);
        public Color stairColor = new Color(0.25f, 0.85f, 0.35f, 0.9f);
        public Color prisonColor = new Color(0.85f, 0.55f, 0.15f, 0.9f);
        public Color boundsColor = new Color(1f, 1f, 1f, 0.25f);
        public Color delaunayColor = new Color(1f, 0.85f, 0.2f, 0.8f);
        public Color mstColor = new Color(0.3f, 0.95f, 0.95f, 1f);
        public Color loopColor = new Color(0.95f, 0.35f, 0.95f, 1f);

        DungeonGenerator gen;
        public DungeonGenerator Generator => gen;

        [ContextMenu("Generate")]
        public void Generate()
        {
            if (randomizeSeedOnGenerate)
                seed = Random.Range(int.MinValue, int.MaxValue);

            if (roomStyle != null) roomStyle.InvalidateWallCache();

            gen = new DungeonGenerator(config, seed);
            gen.Generate();
            int edgeTotal = gen.MstEdges.Count + gen.LoopEdges.Count;
            var typeCounts = new System.Collections.Generic.Dictionary<RoomType, int>();
            foreach (var room in gen.Rooms)
            {
                typeCounts.TryGetValue(room.Type, out int ct);
                typeCounts[room.Type] = ct + 1;
            }
            var typeSummary = new System.Collections.Generic.List<string>();
            foreach (var kv in typeCounts) typeSummary.Add($"{kv.Value} {kv.Key}");
            Debug.Log($"[Dungeon] seed {seed} depth {config.depth}: {gen.Rooms.Count}/{config.roomCount} rooms, " +
                      $"{edgeTotal - gen.FailedEdges}/{edgeTotal} edges carved, " +
                      $"{gen.Stairs.Count / 4} staircases, {gen.PrisonCells.Count} prison cells | " +
                      $"types: {string.Join(", ", typeSummary)}");

            if (buildMeshOnGenerate)
                BuildMesh();
        }

        [ContextMenu("Build Mesh")]
        public void BuildMesh()
        {
            if (gen == null)
            {
                Generate(); // recompile may have cleared the generator
                if (buildMeshOnGenerate) return; // Generate already built the mesh
            }

            // Replace any previous geometry child (any mode) and torches.
            foreach (string name in new[] { "DungeonMesh", "DungeonKit", "DungeonInstanced", "DungeonTorches", "DungeonDoors", "DungeonArchways", "DungeonColumns", "DungeonLadders", "DungeonKitColliders", "DungeonProps", "DungeonFog" })
            {
                Transform old = transform.Find(name);
                if (old != null)
                {
                    if (Application.isPlaying) Destroy(old.gameObject);
                    else DestroyImmediate(old.gameObject);
                }
            }

            InstancedDungeonRenderer sharedInstancer = null;

            // Per-face restrictions from RoomStyle.WallAsset flags. Filled by
            // the kit placer as walls emit, queried by the torch and prop
            // placers below (empty in GeneratedMesh mode = no restrictions).
            var wallFaces = new WallFaceRegistry();

            if (geometryMode == GeometryMode.PrefabKit)
            {
                DungeonKitPlacer.Build(gen, kit, cellSize, transform, roomStyle, wallFaces);
                DungeonKitPlacer.BuildDoors(gen, kit, cellSize, transform, roomStyle);
                DungeonKitPlacer.BuildArchways(gen, kit, cellSize, transform, null, roomStyle);
                DungeonKitPlacer.BuildInteriorColumns(gen, kit, cellSize, transform);
                DungeonKitPlacer.BuildLadders(gen, kit, cellSize, transform);
            }
            else if (geometryMode == GeometryMode.InstancedKit)
            {
                // Collision: the greybox shell, invisible. Visuals: instanced kit.
                // Stair ramps are skipped once the kit has real stair prefabs
                // (EmitCollider below gives them their own authored collider) —
                // otherwise the approximate greybox ramp and the precise
                // stepped collider would disagree about where the floor is.
                bool kitHasStairs = kit.stairPrefabs != null && kit.stairPrefabs.Length > 0;
                var collision = DungeonMesher.Build(gen, cellSize, transform, wallMargin, !kitHasStairs);
                var collisionRenderer = collision.GetComponent<MeshRenderer>();
                if (collisionRenderer != null) collisionRenderer.enabled = false;

                var irGo = new GameObject("DungeonInstanced");
                irGo.transform.SetParent(transform, false);
                var ir = irGo.AddComponent<InstancedDungeonRenderer>();
                sharedInstancer = ir;

                // Holds the collider GameObjects for mesh-instanced pieces that
                // still need real collision (stairs, corner pillars) — the
                // greybox doesn't provide it for these. Mirrors the split
                // archways/columns already use (mesh -> instancer, collider ->
                // GameObject), just routed through Enumerate's second sink.
                var kitColliders = new GameObject("DungeonKitColliders");
                kitColliders.transform.SetParent(transform, false);

                var missing = new System.Collections.Generic.HashSet<string>();
                DungeonKitPlacer.Enumerate(gen, kit, missing, (prefab, posCells, rot, offset) =>
                {
                    var m = Matrix4x4.TRS(posCells * cellSize + offset + transform.position, rot, Vector3.one);
                    // The static shell (walls/floors/ceilings) casts NO shadows
                    // — wall-on-wall shadows are invisible, but thousands of
                    // shell instances redrawn into every shadowed torch's six
                    // cubemap faces were THE torch-shadow performance killer.
                    // The shell still receives, so detail shadows (columns,
                    // arches, props — the placeWithCollider sink below and
                    // PropInstancer paths, which keep casting) fall across it.
                    ir.AddInstance(prefab, m, castShadows: false);
                }, roomStyle, (prefab, posCells, rot, offset) =>
                {
                    Vector3 worldPos = posCells * cellSize + offset + transform.position;
                    PropInstancer.PlaceProps(ir, prefab,
                        new[] { new PropPlacement { position = worldPos, rotation = rot } },
                        PropTier.StaticCollider, cellSize, kitColliders.transform);
                }, wallFaces);

                // Doors stay full GameObjects (they move). Archways split:
                // mesh -> instancer, collider -> GameObject.
                DungeonKitPlacer.BuildDoors(gen, kit, cellSize, transform, roomStyle);
                DungeonKitPlacer.BuildArchways(gen, kit, cellSize, transform, ir, roomStyle);
                DungeonKitPlacer.BuildInteriorColumns(gen, kit, cellSize, transform, ir);
                DungeonKitPlacer.BuildLadders(gen, kit, cellSize, transform, ir);

                ir.Commit(); // idempotent — bakes kit + archway instances together

                Debug.Log($"[Dungeon] Instanced: {ir.InstanceCount} pieces in {ir.BatchCount} batch group(s).");
                if (missing.Count > 0)
                    Debug.LogWarning($"[DungeonKit] Missing prefab slot(s): {string.Join(", ", missing)} — those pieces were skipped.");
            }
            else
            {
                DungeonMesher.Build(gen, cellSize, transform, wallMargin);
            }

            if (torches != null && torches.placeTorches)
                TorchPlacer.Build(gen, torches, cellSize, transform, sharedInstancer, roomStyle, wallFaces);

            if (roomStyle != null)
                RoomPropPlacer.Build(gen, kit, roomStyle, cellSize, transform, sharedInstancer, wallFaces);

            if (fog != null && fog.dynamicFogColor && roomStyle != null)
            {
                var fogGo = new GameObject("DungeonFog");
                fogGo.transform.SetParent(transform, false);
                fogGo.AddComponent<DungeonFogController>().Init(gen, roomStyle, cellSize, transform.position, fog);
            }

            // Torch/prop meshes may have been added to the instancer after its
            // first Commit — re-bake so they render.
            if (sharedInstancer != null) sharedInstancer.Commit();
        }

        void OnDrawGizmos()
        {
            // Grid bounds, always.
            Gizmos.color = boundsColor;
            Vector3 size = (Vector3)config.gridSize * cellSize;
            Gizmos.DrawWireCube(transform.position + size * 0.5f, size);

            if (gen == null) return;

            // Zone debug view: recomputed per gizmo pass (editor-only, rooms
            // are small) from the same code placement uses, so what you see
            // is exactly what RoomPropPlacer will do.
            Dictionary<Vector3Int, RoomZone> zoneMap = null;
            if (colorCellsByZone)
            {
                zoneMap = new Dictionary<Vector3Int, RoomZone>();
                foreach (var room in gen.Rooms)
                    foreach (var kv in RoomPropPlacer.ComputeZones(gen, room).Zones)
                        zoneMap[kv.Key] = kv.Value;
            }

            var grid = gen.Grid;
            for (int i = 0; i < grid.Length; i++)
            {
                CellType c = grid[i];
                if (c == CellType.Empty) continue;

                switch (c)
                {
                    case CellType.Room:
                    {
                        Vector3Int cellPos = grid.Position(i);
                        if (zoneMap != null && zoneMap.TryGetValue(cellPos, out var zone))
                            Gizmos.color = ZoneColor(zone);
                        else
                            Gizmos.color = colorRoomsByType ? RoomTypeColor(cellPos) : roomColor;
                        break;
                    }
                    case CellType.Hallway:    Gizmos.color = hallwayColor; break;
                    case CellType.StairLower:
                    case CellType.StairUpper: Gizmos.color = stairColor;   break;
                    case CellType.Prison:     Gizmos.color = prisonColor;  break;
                }

                Vector3 p = transform.position + ((Vector3)grid.Position(i) + Vector3.one * 0.5f) * cellSize;
                Gizmos.DrawCube(p, Vector3.one * 0.95f * cellSize);
            }

            // Delaunay web only at its own stage — from Graph onward it's just clutter.
            if (stage == ViewStage.Delaunay && gen.DelaunayEdges != null)
            {
                Gizmos.color = delaunayColor;
                foreach (var e in gen.DelaunayEdges)
                    DrawEdge(e);
            }

            if (stage >= ViewStage.Graph && gen.MstEdges != null)
            {
                Gizmos.color = mstColor;
                foreach (var e in gen.MstEdges)
                    DrawEdge(e);

                Gizmos.color = loopColor;
                foreach (var e in gen.LoopEdges)
                    DrawEdge(e);
            }
        }

        static Color ZoneColor(RoomZone z) => z switch
        {
            RoomZone.Entrance => new Color(0.2f, 0.9f, 0.3f),   // green
            RoomZone.Back     => new Color(0.9f, 0.25f, 0.2f),  // red
            RoomZone.Center   => new Color(0.55f, 0.55f, 0.55f),// grey
            _                 => new Color(0.25f, 0.5f, 0.95f), // Perimeter blue
        };

        Color RoomTypeColor(Vector3Int cell)
        {
            if (gen == null) return roomColor;
            foreach (var room in gen.Rooms)
                if (room.Contains(cell))
                    return room.Type switch
                    {
                        RoomType.Start      => new Color(0.2f, 0.9f, 0.3f),   // green
                        RoomType.Exit       => new Color(0.9f, 0.2f, 0.2f),   // red
                        RoomType.ThroneRoom => new Color(0.95f, 0.8f, 0.15f), // gold
                        RoomType.Merchant   => new Color(0.2f, 0.7f, 0.95f),  // cyan
                        RoomType.Barracks   => new Color(0.8f, 0.4f, 0.2f),   // rust
                        RoomType.Kitchen    => new Color(0.9f, 0.55f, 0.35f), // orange
                        RoomType.Library    => new Color(0.6f, 0.4f, 0.85f),  // purple
                        RoomType.Shrine     => new Color(0.85f, 0.85f, 0.95f),// pale
                        RoomType.ChestVault => new Color(0.95f, 0.75f, 0.5f), // tan
                        RoomType.Treasury   => new Color(1f, 0.85f, 0.1f),    // bright gold
                        RoomType.Armory     => new Color(0.7f, 0.3f, 0.15f),  // dark rust
                        RoomType.Pantry     => new Color(0.85f, 0.6f, 0.3f),  // wheat
                        RoomType.Study      => new Color(0.45f, 0.3f, 0.7f),  // deep purple
                        RoomType.Reliquary  => new Color(0.95f, 0.95f, 1f),   // white
                        _                   => new Color(0.5f, 0.5f, 0.55f),  // generic grey
                    };
            return roomColor;
        }

        void DrawEdge(DEdge e)
        {
            Vector3 a = transform.position + ((Vector3)gen.Rooms[e.A].Center + Vector3.one * 0.5f) * cellSize;
            Vector3 b = transform.position + ((Vector3)gen.Rooms[e.B].Center + Vector3.one * 0.5f) * cellSize;
            Gizmos.DrawLine(a, b);
        }
    }
}