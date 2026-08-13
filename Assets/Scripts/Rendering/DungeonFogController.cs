using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    [System.Serializable]
    public class FogSettings
    {
        [Tooltip("Drive RenderSettings.fogColor at runtime toward the torch palette of the room the camera is in (or approaching). Fog itself must be enabled in Lighting > Environment — this only steers its color.")]
        public bool dynamicFogColor = false;
        [Tooltip("Meters outside a room's bounds over which its color fades in as you approach — regardless of look direction (room air spills out of doorways).")]
        public float transitionDistance = 6f;
        [Tooltip("Rooms you LOOK toward tint the fog from farther away, so a visited room seen back down a long hall keeps its color identity instead of washing out to corridor air. Meters; 0 = position-only (no view term). Roughly match your fog's visible distance.")]
        public float lookDistance = 30f;
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
        PlayerRoomTracker tracker;
        RoomStyle style;
        FogSettings settings;
        float cellSize;
        Vector3 origin;

        /// <summary>Fog colour for a room type: its palette HUE at its own fog brightness.</summary>
        public static Color FogColorFor(RoomStyle style, RoomType type)
        {
            var e = style.For(type);
            return FogColor(e.torchColor, e.fogIntensity);
        }

        /// <summary>
        /// HUE FROM THE PALETTE, BRIGHTNESS FROM ITS OWN DIAL.
        ///
        /// Fog used to be assigned the torch colour verbatim, which quietly welded two
        /// unrelated authoring decisions together: pushing a torch colour's HDR intensity up
        /// so the flame VFX would bloom also washed the fog out, and there was no way to have
        /// a punchy flame in a deep dark room. The magnitude is dropped and replaced.
        ///
        /// The SHARED HUE is the part that must not be broken (§7): fog, firelight, flame VFX
        /// and emissive kit all come from one colour so a blue shrine cannot end up with
        /// orange haze. That invariant is about hue, not brightness, so per-consumer
        /// brightness costs nothing.
        ///
        /// Normalizing by the MAX CHANNEL rather than by luminance keeps the most saturated
        /// channel at 1.0, so an LDR palette colour (the common case, max channel already 1)
        /// passes through UNCHANGED at fogIntensity 1 — the old behaviour, exactly, for every
        /// room that was authored before HDR was pushed into these swatches.
        /// </summary>
        public static Color FogColor(Color palette, float fogIntensity)
        {
            Color hue = RoomStyle.Hue(palette);
            return new Color(hue.r * fogIntensity, hue.g * fogIntensity, hue.b * fogIntensity, 1f);
        }

        struct RoomEntry
        {
            public Bounds bounds; // world-space bbox (approach blending only)
            public Color color;
        }
        readonly List<RoomEntry> rooms = new List<RoomEntry>();
        Color current;
        bool initialized;

        public void Init(DungeonGenerator gen, RoomStyle style, float cellSize, Vector3 origin, FogSettings settings, PlayerRoomTracker tracker)
        {
            this.gen = gen;
            this.tracker = tracker;
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
                rooms.Add(new RoomEntry { bounds = b, color = FogColorFor(style, r.Type) });
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

            // Corridors / untyped space: the style's default torch color, at the default
            // fog brightness.
            Color target = FogColor(style.defaultTorchColor, style.DefaultFogIntensity);

            // WHICH ROOM the player is in comes from PlayerRoomTracker; the camera position
            // above is still used for the PROXIMITY and VIEW terms below, which are about
            // where you are looking from rather than where you are standing.
            //
            // BEHAVIOUR CHANGE, deliberate: this used to floor the CAMERA's position into a
            // cell, while the map and the room readout floored the PLAYER's. Leaning through
            // a doorway, or standing where a stairwell puts your eyes a cell above your feet,
            // made the fog announce a room the map disagreed with. Feet win — "which room am
            // I in" is about where you are standing.

            Room inside = null;
            if (tracker != null && tracker.HasPlayer)
            {
                tracker.Refresh();
                inside = tracker.CurrentRoom;
            }
            // A CRAWL BORE HAS ITS OWN HAZE, and it needs no CellType to get one. The cell stays
            // Empty — solid rock to the mesher and the kit — but identity in this project has
            // never come from CellType: alcoves are typed Hallway and get their contents from a
            // registry, pits resolve through PitAt. Fog is the same shape, so a bore resolves
            // through IsCrawlwayCell and nothing about the grid has to change.
            //
            // Asked BEFORE the room test because a bore is never in a room and the corridor
            // default would otherwise claim it — a green sewer pipe glowing with corridor amber
            // is the whole thing this prevents. Worth knowing: this is the ONLY use a crawlway
            // has for a torch palette, since a bore's cell is solid and can hold no torch.
            // FEET, not the camera — the same rule the room test below already follows, and it
            // matters more here: a bore is one cell wide, so crouching at its mouth puts your
            // eyes and your knees in different spaces almost every time.
            Vector3Int feetCell = tracker != null && tracker.HasPlayer
                ? tracker.CurrentCell
                : Vector3Int.FloorToInt((pos - origin) / cellSize);

            if (gen != null && gen.IsCrawlwayCell(feetCell))
            {
                target = FogColor(style.CrawlwayHue(), style.CrawlwayFogIntensity());
            }
            // A chamber is typed Hallway, so RoomAt returns null for it and the corridor default
            // would claim it — the same trap alcoves hit in AudioSpace. Asked here, before the
            // room test, for the same reason the bore is.
            else if (gen != null && gen.IsChamberCell(feetCell))
            {
                target = FogColor(style.ChamberHue(), style.ChamberFogIntensity());
            }
            else if (inside != null)
            {
                target = FogColorFor(style, inside.Type);
            }
            else
            {
                // Each room contributes the STRONGER of two terms; the
                // strongest room tints the fog. Rooms number in the tens — a
                // linear scan per frame is nothing.
                //   Proximity: fades in over transitionDistance regardless of
                //     facing (room air spills out of doorways).
                //   View: fades in over lookDistance, gated by how directly
                //     the camera looks toward the room — so a distant visited
                //     room seen back down a long hall keeps its color
                //     identity instead of washing out to corridor grey.
                Vector3 forward = cam.transform.forward;
                float bestStrength = 0f;
                Color bestColor = target;
                for (int i = 0; i < rooms.Count; i++)
                {
                    Vector3 toRoom = rooms[i].bounds.ClosestPoint(pos) - pos;
                    float d = toRoom.magnitude;

                    float strength = settings.transitionDistance > 0f
                        ? 1f - Mathf.Clamp01(d / settings.transitionDistance)
                        : 0f;

                    if (settings.lookDistance > 0f && d > 0.01f)
                    {
                        // Alignment ramps from 0 at dot=0.3 to full at dot=0.9
                        // — looking straight at a room counts fully, glancing
                        // past it counts a little, behind you counts nothing.
                        float align = Vector3.Dot(forward, toRoom / d);
                        float view = Mathf.Clamp01((align - 0.3f) / 0.6f)
                                     * (1f - Mathf.Clamp01(d / settings.lookDistance));
                        if (view > strength) strength = view;
                    }

                    if (strength > bestStrength) { bestStrength = strength; bestColor = rooms[i].color; }
                }
                target = Color.Lerp(target, bestColor, bestStrength);
            }

            // Frame-rate-independent ease toward the target.
            float k = 1f - Mathf.Exp(-settings.responseSpeed * Time.deltaTime);
            current = Color.Lerp(current, target, k);
            RenderSettings.fogColor = current;
        }
    }
}
