# Crawlways — 1.5m crawl passages

A grate in a wall opens onto a 1.5m-square tunnel bored through solid rock, connecting two
places that are otherwise a long walk apart. The player crouches through; NPCs cannot follow.

Staged so each phase is independently testable and commits on its own. **Phase 1 needs no
art** — it proves siting works and draws the result as gizmos, exactly as the alcove plan
verified its premise before authoring content.

---

## 1. Core premise — crawl cells stay `CellType.Empty`

This is the sharpest difference from alcoves, and it decides everything else.

An **alcove** is typed `Hallway` so it inherits walls, floors and ceilings free from the kit
and mesher (§4). A **crawlway must not** — a 1.5m tube is not a 3m cell with thinner walls,
and every kit piece is authored to a 3m face. Typing crawl cells as anything open would emit
full-size masonry into a hole that is meant to be a bore through rock.

So crawl cells stay `Empty`. To the mesher, the kit placer, `NeedsSlabBetween`, the automap
and every `!= CellType.Empty` test in the project, a crawlway **does not exist** — the rock
is solid and stays solid. Identity lives entirely in `DungeonGenerator.Crawlways` +
`CrawlwayAt`/`IsCrawlwayCell`, the same registry shape as `Prisons`/`Ladders`/`ColumnPoints`/
`Alcoves`.

The consequence, and the whole cost of the feature: **the crawlway brings its own mesh and
its own collider.** Precedent is the bridge deck and the ladder — generator-owned kit pieces
placed at nominal grid coordinates, base-origin, with prefab colliders because the greybox
cannot provide them (§5).

**Verify this premise first** (Phase 1): generate with crawlways on, no prefabs authored, and
confirm the dungeon is byte-identical to one generated with them off. Nothing may change
until Phase 2 places geometry.

## 2. Where a crawlway begins and ends

The endpoints are the feature. The bore between them is trivial.

**The temptation to avoid:** boring blind and stopping wherever you break through. It needs
no pathfinder and produces worthless crawlways — a 4-cell tunnel surfacing in the same
corridor 12m from where you entered is a novelty, not a shortcut. You cannot tell a good one
from a bad one without knowing both ends, so **pick the pair first, then bore.**

A candidate pair (A, B) is legal only if:

| rule | why |
|---|---|
| bore length ≤ `crawlMaxCells` | pacing — `crouchSpeed` is 1 m/s, so each 3m cell is ~3 seconds |
| **a path A→B already exists** in the open network | the crawlway is NEVER load-bearing for connectivity |
| that path ≥ `crawlMinDetourRatio` × bore length | it is actually a shortcut |

One BFS over open cells answers both of the last two, and they are the two that matter.
**Path exists** is what keeps the feature non-load-bearing: if the BFS fails we have found
two disconnected regions, and joining them with a crawlway would paper over a generator bug.
**Path is long** is what makes a crawlway mean something, and it costs one comparison.

Same shape as `BuildGraph`'s `minLoopDetourRatio`, which scores loop edges by exactly this
ratio — worth reading before tuning.

**Which end is which.** Mechanically symmetric; narratively not.
- **A (discovery)** — off a hallway or a room's perimeter wall, somewhere the player walks.
- **B (arrival)** — weighted toward places otherwise expensive to reach. The stage runs last,
  so room types are already assigned and this costs nothing to consult.

**Dead ends are EXCLUDED, not deferred.** The cost of an out-and-back isn't the walk back, it's
the learned reluctance: one dead-end at depth 3 teaches the player that a grate is a coin-flip
costing thirty seconds of crouch-walking, and from then on they decline every grate they see.
The feature gets judged once and then skipped. A slow traversal that might not go anywhere is a
negative-expected-value gamble, and players stop taking those fast. So a crawlway always has two
mouths, and the secret hangs off the side of the run instead of terminating it.

## 2b. Sewer chambers

A full-height room carved in the rock, sealed except for one grate onto the tube — loot, or a
mob that cannot follow you out. It **hangs directly off a bore cell with no spur tunnel**, which
is what keeps `Cells` a list rather than a tree: branching costs an index and a direction, the
tee is a straight tube with a hole in one side rather than a 3-way junction, and the only
backtracking in the whole design is one step back into the tube.

**Reuses `RecessFits` wholesale.** Its host cell is the *bore* cell — solid rock — so the
one-opening rule ("touch no open cell but `h`") yields a fully sealed pocket for free. A prison,
an alcove and a sewer chamber turn out to be the same primitive with different hosts.

