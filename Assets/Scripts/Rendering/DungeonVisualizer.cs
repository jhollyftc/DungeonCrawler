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

        [Header("═══ SEED & LAYOUT ═══")]
        [Tooltip("Same seed + same depth = the same dungeon (golden rule 4). The live value is shown in the in-game overlay, and F1 re-randomizes it when the toggle below is on.")]
        public int seed = 12345;
        public bool randomizeSeedOnGenerate = false;
        [Tooltip("Room counts, grid size, corridor and prison/alcove/pit budgets. A DepthProfile, when assigned, DERIVES several of these from depth and wins outright — the mirrored fields here go dead (§9).")]
        public DungeonConfig config = new DungeonConfig();

        [Header("═══ RUNTIME ═══")]
        [Tooltip("Generate the dungeon on Start. REQUIRED for builds — generated content is never saved into the scene (the procedural mesh, runtime materials, and the instancer's batches don't serialize), so a build has no dungeon unless it makes one at startup.")]
        public bool generateOnStart = true;
        public bool buildMeshOnGenerate = true;

        [Header("═══ GEOMETRY ═══")]
        public GeometryMode geometryMode = GeometryMode.GeneratedMesh;
        public float cellSize = 3f;
        [Tooltip("Meters to inset the collision mesh's wall faces from the nominal grid boundary, so the invisible collider sits flush with (not behind) the kit's decorative wall relief. 0 = flush with the grid, the old behavior.")]
        public float wallMargin = 0f;
        [Tooltip("Log why KitSurface declined to pull an impact point onto the kit's real wall surface.\n\nEvery rejection is silent by design and they look identical on screen — an unrecorded face, a mesh without Read/Write Enabled, a ray that missed, and a hit deeper than the arrow's limit ALL present as 'the arrow stopped where the collider is'. This names which one it was.")]
        public bool debugSurfaceRefine = false;
        [Tooltip("The prefab kit — every piece the dungeon is built from, grouped by ORIGIN CONVENTION. Read the header comments inside before assigning offsets; the kit has two independent conventions and getting either backwards puts a piece half a cell out.")]
        public DungeonKit kit = new DungeonKit();

        [Header("═══ STYLE & ATMOSPHERE ═══")]
        [Tooltip("The whole per-space look: walls, floors, ceilings, openings and props for every room type, plus hallways, alcoves, prison cells and pits. Also the torch palette that drives fog and emissive tinting. Empty = the kit's generic pieces everywhere and uniform warm torches.")]
        public RoomStyle roomStyle;
        public TorchSettings torches = new TorchSettings();
        [Tooltip("Runtime fog color blending toward the current/approaching room's torch palette. Needs a RoomStyle and fog enabled in Lighting > Environment.")]
        public FogSettings fog = new FogSettings();

        [Tooltip("Muffles and quietens sounds with geometry between them and the listener. Applied to the runtime AudioOcclusion manager at generation, so it is authorable HERE rather than on an object that only exists in play mode — the same reason FogSettings lives here and not on DungeonFogController.")]
        public OcclusionSettings occlusion = new OcclusionSettings();

        [Tooltip("Cull radius and shadow-caster radius for the instanced geometry. Applied to the runtime DungeonInstanced renderer at generation, for the same reason as the occlusion settings above — that component is created with AddComponent every generate, so before this existed its dials were C# defaults that no inspector edit could survive.")]
        public InstancedSettings instanced = new InstancedSettings();

        public enum GeometryMode { GeneratedMesh, PrefabKit, InstancedKit }

        [Header("═══ DEBUG VIEW (gizmos only) ═══")]
        [Tooltip("Which generation stage the scene-view gizmos draw. Affects NOTHING that is generated — it is a view filter over the finished result.")]
        public ViewStage stage = ViewStage.Rooms;
        public bool colorRoomsByType = false;
        [Tooltip("Debug: color room floor cells by prop-placement zone (green = Entrance, red = Back, grey = Center, blue = Perimeter). Verifies RoomPropPlacer.ComputeZones; overrides colorRoomsByType on floor cells.")]
        public bool colorCellsByZone = false;
        public Color roomColor = new Color(0.9f, 0.25f, 0.2f, 0.9f);
        public Color hallwayColor = new Color(0.2f, 0.45f, 0.95f, 0.9f);
        public Color stairColor = new Color(0.25f, 0.85f, 0.35f, 0.9f);
        public Color prisonColor = new Color(0.85f, 0.55f, 0.15f, 0.9f);
        [Tooltip("Alcove cells. They are CellType.Hallway in the grid — the metadata list is the only thing that knows they're alcoves — so without this they are indistinguishable from corridor in the gizmo view.")]
        public Color alcoveColor = new Color(0.95f, 0.2f, 0.75f, 0.95f);
        [Tooltip("Pit interiors, and (darker) the floorless openings above them. Like alcoves these are ordinary cell types in the grid — the pit registry is the only thing that knows — so without this a chasm is invisible in the gizmo view.")]
        public Color pitColor = new Color(0.15f, 0.15f, 0.2f, 0.95f);
        [Tooltip("Bridge decks spanning a pit.")]
        public Color bridgeColor = new Color(0.8f, 0.6f, 0.25f, 0.95f);
        [Tooltip("Crawlway bores. These cells are CellType.Empty — SOLID ROCK to every system in the project — so this gizmo is the ONLY way to see one until Phase 2 places its geometry. The label reports the walk it saves.")]
        public Color crawlwayColor = new Color(0.35f, 0.95f, 0.8f, 0.95f);
        /// <summary>
        /// What the scene gizmo draws. A [Flags] mask rather than a row of bools so it is ONE
        /// multi-select dropdown in the inspector — and so "show me only X" is a single click on
        /// X rather than unticking eleven other things.
        ///
        /// Worth having because the gizmo now draws rooms, corridors, alcoves, prisons, pits,
        /// bridges, stairs, sewer networks, chambers, manholes, three kinds of graph edge and a
        /// label on most of them. All of it at once is unreadable, and the thing you are hunting
        /// is usually one layer.
        /// </summary>
        [System.Flags]
        public enum GizmoLayers
        {
            None      = 0,
            Bounds    = 1 << 0,
            Rooms     = 1 << 1,
            Hallways  = 1 << 2,
            Alcoves   = 1 << 3,
            Prisons   = 1 << 4,
            Stairs    = 1 << 5,
            Pits      = 1 << 6,
            Sewers    = 1 << 7,
            Chambers  = 1 << 8,
            Manholes  = 1 << 9,
            Edges     = 1 << 10,
            /// <summary>The floating text. Its own layer because it is by far the noisiest part
            /// and is usually the first thing you want gone.</summary>
            Labels    = 1 << 11,
            /// <summary>Areas of influence over prop selection. Its own layer because it is the
            /// only gizmo covering VOLUME rather than cells, so it swamps everything else the
            /// moment it is on. Appended at a NEW bit rather than inserted, so masks already
            /// saved on a visualizer keep meaning what they meant.</summary>
            Regions   = 1 << 12,
            /// <summary>Locked doors, portcullises, and a line from each gate to its levers —
            /// the fastest way to see whether the NEAR lever really is on the Start side.</summary>
            Gates     = 1 << 13,
            /// <summary>
            /// The floor tint showing each gate's NEAR REGION — everything still reachable from
            /// Start with that gate shut — plus the cell the reachability walk started from.
            ///
            /// SPLIT OFF FROM `Gates` BECAUSE IT COVERS THE FLOOR OF HALF THE DUNGEON and buries
            /// the gate and lever markers it sits under. Kept rather than deleted: it is the only
            /// view that answers "is the generator's idea of MY side the side I am standing on",
            /// and it is what exposed a cut test that was passing gates severing nothing. Reach
            /// for it whenever a lever appears on the wrong side.
            /// </summary>
            GateReach = 1 << 14,
            Everything = ~0,
        }

        [Tooltip("Which gizmo layers to draw. Everything at once is unreadable once a dungeon has sewers, alcoves, prisons and pits in it — turn off LABELS first, they are the noisiest, then isolate whatever you are actually hunting.")]
        public GizmoLayers gizmoLayers = GizmoLayers.Everything;

        bool Show(GizmoLayers layer) => (gizmoLayers & layer) != 0;

        public Color boundsColor = new Color(1f, 1f, 1f, 0.25f);
        public Color delaunayColor = new Color(1f, 0.85f, 0.2f, 0.8f);
        public Color mstColor = new Color(0.3f, 0.95f, 0.95f, 1f);
        public Color loopColor = new Color(0.95f, 0.35f, 0.95f, 1f);

        DungeonGenerator gen;
        public DungeonGenerator Generator => gen;

        // Every root the generator spawns under this transform. Generated
        // content is NEVER persisted in the scene (see ClearGenerated /
        // MarkNotPersisted): the procedural mesh + runtime materials + the
        // instancer's batches don't serialize, and baking ~700 objects into
        // the scene corrupts the built level0 ("Position out of bounds!").
        // The dungeon is a pure function of (seed, depth) — regenerate it,
        // don't store it.
        static readonly string[] GeneratedRoots =
        {
            "DungeonMesh", "DungeonKit", "DungeonInstanced", "DungeonTorches",
            "DungeonDoors", "DungeonArchways", "DungeonColumns", "DungeonLadders",
            "DungeonKitColliders", "DungeonProps", "DungeonHallwayProps", "DungeonFog",
            "DungeonNpcs",
            // EVERY placer's root must be listed here or its output accumulates on each
            // regenerate — F1 would stack a second dungeon's worth of props on the first.
            // "DungeonAlcoveProps" was missed when alcoves landed (already shipped);
            // "DungeonBridges" arrives with pits.
            "DungeonAlcoveProps", "DungeonPrisonProps",
            "DungeonBridges", "DungeonPitRims", "DungeonLintels",
            "DungeonKitSockets", "DungeonCrawlways", "DungeonChamberProps",
            "DungeonPortcullises", "DungeonLevers",
        };

        void Awake()
        {
            // SELF-INSTALLING, because its absence is a SILENT failure: the fog, the map and
            // the room readout all null-guard their tracker lookup, so a scene missing this
            // component loses room-aware fog and map reveal with no error anywhere. Adding it
            // here means it cannot be forgotten on a fresh scene or after a prefab rebuild.
            if (GetComponent<PlayerRoomTracker>() == null) gameObject.AddComponent<PlayerRoomTracker>();
        }

        void Start()
        {
            // REQUIRED for builds: nothing is baked into the scene, so the
            // dungeon must be generated at runtime.
            if (generateOnStart) Generate();
        }

        /// <summary>Destroys every generated root. Handles duplicates (an old
        /// transform.Find-based sweep only caught the first of a name).</summary>
        [ContextMenu("Clear Generated")]
        public void ClearGenerated()
        {
            var names = new System.Collections.Generic.HashSet<string>(GeneratedRoots);
            int removed = 0;
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (!names.Contains(child.name)) continue;
                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
                removed++;
            }
