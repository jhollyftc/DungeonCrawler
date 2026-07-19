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
  impact atmosphere. The palette also drives **dynamic fog** (§10) AND the
  **torch flame VFX**: TorchPlacer resolves each torch's color ONCE and feeds it
  to both the Light and (if present) the prefab's `VisualEffect` — the flame
  graph exposes an HDR Color property its color-over-life gradient multiplies, so
  the gradient owns the SHAPE (bright core → smoke) and the palette owns the hue
  (a blue-lit shrine burns blue). Light and flame can't drift; a missing/mis-named
  property warns once and the flame keeps its authored color.
  (`TorchSettings.tintFlameToLight` / `flameColorProperty`.)
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
  unlimited base wall + capped/band-locked accents per set. A capped asset can
  carry a `featureLabel` (e.g. "Fireplace") so NearWallAsset props target it
  (§8); the reservation dict stores the `WallAsset` so the label reaches
  emission.
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

## 8. Props (RoomPropPlacer + PropSet) — mature

**PropSet** (ScriptableObject, shareable across types). Each entry: prefab
variants, **anchor**, tier, guaranteed-count OR chance-per-cell (+ optional
cap), zone/facing/snap fields, yaw range, sub-cell jitter.

**RNG streams:** per-room `HashStream`s — feature 11001, scatter 11002,
ceiling 11003, sockets 11004, wall-mounted 11005, near-prop 11006 (rule 4).

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
  `snapToCeilingWall` (single-wall snap at the ceiling plane — shared wall
  pick + tangent jitter, reuses `wallGap`).
- `snapToInsideCorner` (FloorScatter + CeilingHung, rooms AND hallways) —
  places ONLY at concave corners (a cell solid in one X dir AND one Z dir),
  tucked diagonally into the corner `wallGap` off each wall, facing out.
  Cobwebs, corner debris; hallway corners = corridor bends/junctions. Ignores
  zones AND `allowPropsInFront` (a corner prop occupies only the corner; that
  flag is about keeping a wall face clear of snapped props — a different
  intent). Takes precedence over grid / snap-to-wall. Shared detection in
  `PropSnap.TryInsideCorner` so the room and hallway placers can't diverge.
  (`snapToCeilingWall` above = the single-wall ceiling snap; `snapToInsideCorner`
  is the true two-wall corner.) `ceilingLayout`:
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
- `NearPropAsset` — placed on a free cell BESIDE an already-placed floor
  prop whose Label = `hostLabel` (a bucket beside a crate). Runs LAST (rank 4)
  so all hosts exist; `chancePerHost` gates each attachment; cell-adjacency
  (a free 8-neighbour cell) prevents overlap, reusing `usedCells`. Placed
  props record `(cell, label)` in `placedProps`, which also drives spacing.
- `NearWallAsset` — placed BESIDE a labeled feature wall (firewood next to a
  fireplace). A `WallAsset.featureLabel` (e.g. "Fireplace") tags it; the kit
  placer records only labeled capped faces into
  `WallFaceRegistry.RecordFeature(cell,dir,label)`. The anchor matches its
  `hostLabel` to that label, and snaps to a wall-adjacent cell BESIDE the
  feature (a tangent neighbour, never the feature cell itself — that would
  cover it), floor-level only. Runs early (rank 1, after features, before
  guaranteed/scatter): its 1-2 valid cells must be claimed before flexible
  props take them, and it depends only on the kit-placed wall, not on other
  props. (NearPropAsset stays rank 4 — it depends on host props.)
- `label` is a prop's KIND: `minSpacing` (cells) keeps same-Label floor props
  apart (two "Statue" entries won't clump — checked in scatter + feature
  picks), and NearPropAsset targets a Label. Floor plane only.
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
- Décor never blocks. Entry order per room (most-constrained first, so tight
  placements claim cells before flexible ones): Feature 0 → NearWallAsset 1 →
  guaranteed 2 → chance scatter 3 → NearPropAsset 4. Deterministic
  (hash-shuffled cells).
