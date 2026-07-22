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

        [Header("Debug — hit direction arrows")]
        [Tooltip("Draw an ARROW (not a flat line) for every hit that lands — real combat, thrown props, AND the debug tool below — showing the exact blow direction and point. A plain ray reads ambiguously for a 3D-angled blow (e.g. Heavy Overhead's downward chop); the arrowhead is a flared cross of 4 short lines around the tip so it reads correctly from any camera angle, not just from directly above or beside.")]
        public bool showHitArrows = true;
        [Tooltip("Real-hit arrow color (red = something actually landed).")]
        public Color hitArrowColor = Color.red;
        [Tooltip("Debug-tool LIVE PREVIEW arrow color (orange = aiming, hasn't fired yet) — deliberately different from hitArrowColor so you can tell 'where it would land' apart from 'a hit that landed'.")]
        public Color previewArrowColor = new Color(1f, 0.55f, 0f);
        [Tooltip("Max arrow shaft length (m). A real hit's arrow scales with its actual (post-impulseScale) strength up to this cap, so a light tap and a bash visibly draw as different lengths — not just different colors/angles.")]
        public float arrowMaxLength = 1f;
        [Tooltip("Seconds a REAL hit's arrow stays visible. The debug tool's live preview redraws every frame while the tool is on, so this doesn't apply to it.")]
        public float hitArrowDuration = 1.5f;
        [Tooltip("Length (m) of the four flared arrowhead lines.")]
        public float arrowHeadLength = 0.18f;
        [Tooltip("Spread angle (deg) of the arrowhead lines away from the shaft.")]
        public float arrowHeadAngle = 22f;
        [Tooltip("Also draw a HEIGHT RULER for every hit: a vertical hips→head reference line (offset to the side, so it's not lost inside the mesh) with a colored tick showing exactly where that hit's Y-height falls on the same 0..1 scale debugHeight uses. Real sword hits always land at roughly the SAME height (MeleeAttack.TryHit uses the sweep's fixed eye-height origin — ClosestPoint on the victim from there, regardless of which swing landed it; only the BLOW DIRECTION varies per swing, height never has), so this is what lets you visually confirm that against the debug tool's deliberately-variable height.")]
        public bool showHeightRuler = true;
        [Tooltip("How far to the side (m) the height ruler is offset from the spine, so it doesn't get lost inside the model.")]
        public float rulerSideOffset = 0.4f;
        [Tooltip("Ruler reference-line color.")]
        public Color rulerColor = new Color(0.7f, 0.7f, 0.7f);

        /// <summary>
        /// A 3D arrow: a shaft plus a flared cross of 4 short lines at the tip (up/
        /// down/left/right relative to the shaft's own facing), so the head reads as
        /// an arrow from ANY viewing angle — a single flat 2-line head can look like a
        /// plain line when viewed nearly edge-on, exactly the risk for a steeply
        /// angled blow like Heavy Overhead's.
        /// </summary>
        void DrawArrow(Vector3 origin, Vector3 direction, float length, Color color, float duration)
        {
            if (direction.sqrMagnitude < 1e-8f || length <= 0f) return;
            Vector3 dir = direction.normalized;
            Vector3 tip = origin + dir * length;
            Debug.DrawLine(origin, tip, color, duration);

            Quaternion look = Quaternion.LookRotation(dir);
            float headLen = Mathf.Min(arrowHeadLength, length * 0.5f);
            Vector3 right = look * Quaternion.Euler(0f, 180f - arrowHeadAngle, 0f) * Vector3.forward;
            Vector3 left  = look * Quaternion.Euler(0f, 180f + arrowHeadAngle, 0f) * Vector3.forward;
            Vector3 up    = look * Quaternion.Euler(180f - arrowHeadAngle, 0f, 0f) * Vector3.forward;
            Vector3 down  = look * Quaternion.Euler(180f + arrowHeadAngle, 0f, 0f) * Vector3.forward;
            Debug.DrawRay(tip, right.normalized * headLen, color, duration);
            Debug.DrawRay(tip, left.normalized * headLen, color, duration);
            Debug.DrawRay(tip, up.normalized * headLen, color, duration);
            Debug.DrawRay(tip, down.normalized * headLen, color, duration);
        }

        Animator bodyAnimator;

        /// <summary>Hips/head world positions for the height ruler and DebugPoint's height scrub. Null if the rig isn't humanoid.</summary>
        bool TryGetHipsHead(out Vector3 hips, out Vector3 head)
        {
            if (bodyAnimator == null) bodyAnimator = GetComponentInChildren<Animator>();
            if (bodyAnimator != null && bodyAnimator.isHuman)
            {
                Transform h = bodyAnimator.GetBoneTransform(HumanBodyBones.Hips);
                Transform hd = bodyAnimator.GetBoneTransform(HumanBodyBones.Head);
                if (h != null && hd != null) { hips = h.position; head = hd.position; return true; }
            }
            hips = head = Vector3.zero;
            return false;
        }

        /// <summary>
        /// A vertical hips→head reference line, offset to the side, with a colored tick
        /// at the hit's Y-height projected onto it (0 = hips, 1 = head — the same scale
        /// debugHeight uses) plus a faint line connecting the tick back to the actual
        /// hit point. Real sword hits all land near the same eye-height-derived point
        /// (see MeleeAttack.TryHit) regardless of swing, so this is what lets you SEE
        /// that clustering directly, compared against the debug tool's deliberately
        /// variable height.
        /// </summary>
        void DrawHeightRuler(Vector3 hitPoint, Color color, float duration)
        {
            if (!showHeightRuler || !TryGetHipsHead(out Vector3 hips, out Vector3 head)) return;

            Vector3 side = transform.right * rulerSideOffset;
            Vector3 rulerBottom = hips + side;
            Vector3 rulerTop = head + side;
            Debug.DrawLine(rulerBottom, rulerTop, rulerColor, duration);

            float t = Mathf.InverseLerp(hips.y, head.y, hitPoint.y);
            Vector3 tick = Vector3.Lerp(rulerBottom, rulerTop, Mathf.Clamp01(t));
            Vector3 tickAxis = transform.right * 0.12f;
            Debug.DrawLine(tick - tickAxis, tick + tickAxis, color, duration);
            Debug.DrawLine(hitPoint, tick, color * 0.6f, duration);
        }

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

            // GUARANTEE something reacts: worldPoint is a point on the COLLIDER
            // SURFACE (MeleeAttack's ClosestPoint), not on any actual skeleton bone —
            // for a stylized/wide rig those can sit much farther apart than hitRadius
            // ever anticipated, silently selecting ZERO bones and producing NO visible
            // flinch no matter how high impulseScale/strength go (impulseScale only
            // scales bones already selected; it can't rescue a selection of none —
            // real field bug, real hits never flinched while the debug tool, whose
            // test point is built directly FROM bone positions, always worked). Find
            // the nearest bone regardless of hitRadius so it always gets at least a
            // floor-strength kick; bones genuinely within hitRadius still use the
            // normal distance falloff on top of that.
            int nearestIdx = -1;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < bones.Length; i++)
            {
                float d = Vector3.Distance(bones[i].bone.position, worldPoint);
                if (d < nearestDist) { nearestDist = d; nearestIdx = i; }
            }

            for (int i = 0; i < bones.Length; i++)
            {
                float dist = Vector3.Distance(bones[i].bone.position, worldPoint);
                float falloff;
                if (dist <= p.hitRadius) falloff = 1f - dist / p.hitRadius;
                else if (i == nearestIdx) falloff = 0.35f;   // outside hitRadius, but nothing else is closer — guaranteed floor
                else continue;

                Vector3 localAxis = bones[i].bone.InverseTransformDirection(worldAxis);
                bones[i].rotVel += localAxis * (strength * p.strength * falloff * 8f);
            }
            active = true;

            if (showHitArrows)
            {
                // Length scales with the ACTUAL (post-impulseScale) strength driving the
                // kick, capped at arrowMaxLength — a light tap and a bash read as visibly
                // different lengths, not just the same-size arrow in a different spot.
                float len = Mathf.Clamp(strength * 0.1f, 0.25f, arrowMaxLength);
                DrawArrow(worldPoint, impulse / rawMag, len, hitArrowColor, hitArrowDuration);
                DrawHeightRuler(worldPoint, hitArrowColor, hitArrowDuration);
            }
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

        void Update()
        {
            if (!debugTool || !Application.isPlaying) return;

            Vector3 from = DebugFromDir();
            Vector3 at = DebugPoint();
            Debug.DrawLine(at + from * 0.6f, at, Color.yellow);   // incoming, from the attacker
            if (showHitArrows)
            {
                DrawArrow(at, -from, arrowMaxLength, previewArrowColor, 0f);   // the push, live preview (orange, redrawn every frame)
                DrawHeightRuler(at, previewArrowColor, 0f);
            }

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
            if (TryGetHipsHead(out Vector3 hips, out Vector3 head)) return Vector3.Lerp(hips, head, debugHeight);
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
