using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Positional-only collision retraction for a held item (sword/shield):
    /// spherecasts shoulder→tip along the weapon's own axis and, if it would
    /// clip world geometry, pulls the whole rig back along that axis so the
    /// blade shortens into view instead of poking through a wall.
    ///
    /// Third layer on top of ViewmodelSway: rest pose → sway → this clamp.
    /// MUST be invoked from the END of ViewmodelSway's LateUpdate via
    /// <see cref="Clamp"/> — never runs its own LateUpdate. Sway computes
    /// where the weapon WANTS to be; this only constrains that result, so
    /// sway and retraction never independently push the pose and fight
    /// (buzz/jitter). Rotation is left untouched in v1 — see
    /// weapon-collision-v1-plan.md for the deferred deflection (v2) design.
    ///
    /// Not seed-driven — this is real-time input-driven runtime behavior,
    /// no Hash streams involved.
    /// </summary>
    public class ViewmodelCollision : MonoBehaviour
    {
        // Rig: EITHER drag child transforms onto the blade in the Scene view
        // (recommended — no guessing at local-axis numbers) OR type raw
        // offsets below in this transform's own mesh-local space.
        [Header("Rig")]
        [Tooltip("Optional. Child transform positioned at the hand pivot / where the weapon enters view. If set, overrides shoulderOffset at Awake.")]
        public Transform shoulderAnchor;
        [Tooltip("Optional. Child transform positioned at the blade tip. If set, overrides tipOffset at Awake.")]
        public Transform tipAnchor;
        [Tooltip("Fallback if shoulderAnchor is unset — local space of this transform (this weapon's own mesh axes, NOT world axes).")]
        public Vector3 shoulderOffset = Vector3.zero;
        [Tooltip("Fallback if tipAnchor is unset — local space of this transform (this weapon's own mesh axes, NOT world axes).")]
        public Vector3 tipOffset = new Vector3(0f, 0f, 0.6f);
        [Tooltip("Draw the shoulder->tip cast line in the Scene view (green = clear, red = retracting) so you can eyeball whether the rig is aimed correctly.")]
        public bool showDebugGizmo = true;

        [Header("Cast")]
        [Tooltip("Spherecast radius — roughly the blade's half-thickness.")]
        public float castRadius = 0.04f;
        [Tooltip("Dungeon collision layers (greybox + prop/arch/column colliders). Narrow this to exclude the player/viewmodel.")]
        public LayerMask worldMask = ~0;
        [Tooltip("Buffer so the blade rests just off the surface, not touching.")]
        public float skin = 0.02f;
        [Tooltip("Cap on how far the weapon can pull back — past this it clips slightly rather than vanishing.")]
        public float maxRetraction = 0.25f;

        [Header("Smoothing (retraction amount, not the pose)")]
        [Tooltip("How fast retraction grows when approaching a wall.")]
        public float retractInSpeed = 6f;
        [Tooltip("How fast retraction eases back out when clearing a wall.")]
        public float retractOutSpeed = 2f;

        float fullLength;
        float currentRetract;

        void Awake()
        {
            // Anchors win if assigned: convert their authored world position
            // into this transform's local space (same frame the raw offset
            // fields are defined in) so Clamp()'s math doesn't need to care
            // which authoring method was used.
            if (shoulderAnchor != null) shoulderOffset = transform.InverseTransformPoint(shoulderAnchor.position);
            if (tipAnchor != null) tipOffset = transform.InverseTransformPoint(tipAnchor.position);
            fullLength = Vector3.Distance(shoulderOffset, tipOffset);
        }

        void OnDrawGizmos()
        {
            if (!showDebugGizmo) return;
            // Live values: anchors (if assigned) reflect authored edit-time
            // pose directly; raw offsets are shown relative to the current
            // transform, which is the actual pose being tested in Play mode.
            Vector3 shoulderWorld = shoulderAnchor != null ? shoulderAnchor.position : transform.TransformPoint(shoulderOffset);
            Vector3 tipWorld = tipAnchor != null ? tipAnchor.position : transform.TransformPoint(tipOffset);
            Gizmos.color = Application.isPlaying && currentRetract > 0.0001f ? Color.red : Color.green;
            Gizmos.DrawLine(shoulderWorld, tipWorld);
            Gizmos.DrawWireSphere(shoulderWorld, castRadius);
            Gizmos.DrawWireSphere(tipWorld, castRadius);
        }

        /// <summary>
        /// Clamps a candidate local pose (what ViewmodelSway is about to
        /// apply this frame) against world collision. Returns the local
        /// position to actually assign; rotation is returned unchanged by
        /// the caller — this method never touches it.
        /// </summary>
        public Vector3 Clamp(Vector3 candidateLocalPos, Quaternion candidateLocalRot)
        {
            Transform parent = transform.parent;
            Vector3 originWorld = parent != null ? parent.TransformPoint(candidateLocalPos) : candidateLocalPos;
            Quaternion worldRot = (parent != null ? parent.rotation : Quaternion.identity) * candidateLocalRot;

            Vector3 shoulderWorld = originWorld + worldRot * shoulderOffset;
            Vector3 tipWorld = originWorld + worldRot * tipOffset;
            Vector3 weaponForward = tipWorld - shoulderWorld;
            float len = weaponForward.magnitude;
            if (len < 0.0001f)
            {
                currentRetract = Mathf.MoveTowards(currentRetract, 0f, retractOutSpeed * Time.deltaTime);
                return candidateLocalPos;
            }
            weaponForward /= len;

            // A SphereCast that STARTS already overlapping a collider does
            // not report a hit against it (Unity cast quirk — the cast can't
            // compute a surface normal for something it's already inside).
            // At point-blank range the shoulder point ends up embedded in the
            // wall, so without this check the cast silently "clears" and the
            // weapon springs back into the wall it's still touching.
            float targetRetract;
            if (Physics.CheckSphere(shoulderWorld, castRadius, worldMask, QueryTriggerInteraction.Ignore))
            {
                targetRetract = maxRetraction;
            }
            else if (Physics.SphereCast(shoulderWorld, castRadius, weaponForward, out RaycastHit hit,
                                    fullLength, worldMask, QueryTriggerInteraction.Ignore))
            {
                float penetration = fullLength - hit.distance;
                targetRetract = Mathf.Clamp(penetration + skin, 0f, maxRetraction);
            }
            else
            {
                targetRetract = 0f;
            }

            float speed = targetRetract > currentRetract ? retractInSpeed : retractOutSpeed;
            currentRetract = Mathf.MoveTowards(currentRetract, targetRetract, speed * Time.deltaTime);

            if (currentRetract <= 0f) return candidateLocalPos;

            Vector3 retractedWorld = originWorld - weaponForward * currentRetract;
            return parent != null ? parent.InverseTransformPoint(retractedWorld) : retractedWorld;
        }
    }
}
