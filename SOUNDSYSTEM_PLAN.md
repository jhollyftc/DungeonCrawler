# SOUNDSYSTEM_PLAN.md — Dynamic Audio System

Companion to CLAUDE.md. Read that first — this assumes its conventions (golden
rules, RoomStyle/DepthProfile pattern, hash streams, house audio patterns) and
doesn't re-derive them. This is a design + implementation plan to hand to Claude
Code, not a finished spec — expect it to get edited as reality pushes back, same
as every other system in this project.

**Revision 2.** Changes from r1, all from a review against the actual codebase
rather than against the idea of it:

- Snapshots and exposed mixer parameters **cannot both drive one parameter** (§1).
- `footstepSurface` is gone — the project already has `SurfaceType` (§10).
- A shared **`PlayerRoomTracker`** is now its own build step, because the
  "which room am I in" query is already duplicated three times (§2).
- **Voice budget** is now a section. It was absent, and it is the constraint most
  likely to present as a combat bug rather than an audio one (§5).
- **Lifecycle** is now a section: the scene RELOADS on F1 and on every depth
  change, so music dies unless it's told not to (§6).
- Corridors get their own `AudioProfile` slot rather than sharing the
  unauthored-type fallback (§3).
- Occlusion no longer uses the room graph; the graph doesn't mean what it needed
  it to mean (§11).
- Reverb stores explicit floats, not an `AudioReverbPreset` enum, and covers
  corridors and pits (§9).
- `AmbientZoneController` resolved to a **manager**, not per-room components (§4).

---

## 0. What already exists — don't rebuild this

The codebase already has a working **house audio pattern**, applied consistently
by several components. Before writing anything new, know what's already there so
this plan extends it instead of duplicating it:

- **Continuous-vs-one-shot split**: a LOOPING sound whose volume/pitch track a
  live value (swing speed, angular velocity), plus separate one-shot stings for
  discrete events. Used by `PhysicsDoorAudio` (creak + thunk/slam),
  `HangingCageAudio` (creak + `ImpactAudio` clang) and `PlayerBowAudio` (draw
  creak + nock/loose one-shots).
- **Speed/impulse-driven volume+pitch**: `ImpactAudio` turns collision speed into
  loudness for free; `PlayerBowAudio` and `NpcCombatAudio` follow the same idea.
- **Audio split by SOURCE, not situation**: established explicitly for NPCs —
  `NpcMeleeAudio` (weapon whoosh, own component and own source) vs
  `NpcCombatAudio` (effort grunts, hurt, death — body noises, and the source
  `NpcFace` reads to drive the jaw). The question is always "what part of the
  world is making this sound," not "when does it happen."
- **Distance culling for voice slots**: `NpcMeleeAudio.cullDistance` already
  exists with the reasoning written down — inaudible sources still consume
  AudioSource slots at crowd sizes. §5 generalises this rather than inventing it.
- **Event hooks with no listener yet**: `ImpactAudio.OnImpact(position, loudness)`
  exists for NPC alerting and nothing consumes it. It is also the natural hook for
  **ducking** (§8) and for a **tension nudge** (§7) — one event, several listeners.
- **2D vs 3D by whose sound it is**: `PlayerMeleeAudio` is 2D (your own arm), NPC
  sources are 3D (a position you locate by ear). Music is 2D, ambient one-shots
  are 3D.
- **`Surface` / `SurfaceType` / `SurfaceLibrary`** — the existing answer to "what
  is this thing made of," built for melee hit VFX/SFX. **Footsteps extend this;
  they do not get their own parallel enum.** See §10.
- **`NpcFootsteps` already exists** alongside `PlayerFootsteps`. Any footstep work
  covers both, and the NPC side is where the per-step cost actually lands.

This plan's job is to (a) give all of the above a **Mixer** to route through
instead of raw `AudioSource.volume`, and (b) add the parts that don't exist yet:
ambient beds, adaptive music, reverb-by-room, and a tension signal.

---

## 1. Mixer hierarchy (Phase 1 — foundational)

