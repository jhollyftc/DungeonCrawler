using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace DungeonGen
{
    /// <summary>
    /// Foot grounding for a generic rig, driving Animation Rigging's
    /// TwoBoneIKConstraints (one per leg). Each frame, each foot raycasts the real
    /// ground under it; while the animation has that foot in STANCE (low to the
    /// body), the IK target snaps its height to the actual surface — so feet land
    /// ON stair treads instead of floating over or sinking into them. During the
    /// swing phase the weight fades out and the animation owns the foot entirely.
    ///
    /// Division of labor: the walk CLIP still authors all the motion; this only
    /// corrects foot HEIGHT against the world. The correction converges instead of
    /// feeding back because the target height comes from the raycast ground — an
    /// external, stable reference — not from the previous frame's pose.
    ///
    /// The rig evaluates inside the Animator's update, BEFORE LateUpdate — so the
    /// targets we write here take effect on the NEXT frame's solve. That one-frame
    /// lag is the standard grounder pattern and is invisible in practice. It also
    /// means the LateUpdate bone layers (flinch springs, head track) stack cleanly
    /// on top of the IK'd pose.
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcFootIK : MonoBehaviour
    {
        [Header("Constraints (assign from the FootRig)")]
        public TwoBoneIKConstraint leftLeg;
        public TwoBoneIKConstraint rightLeg;

        [Header("Ground")]
        [Tooltip("What counts as ground. EXCLUDE the NPC layer (a goblin must not plant its foot on a neighbour) and the Viewmodel layer.")]
        public LayerMask groundMask = ~0;
        [Tooltip("Height of the foot bone above the sole when planted flat (m). Raycast hit + this = target height. Eyeball: foot bone Y in the scene minus floor Y while standing.")]
        public float footHeight = 0.08f;

        [Header("Correction limits")]
        [Tooltip("Max the foot can be LIFTED above its animated height (m) — climbing a step edge.")]
        public float maxLift = 0.45f;
        [Tooltip("Max the foot can DROP below its animated height (m). Keep small: a two-bone leg can't extend past its length, so a big drop just straightens the knee.")]
        public float maxDrop = 0.25f;

        [Header("Stance detection")]
        [Tooltip("A foot counts as PLANTED while its animated height above the character's ground point is below this (m). Above it, it's mid-swing and the animation owns it. Tune against the walk clip: too high grabs the foot mid-stride, too low never plants.")]
        public float plantThreshold = 0.18f;
        [Tooltip("How fast the IK weight blends in/out (per second).")]
        public float weightSmoothing = 12f;

        Transform leftFoot, rightFoot;
        Transform leftTarget, rightTarget;
        float leftWeight, rightWeight;
        Health health;

        void Awake()
        {
            health = GetComponent<Health>();

            if (leftLeg == null || rightLeg == null)
            {
                Debug.LogWarning($"[NPC] {name}: NpcFootIK needs both leg TwoBoneIKConstraints assigned — foot IK disabled. " +
                                 "Build the FootRig per the setup notes and drag the constraints in.", this);
                enabled = false;
                return;
            }

            leftFoot = leftLeg.data.tip;
            rightFoot = rightLeg.data.tip;
            leftTarget = leftLeg.data.target;
            rightTarget = rightLeg.data.target;

            if (leftFoot == null || rightFoot == null || leftTarget == null || rightTarget == null)
            {
                Debug.LogWarning($"[NPC] {name}: a leg constraint is missing its Tip bone or Target — foot IK disabled. " +
                                 "Each TwoBoneIKConstraint needs Root/Mid/Tip bones AND a Target (+ Hint) assigned.", this);
                enabled = false;
            }
        }

        void LateUpdate()
        {
            if (health != null && health.IsDead)
            {
                // Corpse: release the feet so the death clip plays untouched.
                leftWeight = rightWeight = 0f;
                leftLeg.weight = rightLeg.weight = 0f;
                return;
            }

            SolveFoot(leftFoot, leftTarget, ref leftWeight, leftLeg);
            SolveFoot(rightFoot, rightTarget, ref rightWeight, rightLeg);
        }

        void SolveFoot(Transform foot, Transform target, ref float weight, TwoBoneIKConstraint leg)
        {
            Vector3 footPos = foot.position;

            // Stance: is the ANIMATED foot low enough to be considered planted?
            // Measured against the character's own ground point so stairs (where
            // world Y varies underfoot) don't confuse it.
            float animatedHeight = footPos.y - transform.position.y;
            bool planted = animatedHeight < plantThreshold;

            float targetWeight = 0f;
            if (planted &&
                Physics.Raycast(footPos + Vector3.up * 1f, Vector3.down, out RaycastHit hit, 2.5f,
                                groundMask, QueryTriggerInteraction.Ignore))
            {
                float desiredY = hit.point.y + footHeight;
                float delta = Mathf.Clamp(desiredY - footPos.y, -maxDrop, maxLift);

                target.position = new Vector3(footPos.x, footPos.y + delta, footPos.z);
                // Pass the animated rotation through — the constraint drives the
                // foot's rotation to the target's at full weight, and we only want
                // to move its HEIGHT, not re-aim it.
                target.rotation = foot.rotation;
                targetWeight = 1f;
            }

            weight = Mathf.MoveTowards(weight, targetWeight, weightSmoothing * Time.deltaTime);
            leg.weight = weight;
        }
    }
}
