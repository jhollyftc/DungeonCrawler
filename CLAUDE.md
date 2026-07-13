# CLAUDE.md — Procedural Dungeon Crawler Generator

Context for AI coding sessions. Read this first. It captures architecture,
hard-won conventions, and the reasoning behind them so a session starts informed
instead of re-deriving (or re-breaking) things that are already settled.

---

## 1. What this project is

A procedural 3D dungeon crawler generator in **Unity (URP, Forward+ rendering)**,
C#, inspired by vazgriz's dungeon generator and extended well past it. Cosmetic-
first: the world, its variety, and its atmosphere come before combat. First-
person, stylized (toon-shaded) fantasy dungeon.

**Long-term vision — the "growing dungeon" roguelite:** a run starts small (~4–5
rooms); if the player survives they port to a home base to sell/replenish, then
venture out again into a deeper, larger, more dangerous dungeon with new room
types and loot. **Depth is the master progression parameter.** Every system that
scales should scale as a function of run depth.

Cell size = **3 meters**. Grid is 3D (multi-story via stairs). Legacy Input
system ("Both" in project settings).

Repo: https://github.com/jhollyftc/DungeonCrawler (private). Commit per
feature; push after commit.

---

## 2. Golden rules (violating these has caused real bugs)

1. **The nominal grid is the single source of truth.** World position of cell
   `c` is `c * cellSize`. Greybox collision, the player, torch lights, and props
   all live at nominal coordinates. Only the *visual kit* deviates (see rule 2).

2. **`globalVisualOffset` is a KIT-ONLY correction, never a world transform.**
   The kit's Blender assets were authored with origin 1.5 units above the
   geometry base, so kit pieces (walls/floors/ceilings/arches/doors/columns) get
   `globalVisualOffset` (+1.5 Y) to sit correctly on the greybox. **Props, lights,
   spawns, and anything placed at nominal grid coordinates must NOT add this
   offset** — doing so double-corrects them into the air. (This exact bug put
   scatter props 1.5m off the floor.) The permanent fix is re-origining the kit
   assets to their base in Blender and zeroing the offset; until then, the offset
   is a kit-local quirk everything else must ignore.

3. **File name = class name for any MonoBehaviour/ScriptableObject that is
   serialized onto a prefab or asset.** Unity binds by file name. This bit us
   twice (PlayerInteractor, TorchFlicker). Split multi-class files accordingly.

4. **Determinism: same (seed, depth) → same dungeon.** All procedural choices
   draw from the seeded `System.Random` (generator) or the position-hash
   `Hash(cell, salt)` (placers). Never use `UnityEngine.Random` or unsalted
   iteration order for anything that must be stable. When adding RNG draws to a
   pipeline stage, know it shifts the stream for later stages (acceptable at the
   end of the pipeline; risky if inserted mid-pipeline).
   **Placement passes use separate hash streams** (`HashStream`, per-room):
   feature 11001, scatter 11002, ceiling 11003, sockets 11004. Tuning one pass
   must never shift another's placements — new passes get their own stream
   constant, never a shared counter.

5. **Placement reasons in cells, positions freely within them.** Occupancy,
   spacing, and adjacency use integer grid cells; final world positions apply
   deterministic sub-cell jitter + yaw so the 3m grid disappears visually. Keep
   jitter within a safe margin (~cellSize*0.5 - 0.7m) so props clear walls and
   corner posts. **Never derive a grid cell's Y from a float world Y at a story
   boundary** — a chair authored exactly at floor height comes out of matrix
   math at y ≈ ±0.0001 and FloorToInt puts it a story down (this silently
   skipped every socket child). Children inherit their parent's cell Y.

6. **Respond to compiler errors and screenshots literally.** Field-report bugs
   (especially the pillar classifier) are best fixed against the exact geometry
   shown. Don't refactor speculatively.

---

## 3. Pipeline (DungeonGenerator.Generate())

Order matters; several stages depend on earlier ones. Current order:

