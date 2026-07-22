using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Directional procedural flinch — the reliable spring reaction (bones twist
    /// off the animated pose and settle back, in LateUpdate on TOP of the clip so
    /// it never fights animation), but the twist AXIS is chosen from an authored
    /// PROFILE per attack angle. A hit from the front arches the torso back; from
    /// the side it buckles a shoulder; from behind it folds forward — each an
    /// authored feel, picked by which direction the NPC was struck from.
    ///
    /// This is the pragmatic alternative to ragdoll blending: cheap, jitter-free,
    /// rig-agnostic (bones come from the skinned mesh), and every reaction is a
    /// hand-tuned pose rather than a physics gamble. The impulse magnitude still
    /// comes from the momentum-derived DamageInfo, so a barrel rocks harder than
    /// a poke — only the DIRECTION is authored.
    ///
    /// Runs only while disturbed — zero cost for an un-hit NPC.
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcFlinch : MonoBehaviour
    {
        [Serializable]
        public class Profile
        {
            [Tooltip("Label only.")]
            public string name = "Front";
            [Tooltip("Direction the NPC was ATTACKED FROM, relative to its own facing: 0 = front, 90 = its right, 180 = behind, 270 = its left. The profile whose angle is nearest the hit is used.")]
            [Range(0f, 360f)] public float attackedFromAngle = 0f;
            [Tooltip("Which way the struck bones twist, in the NPC's LOCAL space (a rotation axis). Front hit: pitch the torso back (≈ +X). Side hit: roll/yaw (≈ Z or Y). Flip a sign if it leans the wrong way — the debug tool makes this quick to dial.")]
            public Vector3 kickAxis = new Vector3(1f, 0f, 0f);
            [Tooltip("Degrees of twist per m/s of impulse for this profile — its strength. Bigger = a heavier reaction from this angle.")]
            public float strength = 6f;
            [Tooltip("Bones within this radius (m) of the hit react, falling off with distance. Torso-sized spreads a body blow; small = a localized snap.")]
            public float hitRadius = 0.7f;
        }

        [Tooltip("One reaction per angle. Add/label as many as you like — the nearest by angle wins. Start with front/right/back/left and refine.")]
        public List<Profile> profiles = new List<Profile>
        {
            new Profile { name = "Front", attackedFromAngle = 0f,   kickAxis = new Vector3( 1f, 0f,  0f), strength = 6f },
            new Profile { name = "Right", attackedFromAngle = 90f,  kickAxis = new Vector3( 0f, 0f, -1f), strength = 6f },
            new Profile { name = "Back",  attackedFromAngle = 180f, kickAxis = new Vector3(-1f, 0f,  0f), strength = 6f },
            new Profile { name = "Left",  attackedFromAngle = 270f, kickAxis = new Vector3( 0f, 0f,  1f), strength = 6f },
        };

        [Header("Impulse scale")]
        [Tooltip("Scales the raw impulse magnitude BEFORE it drives bone rotation — the single dial for 'real combat hits look rougher/clipped compared to the debug tool.' The per-profile strength values were tuned against a low reference impulse; real combat knockback (light ~5, heavy ~10, bash ~30) is well above that, so the kick math (strength × profile.strength × falloff × 8, uncapped going in) overdrives the spring and slams into maxDeflection — a hard clip that reads as an abrupt, unfinished snap instead of a full arc, instead of the clean motion a lighter impulse produces. Turn this DOWN until a real sword hit looks as fluid as the debug tool at a strength you already like — e.g. if debugStrength 2 looks right and your light swing knocks back at 5, try ~0.4 as a starting point (2/5) and tune from there. Applies to BOTH real hits and the debug tool (same ApplyHit call), so they stay directly comparable.")]
        public float impulseScale = 0.4f;

        [Header("Spring")]
        [Tooltip("How hard bones pull back to the animated pose. Higher = snappier recovery.")]
        public float stiffness = 140f;
        [Tooltip("How fast the wobble dies. Lower = loose/rubbery, higher = one clean flinch. Critically damped ≈ 2*sqrt(stiffness).")]
        public float damping = 16f;
        [Tooltip("Hard cap (deg) on any bone's deflection, so a huge hit distorts but never breaks the silhouette.")]
        public float maxDeflection = 40f;

        struct BoneState { public Transform bone; public Vector3 rot; public Vector3 rotVel; }
        BoneState[] bones;
        bool active;

        void Awake()
        {
            var seen = new HashSet<Transform>();
            var list = new List<BoneState>();
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>())
                foreach (var b in smr.bones)
                    if (b != null && seen.Add(b))
                        list.Add(new BoneState { bone = b });
            bones = list.ToArray();

            if (bones.Length == 0)
            {
                Debug.LogWarning($"[NPC] {name}: NpcFlinch found no skinned bones.", this);
                enabled = false;
            }
        }

        /// <summary>
        /// Kick the skeleton. `impulse` = direction × strength (m/s), the same
        /// momentum-derived vector as knockback. The direction picks the profile;
        /// the magnitude scales the twist.
        /// </summary>
        public void ApplyHit(Vector3 worldPoint, Vector3 impulse)
        {
            float rawMag = impulse.magnitude;
            if (rawMag < 0.01f || bones == null || profiles.Count == 0) return;
            float strength = rawMag * impulseScale;   // impulseScale drives the KICK only; direction below stays true to the raw impulse

            Profile p = ProfileForBlow(impulse / rawMag);
            if (p == null) return;

            // Author the kick in the NPC's local frame, then convert per bone.
            Vector3 worldAxis = (transform.rotation * p.kickAxis).normalized;

            for (int i = 0; i < bones.Length; i++)
            {
                float dist = Vector3.Distance(bones[i].bone.position, worldPoint);
                if (dist > p.hitRadius) continue;
                float falloff = 1f - dist / p.hitRadius;

                Vector3 localAxis = bones[i].bone.InverseTransformDirection(worldAxis);
                bones[i].rotVel += localAxis * (strength * p.strength * falloff * 8f);
            }
            active = true;
        }

        /// <summary>Nearest profile by circular angle. `blowDir` is the push (away from the attacker).</summary>
        Profile ProfileForBlow(Vector3 blowDir)
        {
            // Direction TO the attacker is opposite the push; measure it against the
            // NPC's forward, clockwise-from-above (so +90 = its right).
            Vector3 fromDir = -blowDir;
            Vector3 flat = new Vector3(fromDir.x, 0f, fromDir.z);
            float ang = flat.sqrMagnitude < 1e-4f ? 0f
                      : (Vector3.SignedAngle(transform.forward, flat.normalized, Vector3.up) + 360f) % 360f;

            Profile best = null;
            float bestDelta = float.MaxValue;
            foreach (var p in profiles)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(ang, p.attackedFromAngle));
                if (d < bestDelta) { bestDelta = d; best = p; }
            }
            return best;
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

                s.rotVel += (-stiffness * s.rot - damping * s.rotVel) * dt;
                s.rot += s.rotVel * dt;

                float mag = s.rot.magnitude;
                if (mag > maxDeflection) s.rot *= maxDeflection / mag;

                if (mag > 0.01f) s.bone.localRotation *= Quaternion.Euler(s.rot);
                energy += mag + s.rotVel.magnitude;
            }

            if (energy < 0.05f)
            {
                for (int i = 0; i < bones.Length; i++) { bones[i].rot = Vector3.zero; bones[i].rotVel = Vector3.zero; }
                active = false;
            }
        }

        // ---------------- Debug orbit tool ----------------

        [Header("Debug orbit tool (play mode)")]
        [Tooltip("Enable the rotating test-hit. Point the goblin somewhere (brain off), then drag Debug Angle to strike it from any direction and watch which profile fires.")]
        public bool debugTool = false;
        [Tooltip("Direction the test hit comes FROM, relative to the goblin: 0 = front, 90 = its right, 180 = behind, 270 = its left. This is exactly the angle a profile matches on.")]
        [Range(0f, 360f)] public float debugAngle = 0f;
        [Tooltip("Test impulse strength (m/s).")]
        public float debugStrength = 6f;
        [Tooltip("Strike height between hips and head (0..1).")]
        [Range(0f, 1f)] public float debugHeight = 0.6f;
        [Tooltip("Auto-fire every Debug Interval seconds while on, so you can drag the angle and watch continuously.")]
        public bool debugAutoFire = true;
        public float debugInterval = 1.2f;

        float debugNextFire;
        Animator debugAnimator;

        void Update()
        {
            if (!debugTool || !Application.isPlaying) return;

            Vector3 from = DebugFromDir();
            Vector3 at = DebugPoint();
            Debug.DrawLine(at + from * 0.6f, at, Color.yellow);   // incoming, from the attacker
            Debug.DrawRay(at, -from * 0.4f, Color.red);           // the push

            if (debugAutoFire && Time.time >= debugNextFire)
            {
                debugNextFire = Time.time + debugInterval;
                FireDebug();
            }
        }

        Vector3 DebugFromDir()
        {
            return (Quaternion.Euler(0f, transform.eulerAngles.y + debugAngle, 0f) * Vector3.forward).normalized;
        }

        Vector3 DebugPoint()
        {
            if (debugAnimator == null) debugAnimator = GetComponentInChildren<Animator>();
            if (debugAnimator != null && debugAnimator.isHuman)
            {
                Transform hips = debugAnimator.GetBoneTransform(HumanBodyBones.Hips);
                Transform head = debugAnimator.GetBoneTransform(HumanBodyBones.Head);
                if (hips != null && head != null) return Vector3.Lerp(hips.position, head.position, debugHeight);
            }
            return transform.position + Vector3.up * Mathf.Lerp(0.2f, 1.4f, debugHeight);
        }

        [ContextMenu("Fire Debug Hit")]
        void FireDebug()
        {
            if (!Application.isPlaying) { Debug.LogWarning("[Flinch] Debug hit only works in play mode.", this); return; }
            Vector3 push = -DebugFromDir() * debugStrength;
            Profile p = ProfileForBlow(push.normalized);
            Debug.Log($"[Flinch] {name}: hit from {debugAngle:0}° → profile '{(p != null ? p.name : "none")}'.", this);
            ApplyHit(DebugPoint(), push);
        }
    }
}
