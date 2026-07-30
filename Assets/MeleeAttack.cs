using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A melee swing: windup → active sweep → recovery. A capability component —
    /// NpcBrain calls TryAttack() and listens to events; nothing here decides WHEN
    /// to attack. Built NPC-first but deliberately player-agnostic: the eventual
    /// player melee is this same component with its sweep origin on the camera.
    ///
    /// The sweep carries ViewmodelCollision's hard-won lesson — a cast that STARTS
    /// inside a collider reports nothing, and melee range means you're usually
    /// already touching the target — plus three differences an attack needs:
    /// CheckSphere→Overlap fallback, SphereCastAll (a swing can clip two targets,
    /// a single cast returns only the nearest), and dedupe by root so a
    /// multi-collider victim takes ONE hit per swing.
    ///
    /// Damage/range/timing are inline fields for now; Phase 6 moves them onto a
    /// WeaponDefinition and this component reads whatever is equipped.
    /// </summary>
    [DisallowMultipleComponent]
    public class MeleeAttack : MonoBehaviour
    {
        [Header("Weapon (inline until WeaponDefinition lands in phase 6)")]
        [Tooltip("Damage per landed hit.")]
        public float damage = 10f;
        [Tooltip("Reach of the sweep (m), measured from the origin forward.")]
        public float range = 1.6f;
        [Tooltip("Radius of the sweep (m) — how 'wide'/precise the swing is. The sweep is a vertical CAPSULE, so a THIN radius here still catches short AND tall enemies (the height is covered by the extents below). This is the precision dial you can shrink freely.")]
        public float sweepRadius = 0.45f;
        [Tooltip("Vertical reach ABOVE the aim line (m). The sweep is a vertical capsule so height coverage is separate from radius.")]
        public float sweepUpExtent = 0.3f;
        [Tooltip("Vertical reach BELOW the aim line (m). Enemies are SHORT and the eye-height origin sits above them, so this must reach DOWN to their body — ~1.2-1.4 catches a goblin from eye level even when you're barely looking down.")]
        public float sweepDownExtent = 1.3f;
        [Tooltip("Knockback impulse (m/s) delivered to victims.")]
        public float knockback = 5f;
        [Tooltip("Poise damage per hit — chips the victim's poise pool (Poise component). Enough at once = a poise break → major stagger. Light attacks chip; heavy/bash break in one.")]
        public float poiseDamage = 25f;

        [Header("Timing")]
        [Tooltip("Seconds between starting the swing and the hit landing — the victim's dodge window. THE combat-feel number.")]
        public float windup = 0.45f;
        [Tooltip("Seconds after the hit before another swing can start.")]
        public float recovery = 0.8f;

        [Header("Sweep")]
        [Tooltip("Height above the feet the sweep originates from.")]
        public float originHeight = 1.1f;
        [Tooltip("What the sweep can HIT (victims' colliders). Exclude this NPC's own layer or it can clip itself.")]
        public LayerMask hitMask = ~0;
        [Tooltip("Blocks a hit if anything on this mask sits between the attacker and the victim — a wall or shut door defeats the swing even though the capsule sweep geometrically reached past it (CapsuleCast/Overlap test volume intersection only, not solid occlusion, so a goblin on the far side of a thin door was hittable with no LOS check at all). Should be WORLD geometry only; the NPC and Viewmodel layers are auto-stripped in Awake so a crowd can't block its own hits and the held weapon model can't self-occlude.")]
        public LayerMask losBlockMask = ~0;
        [Tooltip("How far short of the victim's surface the line-of-sight ray stops (m). The ray's endpoint would otherwise sit exactly ON the collider, making the hit a floating-point coin flip — the cause of a swing failing dead-centre but landing a few pixels off. Small: a couple of centimetres.")]
        public float losSurfaceSkin = 0.05f;
        [Tooltip("Optional: aim the sweep from this transform (position + forward) instead of the body. The PLAYER sets this to the camera so you slash where you LOOK — pitch included. NPCs leave it empty and sweep from the body.")]
        public Transform aimSource;
        [Tooltip("With an aimSource, start the sweep this far forward of it — pushes the origin out of the attacker's own capsule so the point-blank CheckSphere tests the TARGET, not your own chest.")]
        public float aimForwardOffset = 0.45f;

        [Tooltip("World-space direction of the BLOW — what the victim recoils along. Set per-swing by the driver (PlayerMelee derives it from the actual slash motion, so a diagonal cut pushes diagonally and a future left-slash pushes right). Zero = fall back to the aim/forward direction. This is the seam for directional attacks → directional reactions.")]
        [HideInInspector] public Vector3 blowDirectionOverride;

        [Tooltip("Log swings, hits, and whiffs.")]
        public bool debugAttack = false;

        /// <summary>Swing started (windup begins). Drive animation/audio windup from this.</summary>
        public event Action OnSwingStart;
        /// <summary>Swing finished (hit or whiff, recovery begins).</summary>
        public event Action OnSwingEnd;
        /// <summary>A hit landed on a victim.</summary>
        public event Action<IDamageable, DamageInfo> OnHitLanded;
        /// <summary>
        /// The swing found no living target but connected with the WORLD — a wall, door,
        /// prop — the "whiff sparks off the surface" moment. point/dir/collider let a
        /// listener spawn a surface-appropriate effect (MeleeHitEffects), matching the
        /// visual language of a landed hit. Fires at most once per swing, and only when
        /// nothing was damaged (a real hit already sells contact on its own).
        /// </summary>
        public event Action<Vector3, Vector3, Collider> OnEnvironmentHit;
        /// <summary>
        /// A cone-sweep target landed inside the optional INNER cone: (victim, blow
        /// direction). Its shove was reduced to `InnerCone.impulse` instead of the full
        /// knockback, so the listener owns what happens next — the bash carries it on the
        /// windshield and flings it at the end. Fires alongside OnHitLanded, not instead
        /// of it, so hit VFX/audio still treat it as an ordinary hit.
        /// </summary>
        public event Action<IDamageable, Vector3> OnConeInnerHit;

        /// <summary>
        /// A narrow sub-cone for DoConeSweep — see its `inner` parameter. `range` 0 or
        /// `halfAngleDeg` 0 disables it, which is the default, so an unaware caller gets
        /// exactly the old behaviour.
        /// </summary>
        public struct InnerCone
        {
            public float halfAngleDeg;
            public float range;
            /// <summary>Shove applied on capture. Keep it small — the point is that the real
            /// knockback is deferred to the release; a large value here fights the carry.</summary>
            public float impulse;

            public bool Active => range > 0f && halfAngleDeg > 0f;
            public float CosHalf => Mathf.Cos(Mathf.Clamp(halfAngleDeg, 1f, 179f) * Mathf.Deg2Rad);
        }

        /// <summary>
        /// The PROP shove of a cone sweep — see ShoveProp. Its own range/angle rather than
        /// the damage cone's, and `impulse` is an IMPULSE (N·s), NOT a velocity like
        /// `knockback`. `impulse` 0 disables it, which is the default.
        /// </summary>
        public struct ConePush
        {
            public float halfAngleDeg;
            public float range;
            /// <summary>Impulse magnitude (N·s) handed to IPushable. Mass turns this into
            /// speed via dv = J/m, so one value covers every prop weight.</summary>
            public float impulse;

            public bool Active => impulse > 0f && range > 0f && halfAngleDeg > 0f;
            public float CosHalf => Mathf.Cos(Mathf.Clamp(halfAngleDeg, 1f, 179f) * Mathf.Deg2Rad);
        }

        public bool IsSwinging { get; private set; }
        public bool CanAttack => !IsSwinging && Time.time >= readyAt && !Suppressed;
        /// <summary>Set true to block attacks (stagger, death, disarm-in-progress).</summary>
        public bool Suppressed { get; set; }

        float readyAt;
        int landedThisSwing;   // victims actually damaged (hitThisSwing also counts walls)
        bool envHitFound;      // this swing touched a non-damageable solid — the first one found
        Vector3 envHitPoint;
        Collider envHitCollider;
        Faction ownFaction;
        InnerCone activeInner;   // set per DoConeSweep call, read by ConeHit
        ConePush activePush;     // ditto, read by ShoveProp
        readonly HashSet<Transform> hitThisSwing = new HashSet<Transform>();
        static readonly Collider[] overlapScratch = new Collider[16];
        static readonly RaycastHit[] castScratch = new RaycastHit[16];
        // The cone sweeps a MUCH larger volume than the sword capsule, and each enemy
        // brings many colliders (its capsule + all the dormant ragdoll bone colliders),
        // so a crowd blows past 16 instantly — a too-small buffer silently caps the
        // OverlapSphere and drops most goblins. Give the cone plenty of room.
        static readonly Collider[] coneScratch = new Collider[128];

        void Awake()
        {
            ownFaction = FactionMember.Of(transform);

            // The LOS mask must be world geometry only: an NPC layer entry would let a
            // crowd block its own attacks on each other, and Viewmodel (the held
            // weapon model, right in front of the camera) would self-occlude every
            // player swing. Auto-stripped so it works without hand-tuning the mask,
            // same convention as PlayerInteractor's cast mask.
            int npc = LayerMask.NameToLayer("NPC");
            if (npc >= 0) losBlockMask &= ~(1 << npc);
            int viewmodel = LayerMask.NameToLayer("Viewmodel");
            if (viewmodel >= 0) losBlockMask &= ~(1 << viewmodel);
        }

        /// <summary>Begin a swing. Returns false if still recovering/suppressed.</summary>
        public bool TryAttack()
        {
            if (!CanAttack) return false;
            IsSwinging = true;
            OnSwingStart?.Invoke();
            Invoke(nameof(LandSweep), windup);
            if (debugAttack) Debug.Log($"[Melee] {name}: swing started (lands in {windup:0.00}s).", this);
            return true;
        }

        // NPC path: the timed landing of a TryAttack() swing.
        void LandSweep()
        {
            IsSwinging = false;
            readyAt = Time.time + recovery;
            DoSweep();
            OnSwingEnd?.Invoke();
        }

        /// <summary>
        /// The actual cast + damage, decoupled from TryAttack's windup timing so
        /// the PLAYER can drive its own swing animation and fire the sweep at the
        /// exact impact frame. Returns true if at least one victim took damage —
        /// the feel layer (hitstop, camera kick) keys off that.
        /// </summary>
        public bool DoSweep()
        {
            hitThisSwing.Clear();
            landedThisSwing = 0;
            envHitFound = false;

            Vector3 origin;
            Vector3 dir;
            if (aimSource != null)
            {
                dir = aimSource.forward;
                origin = aimSource.position + dir * aimForwardOffset;
            }
            else
            {
                dir = transform.forward;
                origin = transform.position + Vector3.up * originHeight;
            }
            // Point-blank case: a cast that STARTS inside the victim's collider
            // reports nothing, so if a DAMAGEABLE target overlaps the origin we
            // must overlap-query instead. The test is deliberately narrow — a
            // damageable, non-self root only. Two earlier versions were wrong:
            // CheckSphere always saw the attacker's OWN capsule, and 'any non-self
            // collider' let a nearby WALL force the short-range branch (in a
            // cramped dungeon that's constant, and proximity to a wall must not
            // shorten your sword — walls don't need the fallback, they're not
            // damageable).
            // The sweep is a VERTICAL CAPSULE, not a sphere: a thin radius still
            // covers short-to-tall enemies because the height is the capsule's
            // length, not its radius. A sphere on the eye-height aim line skimmed
            // OVER short goblins unless the radius was fat.
            Vector3 top = origin + Vector3.up * sweepUpExtent;
            Vector3 bottom = origin - Vector3.up * sweepDownExtent;

            bool touchingTarget = false;
            int probe = Physics.OverlapCapsuleNonAlloc(top, bottom, sweepRadius, overlapScratch, hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < probe; i++)
            {
                // Same identity rule as TryHit: the damageable, never transform.root
                // (parented NPCs all share the dungeon root).
                var d = overlapScratch[i].GetComponentInParent<IDamageable>();
                if (d == null) continue;
                if (d.Transform == transform || d.Transform.IsChildOf(transform)) continue;
                touchingTarget = true;
                break;
            }

            // The BLOW direction (what victims recoil along) is separate from the
            // sweep AIM (hit detection + facing). A diagonal slash aims forward but
            // pushes diagonally; the driver sets blowDirectionOverride from the real
            // swing motion. Cleared after the sweep so it never leaks to the next.
            Vector3 blowDir = blowDirectionOverride.sqrMagnitude > 0.0001f ? blowDirectionOverride.normalized : dir;

            int hits;
            if (touchingTarget)
            {
                hits = Physics.OverlapCapsuleNonAlloc(top, bottom, sweepRadius, overlapScratch, hitMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++) TryHit(overlapScratch[i], origin, dir, blowDir);
            }
            else
            {
                hits = Physics.CapsuleCastNonAlloc(top, bottom, sweepRadius, dir, castScratch, range, hitMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits; i++) TryHit(castScratch[i].collider, origin, dir, blowDir);
            }

            blowDirectionOverride = Vector3.zero;

            // No living target, but the blade caught a wall/door/prop — spark off IT
            // instead of connecting with nothing. Only when nothing was damaged: a real
            // hit already sells contact on its own, and a swing shouldn't double up.
            if (landedThisSwing == 0 && envHitFound)
                OnEnvironmentHit?.Invoke(envHitPoint, blowDir, envHitCollider);

            if (debugAttack)
                Debug.Log($"[Melee] {name}: sweep [{(touchingTarget ? "overlap" : "cast")}] saw {hits} collider(s) → {(landedThisSwing > 0 ? $"{landedThisSwing} HIT" : "whiff")}.", this);
            return landedThisSwing > 0;
        }

        /// <summary>
        /// A CONE shove — every valid target in a forward cone is pushed along its OWN
        /// bearing from the attacker (radial), not one shared direction: an enemy dead
        /// ahead flies straight back, one on the flank is flung out to the side. That's
        /// the "part the crowd" shield-bash feel. Reuses the same faction/dedupe/
        /// OnHitLanded plumbing as DoSweep (so per-enemy hit VFX still fire). Uses the
        /// component's current damage/knockback/poise/range — the driver pushes those
        /// first, exactly like a normal swing. Returns victims hit.
        /// </summary>
        /// <param name="halfAngleDeg">Half the cone's opening angle (55 ≈ a 110° fan in front).</param>
        /// <param name="sideBias">0 = everyone shoved straight forward; 1 = everyone flung fully radially away from the attacker (max spread).</param>
        /// <param name="continueSweep">
        /// Keep the previous call's dedupe set instead of starting fresh. This is what
        /// makes a CHARGE-THROUGH bash work: the caller sweeps every frame while the
        /// lunge carries the player forward, so each enemy is caught at the moment the
        /// player actually reaches them (a rolling shove down the charge path) rather
        /// than everyone in a single snapshot cone at one instant — while the shared
        /// dedupe still guarantees one hit each across the whole window. Returns victims
        /// hit BY THIS CALL, so a per-frame caller can tell when someone new was caught.
        /// </param>
        /// <param name="inner">
        /// Optional NARROW sub-cone nested inside the main one. A target inside it takes the
        /// hit with `inner.impulse` instead of the full `knockback` and is reported through
        /// `OnConeInnerHit`, so the caller can do something SUSTAINED with it instead — the
        /// shield bash's plow, where whoever is dead ahead is carried along on the
        /// windshield and flung at the end rather than immediately. Default = disabled, and
        /// the sweep behaves exactly as it did before this existed. This component knows
        /// nothing about plowing: it only knows "inner cone means reduced shove, and tell
        /// the caller".
        /// </param>
        /// <param name="push">
        /// Optional PROP shove — crates, barrels, tables, doors caught in the same sweep.
        /// Own range/angle, and its `impulse` is an impulse, not a velocity. Default =
        /// disabled. See ShoveProp for why it routes through IPushable.
        /// </param>
        public int DoConeSweep(float halfAngleDeg, float sideBias, bool continueSweep = false,
                               InnerCone inner = default, ConePush push = default)
        {
            if (!continueSweep) hitThisSwing.Clear();
            landedThisSwing = 0;
            activeInner = inner;
            activePush = push;

            Vector3 origin, dir;
            if (aimSource != null) { dir = aimSource.forward; origin = aimSource.position + dir * aimForwardOffset; }
            else { dir = transform.forward; origin = transform.position + Vector3.up * originHeight; }

            // Flatten the aim: a crowd shove pushes along the FLOOR (short enemies flung
            // back, not slammed down through it), and bearings are compared on the plane.
            Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
            flatDir = flatDir.sqrMagnitude > 1e-4f ? flatDir.normalized : dir;
            float cosHalf = Mathf.Cos(Mathf.Clamp(halfAngleDeg, 1f, 179f) * Mathf.Deg2Rad);

            // Broad-phase sphere padded vertically: the origin is at eye height but enemies
            // are short, so a floor target at the cone's edge is diagonally further than
            // `range` — gather generously, then gate the true reach on FLAT distance below.
            // Gather out to whichever cone reaches furthest, or a prop push configured wider
            // than the damage cone would be silently truncated by the broad phase.
            float gather = Mathf.Max(range, activePush.Active ? activePush.range : 0f) + 2f;
            int n = Physics.OverlapSphereNonAlloc(origin, gather, coneScratch, hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                ConeHit(coneScratch[i], origin, flatDir, cosHalf, range, Mathf.Clamp01(sideBias));

            if (debugAttack)
                Debug.Log($"[Melee] {name}: cone sweep saw {n} collider(s){(n >= coneScratch.Length ? " (BUFFER FULL — raise coneScratch)" : "")} → {(landedThisSwing > 0 ? $"{landedThisSwing} HIT" : "whiff")}.", this);
            return landedThisSwing;
        }

        /// <summary>
        /// The PROP half of a cone sweep: a crate, barrel, table or door caught in the shove.
        /// Its own narrower cone (see ConePush), because the wide cone is a shockwave that
        /// staggers PEOPLE — moving objects several metres away reads as telekinesis.
        ///
        /// The force is delivered as an IMPULSE through IPushable, which is what makes mass
        /// work for free: PushableProp (and the plain-Rigidbody fallback) apply it with
        /// ForceMode.Impulse, so dv = J/m and a light barrel launches while a heavy table
        /// barely shifts, with no per-prop tuning. Routing through IPushable rather than
        /// touching the Rigidbody directly also keeps the house split intact — the attacker
        /// supplies force, the object decides what it means — so a PhysicsDoor turns this
        /// into hinge torque (a linear force would fight the joint and tear it off) and a
        /// PushableProp still applies its own multiplier and speed cap.
        ///
        /// NB the impulse is a different QUANTITY from `knockback`, which is a VELOCITY for
        /// NPCs. Feeding one number to both would be a units error presenting as "props
        /// barely twitch" or "props explode", depending entirely on their mass.
        /// </summary>
        void ShoveProp(Collider c, Vector3 origin, Vector3 flatDir, float sideBias, bool alreadyDeduped = false)
        {
            if (!activePush.Active) return;

            Rigidbody body = c.attachedRigidbody;
            // Kinematic covers static scenery AND a living NPC's dormant ragdoll bones,
            // which NpcRagdollReaction parks kinematic until death.
            if (body == null || body.isKinematic) return;

            Transform root = body.transform;
            if (root == transform || root.IsChildOf(transform)) return;

            // A self-mover handles being shoved through its OWN channel (knockback into
            // NpcLocomotion), and driving its Rigidbody as well would fight that. Testing for
            // the CharacterController rather than for an AI component keeps this ignorant of
            // the AI layer — the question is "does this thing move itself", not "is it a
            // goblin".
            if (root.GetComponent<CharacterController>() != null) return;

            // Skipped when the damage path already claimed this root — see the call site in
            // ConeHit. A prop can be BOTH damageable and pushable (a barrel has Health for
            // DestructibleProp AND a Rigidbody), so the shove can't be gated on the absence
            // of the other.
            if (!alreadyDeduped && hitThisSwing.Contains(root)) return;

            Vector3 to = root.position - origin;
            Vector3 toFlat = new Vector3(to.x, 0f, to.z);
            float dist = toFlat.magnitude;
            if (dist > activePush.range) return;
            Vector3 toDir = dist > 1e-3f ? toFlat / dist : flatDir;
            if (Vector3.Dot(toDir, flatDir) < activePush.CosHalf) return;

            // Same LOS rule as the damage path: a shut door between you and a crate in the
            // next room shouldn't let the bash shove it.
            Vector3 point = c.ClosestPoint(origin);
            if (Physics.Linecast(origin, point, losBlockMask, QueryTriggerInteraction.Ignore)) return;

            if (!alreadyDeduped && !hitThisSwing.Add(root)) return;

            // Same radial blow the damage path uses, so props and people fan out together
            // instead of the shove visibly disagreeing with itself.
            Vector3 blow = Vector3.Slerp(flatDir, toDir, sideBias).normalized;

            // Apply through the CENTRE OF MASS HEIGHT, not the raw surface point. The sweep
            // origin is at eye level and props are low, so ClosestPoint lands near a barrel's
            // TOP RIM — and PushableProp defaults to AddForceAtPosition, which turns that
            // lever arm into mostly TORQUE. The barrel tips over in place instead of being
            // launched, which reads as "the shove did nothing" while still making a contact
            // noise. Keeping the lateral offset preserves some spin (a shoved barrel should
            // tumble); only the vertical lever arm is removed.
            Vector3 shovePoint = new Vector3(point.x, body.worldCenterOfMass.y, point.z);

            IPushable pushable = body.GetComponent<IPushable>();
            // PushBurst, not Push: a one-shot blow must bypass maxPushSpeed, which exists to
            // tame the PER-FRAME contact push and otherwise eats the bash entirely once the
            // capsule has already nudged the prop past the cap. See IPushable.PushBurst.
            if (pushable != null) pushable.PushBurst(shovePoint, blow, activePush.impulse);
            else body.AddForceAtPosition(blow * activePush.impulse, shovePoint, ForceMode.Impulse);

            if (debugAttack)
                Debug.Log($"[Melee] {name}: cone SHOVED '{root.name}' — {activePush.impulse:0.#} N·s " +
                          $"on mass {body.mass:0.#} = {activePush.impulse / Mathf.Max(0.01f, body.mass):0.0} m/s " +
                          $"(before the prop's own pushMultiplier/maxPushSpeed).", this);
        }

        void ConeHit(Collider c, Vector3 origin, Vector3 flatDir, float cosHalf, float range, float sideBias)
        {
            if (c == null) return;

            var damageable = c.GetComponentInParent<IDamageable>();
            // Not a living thing — it may still be a PROP worth shoving. Deliberately an
            // else-branch, never a fall-through from the damage path's early returns: a DEAD
            // NPC's ragdoll bones are non-kinematic Rigidbodies, and letting a rejected
            // damageable reach the push code would have the bash fling corpses apart by
            // their individual bones.
            if (damageable == null) { ShoveProp(c, origin, flatDir, sideBias); return; }

            Transform root = damageable.Transform;
            if (root == transform || root.IsChildOf(transform)) return;   // never hit yourself
            if (hitThisSwing.Contains(root)) return;                      // already resolved this root this swing
            if (FactionMember.Of(root) == ownFaction) return;
            if (damageable.IsDead) return;

            // Bearing FROM the attacker, on the floor plane.
            Vector3 to = root.position - origin;
            Vector3 toFlat = new Vector3(to.x, 0f, to.z);
            float dist = toFlat.magnitude;
            if (dist > range) return;                                 // true reach is the FLAT distance
            Vector3 toDir = dist > 1e-3f ? toFlat / dist : flatDir;   // right on top of us → straight ahead

            if (Vector3.Dot(toDir, flatDir) < cosHalf) return;        // outside the cone

            // Line of sight — same reasoning as TryHit: a wall/door between the
            // attacker and this target defeats the bash even though it's geometrically
            // inside the cone (a shut door beside the player shouldn't shove someone
            // standing in the next room through it).
            if (Physics.Linecast(origin, c.ClosestPoint(origin), losBlockMask, QueryTriggerInteraction.Ignore))
                return;

            // Dedupe LAST, only once this collider has passed every rejection check.
            // The broad cone query returns EVERY collider on a goblin — capsule AND
            // every dormant ragdoll bone — in no particular order. Deduping earlier
            // meant a stray bone collider with a bad LOS path (tucked near a wall/
            // corner) could consume the goblin's one-hit slot before the loop ever
            // reached its actual capsule, which had clear LOS the whole time — the
            // goblin silently never got hit despite looking completely unobstructed.
            if (!hitThisSwing.Add(root)) return;

            // Radial blow: from straight-forward (sideBias 0) toward the target's own
            // outward bearing (sideBias 1). This is what fans the crowd back AND aside.
            Vector3 blow = Vector3.Slerp(flatDir, toDir, sideBias).normalized;

            // Inside the inner cone? Then this target is being CAPTURED rather than flung:
            // it still takes the hit (the shield connected — flinch, grunt and impact VFX
            // belong at this moment, not at the release), but the shove is held back for
            // the caller to apply when the carry ends. The bearing test uses the same
            // flat geometry as the outer cone, so the two can never disagree about who is
            // "in front"; only the reach and angle differ.
            bool captured = activeInner.Active
                            && dist <= activeInner.range
                            && Vector3.Dot(toDir, flatDir) >= activeInner.CosHalf;

            var info = new DamageInfo
            {
                amount = damage,
                point = c.ClosestPoint(origin),
                direction = blow,
                instigator = gameObject,
                type = DamageType.Melee,
                impulse = captured ? activeInner.impulse : knockback,
                poiseDamage = poiseDamage,
            };

            // A target can be damageable AND pushable — a barrel has Health for
            // DestructibleProp and a Rigidbody for shoving — and its knockback would
            // otherwise vanish, because Health.TakeDamage does NOT act on info.impulse. On an
            // NPC that's fine (NpcHitReactions listens to OnDamaged and applies it), but a
            // prop has no such component, so the impulse is dropped and the only thing moving
            // it is the capsule's contact push — which scales by ACHIEVED velocity and so
            // collapses the instant a heavy prop stalls the charge. That's why a 45kg barrel
            // stopped a bash dead while still making an impact noise.
            //
            // SHOVE BEFORE DAMAGE, because TakeDamage can destroy the target inside this very
            // call: Health fires OnDied synchronously, DestructibleProp.Break spawns the
            // debris and retires the prop. A push applied afterwards lands on a body that is
            // already gone. (NB the ordering alone does NOT let the debris inherit the shove —
            // AddForce QUEUES until the next physics step, so linearVelocity is unchanged for
            // the rest of this frame. DestructibleProp derives the throw from the killing blow
            // instead; see its inheritBlowSpeed.)
            // alreadyDeduped: this root is in hitThisSwing from the check above.
            ShoveProp(c, origin, flatDir, sideBias, alreadyDeduped: true);

            damageable.TakeDamage(info);
            landedThisSwing++;
            OnHitLanded?.Invoke(damageable, info);
            if (captured) OnConeInnerHit?.Invoke(damageable, blow);

            if (debugAttack)
                Debug.Log($"[Melee] {name}: cone {(captured ? "CAPTURED" : "hit")} '{root.name}' for {damage:0.#}.", this);
        }

        /// <summary>
        /// Would a swing RIGHT NOW connect with something damageable, and what?
        ///
        /// Runs the real sweep's geometry and the real rejection rules, dealing no
        /// damage — so a reticle built on it can never disagree with an actual swing.
        /// That's the whole point: an honest "would this hit?" readout answers whether a
        /// miss was aim or something else (out of range, wrong faction, no line of sight,
        /// target already dead). A reticle that guessed its own geometry would be worse
        /// than none, because it would lie exactly when you're trying to diagnose a miss.
        /// Same rule as ComputeZones backing both the placer and its debug gizmo.
        /// </summary>
        public bool PreviewWouldHit(out Transform target, out string reason)
        {
            target = null;
            reason = "nothing in reach";

            SweepGeometry(out Vector3 origin, out Vector3 dir, out Vector3 top, out Vector3 bottom);

            // Same two-mode gather DoSweep uses: overlap when already touching a target
            // (a cast STARTING inside a collider reports nothing), otherwise a cast.
            int hits = Physics.OverlapCapsuleNonAlloc(top, bottom, sweepRadius, overlapScratch, hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                if (!CanHit(overlapScratch[i], origin, dir, out var d, out string why)) { if (why != null) reason = why; continue; }
                target = d.Transform;
                reason = "in reach";
                return true;
            }

            hits = Physics.CapsuleCastNonAlloc(top, bottom, sweepRadius, dir, castScratch, range, hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                if (!CanHit(castScratch[i].collider, origin, dir, out var d, out string why)) { if (why != null) reason = why; continue; }
                target = d.Transform;
                reason = "in reach";
                return true;
            }

            return false;
        }

        /// <summary>The sweep's origin/direction and capsule ends — shared so DoSweep, the
        /// preview and the gizmo can't describe different volumes.</summary>
        void SweepGeometry(out Vector3 origin, out Vector3 dir, out Vector3 top, out Vector3 bottom)
        {
            if (aimSource != null) { dir = aimSource.forward; origin = aimSource.position + dir * aimForwardOffset; }
            else { dir = transform.forward; origin = transform.position + Vector3.up * originHeight; }

            top = origin + Vector3.up * sweepUpExtent;
            bottom = origin - Vector3.up * sweepDownExtent;
        }

        /// <summary>
        /// Every reason a candidate collider is or isn't a legal victim, WITHOUT dealing
        /// damage or touching per-swing state. TryHit applies these then damages; the
        /// preview applies these then reports. One copy, so they can't drift.
        /// `why` explains a rejection for the reticle's readout (null = not interesting).
        /// </summary>
        bool CanHit(Collider c, Vector3 origin, Vector3 dir, out IDamageable damageable, out string why)
        {
            damageable = null;
            why = null;
            if (c == null) return false;

            damageable = c.GetComponentInParent<IDamageable>();
            if (damageable == null) return false;                              // scenery

            Transform root = damageable.Transform;
            if (root == transform || root.IsChildOf(transform)) return false;  // yourself

            if (FactionMember.Of(root) == ownFaction) { why = "same faction"; return false; }
            if (damageable.IsDead) { why = "already dead"; return false; }

            Vector3 to = c.ClosestPoint(origin) - origin;
            if (to.sqrMagnitude > 0.0004f && Vector3.Dot(to.normalized, dir) < 0.1f) { why = "behind the swing"; return false; }

            if (IsLineBlocked(origin, c, damageable.Transform)) { why = "no line of sight"; return false; }

            return true;
        }

        /// <summary>
        /// Is something between the attacker and this victim?
        ///
        /// TWO traps here, both real bugs. First, HITTING THE VICTIM IS NOT AN
        /// OBSTRUCTION — losBlockMask auto-strips the NPC layer so a goblin never blocks
        /// itself, but a destructible PROP sits on an ordinary layer, so a plain Linecast
        /// to the barrel reported the barrel and rejected the swing. Second, the endpoint
        /// sits exactly ON the victim's surface, so whether that hit registers at all is a
        /// floating-point coin flip — which is why aiming dead centre failed while nudging
        /// a few pixels connected. Same shape as the interaction-sweep flicker, and the
        /// same answer NpcPerception.CanSee already uses: a hit only counts as blocking if
        /// it ISN'T the target. The short pull-back keeps the ray off that surface
        /// entirely rather than relying on the comparison to save us every time.
        /// </summary>
        bool IsLineBlocked(Vector3 origin, Collider victim, Transform victimRoot)
        {
            Vector3 point = victim.ClosestPoint(origin);
            Vector3 to = point - origin;
            float dist = to.magnitude;
            if (dist < 0.01f) return false;                    // origin is inside the victim — touching it, not blocked

            Vector3 dir = to / dist;
            float len = Mathf.Max(0f, dist - losSurfaceSkin);  // stop just short of the surface
            if (len <= 0f) return false;

            if (!Physics.Raycast(origin, dir, out RaycastHit hit, len, losBlockMask, QueryTriggerInteraction.Ignore))
                return false;

            // Identity by IDamageable, never transform.root — spawned NPCs share a
            // generated root, so root comparison resolves every goblin to the same object.
            var hitDamageable = hit.collider.GetComponentInParent<IDamageable>();
            if (hitDamageable != null && hitDamageable.Transform == victimRoot) return false;

            return true;
        }

        void TryHit(Collider c, Vector3 origin, Vector3 dir, Vector3 blowDir)
        {
            if (c == null) return;

            // Identity = the DAMAGEABLE, never transform.root. Spawned NPCs are
            // parented under generated roots (DungeonNpcs → the visualizer), so
            // transform.root resolves every goblin to 'DungeonSpawner' — one
            // shared wrong identity that broke dedupe, faction, and damage lookup
            // in one stroke (real bug: player swings whiffed with the goblin dead
            // center). Health sits on the NPC's own object; walking UP from the
            // collider to the nearest IDamageable finds the true identity boundary.
            var damageable = c.GetComponentInParent<IDamageable>();
            if (damageable == null)
            {
                // Not a living target — remember it as the ENVIRONMENT candidate (first
                // one found; a short melee sweep rarely catches more than one solid) so a
                // whiff still sparks off whatever the blade actually connected with.
                // Excludes the attacker's own colliders, same identity rule as a real hit.
                if (!envHitFound && c.transform != transform && !c.transform.IsChildOf(transform))
                {
                    envHitFound = true;
                    envHitPoint = c.ClosestPoint(origin);
                    envHitCollider = c;
                }
                if (debugAttack) Debug.Log($"[Melee] {name}: '{c.transform.root.name}/{c.name}' rejected — no IDamageable (scenery).", this);
                return;
            }

            // Every legality rule lives in CanHit, which the reticle's PreviewWouldHit
            // also calls — ONE copy, so a "would this land?" readout can never disagree
            // with an actual swing (and a fix like the self-occlusion one below lands in
            // both at once).
            if (!CanHit(c, origin, dir, out damageable, out string why))
            {
                if (debugAttack && why != null)
                    Debug.Log($"[Melee] {name}: '{c.transform.name}' rejected — {why}.", this);
                return;
            }

            Transform root = damageable.Transform;

            // Dedupe LAST, only once this collider has passed every check — same
            // reasoning as ConeHit: a victim can present several colliders (capsule +
            // ragdoll bones) to this sweep, and an early dedupe risked burning the
            // one-hit slot on a badly-positioned one before a good one was tried.
            if (!hitThisSwing.Add(root)) return;

            var info = new DamageInfo
            {
                amount = damage,
                point = c.ClosestPoint(origin),
                direction = blowDir,   // the SWING's direction, not just "toward the target" — drives the recoil
                instigator = gameObject,
                type = DamageType.Melee,
                impulse = knockback,
                poiseDamage = poiseDamage,
            };
            damageable.TakeDamage(info);
            landedThisSwing++;
            OnHitLanded?.Invoke(damageable, info);

            if (debugAttack) Debug.Log($"[Melee] {name}: hit '{root.name}' for {damage:0.#}.", this);
        }

        void OnDrawGizmosSelected()
        {
            // Draw the vertical capsule at both ends of the sweep, so you can see it
            // covers short enemies. Uses the same aim as DoSweep (camera if set).
            Vector3 dir; Vector3 origin;
            if (aimSource != null) { dir = aimSource.forward; origin = aimSource.position + dir * aimForwardOffset; }
            else { dir = transform.forward; origin = transform.position + Vector3.up * originHeight; }

            Gizmos.color = IsSwinging ? Color.red : new Color(1f, 0.5f, 0f, 0.5f);
            DrawSweepCapsule(origin);
            DrawSweepCapsule(origin + dir * range);
            Gizmos.DrawLine(origin, origin + dir * range);
        }

        void DrawSweepCapsule(Vector3 at)
        {
            Vector3 top = at + Vector3.up * sweepUpExtent;
            Vector3 bottom = at - Vector3.up * sweepDownExtent;
            Gizmos.DrawWireSphere(top, sweepRadius);
            Gizmos.DrawWireSphere(bottom, sweepRadius);
            Gizmos.DrawLine(top, bottom);
        }
    }
}