1. **PlaceRooms** — size-classed, largest-first. Builds a size *plan* (one
   guaranteed grand room when throne is legal at depth; depth-scaled large rooms;
   random fill), sorts largest-first (big rooms fit an empty grid best and
   distribute well), places each with a per-entry attempt budget. Rooms may be
   **irregular** (L/T/plus/notch via corner *bites*; straight walls only, never
   circular). A `Room` carries `Bounds` (bounding box) AND a `Cells` HashSet
   (actual footprint). Overlap/fill/door/torch/column logic reads the footprint.
2. **Triangulate** — Delaunay3D (Bowyer–Watson, double precision, jittered
   centers) over room centers.
3. **BuildGraph** — Kruskal MST + loop edges (scored by detour ratio;
   maxLoopEdges, minLoopDetourRatio).
4. **CarveHallways** — stair-aware A* (HallwayPathfinder). Stairs are atomic
   macro-edges; sealed-envelope rule keeps 13 cells around a stair solid;
   `SurroundingsOk` predicate validates corridor cells and filters door
   candidates. Multi-source door-candidate seeding from room perimeters.
5. **AllocateInteriorStairs** — elevated doors (floor+1) get an interior
   staircase through the doorway. The stair must not consume another door's
   threshold cell (elevated corner door above a ground door — real bug that
   walled off a required route) and, after tentative placement, every
   ground-level threshold + stair foot in the room must remain mutually
   reachable (flood-fill; a stair strip can pinch a small room in two).
   Conflicts revert and demote to the doorless drop-in fallback, which the
   ladder pass picks up next.
6. **AllocateLadders** — every drop-in (`IsElevated && !HasInteriorStair`)
   claims the column of room cells beneath its threshold for a wall-mounted
   ladder (`gen.Ladders`), keeping the entrance two-way. Validates the climb
   column is open and the mount wall solid; failure leaves a one-way drop.
   Deterministic, no RNG. Ladder feet are reserved cells in the prop system.
7. **PlacePrisons** (Stage 5 in code comments — numbering is historical, ignore
   the labels) — 1-tall closet cells off hallways, one-opening rule.
8. **AssignRoomTypes** — see §4.
9. **PlaceSatelliteRooms** — type-paired closets (see §4).
10. **PlanInteriorColumns** — lattice-point column plans for grand rooms (see §4).

> The stage-number comments in code (`Stage 5`, `Stage 6`…) are historical and
> out of order after several insertions. Trust the `Generate()` call order, not
> the comment labels.

---

## 4. Room typing, satellites, columns

**RoomType enum** (in DepthProfile.cs): Generic, Start, Exit, ThroneRoom,
Merchant, Barracks, Kitchen, Library, Shrine, and satellite types ChestVault,
Treasury, Armory, Pantry, Study, Reliquary.

**Typing (AssignRoomTypes)** — decisions locked with the user:
- **Start/Exit** = the two ends of the MST diameter (double-BFS on hop distance).
  Exit is a distinct portal-out room, NOT the boss room.
- **Merchant** — ON the critical path (start→exit), mid-path, so it's reliably
  found. Hard cap 1. Gated by depth.
- **Throne** — largest room OFF the critical path (optional reward). Hard cap 1.
  Gated by depth. Throne is optional treasure, not the exit.
- **Categories** (barracks/kitchen/library/shrine) — soft depth-scaled counts,
  assigned to remaining rooms largest-first.
- Singletons are **hard caps**; categories are **soft counts**. Rest = Generic.

**Satellites (PlaceSatelliteRooms)** — closets that hang off a host room:
- Typed by host: Throne→Treasury (**guaranteed**), Barracks→Armory,
  Kitchen→Pantry, Library→Study, Shrine→Reliquary, Generic→ChestVault (all
  **chanced**). Rules live on DepthProfile (guaranteed vs chanced lists).
- **NOT part of the Delaunay/MST graph** — that's what makes them closets. One
  physical door to the host, reachable only through it.
