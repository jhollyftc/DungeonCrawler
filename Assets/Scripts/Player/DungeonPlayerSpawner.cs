using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Placement-only spawner: picks a random room, finds real ground, and
    /// puts the player there.
    ///
    /// PREFERRED: assign `playerPrefab` — a prefab you own and evolve in the
    /// editor (weapons, viewmodel, sockets, audio, UI all live there). Use the
    /// context menu "Build Player Rig In Scene" once to generate a starting
    /// rig, drag it into the Project as a prefab, assign it, and customize
    /// freely from then on.
    ///
    /// FALLBACK: with no prefab assigned, a default player is code-assembled
    /// from the legacy settings below (torch, footsteps) so the project keeps
    /// working out of the box.
    /// </summary>
    [RequireComponent(typeof(DungeonVisualizer))]
    public class DungeonPlayerSpawner : MonoBehaviour
    {
        public bool spawnOnPlay = true;
        [Tooltip("Room type to spawn in. Start for normal play; pick any other type to debug that room (prop placement, lighting). Falls back to Start, then any room, if no room of the chosen type exists this seed.")]
        public RoomType spawnRoomType = RoomType.Start;
        [Tooltip("Your player prefab. When assigned, the legacy sections below are ignored — configure everything on the prefab instead.")]
        public GameObject playerPrefab;
        [Tooltip("Disables all other cameras and audio listeners so the player camera is the only active one.")]
        public bool disableOtherCameras = true;

        [Header("Legacy fallback: player dimensions (meters)")]
        public float playerHeight = 1.8f;
        public float playerRadius = 0.35f;
        public float eyeHeight = 1.62f;

        [Header("Legacy fallback: player torch")]
        public bool givePlayerTorch = true;
        public Color torchColor = new Color(1f, 0.75f, 0.45f);
        public float torchIntensity = 2.2f;
        public float torchRange = 12f;
        [Tooltip("One light can afford real shadows — set to None if performance dips.")]
        public LightShadows torchShadows = LightShadows.Soft;
        public bool torchFlicker = true;
        [Tooltip("Local offset from the camera — right and slightly low, like a hand-held torch.")]
        public Vector3 torchOffset = new Vector3(0.3f, -0.15f, 0.2f);

        [Header("Legacy fallback: footsteps")]
        [Tooltip("Stone footstep clips — 3+ variations recommended so the anti-repeat shuffle has room to work.")]
        public AudioClip[] footstepClips;
        public AudioClip landClip;
        public float stepDistance = 2.4f;
        [Range(0f, 1f)] public float footstepVolume = 0.8f;

        static Room FindRoomOfType(DungeonGenerator gen, RoomType type)
        {
            foreach (var r in gen.Rooms)
                if (r.Type == type) return r;
            return null;
        }

        void Start()
        {
            if (!spawnOnPlay) return;

            var vis = GetComponent<DungeonVisualizer>();
            if (vis.Generator == null)
                vis.Generate(); // deterministic: same seed rebuilds the same dungeon

            var gen = vis.Generator;
            if (gen == null || gen.Rooms.Count == 0)
            {
                Debug.LogError("[Spawner] No rooms to spawn in.");
                return;
            }

            // Spawn room by type: the chosen debug type, else Start, else the
            // first room. Deterministic — same seed + same type = same spot.
            Room room = FindRoomOfType(gen, spawnRoomType)
                        ?? FindRoomOfType(gen, RoomType.Start)
                        ?? gen.Rooms[0];
            if (room.Type != spawnRoomType)
                Debug.LogWarning($"[Spawner] No {spawnRoomType} room this seed — spawning in {room.Type} instead.");
            BoundsInt b = room.Bounds;
            // A guaranteed-interior floor cell — an irregular room's bounding
            // box center can sit in a corner bite (solid rock).
            Vector3Int fc = room.InteriorFloorCell;
            Vector3 floorCenterCells = new Vector3(fc.x + 0.5f, fc.y, fc.z + 0.5f);
            Vector3 floorWorld = transform.position + floorCenterCells * vis.cellSize;
            Vector3 spawn = floorWorld + Vector3.up * (playerHeight * 0.5f + 0.1f);

            // Snap to the real ground. RaycastAll + nearest-to-nominal-floor
            // selection, so a ceiling collider sitting lower than it should
            // (pivot/offset issues in kit assets) can't win over the floor.
            Vector3 rayStart = floorWorld + Vector3.up * (vis.cellSize * 0.9f);
            var hits = Physics.RaycastAll(rayStart, Vector3.down, vis.cellSize * 3f);
            if (hits.Length > 0)
            {
                RaycastHit best = hits[0];
                float bestDelta = Mathf.Abs(best.point.y - floorWorld.y);
                for (int i = 1; i < hits.Length; i++)
                {
                    float delta = Mathf.Abs(hits[i].point.y - floorWorld.y);
                    if (delta < bestDelta) { best = hits[i]; bestDelta = delta; }
                }
                spawn = best.point + Vector3.up * (playerHeight * 0.5f + 0.05f);
                Debug.Log($"[Spawner] Ground under spawn: '{best.collider.name}' at y={best.point.y:F2} " +
                          $"(nominal floor y={floorWorld.y:F2}, {hits.Length} surface(s) in the column)");
                if (bestDelta > 0.5f)
                    Debug.LogWarning($"[Spawner] Nearest walkable surface is {bestDelta:F2}m from the nominal floor level — " +
                                     "a kit asset's collider is probably offset from its intended height.");
            }
            else
            {
                Debug.LogWarning("[Spawner] No collider found under the spawn point — the player WILL fall. " +
                    "In PrefabKit mode, make sure colliders are applied to the prefab ASSETS (Overrides > Apply All), " +
                    "sized to cover the full tile, and not set to Is Trigger. " +
                    "GeneratedMesh mode has a collider built in if you want to verify the controller itself.");
            }

            GameObject player = playerPrefab != null
                ? SpawnFromPrefab(spawn)
                : BuildDefaultPlayer(spawn);

            Debug.Log($"[Spawner] Player spawned in room {gen.Rooms.IndexOf(room)} ({room.Type}) at {spawn}" +
                      (playerPrefab != null ? " (prefab)" : " (default code-built rig)"));
        }

        // ---------------- Prefab path ----------------

        GameObject SpawnFromPrefab(Vector3 spawn)
        {
            var player = Instantiate(playerPrefab, spawn, Quaternion.identity);
            player.name = "Player";

            var cam = player.GetComponentInChildren<Camera>();
            if (cam == null)
            {
                Debug.LogError("[Spawner] Player prefab has no Camera child — add one (the spawner only places; the prefab owns its rig).");
            }
            else
            {
                cam.gameObject.tag = "MainCamera";
                if (cam.GetComponent<AudioListener>() == null)
                    cam.gameObject.AddComponent<AudioListener>();

                // Fill only what the prefab left unwired.
                var fpc = player.GetComponentInChildren<FirstPersonController>();
                if (fpc != null && fpc.cam == null) fpc.cam = cam.transform;
                var interactor = player.GetComponentInChildren<PlayerInteractor>();
                if (interactor != null && interactor.cam == null) interactor.cam = cam.transform;

                HandleOtherCameras(cam);
            }
            return player;
        }

        // ---------------- Legacy code-built rig ----------------

        GameObject BuildDefaultPlayer(Vector3 spawn)
        {
            var player = new GameObject("Player");
            player.transform.position = spawn;

            var cc = player.AddComponent<CharacterController>();
            cc.height = playerHeight;
            cc.radius = playerRadius;

            var camGo = new GameObject("PlayerCamera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = Vector3.up * (eyeHeight - playerHeight * 0.5f);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;

            HandleOtherCameras(cam);
            camGo.AddComponent<AudioListener>();

            var fpc = player.AddComponent<FirstPersonController>();
            fpc.cam = camGo.transform;

            var interactor = player.AddComponent<PlayerInteractor>();
            interactor.cam = camGo.transform;

            var footsteps = player.AddComponent<PlayerFootsteps>();
            footsteps.clips = footstepClips;
            footsteps.landClip = landClip;
            footsteps.stepDistance = stepDistance;
            footsteps.volume = footstepVolume;

            if (givePlayerTorch)
            {
                var torchGo = new GameObject("PlayerTorch");
                torchGo.transform.SetParent(camGo.transform, false);
                torchGo.transform.localPosition = torchOffset;

                var li = torchGo.AddComponent<Light>();
                li.type = LightType.Point;
                li.color = torchColor;
                li.intensity = torchIntensity;
                li.range = torchRange;
                li.shadows = torchShadows;

                if (torchFlicker)
                {
                    var f = torchGo.AddComponent<TorchFlicker>();
                    f.amount = 0.12f; // gentler than wall torches — it's right in your face
                    f.speed = 5f;
                    f.noiseSeed = 777;
                }
            }
            return player;
        }

        void HandleOtherCameras(Camera playerCam)
        {
            if (!disableOtherCameras || !Application.isPlaying) return;
            foreach (var other in FindObjectsOfType<Camera>())
            {
                if (other == playerCam) continue;
                // Never touch cameras the player rig owns. ViewmodelCamera builds a
                // URP OVERLAY camera under the eye at Awake — i.e. during the very
                // Instantiate that precedes this call — and disabling it makes the
                // weapon/shield vanish while URP still happily lists it in the base
                // camera's stack. This pass exists to kill STRAY SCENE cameras, not
                // to police the prefab's own rig.
                if (other.transform.IsChildOf(playerCam.transform)) continue;
                other.gameObject.SetActive(false);
            }
            foreach (var listener in FindObjectsOfType<AudioListener>())
                if (listener.gameObject != playerCam.gameObject) Destroy(listener);
        }

        // ---------------- Migration helper ----------------

        [ContextMenu("Build Player Rig In Scene (for prefab-ing)")]
        void BuildRigInScene()
        {
            var rig = BuildDefaultPlayer(transform.position + Vector3.up * 2f);
            Debug.Log("[Spawner] Player rig built in the scene. Drag it into the Project window " +
                      "to create your player prefab, assign it to Player Prefab on this spawner, " +
                      "then delete the scene copy. From now on, evolve the player in Prefab Mode.",
                      rig);
        }
    }
}