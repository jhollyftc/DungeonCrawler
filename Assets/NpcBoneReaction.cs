using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Per-bone hit reaction that BLENDS with animation: a hit injects angular
    /// impulses into the bones nearest the impact (distance falloff), and every
    /// disturbed bone spring-returns to its animated pose. Offsets are applied in
    /// LateUpdate AFTER the Animator has written the frame's pose and are
    /// re-derived every frame, so this composes with walk/idle/death clips by
    /// construction — the animation never fights the reaction, it carries it.
    ///
    /// This is the standard shipped alternative to true ragdoll-blending, chosen
    /// deliberately: a real ragdoll needs per-bone rigidbodies + joints, which
    /// Unity's wizard only auto-builds for HUMANOID rigs — this goblin is a
    /// generic tripo rig, so that authoring would be manual and fragile against
    /// re-exports. The impulses here come from the momentum-derived DamageInfo,
    /// so the INPUT is real physics even though the solve is a spring: a heavy
    /// barrel rocks the whole torso, a skull snaps one shoulder. If a true
    /// ragdoll ever lands, it replaces this behind the same ApplyHit() call.
    ///
    /// Runs only while disturbed — zero cost for NPCs that haven't been hit.
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcBoneReaction : MonoBehaviour
    {
        [Header("Springs")]
        [Tooltip("How hard bones pull back to the animated pose. Higher = snappier recovery.")]
        public float stiffness = 140f;
        [Tooltip("How fast the wobble dies out. Lower = loose and rubbery, higher = one clean flinch. Critically damped is ~2*sqrt(stiffness).")]
        public float damping = 16f;

        [Header("Hit response")]
        [Tooltip("Degrees of bone deflection per m/s of hit impulse, at the closest bone. The single strength dial.")]
        public float degreesPerImpulse = 6f;
        [Tooltip("Bones within this distance (m) of the hit point react, scaled down with distance. ~torso-sized spreads a body blow; small = localized flinch.")]
        public float hitRadius = 0.7f;
        [Tooltip("Hard cap (deg) on any bone's deflection so a massive hit distorts, never breaks, the silhouette.")]
        public float maxDeflection = 40f;

        struct BoneState
        {
            public Transform bone;
            public Vector3 rot;     // current offset (deg, local axis-angle)
            public Vector3 rotVel;  // deg/s
        }

        BoneState[] bones;
        bool active;

        void Awake()
        {
            // Rig-agnostic bone discovery: whatever the skinned mesh actually
            // skins to. Works for any generic rig, survives re-exports, and never
            // depends on bone naming.
            var seen = new HashSet<Transform>();
            var list = new List<BoneState>();
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>())
                foreach (var b in smr.bones)
                    if (b != null && seen.Add(b))
                        list.Add(new BoneState { bone = b });
            bones = list.ToArray();

            if (bones.Length == 0)
            {
                Debug.LogWarning($"[NPC] {name}: NpcBoneReaction found no skinned bones — " +
                                 "is the model a SkinnedMeshRenderer with an armature?", this);
                enabled = false;
            }
        }

        /// <summary>
        /// Kick the skeleton. `impulse` is direction * strength (m/s) — pass the
        /// same momentum-derived value the knockback uses so the flinch and the
        /// shove always agree about how hard the hit was.
        /// </summary>
        public void ApplyHit(Vector3 worldPoint, Vector3 impulse)
        {
            float strength = impulse.magnitude;
            if (strength < 0.01f || bones == null) return;
            Vector3 dir = impulse / strength;

            for (int i = 0; i < bones.Length; i++)
            {
                float dist = Vector3.Distance(bones[i].bone.position, worldPoint);
                if (dist > hitRadius) continue;

                float falloff = 1f - dist / hitRadius;

                // The bone rotates about the axis perpendicular to the blow — a
                // shove from the left pitches it sideways, a frontal hit rocks it
                // back. Slight per-bone scatter so a body blow doesn't hinge every
                // bone identically like a sheet of plywood.
                Vector3 axis = Vector3.Cross(dir, Vector3.up);
                if (axis.sqrMagnitude < 0.01f) axis = Vector3.Cross(dir, Vector3.right);
                axis = (axis.normalized + UnityEngine.Random.insideUnitSphere * 0.25f).normalized;

                Vector3 localAxis = bones[i].bone.InverseTransformDirection(axis);
                bones[i].rotVel += localAxis * (strength * degreesPerImpulse * falloff * 8f);
            }
            active = true;
        }

        void LateUpdate()
        {
            if (!active) return;

            float dt = Time.deltaTime;
            float energy = 0f;

            for (int i = 0; i < bones.Length; i++)
            {
                ref BoneState s = ref bones[i];
                if (s.bone == null) continue;

                // Damped spring toward zero offset (zero = the animated pose).
                s.rotVel += (-stiffness * s.rot - damping * s.rotVel) * dt;
                s.rot += s.rotVel * dt;

                float mag = s.rot.magnitude;
                if (mag > maxDeflection) s.rot *= maxDeflection / mag;

                // The Animator has already posed this bone this frame; our offset
                // multiplies ON TOP, so the reaction rides whatever is playing.
                if (mag > 0.01f)
                    s.bone.localRotation *= Quaternion.Euler(s.rot);

                energy += mag + s.rotVel.magnitude;
            }

            // Everything settled: stop touching bones entirely until the next hit.
            if (energy < 0.05f)
            {
                for (int i = 0; i < bones.Length; i++)
                {
                    bones[i].rot = Vector3.zero;
                    bones[i].rotVel = Vector3.zero;
                }
                active = false;
            }
        }
    }
}