- 1 wide (shared-wall axis) × 2 deep, so exactly one cell touches the host — a
  clean single doorway. `SatelliteFits` requires exactly one Room adjacency and
  zero hallway/stair/prison adjacency.
- Start/Exit/Merchant never host. One satellite per host.

**Interior columns (PlanInteriorColumns)** — free-standing columns for grand
rooms:
- At **cell-corner lattice points** (where 4 floor tiles meet), NOT cell centers
  — slender, not chunky. They occupy no grid cells (floor stays walkable);
  collision comes from the prefab collider.
- Column prefab is **one cell (3m) tall**; segments are **stacked** to span
  floor→ceiling (a 2-story hall gets 2 segments — no stretching).
- Rules on DepthProfile: throne always, library/generic chanced, min room edge.
  Spacing (default every 2 lattice pts) and wall inset configurable. Skips
  lattice points adjacent to doorways and any point whose 4 cells aren't all real
  footprint cells (no columns in an L-bite).

---

## 5. Rendering architecture (performance-critical — read before touching)

**Two geometry modes** (DungeonVisualizer.GeometryMode):
`GeneratedMesh` (debug greybox), `PrefabKit` (GameObjects), `InstancedKit`
(instanced — the shipping path).

**Collision truth = the greybox mesh (DungeonMesher)**, anchored to the grid,
rendered invisible in InstancedKit mode. The kit is *visual only*. Exceptions —
pieces whose real shape matters get their own prefab colliders (the greybox
can't provide them): archways, doors, interior columns, ladders (base-origin
authored, one 3m segment per story, stacked — NO globalVisualOffset, prop
convention), **stairs and corner pillars** (the latter two route through
`Enumerate`'s `placeWithCollider` sink
→ `EmitCollider` → `PropTier.StaticCollider`, collider GameObjects under a
`DungeonKitColliders` root). When the kit has stair prefabs, the greybox's
approximate sloped ramp is skipped (`includeStairRamps=false`) so the prefab's
authored stepped collider is the sole walking surface — two colliders that
disagree about the floor was the original stair-collision bug.

**`wallMargin`** (DungeonVisualizer, meters): insets the greybox's wall faces
toward the room so the invisible collider sits flush with the kit's decorative
wall relief (cobblestone etc.) instead of behind it. Size it to the kit's
worst-case protrusion. Walls only; floors/ceilings/stairs untouched.

**InstancedDungeonRenderer** — batches by `(mesh, submesh, material,
castShadows)` ONLY (NOT by chunk), so all like geometry consolidates into few
large batches. Per-frame per-instance distance cull packs visible instances
into a reusable scratch array (`renderDistance`, a true radius). `Commit()` is
idempotent/additive — call after each placement pass. **Batching and culling
are deliberately decoupled**; an earlier chunk-keyed version fragmented batches
(thousands of ~25-instance draws) and is the wrong approach — don't reintroduce
chunk-keyed batching.

