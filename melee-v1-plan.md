# melee-v1-plan.md — Player melee (sword swing + shield block/bash)

## Context

The NPC combat core already exists and works: `MeleeAttack`'s sweep, `Health`/
`IDamageable`/`DamageInfo`, and `NpcHitReactions` (knockback, stagger scaling with
impulse, per-bone flinch, death). Goblins already take and react to damage. So the
*hitting* is done — **this plan is about the player's swing and, above all, the
FEEL**: the sword catching on a body mid-swing, the crunch of a landed hit versus
the whiff of cutting air, camera kick, sparks, impact audio, and a shield that
blocks and bashes.

Decisions taken: **procedural swing** (no rig — code-driven arc, matching the
project's procedural viewmodel philosophy; `ViewmodelSway.proceduralWeight` was
built for exactly this), and **shield block + bash**.

## Guiding principle (a hard codebase rule)

**One system writes the viewmodel transform.** The codebase has repeatedly been
bitten by two systems pushing a pose independently and oscillating — it's why
`ViewmodelCollision` is invoked from the END of `ViewmodelSway.LateUpdate`, not its
own. The swing pose obeys the same rule: the melee driver **hands its pose to
`ViewmodelSway`**, which composes one final transform: `rest → sway → attack pose →
collision clamp`. The melee code never touches `transform.localPosition` directly.

A nice consequence: the collision clamp still runs during a swing, so you can't
swing the sword through a wall — the blade retracts against geometry for free.

---

## The pieces to compose (already built)

- **`MeleeAttack`** — the sweep (CheckSphere→Overlap fallback, SphereCastAll,
  dedupe-by-root, facing check). Written player-agnostic. Needs two small changes
  (below) so the player can drive its *timing* and aim from the camera.
- **`ViewmodelSway`** (per hand) — owns each held item's pose; `proceduralWeight`
  already earmarked to suppress sway during a swing. Gains an attack-pose hook.
- **`Health` / `DamageInfo`** — the player already has `Health` + `FactionMember =
  Player`, so goblin swings already hurt the player. Blocking needs one mitigation
  hook.
- **`NpcHitReactions`** — stagger already scales with knockback impulse, so a
  high-impulse shield bash produces a long stagger with no new code.
- **`Dust VFX`** (just added) — the pattern/pipeline for a hit-spark VFX.

---

## Changes to existing files (small, surgical)

**`MeleeAttack.cs`** — split timing from the cast, add camera aim:
- Extract `public bool DoSweep()` — the actual cast + damage, returns whether it
  hit. `TryAttack()` stays for NPCs (`Invoke(DoSweep, windup)`). The player drives
  the swing itself and calls `DoSweep()` at the animation's impact moment, using
  the return to fire hitstop/feel.
- Add optional `Transform aimSource` (null = use own transform, unchanged for
  NPCs). The player sets it to the camera, so the sweep originates at the eye and
  follows pitch — you slash where you *look*, not just where the body faces.

**`ViewmodelSway.cs`** — attack-pose injection:
- `public void SetAttackPose(Vector3 posOffset, Quaternion rotOffset, float swaySuppress)`
  — the melee driver calls this each frame of a swing. Composed AFTER sway, BEFORE
  the collision clamp: `swayedPos = rest + posOffset(sway·(1-suppress)) + attackPos`.
  Zeroed when no swing is active, so idle behavior is byte-identical.

**`Health.cs`** — a mitigation hook (enables block; reusable for armor, NPC shields
later):
- Before applying damage, consult an optional `IDamageMitigator` on the object:
  `info = mitigator.Mitigate(info)`. Keeps `Health` generic. `PlayerBlock`
  implements it.

---

## New files

- **`IDamageMitigator.cs`** — `DamageInfo Mitigate(in DamageInfo info)`. A pre-apply
  transform of incoming damage.
- **`PlayerMelee.cs`** — input + swing/bash state machines. Drives the procedural
  poses through `ViewmodelSway.SetAttackPose`, calls `MeleeAttack.DoSweep()` at the
  impact instant, and on a landed hit fires the feel layer (hitstop, camera kick,
  spark, audio). Owns both hands (sword swing, shield bash).
- **`PlayerBlock.cs`** — block state + `IDamageMitigator`. Raises the shield pose
  while held, mitigates frontal incoming damage, plays the block clang + a heavier
  camera kick on a blocked hit.
- **`Hitstop.cs`** — a static global time-dip manager. `Request(duration, scale)`
  coalesces overlapping requests (takes the strongest), restores on **unscaled**
  time. One owner of `Time.timeScale` so nothing fights over it. Resets its static
  state on play-mode entry (the fast-enter-playmode trap, as `NoiseBus` does).
- **`CameraKick.cs`** — additive rotational punch (+ small positional shake) on the
  player camera, spring-damped back to zero. Runs in LateUpdate AFTER the
  controller's pitch and the player `HeadBob`, composing the same additive-and-
  restore way (never fights them). `Kick(pitch, yaw, roll)` from melee/block events.

*(Swing shape lives in inline `AnimationCurve` + pose-key fields on `PlayerMelee`
for v1 — tunable in the inspector without code. Extract to a `MeleeWeapon`
ScriptableObject when equipment/phase-6 lands; it's the same data a
`WeaponDefinition` will hold.)*

---

## The swing (procedural)

Normalized swing time `t: 0→1` over `windup + active + recovery`, mapped to a
pos/rot offset arc via authored `AnimationCurve`s (so feel is inspector-tunable):
- **Windup** (`t` 0→~0.35): sword pulls back/up, and `swaySuppress` ramps to 1
  (sway hands off to the swing).