```
Master
├── Music          (Explore / Combat / Boss — Snapshot-driven)
├── Ambient
│   ├── Base        (always-on low drone)
│   ├── RoomType    (per-AudioProfile bed layer)
│   ├── Proximity   (point-of-interest fade-ins)
│   └── OneShots    (drips/creaks/chitters — randomized timer pool)
├── SFX
│   ├── Footsteps
│   ├── Combat      (NpcCombatAudio, NpcMeleeAudio, PlayerMeleeAudio, PlayerBowAudio)
│   ├── Physics     (ImpactAudio, PhysicsDoorAudio, HangingCageAudio)
│   └── UI
└── Voice           (NPC barks, future)
```

Every existing `AudioSource` gets an `AudioMixerGroup` assignment matching this
table — a mechanical pass, no new components, and the prerequisite for ducking
and snapshots. Do it component-by-component so the diffs stay reviewable, since
it touches a lot of prefabs.

### THE RULE THAT WILL OTHERWISE COST A DAY: one driver per parameter

**Once `SetFloat` is called on an exposed mixer parameter, that parameter is
pinned and snapshot transitions no longer affect it** until `ClearFloat` is
called. So a parameter that is both exposed-and-scripted AND animated by a
snapshot silently stops responding to the snapshot the first time script touches
it. The symptom is "snapshot transitions work until they don't," which is
miserable to bisect.

Therefore, decided up front and not to be quietly violated:

| Parameter | Driver | Never |
|---|---|---|
| `MusicVolume`, `AmbientVolume`, `SFXVolume` | script (`SetFloat`, settings menu) | snapshots |
| music layer levels | **snapshots only** (§7) | `SetFloat` |
| `TensionSend` | script | must NOT be animated by any snapshot |

If continuous music control turns out to matter more than three discrete states,
**drop snapshots for the music stack entirely** and lerp exposed parameters
instead. Either mechanism is fine; mixing them on one parameter is not.

### dB, not linear

Exposed parameters are **decibels** (0 = unity, -80 = silence). A 0–1 UI slider
must go through `Mathf.Log10(Mathf.Max(v, 0.0001f)) * 20`. Assigning linearly
gives the classic "silent until 0.8, then blares" curve, and it will be blamed on
the mixer rather than on the mapping.

Keep the exposed set to those four until a real need adds more; an unbounded
exposed-parameter list is the mixer equivalent of scattering booleans everywhere.

---

## 2. `PlayerRoomTracker` — one answer to "which room am I in"

**New step, and it comes before ambient, reverb and footsteps because all three
consume it.**

That query is already implemented independently in three places —
`DungeonFogController`, `FirstPersonController.CurrentRoomLabel`, and
`DungeonMapper`. CLAUDE.md claims they "can never disagree" because they use the
same path, but they achieve that by COPYING the code, not by sharing it. This
plan would add three more consumers, taking it to six independent answers to one
question, and `RoomAt` is a linear scan over every room.

```
PlayerRoomTracker (MonoBehaviour, on the player or the visualizer)
├── CurrentCell   (Vector3Int)
├── CurrentRoom   (Room, null in corridors — that is meaningful, see §3)
├── CurrentPit    (PitSpec, null normally)
└── OnRoomChanged (event — ambient crossfade, reverb blend, music baseline)
```

Refactor the existing three onto it as part of this step, don't just add a fourth.
The point is not performance (though six scans a frame is silly) — it's that the
map, the fog, the ambient bed and the footstep surface must not be able to
disagree about where the player is standing. That is exactly the guarantee
CLAUDE.md already claims and doesn't structurally have.

**Publish `CurrentPit` too.** A pit is a room cell that isn't quite a room cell
(§12 of CLAUDE.md, the five-consumers lesson), and both reverb and ambient want to
treat it differently. Resolving it once here is cheaper than each system
rediscovering it.

---

## 3. AudioProfile — the RoomStyle of sound

Mirrors `RoomStyle` (§7 of CLAUDE.md) deliberately: same authoring pattern, same
fallback philosophy, same lookup point. **File name = class name** (Golden Rule
3) — `AudioProfile.cs`.

