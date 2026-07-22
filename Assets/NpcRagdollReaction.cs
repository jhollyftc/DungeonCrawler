using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Physically directional hit reactions via a BLENDED RAGDOLL — the real
    /// thing, not the procedural spring (NpcBoneReaction). On a hit the skeleton
    /// briefly simulates as a ragdoll, recoiling in the actual direction and
    /// force of the blow, then blends back into the animation. A killing blow
    /// leaves it ragdolled for good.
    ///
    /// THE RULE that keeps blended ragdoll from jittering: animation and physics
    /// NEVER drive the same bone at the same time.
    ///   - Physics phase: Animator OFF, bodies non-kinematic. Physics owns the
    ///     skeleton completely — the impulse throws it, joints + gravity shape the
    ///     recoil. No fight.
    ///   - Blend-back phase: Animator ON, bodies kinematic. We Slerp each bone
    ///     from the captured ragdoll pose toward the live animated pose over a
    ///     window. We own the transforms; the Animator is just the target. No fight.
    /// The two phases are disjoint, so nothing oscillates.
    ///
    /// Reaction STRENGTH scales with the blow's momentum-derived impulse (the same
    /// number behind knockback and audio): a graze is a fast quarter-second twitch
    /// in the hit direction; a barrel to the chest is a longer, deeper crumple.
    /// Light grazes below minImpulse fall through to the cheap spring flinch.
    ///
    /// SETUP: run the humanoid Ragdoll Wizard on the goblin (GameObject > 3D
    /// Object > Ragdoll…), mapping hips/spine/head/arms/legs. It adds Rigidbody +
    /// Collider + CharacterJoint per bone. Put those colliders on the NPC layer
    /// (NPC×NPC collision is already off, so they won't fight the capsule or each
    /// other). Then add this component; it finds the bodies itself.
    ///
    /// FIELD LESSON — wild limb whipping on death, isolated to real hits (a manual
    /// drop-into-gravity test looks fine): the project's default solver iterations
    /// (6, sized for simple single bodies) badly under-resolve a many-joint ragdoll
    /// chain under a strong impulse, the same class of instability PhysicsDoor's
    /// hinge needed raising well above that default to fix. Compounded by DEATH
    /// reusing the live reaction's forceScale (tuned for a graze-strength flinch) on
    /// top of NpcHitReactions' flat killing-blow boost — a strong knockback (heavy
    /// swing, bash) then hit the ragdoll far harder than a flinch ever does, and
    /// death has no early blend-back to cut the resulting chaos short. Fixed with
    /// per-bone solverIterations/solverVelocityIterations and a separate, lower
    /// deathForceScale.
    /// </summary>
    [RequireComponent(typeof(Health))]
    [DisallowMultipleComponent]
    public class NpcRagdollReaction : MonoBehaviour
    {
        [Header("Refs (auto-found)")]
        public Animator animator;

        [Header("Reaction gating")]
        [Tooltip("Hits with impulse (m/s) below this don't ragdoll — too light to bother; they fall through to the spring flinch. ~a glancing melee tick.")]
        public float minImpulse = 2f;
        [Tooltip("Impulse (m/s) that counts as a FULL-strength reaction: longest physics time, biggest recoil. ~a hard barrel throw.")]
        public float fullImpulse = 7f;

        [Header("Timing (scaled by hit strength)")]
        [Tooltip("Seconds the skeleton is fully physics-driven — the recoil window, and THE reason a reaction reads as directional. Too short and the body barely leaves its rest pose before the blend yanks it back, so every hit looks like the same tiny twitch no matter the direction. This needs to be long enough for the recoil to actually develop — 0.3-0.6s. Light hit → short end, full hit → long end.")]
        public Vector2 physicsTime = new Vector2(0.3f, 0.6f);
        [Tooltip("Seconds spent blending the ragdoll pose back into the animation. The recoiled pose is held near-full for the first part of this, so a longer blend also means the reaction is VISIBLE longer before it recovers.")]
        public Vector2 blendTime = new Vector2(0.35f, 0.6f);

        [Header("Scope")]
        [Tooltip("ON: only the UPPER body (spine → head + arms) ragdolls on a hit; the hips and legs stay planted (they freeze in their animated stance for the brief window). This is why a headshot no longer flips the legs up. Death always uses the FULL body regardless. Needs a humanoid rig to classify the legs.")]
        public bool upperBodyOnly = true;

        [Header("Force")]
        [Tooltip("Multiplies the blow's impulse into ragdoll force. THE strength dial.")]
        public float forceScale = 2.5f;
        [Tooltip("Solver iterations per ragdoll bone. The Unity PROJECT DEFAULT (6) is sized for simple single bodies, not a many-joint ragdoll chain under a strong impulse — too few iterations under-resolves the CharacterJoint chain, and the symptom is limbs whipping/oscillating wildly right after a hard hit. Most visible on DEATH, where gravity stays on and nothing blends it back early so you watch the instability play out. Same lesson as PhysicsDoor's hinge (also needed well above the project default) — raise this, don't lower the force, to fix jitter without making hits feel weaker.")]
        public int solverIterations = 20;
        [Tooltip("Velocity solver iterations per bone. Same reasoning as Solver Iterations.")]
        public int solverVelocityIterations = 8;
        [Tooltip("Bones within this radius (m) of the impact also react, falling off with distance.")]
        public float spreadRadius = 0.7f;
        [Tooltip("How much neighbouring bones (arms) TRAIL the struck torso vs. staying with the animation. Small: they follow the tipping body rather than punching off on their own. 0 = only the struck bone reacts.")]
        [Range(0f, 1f)] public float neighborTrail = 0.2f;
        [Tooltip("Lift the force application point this far ABOVE the hit (m), toward the head. The torso is anchored at the hips, so it only tips DIRECTIONALLY when the force has leverage over that pivot — applying at the hip-height centre of mass barely rotates it (every direction looked the same). Raise for a bigger directional lean.")]
        public float leverLift = 0.25f;

        [Header("Collision")]
        [Tooltip("Layers the ragdoll bones IGNORE while simulating — set to your world geometry so arms don't catch on walls and flip the sim. The joints + gravity still shape the recoil.")]
        public LayerMask ignoreWhileActive;

        [Header("Debug")]
        [Tooltip("Draw the applied blow direction (red ray from the hit point) and log the struck bone. Turn on to SEE whether the force direction matches the strike — if the ray points right but the body reacts forward, the CharacterJoint is funneling the motion (granularity/axis), not the force being wrong.")]
        public bool debugForce = false;

        [Header("Debug orbit tool (play mode)")]
        [Tooltip("Enable the rotating test-force. Point the goblin somewhere with its brain OFF, then drag Debug Angle to fire a hit from any direction and watch it react — a controlled way to check all sides without circling and swinging.")]
        public bool debugTool = false;
        [Tooltip("Compass direction (deg) the test force PUSHES the body. 0 = the body's forward, 90 = its right, 180 = back, 270 = left. Drag this to orbit the force around the goblin.")]
        [Range(0f, 360f)] public float debugAngle = 0f;
        [Tooltip("Vertical tilt of the test force (deg). + pushes up, - drives down. Keep near 0 to test the horizontal reaction.")]
        [Range(-60f, 60f)] public float debugElevation = 0f;
        [Tooltip("Test force strength (m/s of impulse).")]
        public float debugStrength = 6f;
        [Tooltip("Which bone height to strike. 0 = hips, 1 = head — 0.6 ≈ chest.")]
        [Range(0f, 1f)] public float debugHeight = 0.6f;
        [Tooltip("Auto-fire a test hit every Debug Interval seconds while Debug Tool is on, so you can drag the angle and watch continuously. Off = fire only via the context-menu 'Fire Debug Hit'.")]
        public bool debugAutoFire = true;
        [Tooltip("Seconds between auto-fires — long enough for each reaction to settle.")]
        public float debugInterval = 1.2f;
        [Tooltip("Debug fires go FULL RAGDOLL with no blend-back: the whole body collapses and stays limp so you see the RAW physics response to the force, uncontaminated by the upper-body split or the animation blend. Best paired with auto-fire OFF and single 'Fire Debug Hit' shots (a full ragdoll can't recover). Use it to confirm the bones/joints react correctly in isolation, then turn it off to tune the real blended reaction.")]
        public bool debugFullRagdoll = false;

        [Header("Death")]
        [Tooltip("Separate force multiplier for the KILLING blow, decoupled from the live reaction's forceScale. NpcHitReactions already adds a flat +3 boost to the death impulse so even a weak killing tap topples convincingly — multiplying THAT by forceScale (tuned for a graze-strength flinch) compounds into an excessive impulse on an already-strong killing hit (a heavy swing, a bash), which is what sends limbs whipping/oscillating at the joints. Keep this noticeably lower than forceScale — a corpse only needs a modest kick to read as a convincing collapse; gravity does the rest.")]
        public float deathForceScale = 1.2f;
        [Tooltip("Seconds the ragdoll corpse lies there before sinking away.")]
        public float corpseLinger = 8f;
        [Tooltip("Seconds to sink the corpse through the floor, then destroy.")]
        public float sinkTime = 2.5f;
        [Tooltip("How far down (m) the corpse sinks.")]
        public float sinkDepth = 1.8f;

        // Discovered ragdoll skeleton.
        Rigidbody[] bodies;
        Collider[] cols;
        Transform[] boneTf;
        LayerMask[] originalExclude;
        bool[] originalGravity;
        Quaternion[] blendFromLocal;
        bool[] isLowerBody;   // hips + legs — stay kinematic (planted) during an upper-body reaction
        bool[] activeNow;     // ragdolled this reaction (respects upperBodyOnly; Die overrides to all)

        Health health;
        NpcLocomotion loco;
        MeleeAttack melee;
        NpcHeadTrack headTrack;
        NpcBoneReaction spring;

        bool reacting;
        bool blending;
        float blendProgress, blendDuration;
        Coroutine routine;

        void Awake()
        {
            health = GetComponent<Health>();
            loco = GetComponent<NpcLocomotion>();
            melee = GetComponent<MeleeAttack>();
            headTrack = GetComponent<NpcHeadTrack>();
            spring = GetComponent<NpcBoneReaction>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);

            var bodyList = new List<Rigidbody>(GetComponentsInChildren<Rigidbody>(true));
            bodies = bodyList.ToArray();
            cols = new Collider[bodies.Length];
            boneTf = new Transform[bodies.Length];
            originalExclude = new LayerMask[bodies.Length];
            originalGravity = new bool[bodies.Length];
            blendFromLocal = new Quaternion[bodies.Length];
            isLowerBody = new bool[bodies.Length];
            activeNow = new bool[bodies.Length];

            for (int i = 0; i < bodies.Length; i++)
            {
                boneTf[i] = bodies[i].transform;
                cols[i] = bodies[i].GetComponent<Collider>();
                originalExclude[i] = cols[i] != null ? cols[i].excludeLayers : 0;
                originalGravity[i] = bodies[i].useGravity;
                // Follow the animation until hit.
                bodies[i].isKinematic = true;
                bodies[i].interpolation = RigidbodyInterpolation.Interpolate;
                // See the Solver Iterations tooltip — the project default badly
                // under-resolves a many-joint ragdoll chain under a strong impulse.
                bodies[i].solverIterations = solverIterations;
                bodies[i].solverVelocityIterations = solverVelocityIterations;
            }

            ClassifyLowerBody();

            if (bodies.Length == 0)
                Debug.LogWarning($"[NPC] {name}: NpcRagdollReaction found no ragdoll bodies — run the Ragdoll Wizard on this model first. Falling back to the spring flinch.", this);
        }

        /// <summary>
        /// Mark the hips + legs so an upper-body reaction leaves them kinematic
        /// (planted). Uses the humanoid bone map — exact and rig-name-agnostic.
        /// A ragdoll body counts as lower if it's on, or descends from, a leg root.
        /// </summary>
        void ClassifyLowerBody()
        {
            if (animator == null || !animator.isHuman)
            {
                if (upperBodyOnly)
                    Debug.LogWarning($"[NPC] {name}: upperBodyOnly needs a humanoid Animator to find the legs — reacting with the FULL body instead.", this);
                return;
            }

            // Hips itself is lower (but NOT its descendants — the spine descends
            // from it too). Each leg root AND its descendants (lower leg, foot) are
            // lower.
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform leftLeg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform rightLeg = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);

            for (int i = 0; i < bodies.Length; i++)
            {
                Transform t = boneTf[i];
                isLowerBody[i] =
                    t == hips ||
                    (leftLeg != null && (t == leftLeg || t.IsChildOf(leftLeg))) ||
                    (rightLeg != null && (t == rightLeg || t.IsChildOf(rightLeg)));
            }
        }

        public bool HasRagdoll => bodies != null && bodies.Length > 0;

        /// <summary>
        /// Physically recoil from a hit and blend back. `impulse` = direction ×
        /// strength (m/s), the same momentum-derived vector as knockback. Returns
        /// false if the hit was too light (caller uses the spring instead).
        /// </summary>
        public bool ReactToHit(Vector3 point, Vector3 impulse)
        {
            if (!HasRagdoll || health.IsDead) return false;
            float strength = impulse.magnitude;
            if (strength < minImpulse) return false;

            float k = Mathf.InverseLerp(minImpulse, fullImpulse, strength);
            float pt = Mathf.Lerp(physicsTime.x, physicsTime.y, k);
            float bt = Mathf.Lerp(blendTime.x, blendTime.y, k);

            if (routine != null) StopCoroutine(routine);
            routine = StartCoroutine(ReactRoutine(point, impulse, pt, bt));
            return true;
        }

        IEnumerator ReactRoutine(Vector3 point, Vector3 impulse, float pt, float bt)
        {
            EnterPhysics(fullBody: false, gravity: false);   // a flinch is impulse-driven, not a fall
            ApplyForce(point, impulse * forceScale);

            yield return new WaitForSeconds(pt);

            // Capture where physics left the ragdolled bones, then hand control
            // back to the animator and blend toward it. Untouched (lower) bones
            // weren't moved, so they aren't blended.
            for (int i = 0; i < bodies.Length; i++)
                if (activeNow[i]) blendFromLocal[i] = boneTf[i].localRotation;
            ExitPhysics();

            blending = true;
            blendProgress = 0f;
            blendDuration = Mathf.Max(0.01f, bt);
            yield return new WaitForSeconds(bt);
            blending = false;

            EndReaction();
            routine = null;
        }

        float debugNextFire;

        void Update()
        {
            if (!debugTool || !Application.isPlaying) return;

            Vector3 dir = DebugDir();
            Vector3 at = DebugPoint();
            // Show where the next hit comes from and where it pushes.
            Debug.DrawLine(at - dir * 0.6f, at, Color.yellow);
            Debug.DrawRay(at, dir * 0.4f, Color.red);

            if (debugAutoFire && Time.time >= debugNextFire && !reacting)
            {
                debugNextFire = Time.time + debugInterval;
                DebugFire(at, dir * debugStrength);
            }
        }

        void DebugFire(Vector3 at, Vector3 impulse)
        {
            if (!debugFullRagdoll) { ReactToHit(at, impulse); return; }

            // Isolation test: full-body ragdoll, no blend-back. Raw physics only
            // (gravity on, like a real collapse).
            if (routine != null) StopCoroutine(routine);
            blending = false;
            EnterPhysics(fullBody: true, gravity: true);
            ApplyForce(at, impulse * forceScale);
            // No ExitPhysics — it stays limp so you can inspect the settled pose.
        }

        Vector3 DebugDir()
        {
            // Angle is body-relative (0 = its forward), so orbiting reads the same
            // whichever way the goblin happens to face.
            Quaternion frame = Quaternion.Euler(-debugElevation, transform.eulerAngles.y + debugAngle, 0f);
            return (frame * Vector3.forward).normalized;
        }

        Vector3 DebugPoint()
        {
            // Strike height between hips and head, in world space.
            Transform hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            Transform head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (hips != null && head != null)
                return Vector3.Lerp(hips.position, head.position, debugHeight);
            return transform.position + Vector3.up * Mathf.Lerp(0.2f, 1.4f, debugHeight);
        }

        [ContextMenu("Fire Debug Hit")]
        void FireDebugHit()
        {
            if (!Application.isPlaying) { Debug.LogWarning("[Ragdoll] Debug hit only works in play mode.", this); return; }
            DebugFire(DebugPoint(), DebugDir() * debugStrength);
        }

        /// <summary>Editor helper: drop the full ragdoll back into animation after a debugFullRagdoll test.</summary>
        [ContextMenu("Reset Ragdoll")]
        void ResetRagdoll()
        {
            StopAllCoroutines();
            blending = false;
            ExitPhysics();
            EndReaction();
        }

        void LateUpdate()
        {
            if (!blending) return;

            // Animator has already written the animated pose this frame. Slerp
            // each bone from the captured ragdoll pose back toward it; weight falls
            // 1 → 0 across the blend. Bodies are kinematic here, so writing the
            // transform is safe and physics won't fight it.
            blendProgress += Time.deltaTime / blendDuration;
            float weight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(blendProgress));
            for (int i = 0; i < bodies.Length; i++)
                if (activeNow[i])
                    boneTf[i].localRotation = Quaternion.Slerp(boneTf[i].localRotation, blendFromLocal[i], weight);
        }

        void EnterPhysics(bool fullBody, bool gravity)
        {
            reacting = true;
            if (animator != null) animator.enabled = false;   // frozen kinematic legs hold their planted stance
            if (headTrack != null) headTrack.enabled = false; // don't fight the ragdoll for the head
            for (int i = 0; i < bodies.Length; i++)
            {
                // Upper-body reaction: hips + legs stay kinematic (planted). Death
                // (fullBody) ragdolls everything.
                bool active = fullBody || !upperBodyOnly || !isLowerBody[i];
                activeNow[i] = active;
                bodies[i].isKinematic = !active;
                if (active)
                {
                    // A FLINCH turns gravity OFF: otherwise, over the reaction
                    // window, the free-falling body collapses under gravity in its
                    // natural imbalance direction (always the same way) and swamps
                    // the directional impulse. Impulse-only means the recoil goes
                    // where the BLOW sent it, and the blend brings it home. DEATH
                    // keeps gravity — a corpse should collapse.
                    bodies[i].useGravity = gravity;

                    // Ignore world geometry ONLY during a gravity-off flinch, so
                    // brief arm swings don't catch on walls and flip the sim. A
                    // DEATH (gravity on) must keep colliding, or the corpse falls
                    // straight THROUGH the floor instead of landing on it.
                    if (cols[i] != null)
                        cols[i].excludeLayers = gravity ? originalExclude[i] : ignoreWhileActive;
                }
            }
            // The body reacts in place — freeze pathing and attacks for the window.
            if (loco != null) loco.SpeedMultiplier = 0f;
            if (melee != null) melee.Suppressed = true;
        }

        void ExitPhysics()
        {
            if (animator != null) animator.enabled = true;
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].isKinematic = true;
                bodies[i].useGravity = originalGravity[i];
                if (cols[i] != null) cols[i].excludeLayers = originalExclude[i];
            }
        }

        void EndReaction()
        {
            reacting = false;
            if (headTrack != null) headTrack.enabled = true;
            if (loco != null) loco.SpeedMultiplier = 1f;
            if (melee != null) melee.Suppressed = false;
        }

        void ApplyForce(Vector3 point, Vector3 force)
        {
            // DIRECTION-RELIABLE: push each reacting bone LINEARLY (at its centre of
            // mass) along the blow, weighted by nearness to the impact — so every
            // bone recoils AWAY from the attacker regardless of how precise the hit
            // point is, and the CharacterJoints supply the natural secondary bend.
            // A small at-point component (torqueMix) adds localized snap for the
            // nearest bone only, where the lever error is smallest.
            int nearest = -1;
            float best = float.MaxValue;
            for (int i = 0; i < bodies.Length; i++)
            {
                if (!activeNow[i]) continue;
                float d = (boneTf[i].position - point).sqrMagnitude;
                if (d < best) { best = d; nearest = i; }
            }
            if (nearest < 0) return;

            // LEVER the struck body: apply the force at a point RAISED toward the
            // head, not at the centre of mass. The upper body is anchored at the
            // hips, so a COM force barely rotates it (that's why every direction
            // looked identical — you were only seeing the arms droop). A force with
            // a lever arm over the hip pivot tips the whole torso in the blow's
            // direction. This is the directional reaction.
            Vector3 leverPoint = point + Vector3.up * leverLift;
            bodies[nearest].AddForceAtPosition(force, leverPoint, ForceMode.Impulse);

            if (debugForce)
            {
                Debug.DrawRay(leverPoint, force.normalized * 0.6f, Color.red, 2f);
                Debug.Log($"[Ragdoll] {name}: struck '{boneTf[nearest].name}', blow dir {force.normalized} (world), lever at {leverPoint}.", this);
            }

            // Neighbours (arms) trail the tipping torso lightly, at their COM, so
            // they follow rather than punch off independently.
            if (neighborTrail > 0f)
                for (int i = 0; i < bodies.Length; i++)
                {
                    if (i == nearest || !activeNow[i]) continue;
                    float dist = Vector3.Distance(boneTf[i].position, point);
                    if (dist > spreadRadius) continue;
                    bodies[i].AddForce(force * (1f - dist / spreadRadius) * neighborTrail, ForceMode.Impulse);
                }
        }

        /// <summary>Full ragdoll death — stay limp, no blend back. The killing blow's impulse throws it. Owns its own linger + sink + destroy.</summary>
        public void Die(Vector3 point, Vector3 impulse)
        {
            if (!HasRagdoll) return;
            if (routine != null) StopCoroutine(routine);
            StopAllCoroutines();
            blending = false;

            EnterPhysics(fullBody: true, gravity: true);   // a corpse collapses whole, under gravity
            ApplyForce(point, impulse * deathForceScale);  // deathForceScale, NOT forceScale — see its tooltip
            StartCoroutine(DeathRoutine());
        }

        IEnumerator DeathRoutine()
        {
            yield return new WaitForSeconds(corpseLinger);

            // Freeze the settled pose, then sink every bone (moving the root won't —
            // ragdoll bones live in world space, independent of the transform).
            for (int i = 0; i < bodies.Length; i++) bodies[i].isKinematic = true;

            var start = new Vector3[bodies.Length];
            for (int i = 0; i < bodies.Length; i++) start[i] = boneTf[i].position;

            float t = 0f;
            while (t < sinkTime)
            {
                t += Time.deltaTime;
                float k = t / sinkTime;
                float drop = sinkDepth * k * k;   // ease-in: unnoticeable start
                for (int i = 0; i < bodies.Length; i++)
                    boneTf[i].position = start[i] + Vector3.down * drop;
                yield return null;
            }
            Destroy(gameObject);
        }
    }
}