- **Active slash** (~0.35→0.6): fast diagonal arc. **Impact at a single authored
  `impactT`** (~0.45) — the frame `DoSweep()` fires. A swing whoosh plays as the
  active phase starts.
- **Recovery** (~0.6→1): eases back to rest; `swaySuppress` ramps back to 0.

**Whiff vs hit divergence IS the feel.** A whiff completes the full fast arc
smoothly and quickly. A hit does not — see hitstop.

---

## The feel layer (the heart of the request)

**1. Hitstop — the "sword gets stuck" feel.** Two complementary freezes, both keyed
off `DoSweep()` returning a hit:
- **Local swing freeze (the catch):** stop advancing the swing's `t` for
  `localHitstop` (~0.08–0.12s). The blade literally halts at the contact pose mid-
  slash — this IS "it gets stuck before the animation completes." Then a small
  **recoil** (bounce back a few cm/deg, as if meeting resistance) before recovery.
  This contrast against a smooth whiff is the whole game feel.
- **Global time dip (the crunch):** `Hitstop.Request(~0.05s, ~0.1 scale)` — briefly
  freezes the WHOLE scene (goblin flinch, particles, physics) for a crunchy beat,
  then restores on unscaled time. Keep SHORT — a long dip stalls physics and reads
  as a hitch. Default subtle; scale both freezes by hit "weight" (damage/impulse)
  so a solid hit crunches and a glancing one barely pauses.

**2. Camera kick.** A quick rotational punch on the landed hit (and a tiny one on
swing start for weight), direction reflecting the slash, spring-returning. FIRST-
PERSON kick must be much smaller than third-person or it nauseates — subtle and
tunable, hard caps on magnitude.

**3. Hit spark + audio** (on `DoSweep` hit, at `DamageInfo.point`):
- Spark: a short VFX-Graph burst oriented to the hit direction (the `Dust VFX`
  pipeline). One-shot, pooled or self-destroying.
- Audio: a flesh/impact one-shot at the contact point (3D). The goblin ALSO plays
  its `NpcCombatAudio` hurt grunt — they layer, no conflict. A separate metallic
  **clang** for a blocked hit. A **whoosh** on the swing itself (even a whiff has
  weight) pitched by swing speed.

---

## Shield (block + bash)

Input: **hold RMB = block; press a bash key (default F / MMB) = shield bash.**
Distinct inputs keep it unambiguous (binding tunable).

**Block (hold RMB):** raise the shield into a guard pose via the shield hand's
`ViewmodelSway.SetAttackPose`; set `PlayerBlock.IsBlocking` + a block facing (camera
forward). `PlayerBlock : IDamageMitigator`: an incoming hit from within a frontal
arc (`dot(hitDir, -camForward) > cos(blockArc/2)`) is reduced by `blockReduction`
(default heavy, ~85%), knockback cut, and it plays the clang + a heavier camera
kick (you FEEL the block land). Hits from outside the arc pass through unmitigated —
positioning matters. (Stamina cost is a v2 knob; note but don't build.)

**Bash (press bash key):** a forward shield thrust pose + a `DoSweep()` with shield
params — **low/no damage, high knockback, high stagger**. Because `NpcHitReactions`
stagger already scales with impulse, a high-impulse bash gives a long stagger and a
real opening, with no new reaction code. Bash has its own windup/recovery and a
cooldown; it can't be spammed. A bash while blocking drops the block briefly.

---

## Phasing (each independently testable)

1. **Swing + hit** — procedural sword swing, `DoSweep()` at `impactT`, camera-aimed.
   Goblins take damage/stagger/die. No feel yet. *Test: swing, goblin reacts.*
2. **Core feel** — local hitstop freeze + recoil, global time dip, camera kick, all
   on hit and scaled by weight. THE headline. *Test: hit vs whiff feel divergence.*
3. **Hit VFX + audio** — spark at contact, flesh/whoosh/impact sounds.
4. **Shield block** — raise, frontal mitigation, clang + kick. *Test: a goblin
   swing while you block is mitigated; from behind it isn't.*
5. **Shield bash** — forward shove, high stagger/knockback opening. *Test: bash
   staggers a goblin long enough to follow with a sword hit.*

## Risks

- **Two systems writing the pose → oscillation.** Mitigated by routing the swing
  pose THROUGH `ViewmodelSway` (the established rule).
- **Global hitstop vs physics.** `Time.timeScale` slows `FixedUpdate`; keep the dip
  short and restore on unscaled time. Mid-swing doors/props slow too (acceptable,
  even good). One owner (`Hitstop`) prevents fights.
- **Camera-kick nausea.** First-person melee kick must be subtle, spring-damped,
  hard-capped; composes additively with `HeadBob` + pitch, restoring each frame.
- **Collision clamp truncating swings near walls.** The clamp retracting the blade
  against geometry is desirable, but a swing pressed against a wall will be cut
  short. Acceptable; note during tuning.
- **Block needs reliable incoming hits.** Goblin `MeleeAttack` already targets the
  player's `Health` (FactionMember-gated) — verified working in phase 4.

## Verification

Per phase above, in the editor with a live goblin (K/L dev keys and thrown barrels
already exist for setup). The decisive one is phase 2: swinging at air should feel
fast and clean, swinging INTO a goblin should feel like the blade bites and holds
for a beat — if a hit and a whiff feel the same, the feel layer has failed and the
hitstop/recoil numbers need work before anything else is added.