```
AudioProfile : ScriptableObject
├── ambientBaseLayer        (AudioClip, always-on drone — usually shared)
├── ambientRoomLayer        (AudioClip, per-type identity bed: drips/wind/crackle)
├── oneShotPool             (AudioClip[], randomized timer pool)
├── oneShotIntervalRange    (Vector2, seconds between one-shots)
├── reverb                  (ReverbSettings — explicit floats, see §9)
├── musicTensionBaseline    (0–1, floor for the tension signal here)
└── voicePriority           (int, see §5 — ambient must never be the voice stolen)
```

Note there is **no `footstepSurface`**. See §10.

### Resolution, and the corridor slot

One `AudioProfile` slot on `RoomStyle` itself, not a parallel dictionary keyed by
RoomType — `RoomStyle` is already the authored per-type asset every room looks up
for its visual identity, so this is one lookup instead of two.

`RoomStyle` gains **three** audio slots, not one:

- `audioProfiles` — per `RoomType`, same shape as the wall sets.
- **`hallwayAudioProfile`** — corridors and alcoves.
- `alcoveAudioProfiles` — keyed by `AlcoveKind`, same shape as `alcoveStyles`,
  falling back to the hallway profile.

**The corridor slot is separate from the unauthored fallback on purpose.** A
corridor is not an unauthored room type — `RoomAt` returns *null* there, and a
corridor is a real place with its own identity (dripping, distant wind, the
sound of the dungeon breathing). Folding it into `DefaultAudioProfile` means you
can never give corridors a deliberate voice without also changing the fallback for
every room type not yet authored.

This is the same shape as `RoomStyle.defaultTorchColor` versus the `Generic` room
type's own entry — two genuinely different concepts that look like one until
they're conflated, at which point "why are my hallways the wrong colour" costs an
afternoon. It already happened once. Don't repeat it in audio.

An unauthored ROOM TYPE falls back to a project-level `DefaultAudioProfile` (base
drone only, no room layer, no one-shots) rather than silence — matching
"incomplete authoring degrades gracefully," not "unauthored = nothing plays."

### Non-decision worth writing down explicitly

This system does **not** draw from the generator's seeded hash streams (Golden
Rule 4). One-shot timing and position are cosmetic, real-time and
per-play-session — they aren't part of what makes `(seed, depth)` reproduce the
same dungeon, and routing them through a hash stream would only risk shifting a
later pipeline stage for no benefit. Plain `UnityEngine.Random` is correct here.
If a future system ever needs audio to be part of the deterministic seed
(unlikely), that is a deliberate new decision, not an accident of reusing
whatever was convenient.

---

## 4. Ambient beds + one-shots — a MANAGER, not per-room components

**Resolved from r1's open question: one `AmbientDirector`, not an
`AmbientZoneController` per room.**

Three reasons, in order of weight:

1. **The voice budget (§5) needs a central owner.** You cannot allocate 32 voices
   from inside per-room components that don't know about each other.
2. **Per-room components are per-regenerate garbage.** Every root a placer creates
   must be listed in `DungeonVisualizer.GeneratedRoots` or it accumulates on F1 —
   `DungeonAlcoveProps` was missed once and went unnoticed. Audio accumulating is
   far worse than geometry accumulating: you *hear* five copies of a drone.
3. **Precedent.** `TorchCullingManager` is exactly this shape — many similar
   sources, distance-gated, sliced per frame, centrally budgeted — and it is a
   single manager, not a component per torch.

The director:

1. Owns a small **pool of looping sources** (base, room, proximity) that it
   retargets as the player moves, rather than one pair per room. Crossfades on
   `PlayerRoomTracker.OnRoomChanged`.
2. Runs a **single ticker** (not a coroutine per room) that every
   `Random.Range(oneShotIntervalRange)` seconds picks a clip from the active
   profile's `oneShotPool` and a position within the active room's floor cells.
3. Only considers **active rooms** — the player's room and its immediate
   neighbours. Distant rooms cost nothing, because they aren't iterated.

