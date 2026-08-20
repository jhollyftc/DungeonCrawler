using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

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
        [Tooltip("How far the LIGHT's intensity swings, as a fraction of its base. 0.25 = rides between 0.75x and 1.25x. Read indirectly off walls, so it tolerates a lot more than the flame does.")]
        [Range(0f, 1f)] public float flickerAmount = 0.25f;
        [Tooltip("Flicker rate. Loosely matching this to the flame VFX's flipbook rate makes the two reinforce rather than beat against each other.")]
        public float flickerSpeed = 6f;
        [Tooltip("How hard the FLAME pulses, as a multiple of flickerAmount, driven from the SAME noise sample as the light so the fire and the light it casts cannot drift.\n\nUnder 1 is usually right: the flame is looked at DIRECTLY, where the light's full swing reads as strobing. 0 = a steady flame under a flickering light, which is close to what a real fire does at distance.")]
        [Range(0f, 2f)] public float flameFlickerAmount = 0.6f;

        [Header("Disciplined shadows")]
        [Tooltip("Only the N torches nearest the camera cast shadows; the rest are shadowless fill lights. Point-light shadows are a 6-face cubemap each, so keep this small.")]
        public bool disciplinedShadows = true;
        [Tooltip("Max torches casting shadows at once. 2-4 looks dramatic and stays cheap.\n\nWith 'View biased' on below you can run roughly HALF what you would otherwise need, because no slot is spent on a torch whose shadows land behind you.")]
        public int maxShadowCasters = 3;
        [Tooltip("Give the caster slots to torches whose light can reach what you are LOOKING AT, rather than simply the nearest ones.\n\nWalking a corridor, about half the nearest torches are behind you and their shadows land where nobody can see them — with six slots that is half the budget on nothing, and each slot is a 6-face cubemap. This does not make shadows cheaper by itself; it lets you halve maxShadowCasters for the same perceived quality, which does.\n\nTested against the light's RADIUS, not its position, so a sconce just behind you still qualifies — that one is throwing your own shadow down the corridor ahead.\n\nSHADOWS ONLY. The lights themselves stay radius-culled: a torch behind you really does light the wall in front of you, and view-culling it would darken visible surfaces and pop as you turn.")]
        public bool viewBiasedShadows = true;
        [Tooltip("How much better a torch must score before it takes a caster slot from the one holding it. Needed because view bias makes the selection depend on where you are LOOKING, and turning on the spot reshuffles the ranking far faster than walking does — without a margin, two torches either side of the screen edge swap every update and their shadows blink.")]
        [Range(0f, 0.5f)] public float shadowStealMargin = 0.15f;
        [Tooltip("Fraction of a torch's light range used when asking whether it can reach what you are looking at — in practice, HOW FAR BEHIND YOU A TORCH MAY STILL HOLD A SHADOW SLOT.\n\nThe full radius is far too generous. Measured at range 12, it kept a torch 4m BEHIND the player ranked above one 10m in front: everything within 12m of the view counted as in-view, so the ranking silently collapsed back to nearest-first, which is what view bias was supposed to replace.\n\n0.35 of a 12m range means only torches within about 4m behind still qualify — enough to keep the one throwing your shadow down the corridor ahead, not enough to spend the budget on the room you just left. 1 restores the too-generous behaviour; lower values bias harder toward what is in front of you.")]
        [Range(0.05f, 1f)] public float shadowViewRadiusFactor = 0.35f;
        [Tooltip("How strongly to prefer torches IN FRONT of you when handing out caster slots. A torch is penalised by the square of how far behind the camera it sits.\n\nThe frustum test above is a CLIFF: once a torch passes it, ranking falls back to raw distance — measured, two torches ~3m BEHIND took both slots while one 4.8m in FRONT missed out, all three counting as in-view. This is the smooth version of the same intent, and it is what stops you re-tuning the radius factor every time room geometry changes.\n\nSquared, so it stays gentle for a sconce just off your shoulder — still throwing your shadow down the corridor ahead — and rises quickly for one you have walked well past. 0 disables it and leaves only the frustum cliff.")]
        [Range(0f, 8f)] public float shadowFrontBias = 2f;
        [Tooltip("Log the ranked shadow-caster selection every update: distance, camera-space Z (negative = BEHIND you), the light range the frustum test uses, whether it counted as in-view, and the final score. This is the view that separates \"the flag is off\" from \"the range is so large everything passes\" from \"it is working and that shadow is another torch\".")]
        public bool debugShadowCasters;
        [Tooltip("Runtime toggle for ALL torch shadows, for A/B-ing the look against the cost without regenerating. None disables the key.\n\nThe state is STATIC and survives a regenerate — the same choice NpcPerceptionDebug's F4/F5 make, and for the same reason: a debug switch you have to re-press after every F1 gets used once and then abandoned.")]
        public KeyCode shadowToggleKey = KeyCode.F8;

        [Header("Visual (optional)")]
        [Tooltip("Sconce/torch model, forward axis pointing away from the wall. If the prefab contains its own Light, no extra light is added.")]
        public GameObject[] torchPrefabs;

        [Header("Flame VFX (optional)")]
        [Tooltip("Tint the torch prefab's flame VFX (a VisualEffect anywhere in the prefab) to the SAME per-room-type color the light uses. A shrine's cold-blue light then gets a cold-blue flame. Requires the VFX Graph to expose a Color property (named below) that its color-over-life gradient is multiplied by — script can't reach inside a baked gradient node.")]
        public bool tintFlameToLight = true;
        [Tooltip("Name of the exposed HDR Color property in the flame VFX Graph. Must match exactly. The graph should multiply its color-over-life gradient by this so the flame keeps its bright-core-to-smoke SHAPE while taking the room's hue.")]
        public string flameColorProperty = "Color";

        [Header("Ambient loop (per-torch crackle)")]
        [Tooltip("Fire crackle heard from individual torches, played by a POOL of voices reassigned to whichever torches are nearest — never one AudioSource per torch, which at dungeon torch counts would be 100+ permanent voices against a real budget of 32. Leave the clip list empty to disable. Positional rather than an ambient bed on purpose: a bed cannot pan, and walking a corridor past a sconce is the entire effect.")]
        public TorchAudioSettings torchAudio = new TorchAudioSettings();

        [Header("Culling")]
        [Tooltip("Only torch lights within this distance of the camera are enabled. Sconce meshes are never hidden — only the lights and flicker toggle.")]
        public bool cullTorchLights = true;
        public float cullDistance = 30f;
        [Tooltip("Metres over which a torch fades up from black to its full intensity as it comes into range, ending at cullDistance.\n\nTHIS IS WHAT LETS cullDistance COME IN. A hard on/off pop is what forces a generous cull radius; once the boundary is invisible the radius can be much shorter, which is the actual saving — every live torch is a Forward+ light and the nearest few are 6-face shadow cubemaps.\n\nDistance-driven rather than timed, so pacing back and forth across the edge stays smooth and it needs no hysteresis of its own. 0 restores the old hard cut.")]
        public float cullFadeDistance = 6f;
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
                                       InstancedDungeonRenderer instancer = null, RoomStyle style = null,
                                       WallFaceRegistry wallFaces = null)
        {
            var grid = gen.Grid;
            var root = new GameObject("DungeonTorches");
            root.transform.SetParent(parent, false);

            TorchCullingManager culler = null;
            if (s.cullTorchLights && s.bakeMode != TorchSettings.BakeMode.Baked)
            {
                culler = root.AddComponent<TorchCullingManager>();
                culler.cullDistance = s.cullDistance;
                culler.fadeDistance = s.cullFadeDistance;
                culler.checksPerFrame = s.cullChecksPerFrame;
                culler.disciplinedShadows = s.disciplinedShadows && s.shadows != LightShadows.None;
                culler.maxShadowCasters = s.maxShadowCasters;
                culler.viewBiasedShadows = s.viewBiasedShadows;
                culler.shadowStealMargin = s.shadowStealMargin;
                culler.shadowViewRadiusFactor = s.shadowViewRadiusFactor;
                culler.shadowFrontBias = s.shadowFrontBias;
                culler.debugShadowCasters = s.debugShadowCasters;
                culler.shadowMode = s.shadows;
                culler.shadowToggleKey = s.shadowToggleKey;
            }

            // The crackle pool. Independent of the culler on purpose: torch AUDIO should work
            // whether or not light culling is enabled, and the two answer different questions
            // (which lights are worth drawing vs which torches are worth hearing) at very
            // different ranges. Play mode only — a looping source in the editor while the
            // dungeon is being authored is nobody's idea of a good time.
            TorchAudioPool audioPool = null;
            if (Application.isPlaying && s.torchAudio != null
                && s.torchAudio.loopClips != null && s.torchAudio.loopClips.Length > 0)
            {
                audioPool = root.AddComponent<TorchAudioPool>();
                audioPool.settings = s.torchAudio;
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

                // PIT INTERIORS get no torches. A pit's cells are CellType.Room and its floor
                // reads as floor level, so they qualify on every other test — but a lit chasm
                // reads as another room you can see into rather than a hole you shouldn't fall
                // down, and it robs the pit of the one thing it has: being dark and unknown.
                // (Remove this if you'd rather light them; nothing else depends on it.)
                if (gen.PitAt(c) != null) continue;

                // Torches respect an already-CLAIMED face. Nothing used to claim before this
                // pass ran, so this was a no-op when written — it exists for RecessPropPlacer,
                // which runs first precisely because an alcove has ~3 faces and one hero prop
                // and cannot afford to lose the right wall to a sconce. Regression test if you
                // touch it: torch count and positions must be unchanged on a fixed seed with
                // alcoves disabled.
                foreach (var d in HDirs)
                    if (!Open(c + d) &&                     // solid wall to mount on
                        (wallFaces == null || (wallFaces.TorchAllowed(i, d) &&   // wall asset accepts a torch
                                               !wallFaces.IsClaimed(i, d))))     // and nobody got there first
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

            // VFX exposed-property lookups go through the shader-property id space.
            // Resolve once and remember if the name never matched, so a typo warns
            // exactly once instead of per-torch.
            int flameColorId = Shader.PropertyToID(s.flameColorProperty);
            bool flameTintWarned = false;
            bool flameFlickerWarned = false;

            var accepted = new List<(Vector3Int cell, Vector3Int dir, CellType type)>();
            // Bucket accepted torches by wall plane for cheap distance checks.
            var byPlane = new Dictionary<(Vector3Int dir, int planeCoord, int y), List<Vector3Int>>();

            // SEED WITH AUTHORED TORCHES — sconces spawned from a kit piece's socket, recorded
            // by KitSocketPlacer before this pass runs. Seeding rather than merely claiming the
            // face is what makes an authored torch DISPLACE computed ones instead of adding to
            // them: without it, a face-claim stops a torch on that exact face while nothing
            // prevents one on the next cell along, so a deliberately placed sconce ends up with
            // a computed twin a metre away and the room reads brighter than its palette intends.
            if (wallFaces != null)
            {
                foreach (var (cell, dir) in wallFaces.PreplacedTorches)
                {
                    int seedPlane = dir.x != 0 ? cell.x : cell.z;
                    var seedKey = (dir, seedPlane, cell.y);
                    if (!byPlane.TryGetValue(seedKey, out var seeded))
                    {
                        seeded = new List<Vector3Int>();
                        byPlane[seedKey] = seeded;
                    }
                    seeded.Add(cell);
                }
            }

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
                // Claim this face so wall-mounted props don't overlap a torch.
                wallFaces?.Claim(grid.Index(c), d);
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
                VisualEffect flame = null;

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
                                    flicker = light.gameObject.AddComponent<TorchFlicker>();

                                // SEED EVERY FLICKER, not just the ones created here. A
                                // prefab-authored TorchFlicker arrives with whatever seed was
                                // serialized — the SAME one on every torch in the dungeon — so
                                // the whole place pulses in perfect unison, which reads as one
                                // global brightness animation rather than as separate fires.
                                if (flicker != null)
                                {
                                    flicker.noiseSeed = seed;
                                    flicker.amount = s.flickerAmount;
                                    flicker.speed = s.flickerSpeed;
                                    flicker.flameAmount = s.flameFlickerAmount;
                                }
                                flame = go.GetComponentInChildren<VisualEffect>(true);
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
                        flame = visual.GetComponentInChildren<VisualEffect>(true);
                    }
                    if (light == null)
                    {
                        light = torch.AddComponent<Light>();
                        light.type = LightType.Point;
                    }
                    if (s.flicker && s.bakeMode != TorchSettings.BakeMode.Baked)
                    {
                        // Reuse an authored one rather than adding a second: two flickers on
                        // one light both write intensity every frame, and the result is
                        // whichever ran last, at an amplitude neither was tuned for.
                        flicker = light.GetComponent<TorchFlicker>();
                        if (flicker == null) flicker = light.gameObject.AddComponent<TorchFlicker>();
                        flicker.noiseSeed = Hash(c, 31) % 1000;
                        flicker.amount = s.flickerAmount;
                        flicker.speed = s.flickerSpeed;
                        flicker.flameAmount = s.flameFlickerAmount;
                    }
                }

                // Per-room-type color/intensity from the style; corridors and
                // untyped areas use the torch settings' defaults. Resolved ONCE
                // here so the light and the flame VFX can't drift apart.
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
                    // SEWER CHAMBER BEFORE THE CORRIDOR FALLBACK. A chamber's cells are typed
                    // Hallway, so RoomAt returns null and the corridor branch below would claim
                    // it — leaving the one space in the dungeon you reach on your knees lit
                    // exactly like the corridor you left. Exactly the drift the corridor fix
                    // beneath this was written for, one space further along: the chamber's fog,
                    // walls and props would all say "sewer" while its fire said "hallway".
                    //
                    // A crawl BORE needs no branch here and never will — its cell is solid, so
                    // it has no wall faces and no torch slot can exist in one.
                    else if (gen.IsChamberCell(c))
                    {
                        col = style.ChamberPalette();
                        intensityScale = style.ChamberIntensityScale();
                    }
                    else
                    {
                        // CORRIDORS TAKE THE STYLE'S DEFAULT, not TorchSettings.color. Every
                        // other consumer of the palette already did this — fog, props, kit
                        // emissives and sockets all fall back to defaultTorchColor for a cell
                        // with no room — so leaving torches on their own swatch meant a
                        // corridor's FIRE and its HAZE came from two different colours. That is
                        // precisely the drift §7 exists to prevent, and it showed up as hallway
                        // props taking the hallway hue while the torches beside them did not.
                        //
                        // NB deliberately NOT style.For(RoomType.Generic): a corridor is not an
                        // unauthored room, it is its own place, and a Generic entry must not
                        // silently become the corridor palette. Same distinction hallwayAudio
                        // draws against the default audio profile.
                        col = style.defaultTorchColor;
                        intensityScale = style.defaultIntensityScale > 0f
                            ? style.defaultIntensityScale : 1f;
                    }
                }

                // Tint the flame to match. The color-over-life gradient in the
                // graph supplies the SHAPE (bright core -> smoke); this exposed
                // property supplies the hue, so a blue-lit room burns blue.
                bool flameHasColor = flame != null && flame.HasVector4(flameColorId);

                if (flame != null && s.tintFlameToLight)
                {
                    if (flameHasColor)
                        flame.SetVector4(flameColorId, col);
                    else if (!flameTintWarned)
                    {
                        flameTintWarned = true;
                        Debug.LogWarning($"[Dungeon] Torch flame VFX has no exposed Color property named " +
                            $"'{s.flameColorProperty}' — flames won't tint to the room color. Add an exposed " +
                            $"HDR Color property with that exact name to the flame VFX Graph and multiply its " +
                            $"color-over-life gradient by it. (Warning shown once.)", flame);
                    }
                }

                // FLAME FLICKER IS INDEPENDENT OF FLAME TINT. Whether the fire pulses and
                // whether it takes the room's hue are different questions, and nesting the
                // first inside the second meant turning tinting off silently froze the flame —
                // with flameFlickerAmount visibly doing nothing across its whole range.
                if (flame != null && flicker != null)
                {
                    if (flameHasColor)
                    {
                        // Pulse around whatever colour the flame ACTUALLY has: the room's if
                        // it was just tinted, the graph's own authored one if not. Reading it
                        // back is what lets an untinted flame still flicker without this
                        // overwriting the colour the artist chose.
                        Color flameBase = s.tintFlameToLight ? col : (Color)flame.GetVector4(flameColorId);
                        flicker.SetFlame(flame, s.flameColorProperty, flameBase);
                    }
                    else if (!flameFlickerWarned)
                    {
                        flameFlickerWarned = true;
                        Debug.LogWarning($"[Dungeon] Torch flame VFX has no exposed Vector4/Color property " +
                            $"named '{s.flameColorProperty}', so flameFlickerAmount will do nothing — the " +
                            $"flame cannot be pulsed without a property to scale. The LIGHT still flickers, " +
                            $"which is why this looks like only the VFX setting is broken. (Warning shown once.)", flame);
                    }
                }

                if (light != null)
                {
                    // HUE ONLY. Unity multiplies a Light's colour into its output, so an HDR
                    // palette swatch brightened the light as well as the flame — raising a
                    // room's colour intensity to make the fire bloom also flooded its walls,
                    // which is exactly the coupling intensityScale is supposed to control.
                    // Magnitude is dropped here and comes from intensityScale alone; the flame
                    // VFX above still gets the raw HDR colour, because bloom IS the flame.
                    light.color = RoomStyle.Hue(col);
                    light.intensity = s.intensity * intensityScale;
                    light.range = s.range;

                    // HAND THE INTENSITY TO THE FLICKER, or it discards it. TorchFlicker
                    // captures its base at Awake — which ran inside the Instantiate above,
                    // BEFORE this line — and then rewrites light.intensity from that captured
                    // value every frame. Without this the room's intensityScale survives a
                    // single frame and is then overwritten by the prefab's authored intensity:
                    // a brief bright flash at load, after which every value from 0 to 100
                    // looks identical.
                    if (flicker != null) flicker.SetBaseIntensity(light.intensity);
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

                // Register the torch's WORLD POSITION for the crackle pool. Registered from
                // `pos` rather than from the light, so a torch with no Light (baked mode, or a
                // sconce whose light was culled out of existence) still crackles — the fire is
                // visible in the flame VFX either way.
                if (audioPool != null) audioPool.Register(pos);
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
        [Tooltip("Set by TorchPlacer. Metres over which a torch fades in as it enters cull range.")]
        public float fadeDistance = 6f;
        public int checksPerFrame = 750;

        [Tooltip("Set by TorchPlacer.")]
        public bool disciplinedShadows;
        public int maxShadowCasters = 3;
        public LightShadows shadowMode = LightShadows.Soft;
        [Tooltip("How often (seconds) to recompute which torches cast shadows.")]
        public float shadowUpdateInterval = 0.2f;
        [Tooltip("Set by TorchPlacer. Bias the caster slots toward torches whose light can reach what you are looking at.")]
        public bool viewBiasedShadows = true;
        [Tooltip("Set by TorchPlacer. How much better a challenger must score to steal a caster slot.")]
        public float shadowStealMargin = 0.15f;
        [Tooltip("Set by TorchPlacer. Fraction of light range used by the frustum test.")]
        public float shadowViewRadiusFactor = 0.35f;
        [Tooltip("Set by TorchPlacer. How strongly to prefer torches in front of the camera.")]
        public float shadowFrontBias = 2f;
        [Tooltip("Set by TorchPlacer. Log the ranked caster selection each update.")]
        public bool debugShadowCasters;
        [Tooltip("Set by TorchPlacer. None disables the runtime toggle.")]
        public KeyCode shadowToggleKey = KeyCode.F8;

        /// <summary>
        /// Runtime master switch for every torch shadow in the dungeon.
        ///
        /// STATIC, so it survives a regenerate — the manager is rebuilt with the dungeon on every
        /// F1, and a per-instance flag would silently revert the moment you made a new one, which
        /// is exactly how a debug switch stops being used. `NpcPerceptionDebug`'s sight/hearing
        /// switches are static for the same reason, and carry the same play-mode reset: a static
        /// keeps its value across fast-enter-playmode, so without one the editor would come up
        /// with shadows still off from the last session and nothing on screen saying why.
        /// </summary>
        public static bool ShadowsEnabled = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ShadowsEnabled = true;

        struct Entry
        {
            public Light Light;
            public Behaviour Flicker; // may be null
            public TorchFlicker Flick; // same component, typed, for SetBaseIntensity
            public Vector3 Pos;
            /// <summary>Light range, cached so the frustum test needn't touch the native object.</summary>
            public float Range;
            /// <summary>Authored intensity at registration — what a fade of 1 restores.</summary>
            public float FullIntensity;
            /// <summary>Last fade applied, so an unchanged value writes nothing.</summary>
            public float LastFade;
        }

        readonly System.Collections.Generic.List<Entry> entries
            = new System.Collections.Generic.List<Entry>();
        int cursor;
        Vector3 lastEditorCamPos = new Vector3(float.PositiveInfinity, 0f, 0f);
        float shadowTimer;

        // Reused scratch for the nearest-N shadow selection.
        readonly System.Collections.Generic.List<int> shadowCandidates
            = new System.Collections.Generic.List<int>();
        readonly System.Collections.Generic.List<float> shadowScores
            = new System.Collections.Generic.List<float>();
        // Reused, because CalculateFrustumPlanes(Camera) allocates a fresh array every call and
        // this runs several times a second.
        readonly Plane[] frustumPlanes = new Plane[6];
        // The camera the sweep last resolved. Held rather than re-fetched so the caster pass and
        // the cull pass cannot disagree about which camera they are reasoning from.
        Camera activeCam;
        readonly System.Collections.Generic.HashSet<Light> currentCasters
            = new System.Collections.Generic.HashSet<Light>();
        readonly System.Collections.Generic.List<Light> nextCasters
            = new System.Collections.Generic.List<Light>();

        public void Register(Light light, Behaviour flicker)
        {
            // The authored intensity is READ HERE, which is only correct because TorchPlacer has
            // already assigned light.intensity AND pushed it through SetBaseIntensity by the time
            // it registers. Reading it any earlier would capture the prefab's value instead of
            // the room's palette — the same ordering trap that made intensityScale look dead.
            entries.Add(new Entry
            {
                Light = light,
                Flicker = flicker,
                Flick = flicker as TorchFlicker,
                Pos = light.transform.position,
                Range = light.range,
                FullIntensity = light.intensity,
                LastFade = 1f,
            });
        }

        // Sentinel rather than a bool: the FIRST Update must always apply, because TorchPlacer
        // assigns each light's shadow mode as it spawns it. Regenerating with the toggle off
        // would otherwise bring a fresh dungeon back with shadows on and the switch still
        // reading "off".
        int appliedShadowState = -1;

        void Update()
        {
            if (entries.Count == 0) return;

            if (Application.isPlaying && shadowToggleKey != KeyCode.None
                && Input.GetKeyDown(shadowToggleKey))
            {
                ShadowsEnabled = !ShadowsEnabled;
                PlayerMessage.Show(ShadowsEnabled ? "Torch shadows ON" : "Torch shadows OFF");
            }

            Vector3 camPos;
            bool haveCam = false;

            if (Application.isPlaying)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                activeCam = cam;
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
                activeCam = sv.camera;
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

            // The master switch is applied on CHANGE only, so the ordinary path costs one int
            // compare per frame and nothing is written to a Light that is already correct.
            int want = ShadowsEnabled ? 1 : 0;
            if (appliedShadowState != want)
            {
                appliedShadowState = want;
                ApplyShadowMaster(camPos, haveCam);
            }

            // Disciplined shadows: throttled, picks the nearest N enabled
            // torches to cast; all others stay shadowless.
            if (haveCam && ShadowsEnabled && disciplinedShadows)
            {
                shadowTimer -= Time.deltaTime;
                if (shadowTimer <= 0f)
                {
                    shadowTimer = shadowUpdateInterval;
                    UpdateShadowCasters(camPos);
                }
            }
        }

        /// <summary>
        /// Push the master switch onto every torch.
        ///
        /// Turning shadows back ON has to route through whichever selection is in force, not
        /// simply restore `shadowMode` everywhere: under disciplined shadows only the nearest N
        /// may cast, and blanket-restoring would put every torch in the dungeon into the shadow
        /// atlas at once — the exact cost the discipline exists to prevent, arriving as "the
        /// toggle tanked my framerate" rather than as an obvious mistake.
        /// </summary>
        void ApplyShadowMaster(Vector3 camPos, bool haveCam)
        {
            if (!ShadowsEnabled)
            {
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].Light != null) entries[i].Light.shadows = LightShadows.None;
                currentCasters.Clear();
                return;
            }

            if (disciplinedShadows)
            {
                // Immediately, not on the next tick — a toggle that takes up to
                // shadowUpdateInterval to show reads as not having worked.
                shadowTimer = shadowUpdateInterval;
                if (haveCam) UpdateShadowCasters(camPos);
                return;
            }

            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Light != null && entries[i].Light.enabled)
                    entries[i].Light.shadows = shadowMode;
        }

        void UpdateShadowCasters(Vector3 camPos)
        {
            // Collect enabled torches within cull range, then keep the nearest
            // maxShadowCasters as casters. A partial selection sort avoids a
            // full sort of hundreds of lights.
            // SLOTS GO TO TORCHES WHOSE LIGHT CAN REACH WHAT YOU ARE LOOKING AT.
            //
            // Ranking by raw distance alone spent slots on torches BEHIND the player, whose
            // shadows land where nobody can see them — with six slots that was routinely half the
            // budget, and each slot is a 6-face cubemap. Reallocating is free (same count, same
            // cost); the SAVING comes from then being able to run fewer casters for the same
            // perceived quality.
            //
            // THE TEST IS THE INFLUENCE SPHERE, NOT THE TORCH POSITION, and that distinction is
            // what stops this being a regression. A sconce two metres behind you is out of frame
            // but its 7m radius very much is not — it is what throws YOUR shadow down the corridor
            // ahead, one of the better effects the dungeon produces. Testing position alone would
            // discard exactly that. Only torches whose lit volume cannot touch the frustum at all
            // are demoted.
            //
            // NB THIS IS FOR SHADOWS ONLY. The same test must never gate the LIGHTS: a torch
            // behind you genuinely illuminates the wall in front of you, and culling by view
            // would both darken visible surfaces and pop as you turn — which the distance fade
            // cannot smooth, being driven by distance rather than angle.
            bool useFrustum = viewBiasedShadows && activeCam != null;
            Vector3 camForward = useFrustum ? activeCam.transform.forward : Vector3.forward;
            if (useFrustum) GeometryUtility.CalculateFrustumPlanes(activeCam, frustumPlanes);

            // Big enough that ANY in-view torch outranks EVERY out-of-view one, rather than
            // merely being nudged ahead of it.
            float outOfView = cullDistance * cullDistance * 4f;

            shadowCandidates.Clear();
            shadowScores.Clear();
            float sq = cullDistance * cullDistance;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Light == null || !e.Light.enabled) continue;
                float dsq = (e.Pos - camPos).sqrMagnitude;
                if (dsq > sq) continue;

                // THE TEST RADIUS IS A FRACTION OF THE LIGHT RANGE, NOT THE WHOLE THING, and that
                // fraction is the entire usefulness of this feature. Measured with a range of 12m,
                // the full radius kept a torch 4m BEHIND the player ranked ahead of one 10m in
                // front — it passed the sphere test comfortably, took no penalty, and then won on
                // raw distance. Every torch within ~12m of the frustum was "in view", so the
                // ranking silently collapsed back to nearest-first, which is what it was supposed
                // to replace. Only at 12.3m behind did anything finally fail.
                //
                // A torch's light REACHES a long way; the question here is narrower — does enough
                // of its influence land on screen to be worth a cubemap. The fraction is the
                // answer to that, and it is authored because it depends on range, corridor width
                // and how much of the shadow-thrown-forward effect you want to keep.
                float score = dsq;
                float testRadius = e.Range * Mathf.Clamp01(shadowViewRadiusFactor);
                if (useFrustum && !SphereInFrustum(e.Pos, testRadius)) score += outOfView;

                // A GRADED PENALTY FOR BEING BEHIND YOU, because the frustum test alone is a
                // CLIFF. Once a torch passes it, ranking falls back to raw distance — so measured
                // here, two torches 3.2m and 3.1m BEHIND took both slots while one 4.8m in FRONT
                // missed out, all three "in view". Tightening the radius factor fixes one such
                // case and leaves you re-tuning a threshold every time room geometry changes,
                // because the question was never really "is it in the frustum" but "how much of
                // this torch's usefulness is in front of me".
                //
                // Squared, so it is gentle for a sconce just off your shoulder — which is still
                // throwing your shadow down the corridor ahead and worth a slot — and rises
                // quickly for one you have walked well past. Dot against camera forward rather
                // than InverseTransformPoint: same number, no managed-to-native call per
                // candidate, and this runs over every torch in range several times a second.
                if (useFrustum && shadowFrontBias > 0f)
                {
                    float behind = -Vector3.Dot(e.Pos - camPos, camForward);
                    if (behind > 0f) score += behind * behind * shadowFrontBias;
                }

                // INCUMBENT BONUS — hysteresis, and it became necessary the moment selection
                // started depending on camera ROTATION as well as position. §10b recorded that
                // shadow casters need no steal margin where torch audio does; that was true while
                // ranking was position-only, and turning on the spot reshuffles the set far faster
                // than walking ever did. Without it, two torches either side of the frustum edge
                // trade a slot every update and their shadows blink.
                if (currentCasters.Contains(e.Light)) score *= (1f - Mathf.Clamp01(shadowStealMargin));

                shadowCandidates.Add(i);
                shadowScores.Add(score);
            }

            int keep = Mathf.Min(maxShadowCasters, shadowCandidates.Count);

            // Partial selection sort: best `keep` to the front, avoiding a full sort of hundreds.
            for (int a = 0; a < keep; a++)
            {
                int best = a;
                float bestScore = shadowScores[a];
                for (int b = a + 1; b < shadowCandidates.Count; b++)
                    if (shadowScores[b] < bestScore) { best = b; bestScore = shadowScores[b]; }

                (shadowCandidates[a], shadowCandidates[best]) = (shadowCandidates[best], shadowCandidates[a]);
                (shadowScores[a], shadowScores[best]) = (shadowScores[best], shadowScores[a]);
            }

            // WHY A DEBUG THAT PRINTS THE RANKING RATHER THAN THE RESULT. "Torches behind me are
            // casting" has several causes that look identical from the screen — the flag off, the
            // wrong camera, a light range so large that everything passes the sphere test, or the
            // selection working and the shadow belonging to a different torch. `z` is the torch
            // in CAMERA SPACE, so negative is behind you: that one column separates "the test
            // says in-view" from "it is actually in front of me", which is the disagreement at
            // the heart of every one of those causes.
            if (debugShadowCasters)
            {
                var dbg = new System.Text.StringBuilder();
                dbg.Append($"[Torch] casters: viewBias={viewBiasedShadows} " +
                           $"cam={(activeCam != null ? activeCam.name : "<null>")} " +
                           $"useFrustum={useFrustum} keep={keep}/{shadowCandidates.Count} " +
                           $"margin={shadowStealMargin} cull={cullDistance}");
                int show = Mathf.Min(shadowCandidates.Count, keep + 4);
                for (int a = 0; a < show; a++)
                {
                    var e = entries[shadowCandidates[a]];
                    float d = Vector3.Distance(e.Pos, camPos);
                    // THE SAME RADIUS THE SCORE USED, not the full range. Reporting the raw range
                    // here once made the log contradict the ranking it was explaining — inView
                    // read true beside a score that plainly carried the out-of-view penalty. Same
                    // discipline as ComputeZones backing both the placer and its gizmo: a debug
                    // view that can disagree with the code will, and then it costs a round.
                    float testR = e.Range * Mathf.Clamp01(shadowViewRadiusFactor);
                    bool inView = !useFrustum || SphereInFrustum(e.Pos, testR);
                    float z = activeCam != null
                        ? activeCam.transform.InverseTransformPoint(e.Pos).z : 0f;
                    dbg.Append($"\n  {(a < keep ? "CAST" : "  - ")} d={d,5:0.0}m  z={z,6:0.0}  " +
                               $"testR={testR:0.0}  inView={inView,-5}  score={shadowScores[a]:0}");
                }
                Debug.Log(dbg.ToString(), this);
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

        /// <summary>
        /// Can a light at <paramref name="c"/> with radius <paramref name="r"/> touch the view?
        /// Sphere against the six frustum planes — outside if it is fully behind any one of them.
        /// </summary>
        bool SphereInFrustum(Vector3 c, float r)
        {
            for (int i = 0; i < 6; i++)
                if (frustumPlanes[i].GetDistanceToPoint(c) < -r) return false;
            return true;
        }

        void SlicedSweep(Vector3 camPos)
        {
            int n = Mathf.Min(checksPerFrame, entries.Count);
            for (int k = 0; k < n; k++)
            {
                cursor++;
                if (cursor >= entries.Count) cursor = 0;
                Apply(cursor, camPos);
            }
        }

        void FullSweep(Vector3 camPos)
        {
            for (int i = 0; i < entries.Count; i++)
                Apply(i, camPos);
        }

        /// <summary>
        /// Distance fade for a torch entering or leaving cull range.
        ///
        /// DISTANCE-DRIVEN, NOT A TIMER, and that is what makes it robust. A timed fade restarts
        /// awkwardly when you cross the boundary twice — pace back and forth at the edge and it
        /// stutters — whereas a fade derived from distance is monotonic, needs no per-entry clock,
        /// and is symmetric in both directions for free. It is also its own hysteresis, which is
        /// why there is no separate margin here the way DungeonRendererCulling needs one.
        ///
        /// IT MUST GO THROUGH `SetBaseIntensity`, NEVER `Light.intensity`. TorchFlicker captures
        /// its base once and rewrites the light from that base EVERY Update, so it is a write-only
        /// owner of that property (§5) and a direct assignment here would be discarded a frame
        /// later with no error. Torches with no flicker are written directly, which is the only
        /// case where that is correct.
        ///
        /// The payoff is being able to cull CLOSER: a hard pop is what forces a generous
        /// cullDistance, and once the boundary is invisible the radius can come in.
        /// </summary>
        float FadeAt(in Entry e, Vector3 camPos)
        {
            float dsq = (e.Pos - camPos).sqrMagnitude;
            float cull = cullDistance;
            if (dsq >= cull * cull) return 0f;
            if (fadeDistance <= 0f) return 1f;

            // Square-space early-out first, so the sqrt only runs for torches actually inside the
            // fade shell — a thin band, against a sweep that visits hundreds of entries a frame.
            float inner = Mathf.Max(0f, cull - fadeDistance);
            if (dsq <= inner * inner) return 1f;
            return Mathf.Clamp01((cull - Mathf.Sqrt(dsq)) / fadeDistance);
        }

        void Apply(int index, Vector3 camPos)
        {
            Entry e = entries[index];
            if (e.Light == null) return;

            float fade = FadeAt(e, camPos);
            bool on = fade > 0f;

            // Written only on a real change: at fade 1, which is nearly every torch in range,
            // this costs one float compare rather than a managed-to-native property write.
            if (on && !Mathf.Approximately(fade, e.LastFade))
            {
                float want = e.FullIntensity * fade;
                if (e.Flick != null) e.Flick.SetBaseIntensity(want);
                else e.Light.intensity = want;
                e.LastFade = fade;
                entries[index] = e;
            }

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
                // UNDISCIPLINED lights are set once at spawn and never revisited, so a torch
                // that was out of range when the master switch flipped would keep whatever it
                // had — coming back shadowless after a toggle ON, or casting after a toggle OFF.
                // The switch has to be re-applied wherever a light re-enters range.
                else if (on && !disciplinedShadows)
                {
                    LightShadows want = ShadowsEnabled ? shadowMode : LightShadows.None;
                    if (e.Light.shadows != want) e.Light.shadows = want;
                }
            }
        }
    }
}