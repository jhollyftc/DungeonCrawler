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

**Where things live.** Everything used to sit loose at the Assets root (107
scripts, 3 scenes, stray art); it now goes:

```
Assets/Scripts/   Generation  Rendering  Placement  Player  NPC
                  Combat  Interaction  Audio  UI  Debug  Editor
Assets/Art/       Models  Kit (the modular dungeon meshes)  Prefabs
                  Characters/{Goblin,Skeleton,Ogre}  VFX  Skyboxes  UI
Assets/Scenes/    the scenes, each with its Unity-managed data folder beside it
Assets/_Settings/ RoomStyle, DepthProfile, PropSets, shader-variant collections
Assets/Shaders/   ToonLit, ToonWater, GroundFog, InteriorMapped, TextureEmission
```

**NOT ours, don't reorganize:** `SourceFiles/` (Unity Starter Assets sample),
`Samples/`, `TextMesh Pro/`, `Tutorials/`, `Settings/Build Profiles` (Unity 6
expects that exact path). Editor scripts must stay in a folder NAMED `Editor` —
the name is what makes them editor-only, at any depth.

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
   THREE times (PlayerInteractor, TorchFlicker, and `EmissiveController.cs`
   holding `class EmissionController` — already referenced by four candle
   prefabs). Fix by renaming the **FILE**, not the class: the `.meta` travels
   with the rename so the guid is preserved and existing prefab references stay
   intact. Split multi-class files accordingly.

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
10b. **PlacePits** — cuts chasms across room floors, carves the space beneath, spans them
    with a bridge and mounts a climb-out ladder (see §4). Runs BEFORE `PlanInteriorColumns`,
    or a column is planned on a lattice point over the hole.
11. **WidenJunctions** — opens corridor JUNCTIONS AND BENDS into 2x2 plazas, optionally
    with a column at the centre (reusing `ColumnPoints`, so the kit's existing interior
    column slot renders them). Corridors are 1-wide **structurally**, not by policy — a
    cell exists iff it sits on an A* path and `Commit` writes exactly that cell — so
    there is no width dial and widening must be a post-pass. **Junctions and bends only,
    deliberately:** widening straight runs makes corridors read as rooms and eats the rock
    prisons and alcoves need, whereas widening where routes MEET rewards turning a corner.
    Three guards, each from a real failure shape: the corridor list is SNAPSHOT first (see
    §12's self-hosting rule); carvability defers to `HallwayPathfinder.SurroundingsOk`
    rather than restating it, so a widened cell obeys the sealed 13-cell stair envelope
    like every other corridor cell; and a new cell may never open into a **Room** (would
    punch a doorway with no door, arch or reserved threshold, bypassing `RecordDoor`) or a
    **Prison** (would give a validated one-opening cell a second opening).
12. **PlaceAlcoves** — small validated recesses off corridors with an authored KIND
    (statue nook / shrine niche / collapsed dig / storage recess). See §4.
13. **PlaceCrawlways** — 1.5m crawl passages bored through solid rock between two places
    that are already connected but a long walk apart. See §4 and `CRAWLWAY_PLAN.md`.

> The stage-number comments in code (`Stage 5`, `Stage 6`…) are historical and
> out of order after several insertions. Trust the `Generate()` call order, not
> the comment labels.

---

## 4. Room typing, satellites, columns

**RoomType enum** (in DepthProfile.cs): Generic, Start, Exit, ThroneRoom,
Merchant, Barracks, Kitchen, Library, Shrine, and satellite types ChestVault,
Treasury, Armory, Pantry, Study, Reliquary.

**Typing (AssignRoomTypes)** — decisions locked with the user:
- **Start/Exit** = a CLIMB. Start sits on the **lowest occupied floor**, Exit on the
  **highest** — the player is dropped in at the bottom and works their way out. Exit
  is a distinct portal-out room, NOT the boss room.
  **Chosen as a PAIR, never independently by height** (`ChooseStartAndExit`): the old
  MST-diameter choice silently guaranteed a LONG critical path, and Merchant placement
  depends on it (it scores rooms by distance to the MIDDLE of the start→exit path, so
  a two-room path has no middle and the merchant lands beside Start or Exit). Nothing
  stops the deepest and topmost rooms being MST-adjacent — room Y is random and the
  graph is Delaunay over room centres. So among the extreme floors the pair maximizing
  hop distance wins: the vertical narrative AND a path worth walking, with Merchant and
  Throne needing no changes. Falls back to the graph diameter when every room shares a
  floor. **Room floor = `Bounds.yMin`** (a tall room spans several Y but you walk one).
  **`gridHeight` 12** is the tested value for this: it gives a real climb while keeping
  the maximum stair chain inside what the pathfinder handles. 20 made the required
  route stair-dominated and stressed the A* hard — the critical path is now the full
  vertical span on EVERY seed, which is the worst case for stair-aware pathing
  (~one staircase per level, each needing its 13-cell sealed envelope).
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
- **Must not steal a LADDER's wall face** (real bug): `AllocateLadders` is stage 6,
  satellites stage 9. The ladder pass *does* verify its mount wall is solid, but at
  stage 6 that cell is still Empty so it passes — then a closet is carved into that
  exact face three stages later, putting a ladder across the closet's only door.
  `SatelliteFits` now rejects it, tested across the ladder's whole climb column (not
  just its foot). **The SATELLITE yields, not the ladder** — a ladder is what keeps
  an elevated entrance two-way, and dropping it demotes the room to a one-way drop;
  satellites are chanced decoration, so on affected seeds a closet relocates or is
  simply absent. Prisons need no equivalent guard (verified): their one-opening rule
  already rejects any footprint touching a Room, and their solid-above rule rejects
  the column beneath a hallway — exactly where a ladder's mount wall lives.

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

**Alcoves (`PlaceAlcoves`, `Alcove.cs`)** — recesses carved off corridors, each with an
`AlcoveKind` that decides its contents. The feature that made corridors worth walking:
combined with wide prison cells and junction plazas, "no longer just straight hallways."
- **GRID-INVISIBLE, and that is the whole design.** Alcove cells are written as ordinary
  `CellType.Hallway`, so walls, floors, ceilings and corridor wall-sets come free from the
  existing kit and mesher paths with **zero changes to either**. A dedicated `CellType`
  would be read as solid-adjacent by every `!= CellType.Empty` test across the mesher and
  kit and would render as nothing. Identity lives in `DungeonGenerator.Alcoves` +
  `AlcoveAt`/`IsAlcoveCell`, the same shape as `Prisons`/`Ladders`/`ColumnPoints`.
  **The cost of that choice is the self-hosting trap — see §12.**
- **`RecessFits` is shared with prisons.** Extracted from `TryPlacePrisonAt`, it carries the
  rules that took several passes to settle: all cells Empty, solid above and below, the
  one-opening rule, the 1x1 vestibule for wide shapes, stair clearance. It commits nothing
  and draws nothing from `rng`, so a caller can probe several shapes without perturbing the
  stream. Both callers shrink to fit rather than failing outright.
- **`AlcoveSpec.Direction` is load-bearing**: it gives the recess an intrinsic
  back/left/right frame, which is what lets the existing `Feature` anchor work with no new
  authoring concepts (`WallSide.Back` = the far wall, `FeatureFacing.Outward` = looking out
  at the corridor). Without it an alcove has no zones, centroid or entrance axis and
  `RoomPropPlacer`'s machinery is unusable — it hard-gates on `CellType.Room` anyway.
- **Blocking props are legal at ANY depth, including a 1x1.** A guard once refused them in
  shallow alcoves reasoning "there is nowhere to stand" — wrong, and it contradicted the
  no-flood-fill rule: an alcove is a DEAD END, nothing routes through it, so a collider
  inside one severs nothing however shallow. The symptom was a recessed statue you could
  walk through, which is the exact case the shallow kind exists for. `IsEnterable` survives
  as an informational property only.
- Pipeline position is load-bearing twice: **after** prisons (Hallway is a legal prison
  host, Prison is not a legal alcove neighbour, so alcoves would silently eat prison sites
  if reordered) and **last** among rng-drawing stages (so appending shifted no existing
  seed's rooms, prisons, satellites or columns).

**Crawlways (`PlaceCrawlways`, `Crawlway.cs`, `CRAWLWAY_PLAN.md`)** — a grate in a wall opens
onto a 1.5m tunnel bored through rock. The player crouches through (`crouchHeight` 1.1 already
handles it, so no new movement mode); at 1.5m nothing bakes walkable, so **NPCs are excluded by
GEOMETRY rather than by a rule anyone maintains** — an escape route they cannot follow, for free.
- **CELLS STAY `CellType.Empty`, and that is the whole design — the exact OPPOSITE of alcoves.**
  An alcove is typed `Hallway` precisely so it inherits walls, floors and ceilings free from the
  kit; a crawlway must not, because every kit piece is authored to a 3m face and an open CellType
  would emit full-size masonry into a hole meant to be a 1.5m bore. So to the mesher, the kit
  placer, `NeedsSlabBetween`, the automap and every `!= CellType.Empty` test, a crawlway DOES NOT
  EXIST and the rock is solid. Identity lives in `Crawlways` + `CrawlwayAt`/`IsCrawlwayCell`.
  The price is that it brings its own mesh and collider, as bridges and ladders do.
  **THE BORE IS INERT; THE CHAMBER IS NOT, and the distinction matters twice.** With chambers
  off, the stage mutates nothing and a fixed seed generates an identical dungeon with crawlways
  on or off — still the regression test for the bore. A chamber DOES carve cells, which
  reintroduces §12's **self-hosting trap** the bore was immune to: a chamber is typed Hallway
  during the same grid scan, so later flat indices would bore a crawlway out of a sewer chamber
  unless `IsChamberCell` guards the host. Third instance of that shape after alcoves and plazas.
- **THE ENDPOINTS ARE THE FEATURE; THE BORE BETWEEN THEM IS TRIVIAL.** The cheap version — bore
  blind and stop wherever you break through — needs no search and produces worthless crawlways,
  because a 4-cell tunnel surfacing in the same corridor twelve metres away is a novelty rather
  than a shortcut, and **you cannot tell a good one from a bad one without knowing BOTH ends**.
  So the far end is chosen: flood the rock for candidates, then ONE BFS over the open network
  answers the only two questions that matter. **Is there already a path** — if not these are two
  disconnected regions and joining them would make the crawlway load-bearing for connectivity,
  papering over a generator bug; reject. **Is that path long** — `crawlwayMinDetourRatio` is what
  makes a crawlway MEAN something. Measured, that second rule rejects ~70% of attempts, which is
  the design working and the tally says so in as many words.
- **THE DETOUR RATIO IS A GATE, NOT A SCORE — scoring by it was a real shipped bug.** Ranking
  candidates by `walk / boreLength` means the SHORTEST bore always wins: a 1-cell breach through
  a single wall scored x78 while the genuinely interesting 7-cell tunnel scored x11 and lost.
  The rule rewarded holes and penalised passages, which is the exact opposite of its intent, and
  it shipped looking like a tuning problem. Candidates are now ranked by **TURNS first, then
  length** — a turn is what breaks the sightline through a crawlway and what makes the space
  read as a tunnel rather than a pipe — with `crawlwayMinCells` flooring the length beneath both.
- **`crawlwayMinCells` IS A BALANCE FIX AS MUCH AS AN AESTHETIC ONE.** A 1-cell crawlway is not
  a passage, it is a WINDOW: you stand inside the wall with a clear line of fire at something
  that cannot reach you. Length plus a turn removes the sightline, so the exploit closes as a
  side effect. Worth remembering when tuning it back down.
- **ONE CRAWLWAY PER PAIR OF SPACES** (`crawlPairs`), plus `crawlwayMaxPerSpace`. `crawlwayMinSpacing`
  was the only constraint at first and it is the wrong SHAPE — it measures mouth-to-mouth, so
  three mouths spaced along a single wall all passed it and produced three identical holes into
  the same corridor. **The whole hallway network counts as ONE space** in `SpaceKeyOf`:
  per-corridor-run identity would let a room bore into "the hallway" three times at three points,
  which is the same symptom, and from the player's side those are all "the corridor outside".
  **BUT THE HALLWAY NETWORK IS EXEMPT FROM THE PER-SPACE MOUTH CAP** (`SpaceIsFull`), and getting
  that wrong for one round is the lesson. `crawlwayMaxPerSpace` means "a ROOM should not sprout
  three grates"; applied to space -1, which is not a place but the whole corridor network, it
  capped the entire RUN at one hallway-touching crawlway — and nearly every crawlway has a
  corridor at one end. §12's portability rule again: the value was fine, the DENOMINATOR was a
  different kind of thing. The network needs no cap because `crawlPairs` already stops any one
  room drilling into it twice, which was the actual complaint.
- **`RecessFits` is NOT reusable** (it builds a rectangular slab for a recess that becomes open)
  **but its `CellOk` generalises**: a bore cell touching open space is TERMINAL, so it can only
  be the last cell and interior cells can never graze a corridor or run beside one with a single
  cell of rock between.
- **Solid BELOW is required; solid ABOVE deliberately is not.** The tube is FLOOR-ALIGNED — a
  bore centred in the cell puts its sill 0.75m up, past `maxStepHeight` 0.5 with no mantle
  mechanic, so the player could not enter their own crawlway — which means its floor sits at the
  cell base, where an open cell below has the mesher emit that space's ceiling slab at the very
  same plane and the two z-fight. Nothing coincides above (1.5m of rock between the tube's
  ceiling and the floor of whatever is up there), so **crawlways may run UNDER rooms**.
- **SUPPRESSING A MOUTH REMOVES THE WHOLE 3m FACE, NOT A 1.5m ONE** — the trap the feature turns
  on. A quad is all-or-nothing, so the replacement ring of collision lives on the mouth PREFAB,
  putting a crawl mouth on §5's list of pieces whose real shape the greybox cannot provide.
  Suppression is therefore GATED on `CrawlwayGeometryAvailable`, set once in the visualizer from
  the mouth slot: **with nothing authored, crawlways stay sealed behind solid wall rather than
  opening a hole you fall through**. A generator PROPERTY, not a flag threaded through both
  callers — the mesher and the kit placer must agree about where a wall is, and two flags passed
  separately is exactly how they would come to disagree.
- **A MOUTH FACE MUST BE RECORDED AS DENIED, NOT MERELY LEFT UNEMITTED** (real bug, and it was
  documented here as "handled for free" while shipping backwards). `WallFaceRegistry` is a
  DENY-LIST: `PropsAllowed`/`TorchAllowed` return TRUE for a key nobody added, because a kit
  generic wall carries no metadata and allows everything (§7). So skipping the emit made the
  grate the most PERMISSIVE face in the dungeon rather than the most restricted, and a sconce
  or banner would mount straight over the opening. **Absence means "no restrictions", never
  "nothing here"** — worth checking against any other registry that answers a question by
  omission. Now `Record(allowProps:false, allowTorch:false)` plus a `Claim`.
- **AND THE FLAG IS NOT ENOUGH ON ITS OWN — THE CELL MUST BE RESERVED TOO.** `allowPropsInFront`
  only stops props SNAPPED to that wall; an unsnapped floor-scatter prop lands wherever its
  cell put it and never consults a wall face at all. A grate is a doorway you enter on your
  knees, so a crate in front of it does not clutter the entrance, it seals it. Mouth cells are
  therefore reserved in all three prop passes — `RoomPropPlacer` (with the door thresholds and
  bridge landings), `HallwayPropPlacer`, and `RecessPropPlacer` via the same `NoBlocking`
  mechanism a prison uses for its doorway tile. **The chamber's entry tile is not optional**:
  chamber cells are typed Hallway, so the corridor pass scatters debris in them like anywhere
  else, and that tile is both the tightest cell in the chamber and the only way out.
- **The corner-post classifier needs NO change** — posts come from grid solidity and a mouth
  removes only the middle of the face, so the wall corners genuinely still exist. That is the
  difference from an archway, and why `FramedOpening` is deliberately not extended here.
- **SEWER CHAMBERS, AND WHY THEY ARE NEVER DEAD ENDS.** A crawlway may open into a full-height
  room carved in the rock, sealed except for that one grate — loot, or a mob that literally
  cannot follow you out. The bore still runs THROUGH to its far mouth, and that is the load-
  bearing rule: **an out-and-back through a slow crouch tunnel is worse than no tunnel**, because
  it teaches the player that a grate costs thirty seconds for a coin-flip, and after one of those
  they stop entering grates. The feature would get judged once and then skipped forever. Design
  decisions about a slow traversal are really decisions about whether players will ever use it
  twice.
- **The chamber hangs DIRECTLY off a bore cell with no spur tunnel**, which is what keeps
  `Cells` a LIST rather than a tree — branching costs an index and a direction instead of a
  restructure, the tee is a straight tube with a hole in one side rather than a 3-way junction,
  and the only backtracking in the design is one step. **It reuses `RecessFits` WHOLESALE**: the
  host cell is the BORE cell, i.e. solid rock, so the one-opening rule ("touch no open cell but
  h") yields a fully sealed pocket for free. A prison, an alcove and a sewer chamber are the same
  primitive with different hosts.
- **CHAMBER THROUGH-ROUTES ARE COMMON, AND THE REASON IS `RecessFits`' BLIND SPOT.** A chamber may
  gain extra grates onto any OTHER tunnel its footprint already touches (`AddChamberThroughRoutes`,
  `sewerChamberThroughChance`), turning a detour you back out of into a route you pass through —
  the same rule the bore itself follows. They are frequent rather than rare because the one-opening
  rule forbids a footprint touching any OPEN cell but its host, and **bore cells are
  `CellType.Empty`**, so nothing ever stopped a chamber being carved hard against two or three
  other tunnels. Small ones in particular end up wedged between passages. The opportunity was
  already there; the feature only stopped throwing it away.
  **`CrawlwayChamber.Openings` is a LIST, primary first**, and `Dir`/`MouthCell` deliberately stay
  the ORIGINAL entrance so `RecessPropPlacer` keeps a stable frame to orient its hero prop by once
  a chamber has several ways in.
- **`crawlMouthFaces` IS A SET OF (cell, direction) FACES, NOT OF CELLS.** `crawlMouths` answers
  "does this cell have a grate" for spacing; the face set answers "is THIS WALL the grate", which
  is what the mesher and kit placer need — a chamber cell with two openings is one cell and two
  suppressed faces, and a cell-keyed test would blank a wall that should be masonry. Two registries
  for one feature looks redundant until a cell has more than one opening, which through-routes made
  the normal case.
- **A chamber is a DISCONNECTED NAVMESH ISLAND, by construction** — 1.5m of tube bakes nothing
  walkable, so a mob inside can never leave. Free mob-pen behaviour, but NPC spawning (roadmap
  26) must know, or it will place roamers somewhere they can only stand still. The same
  sealed-island property is why the open-network BFS cache stays valid across a carve, and why a
  later bore must be explicitly refused a chamber cell as a far end rather than left to the
  BFS rejecting it by accident.
- **MANHOLES ARE ONE-WAY, AND THAT IS A GENERATION CONSTRAINT RATHER THAN FLAVOUR.** A drain in
  a prison floor drops a storey, and with `maxStepHeight` 0.5 and no mantle there is no climbing
  back — so a network reachable ONLY by manhole is one the player falls into and cannot leave,
  and the run ends there. `ChooseManholes` therefore runs AFTER `ChooseMouths` and returns
  unless the network already has a wall grate, which makes that state unrepresentable rather
  than unlikely. Prisons only, deliberately: a drain in a cell reads as somewhere waste went,
  the same hole in a throne room reads as a hazard nobody built. It needs no new validation —
  `RecessFits` already demands solid above AND below every prison cell, so the rock underneath
  is network-eligible by construction. The floor suppression is one line in `NeedsSlabBetween`,
  but it must be the FIRST line: a bore cell reads as `Empty`, so the `lower is Empty` early
  return would floor the drain shut before anything else looked. Kept out of the prison's
  doorway tile, which on a wide prison is the entire width of the entrance — you would fall in
  walking past rather than choosing to go down.
- **MANHOLE SPACING IS ONE PER PRISON, AND THE RANKING IS WHY IT WAS NEEDED.** Two drains
  generated side by side in one cell, which reads as a fault rather than as two ways down. The
  per-network budget was never protection: candidates are sorted by TUNNEL NEIGHBOUR COUNT, so a
  junction under a wide prison offers several ADJACENT bore cells scoring identically well and
  the greedy fill takes both — the ranking was actively steering the two picks to the same spot.
  Worth remembering as a shape: **a quality ranking with no diversity term clusters by
  construction**, and rarity then only makes the clustering rare rather than impossible, which
  is why it presents as a freak seed. `PrisonAt` identity is the primary rule;
  `sewerManholeSpacing` (Chebyshev) backs it up for what identity cannot see — two NEIGHBOURING
  prisons each placing one against their shared wall, and two different NETWORKS doing the same,
  since `crawlManholes` spans networks exactly as the mouth spacing sets do.
- **`NeighbourMask` COUNTS TUNNELS, NOT OPENINGS — this blind spot has now bitten FOUR times.**
  Piece selection reads a 4-bit mask of adjacent BORE CELLS, and every opening that is not
  another length of tube is invisible to it: a **chamber** (walled off behind a straight), a
  **grate** (a bore cell with a mouth and one tunnel reads as popcount 1 and takes a DEAD-END
  CAP, sealing the entrance while the network visibly continues), and the **manhole's lid**
  (which is not horizontal at all, so the manhole overrides piece selection outright), and now
  the manhole's **BLANK PLATES**, which needed the same list read the other way round — see
  below. Every non-tunnel opening must be added to `want` by hand. More than four openings falls
  back to a cross rather than refusing to place — slightly wrong beats a literal hole in the
  tunnel.
  **`CrawlwaySpec.NonTunnelOpenings` IS THE SINGLE DECLARATION POINT, and that is the fix for
  the recurrence.** The first three instances were each found and patched SEPARATELY at the call
  site, which is exactly why there was a third and a fourth. Collected in one method, a fifth
  kind of opening becomes correct everywhere at once — so add it THERE rather than at whichever
  consumer noticed.
- **BLANK PLATES SEAL THE FACES THE MANHOLE HAS AND THE CELL DOES NOT USE**
  (`kit.crawlwayBlankPrefabs`). The manhole is forced to a 4-way whatever the cell connects to,
  which is deliberate — it lets the generator leave in any direction — but a manhole in a dead
  end then has up to three openings staring into rock.
  **It is the ONE consumer that reads `NonTunnelOpenings` to keep a face OPEN rather than to open
  one**, and getting that backwards would brick up the very grate the network needs. Only the
  manhole is eligible, and the reason generalises: every other piece is CHOSEN from the openings,
  so its geometry and the cell's connections agree by construction and there is nothing to seal.
  A future forced piece joins here.
- **A CHAMBER OPENING ROLLS FOR ITS GRATE (`sewerChamberGrateChance`); NETWORK WALL MOUTHS NEVER
  DO.** Once chambers gained through-routes the grate count per run multiplied and wrenching each
  one stopped being a moment and became a toll — the dead-end crawlway failure arriving from the
  other side, and the same lesson: **a decision about a SLOW interaction is really a decision
  about whether players will do it twice.** An opening you simply crawl through costs nothing and
  is what makes the barred one worth noticing. Wall mouths are the entrance to the whole system
  and stay barred, which is the one place the wrench is the point.
  `crawlwayOpenMouthPrefabs` takes a purpose-built bar-less frame; with none authored the grated
  piece is placed and `CrawlwayGrate.RemoveGrate()` strips the bars, so the feature degrades
  gracefully like every other kit slot. **RemoveGrate DESTROYS the component rather than
  disabling it** — `PlayerInteractor` resolves with `GetComponentInParent<IInteractable>()`, which
  finds DISABLED components, so a merely-disabled grate still offers its prompt on an opening
  with no bars left to pull. Worth knowing for anything else being "turned off" that an
  interactor might still resolve.
- **`RecessFits` CANNOT SEE TUNNELS.** It tests `Grid[pos] != CellType.Empty`, and a bore cell
  IS Empty — the grid-invisible design again — so a chamber footprint will swallow the tunnel it
  hangs off and pipes render straight through the room. Filtered at the CALLER: teaching
  `RecessFits` about sewers would mean teaching prisons and alcoves about them too.
- Stairs and prisons are excluded as mouths (sealed envelope; the one-opening rule a prison's
  validation rests on). Pit openings too — §12's category rule, and it gets its OWN reject reason
  rather than being folded into another, since a tally that reports the wrong rule is worse than
  one that just counts. **Alcoves ARE eligible deliberately** (a grate at the back of a collapsed
  dig is the best entrance the feature can have), which carries a known clash: `RecessPropPlacer`
  puts the hero prop on that same back wall. The fix is the existing one — claim the mouth face
  in `WallFaceRegistry` before it runs.

**Pits (`PlacePits`, `Pit.cs`)** — chasms cut across a room's floor, with the space beneath
carved out, a bridge across and a ladder out. **Rooms only, structurally:**
`HallwayPathfinder.SurroundingsOk` demands solid rock above AND below every corridor cell,
so open-under-open cannot exist in a hallway. A tall room is already a two-level open volume;
a pit applies that to a SUBSET of one room's cells.
- **THE WHOLE MECHANISM IS ONE LINE IN `NeedsSlabBetween`** (`if (IsPitOpening(upper))
  return false;`). Because that method is already the single source of truth for the mesher,
  the kit placer AND the automap, it reaches collision, visuals and the map together and they
  cannot disagree about where the floor is. Everything else falls out of existing rules: the
  pit floor appears because the cell below its bottom is Empty; its walls appear because its
  neighbours are solid; a 2-deep pit reads as one shaft; the top pit cell gets no ceiling
  because it is open to the room.
- **PIT CELLS KEEP THEIR OWN REGISTRY — never `Room.Cells` or `Room.Bounds`.** Extending
  Bounds downward is the obvious move and corrupts five systems that read `Bounds.yMin` as
  "the room's floor": `InteriorFloorCell`, `AllocateInteriorStairs`, `AllocateLadders`,
  `RecordDoor`'s `IsElevated`, and `ChooseStartAndExit`'s climb-out rule — the room would
  read a storey deeper and the vertical Start/Exit pick would be wrong. Putting them in
  `Cells` while leaving Bounds alone very nearly works (`NeedsSlabBetween` would then need no
  change at all) but `Cells` is iterated by `ComputeZones`, `CellCount` and the prop placers,
  all assuming one floor level. **`RoomAt` falls back through `PitAt`** so a pit is still
  STYLED as part of its room; without it the kit puts generic walls down a hole in a themed
  room.
- **BRIDGES ARE GENERATOR-OWNED KIT PIECES, NOT PROPS** — the decision that makes it safe for
  a pit to sever a room. The connectivity flood-fill counts bridge cells as walkable and
  proves every doorway still reachable BEFORE committing the pit, which is only sound if the
  crossing cannot fail to appear. A prop can decline to place, and cell-level connectivity
  cannot see a prop at all (§10). The deck's collider does double duty: what you walk on, and
  what bakes the span into the navmesh — so **NPCs cross bridges with no AI work**, the sharp
  contrast with ladders, which are invisible to `NavMeshAgent` because climbing is scripted
  rather than walkable geometry. If that collider is a MeshCollider its mesh must be
  Read/Write Enabled or the bridge vanishes from the bake in a PLAYER BUILD only (§10).
- Depth SHRINKS TO FIT (2 cells, 1 where the rock is thin), like prisons and alcoves.
  Start/Exit/Merchant are never eligible. Climb-out reuses `LadderSpec` + `LadderClimbZone`
  verbatim, so NPCs knocked in are stuck until `NpcLocomotion.CheckFall` recovers them.
- **A PIT OPENING PASSES EVERY "IS THIS A ROOM CELL" TEST**, because it genuinely is one —
  just floorless. See §12's category rule; `Room.Holes` is the flag, and it lives on `Room`
  rather than only in the pit registry because `InteriorFloorCell` is a Room property with no
  generator reference. Five systems needed telling; assume a sixth exists.
- **Pit RIM** (`kit.pitRimPrefabs`) trims the broken edge where the floor stops; **stair
  LINTELS** (`kit.lintelPrefabs` + per-type) trim the ceiling-slab edge over a stairwell. Both
  are §5's "edge between two emitted surfaces" category — read that entry before adding a
  third, especially for the origin-convention split.
- **Pit STYLING** (`RoomStyle.pitWalls` / `pitFloorPrefabs`) is global, not per-room-type —
  a chasm exposes what lies BENEATH the dungeon, so raw rock reads better than the room's own
  masonry continuing down. No pit ceiling slot (the top is the hole). **Both resolutions must
  be checked BEFORE the room branch in the kit placer**, because `RoomAt` deliberately
  resolves a pit cell TO its room via `PitAt` — otherwise the room branch always wins and the
  slots can never fire. Unauthored falls through to the room's own walls and floor, so the
  slots are inert until filled. Corner POSTS are excluded from pit interiors outright, beside
  the prison-entrance rule that exists for the same reason.
- **A BRIDGE DECK IS A THRESHOLD, NOT A HOLE** (real bug). Decks were first marked in
  `Room.Holes`, which removes a cell from `rz.Floor` and therefore from the prop system's
  threshold FLOOD-FILL — so on a severing pit the flood-fill could not cross the bridge, every
  blocking placement failed the connectivity check, and the room got NO props at all. "No
  floor, nothing may stand here" and "walkable but don't decorate it" are different
  properties; a deck is the second, exactly as doorways already are.

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

**EVERY PLACER'S ROOT MUST BE LISTED IN `DungeonVisualizer.GeneratedRoots`** or its output
accumulates on every regenerate — F1 stacks a second dungeon's worth of geometry on the first,
which reads as a mysterious framerate collapse rather than as a leak. `DungeonAlcoveProps` was
missed when alcoves shipped and went unnoticed for a while. Adding a `Build...` method that
creates a root is only half the job.

**AN EDGE BETWEEN TWO EMITTED SURFACES IS ITS OWN CATEGORY — no existing rule covers it.**
The wall emitter fires on **open→solid** faces only, so wherever something is open on BOTH
sides the seam is left bare: geometrically correct, visually reading as MISSING geometry.
Two such seams now have trim, and both needed a dedicated pass (`FrameFace` is the closest
prior art — it likewise handles a face between two open cells):
- **`pitRimPrefabs`** — the broken edge where a room's floor stops at a pit. `NeedsSlabBetween`
  suppresses the floor quad over an opening while the neighbour emits its own normally, so
  they met at a bare polygon edge. Emitted per opening cell against each neighbour that is
  open-but-not-an-opening, so each face is visited once. Bridge landings are deliberately NOT
  skipped — the break runs unbroken under the deck so the crossing reads as laid ACROSS a
  broken hole.
- **`lintelPrefabs`** (+ `RoomStyle` per-type `lintelPrefabs` / `hallwayLintelPrefabs`) — the
  underside edge of the ceiling slab over the space at the bottom of a staircase. A stairwell
  is the one place the player sees a full storey of wall in a single view, so that seam is far
  more visible than the same junction anywhere else.
  **The test is "a wall face whose BELOW-AND-OUTWARD cell is OPEN"**, and it sits at the
  BOTTOM of the upper stair cell. The first attempt trimmed the TOP of every walled stair
  cell, which drew a line along the shaft's shoulders including the outside faces you can
  never see — a wall face exists on both stair cells regardless of which side you stand on.
  The correct rule is a *different test*, not a filtered version of the wrong one, and it
  excludes the shaft's side walls for free: the sealed 13-cell stair envelope keeps everything
  below-outward solid, so no slab edge exists there.

**TWO ORIGIN CONVENTIONS COEXIST — check which one a new piece belongs to.** `Emit` adds
`globalVisualOffset` to every piece routed through it (walls, floors, ceilings, arches,
stairs, and **lintels**, which must line up with the walls and ceilings they blend). Pieces
placed directly at nominal grid coordinates get **no** offset and are authored BASE-ORIGIN:
ladders, bridges, pit rims, and all props (golden rule 2). The safe question for anything new
is "does this have to align with kit masonry, or does it sit on the grid like a prop?"
Getting it backwards puts the piece a half-cell out, which is the classic symptom.
**A CRAWLWAY IS THE CASE WHERE ONE FEATURE SPANS BOTH CONVENTIONS** — the TUBE is base-origin
(it sits in rock and touches no masonry), the MOUTH is kit-frame (it replaces a wall face and
must line up with the walls either side). That is why the two slots are filed in different
sections of `DungeonKit`, which is grouped by origin convention precisely so this is visible
rather than something you discover from a half-cell offset.

**Directional kit offsets must be applied in the PIECE'S OWN frame** (real bug):
`ladderOffset` was added in world space while the ladder is rotated by
`LookRotation(-WallDir)`, so an offset meaning "push away from the wall" only worked
where the wall happened to face that world axis — the opposite wall drove the ladder
INTO the masonry and perpendicular walls slid it sideways along the face. Now
`rot * offset` (Z = away from wall, X = along it, Y = up; `rot` is yaw-only so
vertical tuning is unaffected). **`archwayOffset` has the same world-space shape and
is deliberately left alone** — arches sit centred in their opening so it's only ever
used for Y, which is rotation-invariant; same one-line fix if a non-zero X/Z is ever
wanted there.

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

**A PIECE WITH A MOVING PART CANNOT BE INSTANCED — this has now bitten FOUR times and the
symptom is identical every time.** `InstancedDungeonRenderer.Commit()` is additive with **no
removal path**, and the instanced tiers harvest the MESH into a static matrix while
`PropInstancer` keeps custom components on the COLLIDER GameObject. So everything works except
the pixels: the collider detaches and moves, the script runs, the audio plays, the interaction
completes — and the visible mesh stays welded exactly where it was placed. Field-reported as
**"I can turn the collider gizmo on and see it moving around, but the rendered mesh doesn't"**,
which is the phrase to recognise. Carryables, destructibles, wall grates and the manhole cover
each hit it independently.
**The detection is now a runtime check, not a rule to remember**: the crawlway placer tests
`prefab.GetComponentInChildren<CrawlwayGrate>(true)` and promotes that piece to
`FullGameObject`, keeping the instanced path for the purely decorative variants. Prefer that
shape — a piece declares its own tier by what it CARRIES — over documenting a rule the next
author has to know. Note the check must be per-PREFAB, not per-slot: within one weighted pool a
grated and an ungrated variant legitimately want different tiers.

**A VFX GRAPH PROPERTY CONSUMED IN *INITIALIZE* IS READ ONCE PER PARTICLE, AT BIRTH — TO
ANIMATE LIVE PARTICLES IT MUST BE IN *UPDATE* (or Output).** Cost a real debugging round
on the torch flame. The symptom is maximally misleading: selecting the VisualEffect in
play mode showed the exposed HDR Color oscillating correctly across a >2x range, which
normally means "it works", while **every visible particle was holding the colour it
captured when it spawned**. With a flicker period (~0.17s at speed 6) far shorter than
particle lifetime, particles from many different phases coexist on screen and average out,
so the fire reads perfectly FLAT while its driving property is demonstrably moving.
**The discriminating test is to slow the driver right down** (`flickerSpeed` 0.3): if it
pulses then, the property is being read at birth and the period was simply shorter than
the lifetime; if it is still flat, the value is not reaching the output at all. Same
discipline as "does moving the camera bring the NPC back?" — find the test that SEPARATES
the causes rather than one that confirms a guess.
**It silently affects TINTING too**, which is the part that hides for months: colour
applied at spawn means a shrine's flame FADES to blue over a particle lifetime instead of
being blue, and that happens at generation time before the player ever arrives.

**`TorchFlicker` OWNS `Light.intensity` OUTRIGHT — anything writing it after spawn must go
through `SetBaseIntensity`, or the value is silently discarded.** The flicker captures its
base in `Awake` and then rewrites the light from that base EVERY `Update`. `Awake` runs
DURING `Instantiate`, i.e. before the placer configuring the light has run a single line —
so `TorchPlacer`'s intensity survived exactly one frame and was then overwritten by the
prefab's authored value forever. **The symptom was "`intensityScale` does nothing, 0 and
100 look identical, but there is a brief bright flash at load"** — a value being REPLACED
rather than scaled. `Awake` cannot fix this itself (it runs before the caller exists), so
the dependency is explicit rather than a matter of execution order — the same discipline
as `PlayerRoomTracker` frame-stamping instead of trusting `DefaultExecutionOrder`.
`BaseIntensity` is exposed for readers wanting the un-flickered value. General shape: **a
component that drives a property every frame from a cached base is a WRITE-ONLY OWNER of
that property**, and every other writer needs a way in.
It also drives the **flame VFX from the SAME noise sample** (`flameAmount`, a multiple of
the light's `amount`, defaulting BELOW 1 — the light is read indirectly off walls and
tolerates a lot, the flame is looked at directly and the same swing reads as strobing). One
sample for both is the point: a second noise source in the graph would run on its own clock
and drift, so the fire would brighten while the light it casts dimmed. The pulse SCALES the
HDR colour rather than lerping toward black, so the room's hue survives.
**AUTHORING `TorchFlicker` ON A TORCH PREFAB USED TO FAIL SILENTLY TWICE OVER** — worth
knowing because adding it there is the obvious instinct, and `TorchPlacer` adds it for you
so you never need to: its `Light` lookup was same-GameObject-only (on a prefab it lands on
the root while the Light sits on a child, so the component is present, enabled, correctly
configured and does NOTHING), and `noiseSeed` was only assigned on the auto-added path, so
every torch carried the same serialized seed and the whole dungeon pulsed in unison. Both
fixed; the tunables live on `TorchSettings` so there is one source of truth.
**NESTING ONE FEATURE'S SETUP INSIDE ANOTHER'S CONDITION MAKES A DIAL LOOK DEAD.** Flame
flicker was wired inside `if (tintFlameToLight)`, so turning tinting off froze the flame and
`flameFlickerAmount` did nothing across its entire range. Whether the fire PULSES and
whether it takes the room's HUE are different questions; when not tinting, the base colour
is now read back off the graph so an untinted flame still flickers around the artist's
colour.

**GAMEOBJECT RENDERERS ARE DISTANCE-CULLED BY `DungeonRendererCulling`; FOR A LONG TIME ONLY
THE INSTANCED PATH WAS.** `InstancedDungeonRenderer` culls per frame by `renderDistance`
because it owns its own submission; everything rendering as a real GameObject — doors,
FullGameObject props, carryables, chests, grates — had nothing but Unity's FRUSTUM culling, so a
long sightline submitted every one of them at any distance. Measured at depth 20, from ONE SPOT,
turning on the spot:

| looking down a corridor | facing a wall |
|---|---|
| 21.0ms frame, **GPU 2.8ms** | 3.4ms frame, GPU 2.1ms |
| 3242 draw calls, 2997 set-pass | 259 draw calls, 184 set-pass |
| 2314 single-instance SRP draws | 32 |
| instanced: 246 calls / 3458 instances | 178 / 2787 |

**GPU 2.8ms against a 21ms frame — CPU-bound on draw SUBMISSION**, and the instanced count barely
moves between the two. Composition by toggling the generated roots: doors ~1200, torches ~600,
prison props ~600, props ~450, instanced ~300, crawlways ~150.
**SHADOWS WERE RULED OUT and it is worth knowing they were the wrong suspect** — the frame
debugger showed `Draw Additional Lights Shadowmap` at **83** of 4087, against
`DrawOpaqueObjects` 3116 and `DrawTransparentObjects` 827. Cutting `maxShadowCasters` or
un-ticking Cast Shadows would have cost real visual quality for nothing. §12's
discriminating-test rule: the statistics panel counts shadow submissions inside "Draw Calls",
so only the frame debugger separates the two.
Doors dominate because there are **190 of them at depth 20** (room count and corridor length
both scale with depth, and every prison adds one) at ~3 materials each — not a generation bug,
just scale.
**`DungeonRendererCulling` is the fix, and three of its decisions are load-bearing:**
- **It toggles `Renderer.enabled`, NEVER `GameObject.SetActive`.** Deactivating would take out
  the collider, the `PhysicsDoor`, the `Carryable` and every script — and a `StaticCollider`
  prop IS its collider, so the world would go walkable-through at range. For doors it is worse
  still: the collider vanishing lets an NPC walk through a closed doorway, and re-activating
  could leave a body inside the door. Disabling only the renderer changes nothing behavioural.
- **It is SLICED (250/frame) and writes only on a CHANGE.** Testing every renderer each frame is
  the cost it exists to remove, and `transform.position` is a managed→native transition per
  access — the profile that made `Separation()` 58% of `NpcLocomotion` before it was batched.
- **`DungeonMesh` MUST stay excluded.** `DungeonMesher` emits the entire shell as ONE GameObject
  with one renderer at the origin, so a distance test against it is meaningless and the whole
  dungeon blinks out once you are 60m from world zero.
Size `cullDistance` to the FOG, not to taste — past where `DungeonFogController` has washed
geometry to the room colour the pop is invisible, which is exactly why `renderDistance` gets
away with it. Separate from NPC dormancy (roadmap 26), which caps AI cost rather than draws and
toggles `NpcLocomotion`; each owns a different property so they compose without knowing about
each other. Atlasing doors 3 materials → 1 compounds with this but only helps doors.

**Torch culling (TorchCullingManager)** — sliced per-frame distance cull of torch
lights + **disciplined shadows**: only the nearest `maxShadowCasters` (default 3)
torches cast shadows; the rest are shadowless fill. **Point-light shadows are a
6-face cubemap each** — see the per-batch shadow section above for the second
half of this fix. The stats overlay counts submissions (incl. shadow passes),
not objects; the generator's `Instanced: N pieces` log is the true count. When
a metric is implausible, interrogate the metric before optimizing against it.

**"INVISIBLE NPCs THAT POP IN" WAS *TWO* BUGS WITH ONE SYMPTOM (cost several
sessions — read the discriminator first).** Goblins were physically present (their
CharacterController blocked movement, and later the `NpcPerceptionDebug` awareness
bar floated over an empty patch of floor) but the mesh wasn't drawn, then "popped"
in. BOTH causes got worse with more NPCs, which is exactly why one masked the other.
**THE DISCRIMINATOR: does moving the CAMERA bring it back?** A compile-bound mesh
appears once and never regresses — a variant compiles once, globally, and every
renderer using that material draws from then on. A culling-bound mesh returns
whenever you look away and look back. Check that FIRST; it separates them in
seconds. Chasing shader-variant counts is the trap, because "worse at 100 NPCs"
smells like a shader problem and isn't necessarily one.

1. **Warmup asynchrony** (fixed first, and genuinely real): `m_WarmupAsync: 1` in
   GraphicsSettings compiles preloaded variants in the BACKGROUND, so the app starts
   rendering before warmup finishes and variants stream in over the first frames. Fix
   = `m_WarmupAsync: 0` (Project Settings > Graphics > Shader Loading), so warmup
   blocks until done. **Two capture attempts (14 then 25 variants) failed before
   this** — the collections were fine and the variant count was a red herring.
   Preloaded collections live in `Assets/_Settings/SharedVariantcollection*.shadervariants` and
   must stay listed in Graphics' Preloaded Shaders (which stores them by GUID, so an
   untracked collection silently drops off the list on a fresh clone). NB: in-EDITOR
   the preload is tied to Editor process startup (adding a collection mid-session
   needs a full restart), but a Player build honors it immediately — which is what
   made "still broken in a build" the clue pointing at the async flag.
2. **Frustum culling from a bad root bone** (the remaining, larger half — see §10's
   NPC model conventions for the rule). The `SkinnedMeshRenderer`'s root bone was the
   HIPS, so the bind-pose AABB rode an animated bone and slid off the body. This is
   the one that answers "yes" to the camera test.

Correcting the record matters here: `m_WarmupAsync: 0` was believed to be the whole
fix and was documented as such for a while, while the culling bug was still shipping.
A confirmed improvement is not proof of a complete diagnosis.

**Floors/ceilings between STACKED SPACES — `DungeonGenerator.NeedsSlabBetween`:**
both the mesher and the kit placer once decided on a floor by asking "is the cell
below solid?". That conflates a **multi-story room** (stacked cells, ONE open
volume, must NOT have a slab through the middle — the reason the rule existed)
with **two unrelated spaces that merely happen to be stacked** — a real generated
layout, e.g. a satellite closet with a hallway routed above it. Both cells read
open, so neither got a slab and a hole opened between them. **Room identity is the
discriminator** (`BuildFootprint` writes a room's cells at every Y in its bounds,
so both levels of a tall room resolve to the same `Room`; a closet and the
corridor above it never do). The predicate lives on the generator and is called by
BOTH the mesher and the kit placer so collision and visuals can't drift. It
carries an explicit **stair-pair** case that the old code got implicitly: upper
stair cells are written directly atop lower ones (`Grid[t1 + up] = StairUpper`)
and the kit placer's floor rule has NO cell-type guard — it relied entirely on
the solid-below test to skip them, so without it every corridor staircase gains a
floor tile through its middle.

