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
        [Tooltip("How deep BEHIND the collider plane, in metres, the impact point may be pulled onto the kit's REAL surface after hitting the dungeon shell (KitSurface).\n\nCollision truth is the greybox, and it emits one flat quad per cell face inset by Wall Margin — so an arrow into a recessed niche stops on an invisible plane and hangs in front of the recess, and an ordinary wall hit lands Wall Margin proud of the masonry you can see. This re-tests the wall's actual triangles and moves the point onto them.\n\nMEASURED AS DEPTH ALONG THE FACE NORMAL, not as distance travelled, so it means the same thing however obliquely the arrow arrives. Size it to Wall Margin plus your deepest recess (Rock Depth) with a little margin.\n\nIt applies ONLY to hits on the greybox shell, never to props or NPCs, and anything deeper than this is discarded — a slightly proud arrow is a cosmetic miss, an arrow teleported inside masonry is a bug. 0 disables it.")]
        public float maxSurfaceRefine = 0.75f;

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

        // Direction AND speed of travel, refreshed every physics step. Captured because the
        // collision response overwrites linearVelocity before OnCollisionEnter sees it — the
        // speed for the same reason as the direction, since passing through a gap has to put
        // the shot back the way it was rather than merely let it continue.
        Vector3 lastFlightDir;
        float lastFlightSpeed;
        Collider[] myColliders = System.Array.Empty<Collider>();
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
            lastFlightSpeed = velocity.magnitude;   // seeded here too: a point-blank shot can
                                                    // contact before its first FixedUpdate

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
            // Gathered once and kept: the pass-through path needs them again, and
            // GetComponentsInChildren allocates.
            myColliders = GetComponentsInChildren<Collider>();
            if (firedBy != null)
            {
                foreach (var theirs in firedBy.GetComponentsInChildren<Collider>())
                    foreach (var c in myColliders)
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
            lastFlightSpeed = v.magnitude;
            if (alignToVelocity) transform.rotation = Quaternion.LookRotation(lastFlightDir);

            LookAheadForGaps();
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

            // ---- Is there actually anything in the way? ----
            // A barred door's collider is a solid box while its geometry is mostly gaps, so ask
            // the mesh rather than the collider. Probing from slightly BEFORE the contact so a
            // bar sitting exactly on the collider surface is not missed by the epsilon.
            var permeable = ProjectilePermeable.On(collision.collider);
            if (permeable != null)
            {
                Vector3 probeFrom = point - dir * 0.05f;
                if (permeable.Blocks(probeFrom, dir, out float meshT))
                {
                    // It DID hit a bar. Land on the bar rather than on the box collider's
                    // surface — the same correction KitSurface makes for walls, for free,
                    // since finding the geometry is what the test just did.
                    point = probeFrom + dir * meshT;
                    if (permeable.debugPermeable)
                        Debug.Log($"[Permeable] '{permeable.name}' blocked the shot {meshT:0.000}m in.", permeable);
                }
                else
                {
                    PassThrough(permeable);
                    return;   // no damage, no spark, no noise, still armed — nothing happened
                }
            }
            // PULL THE POINT ONTO THE KIT'S REAL SURFACE before anything consumes it, so the
            // stick, the spark and the noise all agree. The greybox is a flat quad per cell
            // face and the visible wall can sit well behind it (a recess) or slightly behind it
            // (wallMargin) — see KitSurface. Restricted to shell hits and clamped, and it
            // fails open, so a miss leaves `point` exactly as PhysX reported it.
            else if (maxSurfaceRefine > 0f && collision.contactCount > 0 &&
                     KitSurface.Refine(collision.collider, point, contact.normal, dir, maxSurfaceRefine,
                                       out Vector3 onSurface))
                point = onSurface;

            bool damaged = TryDamage(collision.collider, point, dir);

            if (surfaceLibrary != null)
                SurfaceImpact.Spawn(surfaceLibrary, Surface.Of(collision.collider), point, dir);

            // Lands somewhere OTHER than the shooter — the distraction half of a bow.
            if (impactLoudness > 0f)
                NoiseBus.Emit(point, impactLoudness, transform, shooterFaction);

            OnImpact?.Invoke(point, damaged);
            Stick(collision.collider, point, dir);
        }

        /// <summary>
        /// Decide about a permeable piece BEFORE PhysX generates a contact with it.
        ///
        /// WHY THIS EXISTS RATHER THAN JUST HANDLING IT AT THE COLLISION: by the time
        /// OnCollisionEnter runs the impulse has already been applied to BOTH bodies, so a shot
        /// passing between the bars still shoved the door — field-reported as gates twitching
        /// open when arrows flew through them. Undoing that means guessing at `Collision.impulse`
        /// signs; never generating the contact is exact. §8's post-bounce rule again: if you need
        /// state from before an impact, you have to act before the impact.
        ///
        /// One ray per physics step per arrow, one step's travel long, on the layers the arrow
        /// can actually hit minus its own. A thin ray can disagree with the arrow's swept
        /// collider at the edges; either way round is harmless, because the collision path
        /// still resolves the shot correctly — this only decides whether a contact is generated
        /// at all.
        /// </summary>
        void LookAheadForGaps()
        {
            if (stuck) return;
            float reach = lastFlightSpeed * Time.fixedDeltaTime + 0.2f;
            if (!Physics.Raycast(transform.position, lastFlightDir, out RaycastHit ahead, reach,
                                 ~(1 << gameObject.layer), QueryTriggerInteraction.Ignore))
                return;

            var permeable = ProjectilePermeable.On(ahead.collider);
            if (permeable == null) return;
            // Probe from slightly before the surface, the same margin the collision path uses,
            // so geometry sitting exactly on the collider face is not missed by an epsilon.
            if (permeable.Blocks(ahead.point - lastFlightDir * 0.05f, lastFlightDir, out _)) return;

            IgnorePiece(permeable);
            if (permeable.debugPermeable)
                Debug.Log($"[Permeable] cleared '{permeable.name}' ahead of contact — no impulse.", permeable);
        }

        /// <summary>
        /// Stop colliding with EVERY collider on the piece, not just the one in front. A door is
        /// a compound; re-contacting a sibling box a millimetre later would stop the arrow
        /// anyway and present as the pass-through working only sometimes.
        /// </summary>
        void IgnorePiece(ProjectilePermeable permeable)
        {
            var theirs = permeable.Colliders;
            for (int i = 0; i < theirs.Count; i++)
            {
                if (theirs[i] == null) continue;
                for (int j = 0; j < myColliders.Length; j++)
                    if (myColliders[j] != null) Physics.IgnoreCollision(myColliders[j], theirs[i], true);
            }
        }

        /// <summary>
        /// Carry on through a gap. The collision has ALREADY been resolved by the time we get
        /// here — PhysX applied its impulse before the callback ran (§8's post-bounce rule, the
        /// same one `lastFlightDir` exists for) — so the shot has to be put back the way it was
        /// rather than merely allowed to continue.
        ///
        /// `IgnoreCollision` against EVERY collider on the piece, not just the one struck: a
        /// door is a compound, and re-contacting a sibling box a millimetre later would stop the
        /// arrow anyway and look like the pass-through failing intermittently.
        ///
        /// Nothing else fires. No damage, no surface impact, no noise, and `armed` is untouched,
        /// because from the game's point of view the arrow did not hit anything.
        /// </summary>
        void PassThrough(ProjectilePermeable permeable)
        {
            IgnorePiece(permeable);

            // Restore the pre-impact velocity. Speed is cached alongside the direction for
            // exactly the same reason the direction is: what PhysX reports now is post-bounce.
            body.linearVelocity = lastFlightDir * (lastFlightSpeed * permeable.speedRetained);

            if (permeable.debugPermeable)
                Debug.Log($"[Permeable] passed through '{permeable.name}' at {lastFlightSpeed:0.0} m/s — still armed.", permeable);
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
