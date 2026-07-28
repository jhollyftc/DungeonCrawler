using System.Collections;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Makes a prop breakable. The prop-side counterpart to NpcHitReactions: it never
    /// decides what hurts, it only decides what being hurt MEANS — here, bursting into
    /// debris.
    ///
    /// Almost nothing new was needed for this. `Health` already implements `IDamageable`,
    /// and melee, ThrownDamage and the shield-bash cone all talk only to `IDamageable`,
    /// so a crate with a Health component is already damageable by every existing
    /// attack. Faction works out too: a prop left `Neutral` differs from both `Player`
    /// and `Dungeon`, so the player AND goblins can smash it. (The documented
    /// silent-whiff trap only bites when BOTH sides are Neutral — so the player must
    /// carry an explicit FactionMember.)
    ///
    /// AUTHORING: the prop MUST be `PropTier.FullGameObject`, exactly like Carryables
    /// and for the same reason (§8) — instanced tiers bake the mesh into a static matrix
    /// and hand the prop only a collider GameObject, and InstancedDungeonRenderer has NO
    /// removal path (Commit is additive/idempotent). Destroy the GameObject and the mesh
    /// stays welded in the air. Destructibles are low-count by nature, so the batching
    /// loss is irrelevant.
    ///
    /// Pairs naturally with Carryable: a barrel you can lift, throw, and shatter.
    /// </summary>
    [RequireComponent(typeof(Health))]
    [DisallowMultipleComponent]
    public class DestructibleProp : MonoBehaviour
    {
        [Header("Fracture")]
        [Tooltip("Pre-fractured version of this prop — chunk meshes with their own Rigidbodies and colliders. Spawned at this prop's exact pose on death. Left empty, the prop simply vanishes (still fires everything else), so a missing asset degrades instead of erroring.")]
        public GameObject fracturedPrefab;
        [Tooltip("Outward impulse (m/s) given to each chunk, away from the killing blow. Enough to scatter, not enough to fire them across the room.")]
        public float burstForce = 2.5f;
        [Tooltip("Fraction of the burst redirected UP, so debris arcs instead of skidding flat along the floor.")]
        [Range(0f, 1f)] public float burstUpBias = 0.35f;
        [Tooltip("Random spin (rad/s) per chunk. A little goes a long way — chunks tumbling too fast read as confetti.")]
        public float burstTorque = 1.5f;
        [Tooltip("Extra rotation (Euler deg) for the debris, applied after the fractured prefab's own root rotation. Should be ZERO if the fracture asset was exported on the same axes as the intact prop — reach for it only when the two were authored differently.")]
        public Vector3 debrisRotationOffset;

        [Header("Cleanup")]
        [Tooltip("Seconds the debris lies there before shrinking away.")]
        public float debrisLifetime = 6f;
        [Tooltip("Seconds the shrink takes.")]
        public float debrisShrinkTime = 1.2f;

        [Header("Impact damage (thrown into things)")]
        [Tooltip("Take damage from this prop's OWN hard impacts, so a thrown barrel shatters on the wall. Reads ImpactAudio's existing OnImpact rather than adding another OnCollisionEnter — that event is already speed-scaled AND gated by a speed floor and retrigger interval, so a bouncing prop can't machine-gun damage the way raw collisions would.")]
        public bool damageOnImpact = true;
        [Tooltip("Damage at loudness 1 (the hardest impact ImpactAudio reports). Set relative to Health.max — at or above it, one solid throw destroys the prop.")]
        public float impactDamageAtFullForce = 25f;
        [Tooltip("Impacts quieter than this (0..1) do nothing. Keeps a prop from being worn down by being nudged and dropped.")]
        [Range(0f, 1f)] public float impactDamageThreshold = 0.35f;

        [Header("Break effect")]
        [Tooltip("The shared SurfaceLibrary — the same asset melee and thrown-prop impacts use, so a crate shattering belongs to the same world as a sword hitting it. Left empty, the prop still breaks, just silently.")]
        public SurfaceLibrary surfaceLibrary;
        [Tooltip("Extra break bursts spawned around the prop, beyond the one at the killing blow. 0 = just the one. A couple reads as the whole thing coming apart rather than a single point failing.")]
        [Range(0, 6)] public int extraBreakBursts = 2;
        [Tooltip("Radius (m) the extra bursts scatter within. Roughly the prop's own size.")]
        public float breakBurstSpread = 0.35f;

        [Header("Noise")]
        [Tooltip("Loudness (0..1) of the NoiseEvent emitted when this breaks. Smashing a crate is loud — NPCs should come and look, which makes breaking things a real stealth decision instead of a free one.")]
        [Range(0f, 1f)] public float breakLoudness = 0.9f;

        [Tooltip("Longest the invisible, inert husk may linger waiting for its own audio to finish before being destroyed. A safety cap — normal impact one-shots are well under a second.")]
        public float maxAudioTail = 3f;

        [Tooltip("Log each hit and the break.")]
        public bool debugDestructible = false;

        /// <summary>Fired just before the prop is destroyed — the seam a future LootTable hangs on.</summary>
        public event System.Action<DamageInfo> OnDestroyed;

        Health health;
        ImpactAudio impactAudio;
        bool broken;   // guards a second break from a simultaneous hit

        void Awake()
        {
            health = GetComponent<Health>();
            impactAudio = GetComponent<ImpactAudio>();
        }

        void OnEnable()
        {
            if (health != null) health.OnDied += HandleDied;
            if (impactAudio != null) impactAudio.OnImpact += HandleImpact;
        }

        void OnDisable()
        {
            if (health != null) health.OnDied -= HandleDied;
            if (impactAudio != null) impactAudio.OnImpact -= HandleImpact;
        }

        /// <summary>
        /// Self-damage from a hard impact. Loudness is already the normalized 0..1 force
        /// ImpactAudio derives from impact speed, so force is destructive on exactly the
        /// same curve it is audible — the same reason damage and audio share ImpactForce
        /// elsewhere, so the two can't drift apart.
        /// </summary>
        void HandleImpact(Vector3 point, float loudness)
        {
            if (!damageOnImpact || broken || health == null || health.IsDead) return;
            if (loudness < impactDamageThreshold) return;

            var info = new DamageInfo
            {
                amount = impactDamageAtFullForce * loudness,
                point = point,
                direction = (point - transform.position).normalized,
                instigator = null,               // the world hit it, nobody did
                type = DamageType.Environment,
                impulse = 0f,                    // it's already moving; don't fight the physics
                poiseDamage = 0f,
            };

            if (debugDestructible)
                Debug.Log($"[Destructible] {name}: impact loudness {loudness:0.00} → {info.amount:0.#} damage.", this);

            health.TakeDamage(info);
        }

        void HandleDied(DamageInfo info) => Break(info);

        void Break(DamageInfo info)
        {
            if (broken) return;
            broken = true;

            // A prop can be destroyed while the player is holding it (a goblin swings at
            // the barrel in your hands), which would leave PlayerCarry gripping a
            // destroyed object.
            var carryable = GetComponent<Carryable>();
            if (carryable != null)
            {
                var carry = FindObjectOfType<PlayerCarry>();
                if (carry != null && carry.Held == carryable) carry.Drop();
            }

            // Loud. NPCs should come looking — see breakLoudness.
            if (breakLoudness > 0f)
                NoiseBus.Emit(transform.position, breakLoudness, transform, Faction.Neutral);

            SpawnBreakEffect(info);

            OnDestroyed?.Invoke(info);   // loot hangs here

            if (fracturedPrefab != null) SpawnDebris(info);
            else if (debugDestructible)
                Debug.Log($"[Destructible] {name}: no fracturedPrefab — vanishing.", this);

            if (debugDestructible) Debug.Log($"[Destructible] {name}: broke.", this);
            RetireAfterAudio();
        }

        /// <summary>
        /// Look destroyed IMMEDIATELY, but survive long enough for any sound already
        /// playing ON this prop to finish.
        ///
        /// Destroying the GameObject outright kills its AudioSource mid-clip. ImpactAudio
        /// plays the throw impact through a source on the prop and fires OnImpact in the
        /// same call stack that then damages, kills and destroys it — so the console shows
        /// the clip playing and you hear nothing. A non-fatal impact is fine; only the
        /// KILLING one goes silent, which is the one that matters.
        ///
        /// Waiting on isPlaying rather than a clip length, because ImpactAudio uses
        /// PlayOneShot — which does NOT set AudioSource.clip, so there's no duration to
        /// read. Capped by maxAudioTail so a looping or stuck source can't keep an
        /// invisible husk alive forever.
        /// </summary>
        void RetireAfterAudio()
        {
            // Gone as far as the player is concerned, this frame.
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = false;

            // Inert: no more collisions (colliders off), and don't let an invisible body
            // fall through the world in the meantime.
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }

            var sources = GetComponentsInChildren<AudioSource>(true);
            foreach (var s in sources)
            {
                if (s != null && s.isPlaying) { StartCoroutine(DestroyWhenQuiet(sources)); return; }
            }

            Destroy(gameObject);   // nothing playing — go now
        }

        IEnumerator DestroyWhenQuiet(AudioSource[] sources)
        {
            float deadline = Time.time + Mathf.Max(0.1f, maxAudioTail);
            while (Time.time < deadline)
            {
                bool playing = false;
                foreach (var s in sources)
                    if (s != null && s.isPlaying) { playing = true; break; }
                if (!playing) break;
                yield return null;
            }
            Destroy(gameObject);
        }

        /// <summary>
        /// The shatter, through the shared surface system so it matches every other
        /// impact in the game (a crate reads as wood whether a sword hits it or it hits
        /// a wall).
        ///
        /// Deliberately passes NO sfxSource: SurfaceImpact then uses PlayClipAtPoint,
        /// which spawns its own self-destructing object. That's the whole fix for "no
        /// sound on destruction" — ImpactAudio plays through an AudioSource ON THIS PROP,
        /// and Break() destroys the prop in the same call stack, cutting the clip off
        /// mid-note. A non-fatal impact sounds fine; only the killing one went silent,
        /// which is exactly the one you notice. A detached one-shot outlives the object
        /// that caused it.
        /// </summary>
        void SpawnBreakEffect(DamageInfo info)
        {
            if (surfaceLibrary == null) return;

            SurfaceType surface = Surface.Of(GetComponentInChildren<Collider>(), SurfaceType.Stone);
            Vector3 point = info.point.sqrMagnitude > 0.0001f ? info.point : transform.position;
            Vector3 dir = info.direction.sqrMagnitude > 0.0001f ? info.direction : Vector3.up;

            SurfaceImpact.Spawn(surfaceLibrary, surface, point, dir);

            // A few more around the body — one burst at the contact point reads as a
            // localized hit, not as the whole prop failing.
            for (int i = 0; i < extraBreakBursts; i++)
            {
                Vector3 p = transform.position + Random.insideUnitSphere * breakBurstSpread;
                SurfaceImpact.Spawn(surfaceLibrary, surface, p, Random.onUnitSphere);
            }
        }

        void SpawnDebris(DamageInfo info)
        {
            // COMPOSE the fractured prefab's own root rotation. Instantiate's rotation
            // argument REPLACES the prefab's root rotation outright, so an imported FBX's
            // axis correction (the -90 X Blender exports usually carry) is silently
            // discarded and the debris lands rotated by exactly that much — the same
            // invariant the kit placer follows: "compose with the prefab's own root
            // rotation". debrisRotationOffset is the escape hatch for a fracture asset
            // authored on different axes from its intact counterpart.
            Quaternion rot = transform.rotation
                           * fracturedPrefab.transform.rotation
                           * Quaternion.Euler(debrisRotationOffset);

            // Scale is deliberately NOT overridden — the fractured prefab's authored
            // scale is already correct for its own meshes, and forcing the intact prop's
            // lossyScale onto it double-applies whenever the two share a convention.
            GameObject debris = Instantiate(fracturedPrefab, transform.position, rot);
            EnsureConvexColliders(debris);

            // Burst away from the blow. Falls back to the prop's own centre when the
            // damage carried no usable point (environment/impact damage at the origin).
            Vector3 origin = info.point.sqrMagnitude > 0.0001f ? info.point : transform.position;

            foreach (var rb in debris.GetComponentsInChildren<Rigidbody>())
            {
                Vector3 away = rb.worldCenterOfMass - origin;
                if (away.sqrMagnitude < 0.0001f) away = Random.onUnitSphere;
                away.Normalize();

                Vector3 dir = Vector3.Lerp(away, Vector3.up, burstUpBias).normalized;
                rb.AddForce(dir * burstForce, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * burstTorque, ForceMode.VelocityChange);
            }

            var cleanup = debris.AddComponent<DebrisCleanup>();
            cleanup.lifetime = debrisLifetime;
            cleanup.shrinkTime = debrisShrinkTime;
        }

        // Warn once per fracture asset, not once per break.
        static readonly System.Collections.Generic.HashSet<GameObject> warnedConcave =
            new System.Collections.Generic.HashSet<GameObject>();

        /// <summary>
        /// PhysX only accepts a CONVEX MeshCollider on a DYNAMIC Rigidbody. A fracture
        /// asset exported with plain mesh colliders trips this, and the failure is bad:
        /// the rejected chunks collide with nothing and drop straight through the floor.
        ///
        /// Flipped at runtime so debris works the moment a fracture asset is dropped in,
        /// but warned about ONCE because the real fix belongs in the asset. Two reasons:
        /// setting convex here forces PhysX to cook a hull for every chunk on every break
        /// (a hitch that scales with chunk count), and cooking needs the mesh data — a
        /// fracture FBX imported WITHOUT Read/Write Enabled may cook fine in the editor
        /// and fail in a PLAYER BUILD, which is the exact silent build-only trap the
        /// stairs navmesh hit (see §10).
        ///
        /// Convexity is the right shape for fracture debris regardless — Voronoi cells
        /// are convex by construction, so nothing is lost by ticking it.
        /// </summary>
        void EnsureConvexColliders(GameObject debris)
        {
            bool fixedAny = false;

            foreach (var mc in debris.GetComponentsInChildren<MeshCollider>(true))
            {
                if (mc.convex) continue;
                // Only dynamic bodies are constrained; a static/kinematic chunk may
                // legitimately keep a concave collider.
                var rb = mc.GetComponentInParent<Rigidbody>();
                if (rb == null || rb.isKinematic) continue;

                mc.convex = true;
                fixedAny = true;
            }

            if (!fixedAny || !warnedConcave.Add(fracturedPrefab)) return;

            Debug.LogWarning(
                $"[Destructible] '{fracturedPrefab.name}' has non-convex MeshColliders on dynamic " +
                "Rigidbodies — PhysX rejects those, so the chunks would collide with nothing and fall " +
                "through the floor. Flipped to convex at runtime, but FIX IT IN THE ASSET: select the " +
                "chunks and tick Convex on their Mesh Colliders. Doing it at runtime re-cooks a hull " +
                "per chunk on every break, and cooking needs mesh data — a fracture FBX without " +
                "Read/Write Enabled can cook in the editor and fail in a build.", this);
        }

    }
}
