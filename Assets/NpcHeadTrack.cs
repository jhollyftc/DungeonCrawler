using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// The goblin watches you: the head bone turns toward the player when they're
    /// close, blended smoothly and clamped to a believable neck range. Runs in
    /// LateUpdate on top of the Animator's pose (same layering as NpcBoneReaction),
    /// so it rides walking/idling — the body keeps doing whatever it's doing while
    /// the head gives you the goblin's attention. Cheap, and it does more for
    /// perceived awareness than any AI logic: being LOOKED AT is what makes an NPC
    /// feel alive.
    ///
    /// Rig-agnostic on purpose: rather than assuming the head bone's local axes
    /// (a Blender bone's "forward" is rarely Unity's +Z), it applies a rotation
    /// delta from the body's forward toward the target, clamped to yaw/pitch
    /// limits. Works on any armature — assign the head bone, done.
    ///
    /// Also REST-ORIENTATION-agnostic: the yaw/pitch clamp is measured in the
    /// body's own local frame (transform.right/up/forward), not world axes — a
    /// standing NPC's "flat, world-Y-up" plane happens to match its own horizontal
    /// plane only because it's upright, not because that's a rule. The identical
    /// component works unmodified on an NPC lying flat (e.g. a skeleton in a
    /// coffin, its own forward pointing at the ceiling): yaw still means "turns its
    /// head side to side," pitch still means "tips it up/down," both correct
    /// relative to however the body actually sits in the world.
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcHeadTrack : MonoBehaviour
    {
        [Tooltip("The head bone from the armature hierarchy. Left empty, the first skinned bone whose name contains 'head' is used (and a warning names the pick so you can verify).")]
        public Transform headBone;
        [Tooltip("Which transform's forward/up/right defines the neck's rest frame for the yaw/pitch clamp. Left empty, uses this NPC's own root — correct for a standing NPC, whose root rotation IS its visual orientation. But if the rest pose comes from ANIMATION instead of a rotated root — e.g. a skeleton lying flat in a coffin via a 'lying down' clip, its root left completely unrotated at the standing default — the root's forward/up no longer means anything relative to what's actually on screen. Assign a bone that DOES track the current animated pose (its hips or spine/chest bone) so the clamp measures against the character's real current orientation instead of an un-posed root.")]
        public Transform bodyReference;

        /// <summary>The transform whose forward/up/right defines "body orientation" for the clamp and the final rotation — bodyReference if assigned, else this NPC's own root.</summary>
        Transform Body => bodyReference != null ? bodyReference : transform;

        [Header("When to track")]
        [Tooltip("Track the player when they're within this range (m).")]
        public float trackRadius = 6f;
        [Tooltip("ON: only track while the NPC actually knows about the player (sees them, or awareness above its investigate threshold). OFF: track anyone close — spookier, but it telegraphs detection the stealth system hasn't granted.")]
        public bool onlyWhenAware = true;

        [Header("Neck limits")]
        [Tooltip("Max head yaw (deg) away from body forward. Beyond it the head gives up rather than owl-turning.")]
        public float maxYaw = 70f;
        [Tooltip("Max head pitch (deg) up/down.")]
        public float maxPitch = 35f;

        [Header("Feel")]
        [Tooltip("How fast the head turns onto / off the target (higher = snappier).")]
        public float blendSpeed = 6f;
        [Tooltip("Full head-turn strength up close fades to zero at trackRadius, starting to fade at this fraction of it.")]
        [Range(0f, 1f)] public float falloffStart = 0.6f;

        NpcPerception senses;
        Health health;
        Transform playerEye;
        float weight;
        Vector3 lastLookDir;   // held while the weight fades out, so losing the target EASES the head back instead of snapping it

        void Awake()
        {
            senses = GetComponent<NpcPerception>();
            health = GetComponent<Health>();

            if (headBone == null)
            {
                foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    foreach (var b in smr.bones)
                    {
                        if (b != null && b.name.ToLowerInvariant().Contains("head"))
                        {
                            headBone = b;
                            break;
                        }
                    }
                    if (headBone != null) break;
                }
                if (headBone != null)
                    Debug.Log($"[NPC] {name}: head tracking auto-picked bone '{headBone.name}' — assign Head Bone explicitly if that's wrong.", this);
            }
            if (headBone == null)
            {
                Debug.LogWarning($"[NPC] {name}: no head bone found (none assigned, none named '*head*') — head tracking disabled.", this);
                enabled = false;
            }
        }

        void LateUpdate()
        {
            if (health != null && health.IsDead) { weight = 0f; return; }

            EnsurePlayer();

            float target = 0f;
            Vector3 lookDir = Vector3.zero;

            if (playerEye != null)
            {
                Vector3 to = playerEye.position - headBone.position;
                float dist = to.magnitude;

                bool wants = dist < trackRadius;
                if (wants && onlyWhenAware && senses != null)
                    wants = senses.CurrentTarget != null || senses.Awareness01 >= senses.investigateThreshold;

                if (wants && dist > 0.05f)
                {
                    lookDir = to / dist;

                    // Clamp to the neck's range, measured in Body's OWN local frame
                    // (right/up/forward) rather than world axes — a standing goblin's
                    // "flat, world-Y-up" plane happens to match its own horizontal
                    // plane, but that's a coincidence of being upright, not a rule. A
                    // skeleton lying flat on its back in a coffin has its OWN forward
                    // pointing at the ceiling and its OWN up pointing at its feet;
                    // measuring against world axes there would be nonsense — and if
                    // that lying-flat pose comes from ANIMATION rather than a rotated
                    // root, even the ROOT's own axes are wrong (see bodyReference).
                    // Atan2 is SIGNED (-180..180), unlike the old Vector3.Angle it
                    // replaced (always unsigned, 0..180) — MUST compare its Abs(),
                    // exactly like pitch already does. Missing this was a real bug:
                    // any time the player was behind-and-to-ONE-side (a negative yaw),
                    // "yaw <= maxYaw" passed trivially (any negative number is <= a
                    // positive maxYaw), completely bypassing the clamp on that side —
                    // the head could track all the way around behind, but only from
                    // one direction, which is exactly the asymmetric symptom this
                    // produced.
                    Vector3 localLook = Body.InverseTransformDirection(lookDir);
                    float yaw = Mathf.Atan2(localLook.x, localLook.z) * Mathf.Rad2Deg;
                    float pitch = Mathf.Atan2(localLook.y, new Vector2(localLook.x, localLook.z).magnitude) * Mathf.Rad2Deg;

                    if (Mathf.Abs(yaw) <= maxYaw && Mathf.Abs(pitch) <= maxPitch)
                    {
                        // Full strength up close, fading out toward the radius edge.
                        float fadeFrom = trackRadius * falloffStart;
                        target = dist <= fadeFrom ? 1f
                               : 1f - Mathf.InverseLerp(fadeFrom, trackRadius, dist);
                    }
                }
            }

            if (target > 0f) lastLookDir = lookDir;   // only update while actively tracking
            weight = Mathf.MoveTowards(weight, target, blendSpeed * Time.deltaTime);
            if (weight < 0.001f || lastLookDir == Vector3.zero) return;

            // World-space delta from "where the body faces" to "where the player
            // is" (or was, while fading out), applied on top of the animated pose.
            // No assumptions about the head bone's local axes — works on any rig.
            // Same Body reference as the clamp above — never mix the two.
            Quaternion delta = Quaternion.FromToRotation(Body.forward, lastLookDir);
            headBone.rotation = Quaternion.Slerp(Quaternion.identity, delta, weight) * headBone.rotation;
        }

        void EnsurePlayer()
        {
            if (playerEye != null) return;
            var fpc = FindObjectOfType<FirstPersonController>();
            if (fpc != null) playerEye = fpc.cam != null ? fpc.cam : fpc.transform;
        }
    }
}
