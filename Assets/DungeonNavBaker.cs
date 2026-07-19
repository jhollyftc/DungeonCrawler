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
        [Tooltip("Generated roots whose colliders must NOT bake into the surface. Doors are dynamic — baked solid they'd wall off their doorways forever. NPCs are dynamic AND still alive from the previous generation when this runs (see Rebuild).")]
        public string[] excludeRoots = { "DungeonDoors", "DungeonNpcs" };

        [Header("NPCs (v1: wanderers)")]
        [Tooltip("NPC prefab: NavMeshAgent + CharacterController + NpcLocomotion + NpcBrain + CharacterControllerPhysicsPush. Spawned after each bake, play mode only.")]
        public GameObject npcPrefab;
        [Tooltip("How many to spawn, each in a different random room.")]
        public int npcCount = 1;
        [Tooltip("Never spawn an NPC in the room the player starts in.")]
        public bool avoidSpawnRoom = true;

        NavMeshSurface surface;
        DungeonVisualizer vis;

        void Awake() => vis = GetComponent<DungeonVisualizer>();

        /// <summary>Called by DungeonVisualizer at the end of BuildMesh().</summary>
        public void Rebuild(DungeonGenerator gen)
        {
            if (!bakeOnBuild || gen == null) return;
            if (vis == null) vis = GetComponent<DungeonVisualizer>();

            if (surface == null)
            {
                surface = GetComponent<NavMeshSurface>();
                if (surface == null) surface = gameObject.AddComponent<NavMeshSurface>();
                // Children of the visualizer = the generated roots; physics
                // colliders = the project's collision truth (§5).
                surface.collectObjects = CollectObjects.Children;
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            }

            // Finer voxels than the default (agentRadius/3): the stairs' stepped
            // mesh colliders otherwise bake as narrow ragged strips, and small
            // height disagreements where the stair prefab overlaps the greybox
            // landing bake as bumps. Set every rebuild so inspector tuning in play
            // mode takes effect on the next F1.
            if (voxelSize > 0f)
            {
                surface.overrideVoxelSize = true;
                surface.voxelSize = voxelSize;
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

            try { surface.BuildNavMesh(); }
            finally
            {
                foreach (var c in disabled)
                    if (c != null) c.enabled = true;
            }

            Debug.Log($"[Nav] NavMesh rebuilt ({disabled.Count} dynamic collider(s) excluded from the bake).");

            if (Application.isPlaying)
                SpawnNpcs(gen);
        }

        void SpawnNpcs(DungeonGenerator gen)
        {
            if (npcPrefab == null || npcCount <= 0 || gen.Rooms.Count == 0) return;

            var root = new GameObject("DungeonNpcs");
            root.transform.SetParent(transform, false);

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
                if (!NavMesh.SamplePosition(pos, out NavMeshHit hit, vis.cellSize, NavMesh.AllAreas))
                    continue;

                var npc = Instantiate(npcPrefab, hit.position, Quaternion.identity, root.transform);
                npc.name = $"{npcPrefab.name}_{spawned}";
                spawned++;
            }

            Debug.Log($"[Nav] Spawned {spawned}/{npcCount} NPC(s).");
        }
    }
}