**YOU CANNOT `MaterialPropertyBlock` THE INSTANCED PATH — vary the MATERIAL instead.**
Nothing renders through a kit prefab's `MeshRenderer`: `InstancedDungeonRenderer`
harvests it ONCE into a `Proto` and draws with `Graphics.RenderMeshInstanced`, so a
block has no renderer to attach to (an `EmissionController` on a kit wall does
exactly nothing — real bug). True per-instance colour would need the property in the
shader's instancing buffer, which URP/Lit doesn't do for `_EmissionColor`. But
`BatchKey` already includes the material, so a swapped material is its own batch
drawing its own colour — `AddInstance` takes an optional replace/with pair.
**CACHE the variants** (`EmissiveMaterialVariants`, keyed by colour): same colour must
return the SAME material or every piece becomes its own batch and instancing is gone.
They're runtime copies, so **destroy them on regenerate** or every F1 leaks a set.
Kit emissives are tinted to the room's TORCH COLOUR — same source as fog and the
flame VFX (§7), so they can't drift, and a new room type gets correct candles free.
**`PropInstancer.PlaceProps` takes the same replace/with pair**, so per-room tinting
reaches every prop placer and not just the kit callback — see `PropTint` in §8, which
is the one resolver all three prop passes share.

**A CUSTOM SHADER ON KIT OR PROP GEOMETRY MUST DECLARE INSTANCING, AND FAILS SILENTLY
IF IT DOESN'T** (real bug — the stained glass that "didn't glow" was never DRAWN).
The kit draws through `Graphics.RenderMeshInstanced`, where each instance's transform
lives in an instancing buffer that only `UNITY_SETUP_INSTANCE_ID` reads; without
`#pragma multi_compile_instancing` + `UNITY_VERTEX_INPUT_INSTANCE_ID` +
`UNITY_SETUP_INSTANCE_ID`, `TransformObjectToHClip` uses whatever single
`unity_ObjectToWorld` happens to be bound and every instance collapses onto one
another. **There is no error**, because `InstancedDungeonRenderer` force-sets
`enableInstancing` on every material it harvests (`BuildProto`) which satisfies
Unity's runtime check and suppresses the "material does not support instancing"
message. The symptom is a submesh that simply isn't where it should be — and because
a multi-material piece still draws its OTHER submeshes correctly, it reads as "that
part looks unlit" rather than "that part is missing". Same family as the
MaterialPropertyBlock rule above: anything authored against a normal `MeshRenderer`
needs re-checking against `RenderMeshInstanced`.
**`MaterialGlobalIlluminationFlags.EmissiveIsBlack` must be cleared explicitly**
(cost an hour): Unity sets it on any material whose authored `_EmissionColor` is
black — exactly the case when the source was previously driven by a property block —
`new Material(source)` copies the flag, and a later `SetColor` does NOT clear it, so
the variant is tinted but never glows. Assign `globalIlluminationFlags` to fix.
(Also: emission only BLOOMS above 1, the palette is LDR-range, and bloom + HDR must
be on in the pipeline — the "tinted but flat" symptom has three separate causes, so
`debugEmissive` logs which.)

**`PlaceCallback` carries the OWNING CELL.** `posCells` can't recover it — a wall's
position sits ON the face between two cells, so flooring it lands on the solid
neighbour or the open one depending which way the face points. Consumers need the
cell to resolve a piece's room (and so its RoomStyle) for any per-room visual
variation. NB the reserved capped-asset path (fireplaces etc.) calls `place` DIRECTLY,
bypassing `Emit` — it's easy to miss when touching this.

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
  **IT DOUBLES THE DRAW CALLS OF EVERY TOONLIT MATERIAL IN THE PROJECT.** The Outline pass
  is tagged `LightMode = SRPDefaultUnlit`, which URP renders in the opaque queue alongside
  `ForwardLit` — so one material is TWO submissions, always. Found by unticking a single
  renderer and watching Draw Calls fall by 2 for a 1-material mesh and by 6 for a 3-material
  one. It is not a bug and the look depends on it, but the cost is a flat ×2 on the whole
  dungeon and it was not priced in when this was first written down.
  **It is also what finally reconciled the door numbers**: 190 doors × 3 materials × 2 = 1140,
  against ~1200 measured. Without the outline factor the arithmetic stalls at 570 and the
  measurement looks wrong — which is what it was blamed on for a while.
  **`_OutlineEnabled` DOES NOT SAVE THE DRAW CALL, and an earlier version of this entry claimed
  it did.** It is a uniform branch collapsing the hull to a degenerate triangle in the VERTEX
  stage — the mesh is still submitted, the pass still runs, the set-pass call still happens.
  It saves RASTERISATION, which is worth nothing while the frame is submission-bound (GPU 2.8ms
  of 21ms). The same trap applies to any "cull the outline at distance" idea done in the shader.
  **To actually stop submitting the pass, the material's SHADER must not have it** — i.e. a
  `ToonLit` variant with the Outline pass absent, sharing its code with the original through an
  `.hlsl` include so the two cannot drift. That is a permanent 50% cut for anything that should
  never have an outline, and it multiplies with atlasing (a 3-material door: 6 draws → 1).
  But note **whole-renderer distance culling (roadmap 16b) dominates the DISTANCE version of
  this idea** — it is the same per-object bookkeeping for twice the saving, since it drops the
  lit pass too, and the per-room fog already hides the pop exactly as `renderDistance` relies on
  today. The one place outline-distance culling is uniquely possible is the INSTANCED path,
  where individual renderers cannot be disabled but `RenderParams.shaderPass` lets the outline
  be submitted to a shorter radius than the lit pass.
  **`_OutlineWidth = 0` IS THE WORST CASE, NOT THE OFF SWITCH** — the trap that made
  `[ToggleUI] _OutlineEnabled` necessary. A zero-width hull is a shell exactly COINCIDENT
  with the mesh, and with `Cull Front` + `ZWrite On` the two surfaces z-fight, so the
  outline colour wins a speckle of fragments across the whole surface. The symptom is "the
  mesh changed colour", not "the outline is still there", and it is worst on thin
  doubly-curved geometry — chain links, wire, foliage — where nearly every fragment sits
  near the silhouette. Off (and width 0) now collapses all three hull vertices onto one
  clip-space point, a zero-area triangle that rasterizes nothing.
  **A UNIFORM BRANCH, deliberately not a `shader_feature`**: the condition is constant
  across a draw so it costs nothing, while a keyword would add a variant — and this project
  preloads its variant collections by GUID and has already lost sessions to variants
  arriving late (§5). Same constraint the emission feature was built under.
  **The width is in WORLD METRES and scales with nothing** — not distance, not object size.
  So the 0.015 default is a 1.5cm shell, which is proportionate on a wall and completely
  swallows a 2cm-thick chain link. On small or thin props the outline usually wants turning
  off rather than turning down.
- **Banded specular glint** — toon highlight, gated by the light's banded
  diffuse.