**One-shot positions must use the prop system's floor data** (`rz.Floor` /
`InteriorFloorCell`), not a random point in the room's bounds. A bounds-random
point can land in a pit opening, inside a wall, or in an L-room's bite. That is
precisely the category-audit failure §12 of CLAUDE.md is about — a pit opening
passes every "is this a room cell" test because it genuinely is one; it just has
no floor. `Room.Holes` is the flag.

**Proximity layers** (underground river, distant chanting) stay authored
per-instance: an `AmbientPointOfInterest` marker component with its own clip and
falloff radius, placed by hand or by a prop entry. Don't infer them from RoomType
— some throne rooms want chanting and some don't, and that's authoring, not a rule.

---

## 5. Voice budget — the constraint that will present as a combat bug

**Unity's default real-voice count is 32.** Beyond that, sources are virtualised
by priority and audible distance — they silently stop being heard.

The target population is ~25 active roamers (CLAUDE.md §11 item 26), each capable
of emitting footsteps, a combat voice line and a weapon whoosh simultaneously.
Add per-active-room ambient layers, a one-shot pool, proximity points, and three
music stems, and the budget is gone before any of it is tuned.

The failure mode is **hit sounds and footsteps dropping out during a busy fight**
— which gets investigated as a combat bug, because that's when it happens.

Rules, all of which have precedent in `NpcMeleeAudio`:

- **Every 3D source has a hard cull distance**, not just rolloff. Rolloff fades a
  source to inaudible while it still holds its slot.
- **Music and ambient are priority-pinned** (`AudioSource.priority` low number =
  high priority) so they are never the voices stolen. They're the bed; if they
  drop, everything sounds broken rather than busy.
- **Per-category allocation**, budgeted explicitly and written here once measured:
  a rough starting split is music 4, ambient 6, footsteps 6, combat 12, physics/UI
  the remainder.
- **Instrument before tuning** (§12): Unity's audio profiler shows real vs virtual
  voice counts live. Get a number from a real crowd fight before assigning the
  budget above — those figures are a guess and are marked as one.

---

## 6. Lifecycle — the scene reloads more than you think

`FirstPersonController.ReloadScene` (F1, PgUp/PgDn) and `DungeonExitPortal` both
call `SceneManager.LoadScene`. **Everything in the scene dies, including audio.**

- **Music must survive** — `DontDestroyOnLoad`, following `Hitstop`'s precedent.
  The depth transition is exactly the moment continuity matters most; a hard
  restart of the explore pad every time you descend reads as a bug.
- **Ambient must NOT survive** — it's per-room and must be rebuilt against the new
  generator.
