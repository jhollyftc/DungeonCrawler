using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    [System.Serializable]
    public class FogSettings
    {
        [Tooltip("Drive RenderSettings.fogColor at runtime toward the torch palette of the room the camera is in (or approaching). Fog itself must be enabled in Lighting > Environment — this only steers its color.")]
        public bool dynamicFogColor = false;
        [Tooltip("Meters outside a room's bounds over which its color fades in as you approach. 0 = colors switch only on entry.")]
        public float transitionDistance = 6f;
        [Tooltip("How quickly the fog color chases its target. Higher = snappier; ~1.5 gives a slow atmospheric drift.")]
        public float responseSpeed = 1.5f;
    }

    /// <summary>
    /// Play-mode environment fog tinting — the fog IS the darkness (no
    /// directional light in-game), so steering its color by room type extends
    /// the type-driven torch palette into the air itself. Each frame the main
    /// camera's position resolves to a room (footprint-aware, so L-bites and
    /// hallways passing through a bbox don't mis-tint) or, in corridors, to
    /// the nearest room within transitionDistance; RenderSettings.fogColor
    /// eases toward that room's torch color. Corridors and untyped space
    /// target the style's default torch color — the same one corridor torches
    /// burn with.
    ///
    /// Spawned by DungeonVisualizer.BuildMesh when FogSettings.dynamicFogColor
    /// is on. Runtime references (generator) are non-serialized: if the
    /// dungeon was generated in edit mode, regenerate in play mode to arm it.
    /// </summary>
    public class DungeonFogController : MonoBehaviour
    {
        DungeonGenerator gen;
        RoomStyle style;
        FogSettings settings;
        float cellSize;
        Vector3 origin;

        struct RoomEntry
        {
            public Bounds bounds; // world-space bbox (approach blending only)
            public Color color;
        }
        readonly List<RoomEntry> rooms = new List<RoomEntry>();
        Color current;
        bool initialized;

        public void Init(DungeonGenerator gen, RoomStyle style, float cellSize, Vector3 origin, FogSettings settings)
        {
            this.gen = gen;
            this.style = style;
            this.cellSize = cellSize;
            this.origin = origin;
            this.settings = settings;

            rooms.Clear();
            foreach (var r in gen.Rooms)
            {
                var b = new Bounds();
                b.SetMinMax((Vector3)r.Bounds.min * cellSize + origin,
                            (Vector3)r.Bounds.max * cellSize + origin);
                rooms.Add(new RoomEntry { bounds = b, color = style.For(r.Type).torchColor });
            }
            current = RenderSettings.fogColor;
            initialized = true;
        }

        void Update()
        {
            if (!initialized || !Application.isPlaying) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 pos = cam.transform.position;

            // Corridors / untyped space: the style's default torch color.
            Color target = style.defaultTorchColor;

            Vector3Int cell = Vector3Int.FloorToInt((pos - origin) / cellSize);
            Room inside = gen.RoomAt(cell);
            if (inside != null)
            {
                target = style.For(inside.Type).torchColor;
            }
            else
            {
                // Approaching: blend the nearest room's color in by distance
                // to its bounds. Rooms number in the tens — a linear scan per
                // frame is nothing.
                float bestSq = float.MaxValue;
                int best = -1;
                for (int i = 0; i < rooms.Count; i++)
                {
                    float sq = rooms[i].bounds.SqrDistance(pos);
                    if (sq < bestSq) { bestSq = sq; best = i; }
                }
                if (best >= 0)
                {
                    float d = Mathf.Sqrt(bestSq);
                    if (d < settings.transitionDistance)
                        target = Color.Lerp(rooms[best].color, target, d / settings.transitionDistance);
                }
            }

            // Frame-rate-independent ease toward the target.
            float k = 1f - Mathf.Exp(-settings.responseSpeed * Time.deltaTime);
            current = Color.Lerp(current, target, k);
            RenderSettings.fogColor = current;
        }
    }
}