#if UNITY_EDITOR
            // Destroying objects from a script does NOT mark the scene dirty,
            // so Ctrl+S would silently no-op and the stale objects would stay
            // in the .unity file. Mark it explicitly or the clear never lands.
            if (!Application.isPlaying && removed > 0)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
                Debug.Log($"[Dungeon] Cleared {removed} generated root(s). Scene marked dirty — SAVE IT (Ctrl+S).");
            }
#endif
        }

        // Edit-mode preview must not be written into the scene file.
        void MarkNotPersisted()
        {
            if (Application.isPlaying) return;
            var names = new System.Collections.Generic.HashSet<string>(GeneratedRoots);
            foreach (Transform child in transform)
            {
                if (!names.Contains(child.name)) continue;
                foreach (var t in child.GetComponentsInChildren<Transform>(true))
                    t.gameObject.hideFlags = HideFlags.DontSaveInEditor;
            }
        }

        // Testing overrides that survive an F1/scene reload (statics outlive the
        // scene). PendingSeed pins the seed instead of randomizing; PendingDepth
        // forces a depth. Set by the dev keys in FirstPersonController, cleared
        // after they're consumed so a normal reload behaves as configured.
        public static int? PendingSeed;
        public static int? PendingDepth;

        [ContextMenu("Generate")]
        public void Generate()
        {
            // A pinned seed (PgUp/PgDn changing depth only) wins over randomize;
            // otherwise honor the inspector's randomize-on-generate.
            if (PendingSeed.HasValue) seed = PendingSeed.Value;
            else if (randomizeSeedOnGenerate) seed = Random.Range(int.MinValue, int.MaxValue);
            PendingSeed = null;

            if (PendingDepth.HasValue)
            {
                config.depth = PendingDepth.Value;
                PendingDepth = null;
            }

            if (roomStyle != null) roomStyle.InvalidateWallCache();

            // Runtime material copies — Unity won't collect these, so a regenerate would
            // leak a full set per distinct colour every F1/PgUp.
            EmissiveMaterialVariants.Clear();

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
                      $"{gen.Stairs.Count / 4} staircases, {gen.Prisons.Count} prison cells, " +
                      $"{gen.Alcoves.Count} alcoves, {gen.Pits.Count} pits, {gen.Crawlways.Count} crawlways | " +
                      $"types: {string.Join(", ", typeSummary)}");

            if (buildMeshOnGenerate)
                BuildMesh();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Push InstancedSettings to the live renderer whenever the inspector changes.
        ///
        /// WITHOUT THIS THE DIALS ONLY LAND AT GENERATION, which is fine for `renderDistance`
        /// and actively bad for a diagnostic: measuring one Mesh LOD level against another meant
        /// regenerating between readings, and a value typed but not regenerated reads as "the
        /// setting does nothing" — the exact failure mode this whole investigation kept hitting.
        /// Editor-only; at runtime generation is still the one place settings are applied.
        /// </summary>
        void OnValidate()
        {
            foreach (var ir in GetComponentsInChildren<InstancedDungeonRenderer>(true))
                instanced.ApplyTo(ir);
        }