- Ceiling props have their OWN occupancy plane (`usedCeilingCells`) and never
  touch the floor blocked/flood-fill set — a floor rack and a ceiling light
  share a cell. Interior-stair cells are excluded as placement targets
  (`Placeable` = grid[c] == Room) but stay walkable for the flood-fill.
- `sharesTile` (FloorScatter/CeilingHung): the entry doesn't reserve its tile
  AND may sit on an already-used one — a corner cobweb that co-exists with a
  hanging lantern on the same tile. Bypasses only the one-prop-per-tile visual
  rule; physical blocking (collider tiers, flood-fill) still applies.

**Hallways (HallwayPropPlacer + RoomStyle.hallwayProps):** one GLOBAL corridor
PropSet — debris, cobwebs, roots. Corridors aren't rooms (no zones/centroid/
entrance), so it's a separate pass scanning CellType.Hallway cells. Supports
FloorScatter (snapToWall + facing; zone/feature fields ignored), CeilingHung
(scatter, or Grid stride ALONG the corridor), and WallMounted (torch-
negotiated). Door hallway cells are reserved. Blocking props run a
connectivity BFS over the hallway+stair network keeping every door mutually
reachable — so a collider pile only lands in wide spots/junctions, never
sealing a 1-wide corridor. Global streams 12002/12003/12005 (distinct from
rooms' per-room 110xx). Wired after RoomPropPlacer so torch face-claims exist.

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

**Carryable props** (see §10 for the carry rig): a prop the player can pick up
and throw MUST be authored `PropTier.FullGameObject`, not an instanced tier. The
instanced tiers bake the MESH into a static matrix and give the prop only a
collider GameObject, so lifting a `StaticCollider` barrel would carry the collider
away while the visible mesh stayed welded to the floor. Carryables are low-count
by nature, so the batching loss is irrelevant. Barrel/crate/skull-style props get
`Rigidbody` + `Carryable` (+ optional `PushableProp`, `ImpactAudio`).

**Inspector UX:** PropSet entries and RoomStyle's nested lists have custom
drawers (`Assets/Editor/`) — summary foldout labels instead of "Element N",
and PropSet entries show only the fields their anchor uses. Editor-only; when
adding a PropEntry field, add it to the drawer's VisibleFields too.

**Built out:** every anchor above (floor scatter, ceiling scatter/grid/
wall-snap/inside-corner, wall-mounted, feature, near-prop, near-wall),
sockets (authored composites), hallway props, label spacing, tile sharing,
and the wall-real-estate negotiation (§7) under all of it. **Not yet built:**
procedural clump-scatter (variable-count piles that clump rather than spread
— near-prop + spacing gets close but isn't a true clumper); a NearWallAsset
that reads a wall feature on a NON-floor band.

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
- **FirstPersonController** — walk/sprint/jump + **hold-to-crouch** (shrinks the
  capsule from the TOP so feet stay planted, drops the camera with it, and blocks
  standing up under a ceiling). `IsCrouching` and `HorizontalSpeed`/`IsGrounded`
  are public — crouch is the seed of future NPC alerting (quiet = unseen). Also
  hosts the **dev overlay** (`OnGUI`, `showControls`) showing live **seed +
  depth** (read from the visualizer each frame, never cached — seed re-randomizes
  on F1) and the **debug keys**: F1 = new dungeon at the same depth, **PgUp/PgDn =
  depth ±1 with the seed PINNED** (watch one seed grow/shrink with depth). Depth
  keys survive the scene reload via `DungeonVisualizer.PendingSeed`/`PendingDepth`
  statics, consumed in `Generate()` before the generator is built (depth drives
  room count + grid size); runtime-only, serialized inspector values untouched.
  NB: `OnGUI` must be a CLASS method — nested inside `Update()` as a local
  function it compiles clean and silently never runs (real bug).
- **FlyCamera**, **PlayerInteractor** (SphereCast, E key, `IInteractable`; stands
  down while PlayerCarry holds something so E is unambiguous).
- **HingedDoor** — the ORIGINAL scripted door (E to open, world-up swing axis;
  facing from DungeonDoorMarker else geometry). Being superseded by **PhysicsDoor**
  (below); swapping is a kit prefab swap.
- **PlayerFootsteps** — distance-based (a step every `stepDistance` of grounded
  travel, so cadence scales with speed for free). Fires `OnStep`/`OnLand` and
  exposes `StrideProgress`/`StepCount` (head bob locks to these). **Coyote-time
  grounding** (`groundedGrace`): `CharacterController.isGrounded` strobes false
  descending stairs (the capsule pops off each step lip), which reset the stride
  accumulator every frame → no footsteps going DOWN stairs (worked going up). The
  grace keeps the stride alive across the gaps; a real jump/fall still reads as
  airborne.
- **HeadBob** (on the camera) — subtle vertical dip + sway + roll, **LOCKED to
  the footstep system**: it reads `PlayerFootsteps.StrideProgress`/`StepCount`
  (the same accumulator that fires the step SOUND) instead of running its own
  clock, so the head dips exactly when the foot lands and they can't drift across
  stops/jumps/stair-descents (all of which reset that accumulator). Vertical dips
  once per footfall; sway/roll run at HALF that (alternating feet) — that half-rate
  is what reads as a walk. Cadence therefore comes entirely from `stepDistance`.
  Heavy carry deepens the lurch (amplitude, via `CarryLoad01`), not the cadence
  (that would desync). Composes ADDITIVELY with crouch (strips last frame's offset
  before reapplying, since crouch only writes the camera Y during transitions);
  roll needs no undo (the controller rewrites localRotation to pure pitch every
  frame). The camera parents the viewmodel + overlay, so both bob for free.
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
- **ViewmodelCamera** (on the player camera) — renders the weapon/shield through
  a separate URP **Overlay camera that CLEARS DEPTH**, so the viewmodel is drawn
  after the world onto cleared depth and physically CANNOT clip through geometry,
  at any rotation, with no per-weapon tuning (the standard FPS fix). At Awake it
  moves the `viewmodelRoots` hierarchies onto a **Viewmodel layer** (you must
  create the layer), strips that layer from the base camera, and builds the
  overlay in the base camera's stack — so the player prefab stays self-contained.
  ViewmodelCollision still runs but its job CHANGES to a pure FEEL mechanic
  (weapon pulls back when you press into a wall), no longer a correctness
  guarantee. `SetViewmodelVisible(false)` stows both while carrying (§ carry).
  **Exclude the Viewmodel layer from world queries** (ViewmodelCollision's mask
  etc.) or the weapon casts against itself. GOTCHA that cost us: the overlay fails
  SILENTLY if disabled — URP still lists it in the stack while the weapon is just
  gone; DungeonPlayerSpawner's `HandleOtherCameras` was disabling EVERY non-player
  camera including this overlay (built during the same Instantiate), so it now
  skips cameras the player rig owns.
- **Physics interaction layer (`IPushable`)** — the split that makes it compose:
  the PLAYER decides how HARD it pushes (a speed-scaled impulse — sprint shoves,
  crouch barely nudges), the OBJECT decides what that force MEANS.
  `CharacterControllerPhysicsPush` (on the player, `OnControllerColliderHit`)
  supplies the force; a `PhysicsDoor` turns it into hinge torque, a `PushableProp`
  applies its own multiplier/speed-cap, a plain Rigidbody gets a mass-aware default
  shove. So tuning a barrel can never un-tune the doors. **FRAMERATE LESSON (real
  field bug):** `OnControllerColliderHit` fires once per FRAME and an Impulse
  ignores time, so raw delivery is (force × framerate) per second — fast PCs opened
  doors, slow PCs couldn't. Fixed by scaling the push by
  `Time.deltaTime × referenceFrameRate`, so per-second delivery is identical on
  every machine. (Carrying a prop into a door always worked because that contact
  resolves in the fixed-rate physics step.)
- **PhysicsDoor + PhysicsDoorAudio** — a door you push open by walking into it.
  Contact → **pure torque about the hinge axis** (never `AddForceAtPosition`,
  which injects linear velocity the joint fights and tears the door off its
  hinge); `ForceMode.Impulse` so mass/leverage feel real; **angular** speed
  clamped (`maxSwingSpeed`) — a hinged door's LINEAR velocity is ~0, so a linear
  clamp never fires and impulses compound. Angle comes from the transform
  (`CurrentAngle`), NOT `HingeJoint.angle` (returns 0 and NaN, which poisoned the
  logic). NO `RigidbodyConstraints` (world-space, vs the LOCAL hinge axis — freezing
  world X/Z welded the FBX doors shut). **One-way-per-swing:** opens either way,
  but once past `commitAngle` the opposite limit snaps to 0 so it can't pass through
  closed — hits a hard stop and thunks like a real frame; full range restored once
  settled. `thunkArmAngle` gates the closing thunk (a shoving match jittering around
  0 stays silent). Audio is state/event-driven: a LOOPING creak whose volume/pitch
  track live swing speed + one-shot thunk/slam (`OnClosed`/`OnSlamOpen`, carrying
  impact speed → volume). Impacts and creak use SEPARATE reference speeds (the door
  hits the closed stop far slower than it peaks). Kinematic toggle = a locked door
  (future).
- **Carrying / throwing (`PlayerCarry` + `Carryable`)** — pick up (via
  `IInteractable`/E), carry, drop (E), throw (LMB). The carry is **VELOCITY-DRIVEN,
  not a kinematic parent**: the prop stays a fully dynamic Rigidbody pulled toward
  a hold point each FixedUpdate, so it never stops colliding — bonks off frames,
  knocks props over, swings a physics door open on contact, and CANNOT be walked
  through a wall (a kinematic carry would let you stroll it through geometry — wrong
  instinct for this game). Mass expresses itself through ONE clamp (`maxCarryForce`):
  a heavy prop lags the hold point and swings wide. Carryables must be
  `PropTier.FullGameObject` (§8). Two safeguards that matter: `Physics.IgnoreCollision`
  between the capsule and the held prop (else the push force and carry force fight
  every frame), and a **break distance** that drops a prop wedged behind geometry
  rather than dragging it forever. Two-handed: the viewmodel stows while carrying
  (hands full). Throw speed is authored per-prop (`Carryable.throwSpeed`), NOT derived
  from mass (mass already governs flight/impact); the throw grunt is pitched by mass.
- **Encumbrance** — one mass signal, `PlayerCarry.CarryLoad01` (0 below
  `freeCarryMass`, 1 at `heavyCarryMass`), drives EVERYTHING that means "heavy":
  carry lag, move-speed penalty (`CarrySpeedMultiplier`), turn-rate penalty
  (`CarryTurnMultiplier`), and head-bob depth. Set a prop's `Rigidbody.mass` and
  its whole heaviness moves together — deliberately one dial, never several that
  can drift apart.
- **ImpactAudio** — speed-driven collision sound for ANY Rigidbody (thrown barrel,
  shoved crate). Force is audible for free (impact speed → volume + pitch). The
  trap: `OnCollisionEnter` is NOT one-per-throw (a landing barrel bounces and
  re-contacts a dozen times), so a speed floor + retrigger interval stop it
  machine-gunning until the prop settles. Fires **`OnImpact(position, loudness)`**
  — the hook for NPC alerting; a thrown prop makes noise SOMEWHERE ELSE, turning
  carrying into a distraction mechanic. Nothing listens yet. Together with
  `IsCrouching` and the door's quiet-swing threshold, the SENSING side of NPC
  alerting is largely built ahead of any consumer.
- **Ladder climbing** — `LadderClimbZone` (trigger marker authored on the
  ladder prefab; extend the trigger ~0.5m above the top opening so cresting
  feels right). FirstPersonController POLLS an overlap sphere each frame
  (trigger callbacks miss exits on teleports/regens): inside a zone, gravity
  off, W/S climb up/down, horizontal damped to 35% so the player can adjust
  or step off. The damped forward push is what carries the player over the
  lip at the top.
- **NPC navigation (`DungeonNavBaker`)** — the dungeon is generated at RUNTIME and
  regenerated on F1/PgUp, so there is no static scene to bake in the editor:
  requires the **AI Navigation package** (`com.unity.ai.navigation`) and rebuilds a
  `NavMeshSurface` at the end of `BuildMesh()`. Collects **physics colliders** from
  the visualizer's children = exactly the project's collision truth (§5), so player
  and NPCs walk the same surface by construction. `excludeRoots` keeps DYNAMIC
  colliders out of the bake: **doors** (baked solid they'd wall off their doorway
  forever, even swung open) and **`DungeonNpcs`** — in play mode `ClearGenerated`
  uses deferred `Destroy()`, so during a regen the PREVIOUS generation's NPCs are
  still alive when `BuildNavMesh()` runs, and their capsules would bake holes into
  the fresh navmesh wherever they stood. Spawn placement is deterministic
  (`vis.seed ^ 0x5EED`).
- **NPC body (`NpcLocomotion`)** — a `NavMeshAgent` that PLANS driving a
  `CharacterController` that MOVES (`agent.updatePosition = false`). **Why the
  hybrid:** a bare agent moves the transform directly and never fires
  `OnControllerColliderHit` — the callback `CharacterControllerPhysicsPush` uses to
  dispatch `IPushable.Push` — so NPCs ghosted through physics doors. Driving a
  CharacterController runs that component on NPCs **verbatim**: same pushForce,
  speed scaling, and framerate normalization as the player, one code path that
  can't drift. **The crux is `agent.nextPosition = transform.position`** — the agent
  follows the BODY, so when the capsule is stopped by a door the agent stops with
  it and keeps steering forward: the NPC *leans* on the door. Speed-scaled push
  then comes free (slow = eases it open under `thunkArmAngle`, silent; charging =
  slams). **Authoring: agent radius ≥ controller radius**, so the agent plans around
  baked geometry with margin and the capsule only touches what the navmesh
  deliberately excludes. Two `Awake`/runtime guards exist because both failures are
  silent and baffling: `center.y` must equal `height/2` for a base-origin model (a
  centered capsule spawns half-buried, never reads grounded, and **falls through the
  world while still pathing**), and `CheckFall()` watches real vertical drop —
  checking `agent.isOnNavMesh` alone does NOT catch it, because `nextPosition` is
  force-synced to the falling body so the agent believes it's on the mesh all the
  way down.
- **NPC brain (`NpcBrain`)** — decisions only. Wander → Investigate → Alerted FSM
  as a **priority interrupt** (sight beats sound beats wandering, re-evaluated each
  frame). Every state delegates to capability components and never touches the agent
  or controller directly. That shape is deliberate: a Unity Behavior tree swapped in
  later calls the identical capability API, so this FSM doubles as the integration
  test proving that API is complete. **Determinism boundary (deliberate, do not
  "fix"):** generation is deterministic, runtime AI is NOT — where an NPC spawns
  reproduces from (seed, depth), but what it decides once alive uses `UnityEngine.Random`,
  because reproducing a fight would need deterministic physics and input replay.
- **NPC perception (`NoiseBus` + `NpcPerception`)** — the SENSING half, finally
  consumed. A static `NoiseBus` carries `NoiseEvent`s (position, 0..1 loudness,
  `Faction`); emitters and listeners are mutually ignorant, which matters because
  props/NPCs respawn every regen (direct wiring would need rediscovery). It **resets
  its static event on play-mode entry** (`RuntimeInitializeOnLoadMethod`) — the
  fast-enter-playmode stale-delegate trap. Three THIN adapters bridge the pre-built
  hooks without those systems learning about AI: `ImpactNoiseEmitter` (a thrown
  barrel makes noise where it LANDS), `DoorNoiseEmitter` (the door's `thunkArmAngle`
  gate keeps an eased-open door quiet), `PlayerNoiseEmitter` (footsteps scaled by
  speed, multiplied down when crouched — crouch-sneak genuinely shrinks the hearing
  radius). `NpcPerception`: hearing (audible if `distance < maxHearRadius × loudness`,
  one formula), sight (view cone + LOS ray aimed at the player CAMERA so crouching
  behind cover breaks line of sight), and **`Awareness01` as a METER not a boolean**
  (suspicious→investigate→hunt from one number). Sight ticks on a random per-NPC
  stagger, never per frame. (`GetInstanceID` is deprecated in Unity 6.5 — use
  `Random`/`GetEntityId`.)
- **NPC combat (`IDamageable`/`DamageInfo`/`Health`, `MeleeAttack`, `ThrownDamage`,
  `FactionMember`)** — attackers only ever talk to `IDamageable`; `Health` sits on
  BOTH player and NPCs, so a goblin's swing and a thrown barrel hurt either with no
  special-casing (same philosophy as `IPushable`: attacker supplies force, victim
  decides meaning). `MeleeAttack` = windup → sweep → recovery; the sweep carries
  ViewmodelCollision's `CheckSphere`-before-`SphereCast` lesson (a cast STARTING
  inside a collider reports nothing, and melee range means you're usually already
  touching) plus `SphereCastAll`/dedupe-by-root/facing-check. `FactionMember` gates
  friendly fire — **if it's MISSING on either side, every swing silently whiffs**
  (Neutral == Neutral reads as same-faction). `ThrownDamage` only hurts while ARMED
  (`PlayerCarry.Throw()` arms it, one hit per flight — a bouncing barrel can't
  double-hit, casual shoving never hurts). Damage shares the `ImpactForce` curve with
  audio so they can't drift; knockback is MOMENTUM-derived (mass × speed), no per-prop
  tuning.
- **NPC reactions (`NpcHitReactions`, `NpcBoneReaction`, `NpcCombatAudio`,
  `NpcAnimatorDriver`, `NpcHeadTrack`)** — how an NPC suffers, all as LateUpdate bone
  layers stacked on the Animator's pose so they blend with whatever's playing:
  - `NpcHitReactions`: knockback via the locomotion capability (thrown hits redirect
    part upward onto a real ballistic arc), stagger scaling with the shove, `ForceAlert`
    at the attacker, and death — animation if the controller has a `Die` trigger, code
    topple as fallback, then an **eased sink through the floor** (the despawn a player
    never catches happening).
  - `NpcBoneReaction`: per-bone hit flinch — angular impulses into bones near impact
    (distance falloff), spring-return to pose. Chosen over TRUE ragdoll-blending
    deliberately (Unity's ragdoll wizard is humanoid-only; this is a generic tripo
    rig). Rig-agnostic (bones from the skinned mesh), zero cost while settled, same
    momentum input as knockback so skeleton and body agree how hard the hit was.
  - `NpcAnimatorDriver`: one-way bridge from `NpcLocomotion.CurrentSpeed` to Animator
    `Speed`/`MotionSpeed`, and `TriggerDeath()`. AI never knows the Animator exists —
    a rigged model is a drop-in. **NEVER root motion** (the CharacterController drives).
  - `NpcHeadTrack`: head bone watches the player up close, WORLD-space delta rotation
    (no Blender bone-axis assumptions), gated by awareness so it's an honest detection
    tell. `NpcCombatAudio`: hurt grunts scaled by impulse, death cry + delayed body-fall
    thud (house audio pattern, § PhysicsDoorAudio).
- **NPC crowd spacing** — three separate mechanisms, learned the hard way:
  (1) an **NPC layer with NPC×NPC collision OFF** in the matrix — capsule
  step-climbing (`stepOffset`, which stairs need) treated a neighbour's shoulder as a
  step, so goblins summited each other. (2) **Feed `Controller.velocity` back to the
  agent** — RVO predicts neighbours from their VELOCITY, and an externally-driven
  agent reports ~zero, so every NPC told every other "I'm stationary" and they walked
  through each other; randomized `avoidancePriority` breaks equal-priority deadlocks.
  (3) **Boids separation steering** in `NpcLocomotion` — RVO only separates agents IN
  TRANSIT, and a crowd CONVERGED on one target (all chasing the player) has everyone
  stopped, so nothing spreads them; separation is additive with pathing → attackers
  settle into a ring, not a stack. Its living-NPC registry is the `NpcRegistry` a
  shout system wants — it exists for free, and death disabling `NpcLocomotion` drops
  corpses from the crowd automatically.
- **NPC foot IK (`NpcFootIK`, Animation Rigging package)** — per-leg `TwoBoneIK`
  grounding for the generic rig: while the animation has a foot in stance, its IK
  target snaps to the raycast ground height (feet land ON stair treads); mid-swing the
  weight fades and the clip owns the foot. Converges instead of feeding back because
  the target comes from the ground raycast, an EXTERNAL stable reference — not the
  previous pose. `groundMask` must exclude the NPC layer (don't plant a foot on a
  neighbour). v1 has no pelvis drop, so a steep descent is limited by leg length.
- **NavMesh from stairs — BUILD-ONLY TRAP (real, cost hours):** runtime navmesh baking
  reads triangles off MeshColliders, which **in a player build requires the mesh's
  Read/Write Enabled import setting**. Non-readable meshes are skipped from the bake
  SILENTLY — the stairs (the only MeshCollider kit pieces) vanished from the build's
  navmesh so NPCs never crossed floors, while the editor (all meshes readable) worked
  perfectly and the collider still carried the player's feet. `DungeonNavBaker` now
  warns (pre-bake) for any non-readable collider mesh — `Mesh.isReadable` reports the
  import setting even in-editor. It also overrides **voxel size** (~0.07): the default
  (agentRadius/3) is too coarse for stepped mesh colliders, baking stairs as narrow
  ragged strips with a lip where the stair prefab overlaps the greybox landing.
- **Animator-controller clobber — process rule:** Unity re-serialized
  `Goblin_Animator.controller` during an asset move and **DROPPED** the added
  parameter/state/transition while keeping the orphaned objects in the file (graph
  showed only the base state). Never edit controller YAML while the Animator window
  has it open; controllers are otherwise hand-authorable (states/blend trees/transitions
  were written directly as YAML when editor drag-and-drop repeatedly failed to land a
  clip — reference the clip by `internalID` from the FBX `.meta` + the FBX guid).
- **NPC model conventions** — base-origin, real-world scale, **scale 1 on the
  prefab root**. A tripo/Blender FBX that imports tiny and gets a 160× root scale
  breaks everything downstream: `CharacterController` radius/height scale with the
  transform (a 0.35 radius becomes 56m), and NavMeshAgent `baseOffset` scales too
  (0.008 × 160 ≈ 1.3m of hover). Fix the importer's Scale Factor, not the component
  values. Empirically, a Blender FBX **containing an armature** exports at correct
  units where the same static mesh did not.
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
12. ✅ Props phase 4: wall-mounted, ceiling parity (zones/facing/grid/
    inside-corner), hallway props, near-prop + near-wall (labeled),
    label spacing, tile sharing. Remaining prop idea: procedural
    clump-scatter (see §8 "Not yet built").
13. ✅ Viewmodel overlay camera (depth-clear; kills weapon/shield clipping).
14. ✅ Physics interaction layer: push-open physics doors (+ audio), the
    `IPushable` push system (framerate-independent), crouch/sneak, and
    carrying/throwing with mass-driven encumbrance + ImpactAudio.
15. ✅ Head bob (footstep-locked; deepens with carry load).
16. ✅ Torch flame VFX tinted to the per-room torch palette.
17. ⏳ Atlas multi-material kit assets (walls/ceilings/arches → 1 material)
    — mostly Blender/texture work; toon shader packed-mask already ready.
18. ⏳ Home-base meta loop + depth progression tuning (portal-out at Exit →
    home base → depth increment → sell/replenish). Design chat first.
19. ✅ **NPC AI phase 1** — runtime NavMesh (`DungeonNavBaker`) + locomotion body
    (`NpcLocomotion`) that pushes doors via the player's own push component + a
    wander brain (`NpcBrain`).
20. ✅ **NPC AI phase 2** — perception: `NoiseBus` + thin emitter adapters +
    `NpcPerception` (hearing/sight/`Awareness01`); brain grown to Investigate/Alerted.
21. ✅ **NPC AI phase 4 (combat) + reactions + crowd + foot IK** — `Health`/
    `IDamageable`, `MeleeAttack`, `ThrownDamage`, `FactionMember`; hit reactions
    (knockback/stagger/death-sink), per-bone flinch, head track, combat audio,
    animator driver (walk/idle blend + death); crowd spacing (NPC-layer matrix +
    RVO velocity feedback + boids separation); `NpcFootIK` (Animation Rigging).
    The goblin now senses, hunts, fights, suffers, and dies. (§10 has the detail
    and the field lessons: the build-only stairs Read/Write trap, RVO velocity
    feedback, the controller-clobber rule, FactionMember silent-whiff.)
22. ⏳ **Player melee (`melee-v1-plan.md`)** — swinging sword + shield bash that
    HIT the combat core built above (goblins already take damage/knockback/stagger
    from `DamageInfo`), with player-facing GAME FEEL as the point: hitstop, the
    weapon "catching" on a body mid-swing, screen shake, hit sparks/audio,
    directional camera kick. The `MeleeAttack` sweep + `ViewmodelCollision`
    anchors + `ViewmodelSway.proceduralWeight` are the pieces it composes.
23. ⏳ NPC AI remaining phases: **call for help** (shout = a loud `NoiseEvent`; the
    `NpcRegistry` and death cry already exist — rate-limit or it alert-loops);
    **equipment/disarm/rearm** (`WeaponDefinition` SO + `PropSocket`-style hand
    socket; a dropped weapon is NOT a `Carryable`, whose `Interact()` hard-codes
    `PlayerCarry`); **NPC carry/throw** (extract `PlayerCarry`'s FixedUpdate drive
    into a shared `CarryDriver` — beware moving serialized fields, it silently
    resets the player prefab's tuning); **spawning** (depth-scaled `EnemyBudget` in
    DepthProfile, per-room-type `EnemySet` in RoomStyle, own placer on free hash
    stream 11007); optional **Unity Behavior** tree swap (install via Package
    Manager UI — never hand-pin a version).
24. ⏳ Atlas multi-material kit assets (walls/ceilings/arches → 1 material) —
    mostly Blender/texture work; toon shader packed-mask already ready.
25. ⏳ Home-base meta loop + depth progression tuning (portal-out at Exit → home
    base → depth increment → sell/replenish). Design chat first.
26. Later: lock-and-key on the MST (key tree-ancestral to lock; single-entrance
    doored rooms = lockable set), difficulty gradient by graph depth, equipment
    + SwayProfiles.

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
