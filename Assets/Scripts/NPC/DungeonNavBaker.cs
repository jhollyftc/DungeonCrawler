using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace DungeonGen
{
    /// <summary>
    /// Runtime NavMesh for the generated dungeon, plus NPC spawning.
    ///
    /// Every NavMesh tutorial bakes in the editor for a static scene — we can't:
    /// the dungeon is generated at runtime and regenerated on F1/PgUp/PgDn, so
    /// the walkable surface must be REBUILT after every generation. This sits on
    /// the DungeonVisualizer, which calls Rebuild() at the end of BuildMesh().
    ///
    /// The surface collects PHYSICS COLLIDERS from the visualizer's children —
    /// i.e. exactly the project's collision truth: the invisible greybox shell,
    /// the stair/pillar prefab colliders, columns, ladders. NOT the instanced
    /// visuals (which have no GameObjects to collect anyway). Player and NPCs
    /// therefore walk the same surface by construction.
    ///
    /// DOORS are the one exception: a PhysicsDoor is dynamic — baked solid it
    /// would permanently wall off its doorway in the nav data even when it swings
    /// open. So door colliders are temporarily disabled during the bake, leaving
    /// doorways OPEN in the surface; the physical door still blocks/yields at
    /// runtime, and an NPC walking into it shoves it open exactly like the player
    /// does — which is true because NpcLocomotion drives a CharacterController,
    /// so CharacterControllerPhysicsPush runs on NPCs verbatim. (A bare
    /// NavMeshAgent would ghost straight through; see NpcLocomotion.) Same logic applies to
    /// props under the excluded roots' carryables — for v1 the placed blocking
    /// props ARE baked (they're placed behind the flood-fill, so they never seal
    /// a route), and a barrel the player later moves just leaves slightly stale
    /// nav, which is acceptable until it isn't.
    /// </summary>
    [RequireComponent(typeof(DungeonVisualizer))]
    public class DungeonNavBaker : MonoBehaviour
    {
        [Header("Bake")]
        [Tooltip("Rebuild the NavMesh automatically after every dungeon generation.")]
        public bool bakeOnBuild = true;
        [Tooltip("Voxel size (m) for the bake. The default (agent radius / 3 ≈ 0.17) is too coarse for STEPPED MESH COLLIDERS: stairs bake as narrow ragged strips and lips between overlapping colliders bake as bumps. 0.06-0.08 resolves the treads cleanly. Cost is bake time only (a few hundred ms on this dungeon) — runtime cost is unchanged.")]
        public float voxelSize = 0.07f;
        [Tooltip("Generated roots whose colliders must NOT bake into the surface. Doors are dynamic — baked solid they'd wall off their doorways forever. NPCs are dynamic AND still alive from the previous generation when this runs (see Rebuild).\n\nNB crawlways are deliberately NOT excluded: the tube's box colliders are real walkable floor, and whether an agent can use them is decided by its HEIGHT, not by a rule here.")]
        public string[] excludeRoots = { "DungeonDoors", "DungeonNpcs", "DungeonPortcullises" };

        [Header("Agent types")]
        [Tooltip("USUALLY LEAVE THIS EMPTY. Every NavMeshSurface on this GameObject is baked automatically, so adding a second agent type is just adding a second NavMeshSurface component here — nothing needs listing. This field is only for surfaces living on OTHER GameObjects, or for deliberately baking a subset.\n\nTHIS IS HOW CRAWLWAYS BECOME PASSABLE, and it needs no AI code at all. A bore's tube already contributes real box colliders with 1.5m of clearance — the reason a goblin can't path through one is simply that Unity's Humanoid agent is 2.0m tall, so the voxelizer discards the span as too low. Add a second NavMeshSurface component here with a short agent type (a 'Crawler' at height ~1.2, radius ~0.25, created in Window > AI > Navigation) and the tubes appear in ITS surface automatically. Corners come free, because it is genuine navmesh rather than an off-mesh link — which is what makes this far cheaper than the ladder problem, where a straight A-to-B link cannot follow a bore that turns.\n\nThe NPC picks its side of this on its own NavMeshAgent's Agent Type dropdown; nothing here needs to know about it.\n\nDO NOT instead lower the Humanoid agent's height to 1.5. That makes every low gap in the dungeon walkable and lets goblins path under things they should not.")]
        public NavMeshSurface[] navSurfaces;

        [Tooltip("After each bake, report for EVERY agent type whether a crawlway bore is actually reachable. Worth leaving on while setting up a crawler agent: 'my kobolds ignore the crawlways' has about five possible causes (agent height, radius, voxel size, the surface not being listed above, the wrong Agent Type on the prefab) and they all present identically as an NPC that simply walks the long way round.")]
        public bool debugAgentReach = true;

        [Header("NPCs (v1: wanderers)")]
        [Tooltip("NPC prefab: NavMeshAgent + CharacterController + NpcLocomotion + NpcBrain + CharacterControllerPhysicsPush. Spawned after each bake, play mode only.")]
        public GameObject npcPrefab;
        [Tooltip("How many to spawn, each in a different random room.")]
        public int npcCount = 1;
        [Tooltip("Never spawn an NPC in the room the player starts in.")]
        public bool avoidSpawnRoom = true;

        DungeonVisualizer vis;

        void Awake() => vis = GetComponent<DungeonVisualizer>();

        /// <summary>Called by DungeonVisualizer at the end of BuildMesh().</summary>
        public void Rebuild(DungeonGenerator gen)
        {
            if (!bakeOnBuild || gen == null) return;
            if (vis == null) vis = GetComponent<DungeonVisualizer>();

            // ONE SURFACE PER AGENT TYPE. Authored as actual NavMeshSurface components rather
            // than as raw agentTypeID ints, because the agent type is only pickable from a
            // dropdown Unity draws on that component — an int field would mean copying opaque
            // hash values out of the Navigation window by hand.
            var baked = ResolveSurfaces();
            if (baked.Length == 0) return;

            foreach (var s in baked)
            {
                // Children of the visualizer = the generated roots; physics
                // colliders = the project's collision truth (§5).
                s.collectObjects = CollectObjects.Children;
                s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;

                // Finer voxels than the default (agentRadius/3): the stairs' stepped
                // mesh colliders otherwise bake as narrow ragged strips, and small
                // height disagreements where the stair prefab overlaps the greybox
                // landing bake as bumps. Set every rebuild so inspector tuning in play
                // mode takes effect on the next F1.
                if (voxelSize > 0f)
                {
                    s.overrideVoxelSize = true;
                    s.voxelSize = voxelSize;
                }
            }

            // Doors and NPCs out of the bake: remember which colliders WE disabled
            // so we never re-enable one something else wanted off.
            //
            // NPCs matter here for a non-obvious reason. In play mode
            // DungeonVisualizer.ClearGenerated uses Destroy(), which DEFERS to the
            // end of the frame — so during a regen (F1/PgUp/PgDn) the previous
            // generation's DungeonNpcs root is still alive right now, and it's a
            // child of the visualizer, which is exactly what this surface collects.
            // Now that NPCs carry a CharacterController (a Collider), their capsules
            // would bake holes into the fresh NavMesh wherever they happened to be
            // standing.
            var disabled = new System.Collections.Generic.List<Collider>();
            foreach (string rootName in excludeRoots)
            {
                foreach (Transform child in transform)
                {
                    if (child.name != rootName) continue;
                    foreach (var c in child.GetComponentsInChildren<Collider>(true))
                        if (c.enabled) { c.enabled = false; disabled.Add(c); }
                }
            }

            // CRAWLWAY GRATES, for exactly the reason doors are excluded — and it cannot be done
            // by root name, which is why it is a second pass. The grate and the tube's walkable
            // floor live under the SAME root, so excluding "DungeonCrawlways" would remove the
            // passage itself from the navmesh and there would be nothing to path through.
            //
            // Baked shut, a grate makes the bore an ISLAND permanently: breaking one at runtime
            // does not rebake, so the nav work would be dead on arrival. Baked open, the navmesh
            // is OPTIMISTIC — an NPC may path toward a crawlway the player has not opened yet
            // and bunch against the bars. That is the lesser evil (it self-corrects the moment
            // the player opens it, which they must do anyway to use the route at all), but it is
            // the same shape as the ladder trap: an agent routed onto something it cannot
            // traverse. The clean fix is letting an NPC break the grate itself —
            // CrawlwayGrate.Break() is public for that.
            foreach (var grate in GetComponentsInChildren<CrawlwayGrate>(true))
            {
                if (grate.IsOpen) continue;   // already broken: its collider is off anyway
                foreach (var c in grate.GetComponentsInChildren<Collider>(true))
                    if (c.enabled) { c.enabled = false; disabled.Add(c); }
            }

            // BUILD-ONLY FAILURE, checked here so it's caught in the EDITOR:
            // runtime navmesh baking reads triangles off MeshColliders, which in a
            // player build requires the mesh's Read/Write Enabled import setting.
            // Non-readable meshes are skipped from the bake SILENTLY — in our case
            // the stairs vanished from the build's navmesh and NPCs just never
            // crossed floors, while the editor (where meshes are always readable)
            // worked perfectly. Mesh.isReadable reports the import setting even in
            // the editor, so this warns before anyone ships the broken bake.
            foreach (var mc in GetComponentsInChildren<MeshCollider>())
            {
                if (mc != null && mc.enabled && mc.sharedMesh != null && !mc.sharedMesh.isReadable)
                    Debug.LogWarning(
                        $"[Nav] MeshCollider '{mc.name}' uses non-readable mesh '{mc.sharedMesh.name}' — " +
                        "it will be MISSING from the NavMesh in a build (editor bakes it fine, which is the trap). " +
                        "Enable Read/Write in the mesh's import settings.", mc);
            }

            try
            {
                foreach (var s in baked) s.BuildNavMesh();
            }
            finally
            {
                foreach (var c in disabled)
                    if (c != null) c.enabled = true;
            }

            Debug.Log($"[Nav] NavMesh rebuilt: {baked.Length} agent surface(s), " +
                      $"{disabled.Count} dynamic collider(s) excluded from the bake.");

            WarnIfNpcAgentUnbaked(baked);
            if (debugAgentReach) ReportCrawlwayReach(gen, baked);

            if (Application.isPlaying)
                SpawnNpcs(gen);
        }

        /// <summary>
        /// The surfaces to bake. Authored list wins; otherwise every NavMeshSurface already on
        /// this GameObject; otherwise one is created — which is exactly the old single-surface
        /// behaviour, so adding agent types is purely additive and an unconfigured project is
        /// unaffected.
        /// </summary>
        NavMeshSurface[] ResolveSurfaces()
        {
            if (navSurfaces != null)
            {
                int n = 0;
                foreach (var s in navSurfaces) if (s != null) n++;
                if (n > 0)
                {
                    var list = new NavMeshSurface[n];
                    int i = 0;
                    foreach (var s in navSurfaces) if (s != null) list[i++] = s;
                    return list;
                }
            }

            var own = GetComponents<NavMeshSurface>();
            if (own.Length > 0) return own;
            return new[] { gameObject.AddComponent<NavMeshSurface>() };
        }

        /// <summary>
        /// The NPC prefab's agent type must be one we actually baked, or that NPC has NO navmesh
        /// anywhere in the dungeon — it spawns, fails every path, and stands still forever.
        ///
        /// Worth a warning rather than a doc line because the symptom reads as broken AI rather
        /// than as missing setup, and adding a crawler agent type is exactly when it happens:
        /// you set the Agent Type on the prefab and forget the matching surface here, or the
        /// reverse. Same family as §12's defaults whose failure is indistinguishable from
        /// correct wiring.
        /// </summary>
        void WarnIfNpcAgentUnbaked(NavMeshSurface[] baked)
        {
            if (npcPrefab == null) return;
            var agent = npcPrefab.GetComponent<NavMeshAgent>();
            if (agent == null) return;

            foreach (var s in baked)
                if (s.agentTypeID == agent.agentTypeID) return;

            Debug.LogWarning(
                $"[Nav] NPC prefab '{npcPrefab.name}' uses agent type " +
                $"'{NavMesh.GetSettingsNameFromID(agent.agentTypeID)}', which NO baked surface provides. " +
                "It will spawn with no navmesh at all and stand still forever, which looks like broken AI " +
                "rather than missing setup. Add a NavMeshSurface for that agent type to this component's " +
                "Nav Surfaces list, or change the prefab's Agent Type.", npcPrefab);
        }

        /// <summary>
        /// For every agent type, is a crawlway bore actually on its navmesh?
        ///
        /// THE FAILURE THIS EXISTS FOR IS SILENT AND HAS FIVE CAUSES. "My kobolds ignore the
        /// crawlways" can be agent height, agent radius, voxel size, the surface not being
        /// listed on this component, or the wrong Agent Type on the prefab — and every one of
        /// them presents identically, as an NPC that simply walks the long way round and looks
        /// like it made a routing decision. Sampling the bore directly answers the question the
        /// setup actually raises, rather than leaving it to be inferred from behaviour.
        ///
        /// A humanoid reporting NO here is CORRECT, not a fault: that exclusion is the feature.
        /// </summary>
        void ReportCrawlwayReach(DungeonGenerator gen, NavMeshSurface[] baked)
        {
            if (gen.Crawlways.Count == 0) return;

            // The first network that actually has a way in — a mouthless one cannot be tested
            // for reachability and would report a misleading "blocked" for every agent type.
            CrawlwaySpec cw = null;
            foreach (var n in gen.Crawlways)
                if (n.Mouths.Count > 0 && n.Cells.Count > 0) { cw = n; break; }
            if (cw == null) return;

            // A cell DEEP in the network, not simply the first one: probing next to the mouth
            // would pass on navmesh that spilled in from the room and say nothing about whether
            // the tunnel itself baked. Furthest-by-bore from the mouth is the honest sample.
            Vector3Int mouthBore = cw.Mouths[0].BoreCell;
            Vector3Int cell = mouthBore;
            int best = -1;
            foreach (var c in cw.Cells)
            {
                int d = Mathf.Abs(c.x - mouthBore.x) + Mathf.Abs(c.z - mouthBore.z) + Mathf.Abs(c.y - mouthBore.y);
                if (d > best) { best = d; cell = c; }
            }

            Vector3 probe = transform.position
                          + (new Vector3(cell.x + 0.5f, cell.y, cell.z + 0.5f) * vis.cellSize)
                          + Vector3.up * 0.3f;

            // The mouth's OPEN cell, so the path test starts on ordinary room/corridor navmesh.
            Vector3Int mouthCell = cw.Mouths[0].OpenCell;
            Vector3 mouth = transform.position
                          + (new Vector3(mouthCell.x + 0.5f, mouthCell.y, mouthCell.z + 0.5f) * vis.cellSize)
                          + Vector3.up * 0.3f;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[Nav] Crawlway at {cell}: ");
            foreach (var s in baked)
            {
                var filter = new NavMeshQueryFilter
                {
                    agentTypeID = s.agentTypeID,
                    areaMask = NavMesh.AllAreas,
                };
                var settings = NavMesh.GetSettingsByID(s.agentTypeID);
                string name = NavMesh.GetSettingsNameFromID(s.agentTypeID);

                bool inBore = NavMesh.SamplePosition(probe, out NavMeshHit boreHit, vis.cellSize * 0.5f, filter);

                // PRESENCE IS NOT CONNECTIVITY, and the difference is the whole diagnosis.
                // "There is navmesh inside the bore" and "an agent standing in the room can walk
                // to it" are different claims, and the symptom that separates them — an NPC that
                // walks up to the grate and stops — looks like a decision rather than a fault.
                // The first version of this only sampled the bore, which proved the bake worked
                // and said nothing about the thing actually being asked.
                string verdict;
                if (!inBore) verdict = "no navmesh in bore";
                else if (!NavMesh.SamplePosition(mouth, out NavMeshHit mouthHit, vis.cellSize, filter))
                    verdict = "bore ok, but NO NAVMESH AT THE MOUTH";
                else
                {
                    var path = new NavMeshPath();
                    bool ok = NavMesh.CalculatePath(mouthHit.position, boreHit.position, filter, path);
                    verdict = ok && path.status == NavMeshPathStatus.PathComplete
                        ? "PASSABLE"
                        : $"bore is an ISLAND ({path.status}) — baked, but not joined to the room";
                }

                // STEP HEIGHT AND SLOPE ARE REPORTED because they are the settings that fragment
                // a new agent type's navmesh without touching whether it fits in a bore. Stairs
                // are the most fragile geometry in this project (see the voxel-size note above),
                // so a Crawler authored with a small step height to suit a small creature loses
                // every staircase — and the symptom is a PathPartial to somewhere perfectly
                // ordinary, not anything that points at stairs.
                sb.Append($"[{name} h={settings.agentHeight:0.##} r={settings.agentRadius:0.##} " +
                          $"step={settings.agentClimb:0.##} slope={settings.agentSlope:0.#}: {verdict}] ");
            }
            sb.Append("— a tall agent failing here is the feature working, not a fault. For a SHORT agent: " +
                      "'no navmesh in bore' means the agent TYPE is too big (height under 1.5, radius under ~0.6); " +
                      "'ISLAND' means the bake is fine and the tube's floor does not meet the room's, so look at " +
                      "the mouth geometry; 'PASSABLE' means nav is not your problem — check the NPC's " +
                      "CharacterController capsule fits the 1.5m bore, since NpcLocomotion moves the BODY and the " +
                      "agent only plans.");
            Debug.Log(sb.ToString());
        }

        void SpawnNpcs(DungeonGenerator gen)
        {
            if (npcPrefab == null || npcCount <= 0 || gen.Rooms.Count == 0) return;

            var root = new GameObject("DungeonNpcs");
            root.transform.SetParent(transform, false);

            // SAMPLE AGAINST THE PREFAB'S OWN AGENT TYPE, not the default one. This code was
            // written when there was exactly one agent type and NavMesh.AllAreas with an implicit
            // Humanoid filter was the same thing as "the navmesh" — §12's perspective rule, where
            // a shared system gains a second kind of user and its defaults are quietly wrong for
            // them. A crawler prefab would otherwise be snapped onto the HUMANOID surface at
            // spawn and could then be standing somewhere its own agent has no navmesh at all.
            int agentType = 0;
            var prefabAgent = npcPrefab.GetComponent<NavMeshAgent>();
            if (prefabAgent != null) agentType = prefabAgent.agentTypeID;
            var spawnFilter = new NavMeshQueryFilter { agentTypeID = agentType, areaMask = NavMesh.AllAreas };

            // Deterministic per (seed): same dungeon, same NPC start rooms —
            // so a tester's (seed, depth) repro includes where the NPCs began.
            var rng = new System.Random(vis.seed ^ 0x5EED);

            int spawned = 0;
            for (int attempt = 0; attempt < npcCount * 8 && spawned < npcCount; attempt++)
            {
                Room room = gen.Rooms[rng.Next(gen.Rooms.Count)];
                if (avoidSpawnRoom && room.Type == RoomType.Start) continue;

                Vector3Int fc = room.InteriorFloorCell;
                Vector3 pos = transform.position +
                              new Vector3(fc.x + 0.5f, fc.y, fc.z + 0.5f) * vis.cellSize;

                // Snap the spawn onto the baked surface — the nominal floor cell
                // can be centimetres off the navmesh (wallMargin, voxelization).
                if (!NavMesh.SamplePosition(pos, out NavMeshHit hit, vis.cellSize, spawnFilter))
                    continue;

                var npc = Instantiate(npcPrefab, hit.position, Quaternion.identity, root.transform);
                npc.name = $"{npcPrefab.name}_{spawned}";
                spawned++;
            }

            Debug.Log($"[Nav] Spawned {spawned}/{npcCount} NPC(s).");
        }
    }
}
