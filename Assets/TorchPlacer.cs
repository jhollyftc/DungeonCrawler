using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    [System.Serializable]
    public class TorchSettings
    {
        public bool placeTorches = true;

        [Header("Spacing (meters between torches)")]
        [Tooltip("Target distance between torches along corridor walls. Lower = brighter, more even.")]
        public float hallwaySpacing = 9f;
        [Tooltip("Target distance between torches along room walls.")]
        public float roomSpacing = 11f;
        [Tooltip("Prison cells stay dark by default — spookier.")]
        public bool torchesInPrisons = false;
        [Tooltip("Small deterministic wobble in spacing so torches don't look mechanically regular. 0 = perfectly even.")]
        [Range(0f, 0.4f)] public float spacingJitter = 0.15f;

        [Header("Placement")]
        [Tooltip("Meters above the floor.")]
        public float height = 2.2f;
        [Tooltip("Meters off the wall, into the room, so the light doesn't clip the wall.")]
        public float wallGap = 0.3f;
        [Tooltip("Fixed sideways shift along the wall from cell center, in meters. Positive = toward the wall's +axis (world +X or +Z).")]
        public float lateralOffset = 0f;
        [Tooltip("Deterministic random sideways spread along the wall, in meters (+/-). Breaks up grid-perfect alignment. Kept within the cell so torches don't drift onto neighbors.")]
        [Range(0f, 1.2f)] public float lateralJitter = 0f;

        [Header("Light")]
        public Color color = new Color(1f, 0.72f, 0.42f);
        public float intensity = 1.6f;
        public float range = 7f;
        [Tooltip("Base shadow mode for torches. With 'Disciplined shadows' on below, most torches stay shadowless and only the nearest few cast — this is the mode those few use.")]
        public LightShadows shadows = LightShadows.Soft;
        public BakeMode bakeMode = BakeMode.Realtime;
        [Tooltip("Perlin intensity flicker. Automatically disabled in Baked mode.")]
        public bool flicker = true;

        [Header("Disciplined shadows")]
        [Tooltip("Only the N torches nearest the camera cast shadows; the rest are shadowless fill lights. Point-light shadows are a 6-face cubemap each, so keep this small.")]
        public bool disciplinedShadows = true;
        [Tooltip("Max torches casting shadows at once. 2-4 looks dramatic and stays cheap.")]
        public int maxShadowCasters = 3;

        [Header("Visual (optional)")]
        [Tooltip("Sconce/torch model, forward axis pointing away from the wall. If the prefab contains its own Light, no extra light is added.")]
        public GameObject[] torchPrefabs;

        [Header("Culling")]
        [Tooltip("Only torch lights within this distance of the camera are enabled. Sconce meshes are never hidden — only the lights and flicker toggle.")]
        public bool cullTorchLights = true;
        public float cullDistance = 30f;
        [Tooltip("Torches checked per frame, round-robin. Keeps the per-frame cost flat and tiny regardless of how many torches exist.")]
        public int cullChecksPerFrame = 750;

        public enum BakeMode { Realtime, Mixed, Baked }
    }

    public static class TorchPlacer
    {
        static readonly Vector3Int[] HDirs =
        {
            new Vector3Int( 1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int( 0, 0, 1),
            new Vector3Int( 0, 0,-1),
        };

        public static GameObject Build(DungeonGenerator gen, TorchSettings s, float cellSize, Transform parent,
                                       InstancedDungeonRenderer instancer = null, RoomStyle style = null)
        {
            var grid = gen.Grid;
            var root = new GameObject("DungeonTorches");
            root.transform.SetParent(parent, false);

            TorchCullingManager culler = null;
            if (s.cullTorchLights && s.bakeMode != TorchSettings.BakeMode.Baked)
            {
                culler = root.AddComponent<TorchCullingManager>();
                culler.cullDistance = s.cullDistance;
                culler.checksPerFrame = s.cullChecksPerFrame;
                culler.disciplinedShadows = s.disciplinedShadows && s.shadows != LightShadows.None;
                culler.maxShadowCasters = s.maxShadowCasters;
                culler.shadowMode = s.shadows;
            }

            bool Open(Vector3Int p) => grid.InBounds(p) && grid[p] != CellType.Empty;

            // ---- Gather all valid wall-mount slots ----
            // A slot is a floor-level walkable cell with a solid neighbor to
            // mount on. Keyed by (cell, direction). We then thin them by
            // spacing rather than by per-face dice.
            var slots = new List<(Vector3Int cell, Vector3Int dir, CellType type)>();
            for (int i = 0; i < grid.Length; i++)
            {
                CellType t = grid[i];
                bool eligible = t == CellType.Hallway || t == CellType.Room ||
                                (t == CellType.Prison && s.torchesInPrisons);
                if (!eligible) continue;

                Vector3Int c = grid.Position(i);
                if (Open(c + Vector3Int.down)) continue;   // floor level only
                foreach (var d in HDirs)
                    if (!Open(c + d))                       // solid wall to mount on
                        slots.Add((c, d, t));
            }

            // ---- Thin by spacing ----
            // Greedy: walk slots in a deterministic order; accept a slot only
            // if no already-accepted torch on the SAME wall plane is within the
            // spacing distance. "Same wall plane" = same facing direction and
            // colinear along the wall, so torches on opposite walls of a
            // corridor alternate independently and both walls get lit.
            float spacingCells = Mathf.Max(1f, s.hallwaySpacing / cellSize);
            float roomSpacingCells = Mathf.Max(1f, s.roomSpacing / cellSize);

            // Deterministic order: sort by a hash so the same seed always
            // thins identically, independent of grid iteration.
            slots.Sort((a, b) =>
                SlotHash(a.cell, a.dir).CompareTo(SlotHash(b.cell, b.dir)));

            var accepted = new List<(Vector3Int cell, Vector3Int dir, CellType type)>();
            // Bucket accepted torches by wall plane for cheap distance checks.
            var byPlane = new Dictionary<(Vector3Int dir, int planeCoord, int y), List<Vector3Int>>();

            foreach (var slot in slots)
            {
                float need = slot.type == CellType.Room ? roomSpacingCells : spacingCells;
                // Per-room-type spacing scale (shrine sparser, treasury denser).
                if (style != null && slot.type == CellType.Room)
                {
                    var room = gen.RoomAt(slot.cell);
                    if (room != null) need *= Mathf.Max(0.2f, style.For(room.Type).spacingScale);
                }
                float jitter = 1f + (Hash(slot.cell, 71) % 1000 / 1000f - 0.5f) * 2f * s.spacingJitter;
                need *= jitter;

                // Plane key: the wall's fixed axis coordinate + facing + level.
                // For an X-facing wall, torches vary along Z; plane fixed at X.
                int planeCoord = slot.dir.x != 0 ? slot.cell.x : slot.cell.z;
                var key = (slot.dir, planeCoord, slot.cell.y);

                bool tooClose = false;
                if (byPlane.TryGetValue(key, out var others))
                {
                    foreach (var o in others)
                    {
                        // Distance ALONG the wall (the varying axis).
                        float dist = slot.dir.x != 0
                            ? Mathf.Abs(slot.cell.z - o.z)
                            : Mathf.Abs(slot.cell.x - o.x);
                        if (dist < need) { tooClose = true; break; }
                    }
                }
                if (tooClose) continue;

                accepted.Add(slot);
                if (!byPlane.TryGetValue(key, out var list))
                {
                    list = new List<Vector3Int>();
                    byPlane[key] = list;
                }
                list.Add(slot.cell);
            }

            // ---- Instantiate ----
            foreach (var slot in accepted)
            {
                Vector3Int c = slot.cell;
                Vector3Int d = slot.dir;
                Vector3 faceCenter = new Vector3(c.x + 0.5f + d.x * 0.5f, c.y, c.z + 0.5f + d.z * 0.5f);

                // Sideways shift ALONG the wall (perpendicular to the mount
                // direction, horizontal). Fixed offset + deterministic jitter,
                // clamped inside the cell so the torch never drifts onto a
                // neighboring wall face.
                Vector3 tangent = new Vector3(Mathf.Abs(d.z), 0f, Mathf.Abs(d.x)); // wall runs along this axis
                float jitter = (Hash(c, 89) % 1000 / 1000f - 0.5f) * 2f * s.lateralJitter;
                float lateralMeters = s.lateralOffset + jitter;
                float halfCell = cellSize * 0.45f; // keep a margin from the cell edge
                lateralMeters = Mathf.Clamp(lateralMeters, -halfCell, halfCell);

                Vector3 pos = faceCenter * cellSize + parent.position
                              - (Vector3)d * s.wallGap
                              + tangent * lateralMeters
                              + Vector3.up * s.height;
                Quaternion rot = Quaternion.LookRotation(-(Vector3)d); // forward = away from wall

                GameObject prefab = (s.torchPrefabs != null && s.torchPrefabs.Length > 0)
                    ? s.torchPrefabs[Hash(c, 7) % s.torchPrefabs.Length]
                    : null;

                Light light = null;
                TorchFlicker flicker = null;

                if (prefab != null && instancer != null)
                {
                    // Split path: torch MESH batches through the instancer;
                    // the Light (+ flicker) stays as an individual GameObject.
                    int seed = Hash(c, 31) % 1000;
                    PropInstancer.PlaceProps(instancer, prefab,
                        new[] { new PropPlacement { position = pos, rotation = rot,
                            configure = go =>
                            {
                                light = go.GetComponentInChildren<Light>();
                                if (light == null)
                                {
                                    light = go.AddComponent<Light>();
                                    light.type = LightType.Point;
                                }
                                flicker = go.GetComponentInChildren<TorchFlicker>();
                                if (s.flicker && s.bakeMode != TorchSettings.BakeMode.Baked && flicker == null)
                                {
                                    flicker = light.gameObject.AddComponent<TorchFlicker>();
                                    flicker.noiseSeed = seed;
                                }
                            } } },
                        PropTier.InstancedMeshWithLight, cellSize, root.transform);
                }
                else
                {
                    // Fallback (no instancer, e.g. PrefabKit mode, or no prefab):
                    // one GameObject carrying mesh + light, as before.
                    var torch = new GameObject("Torch");
                    torch.transform.SetParent(root.transform, false);
                    torch.transform.SetPositionAndRotation(pos, rot);

                    if (prefab != null)
                    {
                        var visual = Object.Instantiate(prefab, pos, rot * prefab.transform.rotation, torch.transform);
                        light = visual.GetComponentInChildren<Light>();
                    }
                    if (light == null)
                    {
                        light = torch.AddComponent<Light>();
                        light.type = LightType.Point;
                    }
                    if (s.flicker && s.bakeMode != TorchSettings.BakeMode.Baked)
                    {
                        flicker = light.gameObject.AddComponent<TorchFlicker>();
                        flicker.noiseSeed = Hash(c, 31) % 1000;
                    }
                }

                if (light != null)
                {
                    // Per-room-type color/intensity from the style; corridors and
                    // untyped areas use the torch settings' defaults.
                    Color col = s.color;
                    float intensityScale = 1f;
                    if (style != null)
                    {
                        var room = gen.RoomAt(c);
                        if (room != null)
                        {
                            var e = style.For(room.Type);
                            col = e.torchColor;
                            intensityScale = e.intensityScale;
                        }
                    }
                    light.color = col;
                    light.intensity = s.intensity * intensityScale;
                    light.range = s.range;
                    // Under discipline, start shadowless — the culler promotes
                    // only the nearest maxShadowCasters to cast each frame.
                    light.shadows = (s.disciplinedShadows && s.shadows != LightShadows.None)
                        ? LightShadows.None
                        : s.shadows;
#if UNITY_EDITOR
                    light.lightmapBakeType = s.bakeMode switch
                    {
                        TorchSettings.BakeMode.Baked => LightmapBakeType.Baked,
                        TorchSettings.BakeMode.Mixed => LightmapBakeType.Mixed,
                        _ => LightmapBakeType.Realtime,
                    };
#endif
                }

                if (culler != null && light != null)
                {
                    light.enabled = false;
                    if (flicker != null) flicker.enabled = false;
                    culler.Register(light, flicker);
                }
            }

            Debug.Log($"[Dungeon] {accepted.Count} torches placed (from {slots.Count} candidate wall slots).");
            return root;
        }

        static int SlotHash(Vector3Int c, Vector3Int d)
        {
            int di = d.x > 0 ? 0 : d.x < 0 ? 1 : d.z > 0 ? 2 : 3;
            return Hash(c, 200 + di);
        }

        static int Hash(Vector3Int c, int salt)
        {
            unchecked
            {
                int h = c.x * 73856093 ^ c.y * 19349663 ^ c.z * 83492791 ^ salt * 374761393;
                h ^= h >> 13; h *= 1274126177; h ^= h >> 16;
                return h & 0x7fffffff;
            }
        }
    }

    /// <summary>
    /// Distance-culls torch lights: only lights within cullDistance of the
    /// active camera are enabled.
    ///
    /// Play mode: time-sliced — a fixed budget of entries is checked per frame,
    /// round-robin, so per-frame cost is flat and tiny regardless of torch count
    /// (no periodic full-sweep hitch).
    /// Edit mode: a full re-cull runs only when the Scene view camera has moved,
    /// and does nothing while it's parked.
    ///
    /// Torch positions are static and cached at registration. Entries share the
    /// manager's lifetime (all children of the same root), so no per-entry
    /// destroyed-object checks are needed.
    /// </summary>
    [ExecuteAlways]
    public class TorchCullingManager : MonoBehaviour
    {
        public float cullDistance = 30f;
        public int checksPerFrame = 750;

        [Tooltip("Set by TorchPlacer.")]
        public bool disciplinedShadows;
        public int maxShadowCasters = 3;
        public LightShadows shadowMode = LightShadows.Soft;
        [Tooltip("How often (seconds) to recompute which torches cast shadows.")]
        public float shadowUpdateInterval = 0.2f;

        struct Entry
        {
            public Light Light;
            public Behaviour Flicker; // may be null
            public Vector3 Pos;
        }

        readonly System.Collections.Generic.List<Entry> entries
            = new System.Collections.Generic.List<Entry>();
        int cursor;
        Vector3 lastEditorCamPos = new Vector3(float.PositiveInfinity, 0f, 0f);
        float shadowTimer;

        // Reused scratch for the nearest-N shadow selection.
        readonly System.Collections.Generic.List<int> shadowCandidates
            = new System.Collections.Generic.List<int>();
        readonly System.Collections.Generic.HashSet<Light> currentCasters
            = new System.Collections.Generic.HashSet<Light>();
        readonly System.Collections.Generic.List<Light> nextCasters
            = new System.Collections.Generic.List<Light>();

        public void Register(Light light, Behaviour flicker)
        {
            entries.Add(new Entry { Light = light, Flicker = flicker, Pos = light.transform.position });
        }

        void Update()
        {
            if (entries.Count == 0) return;

            Vector3 camPos;
            bool haveCam = false;

            if (Application.isPlaying)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                camPos = cam.transform.position;
                haveCam = true;
                SlicedSweep(camPos);
            }
            else
            {
                camPos = Vector3.zero;
#if UNITY_EDITOR
                var sv = UnityEditor.SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null) return;
                camPos = sv.camera.transform.position;
                haveCam = true;
                if ((camPos - lastEditorCamPos).sqrMagnitude >= 4f)
                {
                    lastEditorCamPos = camPos;
                    FullSweep(camPos);
                }
#else
                return;
#endif
            }

            // Disciplined shadows: throttled, picks the nearest N enabled
            // torches to cast; all others stay shadowless.
            if (haveCam && disciplinedShadows)
            {
                shadowTimer -= Time.deltaTime;
                if (shadowTimer <= 0f)
                {
                    shadowTimer = shadowUpdateInterval;
                    UpdateShadowCasters(camPos);
                }
            }
        }

        void UpdateShadowCasters(Vector3 camPos)
        {
            // Collect enabled torches within cull range, then keep the nearest
            // maxShadowCasters as casters. A partial selection sort avoids a
            // full sort of hundreds of lights.
            shadowCandidates.Clear();
            float sq = cullDistance * cullDistance;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Light == null || !e.Light.enabled) continue;
                if ((e.Pos - camPos).sqrMagnitude <= sq)
                    shadowCandidates.Add(i);
            }

            int keep = Mathf.Min(maxShadowCasters, shadowCandidates.Count);

            // Selection-sort the nearest `keep` to the front.
            for (int a = 0; a < keep; a++)
            {
                int best = a;
                float bestSq = Sq(shadowCandidates[a], camPos);
                for (int b = a + 1; b < shadowCandidates.Count; b++)
                {
                    float s = Sq(shadowCandidates[b], camPos);
                    if (s < bestSq) { best = b; bestSq = s; }
                }
                (shadowCandidates[a], shadowCandidates[best]) = (shadowCandidates[best], shadowCandidates[a]);
            }

            // Apply: the front `keep` cast, and anything that was casting but
            // isn't in the new set reverts to shadowless.
            nextCasters.Clear();
            for (int a = 0; a < keep; a++)
            {
                var li = entries[shadowCandidates[a]].Light;
                nextCasters.Add(li);
                if (li.shadows != shadowMode) li.shadows = shadowMode;
                currentCasters.Remove(li);
            }
            // Whatever remains in currentCasters is no longer a caster.
            foreach (var li in currentCasters)
                if (li != null) li.shadows = LightShadows.None;
            currentCasters.Clear();
            for (int a = 0; a < nextCasters.Count; a++)
                currentCasters.Add(nextCasters[a]);
        }

        float Sq(int entryIndex, Vector3 camPos) =>
            (entries[entryIndex].Pos - camPos).sqrMagnitude;

        void SlicedSweep(Vector3 camPos)
        {
            float sq = cullDistance * cullDistance;
            int n = Mathf.Min(checksPerFrame, entries.Count);
            for (int k = 0; k < n; k++)
            {
                cursor++;
                if (cursor >= entries.Count) cursor = 0;
                Apply(entries[cursor], camPos, sq);
            }
        }

        void FullSweep(Vector3 camPos)
        {
            float sq = cullDistance * cullDistance;
            for (int i = 0; i < entries.Count; i++)
                Apply(entries[i], camPos, sq);
        }

        void Apply(in Entry e, Vector3 camPos, float sqDistance)
        {
            if (e.Light == null) return;
            bool on = (e.Pos - camPos).sqrMagnitude < sqDistance;
            if (e.Light.enabled != on)
            {
                e.Light.enabled = on;
                if (e.Flicker != null) e.Flicker.enabled = on;
                // A disciplined light leaving range drops its caster status so
                // it re-enters shadowless and the slot frees for a nearer torch.
                if (!on && disciplinedShadows && e.Light.shadows != LightShadows.None)
                {
                    e.Light.shadows = LightShadows.None;
                    currentCasters.Remove(e.Light);
                }
            }
        }
    }
}