- **Packed PBR mask** (`_MaskMap`: G=roughness, B=metallic) modulates the glint
  per-pixel (rough=matte, metal glints tinted by albedo). Default black = uniform
  glint = pre-mask behavior. Import the mask with **sRGB OFF** (it's data).
- **Normal map** (`_BumpMap`, `_BumpScale`) — perturbs all lighting. Import as
  **Normal map** type. Strong normals + razor-hard bands = crawling band edges;
  `_BumpScale` trades relief vs. band cleanliness.
- **Emission + per-instance FLICKER** (`_EmissionColor` HDR/black, `_EmissionMap`
  white, `_FlickerAmount`, `_FlickerSpeed`, `_FlickerCellSize`). Added here rather
  than as a new shader because a candle's WAX still needs proper lighting, so a
  separate emissive shader would have been a near-copy of this one that then drifts —
  and the candles were on URP/Lit, making them the one prop not toon-shaded.
  Property names match URP/Lit so a material switches over keeping its values; the
  `_EmissionMap` WHITE default is load-bearing, since colour-only emission with no map
  assigned is how the candle is authored and a black default silently kills it.
  Emission is applied **unbanded, after lighting, before fog** — unbanded because a
  flame is a SOURCE, not a lit surface (`Ramp()` would darken a candle in shadow).
  **No new `multi_compile`, so zero extra shader variants** and the preloaded
  collections need no recapture — deliberate, given §5's invisible-NPC cost.
- **THE SHADER IS THE ONLY PLACE A `StaticDecor` PIECE CAN ANIMATE.** That tier has
  mesh-only instancing — no GameObject, so no `Light`, no `TorchFlicker`, nothing a
  script can reach — and a MaterialPropertyBlock can't touch the instanced path (§5).
- **A PER-ELEMENT ID MUST COLLAPSE TO ONE VALUE PER ELEMENT, OR THE ELEMENT SHEARS.**
  The flicker phase is hashed from the instance origin, which is correct per PROP but
  identical for every flame of a single candelabra mesh — they pulse in unison. Raw
  `positionOS` is the obvious fix and is WRONG: it varies per VERTEX, so each vertex
  of one flame picks its own phase and the flame shears instead of pulsing. Two things
  that work: **vertex colour** (authored per flame, exact, and free because a mesh
  without vertex colours reads white = a constant), and **`_FlickerCellSize`**, which
  floors `positionOS` to a cell so a whole flame collapses onto one value — size it
  larger than a flame and smaller than the gap between them; a flame straddling a
  boundary tears, which is the failure mode to recognise. UVs are NOT a usable id when
  the elements are duplicates of one mesh, because duplicates share UV islands exactly.
  NB ToonLit now reads vertex colour on every mesh in the project for this.
- Known API gotchas: `TransformObjectToHClip` (NOT `TransformObjectToWorldHClip`).

**SSAO'S `Source` AND `After Opaque` DECIDE WHETHER THE WHOLE DUNGEON IS DRAWN TWICE.** With
`Source = DepthNormals` and `AfterOpaque = 0`, URP runs a depth-normals PREPASS — measured at
**72 draws against 140 for the entire opaque pass**, i.e. a full extra geometry submission every
frame. Ticking **`After Opaque`** removes it outright (the prepass is replaced by a single
`CopyDepth`) while keeping SSAO working AND keeping ToonWater's foam, which was verified rather
than assumed. **BUT IT MOVES SSAO TO THE FAR SIDE OF FOG, AND `Falloff` IS WHAT KEEPS THAT FROM SHOWING.**
With it off, SSAO folds into lighting during the opaque pass, so each shader's `MixFog` at the
end of its fragment stage fogs the occlusion along with everything else. With it ON, SSAO is a
full-screen pass applied AFTER opaque — i.e. after every shader already applied its fog — so it
darkens pixels the fog had washed out and occlusion is visible THROUGH the haze on distant
geometry. The stock `Falloff` of 100 metres applies AO far past where fog has taken over, which
is what makes it obvious; **dropping Falloff to ~10 confines AO to the near field**, keeping the
contact shading on cobblestone a metre away while distant geometry goes back to pure fog. Two
settings whose names give no hint they are coupled. Free win once tuned; take it on any new
renderer, and set Falloff at the same time.
The uncomfortable corollary for the entry below: adding the `DepthNormals` pass to ToonLit was a
correct fix for a real bug, but the CHEAPER fix was this renderer setting, since SSAO's source
mode is what redirected depth-texture population in the first place. Keep the shader pass — it
costs little and its absence fails silently — but reach for the renderer settings first.
NB the live renderer is **`Assets/PC_Renderer.asset`**; the copy under `SourceFiles/Settings/` is
the Unity sample and editing it does nothing.

**A SHADER CAN BE COMPLETE FOR EVERYTHING THAT MAKES IT LOOK RIGHT AND STILL BE INVISIBLE
TO A PIPELINE PASS.** ToonLit rendered, lit, banded, outlined, cast shadows and had a
correct `DepthOnly` pass — and was **absent from `_CameraDepthTexture` entirely**, because
it had no **`DepthNormals`** pass. URP does not always build the depth texture from
`DepthOnly`: this project's `PC_Renderer` runs **SSAO with `Source = DepthNormals` and
`AfterOpaque` off**, which makes the renderer run a **depth-normals prepass** and populate
the depth texture from THAT, drawing only shaders carrying that pass. Missing it is not a
half-measure; the shader contributes nothing.
Nothing complains, because every property that makes a surface LOOK correct still works.
What silently stops is everything DOWNSTREAM of scene depth — `ToonWater`'s foam and
shallow/deep blend had nothing to read, and **SSAO was never applied to a single
toon-shaded surface**, i.e. to almost the whole dungeon. `TextureEmission` and
`InteriorMapped` already had the pass, which is exactly why the gap survived for so long in
the one shader nearly everything uses.
**THE TEST THAT FOUND IT IS THE REUSABLE PART:** two identical cubes with vertical sides
intersecting the water, one ToonLit and one URP/Lit. Same geometry, same intersection, and
only the URP/Lit one foamed — which disproved a confident and wrong explanation about
surface slope. Holding the geometry constant and varying only the material is what turned
"foam doesn't work" into a one-screenshot answer. §12's discriminating-test rule, applied
to shaders.
**So the checklist for any new custom shader on world geometry is now four items**, each of
which fails silently and differently: instancing (§5), fog (below), `DepthOnly`, and
`DepthNormals`. Add the last two even if nothing reads depth today — the cost is a few
lines and the failure mode is a feature that quietly never worked.

**FOG IS NOT AUTOMATIC — a shader that omits it is the one surface that never
recedes.** Three parts, all required: `#pragma multi_compile_fog`,
`ComputeFogFactor(positionCS.z)` in the vertex stage, `MixFog` on the FINAL colour.
It matters more here than in most projects because `DungeonFogController` drives
`RenderSettings.fogColor` PER ROOM from the torch palette (§7), so skipping fog also
opts a surface out of the room's colour identity — a stained-glass window in a blue
shrine rendered as though it were in neutral air. The symptom is a surface that looks
perfect up close and like a UI decal from across a room. Fog the emission too: fogging
only the base leaves the glow punching through undimmed, the same decal read in
subtler form. And note **bloom is post-process and runs AFTER fog**, so a bright HDR
surface keeps blooming through any amount of haze — dimming the emission itself
(a distance fade) is the only thing bloom then sees.

**Sibling shaders that share ToonLit's conventions** (`_ShadowTint` ambient floor,
`Ramp()` banding, Forward+ `LIGHT_LOOP_BEGIN/END` so torches light them) — they must,
or they don't look like they belong to the same world:
- **`Dungeon/ToonWater`** — wells and fountains. `_ColorBands` bands the depth ramp
  the same way lighting is banded. Two authoring notes: it needs **Depth Texture ON
  in the URP asset**, and the foam band's width is `_FoamDistance` — an early version
  multiplied it by `_FoamCutoff` inside a `step()`, which made the visible band ~5cm
  and read as "no foam at all". `_DebugDepth` draws 1m contour RINGS rather than a
  saturating ramp, deliberately: the first debug view was a gradient whose legend
  could be (and was) read backwards, and rings can't be misinterpreted.
  **IT CAN ONLY SEE WHAT IS IN THE DEPTH TEXTURE**, so "no foam anywhere" is more often a
  problem with the OTHER shader than with this one — see the `DepthNormals` rule above,
  which had ToonLit (and therefore every wall, floor and prop) missing from scene depth
  while URP/Lit objects foamed normally. Reach for `_DebugDepth` first: if a surface shows
  no depth banding at all, it is not in the texture and no water setting will help.
  Foam is SHALLOW WATER, not proximity — the band's width is set by how much water sits
  between the surface and whatever is behind it.
  **CORRECTION WORTH KEEPING:** while the depth texture was broken, "vertical walls give no
  foam, bevel the pit rim" was offered as the explanation and written down as fact. It was
  wrong — an explanation invented for a symptom whose real cause was the missing pass. Once
  walls actually write depth, a water plane intersecting a vertical wall foams perfectly
  well along the waterline and no bevel is needed. **A plausible mechanism that explains the
  symptom is not evidence**, and it is worth noticing when one is being reached for in place
  of a test (§12).
  **SURFACE MOTION IS SAMPLED IN WORLD XZ (`_WorldSpaceMotion`, default ON) SO SEPARATE PLANES
  READ AS ONE BODY OF WATER.** Crawlways are watered one plane per pipe section, and driven
  from mesh UV every plane runs an identical copy of the ripple pattern from its own origin —
  so the pattern RESTARTS at each join. **The symptom reads as a geometry seam, not as a
  shader setting**, which is what makes it hard to place: the planes are lined up correctly
  and the discontinuity is in the PHASE. Same reasoning as `GroundFog` sampling its noise in
  world XZ below.
  **BOTH the fragment ripple UVs AND the vertex swell must use the same space**, or the fix
  half-lands: world ripples over an object-space swell leaves the geometry disagreeing about
  height at a shared edge while the normals line up, which is a subtler version of the same
  seam and looks like a mesh problem. The swell samples the UNDISPLACED world position so the
  wave cannot feed on its own output.
  **Switching spaces means RETUNING the ripple scales by a large factor, not nudging them** —
  mesh UV runs 0..1 across a whole plane, world XZ is in metres, so at a 3m section the same
  number tiles roughly 3x as often (hence the range now reaching to 0.01). In world mode the
  `_BumpMap` tiling/offset is deliberately bypassed so the ripple scales are the only tiling
  dial rather than two numbers that multiply. Turn the toggle OFF for water that MOVES (a
  static world pattern would swim past it) and for a single curved mesh whose authored UVs
  follow the shape. A `[Toggle]` uniform branch rather than a `shader_feature`, for the
  variant-count reason `_OutlineEnabled` carries above.
  The depth side needed no change and is worth knowing: `waterDepth`, the shallow/deep blend
  and the foam band all come from `screenPos` + `SampleSceneDepth`, so they were already
  scene-global and continuous. **If a seam is a FOAM break rather than a ripple
  discontinuity, this is not the cause** and the planes really are misaligned.
- **`Dungeon/GroundFog`** — ankle-height drifting mist, driven by an editor-authored
  `ParticleSystem` (see `GroundFog.cs` in §10). **BILLBOARDS, and the reason is
  structural, so don't "simplify" it back to planes:** floor-parallel planes read well
  until they intersect a wall, pillar, crate or goblin, where they leave a HARD
  horizontal line. Soft-particle depth fade is the standard fix and CANNOT work there —
  a plane hugging the floor has only the floor behind it (~20cm), so there is no depth
  RANGE to fade across, and the fade region is bounded by the fog's own height above
  the floor no matter what the parameters say. Contact softness at 0 and at 1 looked
  identical because both were imperceptible. Three fixes were tried and rejected first:
  a bigger `_DepthFade` (the gap is smaller than any useful value), a world-space
  "rising and horizontally close" test (blind to a wall directly beside the fragment),
  and a screen-space depth ring written with the WRONG SIGN (it searched for occluders
  NEARER than the fragment; the wall is behind). Camera-facing quads dissolve the
  problem by construction — grazing intersections, no edge-on orientation, and no
  visible waterline where stacked layers pool at eye height. Also: Forward+ light
  accumulation was UNBOUNDED and several torches summed into a bright green band —
  `_MaxLight` clamps it. Noise is world-XZ (so puffs stay put as you walk instead of
  swimming with the camera) and vertex `COLOR` is respected so colour-over-lifetime
  still works.

---

## 7. RoomStyle — per-type architectural identity (ScriptableObject)

One RoomStyle asset defines a room type's whole look. What it holds:
- **THE PALETTE SUPPLIES THE HUE; EVERY CONSUMER OWNS ITS OWN BRIGHTNESS.** `torchColor`
  is one HDR swatch read by five systems, and its MAGNITUDE used to scale all of them at
  once — raising a room's colour intensity to make its flames bloom also flooded the walls
  with light, washed out the fog, and blew out every candle and glowing kit piece in the
  room. `intensityScale`, which only ever touched the Light, looked inert by comparison.
  **`RoomStyle.Hue()`** is the single normalizer (scale so the strongest channel is 1 —
  this preserves the channel RATIOS, hence hue and saturation, and discards only
  magnitude). Brightness now lives here:

  | consumer | dial |
  |---|---|
  | torch light | `Entry.intensityScale` |
  | fog | `Entry.fogIntensity` |
  | emissive kit | `kit.emissiveIntensity` |
  | socket emissive | `PropSocket.tintIntensity`, else the kit's |
  | prop emissive | `PropEntry.tintIntensity` |
  | **flame VFX** | **the raw HDR magnitude — bloom IS the flame** |

  The shared-hue invariant below is untouched: everything still comes from ONE colour, so
  a blue shrine cannot get orange haze. **That rule was always about hue, never
  brightness.**
  **THE FLAME IS THE EXCEPTION AND MUST KEEP THE RAW COLOUR.** `KitSocketPlacer` fed one
  palette to both the Light and the flame VFX, so normalizing at the source dropped
  socketed candles and fireplace fires below the bloom threshold — it now carries both
  forms, as `TorchPlacer` already did. Any new consumer must decide which form it wants.
  **0 MEANS "NOT AUTHORED", NEVER "BLACK"**, for `intensityScale`, `fogIntensity` and
  `tintIntensity` alike — an emissive or a fog colour at literal zero is indistinguishable
  from a broken tint, so zero cannot be a usable value. `intensityScale = 0` therefore
  discards the WHOLE entry (colour included) and falls back to the defaults; use 0.05 for
  near-dark. And since `RoomStyle` predates `fogIntensity`, `DefaultFogIntensity` treats 0
  as unauthored — §9's ScriptableObject-initializer lesson, where a raw zero would have
  multiplied every fog colour by nothing and blacked out the dungeon.
- **CORRIDORS TAKE `defaultTorchColor`, AND `TorchPlacer` WAS THE ONE CONSUMER THAT
  FORGOT** (real field bug: "hallway props take the hallway hue, torches don't"). It
  seeded its colour from `TorchSettings.color` and only reassigned when `RoomAt` returned
  a room, so roomless cells silently kept the torch settings' own swatch while fog, props,
  kit emissives and sockets all used the style default — a corridor's FIRE and its HAZE
  came from two different colours. Deliberately NOT `style.For(RoomType.Generic)`: a
  corridor is not an unauthored room, it is its own place, and a real Generic entry must
  not silently become the corridor palette. Same distinction `hallwayAudio` draws against
  the default audio profile.
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
- **BANDING IS NOT WALLS-ONLY — `RoomStyle.BandedAsset` applies the same Bottom/Middle/Top
  vocabulary to any STACKED kit piece**, which currently means interior columns and corner
  posts (`kit.interiorColumnBands` / `outerCornerPillarBands` / `innerCornerPillarBands`).
  Both are already placed one segment per storey, so a base / shaft / capital set is just
  banding applied to the segment index. **Strict bands as for walls** — a band with nothing
  eligible falls back to the UNBANDED list rather than borrowing another band's pieces;
  borrowing is what put capitals at floor level. Additive: the existing unbanded arrays are
  untouched and empty band lists reproduce the old behaviour exactly.
  **TWO GATES HAD TO CHANGE OR THE FEATURE WOULD HAVE LOOKED BROKEN** — both the corner-post
  and interior-column paths tested only the UNBANDED array for emptiness, so moving your
  pieces into the band lists and clearing the old one disabled posts and columns entirely.
  §12's category rule in miniature: giving a subset of a category a new property means
  auditing everyone who reads that category, including the "is this authored at all" checks.
  Columns re-pick per segment with the segment folded into the salt, so a 3-storey column is
  three different pieces rather than one repeated.
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
  **FLAGS ARE AUTHORED PER LIST, SO THEY MUST BE READ BACK PER LIST** (real field
  bug). `WallFlagsFor` merged most-restrictive keyed by PREFAB ALONE, but the same
  prefab legitimately appears in several lists with different flags — `Wall_Basic_P`
  is a normal hallway wall that takes torches AND a pit-interior wall that must not
  (a lit chasm reads as another room, §4). So one `allowTorch: 0` in `pitWalls`
  silently disabled torches for that prefab EVERYWHERE, and a hallway whose set
  explicitly allowed torches generated none at all — while also suppressing
  wall-mounted props in front of every such face dungeon-wide, which nobody had
  noticed because absent props look like unauthored props. Now keyed by
  `(prefab, WallContext)` — room TYPE, or Hallway/Prison/Pit — with the emitter
  tracking which list it picked from alongside the prefab. Merging still happens
  WITHIN a list, which is the case the restrictive reading was written for.
  **The general shape: reusing one asset across contexts means its METADATA is
  context-scoped too, even when the asset isn't.**
- **`WallAsset.rockDepth` — A WALL'S OUTWARD NEIGHBOUR IS NO LONGER ALWAYS SOLID.** That
  neighbour being solid is what MADE it a wall, so a recessed niche, a barred window's reveal
  or a shrine cut into the masonry could occupy that cell as deeply as it liked and nobody
  ever wrote the assumption down. A sewer bore is the first thing that ever takes it, and a
  tube piece runs its arms all the way to the cell FACE so it can meet its neighbours — so a
  **cross or a tee parked behind a recess pushes visibly through it**. §12's category rule
  again: a subset of "solid rock" gained the property "might not be solid", and the single
  consumer that assumed otherwise failed visually rather than loudly.
  `rockDepth` (metres behind the wall plane, 0 = flat = every existing asset) is checked
  against `RockClearance`, which is `kit.crawlwayWallClearance` behind a bore cell and
  infinity everywhere else. **Ordering was NOT the problem** — the tempting reading is "walls
  are placed before pipes so the wall cannot know", but `crawlCells` is fully populated during
  generation, long before any placement pass, and `gen.IsCrawlwayCell` is a direct query.
  Three things that would each fail quietly:
  - **The RESERVATION pre-pass needs the filter too.** A capped asset never goes through
    `PickWall` — it is dealt onto a face in `DealCappedAssets` and emitted through
    `EmitReserved` — so filtering only the general pick leaves a capped recess clipping while
    the uncapped ones behave. Exactly the shape that made `maxPerRoom` look ignored once.
  - **The depth filter is NOT relaxable, unlike the noise one.** `PickWall` deliberately falls
    back to the whole pool when an over-narrow `noiseRange` excludes everything, because an
    over-narrow range should look wrong rather than missing. A wall pushing through a pipe is
    a visible clip regardless, so it stays excluded inside that fallback.
  - **A null pick means "nothing in this set fits", not "no set".** All five call sites now
    leave `emitted` false so the face drops to the kit generic. Setting it unconditionally —
    which is what the code did before — would skip the wall entirely and open a HOLE into the
    bore, which is worse than the clip being fixed.
  Deliberately CONSERVATIVE: any adjacent bore cell disqualifies, not only one whose piece
  points an arm that way. Which piece lands there is decided later by `TubePieceFor`, and
  having the wall emitter predict piece selection would couple two passes that share nothing
  but the cell registry. The cost is that a flagged asset appears somewhat less often than its
  weight implies near a sewer — surfaced as `recess 0.4m` on the collapsed inspector entry, so
  "why is this variant rarer than I asked" is answerable without opening every element.
- **Wall variant FREQUENCY vs DISTRIBUTION — two dials answering different questions,
  and only one of them existed.** The pick was `Hash(cell) % pool.Length`: every
  UNCAPPED variant exactly equally likely, fakeable only by listing a prefab twice
  (which also merges its flags most-restrictively — the trap above, at close range).
  - **`WallAsset.weight`** is frequency. UNCAPPED assets only; a capped one is dealt
    by the reservation pre-pass which already fully determines its count, so the
    inspector hides weight on capped entries rather than advertising a dead setting.
    Weights resolve PER BAND, so an asset eligible in Bottom and Middle competes
    separately in each and its share depends on what else is eligible there.
  - **`WallAsset.noiseRange` + `ValueNoise`** is distribution, which weights can never
    touch. A per-face hash is WHITE NOISE — statistically even, spatially
    structureless — so weighting only makes a variant RARER, never CLUSTERED: you get
    one cracked wall here and another three cells away, forever, and never a damaged
    SECTION. Sampling a smooth field at the face's position gives clusters because
    neighbouring faces sample nearly the same value. Default range (0,1) is eligible
    everywhere, so clustering is opt-in per asset and `kit.wallNoiseScale` 0 disables
    the field entirely.
  - They COMPOSE: noise decides what is ELIGIBLE at a face, weight decides the MIX
    among those. Noise picks a region's character, weight fills it.
  - **NOT `Mathf.PerlinNoise`** — Unity documents it as free to differ between
    platforms and versions, which would break rule 4 in the one way that stays
    invisible until two machines are compared. `ValueNoise` is built from
    `DungeonKitPlacer.Hash`, the integer hash every placement pass already trusts.
    Each floor gets its own field (y folded into the salt); a shared vertical field
    aligns a tall room's damage into a stripe that reads as deliberate masonry.
  - `Emit` split into `Emit` (uniform pick — still right for floors, ceilings, bars,
    arches) and `EmitPrefab` (placement + socket recording for an already-chosen
    prefab), so the weighted wall pick and the reserved capped-asset path share ONE
    placement path. That path had already diverged once and needed its socket
    recording bolted on separately.
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
  emission. **PRISONS RESERVE TOO** — a closet is a discrete enclosure, so
  "1 per room" has an obvious meaning there; they were ignoring the cap entirely
  (no reservation dealt the asset, AND `PrisonWalls()` handed it to the general
  hash pick anyway, so a "1 per room" drain competed for every face at even odds).
  Capped entries are excluded from the general pool exactly as `UnlimitedWalls`
  does for rooms — leaving them in BOTH is what made the cap look ignored.
  `DealCappedAssets` is shared, since rooms and prisons differ only in which cells
  form the enclosure and how a cell maps to a band. **HALLWAYS STRUCTURALLY CANNOT
  honour it** — a corridor network has no unit to count per — so `HallwayWalls()`
  WARNS rather than letting the field quietly do nothing. NB reservations currently
  include a prison's DOORWAY face, where the bars or door go; exclude
  hallway-facing faces if that turns out to matter.
- **Openings** (`OpeningSet` per type): archway + door prefabs. Chosen by the
  room the opening leads INTO (a throne entrance gets the throne arch; a treasury
  closet door styles the treasury). Empty = kit generic.
  **An opening is defined by ROOM MEMBERSHIP, not CellType** (real bug): `BuildArchways`
  and `FrameFace` once required `Hallway → Room` by cell type, which misses every
  doorway with an interior staircase through it — those cells stay in `Room.Cells` and
  only their CellType changes to StairLower/StairUpper, so they read as "not a Room"
  and got NO ARCH. The generator itself never had this problem: `RecordDoor` tests
  `room.Contains(hallwayCell + d)`. **`BuildArchways` and `FrameFace` must agree** —
  `FrameFace` feeds `FramedOpening`, which tells the corner-post classifier a face
  already carries its own frame, so fixing only the arch side is WORSE than the bug
  (arches appear while posts still land through them). Prisons stay excluded; they
  frame their openings with bars.
- **Pillars** (outer/inner per type in OpeningSet). Resolved **priority-by-
  specialness** at edges touching multiple rooms: `RoomStyle.Specialness()`
  ladder (Start/Exit/Throne 5 > Treasury 4 > Merchant 3 > satellites 2 >
  categories 1 > Generic 0). Highest-scoring adjacent room's pillar wins; hallway
  contributes nothing.
