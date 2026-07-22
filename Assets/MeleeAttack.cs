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

        public bool IsSwinging { get; private set; }
        public bool CanAttack => !IsSwinging && Time.time >= readyAt && !Suppressed;
        /// <summary>Set true to block attacks (stagger, death, disarm-in-progress).</summary>
        public bool Suppressed { get; set; }

        float readyAt;
        int landedThisSwing;   // victims actually damaged (hitThisSwing also counts walls)
        Faction ownFaction;
        readonly HashSet<Transform> hitThisSwing = new HashSet<Transform>();
        static readonly Collider[] overlapScratch = new Collider[16];
        static readonly RaycastHit[] castScratch = new RaycastHit[16];
        // The cone sweeps a MUCH larger volume than the sword capsule, and each enemy
        // brings many colliders (its capsule + all the dormant ragdoll bone colliders),
        // so a crowd blows past 16 instantly — a too-small buffer silently caps the
        // OverlapSphere and drops most goblins. Give the cone plenty of room.
        static readonly Collider[] coneScratch = new Collider[128];

        void Awake() => ownFaction = FactionMember.Of(transform);

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
        public int DoConeSweep(float halfAngleDeg, float sideBias)
        {
            hitThisSwing.Clear();
            landedThisSwing = 0;

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
            int n = Physics.OverlapSphereNonAlloc(origin, range + 2f, coneScratch, hitMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                ConeHit(coneScratch[i], origin, flatDir, cosHalf, range, Mathf.Clamp01(sideBias));

            if (debugAttack)
                Debug.Log($"[Melee] {name}: cone sweep saw {n} collider(s){(n >= coneScratch.Length ? " (BUFFER FULL — raise coneScratch)" : "")} → {(landedThisSwing > 0 ? $"{landedThisSwing} HIT" : "whiff")}.", this);
            return landedThisSwing;
        }

        void ConeHit(Collider c, Vector3 origin, Vector3 flatDir, float cosHalf, float range, float sideBias)
        {
            if (c == null) return;

            var damageable = c.GetComponentInParent<IDamageable>();
            if (damageable == null) return;
            Transform root = damageable.Transform;
            if (root == transform || root.IsChildOf(transform)) return;   // never hit yourself
            if (!hitThisSwing.Add(root)) return;                          // one hit per victim
            if (FactionMember.Of(root) == ownFaction) return;
            if (damageable.IsDead) return;

            // Bearing FROM the attacker, on the floor plane.
            Vector3 to = root.position - origin;
            Vector3 toFlat = new Vector3(to.x, 0f, to.z);
            float dist = toFlat.magnitude;
            if (dist > range) return;                                 // true reach is the FLAT distance
            Vector3 toDir = dist > 1e-3f ? toFlat / dist : flatDir;   // right on top of us → straight ahead

            if (Vector3.Dot(toDir, flatDir) < cosHalf) return;        // outside the cone

            // Radial blow: from straight-forward (sideBias 0) toward the target's own
            // outward bearing (sideBias 1). This is what fans the crowd back AND aside.
            Vector3 blow = Vector3.Slerp(flatDir, toDir, sideBias).normalized;

            var info = new DamageInfo
            {
                amount = damage,
                point = c.ClosestPoint(origin),
                direction = blow,
                instigator = gameObject,
                type = DamageType.Melee,
                impulse = knockback,
                poiseDamage = poiseDamage,
            };
            damageable.TakeDamage(info);
            landedThisSwing++;
            OnHitLanded?.Invoke(damageable, info);

            if (debugAttack) Debug.Log($"[Melee] {name}: cone hit '{root.name}' for {damage:0.#}.", this);
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
                if (debugAttack) Debug.Log($"[Melee] {name}: '{c.transform.root.name}/{c.name}' rejected — no IDamageable (scenery).", this);
                return;
            }

            Transform root = damageable.Transform;
            if (root == transform || root.IsChildOf(transform)) return;  // never hit yourself
            if (!hitThisSwing.Add(root)) return;                         // one hit per victim per swing
            if (FactionMember.Of(root) == ownFaction)
            {
                if (debugAttack) Debug.Log($"[Melee] {name}: '{root.name}' rejected — same faction ({ownFaction}). If this is a valid target, someone's FactionMember is missing or wrong.", this);
                return;
            }
            if (damageable.IsDead) return;

            // Only in front — the overlap fallback is a sphere and shouldn't let a
            // swing hit someone standing behind the attacker. Compared in FULL 3D
            // against the aim direction: the old flat-vs-pitched-dir comparison
            // collapsed when the player looked steeply DOWN at a short goblin and
            // rejected legitimate point-blank hits.
            Vector3 to = c.ClosestPoint(origin) - origin;
            if (to.sqrMagnitude > 0.0004f && Vector3.Dot(to.normalized, dir) < 0.1f)
            {
                if (debugAttack) Debug.Log($"[Melee] {name}: '{root.name}' rejected — behind the swing (dot {Vector3.Dot(to.normalized, dir):0.00}).", this);
                return;
            }

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