**Open: make the chamber a DROP.** Its floor a storey below the tube, so entering is a commitment
and the mouth gives no firing position down into the room — you're looking at a floor you have to
drop to. Suits "sewer", reuses the pit machinery for the climb-out, and it is the real fix for
the bow exploit rather than the partial one that `crawlwayMinCells` gives.

**Open, and worth not losing — the ONE-WAY VALVE (user's idea).** Only the grate leading *to* the
top of the chamber opens from the outside; the far grate is locked from the crawlway side. So the
route is forced: in through one mouth, drop into the chamber, out through the other, unlocking it
from inside as you go. That solves the backtracking and the firing-position exploit in a single
move, and it makes the crawlway a small self-contained puzzle rather than a corridor. It also
gives the lock-and-key work (roadmap 30) a natural first customer.

## 3. Geometry — one cell, one tube

**Dimensions:** cross-section 1.5 × 1.5, **length 3.0m** — one piece per grid cell. Not
1.5³. One closed tube asset; no separate floor/wall/ceiling pieces, because the tube is not
a room.

**Set:** straight, corner, mouth, dead-end cap.

**FLOOR-ALIGNED, NOT CENTRED.** A bore centred in the cell puts the sill 0.75m up, which
exceeds `maxStepHeight` (0.5) with no mantle mechanic — the player physically could not enter
their own crawlway. Floor-aligned leaves 0.75m of rock either side, 1.5m above, and **none
below**, which is where the validation rule comes from (§4). A ~0.15m sill is worth authoring
for visual readability; it is well under `stepOffset`.

**Origin conventions — the tube and the mouth differ, and this is §5's two-conventions trap
at close range.** The tube sits on the grid like a prop: **base-origin, NO
`globalVisualOffset`**, same as ladders, bridges and pit rims. The mouth must align with the
kit masonry it interrupts, so it is **kit-frame and the offset applies**. Getting either
backwards puts the piece a half-cell out.

## 4. Validation

`RecessFits` is **not** directly reusable — it builds a rectangular slab and is written for a
recess that becomes open. But its inner `CellOk` is almost exactly the crawl-cell predicate,
and the overlap is worth stating so the two do not drift:

- **cell is `Empty`** — same.
- **one-opening rule** — same, and it generalises perfectly: a crawl cell may touch no open
  cell except at its two mouths. That is what stops a bore grazing a room or running
  alongside a corridor with a cell of rock between.
- **solid below — REQUIRED, and for a concrete reason.** The tube's floor sits at the cell's
  base, `y * cellSize`. If the cell below were open, the mesher emits that space's ceiling
  slab at exactly the same plane and the two z-fight.
- **solid above — NOT required, deliberately.** An open cell above has its floor at
  `(y+1) * cellSize` while the tube's ceiling is at `y * cellSize + 1.5`, so there is 1.5m of
  rock between and no coincident geometry. **Crawlways may therefore run under rooms**, which
  is one of the better things they can do.

Plus crawlway-only rules: mouths not on or beside a `Doors[].HallwayCell`; spacing from other
crawlway mouths; the cell a mouth opens into must be walkable, and **not a pit opening** — a
pit opening is a room cell in every structural respect and simply has no floor (§12's category
rule), so a grate there opens onto thin air.

**Stairs and prisons are excluded as mouths.** Stairs because the sealed 13-cell envelope is
the most fragile geometry in the dungeon; prisons because their entire validation rests on
having exactly one opening and a grate would be a second. *A crawlway out of a prison cell is
a good idea and a deliberate v2* — it needs every prison consumer re-checked first.

**Alcoves ARE eligible, deliberately** — a grate at the back of a collapsed dig is the best
entrance the feature can have. It carries a known clash to resolve in Phase 2: `RecessPropPlacer`
puts an alcove's hero prop on the back wall, which is the same face. The fix is the existing
one, not a new one — claim the mouth face in `WallFaceRegistry` before `RecessPropPlacer` runs,
exactly as alcoves already claim before `TorchPlacer` (§8's most-constrained-first order).

## 5. Collision

Nothing exists inside solid rock, so the crawlway supplies all of it.

**The trap — suppressing a wall removes the whole 3m face.** The greybox emits one quad per
cell face. Suppress it to open the mouth and a **3m × 3m** collider is gone, not a 1.5m one,
and the player walks through the rock either side of the grate. **The mouth prefab must
restore the ring**: box colliders framing the bore and covering the rest of that face. That
puts a crawl mouth on §5's existing list of pieces whose real shape the greybox cannot
provide — a documented pattern rather than a new exception.

The alternative, teaching `DungeonMesher` to emit a holed quad, keeps collision truth in the
greybox and is philosophically tidier. Rejected: the mesher only knows whole faces, and that
is a real new capability in a system deliberately shared with the kit placer.

**The tube uses BOX colliders, not a MeshCollider** — four per straight (floor, ceiling, two
sides), an L of the same per corner. A hollow tube is non-convex, which is legal on static
geometry but PhysX prefers primitives, and boxes sidestep §10's build-only trap entirely
(a non-readable MeshCollider vanishes from a player build's navmesh silently).

**Suppression is a predicate consulted by BOTH the mesher and the kit placer**, the
`NeedsSlabBetween` pattern, so collision and visuals cannot disagree about where the wall is.

**Three details that would otherwise bite:**
- **`ceilingMask` must include the tube's layer**, or the controller's stand-up block never
  fires and the player stands up inside the bore and clips through rock.
- **NavMesh excludes itself.** At 1.5m nothing bakes walkable, so NPCs are kept out by
  geometry rather than by a rule anyone maintains — the escape-route behaviour for free, and
  the crawlway root does not even need to be in `excludeRoots`.
- **Carrying a prop through resolves itself.** `PlayerCarry` holds at 1.3m in front; a barrel
  wedges and `breakDistance` (2.5m) drops it. "You can't crawl carrying that" falls out of
  existing physics — worth deciding deliberately rather than discovering.

**Do not make the grate a `PhysicsDoor`.** A hinged door swinging into a 1.5m bore fills it,
and the standoff-jam problem (§10) is worse in a space with no room to step back. Swing it
outward into the room, or make it lift away — `HingedDoor` is the simpler precedent.

## 6. Pipeline position

`PlaceCrawlways()` appended **LAST** in `Generate()`, after `PlaceAlcoves()`.

- **Determinism:** nothing draws from `rng` after that point, so appending shifts no existing
  stream and every existing seed keeps its rooms, prisons, satellites, columns and alcoves.
- Running last means room types are assigned, which is what lets destination weighting (§2)
  cost nothing.
- Draw width/length/kind **unconditionally**, then shrink or reject — the draw count must
  never depend on how many attempts a site needed (`DungeonGenerator.cs:1403-1407`).
- **Self-hosting is a non-issue here**, uniquely: crawl cells stay `Empty`, so unlike alcoves
  (§12) a crawlway is not a legal host for another. The `IsCrawlwayCell` guard is still
  needed to stop two bores intersecting, but there is no creeping-blob failure mode.

## 7. Phases

**Phase 1 — generation + registry + gizmo.** No art, no geometry, no mesher change. The
dungeon must generate identically to crawlways-off. Draw bores and mouths as gizmos so siting
is tunable before a single asset exists. Includes the rejection tally (§12's
instrument-before-hypothesising rule — it beat the theory on all three alcove rounds).

**Phase 2 — geometry and collision.** `BuildCrawlways` in the kit placer patterned on
`BuildLadders`; `DungeonKit` tube/corner/mouth/cap slots; the wall-suppression predicate
shared by mesher and kit placer; root registered in `GeneratedRoots` (§5 — `DungeonAlcoveProps`
was missed when alcoves shipped).

**Phase 3 — contents and feel.** The grate interactable; `AudioSpace` resolution for a crawl
cell (tight, dry reverb — it is the one space with no room to measure); `FootstepSurface` for
the tube; optional cache at a dead end.

## 8. Risks

1. **The 3m-face suppression (§5)** — the one that ships a hole in the world if the mouth
   prefab's ring is wrong. Highest risk in Phase 2.
2. **Wall-face consumers.** Suppressing a face touches many readers — §12's category rule, so
   budget for finding them one at a time. Two are already reasoned through:
   - **`WallFaceRegistry` and everything downstream of it** (torch slots, `WallMounted` props,
     capped feature walls) is handled for free, because the kit placer skips the face *before*
     the emit — so a mouth face is never recorded and nothing can be dealt onto a wall with a
     hole in it.
   - **The corner-post classifier needs NO change**, and this is worth stating so nobody
     "fixes" it: posts are decided from grid solidity, not from what the kit emitted, and a
     mouth removes only the middle 1.5m of a 3m face. The wall corners genuinely still exist,
     so posts there are correct. This is *unlike* an archway, which is why `FramedOpening`
     exists for arches and is deliberately not extended here.
3. **Crawlway density.** Pair-finding is stricter than alcove siting, so the likely failure
   is zero crawlways rather than too many. The rejection tally is what makes that debuggable.
4. **Origin convention** on tube vs mouth (§3) — classic half-cell symptom.