- **Props** (`PropSetEntry` per type — shareable PropSet assets). See §8.
- **Sewer chambers and crawlways are two more SPACES** (§7's grouping), not new `RoomType`s —
  that enum is what the typing pass, the MST, satellites and the specialness ladder all reason
  about, so adding to it means auditing every one of them for no gain. **Identity for styling
  has never come from `CellType` in this project**: alcoves are typed Hallway, pits resolve
  through `PitAt`, and these resolve through `ChamberAt`/`IsCrawlwayCell`. That is what lets a
  1.5m bore have its own fog while its cell stays solid rock to the mesher.
  - **Chambers** get walls/floors/ceilings/props/audio and a full palette. Every consumer must
    check them BEFORE its hallway fallback (kit walls, kit floors, fog, `AudioSpace`,
    `TorchPlacer`) — chamber cells are typed Hallway, so otherwise the hallway branch always
    wins and the slot can never fire. Exactly the rule pit styling already carries against the
    room branch.
  - **Crawlway bores are FOG-ONLY, permanently.** A bore's cell is solid, so it has no wall
    faces and no torch slot can ever exist in one — `TorchPlacer` needs no crawlway branch and
    never will. The palette exists to colour the haze and anything emissive authored onto the
    pipe itself.
  - Chamber contents make `RecessPropPlacer` a THIRD caller, which is the thing that confirms
    the earlier generalisation: a chamber arrives already shaped as a `RecessTarget` and needed
    no new machinery. Salt block 12500.
- **Alcove styles** (`alcoveStyles`: one PropSet per `AlcoveKind`). Alcove cells are typed
  Hallway, so they inherit hallway WALLS/floors/ceilings — only the contents are per-kind.
  A kind with no entry generates as an empty recess, degrading gracefully like every other
  slot here. Per-kind kit walls/floors are a deliberate v2: skipping them kept
  `DungeonKitPlacer` untouched.

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
- **`minRoomHeightCells` / `avoidEntranceCell`** — two requirements that apply to EVERY anchor,
  so they are registered ABOVE the switch in `PropSetEntryDrawer` (a new anchor cannot forget
  them) and checked in the room, hallway and recess placers alike. Written for hanging props:
  a chandelier authored to drop convincingly needs headroom, and `minRoomHeightCells` excludes
  corridors and recesses for free, which is usually what you want. `avoidEntranceCell` keeps a
  ceiling prop out of the one tile you must walk through — a caged skeleton swinging in a prison
  doorway is the case it exists for. **Authorable rather than automatic on purpose**: a banner
  or sign hung OVER an entrance is a legitimate thing to want. In rooms `minRoomHeightCells`
  overlaps `preferredZones` (a ceiling cell inherits its floor column's zone) but the recesses
  have no zones at all, which is why it is its own field rather than a zone flag.
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

**Recesses — alcoves AND prison cells (`RecessPropPlacer`)** — one pass, because a prison
and an alcove are the SAME generator primitive: both come out of `RecessFits`, both are
validated dead-end pockets hanging off one hallway cell. They differ only in the CellType
they commit and in what contents they take. Contents arrive as a `System.Func<float,
PropSet>` on `RecessTarget` so the shared pass never learns what an `AlcoveKind` is.
Its own pass because neither existing one fits: `RoomPropPlacer` hard-gates on
`CellType.Room` and needs zones/entrance/centroid; `HallwayPropPlacer` scatters ONE global
set over every corridor cell, where a recess needs per-kind content and a hero prop.
Everything else is reused unchanged (`PropSet`, anchors, `PropTier`, `PropInstancer`,
`PropSnap`, `WallFaceRegistry`).
- **The generalisation was cheap because the three placement helpers already used nothing
  but `Cells`, `Direction` and `MouthCell`** — which is exactly `RecessTarget`. Worth
  noticing when a second caller for any pass appears: if the helpers only touch a handful
  of fields, the "type" they take is already an interface waiting to be named.
- **`PrisonSpec` replaced `List<BoundsInt> PrisonCells`.** A bounding box cannot support a
  `Feature` prop, which needs the recess's `Direction` frame — without it a bunk lands on a
  random wall and the cell reads as scattered junk rather than a place someone was kept.
  Every field it carries was already computed by `RecessFits` and thrown away. Two
  consumers were also quietly WRONG on a WIDE prison, whose footprint is a 1x1 vestibule
  plus a pocket behind rather than its bbox: the capped-wall reservation rescanned the bbox
  for Prison cells, and `PrisonDoorMarker` resolved its index by bbox containment.
- **WEIGHTED contents pools** (`RoomStyle.prisonProps`, `AlcoveStyleEntry.variants`) are
  what stop every statue nook in a run looking identical — the per-cell hash varies WHERE
  things land, never WHAT is there. The existing single `props` slot survives as a weight-1
  pool entry, so authored data keeps working and variants EXTEND the pool. **The variant
  roll is drawn UNCONDITIONALLY once per recess**: rolling it only when a pool has variants
  would make the draw count depend on authoring, so adding a variant to one kind would
  reshuffle every recess after it.
- **`RecessTarget.NoBlocking` reserves a prison's MOUTH.** The no-flood-fill exemption is
  narrower than it looks — a dead end cannot SEVER the dungeon, but it can SEAL ITSELF, and
  a crate behind the bars is indistinguishable from a generation bug. Reserving the mouth
  is what leaves the flood-fill genuinely nothing to check. Décor is still allowed there;
  only collider tiers are refused. In `PlaceScatterLike` the filter runs BEFORE the hash
  sort, so a refused cell doesn't consume a chance roll and silently thin the density.
- **Runs BEFORE TorchPlacer** — §8's most-constrained-first rule taken to its conclusion:
  an alcove has ~3 wall faces and one authored hero prop, making it the tightest consumer
  of wall real estate in the dungeon, and it must claim its feature face before a sconce
  takes it. That needed one line in `TorchPlacer` (respect an already-CLAIMED face).
  Nothing claimed before torches previously, so it was a behavioural no-op — which is what
  made it verifiable: identical torch counts on fixed seeds with alcoves off.
- **`HallwayPropPlacer` excludes alcove cells** and reserves the corridor tile in front of
  each mouth. Alcove cells are typed Hallway, so without this they collect generic debris
  on top of their authored contents — burying the thing the alcove exists to show — and
  rubble piles exactly where you stand to look in.
- **No flood-fill, deliberately.** A dead-end pocket off one corridor cell cannot sever
  anything. Do NOT add alcove cells to the hallway BFS "for safety" — it would let a
  blocking prop veto itself for nothing.
- **A salt BLOCK PER CALLER**, never a shared counter: alcoves 12100+, prisons 12300+,
  same offsets within each (feature/wall/scatter/ceiling/variant). Tuning prisons must not
  reshuffle every alcove. Hallway is 12002/12003/12005, rooms 110xx.
- `snapToInsideCorner` pays off unusually well here: a recess is nearly all inside corners.

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

**KIT sockets (`KitSocketPlacer`)** — the same `PropSocket` component authored on
WALL / CEILING / FLOOR kit pieces instead of on props. What it buys over the prop
system is an **exact authored position on a specific piece**: the placers choose
positions by zone, chance and spacing, which can never say "on the mantel of this
fireplace" or "in the niche of this recess". A wall with a recess gets candles IN
the recess; a fireplace wall gets its fire where the hearth is.
- **Record-then-consume, not spawn-inline.** `DungeonKitPlacer` records a
  `SocketSite` and a later pass fills it, for two reasons: `place` cannot express
  `PropTier` (a fireplace VFX must be FullGameObject, a candle StaticDecor), and one
  pass then serves both the PrefabKit and InstancedKit paths. Same shape as
  `WallFaceRegistry.FeatureFaces` feeding NearWallAsset. Recording lives INSIDE
  `Emit`, covering all 13 of its call sites at once — plus the reserved capped-asset
  path separately, which calls `place` directly and is exactly where feature walls
  land (§7's standing warning about that path, now with a second consumer).
- **RUNS BEFORE `TorchPlacer`.** `countsAsTorch` claims the face AND seeds
  TorchPlacer's spacing buckets before thinning. Claiming alone is not enough:
  it stops a torch on that exact face while nothing prevents one on the next cell
  along, so an authored sconce gains a computed twin a metre away. Seeding is what
  makes authored torches DISPLACE computed ones rather than add to them, so a room's
  brightness still matches its palette. Same most-constrained-first logic as alcoves.
- **`tintToRoomPalette` resolves by what the child IS**, not by a second authoring
  flag: a `Light` gets tinted with its flame VFX; an emissive material gets the
  room's cached variant swapped in — which is how a candle glows per-room with **no
  Light at all**, and at StaticDecor **no GameObject either**. That is the answer to
  "can I add variety without a framerate cliff": bounded by one extra batch per
  palette colour, not by candle count. Default OFF (most sockets hold shields,
  banners and rubble, where tinting would be wrong).
  **The tinted `Light` and the tinted flame VFX take DIFFERENT FORMS of the palette** —
  hue for the light, raw HDR for the flame — see §7. Feeding one to both is what would
  drop a socketed candle's fire below the bloom threshold.
- **`PropSocket.tintIntensity`** is the socket counterpart to a `PropEntry`'s, and exists
  for the same reason: one emissive material and one room palette should serve a dim shelf
  candle AND a blazing brazier. Without it every socketed glow in the dungeon shared
  `kit.emissiveIntensity`. 0 = inherit the kit's (§7's not-authored convention).
- **`tintMaterial` is the CHILD'S own glow material**, not the kit's. A candle's
  wax-and-flame material is not the walls' emissive material, and `AddInstance`
  replaces only the exact material handed to it — keying the swap off
  `kit.emissiveMaterial` meant it matched nothing and every socket child kept its
  authored colour. Falls back to the kit's, which is right only for a child that
  literally shares it.
- **FLOOR sockets may not block.** Sockets spawn outside `RoomPropPlacer`'s occupancy
  system, so nothing flood-fills after them and a blocking child on a floor tile could
  sit in a doorway or pinch a room in two with nothing to catch it. Demoted to
  StaticDecor with a warning rather than skipped, so the piece still appears. Walls
  and ceilings are structurally incapable of this, which is why the guard is
  floors-only.
- Own salt **12201**. Root `DungeonKitSockets`, listed in `GeneratedRoots` (§5).
- Trap shared with props: the pose composes from the piece's **actually rendered**
  pose, `globalVisualOffset` included — without it every child lands a half-cell low
  (golden rule 2, the shape that once floated scatter props in the air).

**Chests:** author as a `Feature`, guaranteed ×1, `StaticCollider` entry in
Treasury/ChestVault sets. Inert now; interactive later = tier change only.

**Carryable props** (see §10 for the carry rig): a prop the player can pick up
and throw MUST be authored `PropTier.FullGameObject`, not an instanced tier. The
instanced tiers bake the MESH into a static matrix and give the prop only a
collider GameObject, so lifting a `StaticCollider` barrel would carry the collider
away while the visible mesh stayed welded to the floor. Carryables are low-count
by nature, so the batching loss is irrelevant. Barrel/crate/skull-style props get
`Rigidbody` + `Carryable` (+ optional `PushableProp`, `ImpactAudio`).

**Destructible props (`DestructibleProp` + `DebrisCleanup`)** — crates and barrels
that break apart, built with loot drops in mind (the hook is the destruction event;
nothing spawns yet). Same `FullGameObject` requirement as carryables, and for a
stronger reason: `InstancedDungeonRenderer.Commit()` is additive with **NO REMOVAL
PATH**, so an instanced mesh literally cannot be un-drawn when the prop dies.
- Damage arrives through `ImpactAudio.OnImpact` rather than a second collision
  handler, so the sound and the damage can never disagree about what counts as a
  hit. **The retrigger gate `return`ed BEFORE firing `OnImpact`** (real bug), so a
  suppressed impact dealt NO DAMAGE — a barrel destroyed by a throw was silent
  because the destroying blow was the one being suppressed as a re-contact.
  `alwaysAudibleForce` (0.7) exempts genuinely hard hits from suppression.
- Fracture chunks need `EnsureConvexColliders`: a concave MeshCollider on a dynamic
  Rigidbody is illegal in PhysX and Unity only complains at runtime.
- Cleanup SCALES the chunks down rather than sinking them, and each shrinks about its
  own **`VisualCentre`** (from renderer bounds), NOT its transform origin — the FBX
  fracture export left every chunk's transform at the shared object origin, so scaling
  about the pivot collapsed the whole pile toward one point instead of each piece
  shrinking in place. `RetireAfterAudio`/`DestroyWhenQuiet` hold the object alive
  until its impact sound finishes so cleanup can't cut the sound off.
- **Debris inherits motion from TWO sources, because neither covers both cases.**
  (1) The prop's own velocity, for something already moving — but read from a
  **`FixedUpdate` cache, never live** (see the post-bounce rule below). (2) The KILLING
  BLOW (`inheritBlowSpeed`, along `DamageInfo.direction`), for a prop standing STILL that
  a hit destroys outright. Case 2 cannot be solved by ordering: **every `AddForce`
  overload QUEUES until the next physics step**, so a shove applied earlier in the same
  frame is invisible to a break happening later in it, and the prop is destroyed before
  that step ever runs. The blow term FADES OUT as the prop's own speed rises
  (`inheritBlowFadeSpeed`) because the two are ALTERNATIVES, not addends — and because for
  an IMPACT break `direction` points at the surface just struck, so adding it to a barrel
  already flying into a wall would kick the debris back into the wall. Chunks get the
  rigid-body POINT velocity **`v + ω × r`**, not a flat copy of `v`: a flat copy gives
  every chunk identical motion so the debris drifts as a loose clump, while the spin term
  throws outer chunks wider than those near the axis, so a tumbling prop comes apart along
  its spin. `ForceMode.VelocityChange`, since every part of a rigid body genuinely shares
  one velocity.
- **RECURRING TRAP (three times now) — VELOCITY READ IN A COLLISION CALLBACK IS
  POST-BOUNCE.** PhysX resolves the impact BEFORE `OnCollisionEnter` runs, so
  `body.linearVelocity` (and position) read there describe the state AFTER the bounce:
  pointing back the way the body came, and much smaller. Bitten `Arrow` (stuck at wild
  angles until `lastFlightDir` was cached each `FixedUpdate`) and `DestructibleProp`
  (debris would have been flung backwards out of the wall it hit — and note the break path
  runs from `ImpactAudio.OnImpact`, which is driven by `OnCollisionEnter`, so it inherits
  the trap indirectly). **The fix is always the same: cache the value each `FixedUpdate`
  and use the cached one.** If you need a body's velocity at the moment of an impact, you
  cannot ask for it after the impact.

**Props spawn KINEMATIC and wake on first contact (`PropPhysicsSleep`)** — a push, a
pickup, damage, or the death of whatever they were resting on.
- **NOT a CPU optimization, mostly.** Unity already auto-sleeps settled rigidbodies, so
  the steady state was never expensive — what cost was the SETTLE, and the re-settle every
  time a fight jostled the room. A fresh dungeon dropped and settled a hundred props at
  once, and `ImpactAudio`'s retrigger gate is PER SOURCE, so a hundred props each settling
  ONCE sails straight past it (§10b). The real win beyond audio is that props stay exactly
  where the generator put them instead of drifting, sinking or squeezing through geometry
  over a long run.
- **`Wake()` WAS UNREACHABLE FROM A PUSH** (real field bug — "pushing props doesn't wake
  them"). `CharacterControllerPhysicsPush` early-returns on `isKinematic` before
  dispatching `IPushable`, and a sleeping prop IS kinematic, so the filter discarded the
  very contact meant to wake it. The wake there is deliberately NARROW — only a
  `PropPhysicsSleep` wakes. `PhysicsDoor` also goes kinematic on purpose (the standoff
  jam, §10), and waking that would undo the one thing stopping a door shoved from both
  sides launching the pusher through it.
- **`WakeNeighbours` MUST MEASURE FROM COLLIDER BOUNDS, NOT A SPHERE AT THE PIVOT** (real
  field bug — bowls left hovering after their table was destroyed). A table's pivot is at
  its BASE, so any sphere small enough to be safe never reaches the tabletop where the
  props actually are. Now an `OverlapBox` over the prop's bounds with a small margin: a
  contact test, not a blast radius — a large one wakes a whole room from one nudge. Hooked
  to `Health.OnDied` as well as `OnDamaged`, because it is the DEATH that removes the
  support and `Wake()` short-circuits on an already-awake table.
- **THE TRADE: a kinematic body does not fall.** Physics had quietly been correcting
  placement all along, dropping anything spawned slightly high onto the floor.
  `warnIfHovering` reports it rather than leaving it to be noticed visually — these are
  PRE-EXISTING authoring errors that settling was hiding, not new bugs, so treat the
  warnings as a backlog rather than a regression.

**Per-room prop TINT (`PropTint`)** — the prop-side counterpart to the kit's emissive
tinting (§5/§7), so a candle on a shrine shelf burns the same cold blue as its torches
instead of its authored orange. ONE resolver shared by the room, hallway and recess
placers: they differ only in whether a `Room` is in play, and a corridor prop resolving
to a different colour than the kit shell around it is precisely the mismatch this
prevents. Corridors and recesses pass a null Room and take `defaultTorchColor`, the same
fallback the kit uses for a cell in no room.
- **Opt-in PER ENTRY, not per material** (`tintToRoomPalette` / `tintMaterial` /
  `tintIntensity`): the same emissive material is often reused on something that must NOT
  shift hue — a lantern with coloured glass, a rune whose colour is its meaning.
- **`tintMaterial` is the CHILD's own material.** A socket child is a different prefab
  with a different material from its parent, and `AddInstance` replaces only the exact
  material handed to it — keying the swap off `kit.emissiveMaterial` matched nothing and
  every socket child silently kept its authored colour.
- Works on `StaticDecor`, which is the point: a glowing candle with no Light and no
  GameObject, costing one batch per palette colour rather than one per prop.

**Inspector UX:** PropSet entries and RoomStyle's nested lists have custom
drawers (`Assets/Scripts/Editor/`) — summary foldout labels instead of "Element N",
and PropSet entries show only the fields their anchor uses.
**A CUSTOM `PropertyDrawer` DOES NOT RUN DECORATOR DRAWERS, so `[Header]` on a drawn type
is INERT.** `PropSetEntryDrawer` draws each field with `EditorGUI.PropertyField` on a
child property and sizes the block with `GetPropertyHeight`; neither includes decorators,
and had a header rendered anyway the height maths would have mislaid every field below it.
PropEntry's section markers are therefore plain comments, and the headings you actually
see come from the `§` tokens in `VisibleFields` — **which is also what decides WHICH
fields show for the selected anchor, so a new PropEntry field must be registered there or
it never appears in the inspector at all.**

**RoomStyle is grouped by SPACE, not by subsystem** (Rooms / Hallways / Alcoves / Prisons
/ Pits, each contiguous). Authoring happens one place at a time — "what does a prison look
like" — and the old subsystem grouping meant three trips to three sections, where it was
easy to fill in a prison's walls and forget its floor. **`DungeonKit` is grouped by ORIGIN
CONVENTION** for the same reason, and doing so exposed that there are TWO independent
conventions rather than one: whether `globalVisualOffset` applies, AND whether the
per-piece nudge is rotated into the piece's frame or world-space. A lintel is kit-frame
with a ROTATED nudge; a bridge is base-origin with a WORLD one. Field order is
presentation only — Unity serializes by NAME, so regrouping never loses authored data.

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
- **Alcove budget:** `alcoveMinDepth`, `alcoveBaseChance`/`alcoveChancePerDepth`/
  `alcoveMaxChance`, `alcoveMaxCount`, and an `AlcoveRule` list (kind, minDepth, weight,
  maxPerRun, widthRange, depthRange). Kind pick is ONE weighted draw against the legal
  kinds; `maxPerRun` is enforced by **rejecting after the draw**, never by skipping it, so
  the draw count can't depend on what was already placed.
- **WHEN A PROFILE IS ASSIGNED IT WINS OUTRIGHT, and the DungeonVisualizer's mirrored
  fields are dead.** Cost real debugging time on alcoves: `alcoveChance` was turned up on
  the visualizer with no effect whatsoever, because the profile — created before those
  fields existed, so reading as all-zero — was what the generator consulted. **A
  ScriptableObject asset that predates a new field does not get its C# initializer
  reliably**; check the asset in the inspector, not the code default. `PlaceAlcoves` now
  warns naming the exact cause rather than silently carving none.

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
  on F1), **current room** (`CurrentRoomLabel`, by `RoomType`, or "Hallway") — the
  same world-to-cell + `RoomAt` lookup `DungeonFogController` uses to pick a room's
  fog color, so the readout can never disagree with what the fog shows — and the
  **debug keys**: F1 = new dungeon at the same depth, **PgUp/PgDn =
  depth ±1 with the seed PINNED** (watch one seed grow/shrink with depth). Depth
  keys survive the scene reload via `DungeonVisualizer.PendingSeed`/`PendingDepth`
  statics, consumed in `Generate()` before the generator is built (depth drives
  room count + grid size); runtime-only, serialized inspector values untouched.
  NB: `OnGUI` must be a CLASS method — nested inside `Update()` as a local
  function it compiles clean and silently never runs (real bug). **Keep the
  overlay's printed control list in sync with actual bindings** — it's authored text,
  not derived from the input code, so nothing catches it going stale, and it has now
  drifted TWICE: first behind melee input (LMB/RMB/Q), then behind the 1/2 weapon swap
  and most of the F-keys. Now split into Controls and Debug sections (the F-keys were
  mixed in with movement), with `overlayFontSize` (12, was a hardcoded 18) and the box
  sized from `style.CalcSize` rather than a fixed rect, so adding a line can no longer
  push the list outside its own background.
- **Gizmo layers (`DungeonVisualizer.gizmoLayers`)** — a `[Flags]` mask over what the scene view
  draws, because with rooms, corridors, alcoves, prisons, pits, bridges, stairs, sewer networks,
  chambers, manholes, three kinds of graph edge and a label on most of them, everything at once
  is unreadable. One multi-select dropdown rather than a row of bools, so "show me only sewers"
  is a single click. **LABELS is its own layer** — the floating text is the noisiest part and
  killing just that recovers most of the readability. Two layers cannot follow `CellType`, for
  the reason that keeps recurring: PITS are `CellType.Room` (so the pit test happens inside the
  Room case, or turning Rooms off takes chasms with it) and CHAMBERS are `CellType.Hallway` like
  alcoves (so corridors/alcoves/chambers are three registry lookups in one case).
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
  **`SetAttackPose` IS A LATCH, NOT A FRAME COMMAND.** It stores what it was last given and
  keeps it until something writes again — so any pose a system applies must be actively
  driven back to rest by that same system; nothing clears it for you. The SHIELD survives
  this because `TickShield` eases toward a persistent `shieldTargetPos` that defaults to
  zero, so it returns to rest on its own. The SWORD has no such loop on the ordinary path
  (`swordTarget*`/`TickSword` exist but are wired only into the BASH path) — it is written
  directly by `SetHandPoses` during a swing and zeroed once at `EndSwing`. So posing the
  sword directly for a block both POPPED (no ease-in) and then stuck there forever, because
  no idle code path writes the sword again. Any new hand pose needs its own smoothed layer
  written EVERY frame, standing down while the swing system owns the hand.
  **ADD IMPACT SHAKE AFTER THE SMOOTHING, NEVER INTO THE TARGET.** A target is what a lerp
  eases TOWARD, so an oscillation written there is damped into nothing by the very smoothing
  that makes the pose feel solid — you wind the amplitude up and up and still see almost no
  movement, with nothing obviously wrong. Same rule as HeadBob composing additively on the
  camera. (The shield's block jolt is directional, converted into viewmodel space via
  `InverseTransformDirection`; a symmetric wobble reads as a rumble effect, not an impact.)
  **`shieldCounterEuler`/`swordCounterPosition` are SCALES, not offsets** — they multiply
  the other hand's swing euler to derive counter-motion. Authoring a POSE into one (e.g.
  pasting block-pose eulers into `shieldCounterEuler`) multiplies the swing by tens, so the
  off-hand rotates wildly for the whole swing and only during swings. Field-reported and
  looked exactly like a code bug.
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
  **A CAMERA BUILT IN CODE GETS DEFAULTS, AND `renderPostProcessing` DEFAULTS TO FALSE** —
  so the global Volume graded the entire world and stopped dead at the weapon, which kept
  raw ungraded colours and read as a sticker pasted on the screen (no dimming in a dark
  corridor, no bloom on its emissives). URP runs a stack's post-processing ONCE at the end
  and this overlay is the last camera in the stack, so that one flag is what pulls the
  composited image through the pass. Nothing in the inspector shows it, because a code-built
  camera has no serialized row to look at — §12's "default whose failure is
  indistinguishable from correct wiring", now with a fifth instance.
  `volumeLayerMask`, `volumeTrigger` and the antialiasing pair are **copied from the base
  camera rather than left at their defaults**: a Volume only affects a camera whose mask
  includes its layer, so a project whose global Volume sits on any non-Default layer would
  have the overlay silently ignore it while the base obeyed, with both cameras looking
  correctly configured. Deriving from the base makes agreement structural instead of a
  coincidence of two settings.
  **DEPTH OF FIELD FIGHTS THE DEPTH CLEAR** and always will: the overlay clears depth then
  writes the weapon's own at ~0.5m, so a DoF focused across the room blurs the weapon hard.
  That is inherent to the technique — `postProcessViewmodel` off is the answer, not a fix.
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
  resolves in the fixed-rate physics step.) **INTENT, NOT ACHIEVED VELOCITY (real
  field bug):** the speed-scaling originally read `controller.velocity` — the speed
  actually achieved. But a door you're shouldering BLOCKS the controller, so achieved
  velocity collapses to ~0 exactly when you're leaning on it, and the push scale
  bottomed out at `minimumPushScale` — the door then got almost no torque. Fixed with
  `IMoveIntent` (`FirstPersonController.IntendedSpeed` = input × speed, reported BEFORE
  the world blocks it); the push scales by `Max(intent, achieved)`, so leaning on a
  stuck door still delivers real torque, and crouch still eases doors (lower INTENDED
  speed) rather than merely stalling. **But this is OBJECT-CHOSEN, not global** —
  `IPushable.PreferIntentPush` — because doors and heavy props want OPPOSITE things from
  the same stall: a door SHOULD yield to a lean (intent, stays strong → opens), a heavy
  prop (the wooden table) SHOULD resist and slow you (achieved, collapses → stubborn).
  Making intent global was a regression that made every heavy prop shove easily. Doors
  return true; props (and plain Rigidbodies, and NPCs without `IMoveIntent`) use achieved.
  **`Push()` is for CONTINUOUS CONTACT; a deliberate one-shot blow uses `PushBurst()`.**
  `Push` refuses outright once the prop is already moving at `maxPushSpeed`, which is
  essential for contact — that fires every frame you lean on something and would
  otherwise accelerate it without limit. A one-shot blow (the shield bash's cone shove)
  fires ONCE per attack thanks to sweep dedupe, so the cap protects nothing and instead
  silently EATS the blow: the capsule's contact push has usually already pushed the prop
  past the cap by the time the attack's cone resolves, so the bash appeared to do nothing
  EXCEPT on the rare occasions the cone reached the prop before the player did. That
  intermittency is the tell. `PushBurst` defaults to `Push`, so an implementer that
  doesn't care (a door — hinge torque has its own clamps) needs no change.
  **`Health.TakeDamage` does NOT act on `DamageInfo.impulse` — only `NpcHitReactions`
  does** (it subscribes to `OnDamaged`). So knockback set on a DamageInfo aimed at
  anything WITHOUT that component is silently discarded. A destructible prop is exactly
  that case: a barrel carries `Health` (for `DestructibleProp`) and so reads as
  `IDamageable`, but nothing on it consumes the impulse. Cost real debugging time — the
  symptom is "I set impulse and it didn't move", with no error anywhere. Props must be
  shoved through `IPushable`, separately from being damaged.
  **KNOCKBACK IS A VELOCITY; A PROP SHOVE IS AN IMPULSE — different QUANTITIES, never
  share the number.** `MeleeAttack.knockback` becomes `NpcLocomotion.AddImpulse(blow *
  knockback)`, i.e. m/s. `IPushable.Push`'s `force` is an impulse in N·s that
  `PushableProp` applies with `ForceMode.Impulse`, so Δv = J/m. That's the whole mass
  story and why it needs no per-prop tuning (same philosophy as `ThrownDamage`'s
  momentum-derived knockback) — but it also means feeding one number to both presents as
  "props barely twitch" or "props explode" purely as a function of their mass, which
  reads as a tuning problem rather than a category error. Size a prop impulse as
  **mass × desired launch speed** (the barrel is 45kg, so useful values are in the
  HUNDREDS), and note it must also exceed `mass × the player's dash speed` or the prop
  cannot outrun the lunge and you simply keep colliding with it.
  **Aim a shove through the CENTRE-OF-MASS HEIGHT.** `PushableProp` defaults to
  `AddForceAtPosition`, and a melee/cone origin sits at EYE height while props are low —
  so `Collider.ClosestPoint` lands near a barrel's top rim and the lever arm turns the
  shove into mostly TORQUE. The barrel tips over in place while still making a contact
  noise, which reads as "the push did nothing". Keep the lateral offset (a shoved barrel
  should tumble); remove only the vertical one.
- **DEPENETRATION LAUNCH (real field bug — THE "door flies open with zero push"):** a
  dynamic door (mass 20) ejects from an overlapping collider at up to
  `Rigidbody.maxDepenetrationVelocity` — Unity's default ~10 m/s. The player's
  CharacterController sinking into the door then FLINGS it open (and off its hinge)
  purely by depenetration, INDEPENDENT of push torque — so it flew open even with
  `pushForce` at 0.0001. Diagnosis tell: `debugPush` showed torque ~0.06 (nothing) yet
  the `worldAxis` tilted off vertical (toppling). Fix: `PhysicsDoor` clamps
  `maxDepenetrationVelocity` LOW (**0.1** for these kit doors — their mesh is tiny ×
  large scale, so the world-space collider is genuinely thin and penetrates easily).
  Now the capsule can't out-push the separation: the door RESISTS and slows you, and
  only the controlled hinge torque (scaled by intent, above) opens it — the "force it
  open" feel. A thick collider had masked this by keeping penetration shallow; thinning
  the collider to fight NPC-head clipping exposed it (wrong lever — see the door prefab).
  **This is a PROJECT-WIDE issue, not just doors** (real field bug #2): the CharacterController
  is treated as effectively infinite mass, so a stalled capsule sinking into ANY dynamic
  Rigidbody launches it by raw depenetration — independent of `Push()`/impulse entirely.
  A heavy prop (a table) shoved easily at FULL RUN SPEED with `Rigidbody.mass` at 45000,
  `PushableProp.pushMultiplier`/`maxPushSpeed` at 0.001, and even the whole component
  DISABLED — none of it mattered because none of it is in the path actually moving the
  table. Fixed at TWO levels: the project default
  (`ProjectSettings > Physics > Default Max Depenetration Velocity`, dropped 10 → 1) so
  every prop is sane out of the box, and `PushableProp.maxDepenetrationVelocity`
  (default 0.5) as a per-prop override for anything needing its own value. **Any new
  pushable/collidable dynamic body should get its own explicit override if the project
  default ever changes** — don't rely on inheriting it silently.
- **PhysicsDoor + PhysicsDoorAudio** — a door you push open by walking into it.
  Contact → **pure torque about the hinge axis** (never `AddForceAtPosition`,
  which injects linear velocity the joint fights and tears the door off its
  hinge); `ForceMode.Impulse` so mass/leverage feel real; **angular** speed
  clamped (`maxSwingSpeed`) — a hinged door's LINEAR velocity is ~0, so a linear
  clamp never fires and impulses compound. Angle comes from the transform
  (`CurrentAngle`), NOT `HingeJoint.angle` (returns 0 and NaN, which poisoned the
  logic). **`RigidbodyConstraints` freeze the two rotation axes PERPENDICULAR to the
  hinge** (`FreezeRotationX | FreezeRotationY`, since `hinge.axis` = local Z), leaving
  the hinge free to swing but rigidly forbidding the off-axis TOPPLE a depenetration
  spike causes. NEVER freeze the hinge's own axis — that welds the door (the original
  bug froze a pair that included it). Confirmed via `debugPush`: `constraints=48`,
  `worldAxis` stays `(0,1,0)`, door swings freely. If a door is authored with a
  different `hinge.axis`, change the frozen pair to the two perpendicular to it.
  **One-way-per-swing:** opens either way,
  but once past `commitAngle` the opposite limit snaps to 0 so it can't pass through
  closed — hits a hard stop and thunks like a real frame; full range restored once
  settled. `thunkArmAngle` gates the closing thunk (a shoving match jittering around
  0 stays silent). Audio is state/event-driven: a LOOPING creak whose volume/pitch
  track live swing speed + one-shot thunk/slam (`OnClosed`/`OnSlamOpen`, carrying
  impact speed → volume). Impacts and creak use SEPARATE reference speeds (the door
  hits the closed stop far slower than it peaks). Kinematic toggle = a locked door
  (future). **Self-closing spring vs. an active push — competing solver terms are
  the real enemy:** the spring, push torque, and depenetration correction fighting
  simultaneously under a limited solver iteration count is what actually pops/tunnels
  a hinge, so `Push()` suppresses `hinge.useSpring` for `pushSpringSuppressTime`
  after every push (spring resumes once the suppression window elapses with no new
  push) — confirmed by the user's own test that disabling the spring entirely made
  doors far more stable. Two more hardening passes worth knowing the REASON for, not
  just the values: an unconditional per-`FixedUpdate` clamp on both
  `doorBody.angularVelocity` (`maxSwingSpeed`) and `.linearVelocity` (`maxLinearSpeed`)
  — the existing in-`Push()` check only prevented ADDING more torque, it never
  clamped velocity arriving from elsewhere (e.g. depenetration); and
  `limitContactDistance` (a soft cushion before the joint's hard limit, was
  hardcoded 0). Both came from reviewing an external AI-written troubleshooting
  doc against the actual code — most of its "core stability" advice was already
  implemented (often exceeding its suggested values), these two were the genuinely
  new low-risk items; its `Enable Preprocessing: On` suggestion was deliberately
  NOT taken (disagrees with our tested `false`, and community consensus on that
  flag is split) — a lesson in verifying AI-sourced advice against ground truth
  instead of applying it wholesale.
  **STANDOFF JAM — a door shoved from BOTH sides goes briefly kinematic** (real
  field bug): with a player one side and NPCs the other, the pusher could WARP
  THROUGH to the far side, or wedge between door and frame and get driven UP into
  the ceiling. `maxDepenetrationVelocity` is deliberately 0.1 m/s so the door can't
  launch — but capsules press in at 3.5–8 m/s (NPC agent → player sprint), so the
  door loses that race by 35–80× and penetration accumulates without bound;
  `CharacterController.Move` then resolves the overlap to the NEAREST exit, which by
  that point is the FAR SIDE. Wedged against the frame the only free direction is up,
  hence the climb. **Collider thickness only buys time here, it cannot win the race**
  — the deciding number is the velocity ratio, not the thickness. `Push()` already
  computed a SIGNED torque about the hinge axis, so which side a push came from was
  free: opposite signs inside `opposingPushWindow` → `Jam()` → kinematic for
  `jamHoldTime`, which sidesteps depenetration entirely (a CharacterController treats
  a kinematic body as world geometry). It's also just physically true. Two guards,
  both silent failures otherwise: **never jam an already-kinematic door** (that's the
  planned LOCKED state — `Unjam()` would turn a locked door dynamic), and `OnDisable`
  releases so a door can't be left welded shut.
  **`maxDepenetrationVelocity` 0.1 is CONFIRMED GOOD** and should stay: it was
  originally chosen for a thin collider, and it was re-examined once the collider got
  thicker (the reasoning above suggested raising it toward 0.5–1.0) — but 0.1 tested
  better in play. Don't "fix" it upward on the thickness argument alone.
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
- **THE THROW IS A HEAVE, NOT AN EVENT** — press, wind up, then launch. **The viewmodel is
  STOWED while carrying, so there are no arms to animate**: the weight can only be told by
  the CAMERA and by the PROP, which is the constraint that shapes the whole thing. Every
  term scales off `CarryLoad01`, so a prop's `Rigidbody.mass` still moves its whole
  heaviness together.
  - **The prop animates itself, nearly free.** The carry rig already force-drives the body
    toward `HoldPoint()` under the `maxCarryForce` clamp, so pulling that point back and
    down during the wind-up makes a heavy prop LAG into the coil while a light one snaps to
    it — one offset, two weights, nothing authored per prop.
  - **`CameraKick.SetSustained` exists because the spring cannot hold a pose.** An impulse
    system starts returning the instant it fires, so "lean back and hold it while the throw
    loads" comes out as a twitch no matter how large the impulse — raising the numbers makes
    a bigger twitch, not a longer lean. The held layer carries its own LARGER caps, which is
    defensible because **it is angular VELOCITY that nauseates, not angle**: a slow
    deliberate lean tolerates far more than a fast jolt of the same size.
  - **`SetSustained` is FRAME-STAMPED, NOT LATCHED** — stop calling and it eases home.
    `ViewmodelSway.SetAttackPose` IS a latch and that is exactly how a blocked sword stuck
    mid-pose (see it in §10), so a throw interrupted by the prop being destroyed or the
    player dying leaves the camera upright with no explicit clear anywhere.
  - **`Release()` clears `IsWindingUp`**, because it is the one choke point for "no longer
    holding anything". Clearing it only at launch would let a prop destroyed mid-heave leave
    the flag set, `Update` returning early forever, and pickups silently dead.
  - **FOV IS THE CHEAP DRAMA AND A CAMERA DOLLY IS THE EXPENSIVE KIND.** A large backward
    camera offset leaves the player's capsule and can end up inside the wall behind them —
    trivially reproduced by winding up with your back to a wall — whereas widening the FOV
    costs nothing in clipping risk. Practical ceiling on the dolly is ~0.25–0.4m; past that
    it needs a backward clearance clamp of the `ViewmodelCollision` shape. NB the lean also
    compounds into the prop, since `HoldPoint()` is measured from `cam.position`.
  - **The grunt belongs to the DRIVE, not the coil.** It was briefly moved to the wind-up on
    the reasoning that the exertion IS the heave; wrong — you brace quietly through a
    wind-up and vocalise on the exertion that follows, and played early it covers the
    loading phase while the throw itself lands silent.
  - The release step uses `FirstPersonController.AddImpulse`, the same decaying external
    velocity the shield bash lunges with, so it folds into the one `cc.Move` and cannot push
    the player through a wall. **Forward, not recoil**: physically a heave shoves you back,
    but from inside the head that reads as being pushed rather than as throwing.
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
- **HangingCageAudio** — the squeak/creak counterpart to `PhysicsDoorAudio` for a
  hanging, chain-hinged prop (cage, chandelier): a LOOPING creak whose volume/pitch
  track live swing speed, silent at rest. Unlike a `PhysicsDoor` there's no existing
  component authoring a `SwingSpeed`, so it reads the Rigidbody's own
  `angularVelocity` directly — put it on the OUTERMOST body of the chain (the cage,
  not a link): a hinge-chained Rigidbody's angular velocity is measured in WORLD
  space, so each link's swing adds onto the one above it, and the cage's own
  angularVelocity already reflects the combined motion of every link between it and
  the ceiling anchor with no need to sample the chain. Pairs with the existing
  `ImpactAudio` on the same body for a metallic clang on player contact — same
  continuous-vs-one-shot split `PhysicsDoorAudio` makes between its creak and thunk.
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
  **EXTERNAL SYSTEMS MOVE AN NPC THROUGH A DECLARED CHANNEL, NEVER THE TRANSFORM.**
  `RejectUnwantedPush` clamps any horizontal displacement beyond
  `(want + sep) * dt + (impulse + plow) * udt` straight back out — that's what stops the
  player shoving NPCs through walls, and it CANNOT tell a deliberate drive from an
  accidental capsule shove unless the drive declares itself. Two channels exist and the
  distinction is real: **`AddImpulse` is a one-shot that DECAYS** (which is what makes a
  hit read as a hit), while **`SetPlowVelocity` is SUSTAINED** — the shield bash's
  windshield carry, where a captured NPC is driven along in front of the charging player.
  Faking the sustained case by re-triggering `AddImpulse` every frame would silently reset
  `impulseDecay` forever and read as a bug to whoever found it. While plowed, the NPC's own
  `want` is ZEROED (its approach would otherwise crawl against the carry — same principle
  as `facingLockedFrame`: while an external system owns the motion, the local one yields),
  and `IsBlocked` is measured against the PLOW rather than `want`, because the bash needs
  it to spot a body pinned against geometry and let go — the player passes through NPCs, so
  a pinned one would otherwise end up BEHIND the shield. **The plow expires on a TIMEOUT,
  not an explicit clear**, because the driver can vanish mid-carry (a loadout swap disables
  PlayerMelee, the player dies, the dungeon regenerates) and a latched flag would leave a
  body driven forever.
  **PERF — separation was 58% of the whole component** (profiled at 100 NPCs:
  `Update()` 24.11ms, of which ~14ms was `Separation()`). The cost was NOT the O(n²)
  arithmetic — it was reading `other.transform.position` inside the inner loop, a
  managed→native transition per access, ~30k per frame. Fixed by snapshotting every
  NPC position into a plain array once per frame, hoisting the self read, comparing
  SQUARED distances (sqrt only for real neighbours), and using `ReferenceEquals`
  instead of Unity's overloaded `==` (which runs a native destroyed-check). 24.11ms
  → 11.91ms; separation ~14ms → ~1.7ms. Confirmed by zeroing `separationStrength`
  and re-profiling before optimizing — interrogate the metric first.
  **The snapshot REQUIRED `escapeDir`** (don't remove it as redundant): the old live
  reads broke up a stacked crowd by ACCIDENT, because NPC #50 read #0's position
  after #0 had already moved that frame — update order supplied the asymmetry. A
  consistent snapshot removes that accident, so a symmetric pile stays symmetric
  forever, and `transform.right` as the degenerate-overlap escape sent every
  coincident goblin the same way: they roamed as ONE body and appeared to "divide
  into many" when alerted. A stable per-NPC random bearing breaks the tie explicitly.
  **`FaceTowards` OWNS the facing for its frame** (`facingLockedFrame`), and
  `FaceMovement` stands down. `NpcBrain` calls `FaceTowards(target)` every frame while
  alerted while locomotion steered toward travel direction — two systems rotating one
  transform, pulling 180° apart while RETREATING, with the winner decided by undefined
  script execution order. Because `LocalVelocity` is measured in the body's own frame,
  that spin alone flipped the Animator's `VelocityZ` sign and flickered the blend tree
  (see `NpcAnimatorDriver`). Frame-STAMPED, not a bool, so the lock holds whichever
  component runs first. Same rule as `ViewmodelCollision`: one system owns a transform.
  Remaining cost at 100 NPCs (~10ms) is `Controller.Move()` plus agent syncs — native,
  and ~2.5ms at the real target of ~25 active NPCs, so left alone deliberately.
- **NPC brain (`NpcBrain`)** — decisions only. Wander → Investigate → Alerted FSM
  as a **priority interrupt** (sight beats sound beats wandering, re-evaluated each
  frame). Every state delegates to capability components and never touches the agent
  or controller directly. That shape is deliberate: a Unity Behavior tree swapped in
  later calls the identical capability API, so this FSM doubles as the integration
  test proving that API is complete. **Determinism boundary (deliberate, do not
  "fix"):** generation is deterministic, runtime AI is NOT — where an NPC spawns
  reproduces from (seed, depth), but what it decides once alive uses `UnityEngine.Random`,
  because reproducing a fight would need deterministic physics and input replay.
  **HYSTERESIS on every distance band, or a crowd flaps** (`approachHysteresis`, a
  MULTIPLIER so it scales if `engageDistance` is retuned). Bare thresholds meant boids
  separation nudging an NPC a centimetre past `engageDistance` repathed it inward, which
  separation undid, forever. A ring of attackers physically CANNOT all sit at
  `engageDistance` (circumference ÷ `separationRadius` caps how many fit), so the surplus
  oscillates continuously. Both boundaries now latch and release wider: at
  `engageDistance` 3 the bands are retreat < 2.0 until 2.5, hold 2.5–3.0, approach > 3.75.
  **INVESTIGATE needed the same treatment, for a sharper reason** (`investigateRadius`):
  every NPC investigating one noise paths to the IDENTICAL `LastKnownPosition`, and
  `HasArrived` is a POINT test — a group can never satisfy it together, because they'd
  all have to stand on one tile. Separation shoves them off, arrival flips false,
  everyone repaths inward, forever. The radius is investigate's `engageDistance`: it lets
  them settle into a RING around the spot, which is what separation was already trying to
  do and the brain kept undoing. `IsBlocked` still counts as arrived (wedged on a prop →
  look around rather than grind). **General rule: whenever a group shares one
  destination, the arrival test must be a BAND, not a point.**
  Also stop churning the agent — `Stop()` was calling `ResetPath()` EVERY frame while
  parked, approach recomputed a full path every frame, and retreat re-issued a destination
  every frame at the `tooCloseDistance` line.
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
  **KNOWN GAP — sight detection is BINARY AND INSTANT, whatever the doc above says.**
  `TickSight` sets `CurrentTarget` the moment the cone + LOS test passes, regardless of
  `Awareness01`, and `NpcBrain` interrupts straight to Alerted on `CurrentTarget != null`.
  So the meter only actually gates the HEARING → investigate path; for sight it's
  decorative. This is the likeliest reason goblins read as over-sensitive, and note that
  `sightGainPerSecond`/`decayPerSecond`/`investigateThreshold` **cannot fix it** — they
  shape the meter, and the meter isn't what triggers the hunt. Gating `CurrentTarget`
  behind an awareness threshold is the fix; left undone deliberately, as a design call.
  **`NpcPerceptionDebug`** (drop on any scene object) makes it visible: an awareness bar
  over every NPC — **F3** overlay, **F4** sight off, **F5** hearing off. The switches are
  STATIC (one keypress covers the whole dungeon; isolating a sense in a crowd is
  impossible otherwise) and reset on play-mode entry, same fast-enter-playmode trap
  `NoiseBus` guards. A bar near ZERO while the NPC reads SEES is the gap above, not an
  overlay artifact.
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
  **LOS MUST NOT COUNT THE TARGET AS ITS OWN OCCLUDER (real bug that cost real
  swings).** `IsLineBlocked` treated any collider between shoulder and hit point as
  blocking — including the target's own capsule, so a barrel occluded ITSELF and the
  swing was rejected as "no line of sight" while the reticle sat dead centre on it.
  Nudging the aim a few pixels made it hit, which is why it read as an aim problem for
  a long time. A hit only blocks if it **isn't the target** (plus `losSurfaceSkin` for
  the surface the ray starts against). Same family as ViewmodelCollision's
  `CheckSphere` lesson and the old flickering E-interaction sweep: at melee range you
  are already touching what you're testing against, so any naive cast is wrong.
  **`MeleeReticle`** (F6, `showReason`) exists because of this — `PreviewWouldHit(out
  Transform, out string)` runs the REAL `CanHit` path and reports its rejection reason
  live, so "did I miss or did the code refuse" is answerable instead of guessable.
  `TryHit` delegates to the same `CanHit`, so the reticle can't drift from the swing.
- **Player ranged attack (`PlayerLoadout`, `PlayerBow`, `Arrow`, `PlayerBowAudio`,
  `Hitbox`)** — `1` = melee+shield, `2` = bow; hold LMB to draw, release to fire.
  - **`PlayerLoadout` swaps by ENABLING only the active script**, not by spawning or
    destroying anything — each weapon component owns its own input and stands down when
    disabled, so adding a third weapon needs no dispatcher.
  - **The bow is ANIMATOR-driven, unlike the procedural sword** (`PlayerMelee` poses
    the viewmodel in code). Deliberate: a draw is a HELD pose parameterized by one
    value, which an Animator expresses natively via `Draw`/`DrawAmount`/`Release`.
    **`SetTrigger` ON A BOOL SETS IT TRUE AND NOTHING EVER CLEARS IT** (real bug — the
    draw and fire played once, then the machine parked in `Bow_fire` forever, because
    its only exit required `Release == false`). Only true Triggers auto-consume when a
    transition takes them. `PlayerBow` now DETECTS the parameter type and drives it
    accordingly (`SetTrigger`, or set-then-clear-next-frame for a Bool), and clears a
    stale `Release` when a draw starts — a trigger no transition consumed stays armed
    and would fire the instant a valid transition appeared. **Release SPEED is governed
    by the TRANSITION duration, not the state's Speed**: a 0.25s `m_HasFixedDuration`
    crossfade was the bottleneck, and raising state Speed made it worse (the clip
    finished inside the crossfade).
  - **Nocked arrow** is parented to the bow's `string` bone in the PREFAB, not from
    code — the bone may carry the non-uniform scale an FBX axis conversion leaves
    behind, and authoring it makes distortion visible in the editor. Visibility is
    DERIVED from state every frame (`SyncNockedArrow`), never toggled at transitions:
    `Update` has several early returns (carrying, not drawing, still holding) and an
    event-driven toggle would miss one and leave the mesh stuck on.
  - **`Arrow` sticking — four separate causes, all fixed, all worth knowing:**
    (a) rotation was never set on stick at all; (b) **`body.linearVelocity` inside
    `OnCollisionEnter` is POST-BOUNCE** — PhysX resolves the impact BEFORE the callback
    runs, so the impact velocity (and position) read there are already wrong; the flight
    direction is cached each `FixedUpdate` (`lastFlightDir`) and an explicit
    `LookRotation(dir)` applied on stick, with `freezeRotation` to stop in-flight
    tumbling; (c) the visible tip is not the pivot, so placement is
    `point + dir * (embedDepth - tipAhead)`; (d) **`CollisionDetectionMode.ContinuousDynamic`
    is mandatory above ~30 m/s** or the arrow tunnels and reports a hit point from the
    wrong side.
  - **Arrows FOLLOW their victim, they do NOT parent to it.** `SetParent(worldPositionStays)`
    can only preserve world rotation when the parent's scale is UNIFORM, and rig bones
    aren't (the goblin mesh sits at 175) — the resulting shear is not expressible as a
    local rotation, so arrows stuck to NPCs came out at wild angles.
    `followAnchor`/`followOffset`/`followRotation` in `LateUpdate` instead.
  - **`DamageType.Projectile` exists because `Thrown` redirects part of the shove
    UPWARD** (`NpcHitReactions.thrownVerticalPop`) — right for a hurled barrel where
    being lifted sells the blow, wrong for a puncture: goblins shot from above flew
    skyward. Appended to the enum so serialized indices are untouched.
  - **`Hitbox`** marks one collider as a weak point (head sphere on the goblin's bone;
    NPCs already carry dormant ragdoll bone colliders, so it's a valid target alive or
    dead). A component read through a static helper, exactly like `Surface.Of` — a
    damage source asks "what did I hit" without learning any anatomy, and a new
    creature declares its own weak points by authoring them (bone-NAME matching breaks
    on any rig rename; a layer would burn one of 32). **Lookup is `GetComponent`, NOT
    `GetComponentInParent`** — walking up would let a Hitbox on the NPC root apply to
    every collider beneath it, making every torso hit a headshot. Scales the damage
    AMOUNT only; knockback stays unscaled. **Melee is deliberately NOT wired to this** —
    a 2.5× head multiplier on the sword is a balance change to the combo/poise economy,
    not a bug fix.
  - **Aim FOV** (`drawFovZoom`) NARROWS the world FOV with the draw — an archer's focus
    tightening — deliberately the OPPOSITE sign to the shield bash's `bashFovBump`, which
    WIDENS: a lunge wants the world rushing at you, an aimed shot wants it pulled in, and
    if both widened the two would stop meaning different things. Tracks `Draw01`
    continuously (same reasoning as the draw creak below), and because the target is
    DERIVED from the live draw rather than set by draw/release events, every exit path eases
    it home — which is also why `TickDrawFov` must sit ABOVE `Update`'s early returns, the
    same placement rule as `SyncNockedArrow`. **WORLD camera only**: the viewmodel overlay
    keeps its own FOV so the bow itself never distorts (§10 ViewmodelCamera).
    **FOV IS NOW OWNED BY `PlayerFov` — nothing else may write `Camera.fieldOfView`.**
    This section used to carry a warning that bow and melee each cached their own `baseFov`
    LAZILY, safe only because `PlayerLoadout` guarantees exactly one is enabled, and that a
    THIRD consumer was the point to extract an owner. **The throw heave (§ PlayerCarry) was
    that third consumer and it broke the convention rather than stretching it: CARRYING DOES
    NOT DISABLE MELEE**, so both drive the FOV in the same frame.
    The failure that forced it is worth keeping, because it is silent and permanent: a
    lazily-captured base sampled while another effect is displacing the camera adopts the
    OFFSET as "normal", after which every effect measures from the wrong number and the FOV
    ratchets away run by run.
    `PlayerFov` captures the base ONCE and EAGERLY (so it can never be sampled mid-effect),
    takes **frame-stamped additive requests** (`AddOffset`, called every frame you want it),
    and eases home when nothing asks — which retired both the lazy capture AND the manual
    restore-on-disable in bow and melee. Offsets SUM, so no caller can silently stomp
    another's. Same contract as `CameraKick.SetSustained`, deliberately.
    **Resolve it through `PlayerFov.Ensure(this)`, never `GetComponentInParent`.** Awake
    order between sibling components is undefined, so whichever consumer ran first would
    cache a null — and **a missing FOV owner does not throw, the effect simply never
    happens**. That was found by raising a throw's FOV setting from 6 to 100 with no visible
    change whatsoever, because the component had never been added to the prefab and nothing
    said so. `Ensure` creates it on the `FirstPersonController`'s GameObject, so there is no
    prefab step to forget on this rig or the next one.
  - `PlayerBowAudio` follows the house continuous-vs-one-shot split (§ PhysicsDoorAudio):
    a LOOPING creak whose volume/pitch track live `Draw01` (so holding at full draw is
    audibly tense, not silent) + one-shots for nock/loose/let-down scaled by the draw
    they fired at. The loop is driven from `bow.IsDrawing` each frame rather than
    started/stopped by events, so every exit path (fired, let down, weapon swapped,
    picked up a barrel) fades it out without each needing to remember to; `OnDisable`
    hard-stops it so swapping to melee mid-draw can't leave a creak looping forever.
- **NPC MELEE IS ANIMATION-DRIVEN (`sweepFromAnimationEvent`).** `MeleeAttack` holds NO
  Animator reference at all — it decides WHEN a swing happens and knows nothing about how
  it looks, the same one-way rule the AI follows; `NpcAnimatorDriver` remains the single
  seam (`Attack`/`CancelAttack` triggers, fired from `OnSwingStart`/`OnSwingCancelled`).
  With the flag on, the hit comes from an **Animation Event calling `AnimationSweep`** on
  the clip's impact frame instead of the `windup` timer — `DoSweep()` was public and
  decoupled from `TryAttack()` for exactly this. **Why it matters beyond tidiness:** a
  fixed delay cannot match a per-weapon clip's impact frame, and a hit landing when the
  blade VISUALLY connects is what makes a parry feel fair — otherwise players learn an
  invisible timer rather than the animation, and every new weapon needs its windup
  hand-synced. Three guards, all protecting against silent failures:
  a **failsafe timeout** (a missing/misnamed event would leave `IsSwinging` true forever,
  so `CanAttack` never returns true again and the NPC silently stops fighting);
  `AnimationSweep` is **gated on `IsSwinging`** (a looping attack state would sweep
  repeatedly, and `DoSweep` CLEARS its dedupe set each call, so every repeat is a fresh
  full-damage hit); and `CancelAttack` exists because **cancelling in code cannot pull the
  Animator out of a state** — the event would still fire on schedule.
  **Animator authoring:** an UPPER-BODY masked override layer (an attack state on the base
  layer replaces the locomotion blend tree and the NPC freezes mid-stride to swing),
  resting in an **empty default state** — an override layer at weight 1 drives its masked
  bones whatever state it is in, so without an empty state the upper body is permanently
  posed by it. `Any State → Swing` on `Attack` with **Can Transition To Self OFF** (Any
  State includes the current state, and a restarted clip re-fires its event).
- **`LandSweep` MUST clear its state AFTER `DoSweep()`, never before (real bug).** A
  victim's guard mitigates inside `Health.TakeDamage`, which runs inside `DoSweep`, and it
  answers by halting the attacker's swing (`Blocked`/`Parried` → `CancelSwing`). With
  `IsSwinging` cleared up front, `CancelSwing` hit its own early-return and did nothing —
  the punish was silently inert and the attacker played out the rest of its swing as if it
  had connected. `LandSweep` now also SKIPS its normal resolution if something cancelled
  mid-sweep, so one swing can't report as both completed and abandoned.
- **TWO SWEEP BUGS THAT ONLY EXIST FROM THE NPC'S SIDE** (the code was written from the
  player's, see §12's perspective rule):
  - **Gather buffers were sized for the wrong attacker.** `overlapScratch`/`castScratch`
    were 16 — fine for a player whose 1.6m sweep holds one or two enemies. A goblin
    swinging from INSIDE a crowd is surrounded by neighbours at separation radius, each
    bringing a capsule plus ~15 dormant ragdoll bone colliders, and **`hitMask` is NOT
    auto-stripped of the NPC layer** (only `losBlockMask` is) — so those all get gathered,
    consume slots, and are only rejected by the faction check AFTERWARDS. Overflow
    truncates silently and the dropped collider can be the player: swings that land in
    duels and whiff only in crowds, with nothing in the log. Now 64 (the lesson
    `coneScratch` already carried), plus an ungated warning when the buffer fills.
  - **LOS excluded the victim but never the ATTACKER.** With an `aimSource` the origin is
    pushed clear by `aimForwardOffset`; an NPC has none, so its origin sits inside its own
    chest and every collider it owns is a candidate occluder. It worked only because the
    goblin's colliders are all on the NPC layer, which `Awake` strips — layer hygiene on
    every rig, and this project has already shipped a character left on Default. Now
    excluded structurally. (Still set new rigs to the NPC layer: `hitMask`, NPC-NPC
    collision and crowd separation all depend on it.)