**Per-batch shadow casting** — batches carry a `castShadows` flag (part of the
BatchKey). **The static kit shell (walls/floors/ceilings) casts NO shadows —
receive only.** Wall-on-wall shadows are invisible, but thousands of shell
instances redrawn into every shadowed torch's six cubemap faces were THE
torch-shadow performance killer (Shadows.Draw 371 → 136, frame 501 → 272).
Detail geometry — columns, arches, stairs, pillars, props, torches — keeps
casting (PropInstancer's `castShadows` param, default true). Lesson: shadow-
pass cost is geometry COUNT per cube face, not per-face quality — cull WHAT
casts, not just how nicely. (Prison bars ride the shell path and also don't
cast; deliberate scope line, revisit if their shadows are missed.)

**PropInstancer** — the general "how anything gets placed" system. Splits a
prefab into MESH (→ instancer, batched) and FUNCTION (→ a GameObject with
colliders/lights/logic). Four **PropTier**s:
- `StaticDecor` — mesh only, no GameObject. Rubble, bones.
- `StaticCollider` — mesh instanced + collider GameObject. Arches, gates,
  columns, crates.
- `InstancedMeshWithLight` — mesh instanced + Light/flicker GameObject stays
  individual. Torches, candles.
- `FullGameObject` — nothing instanced. Movers/interactives: doors, gates.
- **Invariant:** the mesh path passes ONLY the placement rotation;
  `AddInstance`/`BuildProto` composes the prefab's root rotation internally.
  Composing root rotation on the mesh path too = double rotation (real bug).
  Corollary for sockets: a socket's world pose is computed from the parent's
  FULL visual pose (placement * root rotation/scale), and the child's own
  placement then passes placement-only rotation again.

**Tier assignment rule:** "is this mesh one of many identical *static* copies?"
decides instanced-vs-GameObject, independent of what it does. High-count static →
instanced; movers/interactives → GameObject.

**Torch culling (TorchCullingManager)** — sliced per-frame distance cull of torch
lights + **disciplined shadows**: only the nearest `maxShadowCasters` (default 3)
torches cast shadows; the rest are shadowless fill. **Point-light shadows are a
6-face cubemap each** — see the per-batch shadow section above for the second
half of this fix. The stats overlay counts submissions (incl. shadow passes),
not objects; the generator's `Instanced: N pieces` log is the true count. When
a metric is implausible, interrogate the metric before optimizing against it.

**Material/atlasing note:** each distinct material = a separate instanced batch.
Multi-material assets multiply batches (a 3-material arch = 3 instances/batches
per placement). Plan is to atlas multi-material kit assets down to 1 material
each. The toon shader's packed-mask + normal support (see §6) is what lets an
atlased single-material asset keep its per-pixel material variety.

---

## 6. The toon shader (Dungeon/ToonLit, URP)

Per-material lit toon shader — NOT post-process (post-process would band the
textures too; we only want to band the *lighting*). Passes: ForwardLit, Outline
(inverted hull), ShadowCaster, DepthOnly.

- **Banded diffuse** for main + additional lights via `Ramp()` (`_Bands`,
  `_BandSoftness`). Torch attenuation is banded INSIDE the ramp — stepped light
  pools (the signature look).
- **No directional light in-game** — `_ShadowTint` is the ambient floor and the
  single most important color (it IS the darkness). Pair with torch color.
- **Forward+ required** — the additional-light loop uses `LIGHT_LOOP_BEGIN/END`
  (cluster macros). Set URP asset Rendering Path = Forward+, Additional Lights =
  Per Pixel. Plain Forward caps additional lights per object and starves the big
  instanced batch.
- **Inverted-hull outline** — black shell, front-faces culled. Per-object, rides
  instancing. Not screen-space.
- **Banded specular glint** — toon highlight, gated by the light's banded
  diffuse.
- **Packed PBR mask** (`_MaskMap`: G=roughness, B=metallic) modulates the glint
  per-pixel (rough=matte, metal glints tinted by albedo). Default black = uniform
  glint = pre-mask behavior. Import the mask with **sRGB OFF** (it's data).
- **Normal map** (`_BumpMap`, `_BumpScale`) — perturbs all lighting. Import as
  **Normal map** type. Strong normals + razor-hard bands = crawling band edges;
  `_BumpScale` trades relief vs. band cleanliness.
- Known API gotchas: `TransformObjectToHClip` (NOT `TransformObjectToWorldHClip`).

---

## 7. RoomStyle — per-type architectural identity (ScriptableObject)

One RoomStyle asset defines a room type's whole look. What it holds:
- **Torch palette** per type: color (HDR), intensity scale, spacing scale.
  Corridors/untyped use the defaults. This is type-driven lighting — a shrine
  glows cold-blue, a treasury gold, *before any prop exists*. Cheapest, highest-
  impact atmosphere. The palette also drives **dynamic fog** (§10).
- **Banded walls** (`WallSet` per type; `WallAsset` with Bottom/Middle/Top band
  checkboxes + `maxPerRoom` cap). Bands are semantic (Bottom = floor course,
  Top = ceiling course, Middle = between; single-story & hallways = Bottom), NOT
  floor-numbered — a drain checked Bottom-only is correct at any room height.
  **Strict bands:** a band with no eligible asset falls back to the KIT generic,
  never borrows another band's assets (borrowing = floating drains).
- **Wall placement flags** (`WallAsset.allowPropsInFront` / `allowTorch`) — the
  start of the **wall real estate** system. Which asset lands on a face is only
  decided inside DungeonKitPlacer's emission (hash picks + capped reservations),
  so the kit placer RECORDS restricted faces into a `WallFaceRegistry` (keyed
  by open cell + direction) as walls emit; TorchPlacer and RoomPropPlacer QUERY
  it afterward (build order guarantees walls first). A recessed candle niche:
  both flags off — no snapped fountain in front, no sconce on it. Torch slots
  are filtered BEFORE spacing-thinning, so a skipped face doesn't leave a dark
  gap. Kit generic walls carry no metadata = allow everything. The registry
  also tracks **claimed** faces (one occupant per face): TorchPlacer claims
  each accepted face, and RoomPropPlacer's WallMounted pass skips claimed
  faces and claims its own — so a banner never lands behind a torch flame.
  `allowPropsInFront` gates both floor-in-front props and wall-mounted props
  (one flag; split only if a real asset needs the distinction).
- **Hallway / prison / stair walls:** `hallwayWalls` and `prisonWalls` lists
  (band always Bottom). Stair cells resolve their owner: interior stairs carved
  INSIDE a room (their cells never leave `Room.Cells` — only the CellType
  changes) use the owning room's wall set via `RoomAt`; corridor stairs use
  hallway walls.
- **Wall caps via reservation pre-pass:** capped assets (fireplace: 1/room) are
  reserved onto hash-SHUFFLED faces before emission (never scan order, which
  clumped them into a corner). Caps are guaranteed counts, dealt once per asset
  across its allowed bands, with a shared used-face set. Authoring shape: one
  unlimited base wall + capped/band-locked accents per set.
- **Openings** (`OpeningSet` per type): archway + door prefabs. Chosen by the
  room the opening leads INTO (a throne entrance gets the throne arch; a treasury
  closet door styles the treasury). Empty = kit generic.
- **Pillars** (outer/inner per type in OpeningSet). Resolved **priority-by-
  specialness** at edges touching multiple rooms: `RoomStyle.Specialness()`
  ladder (Start/Exit/Throne 5 > Treasury 4 > Merchant 3 > satellites 2 >
  categories 1 > Generic 0). Highest-scoring adjacent room's pillar wins; hallway
  contributes nothing.
- **Props** (`PropSetEntry` per type — shareable PropSet assets). See §8.

Fallback philosophy throughout: empty/unauthored → kit generic; incomplete
authoring degrades gracefully, never renders wall-less. **Keep at least one
generic arch + door in the KIT even once every type is styled** — the pillar
classifier's frame-capability checks key off the kit slots.

---

## 8. Props (RoomPropPlacer + PropSet) — phases 1–3 built

**PropSet** (ScriptableObject, shareable across types). Each entry: prefab
variants, **anchor**, tier, guaranteed-count OR chance-per-cell (+ optional
cap), zone/facing/snap fields, yaw range, sub-cell jitter.

**RNG streams:** per-room `HashStream`s — feature 11001, scatter 11002,
ceiling 11003, sockets 11004, wall-mounted 11005 (golden rule 4).

**Zones (`RoomZone`)** — every floor cell classifies once, first match wins:
`Entrance` (reserved thresholds + cells within 1 step), `Perimeter`
(wall-adjacent, non-entrance), then `Back`/`Center` split at t ≥ 0.66 along
the entrance axis. Entrance-relative, not world-cardinal: `enterDir` is "which
way you'd face walking in the main door." `RoomPropPlacer.ComputeZones` is the
single source of truth — the placer AND DungeonVisualizer's `colorCellsByZone`
debug gizmo (Entrance green, Back red, Center grey, Perimeter blue) both call
it, so the debug view can never lie. Scatter/ceiling entries filter by
`preferredZones` — a `[Flags]` RoomZoneMask (multi-select, e.g. Center+Back;
bit = `1 << (int)RoomZone`), default Perimeter ≈ the classic wall bias;
`allowCenter` = legacy "anywhere" escape hatch that skips the filter.
Guaranteed entries fall back to any free cell if their zone is empty; chance
scatter just places nothing.

**Anchors:**
- `FloorScatter` — density-driven. `facing` (FacingRule): Random /
  FaceEntrance / FaceRoomCenter / FaceAwayFromNearestWall (shelves) /
  AlignWithWall (benches; tangent direction stream-picked). **yawRange applies
  ON TOP of every facing** (Random = identity base) — wall-aligned entries
  need it narrowed (±5°), the (0,360) default will spin them. `snapToWall` +
  `wallGap`: prop origin sits wallGap meters off the nominal wall plane,
  jitter runs along the wall tangent only; the wall pick is SHARED between
  facing and snap (a corner shelf never faces one wall while snapping to the
  other), skips faces whose wall asset forbids props (§7), and a snap entry
  with no allowed wall skips the cell rather than floating at its center.
- `CeilingHung` — ceiling plane, with floor-scatter parity: `preferredZones`
  (a ceiling cell's zone = its floor column's zone), `facing` rules, and
  `snapToCeilingCorner` (cobweb in a ceiling corner — shared wall pick +
  tangent jitter at the ceiling plane, reuses `wallGap`). `ceilingLayout`:
  Scatter (random by chance) or **Grid** — a stride lattice anchored to the
  room corner (hanging lights in rows; `gridStride` cells apart, 2 = every
  other tile). The chance roll still applies in Grid, so a grid can have
  deliberate gaps; corner-snap is a Scatter-only feature. NOTE: the zone
  filter now applies (default `preferredZones` = Perimeter) — set Center on
  existing chandelier entries or they migrate to the walls.
- `WallMounted` — mounted ON a wall face (banners, shields, mirrors) at
  `mountHeight` (+ optional `mountHeightJitter`), `wallGap` off the face,
  `subCellJitter` lateral spread along the wall, forward = away from the wall
  (+ yawRange variation — narrow it). No floor occupancy / no flood-fill;
  negotiates faces via the WallFaceRegistry claim system (§7). Faces whose
  wall asset has `allowPropsInFront` off, or that a torch/earlier mount
  claimed, are skipped.
- `Feature` — THE placed prop (throne, altar, counter). Position: `WallSide`
  (Back/Left/Right/Front relative to the entrance × wall-run Center/Corner,
  free-cell fallback walks the run; sides without a wall — L-bites — skip) or
  `RoomCenter` (nearest free cell to the TRUE footprint centroid, NOT
  InteriorFloorCell — the bbox-center snap lands beside the notch in L rooms).
  Facing (FeatureFacing): Outward/Inward (wall-relative, default; RoomCenter
  falls back to entrance-relative) / FaceEntrance / FaceAwayFromEntrance /
  Fixed, plus additive `featureYaw`. `snapToWall` works here too.

**Occupancy system (guarantees props never break the dungeon):**
- Threshold cells (floor cells at any door/arch opening, incl. satellite
  closets) are RESERVED — nothing places there.
- Blocking props (collider tiers) claim cells; after each blocking placement a
  **flood-fill confirms all thresholds still mutually reachable** — if not, that
  placement is rolled back. Crank density safely.
- Décor never blocks. Entry order per room: features → guaranteed → chance
  scatter. Deterministic (hash-shuffled cells).

**Sockets (`PropSocket`)** — parent props spawn child props (table → chairs):
- Authored as empty child transforms on the prefab, positioned/oriented where
  the child belongs; **the component goes ON the socket transform — its own
  transform IS the child's pose.** (Putting the script on the prefab root
  spawns every child at the table's origin — real authoring mistake, the
  selected-socket gizmo shows sphere + facing ray to verify.)
- Fields: child prefab pool (hash-pick), childTier, fillChance (0.75 = the
  occasional missing chair), small yaw/position jitter (§ logical pose first,
  variation second — a chair may be 5° off, never 130°).
- Sockets are read from the **prefab asset** (décor parents never spawn an
  instance). Children are independent placements, never runtime-parented —
  tables batch with tables, chairs with chairs. Depth caps at
  parent→child→grandchild.
- Occupancy: children outside the room footprint or on reserved thresholds are
  skipped (a table by a door never pushes a chair into the doorway); blocking
  children claim cells behind the flood-fill. Child cell Y = parent's cell Y
  (golden rule 5's float-boundary lesson). The summary log reports socket
  fills and skip reasons.

**Chests:** author as a `Feature`, guaranteed ×1, `StaticCollider` entry in
Treasury/ChestVault sets. Inert now; interactive later = tier change only.

**Inspector UX:** PropSet entries and RoomStyle's nested lists have custom
drawers (`Assets/Editor/`) — summary foldout labels instead of "Element N",
and PropSet entries show only the fields their anchor uses. Editor-only; when
adding a PropEntry field, add it to the drawer's VisibleFields too.

**Not yet built:** wall-mounted props (the WallFaceRegistry is their
foundation; the mounting itself — negotiating faces with torches — is
pending), clusters beyond sockets, NearWallAsset anchor (woodpile beside
fireplace).

---

## 9. DepthProfile (ScriptableObject) — the progression curve

Formula-driven with authored override points (the user's explicit choice).
- **Formulas:** room count = base + depth*rate; grid size scales with room count;
  large-room counts scale with depth.
- **Authored overrides:** type unlock depths (throne ≥6, merchant ≥3, category
  minDepths), satellite rules (guaranteed/chanced lists), column rules, size-
  class edges. This table IS the content-progression curve.
- When a profile is assigned, the generator derives room count + grid size from
  `depth` at construction. Without a profile, explicit config values are used and
  some type-driven features (satellites, columns) are skipped.

---

## 10. Player / systems

- **DungeonPlayerSpawner** — prefab-first (`playerPrefab` slot) with legacy code-
  built fallback; ground-snaps via RaycastAll to nearest floor. **`spawnRoomType`
  dropdown** picks the spawn room by type (Start for play; any type to debug that
  room's props/lighting), falls back Start → any room. Uses `InteriorFloorCell`
  (an L-room's bbox center can be in a bite).
- **FirstPersonController**, FlyCamera, PlayerInteractor (SphereCast, E key,
  IInteractable), HingedDoor (world-up swing axis; facing from DungeonDoorMarker
  else geometry), PlayerFootsteps (distance-based; fires `OnStep`/`OnLand`
  events — used by viewmodel sway).
- **ViewmodelSway** — spring-based weapon/shield bob/sway, one component per hand,
  runs in LateUpdate on the captured rest pose. Rotation offset is PRE-multiplied
  (`Euler(offset)*rest`) so sway axes are camera-relative regardless of the hand's
  authored rotation. `proceduralWeight` hook for future attack suppression.
  Future: extract tuning to `SwayProfile` ScriptableObjects per weapon/shield
  when the equipment system lands.
- **ViewmodelCollision** — the third pose layer: rest → sway → **collision
  clamp**. Invoked from the END of ViewmodelSway's LateUpdate (never its own
  LateUpdate — two systems pushing the pose independently oscillate).
  Shoulder→tip spherecast along the weapon's own axis; hits pull the weapon
  back along that axis (`maxRetraction` cap, `skin` gap, asymmetric in/out
  smoothing on the retraction SCALAR — the output is a clamp, not a force).
  Rig via `shoulderAnchor`/`tipAnchor` child transforms (or raw local offsets;
  scene gizmo green=clear / red=retracting). **A SphereCast that starts inside
  a collider reports no hit** — a `CheckSphere` overlap guard forces full
  retraction at point-blank (real bug: retraction died pressed against walls).
  This cast is the future attack hit-sweep's foundation; deflection (blade
  sliding along walls) is a deliberately deferred v2.
- **Ladder climbing** — `LadderClimbZone` (trigger marker authored on the
  ladder prefab; extend the trigger ~0.5m above the top opening so cresting
  feels right). FirstPersonController POLLS an overlap sphere each frame
  (trigger callbacks miss exits on teleports/regens): inside a zone, gravity
  off, W/S climb up/down, horizontal damped to 35% so the player can adjust
  or step off. The damped forward push is what carries the player over the
  lip at the top.
- **DungeonFogController + FogSettings** (on DungeonVisualizer) — dynamic fog:
  `RenderSettings.fogColor` eases toward a room's torch color by the STRONGER
  of two terms per room: proximity (within `transitionDistance`, facing-
  agnostic — room air spills from doorways) and view (within `lookDistance`,
  gated by camera alignment — a visited room seen back down a long hall keeps
  its color identity instead of washing out). Inside a room = that room's
  color, footprint-aware. Corridors target the style's default torch color,
  so fog and firelight always agree. Big atmosphere win. Play-mode only; the
  controller holds a runtime generator reference, so regenerate in play mode
  to arm it. Fog itself must be enabled in Lighting > Environment — the
  controller only steers color.

---

## 11. Roadmap (agreed order)

Cosmetic-first; combat is far off ("get the world together first").
1. ✅ Room typing + depth parameter
2. ✅ Satellite/chest rooms (type-paired)
3. ✅ Type-driven torch lighting (+ dynamic fog)
4. ✅ Irregular room shapes + size classes
5. ✅ Interior columns
6. ✅ Per-type walls (banded, capped), arches, doors, pillars (+ prison walls,
   wall placement flags / WallFaceRegistry)
7. ✅ Props phase 1 (scatter, ceiling, feature)
8. ✅ Props phase 2/3 core: zones + facing rules + snapToWall + entrance-
   relative feature placement + sockets (parent→child→grandchild)
9. ✅ Weapon–world collision v1 (retraction); deflection deferred
10. ✅ Torch shadow perf (per-batch castShadows; shell receives only)
11. ✅ Ladders for drop-in elevated entrances (generator sites → kit segments
    → LadderClimbZone climbing)
12. ✅ Wall-mounted props (WallMounted anchor; torch-face negotiation via
    WallFaceRegistry claims) + ceiling-mounted parity (zones, facing,
    snap-to-corner). ⏳ still: NearWallAsset anchor, richer clusters
13. ⏳ Atlas multi-material kit assets (walls/ceilings/arches → 1 material)
14. ⏳ Home-base meta loop + depth progression tuning
15. Later: lock-and-key on the MST (key tree-ancestral to lock; single-entrance
    doored rooms = lockable set), difficulty gradient by graph depth, equipment
    + SwayProfiles, then combat.

---

## 12. Working style that's kept this codebase coherent

- Small changes, tested in Unity between features, small git commits (repo:
  github.com/jhollyftc/DungeonCrawler — commit per feature, push after).
- **Review every diff** — the copy-paste workflow that preceded VS Code was a de
  facto review gate; keep reviewing when edits get frictionless.
- Design conversations (new systems, tradeoffs, "what's wrong with this
  screenshot") happen in the Claude chat interface; implementation and debugging
  happen here in the editor. Bring decisions in, take implementation out.
- The user makes all Blender assets and authors the ScriptableObjects; keep asset
  conventions stable (base-origin pivots, shared wall dimensions/facing, one-cell
  column segments) so new art drops into existing slots.