#endif

        /// Forwarded from the runtime renderer, which is created by BuildMesh and so cannot be
        /// found in the scene until a dungeon exists — the reason the report looked like it was
        /// logging nowhere.
        [ContextMenu("Log Geometry Cost Report")]
        public void LogGeometryCostReport()
        {
            // ALL of them, not the first. ClearGenerated uses deferred Destroy in play mode, so
            // during a regenerate the previous generation's renderer is still alive — and
            // GetComponentInChildren would hand back whichever sits first in hierarchy order,
            // which is not reliably the live one. Reporting every candidate with its batch count
            // makes "found nothing" and "found the wrong one" distinguishable instead of both
            // arriving as an empty report.
            var found = GetComponentsInChildren<InstancedDungeonRenderer>(true);
            if (found.Length == 0)
            {
                Debug.LogWarning($"[Instanced] No DungeonInstanced renderer under this visualizer. " +
                                 $"geometryMode is {geometryMode} (the renderer only exists in " +
                                 $"InstancedKit), isPlaying={Application.isPlaying}.", this);
                return;
            }

            InstancedDungeonRenderer live = found[0];
            var sb = new System.Text.StringBuilder();
            foreach (var c in found)
            {
                sb.Append($"\n  {c.gameObject.name}: {c.BatchCount} batches, {c.InstanceCount} instances");
                if (c.BatchCount > live.BatchCount) live = c;
            }
            Debug.LogWarning($"[Instanced] {found.Length} renderer(s), geometryMode={geometryMode}, " +
                             $"isPlaying={Application.isPlaying}:{sb}", this);

            live.LogGeometryCostReport();
        }

        /// <summary>
        /// Names of content passes that threw during the last BuildMesh. Empty is the normal case.
        /// Read by the dev overlay, because an error in the Console is one you meet again in six
        /// months on a different seed.
        /// </summary>
        public readonly List<string> FailedPasses = new List<string>();

        /// <summary>
        /// Run one content pass so that a throw inside it cannot delete every pass after it.
        ///
        /// WHY THIS EXISTS: `GatePlacer` NullReferenced on a seed where two gates resolved to one
        /// door, which aborted BuildMesh partway — so torches and all three prop passes silently
        /// never ran. The visible symptom was "props stopped generating", which is about as far
        /// from the cause as a symptom gets, and it cost hours. In a BUILD the same throw is a
        /// silent content failure with no Console to read.
        ///
        /// PLACERS ONLY, NEVER `DungeonGenerator.Generate()`. The generator decides rooms,
        /// corridors, doors and connectivity, and a half-built grid is genuinely corrupt — an
        /// unreachable Exit, or gates whose reachability invariant (§4) was computed against half
        /// a dungeon. Continuing there would produce a dungeon that is subtly wrong instead of
        /// obviously broken, which is worse. Placers are additive, own their own roots, and
        /// already follow a degrade-gracefully philosophy everywhere else (§7).
        ///
        /// PER PASS, NOT ONE BLOCK AROUND ALL OF THEM — one try/catch would just move the
        /// boundary, and a gate throw would still take out the prop passes behind it.
        ///
        /// Known and accepted: a pass that throws midway may leave shared state part-written (a
        /// half-claimed WallFaceRegistry, partial occupancy). That is §12's decide-before-mutating
        /// shape, but those are cosmetic inconsistencies — connectivity lives in the generator,
        /// which this never wraps.
        /// </summary>
        void RunPass(string passName, System.Action pass)
        {
            try { pass(); }
            catch (System.Exception e)
            {
                FailedPasses.Add(passName);
                Debug.LogError(
                    $"[Dungeon] PASS FAILED: {passName} — seed {seed}, depth {config.depth}.\n" +
                    $"Later passes STILL RAN, so this dungeon is missing only what {passName} " +
                    $"places. Set that seed and depth to reproduce it exactly.\n{e}", this);
            }
        }

        [ContextMenu("Build Mesh")]
        public void BuildMesh()
        {
            if (gen == null)
            {
                Generate(); // recompile may have cleared the generator
                if (buildMeshOnGenerate) return; // Generate already built the mesh
            }

            // Replace any previous geometry (any mode), torches, props, fog.
            ClearGenerated();
            FailedPasses.Clear();

            // MAY A CRAWLWAY MOUTH SUPPRESS ITS WALL? Only if something will replace it.
            // Opening a mouth removes the greybox's whole 3m face (a quad is all-or-nothing),
            // and the ring of collision around the 1.5m bore lives on the mouth PREFAB — so with
            // no prefab, or in the pure-greybox debug mode where no kit piece is placed at all,
            // suppressing would leave a literal hole in the world. Decided ONCE here, where both
            // halves of the question are visible, and read by the mesher and the kit placer
            // through the generator so they cannot disagree (§5).
            // EITHER mouth slot counts. Checking only the plain array would leave a kit whose
            // mouths live entirely in the weighted variants list rendering its grates against an
            // unsuppressed wall — the feature silently half-on, which is the exact failure this
            // gate exists to prevent.
            gen.CrawlwayGeometryAvailable =
                geometryMode != GeometryMode.GeneratedMesh && kit != null &&
                ((kit.crawlwayMouthPrefabs != null && kit.crawlwayMouthPrefabs.Length > 0) ||
                 (kit.crawlwayMouthVariants != null && kit.crawlwayMouthVariants.Length > 0));

            InstancedDungeonRenderer sharedInstancer = null;

            // BUILT REGARDLESS OF GEOMETRY MODE, because both consumers need it and only one of
            // them is mode-specific: the instanced renderer exists in InstancedKit alone, while
            // DungeonRendererCulling manages doors, FullGameObject props and carryables in every
            // mode. Created HERE so it exists before anything registers with it — cells are
            // resolved once at registration, and a consumer handed this late would have everything
            // tagged -1, which fails open and shows up as occlusion culling quietly doing nothing.
            var visibility = new DungeonVisibility(gen, cellSize, transform.position)
            {
                maxSteps = Mathf.CeilToInt(instanced.renderDistance / Mathf.Max(0.01f, cellSize)) + 2
            };

            // Per-face restrictions from RoomStyle.WallAsset flags. Filled by
            // the kit placer as walls emit, queried by the torch and prop
            // placers below (empty in GeneratedMesh mode = no restrictions).
            var wallFaces = new WallFaceRegistry();

            // Kit pieces that carry PropSockets, recorded as they emit and filled by
            // KitSocketPlacer below. Collected rather than spawned inline because `place`
            // cannot express PropTier — see DungeonKitPlacer.SocketSite.
            var socketSites = new List<DungeonKitPlacer.SocketSite>();

            // WALL pieces and where they landed, so KitSurface can ray-test their real
            // triangles after an impact lands on the greybox plane in front of them. Filled
            // by the kit placer, installed once below. Cleared here so a mode with no kit
            // leaves no stale map from the previous generate pointing at destroyed meshes.
            var surfaceSites = new List<DungeonKitPlacer.SurfaceSite>();
            KitSurface.Clear();

            if (geometryMode == GeometryMode.PrefabKit)
            {
                DungeonKitPlacer.Build(gen, kit, cellSize, transform, roomStyle, wallFaces, socketSites);
                DungeonKitPlacer.BuildDoors(gen, kit, cellSize, transform, roomStyle);
                DungeonKitPlacer.BuildArchways(gen, kit, cellSize, transform, null, roomStyle);
                DungeonKitPlacer.BuildInteriorColumns(gen, kit, cellSize, transform);
                DungeonKitPlacer.BuildLadders(gen, kit, cellSize, transform);
                DungeonKitPlacer.BuildBridges(gen, kit, cellSize, transform);
                DungeonKitPlacer.BuildPitRims(gen, kit, cellSize, transform);
                DungeonKitPlacer.BuildLintels(gen, kit, cellSize, transform, roomStyle);
                DungeonKitPlacer.BuildCrawlways(gen, kit, cellSize, transform);
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
                // BEFORE any placer runs. AddInstance resolves each instance's grid cell through
                // this, so a renderer that gets it late has a dungeon's worth of instances already
                // tagged -1 — which fails OPEN, so the symptom would be occlusion culling quietly
                // doing nothing rather than anything visibly wrong.
                ir.visibility = visibility;
                // BEFORE anything else touches it. AddComponent runs Awake synchronously, and
                // more to the point this component is rebuilt on every generate — so without
                // this push its dials are C# defaults and an inspector edit lasts until the
                // next F1. Same shape as the occlusion and fog settings above.
                instanced.ApplyTo(ir);
                sharedInstancer = ir;

                // Holds the collider GameObjects for mesh-instanced pieces that
                // still need real collision (stairs, corner pillars) — the
                // greybox doesn't provide it for these. Mirrors the split
                // archways/columns already use (mesh -> instancer, collider ->
                // GameObject), just routed through Enumerate's second sink.
                var kitColliders = new GameObject("DungeonKitColliders");
                kitColliders.transform.SetParent(transform, false);

                var missing = new System.Collections.Generic.HashSet<string>();
                DungeonKitPlacer.Enumerate(gen, kit, missing, (prefab, posCells, rot, offset, cell) =>
                {
                    var m = Matrix4x4.TRS(posCells * cellSize + offset + transform.position, rot, Vector3.one);

                    // Per-room emissive tint. The piece's OWNING CELL is why PlaceCallback
                    // carries one: a wall's posCells sits on the face between two cells and
                    // can't be floored back to its owner reliably. Same rule as fog and the
                    // flame VFX — the room's torch palette owns the hue, so a shrine's
                    // candles can't drift away from its torches. Costs one extra batch per
                    // distinct colour; a cached variant per colour is what keeps it bounded.
                    Material tint = null;
                    if (kit.emissiveMaterial != null && roomStyle != null)
                    {
                        EmissiveMaterialVariants.debugLog = kit.debugEmissive;
                        Room r = gen.RoomAt(cell);
                        // HUE ONLY — kit.emissiveIntensity is the brightness dial for glowing
                        // kit pieces; the palette's own HDR magnitude must not multiply into it.
                        Color c = RoomStyle.Hue(r != null ? roomStyle.For(r.Type).torchColor
                                                          : roomStyle.defaultTorchColor);
                        tint = EmissiveMaterialVariants.Get(
                            kit.emissiveMaterial, c * kit.emissiveIntensity, kit.emissiveProperty);
                    }

                    // The static shell (walls/floors/ceilings) casts NO shadows
                    // — wall-on-wall shadows are invisible, but thousands of
                    // shell instances redrawn into every shadowed torch's six
                    // cubemap faces were THE torch-shadow performance killer.
                    // The shell still receives, so detail shadows (columns,
                    // arches, props — the placeWithCollider sink below and
                    // PropInstancer paths, which keep casting) fall across it.
                    ir.AddInstance(prefab, m, castShadows: false, replaceMat: kit.emissiveMaterial, withMat: tint);
                }, roomStyle, (prefab, posCells, rot, offset, cell) =>
                {
                    Vector3 worldPos = posCells * cellSize + offset + transform.position;
                    PropInstancer.PlaceProps(ir, prefab,
                        new[] { new PropPlacement { position = worldPos, rotation = rot } },
                        PropTier.StaticCollider, cellSize, kitColliders.transform);
                }, wallFaces, socketSites, surfaceSites);

                // The GREYBOX collider is passed deliberately: a refinement only ever follows a
                // hit on the shell, so an arrow that struck a crate standing against a wall is
                // never dragged through the crate into the masonry behind it.
                KitSurface.debug = debugSurfaceRefine;
                KitSurface.Install(surfaceSites, cellSize, transform.position,
                                   collision != null ? collision.GetComponent<Collider>() : null);

                // Doors stay full GameObjects (they move). Archways split:
                // mesh -> instancer, collider -> GameObject.
                RunPass("Doors", () => DungeonKitPlacer.BuildDoors(gen, kit, cellSize, transform, roomStyle));
                RunPass("Archways", () => DungeonKitPlacer.BuildArchways(gen, kit, cellSize, transform, ir, roomStyle));
                RunPass("InteriorColumns", () => DungeonKitPlacer.BuildInteriorColumns(gen, kit, cellSize, transform, ir));
                RunPass("Ladders", () => DungeonKitPlacer.BuildLadders(gen, kit, cellSize, transform, ir));
                RunPass("Bridges", () => DungeonKitPlacer.BuildBridges(gen, kit, cellSize, transform, ir));
                RunPass("PitRims", () => DungeonKitPlacer.BuildPitRims(gen, kit, cellSize, transform, ir));
                RunPass("Lintels", () => DungeonKitPlacer.BuildLintels(gen, kit, cellSize, transform, roomStyle, ir));
                RunPass("Crawlways", () => DungeonKitPlacer.BuildCrawlways(gen, kit, cellSize, transform, ir));

                ir.Commit(); // idempotent — bakes kit + archway instances together

                Debug.Log($"[Dungeon] Instanced: {ir.InstanceCount} pieces in {ir.BatchCount} batch group(s).");
                if (missing.Count > 0)
                    Debug.LogWarning($"[DungeonKit] Missing prefab slot(s): {string.Join(", ", missing)} — those pieces were skipped.");
            }
            else
            {
                DungeonMesher.Build(gen, cellSize, transform, wallMargin);
            }

            // KIT SOCKETS FIRST of all the content passes: a socket torch must claim its face
            // AND enter TorchPlacer's spacing buckets before any computed torch is chosen, or a
            // deliberately-placed sconce gets a computed twin a metre away.
            if (roomStyle != null)
                RunPass("KitSockets", () => KitSocketPlacer.Build(gen, kit, socketSites, cellSize, transform, sharedInstancer, roomStyle, wallFaces, torches));

            // ONE PACKING FOR THE WHOLE DUNGEON, NOT ONE PER PASS. Sub-cell décor arbitration
            // (§8) has to span the recess → room → hallway order below, because a corridor prop
            // routinely lands in the cell next to a room prop and the boundary between them is
            // the one place an overlap would be noticed. Rebuilt per generate, so a regenerate
            // starts empty; the packing ORDER is simply the pass order, and reordering these
            // passes for any other reason will shuffle packed décor.
            var packing = new DecorPacking();

            // RECESSES next, before torches. §8's most-constrained-first rule taken to its
            // conclusion: an alcove or prison cell has about three wall faces and one authored
            // hero prop, so it is the tightest consumer of wall real estate in the dungeon and
            // must claim its face before a sconce can take it (prisons get torches too, when
            // TorchSettings.torchesInPrisons is on). Everything after this honours those claims.
            if (roomStyle != null)
            {
                RunPass("AlcoveProps", () => RecessPropPlacer.BuildAlcoves(gen, roomStyle, cellSize, transform, sharedInstancer, wallFaces, packing));
                RunPass("PrisonProps", () => RecessPropPlacer.BuildPrisons(gen, roomStyle, cellSize, transform, sharedInstancer, wallFaces, packing));
                RunPass("ChamberProps", () => RecessPropPlacer.BuildChambers(gen, roomStyle, cellSize, transform, sharedInstancer, wallFaces, packing));
            }

            // GATES BEFORE TORCHES, for the same reason recesses are: a lever occupies a wall
            // face and must CLAIM it, or a sconce mounts over the one thing the player is meant
            // to hunt for. It also needs the doors to exist, which BuildDoors has already done.
            RunPass("Gates", () => GatePlacer.Build(gen, kit, cellSize, transform, wallFaces));

            if (torches != null && torches.placeTorches)
                RunPass("Torches", () => TorchPlacer.Build(gen, torches, cellSize, transform, sharedInstancer, roomStyle, wallFaces));

            if (roomStyle != null)
            {
                RunPass("RoomProps", () => RoomPropPlacer.Build(gen, kit, roomStyle, cellSize, transform, sharedInstancer, wallFaces, packing));
                RunPass("HallwayProps", () => HallwayPropPlacer.Build(gen, roomStyle, cellSize, transform, sharedInstancer, wallFaces, packing));
                // Silent per item, deliberately (it is décor), but NOT silent in aggregate —
                // an entry whose clearance radius is far too large for the space would place
                // almost nothing and read as an authoring value that "does nothing".
                packing.LogSummary();
            }

            // Push the authored occlusion settings into the runtime manager. Done at
            // generation rather than at Awake because the manager auto-installs on first USE
            // (a torch registering, an impact playing), which may be either side of Awake
            // depending on what happens first — asking for it here creates it if it doesn't
            // exist yet and reconfigures it if it does, so a regenerate re-applies changes.
            if (occlusion != null && Application.isPlaying) occlusion.ApplyTo(AudioOcclusion.Manager);

            if (fog != null && fog.dynamicFogColor && roomStyle != null)
            {
                var fogGo = new GameObject("DungeonFog");
                fogGo.transform.SetParent(transform, false);
                fogGo.AddComponent<DungeonFogController>().Init(gen, roomStyle, cellSize, transform.position, fog, GetComponent<PlayerRoomTracker>());
            }

            // Torch/prop meshes may have been added to the instancer after its
            // first Commit — re-bake so they render.
            if (sharedInstancer != null) sharedInstancer.Commit();

            // NavMesh LAST: it bakes off the physics colliders placed above
            // (greybox shell, stairs, pillars, columns), so everything walkable
            // must already exist. Optional component — no baker, no NPCs.
            GetComponent<DungeonNavBaker>()?.Rebuild(gen);

            // AFTER every placer, because it collects what they produced. Rebuilt per generate
            // rather than kept across one: ClearGenerated destroys the old roots, so a list held
            // over would be full of nulls and missing everything new.
            var rendererCulling = GetComponent<DungeonRendererCulling>();
            if (rendererCulling != null)
            {
                // BEFORE Rebuild, not after: Rebuild resolves each renderer's cell through this,
                // so assigning it afterwards leaves a whole dungeon's worth tagged -1 — which
                // fails open, so the symptom is occlusion silently doing nothing rather than
                // anything visibly wrong. Same ordering trap as the instanced renderer's.
                rendererCulling.visibility = visibility;
                rendererCulling.Rebuild();
            }

            // Keep the edit-mode preview out of the saved scene.
            MarkNotPersisted();

            // ONE SUMMARY LINE AT THE END, on top of each pass's own error. A throw scrolls out
            // of the Console behind everything the later passes log, and this dungeon is now
            // MISSING CONTENT while looking broadly fine — the state that has to be impossible
            // to walk past. The overlay carries it too, for the same reason.
            if (FailedPasses.Count > 0)
                Debug.LogError($"[Dungeon] {FailedPasses.Count} pass(es) FAILED on seed {seed} " +
                               $"depth {config.depth}: {string.Join(", ", FailedPasses)}. This " +
                               "dungeon is missing their content — scroll up for the stack traces.", this);
        }

        void OnDrawGizmos()
        {
            if (Show(GizmoLayers.Bounds))
            {
                Gizmos.color = boundsColor;
                Vector3 size = (Vector3)config.gridSize * cellSize;
                Gizmos.DrawWireCube(transform.position + size * 0.5f, size);
            }

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

                Vector3Int cellPos = grid.Position(i);
                bool draw = true;

                switch (c)
                {
                    case CellType.Room:
                    {
                        // Pits first: an opening and its interior are both CellType.Room, so the
                        // registry is the only way to see a chasm at all. Which also means the
                        // PITS layer has to be tested here rather than as its own CellType case.
                        bool isPit = gen.IsBridgeCell(cellPos) || gen.IsPitOpening(cellPos) ||
                                     gen.PitAt(cellPos) != null;
                        draw = isPit ? Show(GizmoLayers.Pits) : Show(GizmoLayers.Rooms);
                        if (!draw) break;

                        if (gen.IsBridgeCell(cellPos)) { Gizmos.color = bridgeColor; break; }
                        if (gen.IsPitOpening(cellPos)) { Gizmos.color = pitColor * 0.6f; break; }
                        if (gen.PitAt(cellPos) != null) { Gizmos.color = pitColor; break; }
                        if (zoneMap != null && zoneMap.TryGetValue(cellPos, out var zone))
                            Gizmos.color = ZoneColor(zone);
                        else
                            Gizmos.color = colorRoomsByType ? RoomTypeColor(cellPos) : roomColor;
                        break;
                    }
                    // Alcove cells ARE Hallway cells — the metadata list is the only thing that
                    // distinguishes them — so this has to be a lookup, not a CellType case. Same
                    // for sewer chambers, which are typed Hallway for exactly the same reason.
                    case CellType.Hallway:
                    {
                        bool isAlcove = gen.IsAlcoveCell(cellPos);
                        bool isChamber = gen.IsChamberCell(cellPos);
                        draw = isChamber ? Show(GizmoLayers.Chambers)
                             : isAlcove  ? Show(GizmoLayers.Alcoves)
                                         : Show(GizmoLayers.Hallways);
                        Gizmos.color = isAlcove ? alcoveColor : isChamber ? crawlwayColor : hallwayColor;
                        break;
                    }
                    case CellType.StairLower:
                    case CellType.StairUpper:
                        draw = Show(GizmoLayers.Stairs);
                        Gizmos.color = stairColor;
                        break;
                    case CellType.Prison:
                        draw = Show(GizmoLayers.Prisons);
                        Gizmos.color = prisonColor;
                        break;
                }

                if (!draw) continue;

                Vector3 p = transform.position + ((Vector3)cellPos + Vector3.one * 0.5f) * cellSize;
                Gizmos.DrawCube(p, Vector3.one * 0.95f * cellSize);
            }

            // Delaunay web only at its own stage — from Graph onward it is just clutter.
            if (Show(GizmoLayers.Edges) && stage == ViewStage.Delaunay && gen.DelaunayEdges != null)
            {
                Gizmos.color = delaunayColor;
                foreach (var e in gen.DelaunayEdges)
                    DrawEdge(e);
            }

            if (Show(GizmoLayers.Edges) && stage >= ViewStage.Graph && gen.MstEdges != null)
            {
                Gizmos.color = mstColor;
                foreach (var e in gen.MstEdges)
                    DrawEdge(e);

                Gizmos.color = loopColor;
                foreach (var e in gen.LoopEdges)
                    DrawEdge(e);
            }

            // Alcove mouths: a line from the corridor cell you turn off, into the recess, plus
            // the kind as a label. The DIRECTION is worth seeing — it's what orients the hero
            // prop, so a statue facing the wrong way is a direction bug, not a prop bug.
            if (Show(GizmoLayers.Alcoves) && gen.Alcoves != null && gen.Alcoves.Count > 0)
            {
                Gizmos.color = alcoveColor;
                foreach (var a in gen.Alcoves)
                {
                    Vector3 from = transform.position + ((Vector3)a.HallCell + Vector3.one * 0.5f) * cellSize;
                    Vector3 to = transform.position + ((Vector3)a.MouthCell + Vector3.one * 0.5f) * cellSize;
                    Gizmos.DrawLine(from, to);
                    Gizmos.DrawWireSphere(to, cellSize * 0.18f);
#if UNITY_EDITOR
                    UnityEditor.Handles.color = alcoveColor;
                    if (Show(GizmoLayers.Labels))
                    UnityEditor.Handles.Label(to + Vector3.up * cellSize * 0.4f,
                                              $"{a.Kind} {a.Width}x{a.Depth}{(a.IsEnterable ? "" : " (shallow)")}");
#endif
                }
            }

            // Crawlways. A bore is CellType.Empty, i.e. indistinguishable from solid rock in
            // every other view, so this is the only way to see one at all before its geometry
            // exists. Cubes for the bored cells, a fatter marker at each mouth, and the DETOUR
            // RATIO in the label — that number is the entire justification for the crawlway
            // being there, so it belongs where you can read it while tuning.
            if (gen.Crawlways != null && gen.Crawlways.Count > 0)
            {
                Gizmos.color = crawlwayColor;
                foreach (var cw in gen.Crawlways)
                {
                    Vector3 Centre(Vector3Int c) =>
                        transform.position + ((Vector3)c + Vector3.one * 0.5f) * cellSize;

                    // The tube is floor-aligned, not centred (a centred bore's sill exceeds
                    // maxStepHeight and the player could not climb in), so draw it low in the
                    // cell where it actually sits rather than at the cell's middle.
                    Vector3 Bore(Vector3Int c) => Centre(c) - Vector3.up * cellSize * 0.25f;

                    // A GRAPH, so draw the edges rather than a route. Each bore cell links to the
                    // neighbours its mask reports, which is also a live check on the thing piece
                    // selection depends on — a tunnel that looks disconnected here will render
                    // with the wrong tube pieces.
                    if (Show(GizmoLayers.Sewers))
                    foreach (var c in cw.Cells)
                    {
                        Vector3 p = Bore(c);
                        Gizmos.DrawWireCube(p, Vector3.one * cellSize * 0.5f);
                        int mask = cw.NeighbourMask(c);
                        for (int bit = 0; bit < 4; bit++)
                            if ((mask & (1 << bit)) != 0)
                                Gizmos.DrawLine(p, Bore(c + CrawlwaySpec.DirOfBit(bit)));
                    }

                    if (Show(GizmoLayers.Sewers))
                    foreach (var m in cw.Mouths)
                    {
                        Gizmos.DrawWireSphere(Centre(m.OpenCell), cellSize * 0.22f);
                        Gizmos.DrawLine(Centre(m.OpenCell), Bore(m.BoreCell));
                    }

                    // Manholes — drawn as a vertical drop, since that is what distinguishes them
                    // from a mouth: you go DOWN and cannot come back up the way you came.
                    if (Show(GizmoLayers.Manholes))
                    foreach (var mh in cw.Manholes)
                    {
                        Gizmos.DrawLine(Centre(mh.OpenCell), Bore(mh.BoreCell));
                        Gizmos.DrawWireCube(Centre(mh.OpenCell), new Vector3(1f, 0.05f, 1f) * cellSize * 0.7f);
                    }

                    // Sewer chambers. Their cells ARE typed Hallway, so without this they read as
                    // ordinary corridor in the gizmo view — the same reason alcoves need a colour.
                    if (Show(GizmoLayers.Chambers))
                    foreach (var ch in cw.Chambers)
                    {
                        // One line per grate — a chamber with two or three is a route through,
                        // and that is exactly what you want to see at a glance.
                        foreach (var op in ch.Openings)
                            Gizmos.DrawLine(Bore(op.BoreCell), Centre(op.ChamberCell));
                        foreach (var c in ch.Cells)
                            Gizmos.DrawWireCube(Centre(c), Vector3.one * cellSize * 0.8f);
                    }
#if UNITY_EDITOR
                    UnityEditor.Handles.color = crawlwayColor;
                    int chamberCells = 0;
                    foreach (var ch in cw.Chambers) chamberCells += ch.Cells.Count;
                    Vector3 label = cw.Mouths.Count > 0
                        ? Centre(cw.Mouths[0].OpenCell)
                        : transform.position + (cw.CenterCell + Vector3.one * 0.5f) * cellSize;
                    if (Show(GizmoLayers.Labels))
                    UnityEditor.Handles.Label(label + Vector3.up * cellSize * 0.4f,
                        $"sewer: {cw.Cells.Count} cells, {cw.Mouths.Count} mouth(s), " +
                        $"{cw.Manholes.Count} manhole(s), " +
                        $"{cw.Chambers.Count} chamber(s) ({chamberCells} cells)" +
                        $"{(cw.BestDetour > 0 ? $" — longest walk it short-circuits: {cw.BestDetour}" : "")}");
#endif
                }
            }

            // ---- Gates: locked doors, portcullises, and the levers that drive them ----
            //
            // THE LINE FROM A GATE TO ITS LEVERS IS THE POINT. The softlock invariant is that the
            // NEAR lever sits in the component still reachable from Start with the gate shut, and
            // that is invisible in numbers but obvious as a line — you can see at a glance
            // whether it runs back toward Start or off into the region the gate cuts off.
            if (Show(GizmoLayers.Gates) && gen.Gates != null)
            {
                foreach (var g in gen.Gates)
                {
                    bool bars = g.Kind == GateKind.Portcullis;
                    Color c = bars ? new Color(0.95f, 0.75f, 0.2f) : new Color(0.95f, 0.35f, 0.35f);
                    Vector3 at = transform.position + (g.Cell + Vector3.one * 0.5f) * cellSize;

                    Gizmos.color = c;
                    Gizmos.DrawWireCube(at, Vector3.one * cellSize * 0.85f);

                    // THE NEAR REGION, drawn flat. This is the generator's answer to "which side
                    // is yours with the gate shut", and it is the single thing that decides
                    // whether a gate is openable or a softlock — yet it is invisible in the log,
                    // which reports "near 1" whether the judgement was right or wrong. Stand in
                    // the dungeon and look: if the tinted floor is NOT the side you are on, the
                    // reachability walk is wrong, not the labelling.
                    //
                    // The ORIGIN is marked too, because a near side computed from the wrong start
                    // cell is perfectly self-consistent and the region alone cannot show it.
                    if (Show(GizmoLayers.GateReach) && g.NearCells != null && g.NearCells.Count > 0)
                    {
                        Gizmos.color = new Color(c.r, c.g, c.b, 0.12f);
                        foreach (var nc in g.NearCells)
                            Gizmos.DrawCube(transform.position + (nc + Vector3.one * 0.5f) * cellSize,
                                            new Vector3(0.85f, 0.05f, 0.85f) * cellSize);

                        Gizmos.color = new Color(0.3f, 1f, 0.4f);
                        Vector3 o = transform.position + (g.ReachOrigin + Vector3.one * 0.5f) * cellSize;
                        Gizmos.DrawWireSphere(o, cellSize * 0.45f);
                        Gizmos.DrawLine(o, o + Vector3.up * cellSize);
                    }

                    // PLACED LEVERS ONLY GET A LINE. The generator offers several CANDIDATE faces
                    // per side — it cannot see which wall asset the kit will land on one — so
                    // most entries here were never built. Drawing them all identically was
                    // actively misleading: you follow a line to a cell, find bare wall, and
                    // conclude the lever failed when it simply went to another candidate.
                    int placedNear = 0, placedFar = 0;
                    foreach (var lv in g.Levers)
                    {
                        Vector3 lp = transform.position + (lv.Cell + Vector3.one * 0.5f) * cellSize;

                        if (!lv.Placed)
                        {
                            // A candidate that lost. Tiny and dim — visible when you are hunting
                            // for why a lever went where it did, invisible the rest of the time.
                            Gizmos.color = new Color(c.r, c.g, c.b, 0.18f);
                            Gizmos.DrawWireCube(lp, Vector3.one * cellSize * 0.15f);
                            continue;
                        }

                        if (lv.NearSide) placedNear++; else placedFar++;
                        // Near solid, far dimmed — so which side is which reads without a label.
                        Gizmos.color = lv.NearSide ? c : new Color(c.r, c.g, c.b, 0.35f);
                        Gizmos.DrawLine(at, lp);
                        Gizmos.DrawWireSphere(lp, cellSize * (lv.NearSide ? 0.3f : 0.22f));
                    }

#if UNITY_EDITOR
                    if (Show(GizmoLayers.Labels))
                    {
                        UnityEditor.Handles.color = c;
                        // PLACED counts, not candidate counts — "2 levers" meaning two of eight
                        // faces got one is the number worth reading. A near count of 0 on a gate
                        // that still exists is a bug worth seeing immediately.
                        UnityEditor.Handles.Label(at + Vector3.up * cellSize * 0.5f,
                            $"{g.Label} — near {placedNear}, far {placedFar} " +
                            $"({g.Levers.Count} candidate faces), {g.DepthFromStart} from Start");
                    }
#endif
                }
            }

            // ---- Regions: areas of influence over prop selection ----
            //
            // DRAWN AS AN ELLIPSOID, NOT A SPHERE, and that is the whole reason this is worth
            // having. The distance metric multiplies dy by regionYScale, so a region reaches
            // `radius` cells horizontally but only `radius / YScale` STOREYS vertically — a
            // wire sphere would draw a lie, and the vertical extent is the part that is
            // genuinely hard to reason about from the numbers.
            //
            // Two shells: the outer one is the hard cutoff where influence hits zero, the inner
            // one is where it falls to HALF. The gap between them is the honest picture of
            // falloffPower — a high power puts the half-shell close to the centre and most of
            // the volume is barely influenced, which is exactly the tuning trap the default used
            // to walk into.
            if (Show(GizmoLayers.Regions) && gen.Regions != null && gen.Regions.Any)
            {
                float ys = Mathf.Max(0.01f, gen.Regions.YScale);
                for (int i = 0; i < gen.Regions.Sites.Count; i++)
                {
                    RegionSite s = gen.Regions.Sites[i];
                    Color c = s.Definition != null ? s.Definition.gizmoColor : Color.green;

                    Vector3 centre = transform.position
                                   + (s.Cell + Vector3.one * 0.5f) * cellSize;

                    // Squash Y by the same factor the metric exaggerates it by, so the drawn
                    // shape is the set of cells the field actually reaches.
                    Matrix4x4 prev = Gizmos.matrix;
                    Gizmos.matrix = Matrix4x4.TRS(centre, Quaternion.identity,
                                                  new Vector3(1f, 1f / ys, 1f));

                    Gizmos.color = new Color(c.r, c.g, c.b, 0.55f);
                    Gizmos.DrawWireSphere(Vector3.zero, s.Radius * cellSize);

                    float power = s.Definition != null ? s.Definition.falloffPower : 2f;
                    float strength = s.Definition != null ? s.Definition.strength : 1f;
                    // Where influence == 0.5: (1-t)^power * strength = 0.5
                    float halfT = 1f - Mathf.Pow(Mathf.Clamp01(0.5f / Mathf.Max(0.0001f, strength)),
                                                 1f / Mathf.Max(0.0001f, power));
                    if (halfT > 0.01f)
                    {
                        Gizmos.color = new Color(c.r, c.g, c.b, 0.9f);
                        Gizmos.DrawWireSphere(Vector3.zero, s.Radius * halfT * cellSize);
                    }

                    Gizmos.matrix = prev;

                    Gizmos.color = c;
                    Gizmos.DrawWireCube(centre, Vector3.one * cellSize * 0.6f);

#if UNITY_EDITOR
                    if (Show(GizmoLayers.Labels))
                    {
                        UnityEditor.Handles.color = c;
                        UnityEditor.Handles.Label(centre + Vector3.up * cellSize * 0.6f,
                            $"{(s.Definition != null ? s.Definition.Label : "?")}\n" +
                            $"r {s.Radius:0.#} cells / {s.Radius / ys:0.#} floors\n" +
                            $"half at {s.Radius * halfT:0.#} (power {power:0.##}, strength {strength:0.##})");
                    }
#endif
                }
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