- **GUARDING — block and parry (`IDamageMitigator`, `PlayerGuard`)**. RMB guards; heavy
  attacks moved to MMB.
  - **Mitigation lives on the VICTIM, consulted inside `Health.TakeDamage`** — never in the
    attacker. Same split as `IPushable`: attacker supplies force, victim decides meaning.
    So blocking works against melee, arrows, thrown props and environmental damage at once,
    and no damage source ever learns guarding exists. Putting it in `MeleeAttack` would mean
    adding "is the victim blocking?" to every damage source forever, and the ones added
    later would quietly forget. The blow is COPIED and mitigators edit the copy, so
    `OnDamaged` listeners (`NpcCombatAudio` volume, `NpcHitReactions` knockback, `NpcFace`
    shock) react to what actually landed rather than what was thrown.
  - **The parry window is measured from the DEFENDER'S input and evaluated AT impact** —
    "was the guard raised within the last `parryWindow` seconds?" That is what makes it
    compatible with animation-event sweeps, which genuinely cannot publish an impact time in
    advance. The alternative framing ("parry if you press within ±X of the impact frame")
    would have forced every attacker to predict itself. The window is CONSUMED on use and an
    expired one starts a cooldown, so one raise can't parry a volley and mashing gets you
    blocking but never free parries.
  - **The punish reuses existing machinery**: a parry calls `MeleeAttack.Parried()` (halting
    the swing) plus `Poise.Break()`, so it produces the same major stagger `NpcHitReactions`
    already generates — no bespoke stun state, and high poise becomes a free "resists
    parries" difficulty lever. `Poise` gained public `Chip`/`Break` for this; it previously
    had no methods at all, only its `OnDamaged` subscription. The attacker is reached
    through `DamageInfo.instigator`, so **no interface needed a return value** and each
    attacker still decides what being parried costs it.
  - **Both block and parry HALT the swing** (`Blocked()`/`Parried()`), so the blade stops
    dead instead of following through as though it cut flesh — the main tell that a block
    registered.
  - **A blocked hit reports the SHIELD's surface** (`Mitigation.overrideSurface`), because
    the blow never touched the victim. Without it `MeleeHitEffects` resolves
    `Surface.Of(victim)` and sprays blood for a hit that rang off metal. The mitigator is
    the only thing that knows what interposed itself, so `SurfaceImpact.Spawn` stays one
    seam rather than growing a guard special-case. Safe to read in `OnHitLanded` — that
    fires after `TakeDamage` returns.
  - **Named GUARD, not Shield**: the shield's hold-vs-time behaviours and the eventual
    weapon parry are one mechanism with different numbers (tighter window, worse chip
    mitigation), so naming it after the shield would mean renaming it.
  - **`PlayerGuard` holds STATE only — `PlayerMelee` still owns both hands.** One owner per
    transform, as with `ViewmodelCollision` and `facingLockedFrame`.
  - Blocking costs poise at `blockPoiseCost` (>1 on purpose): that pressure is what makes
    turtling lose to a crowd, and the reason a guard BREAK exists at all. **`Poise` is now
    on the PLAYER**, not just NPCs; without it a block costs nothing and can never break.
