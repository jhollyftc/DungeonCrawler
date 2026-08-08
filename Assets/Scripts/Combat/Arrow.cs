using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A fired arrow: flies, damages the first thing it hits, then STICKS there.
    ///
    /// Damage goes through IDamageable like every other attack, so an arrow hurts an NPC,
    /// the player or a destructible crate with no special-casing — same reason a goblin's
    /// sword and a thrown barrel already work on anything.
    ///
    /// ARMED like ThrownDamage: it can only hurt something once, and only while in
    /// flight. An arrow already stuck in a wall must never damage whatever brushes past
    /// it, and a single shot must never double-hit on a second contact frame.
    ///
    /// STICKING parents the arrow to what it hit, so it rides a walking goblin and stays
    /// with a limb through a ragdoll. Two hazards handled: bone transforms can carry
    /// extreme scales (this project's goblin mesh sits at 175), which would inflate a
    /// naively parented arrow — SetParent(worldPositionStays) compensates, and
    /// stickToRootOnly is the escape hatch. And a destructible prop can be destroyed
    /// while an arrow is in it, which takes the arrow with it; that reads correctly, so
    /// it's left alone.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class Arrow : MonoBehaviour
    {
        [Header("Damage")]
        [Tooltip("Damage at a FULL draw. Scaled down by the draw the shot was released at.")]
        public float damage = 18f;
        [Tooltip("Knockback impulse (m/s) delivered on hit.")]
        public float knockback = 2.5f;
        [Tooltip("Poise damage. Low — an arrow stings, it doesn't stagger like a mace.")]
        public float poiseDamage = 15f;

        [Header("Flight")]
        [Tooltip("Seconds the arrow stays lethal after being fired. Also its lifetime if it never hits anything.")]
        public float armedDuration = 8f;
        [Tooltip("Rotate the arrow to face its own velocity while flying, so it arcs nose-first instead of tumbling.")]
        public bool alignToVelocity = true;

        [Header("Sticking")]
        [Tooltip("Seconds a stuck arrow remains before shrinking away.")]
        public float stickLifetime = 12f;
        [Tooltip("Seconds the shrink takes (reuses DebrisCleanup, the same retirement fracture debris uses).")]
        public float stickShrinkTime = 0.8f;
        [Tooltip("Metres the arrow sinks INTO whatever it hits, so it reads as embedded rather than glued to the surface. Measured from the TIP.")]
        public float embedDepth = 0.12f;
        [Tooltip("Empty child transform at the arrow's POINT. The contact PhysX reports is on the collider surface, but sticking moves the arrow's PIVOT there — so without knowing where the tip sits relative to the pivot, every arrow lands offset by that distance (buried to the fletching, or floating off the wall). Same explicit-anchor approach ViewmodelCollision uses for its shoulder/tip. Left empty, the pivot is assumed to BE the tip.")]
        public Transform tip;
        [Tooltip("Parent to the victim's ROOT instead of the exact collider hit. Costs the arrow riding an individual limb, but sidesteps bone transforms with extreme scales.")]
        public bool stickToRootOnly = false;

        [Header("Effects")]
        [Tooltip("Shared SurfaceLibrary — an arrow thudding into wood should sound like everything else that hits wood.")]
        public SurfaceLibrary surfaceLibrary;
        [Tooltip("Loudness (0..1) of the NoiseEvent on impact. An arrow hitting stone is a real sound NPCs should investigate — and it lands somewhere ELSE, which makes it a distraction tool.")]
        [Range(0f, 1f)] public float impactLoudness = 0.45f;

        /// <summary>Fired on any impact: (position, whether it damaged something).</summary>
        public event System.Action<Vector3, bool> OnImpact;
        /// <summary>A weak point was struck: (the Hitbox, world point). The hook for a
        /// distinct headshot sound or VFX — kept separate from OnImpact so a listener
        /// doesn't have to re-derive what was hit.</summary>
        public event System.Action<Hitbox, Vector3> OnWeakPointHit;

        Rigidbody body;
        GameObject shooter;
        Faction shooterFaction = Faction.Player;
        float drawScale = 1f;
        bool armed;
        bool stuck;

        // Direction of travel, refreshed every physics step. Captured because the
        // collision response overwrites linearVelocity before OnCollisionEnter sees it.
        Vector3 lastFlightDir;
        float tipAhead;   // pivot → tip distance along local forward; see `tip`

        // Stuck-to-a-mover state — see Stick() for why this isn't just parenting.
        Transform followAnchor;
        Vector3 followOffset;
        Quaternion followRotation;
        bool followMoves;

        void Awake()
        {
            body = GetComponent<Rigidbody>();

            // Distance from pivot to tip along the arrow's own forward, captured before
            // anything moves it. Constant for the prefab, so it's measured once.
            tipAhead = tip != null ? transform.InverseTransformPoint(tip.position).z : 0f;
        }

        /// <summary>
        /// Launch. `draw` (0..1) scales damage and is expected to have already scaled the
        /// speed the caller applied, so a half-drawn shot is weak in both senses.
        /// </summary>
        public void Fire(GameObject firedBy, Vector3 velocity, float draw = 1f)
        {
            shooter = firedBy;
            shooterFaction = firedBy != null ? FactionMember.Of(firedBy.transform) : Faction.Player;
            drawScale = Mathf.Clamp01(draw);
            armed = true;

            body.isKinematic = false;
            body.linearVelocity = velocity;
            lastFlightDir = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : transform.forward;

            // We drive the arrow's facing from its velocity, so let PhysX handle position
            // only. Left free, integrated angular velocity fights those writes and the
            // arrow tumbles in flight — and a tumbling arrow is what makes the stuck
            // orientation look arbitrary even once it IS set explicitly.
            if (alignToVelocity) body.freezeRotation = true;

            // CONTINUOUS detection is not optional at these speeds. A 38 m/s arrow covers
            // ~76cm per 0.02s physics step, so discrete detection can put it deep inside
            // — or straight through — a wall before a contact is generated, and the
            // contact point that comes back is then wherever it happened to end up. That
            // inconsistency is what makes stuck arrows appear at scattered offsets rather
            // than where they visibly struck.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // Don't let the shot collide with whoever fired it — the spawn point sits
            // right at the camera, usually inside the player's own capsule.
            if (firedBy != null)
            {
                var mine = GetComponentsInChildren<Collider>();
                foreach (var theirs in firedBy.GetComponentsInChildren<Collider>())
                    foreach (var c in mine)
                        if (c != null && theirs != null) Physics.IgnoreCollision(c, theirs, true);
            }

            Destroy(gameObject, armedDuration + stickLifetime + stickShrinkTime + 1f);
            Invoke(nameof(Disarm), armedDuration);
        }

        void Disarm() => armed = false;

        void FixedUpdate()
        {
            if (stuck) return;

            Vector3 v = body.linearVelocity;
            if (v.sqrMagnitude <= 0.5f) return;

            // Remember the flight direction EVERY step. By the time OnCollisionEnter
            // runs, PhysX has already resolved the impact — linearVelocity there is the
            // post-bounce velocity, not the direction the arrow was travelling. Reading
            // it at impact is what produced arrows stuck at random angles.
            lastFlightDir = v.normalized;
            if (alignToVelocity) transform.rotation = Quaternion.LookRotation(lastFlightDir);
        }

        /// <summary>
        /// Ride a moving anchor. LateUpdate so it runs AFTER the Animator and the
        /// ragdoll have posed the bone this frame — following in Update would leave the
        /// arrow a frame behind and visibly swimming on a running goblin.
        /// </summary>
        void LateUpdate()
        {
            if (!stuck || !followMoves) return;

            // The anchor can be destroyed underneath us — a destructible prop shattering,
            // or a corpse despawning. Leave the arrow where it is rather than following a
            // dead transform; DebrisCleanup still retires it on schedule.
            if (followAnchor == null) { followMoves = false; return; }

            transform.position = followAnchor.TransformPoint(followOffset);
            transform.rotation = followAnchor.rotation * followRotation;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (stuck) return;

            ContactPoint contact = collision.contactCount > 0 ? collision.GetContact(0) : default;
            Vector3 point = collision.contactCount > 0 ? contact.point : transform.position;

            // The direction the arrow was TRAVELLING, cached last FixedUpdate — not
            // body.linearVelocity, which the collision response has already changed.
            Vector3 dir = lastFlightDir.sqrMagnitude > 0.01f ? lastFlightDir : transform.forward;

            bool damaged = TryDamage(collision.collider, point, dir);

            if (surfaceLibrary != null)
                SurfaceImpact.Spawn(surfaceLibrary, Surface.Of(collision.collider), point, dir);

            // Lands somewhere OTHER than the shooter — the distraction half of a bow.
            if (impactLoudness > 0f)
                NoiseBus.Emit(point, impactLoudness, transform, shooterFaction);

            OnImpact?.Invoke(point, damaged);
            Stick(collision.collider, point, dir);
        }

        bool TryDamage(Collider hit, Vector3 point, Vector3 dir)
        {
            if (!armed) return false;   // already spent, or long since landed

            var damageable = hit.GetComponentInParent<IDamageable>();
            if (damageable == null) return false;
            if (shooter != null && damageable.Transform == shooter.transform) return false;
            if (damageable.IsDead) return false;
            if (FactionMember.Of(damageable.Transform) == shooterFaction) return false;

            armed = false;   // one victim per shot, before TakeDamage — a death reaction
                             // can move colliders and re-contact this same frame

            // Weak point? Resolved from the EXACT collider struck, so a head shot is
            // worth more than the same arrow into a torso. Knockback is left unscaled —
            // where you hit something shouldn't change how hard it's shoved.
            Hitbox hitbox = Hitbox.On(hit);
            float multiplier = hitbox != null ? Mathf.Max(0f, hitbox.damageMultiplier) : 1f;

            damageable.TakeDamage(new DamageInfo
            {
                amount = damage * drawScale * multiplier,
                point = point,
                direction = dir,
                instigator = shooter,
                // Projectile, NOT Thrown: Thrown redirects part of the shove UPWARD
                // (NpcHitReactions.thrownVerticalPop), which is tuned for a hurled barrel
                // and sent NPCs flying skyward when shot from above.
                type = DamageType.Projectile,
                impulse = knockback * drawScale,
                poiseDamage = poiseDamage * drawScale,
            });

            if (hitbox != null) OnWeakPointHit?.Invoke(hitbox, point);
            return true;
        }

        void Stick(Collider hit, Vector3 point, Vector3 dir)
        {
            stuck = true;
            armed = false;
            CancelInvoke(nameof(Disarm));

            body.isKinematic = true;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;

            // ORIENT EXPLICITLY along the flight path, then sink in along it. Both are
            // required: without setting rotation the arrow keeps whatever orientation the
            // collision response left it in, which is why arrows fired straight at a
            // pillar ended up at wildly different angles. An arrow embeds along the path
            // it arrived on, so the flight direction — not the surface normal — is what
            // orients it; that's also what makes a glancing shot sit at a shallow angle
            // instead of standing perpendicular to the wall.
            transform.rotation = Quaternion.LookRotation(dir);

            // Place the TIP at the contact (then embedDepth further in), not the pivot.
            // contact.point is on the collider surface; moving the pivot there offsets
            // the arrow by however far the tip sits ahead of it — a constant error on
            // top of the contact noise, which is why arrows looked both scattered AND
            // wrongly sunk. See `tip`.
            transform.position = point + dir * (embedDepth - tipAhead);

            Transform anchor = hit != null ? hit.transform : null;
            if (stickToRootOnly && hit != null)
            {
                var d = hit.GetComponentInParent<IDamageable>();
                anchor = d != null ? d.Transform : hit.transform.root;
            }

            // FOLLOW the anchor, never PARENT to it.
            //
            // SetParent(worldPositionStays: true) only preserves world rotation when the
            // parent's scale is UNIFORM. Rig bones routinely aren't — an FBX axis
            // conversion leaves non-uniform and sometimes negative scales, and this
            // project's goblin mesh sits at 175 on top of that. The correct local
            // transform would then need SHEAR, which a TRS Transform cannot express, so
            // Unity skews the rotation to fit and the arrow ends up pointing somewhere
            // else (real bug).
            //
            // Following sidesteps it: the offset round-trips through the anchor's own
            // space (so scale cancels exactly), while the rotation is a pure quaternion
            // that never touches scale. The arrow also keeps its own localScale, which
            // means DebrisCleanup's shrink stays correct instead of operating on a
            // 1/175 value.
            if (anchor != null)
            {
                followAnchor = anchor;
                followOffset = anchor.InverseTransformPoint(transform.position);
                followRotation = Quaternion.Inverse(anchor.rotation) * transform.rotation;

                // Only pay the per-frame follow for anchors that can actually move. An
                // arrow in a wall is the common case and needs nothing.
                followMoves = anchor.GetComponentInParent<Rigidbody>() != null
                           || anchor.GetComponentInParent<Animator>() != null;
            }

            // Retire through DebrisCleanup — the same wait-then-shrink-then-destroy
            // fracture debris uses, so a stuck arrow fades out the way everything else
            // does instead of vanishing. The Destroy timer from Fire() stays as a
            // backstop for an arrow that somehow never gets here.
            var cleanup = gameObject.AddComponent<DebrisCleanup>();
            cleanup.lifetime = stickLifetime;
            cleanup.shrinkTime = stickShrinkTime;
        }
    }
}