- **The mixer asset persists on its own** (it's an asset, not a scene object), but
  any snapshot state or cached `AudioMixerGroup` reference held statically does
  not survive play-mode exit cleanly.
- **Any static audio manager needs a `RuntimeInitializeOnLoadMethod(SubsystemRegistration)`
  reset**, exactly as `NoiseBus`, `NpcPerceptionDebug` and `EmissiveMaterialVariants`
  already do. A statically cached mixer reference or "current snapshot" would
  otherwise survive a play-mode exit and hand out stale state — the fast-enter-
  playmode trap this project has now hit three times.
- **One `AudioListener` only.** The player rig runs a base camera plus a URP
  overlay camera for the viewmodel; the listener belongs on the base camera.
  `DungeonPlayerSpawner.HandleOtherCameras` already had to learn to skip cameras
  the player rig owns — check the listener doesn't get duplicated or disabled by
  the same path.

---

## 7. Adaptive music (vertical layering + Snapshots)

Stems, not tracks:

- **Layer 0 — Pad**: always on when music is audible at all.
- **Layer 1 — Percussion**: fades in as tension rises above the room's
  `musicTensionBaseline`.
- **Layer 2 — Melodic/combat**: fades in on real engagement.
- **Boss**: a separate cue with its own Snapshot, hard-triggered on boss-room
  entry — not a layer of the explore stack.

`TensionSend` is a single float (0–1), same "one dial, never several that can
drift apart" discipline as `PlayerCarry.CarryLoad01`.

### Feed it from `Awareness01`, not from `CurrentTarget`

The obvious source is "how many NPCs are hunting me." **Don't use it yet.**
CLAUDE.md documents a known gap: `NpcPerception.TickSight` sets `CurrentTarget`
the instant the view cone + LOS test passes, *regardless of `Awareness01`*, and
`NpcBrain` interrupts straight to Alerted on that. So combat music would slam on
the moment any goblin glimpses the player, including in the over-sensitive cases
that gap already causes. Hysteresis fixes flapping at a threshold; it does not fix
over-triggering.

Instead, sum **`Awareness01` across nearby NPCs** (the `NpcLocomotion` separation
registry is already the living-NPC list, so this needs no new bookkeeping). That
degrades gracefully, gives a rising-dread curve rather than a binary flip, and is
what you want musically regardless. If the sight gating is ever fixed — it's a
deliberate open design call, not a bug awaiting a fix — target count becomes a
legitimate second term.

Combine terms with a weighted max plus the room's `musicTensionBaseline` floor —
not a new state machine.

`ImpactAudio.OnImpact` is a second natural input: a loud impact should nudge
tension even with no NPC nearby, because the PLAYER heard it.

**Snapshots** `Explore` / `Combat` / `Boss`, transitioned via `TransitionTo` on
tension crossing a threshold with hysteresis. Per §1, `TensionSend` itself is
script-driven and must not also be a snapshot-animated parameter.

---

## 8. Ducking

Route through Send/Receive + the Duck Volume effect, not manual volume math:

- **Impact duck**: `ImpactAudio.OnImpact(position, loudness)` ducks Ambient and
  Music briefly, scaled by loudness. Same event §7 uses for tension — one hook,
  two listeners, not two parallel "something loud happened" signals.
- **Combat duck**: NPC hit/death barks duck Music's pad slightly so they read
  clearly.

Trivial once §1 and §7 exist; mostly Send/Receive configuration, not code.

---

## 9. Reverb per space (computed, not hand-placed)

No hand-placed Reverb Zones — room bounds are already known at generation time,
so derive reverb the way the pit and lintel systems derive geometry from existing
data rather than new authoring.

### A MIXER BUS, not a listener filter — corrected from r2

r2 said apply reverb via an `AudioReverbFilter` on the **AudioListener**. That is
wrong, and building the mixer made it obvious: **a filter on the listener processes
the ENTIRE final mix.** The music would reverberate with the room, and so would the
player's own 2D sounds — sword whoosh, bow creak, carry grunt. Those are in your
hands, not in the room.

The mixer expresses it correctly:

- A **Reverb** group under Master, chain **`Receive` → `SFX Reverb` → `Attenuation`**.
- **Send** effects on **SFX** and **Ambient**, targeting that Receive.
- **Music never sends**, so it stays dry by construction.

**Chain order is signal order, top to bottom.** A `Receive` placed BELOW the reverb
gets its audio processed only by effects beneath it — i.e. none — so the sends arrive
and leave dry, and the bus looks correctly wired while doing nothing. (This is exactly
what happened on the first attempt.) Likewise a `Send` above `Attenuation` is
pre-fader, so muting that bus still feeds the reverb; put sends after it.

Per-category wetness falls out for free: ambient beds can sit wetter than combat hits
just by differing send levels.

### Store floats, not a preset enum

Two enum values cannot be interpolated, which would make "blend between a small room
and a great hall" literally unimplementable. So `AudioProfile.reverb` is an explicit
struct — decay time, room, room HF — matching the parameters exposed on the mixer's
`SFX Reverb` effect. (`AudioReverbFilter` had a second reason: it ignores manual
parameters unless its preset is `User`. Moot now, but worth knowing if a per-source
filter is ever wanted.)

### Size

Interpolate between a small-room and a great-hall setting by room size, driven by
`SetFloat` on the exposed reverb parameters and blended on
`PlayerRoomTracker.OnRoomChanged`. Per §1's rule those parameters are script-driven,
so no snapshot may also animate them.

**`Room.Cells.Count` is a VOLUME, not an area** — `BuildFootprint` writes a room's
footprint at every Y within its bounds, so a two-storey room counts double. That is
*correct* for reverb and should not be "fixed": a tall hall really does sound bigger
than a low one of the same floor area. Written down here because it looks like a bug.

### Corridors and pits need their own settings

§6 of r1 covered rooms only, leaving corridors on whatever the last room set. The
two most acoustically distinctive spaces in the dungeon are exactly the ones that
weren't rooms:

- **Corridors** — tight, slapback, short decay. From `hallwayAudioProfile` (§3).
- **Pits** — the deep hole you can't see the bottom of. `PlayerRoomTracker.CurrentPit`
  (§2) is published precisely so this is a lookup rather than a rediscovery.

Per-profile settings override the computed value; computed is the fallback, not the
only option, matching the fallback philosophy everywhere else.

---

## 10. Footsteps and surfaces — extend `SurfaceType`, don't fork it

**r1's `AudioProfile.footstepSurface` enum is removed.**

`Surface.cs` already defines `SurfaceType { Stone, Flesh, Bone, Wood, Metal, Cloth }`,
with `Surface.Of` resolving it by walking up from a struck collider, and
`SurfaceLibrary` mapping type → VFX/SFX. r1 proposed a second enum
(`Stone / Gravel / Water / Bone / Wood`) that overlaps it, adds two values and drops
two. Two enums that both answer "what is this made of" will drift — that is §12's
category lesson, and the divergence would show up as a wooden bridge that sparks like
stone under a sword but thuds like wood underfoot.

It is also the **wrong granularity**. Surface is a property of the CELL, not the room
type. `RoomStyle` already carries `hallwayFloorPrefabs`, `pitFloorPrefabs` and
`prisonFloorPrefabs` separately, and bridge decks are wood. A per-room-type enum
cannot express "you just stepped onto a wooden bridge over a pit," which is one of
the most distinctive moments the generator produces.

**Instead:**

1. Add `Gravel` and `Water` to `SurfaceType`.
2. Resolve footsteps from a **short downward ray + `Surface.Of`** at each footfall —
   the same lookup melee already uses. A wooden bridge then sounds wooden with zero
   extra authoring, because the bridge prefab already wants a `Surface` for sword hits.
3. `SurfaceLibrary` gains a footstep clip pool per type, beside its impact entries —
   one asset answering "what does this material do when struck AND when walked on."
4. Round-robin + ±5–10% pitch jitter, matching `ImpactAudio`'s retrigger discipline
   for the same "same sound N times in a row" problem.

**Covers `NpcFootsteps` as well as `PlayerFootsteps`** — they already both exist, and
the NPC side is where the per-step raycast cost actually lands at 25 roamers. Hook
both off the existing `OnStep` event; do not add a second grounding check
(`PlayerFootsteps` already carries the coyote-time fix for stair descent, and
duplicating it would reintroduce the bug it solved).

---

## 11. Occlusion (lowest priority — and NOT via the room graph)

**r1 proposed reusing the Delaunay/MST room graph. That graph does not mean what
this needs it to mean.** It encodes "a corridor was carved between these two rooms,"
not "sound can travel between them." Two rooms sharing a wall are usually *not*
graph-connected; two graph-connected rooms may be thirty metres apart through winding
corridor. Using it would muffle the room you can practically hear through the wall,
while passing sound freely down a corridor that should attenuate it — wrong in both
directions.

The instinct to piggyback on generator data is right; this is the wrong dataset.

**Cheap version that matches the house pattern:** one raycast from listener to source,
run on a **per-source stagger** exactly as `NpcPerception` staggers its sight ticks,
feeding a lowpass + attenuation multiplier. Staggering is what makes it affordable;
per-frame raycasts for every source is what doesn't fit.

Keep this last regardless. It's only worth building once enough concurrent sources
exist that bleed-through is a noticed problem rather than a theoretical one.

---

## 12. Build order

Mark done inline as this progresses, same convention as CLAUDE.md §11.

1. [x] **Mixer asset + group hierarchy** (§1). Route every existing `AudioSource`
   to its group. No new behaviour — pure plumbing; verify nothing changed
   volume-wise. Establish the one-driver-per-parameter table before anything reads
   it.
2. [x] **`PlayerRoomTracker`** (§2) — *new step.* Extract the room lookup and
   refactor `DungeonFogController`, `FirstPersonController` and `DungeonMapper` onto
   it. No audio yet; this is a prerequisite refactor and should be verified by
   nothing changing.
3. [x] **`AudioProfile`** (§3) + `RoomStyle` slots (per-type, hallway, alcove) +
   `DefaultAudioProfile`.
4. [x] **`AmbientDirector`** (§4) — base + room layers first; one-shot pool and
   proximity points as a follow-up once the crossfade is stable. Root registered in
   `GeneratedRoots` if it creates one.
   **Built as a manager watching the RESOLVED PROFILE, not `OnRoomChanged`** —
   corridors, alcoves, prisons and pits all have `CurrentRoom == null`, so that event
   fires for exactly one of the five spaces the game has. `OneShotAudioPool` was
   needed because `PlayClipAtPoint` creates a hidden object with no group and
   therefore cannot be mixed at all.
5. [x] **Voice budget instrumentation** (§5) — measure a real crowd fight, then set
   the per-category allocation. Do this BEFORE music stems, so the budget is known
   before more sources are added to it.
   **Measured: peak 14 voices, 0 stolen, at 50 NPCs mid-fight — double the target
   population.** So the per-category allocation below stays THEORETICAL; there is no
   pressure to divide a budget with better than 2x headroom, and #7's stems will not
   trouble it. The measurement found something else entirely: 186 phantom voices from
   `playOnAwake` with a null clip (see §5 and CLAUDE.md §10b). Culling and priorities
   were built first against a plausible misreading of the numbers, and were not the
   fix — they are kept on their own merits.
6. [x] **Reverb by space** (§9) **+ footstep surfaces** (§10) — both consume #2;
   do them together.
   `AudioSpace` extracted from `AmbientDirector` so ambience and reverb cannot
   disagree about which space you are in. **The reverb parameters are MILLIBELS, not
   dB** — the r2 draft's values were authored as dB and would have left every space
   fully wet; see §9. Footsteps EXTENDED `SurfaceType` rather than forking it, as
   §10 required. Field-tuned: the room population is bimodal (closets ~2 cells,
   ordinary rooms 60-150), so the size blend spans 10..165 and closets clamp.
7. [ ] **Adaptive music stems + Snapshots + `TensionSend`** (§7) — scaffold with
   placeholder loops; needs the `DontDestroyOnLoad` decision from §6 in place.
8. [ ] **Ducking** (§8).
9. [ ] **Occlusion** (§11) — lowest priority, reworked premise.

---

## 13. Open questions

Resolved from r1 and recorded rather than deleted, so the reasoning survives:

- ~~Per-room component or manager?~~ **Manager** (§4). The voice budget needs a
  central owner, per-room roots accumulate on regenerate, and `TorchCullingManager`
  is the existing precedent for this exact shape.
- ~~Global tension smoothing or per-room?~~ **Global constant.** Don't pre-add a
  per-room override nobody has asked for.

Still open:

- **Music stems: authored or sourced?** Affects only whether #7 starts with real
  content. Placeholder loops are enough to prove the `TensionSend` plumbing, so this
  doesn't block anything — but it's the longest lead-time item, so decide early.
- **Does the boss cue need its own mixer group**, or is it a Music-group cue with its
  own snapshot? Defer until there is a boss.
- **Should the glow of a stained-glass window imply an ambient source?** A lit window
  is the only "outside" the dungeon has. Possibly an `AmbientPointOfInterest` on the
  wall prefab via the kit-socket system (CLAUDE.md §8) rather than anything new —
  sockets already place authored things at authored positions on kit pieces, and an
  audio emitter is just another child prefab.