- **NPC reactions (`NpcHitReactions`, `NpcFlinch`, `NpcRagdollReaction`,
  `NpcCombatAudio`, `NpcAnimatorDriver`, `NpcHeadTrack`)** — how an NPC suffers, all
  as LateUpdate bone layers stacked on the Animator's pose so they blend with
  whatever's playing:
  - `NpcHitReactions`: the router. Knockback via the locomotion capability (thrown
    hits redirect part upward onto a real ballistic arc), stagger scaling with the
    shove, `Flinch(...)`/`ForceAlert` at the attacker. `applyCapsuleKnockback` (bool)
    gates ONLY the capsule-slide impulse — stagger and the bone flinch stay
    unconditional — so the slide can be isolated from the bone reaction while tuning
    either in isolation. Death: animation if the controller has a `Die` trigger, code
    topple as fallback, an **eased sink through the floor** (the despawn a player
    never catches happening) — and, critically, **disables `flinch`/`boneReaction`
    BEFORE calling `ragdoll.Die(...)`**. Skipping that order was a real bug: `NpcFlinch`
    runs in LateUpdate and kept overwriting bone rotations after death, fighting the
    ragdoll's own physics for the same bones (animation and physics must never drive
    one bone at once — see `NpcRagdollReaction` below for the deliberate reverse case).
  - `NpcFlinch`: per-bone hit flinch — angular impulses into bones near impact
    (distance falloff), spring-return to pose, chosen over true ragdoll-blending for
    LIVING hits deliberately (cheap while settled, works on a generic tripo rig).
    Authored **directional profiles** (`ProfileForBlow`, picked by the swing's blow
    direction — see roadmap #22's directional light combo, the reason varied swing
    directions actually matter). `impulseScale` scales the kick separately from the
    direction/profile pick, so knockback strength and flinch intensity can be tuned
    independently. **Debug tooling**: `showHitArrows` draws a proper 3D arrowhead
    (not a flat ray) at each real/debug hit showing blow direction, plus a **height
    ruler** (hips→head reference line with a tick at the hit's projected height) so a
    sword hit's actual height can be compared against the orbiting debug-preview tool
    that exercises the same code path. **Real hits were silently no-oping** even at
    high `impulseScale` — the nearest bone was outside `hitRadius` because real hits
    land at the collider SURFACE (`MeleeAttack`'s hit point) while the debug tool's
    test point is built directly from bone positions, so it never exposed the gap.
    Fixed by guaranteeing the nearest bone always gets at least a floor-strength kick,
    independent of `hitRadius`.
  - `NpcRagdollReaction`: full blended ragdoll (Unity Ragdoll Wizard, CharacterJoint
    chains) — gravity-OFF flinch for a "big hit" tier (opt-in, unused by default; see
    roadmap #22's poise-break note), gravity-ON for death. High solver iterations +
    a separate `deathForceScale` for stability. `playerWalksThroughCorpse` lets the
    player pass through a settled body rather than being blocked by dead-weight
    collision. This is the ONE place animation and physics are meant to fight over
    the same bones in sequence (ragdoll takes over completely) — contrast
    `NpcHitReactions`' death-disable fix above, which exists because `NpcFlinch`
    was NOT supposed to still be driving bones once this takes over.
  - `NpcAnimatorDriver`: one-way bridge from `NpcLocomotion` to Animator
    `Speed`/`MotionSpeed`/`VelocityX`/`VelocityZ`, and `TriggerDeath()`. AI never knows
    the Animator exists — a rigged model is a drop-in. **NEVER root motion** (the
    CharacterController drives). Reads `NpcLocomotion.CurrentSpeed`, which is
    `trueVelocity`, never raw `Controller.velocity` — the latter spikes during a push
    correction and drove a "running in place" animation artifact that was never a
    real position bug. **2D directional blend**: `LocalVelocity` (velocity in the NPC's
    OWN frame) drives a Freeform Directional tree with forward/back/strafe clips, so a
    crowd shove sideways plays a strafe instead of sliding the feet through a
    forward-walk pose. Two calibration fields, both non-obvious:
    `movementDeadzone` — a settled crowd never sits at exactly zero (separation and
    repathing nudge a few cm/s indefinitely), and with idle at (0,0) the pose flickered
    between idle and a walk direction EVERY FRAME. Applied to raw speed BEFORE damping
    (so the damped value eases to a true zero) and zeroing both axes as a PAIR (else a
    diagonal creep collapses onto one axis and swings the blend to a pure strafe on its
    way to idle). `fullBlendSpeed` — the tree's poles are unit vectors, so feeding raw
    m/s put an NPC at 0.4 m/s ~60% toward IDLE, feet barely cycling while the body slid.
    The magnitude is remapped to reach a full walk clip at `fullBlendSpeed` (direction
    preserved), leaving `MotionSpeed` to scale the CYCLE RATE to ground speed — which is
    what actually keeps feet planted. **Requires the blend state's Speed Multiplier bound
    to `MotionSpeed`, per state** (a second locomotion state e.g. Walk_Combat needs its
    own binding; missing it silently reintroduces sliding).
    **DIAGNOSTIC LESSON — the "jitter" was the POSE, not the POSITION.** A settled crowd
    visibly shivered; every physics-side theory (Jacobi overshoot from the position
    snapshot, damping the separation force) was WRONG, and was disproved decisively by
    setting separation smoothing to 0.99 and seeing NO change. The real causes were the
    facing fight (see `NpcLocomotion`) plus these two blend-tree calibrations. When
    something *looks* like it's vibrating, check whether the transform is actually
    moving before optimizing the physics.
  - `NpcHeadTrack`: head bone watches the player up close. Rig-agnostic (a rotation
    DELTA from body-forward toward the target, not an assumption about the head
    bone's own local axes) and **rest-orientation-agnostic**: the yaw/pitch clamp is
    measured in `Body`'s own local frame (`InverseTransformDirection` + signed
    `Atan2`, wrapped in `Mathf.Abs()` for BOTH yaw and pitch — `Atan2` is signed
    unlike the `Vector3.Angle` it replaced; missing `Abs()` on just one axis let the
    head track all the way around behind the NPC from one side only, a real shipped
    bug). `Body` defaults to the NPC's own root (correct for a standing NPC, whose
    root rotation IS its visual orientation) but takes an optional `bodyReference`
    override for a pose that comes from ANIMATION rather than a rotated root — e.g. a
    skeleton lying flat in a coffin via a "lying down" clip, whose root never
    rotates. Empirically, for a fully-animated pose the bone that works best as
    `bodyReference` is the **head bone itself**: being the last link in the animated
    chain, its accumulated world rotation reflects the whole posed hierarchy (hips
    tipped, spine bent, neck posed) in a way an earlier bone like hips/spine alone
    doesn't capture. **Gated on what the NPC actually KNOWS, not on awareness**
    (`onlyWhatItKnows`): SEEING the player tracks their live position; merely SUSPICIOUS
    tracks `LastKnownPosition`; neither tracks nothing. Awareness rises from HEARING too,
    so gating on it made a goblin that had only heard a noise lock onto the player's real
    position THROUGH WALLS — leaking the one thing hearing is meant to leave uncertain and
    undoing the LOS check that makes crouching behind cover work. The suspicion case is
    the good one: it stares at where the barrel landed while you slip past behind it.
    `suspicionLookHeight` lifts the gaze off the floor (noise positions are often ground
    level, and staring down reads as confusion). An honest detection tell either way.
    **AUDIO IS SPLIT BY SOURCE, NOT BY SITUATION.** `NpcCombatAudio`'s AudioSource is the
    VOICE — `NpcFace` follows its amplitude to open the jaw — so anything played through it
    moves the goblin's mouth. That decides placement on its own: the swing WHOOSH gets its
    own component and source (`NpcMeleeAudio`; a sword whoosh coming out of a goblin's face
    would be wrong), while the attack EFFORT GRUNT belongs in `NpcCombatAudio` beside the
    hurt and death voices, where it should move the mouth and does so for free. Ask "what
    is making this sound", not "when does it happen". NPC sources are 3D (a position you
    locate by ear) unlike `PlayerMeleeAudio`'s 2D one (your own arm); the whoosh rolls off
    shorter than the voice and hard-CULLS by distance, since inaudible sources still consume
    voice slots at crowd sizes. Both voice lines are thinned by chance + interval like
    `hurtChance` already was — a cry on every swing from 25 roamers is a wall of shouting.
    `NpcCombatAudio`: hurt grunts scaled by impulse, death cry + delayed body-fall
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
- **The player could shove NPCs around, even through walls (real field bug):**
  `CharacterController.Move` ALWAYS resolves any overlap the capsule finds itself
  in, regardless of the requested motion vector — a well-known Unity CC quirk. Since
  `NpcLocomotion.Update` calls `Move` every frame it's enabled (even idle, with
  near-zero requested motion), the player simply walking into a goblin and holding
  forward got it silently displaced every frame by that automatic resolution, and
  enough sustained frames of it accumulated real distance — occasionally enough to
  tunnel through thin geometry. Confirmed by the tell: it only happened with
  `NpcLocomotion` ON, because that's what calls `Move` at all. **Not** the boids
  separation above (verified from source — its registry is NPC-only, never the
  player). Fixed WITHOUT touching collision (the player still bumps into and is
  blocked by NPCs, unchanged) by rejecting the DISPLACEMENT after each `Move`:
  normal wall-blocking/sliding can only ever REDUCE a character's displacement below
  what it asked for, never add displacement in an unrequested direction, so any
  horizontal movement beyond `(want + impulse + Separation()) * dt`'s magnitude —
  which already legitimately covers pathing, knockback, AND NPC-NPC separation —
  can only be an external shove. `NpcLocomotion.RejectUnwantedPush` clamps exactly
  that excess back out.
- **NPC foot IK (`NpcFootIK`, Animation Rigging package)** — per-leg `TwoBoneIK`
  grounding for the generic rig: while the animation has a foot in stance, its IK
  target snaps to the raycast ground height (feet land ON stair treads); mid-swing the
  weight fades and the clip owns the foot. Converges instead of feeding back because
  the target comes from the ground raycast, an EXTERNAL stable reference — not the
  previous pose. `groundMask` must exclude the NPC layer (don't plant a foot on a
  neighbour). v1 has no pelvis drop, so a steep descent is limited by leg length.
- **NPC facial expression (`NpcFace`)** — BUILT. A cheap, high-charm life signal:
  jaw + eyebrow bones driven by THREE layered sines each (fBm-style, so it never
  visibly loops), normalized 0..1 and lerped between a min/max angle. **The min/max
  RANGE is the emotion** — narrow/raise the eyebrow range for surprise, lower/narrow
  it for anger; widen the jaw for a teeth-grind. LateUpdate (composes on top of any
  body animation), `[ExecuteAlways]` for edit-mode tuning. What's in it now:
  - **Awareness moods** — an `Expression` list picked by `NpcPerception.Awareness01`
    (Calm/Suspicious/Angry), one-way like `NpcAnimatorDriver`, so the face hardens
    as it detects you. A diegetic detection tell, same role as head-tracking.
  - **EYES with no extra bones** — the eye material is the ToonLit rim-light turned
    into an iris: `_BaseColor` = pupil, `_RimColor`/`_RimAmount` = sclera
    color/brightness, `_RimThreshold` = pupil SIZE (higher = BIGGER pupil, verified
    against the shader's rim math). Driven per material SLOT via the **INDEXED**
    `SetPropertyBlock` overload — the non-indexed one applies to the WHOLE renderer,
    so with eyes as one slot on the goblin's body mesh it tints the entire goblin.
  - **Hit shock** (momentary, per blow) vs **wounded** (sustained, by `Health01`) —
    deliberately different: injury BLENDS with the awareness mood rather than being
    another priority tier, so a badly hurt goblin that spots you still reads angry,
    just haggard. A priority chain would mute its reaction exactly when its face
    matters most. Death outranks both, permanently.
  - **Voice sync** — jaw opens to live `NpcCombatAudio` amplitude, toward
    `jawOpenAngle` BEYOND the mood's idle range (blending toward the mood's `jawMax`
    instead caps an angry clamped jaw shut — real bug).
  (Class was `JawSineAnimation` in `FacePoseTester.cs` — renamed to match, rule 3.)
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
- **CRAWLWAYS ARE PASSABLE BY GIVING SMALL CREATURES THEIR OWN AGENT TYPE — no AI code at
  all.** The tube's box colliders were always being baked (`DungeonCrawlways` is deliberately
  NOT in `excludeRoots`); they simply produced no walkable spans, because the voxelizer needs
  Agent Height of clearance and Unity's Humanoid is 2.0m against a 1.5m bore. So a second
  `NavMeshSurface` with a short agent type makes the bores appear in ITS surface and nowhere
  else. **Corners come free**, which is the whole reason this beats the ladder approach: an
  off-mesh link is a straight A→B traversal and cannot follow a bore that turns, so
  `NavMeshLink`s would have needed a chain per cell plus scripted traversal. Same philosophy as
  bridges — behaviour falling out of geometry rather than out of AI. `DungeonNavBaker` bakes
  every `NavMeshSurface` on its GameObject, so adding an agent type is adding a component.
  **DO NOT instead lower the Humanoid height to 1.5**: that makes every low gap in the dungeon
  walkable. The design payoff is that crawlways become SIZE-GATED — kobolds follow you in,
  goblins cannot — which keeps the escape-route property against the things it matters for.
- **A NEW AGENT TYPE'S STEP HEIGHT AND MAX SLOPE FRAGMENT ITS NAVMESH, AND NOTHING POINTS AT
  THEM** (cost a real debugging round). Height and radius are what you think about when adding
  a small agent, and they are not what breaks: authoring a small creature with a small STEP
  HEIGHT loses every staircase — already the most fragile navmesh here, which is why the baker
  overrides voxel size — and small lips where kit meets greybox. **The symptom is
  `PathPartial` to somewhere completely ordinary**, i.e. an NPC that walks partway and stops,
  looking exactly like a routing decision or a stuck body. A small creature wants a GENEROUS
  step height; it is a pathing allowance, not an animation claim. Both `DungeonNavBaker` and
  `NpcLocomotion.debugNav` now print step and slope beside height and radius for this reason.
- **`NavMesh.SamplePosition(..., NavMesh.AllAreas)` TAKES AN AREA MASK AND SILENTLY USES THE
  DEFAULT AGENT TYPE.** Indistinguishable from correct while the project had one agent type;
  the moment a second exists, every such call asks "is there HUMANOID navmesh here?" on behalf
  of a crawler. Inside a crawlway the answer is no, so the destination sample fails, the brain
  never issues a path, and the NPC stands at the grate looking like it chose not to follow.
  Four call sites had it (`NpcBrain` ×3, `NpcLocomotion.WarpToNavMesh`) plus the baker's spawn
  snap. Fixed via `NpcLocomotion.NavFilter`, one property built from the agent's own
  `agentTypeID`, so they cannot drift. §12's perspective rule — a shared system gains a second
  kind of user and its defaults are quietly wrong for them.
- **`NpcLocomotion.debugNav` EXISTS BECAUSE "THE NPC JUST STANDS THERE" HAS SIX CAUSES THAT
  LOOK IDENTICAL** — wrong agent type, off its navmesh, no destination issued, a Partial path,
  a body that cannot fit where the agent planned, or a physical block. The split that makes it
  slippery is structural: **the agent PLANS and the CharacterController MOVES**
  (`updatePosition = false`), so `want` can read a healthy 2.5 m/s while the body goes nowhere,
  or the body can be free while `want` is zero. Printing both together separates them in one
  line, and printing **where a partial path GIVES UP** turns the diagnosis into flying to a
  world position and looking at it: a staircase means step height, a crawlway mouth means the
  tube is an island, a doorway means a prop narrowed it.
- **LADDERS ARE INVISIBLE TO THE NAVMESH — a live gameplay limitation, not a quirk.**
  Climbing is scripted (`LadderClimbZone` + the controller's overlap poll), so no
  `NavMeshAgent` has any route across a ladder. On a seed whose only way up is a
  ladder, **NPCs simply cannot follow the player** — and `AllocateLadders` produces
  exactly that whenever an elevated door can't get an interior stair. Fixing it needs
  BOTH halves: `NavMeshLink`s spanning each ladder (foot→head) at bake time in
  `DungeonNavBaker`, AND off-mesh-link handling in `NpcLocomotion`
  (`agent.isOnOffMeshLink` → drive the capsule up → `CompleteOffMeshLink()`). Adding
  the links alone is WORSE than nothing: an agent paths onto a link it can't traverse
  and gets stuck at the bottom. A ladder's walkable top is the HALLWAY cell through
  the doorway (`BaseCell + up*HeightCells + WallDir`) — the room-side threshold sits
  in the room's open vertical volume with no floor under it.
- **CELL-LEVEL CONNECTIVITY ≠ NAVMESH CONNECTIVITY (generalizes — remember this
  shape).** The prop system's safety net is a flood-fill/BFS over grid CELLS, and it
  can pass while the navmesh is severed: a collider prop on an ordinary floor cell
  leaves cell connectivity intact but narrows the real gap below the agent radius. The
  player never notices — a `CharacterController` has `stepOffset` and squeezes past —
  so the tell is **"the debug path is red along a route I just walked"**. First seen at
  a staircase foot (stairs are the most fragile navmesh in the project, see the voxel
  note above), fixed by keeping BLOCKING tiers off any cell touching a stair
  (`PropSnap.NearStair`, shared so the room and hallway placers can't drift; décor is
  unaffected — no collider, no bake impact). **Doorways are the same shape with more
  margin** — that's where to look next if a red-but-walkable path shows up away from
  stairs.
- **DungeonPathDebug** (on DungeonVisualizer) — draws the walkable route to the Exit
  (Start→Exit, or player→Exit), **P** toggles. Pathed with `NavMesh.CalculatePath`
  rather than the room graph on purpose: the graph only says which rooms CONNECT, this
  says whether you can WALK there. Three states — **green** direct, **amber** only via
  a ladder (so NPCs can't follow, warned once per dungeon), **red** genuinely
  unreachable, warned once with seed+depth. It BFS-hops navmesh islands using the
  generator's `Ladders` list. The line is built in code (no prefab/material to author)
  with **Sprites/Default**, because URP/Unlit ignores VERTEX colour and `startColor`/
  `endColor` silently did nothing — the line rendered white and all three states looked
  identical. Given the vertical Start/Exit rule this doubles as the connectivity check
  for the hardest routes the generator can produce.
- **DungeonExitPortal** — the interactable in the Exit room that advances a run to the
  next depth (placeholder for a ladder/hatch). No new machinery: `PendingDepth`/
  `PendingSeed` are statics consumed inside `Generate()` before the generator is built,
  so they survive the scene reload, and `DungeonPlayerSpawner` then puts the player in
  Start. **It always sets a seed explicitly** — `randomizeSeedOnGenerate` defaults to
  FALSE, so leaving it alone regenerates the SAME layout one depth deeper. The default
  DERIVES the next seed from the current one (a proper bit-mixer, not `seed + depth`,
  which leaves consecutive runs sharing most of their bits), so a whole multi-depth run
  replays from its first seed — rule 4 staying useful across a run, not just one floor.
  Author it as a Feature, guaranteed ×1, **`PropTier.FullGameObject`** (§8 — an
  instanced tier would bake the mesh and spawn only a collider).
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
  **THE SKINNED MESH'S ROOT BONE MUST BE THE ARMATURE, NOT THE HIPS (real field bug,
  cost several sessions, and it will recur on every new rig).** With
  `updateWhenOffscreen` OFF — which is what we want, since recomputing bounds from
  skinned vertices every frame is exactly the per-NPC native cost §10's perf work
  removed — Unity frustum-culls a `SkinnedMeshRenderer` using its **bind-pose AABB
  transformed through the ROOT BONE**, never from the actual animated vertices. The
  goblin's root bone was the hips, an ANIMATED bone that bobs, shifts and rotates
  every frame of a walk cycle, so the bounding box rode the hips instead of enclosing
  the body and any hip displacement slid it off the silhouette — the renderer was
  culled while the goblin stood in plain view. The Armature sits at the top of the rig
  and barely moves relative to the NPC root, so the box stays over the character.
  **Why it was so hard to see:** it needed the hip offset to clear the body AND the
  frustum edge to fall in the gap, so it depended on animation phase and camera angle
  *together* (hence "occasional"), and it worsened with NPC COUNT only because more
  bodies sit near the screen edges at any moment — which is where a shifted box first
  leaves the frustum. That count-scaling is what disguised it as a shader problem (§5).
  **Nothing in the project validates this**, so a new rigged character can arrive with
  the same defect and present as "occasionally invisible" all over again — check the
  root bone when adding one. Residual risk: the bounds are still the FBX bind-pose box,
  so a pose EXCEEDING it (a physics-driven ragdoll death is the likely one) could cull
  again; the fix there is padding `localBounds` at spawn, NOT switching
  `updateWhenOffscreen` on.
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
- **GroundFog** (on DungeonVisualizer, beside DungeonFogController) — ankle-height
  drifting mist. The SHADER and the reason it's billboards are in §6; this component
  owns only the two things the inspector can't know. The `ParticleSystem` itself is
  **authored in the editor deliberately** — emission, size, lifetime and shape are art
  decisions that want live preview, and setting particle modules blind from code is a
  poor trade.
  - Floor height comes from the player's **CELL, not their world Y**, so standing on a
    stair tread or a crate doesn't lift the fog with you.
  - The emitter **SNAPS** on a floor change rather than easing. With World simulation
    space the existing puffs stay where they were, so there's no sheet of fog visibly
    sliding vertically — which is what forced a fade-out on the earlier plane version —
    and only the SPAWN point moves. Previous-floor puffs are `Clear`ed so they can't
    hang in mid-air over a ledge or through a doorway.
  - Tint reads `RenderSettings.fogColor`, which DungeonFogController already drives
    from the room's torch palette (§7), so ground fog can't disagree with the distance
    fog or the torchlight and there's no second copy of the room lookup. Applied via
    `MaterialPropertyBlock` so it never instantiates a copy of the shared material.
- **DungeonMapper** (on DungeonVisualizer) — fog-of-war automap. **The dungeon is
  ALREADY the map**: it's a typed integer grid, so this is a FILTER over the
  generator's output (a set of explored rooms/cells) painted into one `Texture2D`,
  a pixel-block per cell. Renders to an optional `RawImage`, else an OnGUI overlay
  (the same dev-overlay approach FirstPersonController uses); **M** toggles.
  - **Reveal is deliberately NOT a uniform radius.** Rooms reveal WHOLESALE on entry
    — honest (you can see the room) and free, since `Room.Cells` is the exact
    footprint, which also gets L-shapes right instead of their bounding box.
    Corridors reveal cell by cell as walked (1-wide and winding, so the drip-feed is
    what makes the map feel earned). A radius would dribble a room in as you cross it
    AND leak through walls into rooms you never entered; the corridor radius
    explicitly refuses to bleed into an unentered room.
  - **Walls are masked from the GRID, never from the explored set.** Masking on
    exploration draws a solid wall across the far end of a half-walked corridor — the
    map asserting a dead end where the tunnel continues. On solidity, a FRONTIER
    (neighbour open but unexplored) gets no wall and reads as "continues, unknown".
    Biggest readability win in the whole system.
  - **ONE FLOOR AT A TIME**, with THREE distinct connector glyphs, because the
    generator really does produce one-way links (an elevated door takes an interior
    stair → falls back to a ladder → failing both, "leaves a one-way drop"). Drawing
    that like a staircase would lie to a player planning a route back. Ghosted
    neighbour floors are an optional toggle, drawn FLAT (no walls, no connector
    colors) and darkened as well as faded — alpha alone still reads as "current
    floor", which is the exact ambiguity a ghost must avoid.
  - **Floor NUMBERS count from the lowest OCCUPIED grid level**, not raw Y:
    `gridHeight` is a budget, so a dungeon whose content starts at y=5 would announce
    "Floor 5" — a number that means nothing and changes between seeds for no visible
    reason.
  - Glyphs are hand-authored pixel art (`string[]`, `#` = filled), not text — a
    `Texture2D` has no font rasterizer and scaled-down text mushes at ~7px. Room-type
    glyphs mark only Start/Exit/Merchant/Throne/Treasury; glyph everything and nothing
    stands out. Placed at `InteriorFloorCell`, NOT `Bounds.center` (an L-room's bbox
    centre can be in the bite).
  - Uses the **same world→cell + `RoomAt` path** as `FirstPersonController.CurrentRoomLabel`
    and `DungeonFogController`, so the map can't disagree with the room readout or the
    fog color. Watches the **generator instance** to wipe on regenerate (F1/PgUp).
    `revealAll` is a debug view that never marks anything explored, so toggling it off
    restores the genuine fog state.

---

## 10b. Audio (`SOUNDSYSTEM_PLAN.md` has the staged plan)

Individual sound-producing components are documented beside the systems they belong to
in §10 (`PhysicsDoorAudio`, `ImpactAudio`, `HangingCageAudio`, `NpcCombatAudio`,
`PlayerBowAudio`, `PlayerFootsteps`). This section is the INFRASTRUCTURE under them —
routing, ambience, the voice budget, and the authoring tools.

**HOUSE PATTERN, stated once because every audio component follows it:** continuous
sounds are a LOOPING source whose volume/pitch track a live value, driven from state
every frame; discrete sounds are one-shots. The loop is never started/stopped by events
— `PlayerBowAudio` reads `bow.IsDrawing` each frame precisely so that every exit path
(fired, let down, weapon swapped, picked up a barrel) fades it out without each one
having to remember to. `OnDisable` hard-stops, so a swap mid-action can't leave a creak
looping forever.

### Mixer routing

`DungeonAudioMixer` with Master → SFX {Physics, Combat, Footsteps} / Ambient {Base,
Room, OneShots} / Music, plus a Reverb bus fed by sends.

- **AN UNASSIGNED MIXER GROUP BYPASSES THE MIXER ENTIRELY** — it goes straight to the
  listener, NOT to Master as the name "output" suggests. So an unrouted source is
  inaudible to every meter, immune to every volume slider, and looks exactly like a
  source that isn't playing. The F7 overlay flags them as `<unrouted>` for this reason.
- **`AudioBus.Route` vs `AudioBus.Assign` — the distinction is load-bearing.** `Route`
  is null-TOLERANT: an owned source with no group configured keeps whatever it has.
  `Assign` is UNCONDITIONAL, for POOLED sources — a pooled voice must be told its group
  on every acquisition, because otherwise it inherits the PREVIOUS caller's group and a
  footstep comes out of the Combat bus. That was a real bug, and it is invisible except
  on the meters.
- **The group is set on the COMPONENT, not on the AudioSource.** Most of these sources
  are created at RUNTIME when the prefab has none, so an Output assigned in the inspector
  would cover only the authored case.

### One-shots

**`AudioSource.PlayClipAtPoint` CANNOT BE MIXED.** It creates a hidden throwaway
GameObject with no group, so every sound played through it bypasses the mixer by the rule
above. `OneShotAudioPool` replaces it: an 8-voice ring, **one child GameObject per
voice** (an AudioSource is positioned by its transform, so a shared object cannot place
several sounds), taking an `AudioSpatial` per call.

### Which space am I in — `AudioSpace`

**ONE resolution, shared by `AmbientDirector` and `ReverbDirector`.** Its ORDER is
load-bearing and invisible from any single call site: a pit cell ALSO resolves to a Room
(`RoomAt` deliberately falls through `PitAt` so a pit is styled as part of its room, §4),
and an alcove cell is `CellType.Hallway` (§4's grid-invisible design), so the most
specific space must be asked about first — pit, room, prison, alcove, hallway. Two copies
of that order would drift, and the symptom is ambience and reverb disagreeing about which
room you are standing in. Same family as `ComputeZones`, `NeedsSlabBetween` and
`PropSnap.NearStair`.

It also carries `SizeCells`, which falls back to the BOUNDING BOX volume when `Cells` is
empty — `Room` documents an empty `Cells` set as "treat as a full box (legacy safety)",
so a perfectly ordinary room can report 0 cells and a size-driven consumer would then
treat the grandest hall in the dungeon as a closet.

### Ambience

`AmbientDirector` is a MANAGER, not a per-room component — two sources per layer so it
can crossfade, and a one-shot ticker that picks a random floor cell via
`RoomPropPlacer.ComputeZones(...).Floor` (with a debug gizmo, because "is it choosing a
cell or just the centre?" is otherwise unanswerable).

**IT WATCHES THE RESOLVED PROFILE, NOT `OnRoomChanged`.** Corridors, alcoves, prisons and
pits all have `CurrentRoom == null`, so a room-change event fires for exactly one of the
five spaces the game has. Watching what the profile RESOLVES TO covers all of them and
needs no per-space cases.

`PlayerRoomTracker` (`[DefaultExecutionOrder(-100)]`, frame-stamped `Refresh()`,
self-installing from `DungeonVisualizer.Awake`) is the shared answer to "where is the
player" — `CurrentCell`/`CurrentRoom`/`CurrentPit`. Same rule as §10's fog/map/room-label
trio: one world→cell path so nothing can disagree.

### Reverb (`ReverbDirector`)

Computed from the room the generator already built, never hand-placed — the same
philosophy as the pit rims and lintels. Interpolated between a `small` and a `hall`
setting by `AudioSpace.SizeCells`, so a new room type sounds right with no authoring;
`AudioProfile.overrideReverb` is the exception hatch.

- **A MIXER BUS, NOT A LISTENER FILTER.** An `AudioReverbFilter` on the AudioListener
  processes the ENTIRE final mix, so music would reverberate with the room. The mixer
  expresses it correctly: `SFX` and `Ambient` **Send** to a `Reverb` group, `Music` never
  sends and is dry by construction. **Chain order is signal order** — the `Receive` must
  sit ABOVE the reverb effect, or the send arrives and leaves dry while the bus looks
  correctly wired.
- **THE PARAMETERS ARE MILLIBELS, NOT DECIBELS** (cost a full round of wrong defaults).
  Unity's `SFX Reverb` takes `Room` / `Room HF` across **-10000..0 mB**; its own default
  for `Room` is -1000. Authored as though they were dB, every value from -6 to -24 lands
  within a quarter of a decibel of FULLY WET, so a closet and a grand hall come out
  identically drenched and the size blend reads as broken code. Useful range is about
  -2500 (dry, tight) to -600 (cavernous). Only `Decay Time` is in an intuitive unit.
- **THE ROOM POPULATION IS BIMODAL** — satellite closets are ~2 cells, ordinary rooms are
  60-150, nothing in between. So `smallCells` does NOT tune the closets (they clamp to
  `small` at any value above ~3); it only decides where the smallest ORDINARY room sits.
  Measured values put the blend at 10..165. Check this again if room sizing ever changes:
  the first values (12/120) left the whole population in the upper half of the curve with
  the two largest rooms clamped together.
- **`AudioMixer.SetFloat` DOES NOT UPDATE THE MIXER WINDOW'S SLIDERS.** A script-driven
  exposed parameter keeps showing its snapshot value, so a static slider is not evidence
  the driver is dead. Use the `debugReverb` log. (Conversely a moving VU meter on the
  Reverb group proves only that the SEND works, not that reverb is being applied.)
- **A NEW `Send` DEFAULTS TO -80 dB, i.e. SILENT** — cost a debugging round. It is the
  third default in this system whose failure mode is indistinguishable from correct
  wiring, after an unassigned mixer group and `playOnAwake` with a null clip.
- Corridors, prisons, alcoves and pits have no room to measure, so they take their
  profile's reverb even when `overrideReverb` is off — for a corridor it is the only
  number there is, and treating "not overridden" as "use the small default" would make
  every corridor sound like a closet.

### Footsteps and surfaces (`FootstepSurface`, `Surface.Below`)

Each footfall resolves what is underfoot and picks its clip from `SurfaceLibrary`, so a
wooden staircase or bridge sounds wooden with no authoring beyond the `Surface` component
the prefab already wants for sword hits.

**TWO LAYERS, AND THE SECOND ONE IS NOT OPTIONAL — MOST OF THE DUNGEON CANNOT CARRY A
`Surface` AT ALL.** `DungeonMesher.Build` emits the ENTIRE shell — every floor, wall and
ceiling — as ONE GameObject with ONE `MeshCollider`. A downward probe on an ordinary
floor therefore always hits that single collider, which can only hold one `Surface` for
the whole dungeon. **A `Surface` authored onto a floor prefab looks correct and silently
does nothing**, because that prefab's collider is not in the world (§5: the kit is visual
only; collision is the greybox). Stairs work only because stairs are among the handful of
pieces that DO get their own collider GameObject — archways, doors, columns, ladders,
corner pillars are the rest.
1. **Probe** (`Surface.TryBelow`) — anything with its own collider: stairs, bridges,
   doors, props. Per-CELL by construction, which is what makes "you just stepped onto a
   wooden bridge over a pit" work with no extra authoring.
2. **Cell lookup** — everything else. The generator already knows what a cell IS, so
   `AudioSpace.ResolveAt` resolves the space and its `AudioProfile.floorSurface` answers.

`TryBelow` returning "no `Surface` found" rather than collapsing into the fallback is the
hinge the whole thing turns on. And the probe still WINS wherever it can answer, so the
original rule — surface is a property of the CELL, not the room type — is preserved
rather than reversed; the per-space value is only the floor beneath everything else.

**Resolved from EACH ACTOR'S OWN position** (`ResolveAt(vis, cell)`, with `Resolve(tracker)`
now a thin wrapper over it), so an NPC crossing a bridge sounds like wood while the player
standing in the room does not. `AudioSpace.CellOf` matches `PlayerRoomTracker`'s
world-to-cell conversion exactly instead of re-deriving it — golden rule 5's
float-Y-at-a-storey-boundary trap.

- **ONE `SurfaceType`, EXTENDED — never a second footstep enum.** The first draft proposed
  a parallel `Stone/Gravel/Water/Bone/Wood`; two enums that both answer "what is this made
  of" WILL drift, and the tell is a wooden bridge that sparks like stone under a sword but
  thuds like wood underfoot. **APPEND ONLY** (`Gravel`, `Water` added): `Surface.type` is
  serialized by INDEX on every tagged prop and NPC, so inserting in the middle silently
  re-materials all of them — the reason `DamageType.Projectile` was appended too.
- **SURFACE IS A PROPERTY OF THE CELL, NOT THE ROOM TYPE.** That is why it is a probe and
  not a `RoomStyle` lookup: a per-room-type setting cannot express "you just stepped onto
  a wooden bridge over a pit", which is one of the most distinctive moments the generator
  produces.
- **`probeMask` MUST EXCLUDE THE NPC LAYER** or a goblin in a crowd samples the surface of
  whoever it is standing beside — the same rule `NpcFootIK.groundMask` carries. Both
  authored components are masked; a new one will not be.
- **The NPC cull runs BEFORE the probe**, so the crowd's raycast cost scales with how many
  NPCs you can HEAR rather than how many exist.
- **Fallback is the design, and it is why the debug log names WHICH SOURCE WON.** A
  surface with no authored clips falls back to the component's own, so an unfilled library
  changes nothing — but that also means a silently-failing library sounds IDENTICAL to a
  working one while Stone is the only surface authored. The log states the reason the
  library declined for exactly that reason.
- NB the library path multiplies `Entry.footstepVolume` by the component's `volume`, so
  moving a surface onto the library makes it quieter unless that is set to 1.
- **AN UNASSIGNED `RoomStyle` AUDIO SLOT FAILS PLAUSIBLY, NOT LOUDLY.** `PitAudio()` falls
  back to the OWNING ROOM's profile (deliberately — a pit is styled as part of its room
  everywhere else), so an empty `pitAudio` gave pits the room's floor surface AND the
  room's reverb, with nothing wrong-looking anywhere. `PrisonAudio`/`AlcoveAudio` fall back
  to the hallway the same way. When a space "sounds like the wrong space", check the slot
  is filled before doubting the resolution.

### Positional loops (`TorchAudioPool`) — and why a loop is not a one-shot

Per-torch fire crackle. **Positional rather than an ambient bed because a bed cannot PAN** —
walking a corridor past a sconce and hearing it swing across you and fall behind is the
whole effect, and corridors are where torches are sparse enough to locate individually.

- **A LOOP HOLDS ITS VOICE FOR AS LONG AS IT PLAYS**, unlike a one-shot that frees the slot
  when the clip ends. So one AudioSource per torch is 100+ PERMANENT voices against a
  budget of 32 — the phantom-voice shape again, except these ones actually mix, which makes
  it worse. A fixed pool is reassigned to whichever torches are nearest, bounding cost by
  what can be PERCEIVED rather than by what exists. Exactly `maxShadowCasters`' logic.
- **HYSTERESIS ON THE STEAL (`stealMargin`) IS REQUIRED, NOT POLISH.** Two torches at
  near-equal distance swap rank on the smallest movement and a looping source jumping
  between them stutters — the oscillation `NpcBrain.approachHysteresis` and
  `investigateRadius` exist to stop. **Shadow casters need no equivalent**, because a shadow
  popping between two distant torches is invisible; audio is not. Same query, different
  tolerance.
- **THE ROLLOFF MUST REACH ZERO AT `maxDistance`.** That single property is what lets a
  voice be released or stolen there with NO fade machinery — the switch happens while the
  source is already silent. Linear guarantees it; a custom curve is the author's
  responsibility (checked at startup, warning names the actual end value); **Logarithmic is
  deliberately not offered** since it never reaches zero and would click on every steal. The
  symptom of getting this wrong is a click each time you walk past a torch, which reads as a
  bad loop point rather than a rolloff setting.
- Voices start at a **random point in the loop** — every one starting at t=0 makes a
  corridor crackle in lockstep, which reads as one wide sound rather than several fires.
- `AudioPriority.AmbientPoint` (48): below a bed, whose absence is a hole in the whole
  world, but above player actions, because the pooled voices are by construction the nearest
  audible ones.

**`AudioSpatial` CAN NOW CARRY A CUSTOM ROLLOFF CURVE**, which is how impact falloff
(`SurfaceLibrary.impactSpatial` → every melee hit, thrown prop and arrow, via the one
`SurfaceImpact.Spawn` seam) is authored. It was previously refused on the grounds that a
curve baked onto a pooled voice would leak to the next caller — real, but `ApplyTo` runs on
EVERY acquisition, so each caller overwrites the last. **Unlike the torch loop this curve
need NOT reach zero**: a one-shot ends on its own, so there is no reassignment to hide, and
staying faintly audible at the edge of the range is a legitimate choice.

### Occlusion (`AudioOcclusion` + `AudioOcclusionManager`)

Sound with geometry between it and the listener is quietened and, more importantly,
LOWPASSED. Settings live on `DungeonVisualizer` beside `FogSettings`, because the manager
auto-installs on first use and so cannot otherwise be authored before play.

- **NOT VIA THE ROOM GRAPH** (the plan's own corrected premise). The Delaunay/MST graph
  encodes "a corridor was carved between these two rooms", NOT "sound can travel between
  them": two rooms sharing a wall are usually not graph-connected, and two that are can be
  thirty metres apart through winding corridor. It would muffle the room you can hear
  through the wall while passing sound freely down a corridor that should attenuate it —
  wrong in both directions.
- **TWO PATHS.** Loops register and are re-tested round-robin with smoothing (a creak must
  un-muffle as you round the corner; smoothing stops a body crossing the sightline making
  it stutter). One-shots take ONE raycast when they play — nothing later can correct a
  sound that has already finished. The 8 POOLED voices are the exception and are tracked
  continuously, because they carry the multi-second ambient clips where walking out of
  earshot mid-clip is normal.
- **`sourceSkin` IGNORES THE LAST STRETCH OF PATH NEXT TO THE SOURCE, AND IS LOAD-BEARING.**
  Sound here is emitted FROM surfaces constantly, so a ray run all the way to the source
  hits the geometry the sound is coming off. Two real cases: `SurfaceImpact` plays at the
  CONTACT POINT, which is by definition exactly on a collider, so every arrow and sword
  strike on a wall would have read as fully occluded; and **a wall torch has only
  `wallGap` (0.3) − `wallMargin` (0.2) = 0.1m of clearance from the greybox collision
  plane**, which is far too fine a margin to survive a raycast at a grazing angle down a
  corridor. Note that pairing: `wallMargin` insets collision TOWARD the room (§5), so it
  eats directly into the torch's standoff, and raising it past 0.3 would put torches behind
  their own wall.
- **`occludedVolume` AND `occludedCutoff` ARE NOT INDEPENDENT — they double-count.** A
  lowpass at 900Hz is itself a large attenuation for BRIGHT material, and the ambient
  library (drips, chains, chants) is almost all bright, so cutting the highs already
  removes most of their energy; a further broadband ×0.4 made them vanish entirely. Erring
  HIGH on volume (0.65–0.75) and letting the cutoff carry the character is the right
  compromise, because the cutoff is what reads as BLOCKED where attenuation alone reads as
  merely further away. If impacts ever feel under-muffled while ambience feels right, that
  is the signal one global multiplier is not enough and it wants to move onto `AudioSpatial`
  per sound family.
- **EXEMPTION BY `spatialBlend`, NEVER A LIST.** Anything 2D — the player's own footsteps,
  sword, bow, and the ambient beds — is not in the world and cannot be occluded by it. A
  new 2D sound gets this right without being told.
- **`blockerMask` MUST EXCLUDE THE NPC LAYER** (and Player, and Viewmodel): a goblin
  between you and a torch is not a wall, and a crowd crossing the line would make
  everything behind it flutter. Third instance of this rule after `FootstepSurface.probeMask`
  and `NpcFootIK.groundMask`.
- **FILTER-ONLY REGISTRATION EXISTS FOR SOURCES WHOSE VOLUME IS THEIR OWN STATE.**
  `PhysicsDoorAudio` and `HangingCageAudio` `MoveTowards` on `volume` and then READ IT BACK
  to decide whether to play at all, so a second writer would corrupt the accumulator rather
  than merely fight it. Muffling alone is the better half anyway.
- **A POOLED VOICE MUST BE SEEDED PER ACQUISITION** or a sound in the open inherits the
  muffling of the impact before it — and `Begin` SNAPS rather than eases, because a NEW
  sound should start correct rather than slide into correctness over the smoothing window.

**BUILDING IT EXPOSED THREE BUGS THAT WERE NOTHING TO DO WITH OCCLUSION**, two of which
were shipping silently — which is the argument for building a system that asks "can the
player actually perceive this" earlier rather than later. Ambient one-shots were being
placed INSIDE WALLS (the corridor scan sampled a ±4 cell box asking only "open, with a
floor", which reaches into adjacent prisons and rooms); the bed crossfade could not be
interrupted; and impacts would have occluded themselves at their own contact point.

**AN AMBIENT ONE-SHOT IS NOW PLACED ONLY WHERE IT CAN BE HEARD** (`AmbientDirector.Audible`,
one raycast per candidate). Deliberately asked of the occlusion system rather than by
CellType, which gets two cases backwards: an ALCOVE is typed Hallway and is good ambience,
while a prison MOUTH is genuinely audible from the corridor. It also self-corrects when the
mask or `sourceSkin` is retuned. **`TorchAudioPool` does NOT yet do this** — it picks the
nearest torches by raw distance, which preferentially picks ones through a thin wall, and
that is the likeliest thing to revisit if torch crackle ever feels absent.

**A CROSSFADE PARAMETERISED BY ONE CLOCK CANNOT BE INTERRUPTED.** `AmbientDirector.Layer`
drove both sources from a shared `t` reset to 0 on every `Play`, so re-entering a space
mid-fade recomputed the OUTGOING source as `master * (1 - 0)` and slammed it to full — the
room's bed popping in every time you crossed a doorway twice. Tracking each source's own
level (`MoveTowards`) makes interruption free, and interruption is the NORMAL case, because
a doorway is exactly where people linger and turn around. Returning to a bed still fading
out swaps the roles rather than calling `Play` again, which would restart it from sample 0.

### THE VOICE BUDGET (F7 — `AudioBudgetDebug`)

Unity's real-voice limit is ~32. Past it, voices are STOLEN, and a stolen voice is not a
quieter voice — it is a sound that does not play.

- **`AudioSource.isVirtual` IS THE METRIC, NOT SOURCE COUNT.** An idle source costs
  essentially nothing; the project runs 341 sources against a peak of 14 playing. Counting
  sources measures nothing and looks alarming.
- **`playOnAwake` WITH A NULL CLIP IS A PERMANENT PHANTOM VOICE** (the big one — it was
  186 of them against a budget of 32, peaking at 189 playing with 84 stolen). A source
  told to play with no clip enters a playing state that NEVER COMPLETES: it reports
  `isPlaying` forever while making no sound. Silent, invisible, and holding a slot each.
  **`playOnAwake = false` in `Awake` cannot undo it** — it governs only a FUTURE start,
  and the engine acts on the authored flag before `Awake` runs. `Stop()` is required, and
  eight components now do both. Untick the flag on the prefab as well; the `Stop()` is the
  defensive half.
- **CULLING IS NOT ROLLOFF.** Rolloff makes a distant source QUIET; it still starts, still
  holds a slot, and still competes. `AudioCull.TooFar(transform, distance)` (cached
  listener) is the actual fix, and every one-shot component calls it.
  **WHERE the cull sits matters**: `ImpactAudio` culls AFTER firing `OnImpact`, because
  that event drives `DestructibleProp` damage and NPC alerting — culling above it would
  mean a crate you cannot hear also cannot break. Distance decides whether you HEAR an
  impact, not whether it HAPPENED. (Exactly the mistake the retrigger gate made once
  already, §8.)
- **`AudioSource.priority` IS INVERTED** — 0 is kept, 256 is dropped first. `AudioPriority`
  names the tiers so nobody has to remember: Bed 32, PlayerAction 64, WorldImpact 96,
  NpcVoice 140, NpcCombat 170, NpcFootstep 200. Beds are protected because a missing
  ambient bed is more noticeable than a missing footstep in a crowd.
- **THE SETTLE BURST WAS A SEPARATE, REAL PROBLEM** — see `PropPhysicsSleep` in §8. A
  hundred props settling at generation each fire one impact, and `ImpactAudio`'s retrigger
  gate is PER SOURCE, so a hundred single impacts sail straight past it.
- **Measured outcome: 50 NPCs mid-fight (double the §11 target population) peaks at 14
  voices with 0 stolen.** So the per-category budget allocations in the plan can stay
  theoretical, and step 7's music stems will not trouble it.

**DIAGNOSTIC LESSON, and it is the same one §12 keeps recording.** The overlay showed
`111 / 84 / 50 / 25` — the SAME numbers in three consecutive screenshots taken during
completely different gameplay. Identical numbers under changing conditions mean the count
is not measuring the activity you think it is, and that was the whole answer sitting in
plain view. Instead, two fixes (culling, then `PropPhysicsSleep`) were built against a
plausible reading of "Physics is 111 of 189" BEFORE the cause was found. Both were worth
keeping on their own merits; neither was the fix. **Interrogate a metric that doesn't
move before optimizing against it.**

### Normalizing audio files (`Tools/normalize-audio.ps1`, `Tools/sort-audio.ps1`)

Authored clips arrived spanning 38 LU (−44.9 to −6.9 LUFS), which no amount of per-source
volume tuning can fix — it just moves the problem to the next clip.

- **INTEGRATED LUFS IS FOR PROGRAMS; PEAK IS FOR SHORT TRANSIENTS.** LUFS normalization
  of a 200ms impact demands linear gain that the true-peak ceiling then refuses, because
  the CREST FACTOR is huge — the clip is nearly all transient. 23 files missed target for
  exactly this reason, ALL of them quieter than asked. Categories now carry a per-category
  `Mode` (`lufs` / `peak`); after splitting, all 64 landed within 1 dB.
- `-Measure` / `-Suggest` / `-Install`, with a verification pass after writing.
  **`-Suggest` reports the MEDIAN plus named outliers**, not the worst member — the first
  version recommended dropping the whole combat category from −16 to −24.5 because one
  already-hot whoosh dragged it there.
- **PowerShell 5.1 traps this cost time on:** a BOM-less `.ps1` is read as ANSI, so a
  UTF-8 em-dash breaks parsing and the error is reported **on the file's last line**,
  nowhere near the actual character. `2>&1` on a native exe wraps stderr in ErrorRecords
  and sets `$?` false on a successful exit (hence `Invoke-Capture` via `ProcessStartInfo`).
  `-f` formatting is culture-aware, so numbers go through an invariant `Num()`.
- **THE FOLDER A FILE SITS IN *IS* ITS LOUDNESS TARGET**, so filing is not bookkeeping — it
  silently decides how loud the sound ends up. `sort-audio` had `Torch_Flame` in
  `ambient_beds`, correct while the plan still assumed room-level fire and wrong once the
  crackle became positional; left there it would have authored every torch 2 LU quiet and
  presented as a mixing problem. `proximity` (-22) exists for positional loops: louder than
  a bed, which sits behind everything, because you walk right past the source.
- **A DYNAMIC-MODE FALLBACK IS WORSE FOR A LOOP THAN FOR A ONE-SHOT.** Two-pass loudnorm
  applies one constant gain, leaving the waveform — and the loop SEAM — untouched. When it
  cannot reach the target linearly it falls back to time-varying gain: a one-shot merely
  sounds squashed, a loop ticks audibly once per cycle forever, and you will hunt that as a
  bad loop point. Re-export a missed loop closer to target rather than pushing harder.
- Installing a new batch requires **Unity CLOSED**, same as §12's asset-move rule and for
  the same reason.

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
12b. ✅ **Kit sockets** (`KitSocketPlacer`) — `PropSocket` authored on WALL/CEILING/
    FLOOR kit pieces, so a piece declares where its fire, candles, sconce or hanging
    decor belong on its own geometry. Torch-claiming + spacing seed, per-room emissive
    tinting with no Light and no GameObject, floor-blocking guard. See §8.
    ⏳ open: per-kind alcove kit walls could use the same mechanism; an authored socket
    on a pit rim or lintel is untested.
13. ✅ Viewmodel overlay camera (depth-clear; kills weapon/shield clipping).
14. ✅ Physics interaction layer: push-open physics doors (+ audio), the
    `IPushable` push system (framerate-independent), crouch/sneak, and
    carrying/throwing with mass-driven encumbrance + ImpactAudio.
15. ✅ Head bob (footstep-locked; deepens with carry load).
16. ✅ Torch flame VFX tinted to the per-room torch palette.
16b. ✅ **DISTANCE-CULL GAMEOBJECT RENDERERS** (`DungeonRendererCulling`) — only the instanced
    path was culled, so a long sightline submitted every door, prop and torch in the dungeon:
    21ms frame against **2.8ms GPU**, CPU-bound on submission. Sliced `Renderer.enabled`
    toggling under the generated roots, covering doors, torches, prison props and room props
    together. Field verdict at depth 20: "huge increase in fps when looking into the mass of the
    dungeon." §5 has the measurements, why SHADOWS were the wrong suspect, and the three
    decisions that matter. ⏳ open: the same idea for the INSTANCED path's outline pass, where
    `RenderParams.shaderPass` could submit the outline to a shorter radius than the lit pass —
    low priority, that path is ~300 of 3242 draws.
17. ⏳ Atlas multi-material kit assets (walls/ceilings/arches → 1 material)
    — mostly Blender/texture work; toon shader packed-mask already ready. **Doors first**:
    190 of them at depth 20 at ~3 materials each is the single biggest asset-side contributor.
    **Multiply by two for the outline pass** (§6), and note the two fixes COMPOUND — a
    3-material door with an outline is 6 draws; atlased and outline-off it is 1.
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
22. **Player melee (`melee-v1-plan.md`)** — ✅ phases 1-2: procedural sword swing
    (`PlayerMelee` through `ViewmodelSway.SetAttackPose`) that HITs the combat core,
    + the FEEL layer (local swing-freeze/recoil, global `Hitstop`, `CameraKick`).
    ✅ **directional light combo** (LMB, cycles per-tap → varied blow directions →
    varied `NpcFlinch` profiles) + **heavy charge** (RMB hold-to-wind, release-to-
    swing, abort if released early, tension tremor) + **poise** (`Poise`, chip vs
    break → major stagger, anti-stunlock resistance window). ✅ **hit feel**: hit-
    retract-home (blade stops in the body and retreats instead of following through),
    shield counter-motion (off-hand derived from the sword, no per-swing authoring),
    vertical capsule sweep (short enemies). ✅ **phase 3** hit VFX/SFX via the SURFACE
    system (§ below) off `MeleeAttack.OnHitLanded`, + swing WHOOSH audio
    (`PlayerMeleeAudio` on `OnAttackSwung`, fired at slash-launch; distinct from the
    surface-driven impact sound so they layer). The SURFACE system is one shared
    `SurfaceLibrary` (SO) mapping `SurfaceType`→VFX/SFX, spawned through the single
    `SurfaceImpact.Spawn`; a `Surface` component tags exceptions (goblin = Flesh) and
    the world defaults to Stone. Every hit source routes through it — melee
    (`MeleeHitEffects`), THROWN/collided props (`SurfaceCollisionImpact`, on any hard
    impact, gated like `ImpactAudio` so a bouncing barrel doesn't spam), future
    projectiles — so "what an impact looks/sounds like" lives in one place.
    ✅ **phase 4 shield BASH** (`bashKey`,
    default Q): a HOLD-to-wind charged **lunge** — release fires a forward dash
    (`FirstPersonController.AddImpulse`, a decaying external velocity folded into the
    one `cc.Move`), an FOV widen→drop tell (world camera only; the overlay keeps its
    FOV), and a **cone shove** (`MeleeAttack.DoConeSweep`) that flings everyone in a
    forward cone along their OWN bearing (center back, flanks aside) with a guaranteed
    poise break — a control tool, not a damage tool. The sword counters the bash pose
    (mirror of the shield counter-motion). GOTCHA: the cone's OverlapSphere needs a
    BIG buffer (128) — each goblin carries its capsule + all dormant ragdoll bone
    colliders, so a crowd overflows a 16-slot NonAlloc buffer and drops most targets.
    ✅ **shield BLOCK and PARRY** (`IDamageMitigator` + `PlayerGuard`) — RMB guards, heavy
    moved to MMB. Hold = damage/knockback reduced at a poise cost; tap inside `parryWindow`
    = negated, no poise cost, attacker halted and poise-BROKEN. Blocked hits spark off the
    shield's surface and stop the attacker's swing dead. See §10 for the architecture and
    why mitigation lives on the victim.
    ⏳ still open: the no-shield WEAPON parry (same component, tighter window, worse chip
    mitigation — needs the loadout to report whether a shield is equipped); NPCs guarding
    (`IDamageMitigator` is faction-agnostic and would work as-is, but needs AI to decide
    when); non-damageable surface hits (wall/prop swing sparks).
    **Field lessons from tuning the light combo's HOLD-to-attack** (`GetMouseButton`
    instead of `GetMouseButtonDown`, so mashing isn't required to keep chaining):
    a buffered continuation must be **re-checked live at the moment of consumption**
    (`Input.GetMouseButton(...)` fresh, right then), never trusted from a stale
    per-frame latch — a latch captured from a HELD button (not just a fresh press)
    fires almost as soon as the ending window opens and doesn't know the button was
    released a moment later, so one extra swing snuck in after releasing LMB. The
    buffer CAPTURE itself stayed `GetMouseButtonDown` (tap-only) — buffering a tap is
    supposed to fire regardless of later button state (that's the point of a buffer);
    it's only the hold-continuation that needs the live re-check. The same overlap
    surfaced between throwing a carried prop and swinging: LMB throws, and if it's
    still held that same press also read as a swing input — fixed with
    `suppressLightUntilRelease`, latched on throw, cleared only on a full
    `GetMouseButtonUp`. **Equipped shield collider**: goblins could climb onto its
    solid `BoxCollider` (a `CharacterController` treats any solid collider as
    step-able geometry), worst after bash. Fixed by making it a TRIGGER rather than
    removing it — preserves `Physics.Overlap`/raycast queries for the still-open
    shield-block system above, while a trigger can't be climbed or block movement.
    ✅ **Bash v2 — it now physically MOVES things.** A narrow `InnerCone` nested inside the
    wide cone marks whoever is dead ahead for the **PLOW**: carried on the windshield for
    the lunge and flung at the end, rather than flung on contact (velocity-matched to the
    player's live `ExternalVelocity` with a weak spring toward a PLAYER-LOCAL capture
    offset, so turning the mouse swings them round with you — same "mostly physical,
    lightly corrected, never a rigid weld" philosophy as `PlayerCarry`'s hold point). The
    wide cone still fans the flanks aside, so one bash does both; plow angle 0 restores the
    old behaviour exactly. A separate `ConePush` cone **shoves PROPS** (`bashPropImpulse`),
    routed through `IPushable` so per-prop tuning and hinge torque still apply — bashing a
    physics door open falls out for free, and a barrel driven into a wall breaks itself via
    the existing `ImpactAudio` → `DestructibleProp` seam. See §10 for the four separate
    reasons props initially didn't move at all (impulse discarded by `Health`,
    `maxPushSpeed` eating one-shot blows, eye-height contact point becoming torque, and an
    impulse sized ~30× too small), and the NPC plow channel's rules.
    Order matters: **the shove runs BEFORE `TakeDamage`**, because `TakeDamage` can destroy
    the target inside that very call (`Health` fires `OnDied` synchronously →
    `DestructibleProp.Break` → debris + retire), so a push applied after lands on a body
    that is already gone.
    ✅ **Melee LOS self-occlusion** fixed (the target no longer occludes itself) +
    `MeleeReticle` (F6) reporting the real `CanHit` rejection reason — see §10. This
    had been costing genuine swings while reading as an aim problem.
22b. ✅ **Player ranged attack** — `PlayerLoadout` (1 = melee+shield, 2 = bow),
    `PlayerBow` (Animator-driven draw/release, unlike the procedural sword),
    `Arrow` (sticks to world and NPCs, follows rather than parents), `PlayerBowAudio`,
    and `Hitbox` weak points (headshots) + `DamageType.Projectile`. §10 has the detail
    and the field lessons: SetTrigger-on-a-Bool, post-bounce `linearVelocity`,
    `SetParent` under non-uniform bone scale, continuous collision above ~30 m/s.
    ✅ aim FOV zoom on the draw (see §10 — and the FOV-ownership caveat there).
    ⏳ open: a distinct headshot SFX/VFX off `Arrow.OnWeakPointHit` (the hook exists,
    nothing listens); arrow pickup/recovery; NPC archers reusing `Arrow`.
22c. ✅ **Destructible props** — `DestructibleProp` + `DebrisCleanup` for crates and
    barrels, damaged through `ImpactAudio.OnImpact` so sound and damage can't disagree.
    §8 has the fracture-collider and shrink-about-visual-centre lessons, plus debris
    velocity inheritance (the prop's own motion + the killing blow). ⏳ open: loot
    on destruction (the destruction event is the hook).
23. ✅ **NPC hit reactions v2** — directional spring flinch (`NpcFlinch`, authored
    per-angle profiles, orbit debug tool) for living hits; full blended ragdoll
    (`NpcRagdollReaction`, gravity-off flinch / gravity-on death) for death, opt-in
    for reactions. Routed by `NpcHitReactions`.
24. ✅ **Emotive NPC face** (`NpcFace`) — awareness-driven expression presets (the
    min/max ranges ARE the mood), eyes via the ToonLit rim-light trick, momentary
    hit shock vs sustained health-driven wounded face, permanent death face, and
    jaw ↔ vocalization sync. See §10.
25. ✅ **Fog-of-war automap** (`DungeonMapper`) — room-wholesale / corridor-drip
    reveal, grid-masked walls, door marks, one floor at a time with distinct
    stair/ladder/one-way-drop glyphs, room-type glyphs. See §10. Next passes if
    wanted: an authored tileset blitted per cell (the bitmask logic is unchanged —
    only the per-cell paint step swaps), and wiring it to a real UI `RawImage`.
25b. ✅ **Atmosphere shaders** — `Dungeon/ToonWater` (wells, fountains) and
    `Dungeon/GroundFog` (billboard ground mist, tinted from the torch palette via
    `RenderSettings.fogColor`). Both share ToonLit's conventions so they read as the
    same world; §6 has the authoring notes and the planes-vs-billboards lesson.
    ⏳ open: interactive fog that WAKES as the player walks through it (the compute-
    shader approach that started the conversation) — deliberately deferred, the cheap
    version looks good enough that the cost isn't justified yet.
    ✅ **`Custom/URP/TextureEmission`** — stained-glass windows: luminance-derived glow
    mask, per-window variation and flicker hashed from world position, grazing boost,
    distance fade, DepthOnly pass. Its two bugs are the general lessons: a custom shader
    on kit geometry must declare INSTANCING and fails silently otherwise (§5), and FOG IS
    NOT AUTOMATIC (§6).
    ✅ **ToonLit emission + flicker** — candles glow and burn per-instance, with a
    per-element id so one candelabra's flames don't pulse in unison (§6). The shader is
    the only place a `StaticDecor` piece can animate at all.
25c. ⏳ **Sound system** (`SOUNDSYSTEM_PLAN.md`; architecture in §10b). ✅ steps 1-6:
    `DungeonAudioMixer` + routing every source through `AudioBus`; `PlayerRoomTracker`;
    `AudioProfile` + per-space `RoomStyle` slots; `AmbientDirector` (beds, crossfade,
    positional one-shots via `OneShotAudioPool`, since `PlayClipAtPoint` cannot be mixed);
    and the VOICE BUDGET — `AudioCull`, `AudioPriority`, the F7 overlay, and the phantom
    `playOnAwake` fix that was the whole problem (189 playing / 84 stolen → 14 / 0 at
    double the target population). `Tools/normalize-audio.ps1` fixed a 38 LU spread across
    the library. ⏳ **step 6** reverb by space + footstep surfaces (extend `SurfaceType`
    with Gravel/Water) — DONE, see below.
    ✅ **step 6** — `AudioSpace` (the one space resolution, shared so ambience and reverb
    cannot disagree about which room you are in), `ReverbDirector` (computed from room
    size, driving a mixer BUS rather than a listener filter, in MILLIBELS not dB), and
    surface-aware footsteps that EXTENDED `SurfaceType` rather than forking it.
    ✅ **step 9 (occlusion)** — pulled forward ahead of 7/8, since it depends on neither and
    they are blocked on a content decision. Raycast-based, NOT the room graph; it promptly
    exposed three unrelated bugs, two of them shipping silently (see §10b).
    ⏳ **7** music stems + Snapshots + `TensionSend` — needs the lifecycle decision first
    (music survives a scene reload via `DontDestroyOnLoad`, ambience must NOT, and F1 /
    PgUp / the exit portal all reload), and a content decision: **layers that STACK or tracks
    that SWAP**. Vertical layering needs stems authored same-tempo/key/length and
    subset-complete (pad alone, pad+perc, and all three must each sound finished); most
    sourced music is a finished mix, and library "stems" usually means MIX stems (drums,
    bass, strings) which are not intensity layers. If it ends up finished tracks, the design
    becomes HORIZONTAL — crossfading Explore/Combat cues on the same `TensionSend` — which is
    legitimate but less responsive. `TensionSend` itself is identical either way, so build
    the dial first and let placeholder loops prove it. **8** ducking (needs 7).
26. ⏳ NPC AI remaining phases: **call for help** (shout = a loud `NoiseEvent`; the
    `NpcRegistry` and death cry already exist — rate-limit or it alert-loops);
    **disarm/rearm** (a dropped weapon is NOT a `Carryable`, whose `Interact()`
    hard-codes `PlayerCarry`) — the equipment system itself is now item 27;
    **NPC carry/throw** (extract `PlayerCarry`'s FixedUpdate drive
    into a shared `CarryDriver` — beware moving serialized fields, it silently
    resets the player prefab's tuning); **spawning** (depth-scaled `EnemyBudget` in
    DepthProfile, per-room-type `EnemySet` in RoomStyle, own placer on free hash
    stream 11007); optional **Unity Behavior** tree swap (install via Package
    Manager UI — never hand-pin a version).
    **POPULATION TARGET (decided, drives the perf work):** ~**25 active roamers**
    in hallways, with all other NPCs **stationary and DORMANT in rooms**, awakened
    when the player arrives. The 100-simultaneously-active stress tests are NOT the
    shipping target — dormancy is the real optimization, and it caps the active
    population by construction. **Dormancy must actually DISABLE `NpcLocomotion`**:
    that's what removes an NPC from the separation registry (`OnEnable`/`OnDisable`)
    as well as its `Controller.Move()` and agent sync. Dormancy that only stops
    pathing would leave the whole per-NPC cost in place.
27. ⏳ **NPC equipment / weapons** — `WeaponDefinition` SO + a hand-socket Transform
    (NOT `PropSocket`, which is the dungeon prop system) + `NpcEquipment` that picks
    one at spawn and pushes stats into `MeleeAttack`. Split the SO three ways: a
    shared COMBAT payload (damage/range/sweep/knockback/poise/`SurfaceType`), PLAYER
    presentation (`SwingDefinition` arcs — procedural), and NPC presentation
    (`AnimatorOverrideController` + grip offset). **NPC swing timing must come from
    an Animation Event, not `MeleeAttack.windup`** — a fixed delay can't match a
    per-weapon clip's impact frame, and `DoSweep()` is ALREADY decoupled from
    `TryAttack()`'s timer precisely so a driver can fire it on the exact frame (the
    player is the first consumer of that seam; animated NPCs are the second). Pick
    the weapon from a HASH STREAM, not `UnityEngine.Random` — loadout is part of
    spawning, which is deterministic (rule 4). No collider on the weapon (the sweep
    is its own cast). UPDATE: `MeleeAttack` IS now on the goblin and NPCs swing —
    animation-driven via `sweepFromAnimationEvent` (§10), which is the seam this item's
    per-weapon clips were always meant to use, so a `WeaponDefinition` supplies the clip and
    the impact frame comes with it. `engageDistance` must sit UNDER the weapon's `range` or
    the NPC stops outside its own reach and swings at air — the brain's own tooltip says so,
    and it is the first thing that goes wrong when a weapon's range changes.
    `RandomMeshSelector` is a temporary visual stand-in this supersedes.
28. ⏳ Atlas multi-material kit assets (walls/ceilings/arches → 1 material) —
    mostly Blender/texture work; toon shader packed-mask already ready.
29. ⏳ Home-base meta loop + depth progression tuning (portal-out at Exit → home
    base → depth increment → sell/replenish). Design chat first.
29b. ✅ **Corridor spatial variety** — the pass that made hallways worth walking. Wide
    PRISON cells via a 1x1 vestibule (a wide mouth on a corridor is geometrically
    impossible — the one-opening rule forbids it — so the cell widens BEHIND a doorway
    tile); **hallway ALCOVES** with per-kind contents; **junction PLAZAS** with optional
    central pillars reusing the interior-column system. See §3 stages 11-12, §4, §8, §9.
    Field verdict: "no longer just straight hallways."
    ⏳ open: per-kind kit walls/floors for alcoves; an arch on the alcove mouth (pay the
    `BuildArchways` + `FrameFace` pair cost together, §7, or posts land through arches).
    ✅ **ROOM PITS AND BRIDGES** — chasms across room floors with the space beneath carved
    out, a generator-owned bridge deck across and a ladder out (§4). Rooms only, structurally.
    The whole mechanism turned out to be ONE line in `NeedsSlabBetween`; the work was the four
    consumers that assumed a room cell has a floor (§12's category rule).
    ⏳ open: loot in pit bottoms (a natural hiding place, and `DestructibleProp`'s OnDestroyed
    hook is the model); fall damage (`DamageType.Fall` exists but is unwired); NPCs stranded
    in a pit rely on `NpcLocomotion.CheckFall` recovering them, which is verified-by-play
    rather than designed.
    ✅ **PRISON CONTENTS** — prisons take authored props from a WEIGHTED pool, so one cell
    holds a bunk and a bucket and the next a skeleton in chains. `AlcovePropPlacer` became
    `RecessPropPlacer` over both (§8), since the two were always the same primitive.
    ✅ **WALL VARIANT VARIETY** — per-asset `weight` (frequency) plus `ValueNoise` +
    `noiseRange` (clustering), which are different questions and needed different answers
    (§7). Damaged SECTIONS rather than an even sprinkle.
    ✅ **CRAWLWAYS** — 1.5m passages bored through rock between two places already connected but
    a long walk apart, with the far end CHOSEN by detour ratio rather than stumbled into (§4,
    `CRAWLWAY_PLAN.md`). Phase 1 sites them (cells stay `Empty`, so the stage is provably inert);
    phase 2 places the tube and the grate and suppresses the wall behind it, gated so an
    unauthored kit leaves them sealed rather than holed.
    ✅ **SEWER NETWORKS (v2)** — grown from the rock rather than bored between two chosen mouths,
    which made the "pointless shortcut between two hallways" inexpressible instead of merely
    rejected. Branching trees with chambers, a small budget of wall grates, and **one-way
    MANHOLES** down from prison floors. §4 has the design and the four bugs testing it found.
    ✅ **phase 3 — `CrawlwayGrate`**, the grate as an interactable. NOT a `PhysicsDoor` (a hinged
    door swinging into a 1.5m bore fills it, and the standoff jam is worse where there is no room
    to step back): you GRIP it and build strain until it gives, then it becomes a `Carryable`.
    `GrateMode` splits the two shapes — a **WallGrate** falls into the open cell, a **FloorCover**
    is hauled straight up, where "which way it falls" means nothing. The cover is resisted by a
    **camera PITCH TETHER**: you look up against it and the effort itself is the input, which is
    what made lifting read as lifting rather than as holding a button. `SetPitchTether` and
    `LookPitchDelta` join the frame-stamped family (`CameraKick.SetSustained`,
    `PlayerFov.AddOffset`, `SetSustainedVelocity`) — stop calling and it eases home, so an
    interrupted lift cannot leave the camera leashed, which is exactly how `SetAttackPose`'s LATCH
    once stuck a sword mid-pose. `LookPitchDelta` is also another INTENT reading (§10's
    `IMoveIntent`): it measures effort against something you are pressed against, where achieved
    pitch collapses to zero at the tether's limit.
    ✅ mouth faces recorded as DENIED and CLAIMED before `RecessPropPlacer`, so an alcove grate and
    its hero prop stop competing for the same wall; chamber openings roll for a grate; blank plates
    cap a manhole's unused faces; manholes no longer cluster in one prison.
    ⏳ open: an `AudioSpace` resolution for a bore (the one space with no room to measure, so tight
    and dry); `FootstepSurface` for the tube; dead-end crawlways holding a cache, which turns the
    rejection path into content; NPCs breaking grates (`CrawlwayGrate.Break()` is public for it,
    nothing calls it); the ONE-WAY VALVE idea in `CRAWLWAY_PLAN.md` — a chamber you drop into whose
    exit grate only opens from the inside.
    ⏳ open: the same clustering applied to FLOORS and ceilings — `ValueNoise` is already
    generic and the floor pick is the same uniform `Hash % length`; per-kind kit walls and
    floors for alcoves; an arch on the alcove mouth (pay the `BuildArchways` + `FrameFace`
    pair cost together, §7, or posts land through arches).
30. Later: lock-and-key on the MST (key tree-ancestral to lock; single-entrance
    doored rooms = lockable set), difficulty gradient by graph depth, equipment
    + SwayProfiles.

---

## 12. Working style that's kept this codebase coherent

- Small changes, tested in Unity between features, small git commits (repo:
  github.com/jhollyftc/DungeonCrawler — commit per feature, push after).
- **Review every diff** — the copy-paste workflow that preceded VS Code was a de
  facto review gate; keep reviewing when edits get frictionless.
- **MOVING ASSETS IS SAFE, AND THE SAFETY IS ENTIRELY IN THE `.meta`.** Unity resolves
  every reference through the GUID stored in an asset's `.meta`, never through its path —
  so a file moved TOGETHER WITH ITS META keeps every reference in every prefab, scene,
  ScriptableObject and project setting. Move one without the other, or let Unity
  regenerate one, and the GUID changes: for a script that turns every component on every
  prefab into `Missing (Mono Script)`, which is close to unrecoverable by hand.
  **The procedure that makes it a non-event** (the whole Assets tree was reorganized this
  way, ~1400 files, zero breakage):
  1. **Unity CLOSED.** A running editor can notice files vanish and regenerate metas
     mid-operation, which is exactly how GUIDs get lost.
  2. `git mv` the asset and its `.meta` in the same breath; for a FOLDER, move the
     folder's own `.meta` too or it loses its identity.
  3. **The invariant is NO GUID LOST** — collect every `guid:` from every `.meta` before
     and after and check nothing disappeared. That is exhaustive for this failure class,
     not a spot check. Do NOT demand an *identical* set: Unity legitimately ADDS folder
     metas for new directories on next open, and additions cannot break a reference.
  4. Batch by category, one commit each, so a disliked grouping reverts on its own.
  **What GUIDs do NOT protect:** `Resources.Load` and any other path-based load. This
  project has none (checked), and its one `AssetDatabase` use goes through
  `FindAssets`/`GUIDToAssetPath`, which is GUID-based — verify that again before any
  future move rather than assuming.
- **A DUPLICATE ASSET IS A TRAP, AND THE TIDY-LOOKING COPY MAY BE THE ORPHAN.** Two
  byte-identical textures sat at the Assets root AND in `Models/Textures` under the same
  names but with DIFFERENT GUIDs, so Unity saw four textures. Deleting the copy that
  looked misplaced would have broken `Material_Stairs.mat` — because the ROOT copy was the
  one actually referenced and the neatly-filed one was unused. Before removing an apparent
  duplicate, grep the project for BOTH GUIDs and see which is referenced; file position
  says nothing about which is live.
- **Commit a new script's `.meta` WITH it.** `DungeonMapper.cs` went in without its
  `.meta` and would have picked up a different guid on another machine, breaking every
  prefab reference — the same class of failure as the `EmissiveController`/
  `EmissionController` rename (rule 3). `git status` shows untracked `.meta` files
  right beside the script; they're easy to skip when staging by name.
- **ADDING A PROPERTY TO A SUBSET OF AN EXISTING CATEGORY MEANS AUDITING EVERY CONSUMER OF
  THAT CATEGORY.** A pit opening is a room cell in every respect the code can test — it is in
  `Room.Cells`, its `CellType` is `Room`, it sits at floor level — it simply has no floor. So
  every system that had quietly assumed "room cell ⇒ something to stand on" broke, and each
  broke SILENTLY and DIFFERENTLY. **FIVE consumers, found one at a time over several test
  rounds:** `RoomPropPlacer` dropped a chest into the void; `InteriorFloorCell` handed a
  floorless cell to the player spawn, the navmesh sample point and the path-debug endpoints;
  `PlanInteriorColumns` hung stacked segments over the chasm; `TorchPlacer` lit the inside of
  it; and the corner-post classifier posted the pit's wall corners. Five unrelated-looking
  bugs, one cause.
  **The count is the lesson.** Each one satisfied every structural test its system makes —
  "is this floor", "is this a wall corner", "is this a lattice point in a room" — and failed
  only the thing nobody had written down. So when you give a subset of an existing category a
  new property, the work is not the feature (pits were ONE line in `NeedsSlabBetween`); it is
  grepping for everyone who reads that category and asking whether the new property breaks
  their assumption. Budget for that, and expect to keep finding them after you think you are
  done. (`Room.Holes` / `PitAt` are the flags here; the same shape will recur for any future
  "room cell that isn't quite a room cell". Note the pillar classifier already carried an
  unconditional prison-entrance exclusion for the identical reason — when a system already has
  one special case of this shape, that is where the next one belongs.)
- **`gen.Doors` MEANS GRAPH-EDGE ENTRANCES, NOT EVERY OPENING — and a "doorway" is TWO cells.**
  Both halves of that cost a debugging round on sewer mouths. `RecordDoor` is called only from
  `CarveHallways`, so PRISON entrances are absent entirely (they are carved by `RecessFits` and
  never record one), as are alcove mouths and sewer grates — each lives in its own registry. And
  a door occupies `HallwayCell` AND `HallwayCell + Direction`, so collecting only the first
  measures a room-side position from across the threshold and it lands a cell nearer than it
  looks. The symptom was a grate beside a prison archway **however high the clearance was set** —
  raising it to 10 only "worked" because a Chebyshev box that size around an UNRELATED door
  happened to cover the spot, which is the tell for measuring against the wrong set rather than
  measuring too little. NB `HasDoor` is only a flag ON a recorded entrance (physical door vs
  open arch), so archways ARE in `Doors` and always were. `RoomPropPlacer` gathers thresholds
  from five separate sources by hand — that accumulation is the pattern, not an oversight.
- **DECIDE VIABILITY BEFORE MUTATING SHARED STATE; DO NOT BUILD A LONGER UNDO.** Sewer networks
  carved their chambers before the "can anyone get in" test, and a discarded network's undo
  reverted the grid and the cell registry but NOT the `crawlMouths` entries that chamber carving
  also adds. Sixty-four abandoned networks left ~200 phantom mouths in the world-spacing set,
  which then rejected real mouths on the networks that SURVIVED — every one ended up with
  exactly one mouth against a budget of four. **The tell was two numbers that should have
  matched and didn't**: 253 chambers reported carved while the finished networks held 14.
  Reordering so the survival test runs before anything carves left nothing to undo but the cell
  registry; extending the undo would have worked and stayed one forgotten line away from
  breaking again. Whenever a pass can ABANDON its work, count what it produced against what it
  kept — that comparison is what makes this class of leak visible at all.
- **A PASS THAT WRITES THE CELLTYPE IT READS WILL FEED ON ITSELF.** Both new corridor
  features hit this, one stage apart, and it is a property of the shape rather than a slip.
  **Alcoves** are typed `Hallway` (that's what gets them free walls and floors), so an
  alcove is a legal HOST for another alcove; carving during a single grid scan meant later
  flat indices saw fresh cells and chained recess off recess — branching tunnels, not
  niches. **Junction plazas** widen `Hallway` cells, and widening creates new junctions, so
  a live scan grows plazas outward indefinitely. Prisons are immune only by accident: they
  carve `Prison`, which their own one-opening rule then rejects. Two fixes, both needed:
  **snapshot the input set before mutating**, and **guard membership explicitly**
  (`IsAlcoveCell`, `plazaCells`). The failure looks the same either way — a creeping blob
  instead of a place — and is invisible until you crank the chance up.
- **A CONSTANT THAT IS CORRECT FOR ONE SYSTEM IS NOT PORTABLE TO ANOTHER WHOSE DENOMINATOR
  DIFFERS.** Alcove tuning burned several rounds on this, three times over, all from
  copying prison values: (1) `chance` was rolled per COMPASS DIRECTION off every corridor
  cell, but the directions running ALONG a corridor lead to more corridor — so half the
  rolls on a straight run were doomed before validation, and every one was tallied as "no
  room in the rock". A measured 100 rejections looked like a geometry problem and was an
  accounting one; rolling only against genuinely solid faces made `chance` mean what an
  author expects and doubled density at the same setting. (2) `alcoveDoorClearance` copied
  a value of 2, which is a CHEBYSHEV box — 25 cells around EVERY door, eating 45% of all
  sites. (3) The `[Range(0f, 0.5f)]` slider cap encoded the old per-direction denominator
  and silently prevented the fix from being tuned. When porting a working system's numbers,
  ask what the denominator IS, not what the value was.
- **INSTRUMENT BEFORE HYPOTHESISING — the tally beat the theory every time.** Across the
  alcove work: the per-rule rejection counter identified the real cause on all three
  rounds, while confident diagnoses (door clearance, then depth) were wrong twice, and a
  depth-shrink built on the second one moved rejections 100 → 101. Same pattern as the
  invisible-NPC hunt and the crowd jitter. A cheap counter that reports which rule fired
  beats any amount of reading the code. Corollary: **do not let the diagnostic assert its
  own conclusion** — the first version of that message claimed geometry was "usually"
  dominant and the very first real run disagreed; it now computes the dominant reason.

- **CODE WRITTEN FROM ONE ACTOR'S PERSPECTIVE BREAKS FOR THE OTHER — re-read it as the
  other actor before shipping.** `MeleeAttack` was built player-first and worked perfectly
  for years of player swings; the moment NPCs started attacking, TWO latent bugs opened at
  once (a 16-collider gather buffer sized for a player's one-or-two targets vs a goblin
  standing inside a crowd of ragdoll colliders, and an LOS test that excluded the victim
  but not the attacker, which only mattered because NPCs have no `aimSource` pushing the
  origin clear of their own chest). Neither produced an error; both present as "swings
  sometimes silently whiff". The same shape appeared in the bash work — buffers, contact
  points and impulse magnitudes all sized for the player's case. When a shared system gains
  a second kind of user, walk its assumptions from the new user's position deliberately;
  the defaults will be wrong in ways that never surface as exceptions.
- **`UnityEngine.Object` DEFINES AN IMPLICIT `operator bool`, SO ARGUMENT-ORDER
  MISTAKES INVOLVING A `bool` PARAMETER COMPILE SILENTLY.** It is the conversion that
  makes `if (obj)` work, and it means a `Material` passed positionally into a `bool`
  slot is a legal call, not the type error it would be in ordinary C#.
  `PropInstancer.PlaceProps` has `bool castShadows` sitting immediately before the
  two optional `Material`s, and a positional call bound `castShadows = replaceMat !=
  null` and shifted the pair one slot — the emissive swap was inert and shadow
  casting was driven by whether a tint existed, with no warning anywhere. **Pass
  arguments BY NAME whenever a signature puts a `bool` next to optional Unity-object
  parameters**, and be suspicious of any "why is this flag on/off" symptom in a call
  that mixes them. The general rule: C#'s type checker is a weaker safety net inside
  Unity than outside it, because `UnityEngine.Object` opts into a lossy conversion.
- **A DEFAULT WHOSE FAILURE IS INDISTINGUISHABLE FROM CORRECT WIRING IS THE MOST EXPENSIVE
  KIND OF BUG IN THIS PROJECT.** Four instances inside one work stream, every one presenting
  as "this setting does nothing" while everything visible looked right:
  an **unassigned mixer group** (bypasses the mixer entirely — straight to the listener, not
  to Master as the name suggests); **`playOnAwake` with a null clip** (a permanently "playing"
  silent voice); a **newly added mixer `Send`** (defaults to −80 dB, i.e. connected and
  delivering nothing); and a **missing `PlayerFov`** (a null check that skipped the effect
  without a word, found by raising a value from 6 to 100 and seeing no change).
  **The fix is never better documentation.** Three of the four WERE documented. What actually
  worked: remove the manual step (`PlayerFov.Ensure` finds-or-creates), or make the failure
  announce itself (`AudioBudgetDebug` flags `<unrouted>` sources; `ReverbDirector` warns once
  naming the exact unexposed parameter). When adding a system that can be half-wired, budget
  for one of those two rather than a line in a tooltip.
- **Two unrelated fixes in one file still get two commits.** Stage one, commit,
  restore the other, commit again — the history is what makes a field lesson findable
  later, and a combined commit buries one of them.
- **A CONFIRMED IMPROVEMENT IS NOT PROOF OF A COMPLETE DIAGNOSIS.** The invisible-NPC
  bug (§5) was declared fixed by `m_WarmupAsync: 0`, documented as settled, and kept
  shipping — the async flag was genuinely one real cause, and the visible improvement
  was enough to stop looking. The remaining half (a root bone, §10) was found only
  because `NpcPerceptionDebug` happened to draw a bar over an invisible goblin months
  later. When a symptom has a plausible cause AND a count-scaling that flatters that
  cause, look for a **discriminating test** rather than a confirming one — here, "does
  moving the camera bring it back?" would have separated the two in seconds. This is
  the same discipline that resolved the crowd jitter (setting smoothing to 0.99 and
  seeing NO change disproved the physics theory outright) and the separation perf work
  (zeroing `separationStrength` before optimizing anything).
- Design conversations (new systems, tradeoffs, "what's wrong with this
  screenshot") happen in the Claude chat interface; implementation and debugging
  happen here in the editor. Bring decisions in, take implementation out.
- The user makes all Blender assets and authors the ScriptableObjects; keep asset
  conventions stable (base-origin pivots, shared wall dimensions/facing, one-cell
  column segments) so new art drops into existing slots.
