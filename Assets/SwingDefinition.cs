using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// One authored melee swing — the whole recipe for a single attack: its arc,
    /// timing, combat values, feel, and how it pushes a victim. PlayerMelee holds a
    /// LIST of these (the light combo) plus one heavy, and drives whichever is
    /// active. Each swing's `slash - windup` pose delta becomes its blow direction
    /// (ComputeBlowDirection), so a right-slash, left-slash, and overhead each shove
    /// a goblin a different way and fire a different NpcFlinch profile — for free.
    ///
    /// Plain [Serializable], not a MonoBehaviour or ScriptableObject: inline on the
    /// component so the previewT scrub + arc gizmo stay live-tunable per swing.
    /// Lifts wholesale into a MeleeWeapon SO when phase-6 equipment lands.
    /// </summary>
    [Serializable]
    public class SwingDefinition
    {
        [Tooltip("Label only (Right Slash, Left Slash, Overhead, Heavy…).")]
        public string name = "Swing";

        [Header("Timing (seconds / normalized 0..1)")]
        [Tooltip("Total swing time, windup through recovery.")]
        public float duration = 0.6f;
        [Tooltip("Normalized t where the windup ends and the slash launches.")]
        [Range(0.05f, 0.9f)] public float windupEnd = 0.3f;
        [Tooltip("Normalized t of the IMPACT — the single frame the sweep fires.")]
        [Range(0.1f, 0.95f)] public float impactT = 0.42f;
        [Tooltip("Normalized t where the slash arc ends and recovery eases back.")]
        [Range(0.2f, 0.95f)] public float slashEnd = 0.55f;
        [Tooltip("Extra seconds after the swing before the next can start.")]
        public float cooldown = 0.05f;

        [Header("Arc (camera-local offsets from the rest pose)")]
        [Tooltip("Pose at the top of the windup — coiled.")]
        public Vector3 windupPosition = new Vector3(0.06f, 0.07f, -0.12f);
        public Vector3 windupEuler = new Vector3(-35f, 25f, 15f);
        [Tooltip("Pose at the end of the slash. The windup→slash DELTA is also the blow direction.")]
        public Vector3 slashPosition = new Vector3(-0.10f, -0.06f, 0.16f);
        public Vector3 slashEuler = new Vector3(50f, -35f, -25f);

        [Header("Combat (pushed onto MeleeAttack before the sweep)")]
        public float damage = 10f;
        public float knockback = 5f;
        [Tooltip("Poise damage — chips the victim's poise pool. Enough in one hit = a guaranteed poise break (heavy).")]
        public float poiseDamage = 25f;
        [Tooltip("Sweep reach (m). 0 = keep MeleeAttack's default.")]
        public float range = 1.8f;
        [Tooltip("Sweep radius / swing width (m). 0 = keep MeleeAttack's default.")]
        public float sweepRadius = 0.45f;

        [Header("Feel — bigger on a heavy")]
        public float localHitstop = 0.09f;
        public float recoilDistance = 0.045f;
        [Tooltip("On a HIT, after the freeze, the blade RETREATS from the contact point back to rest over this many seconds — it met resistance, so it doesn't follow through the rest of the arc. A WHIFF ignores this and completes the arc normally.")]
        public float hitRetractTime = 0.28f;
        public float globalDipDuration = 0.05f;
        [Range(0f, 1f)] public float globalDipScale = 0.12f;
        [Tooltip("Camera punch on a LANDED hit (deg: pitch, yaw, roll).")]
        public Vector3 hitKickEuler = new Vector3(1.8f, -0.7f, -2.2f);
        [Tooltip("Tiny camera counter-motion at swing START, for weight even on a whiff.")]
        public Vector3 swingKickEuler = new Vector3(-0.5f, 0.3f, 0.7f);

        [Header("Blow direction")]
        [Tooltip("Bias toward straight-forward vs. the swing's own lateral direction. 0 = recoil purely along the slash; 1 = always straight back.")]
        [Range(0f, 1f)] public float blowForwardBias = 0.4f;
        [Tooltip("How much of the swing's VERTICAL motion becomes vertical push. Low by default (short enemies), high for a chop that visibly drives down.")]
        [Range(0f, 1f)] public float blowVerticalScale = 0.3f;

        /// <summary>
        /// The procedural arc as a pure function of normalized time: rest → windup
        /// (coil) → slash (fast, front-loaded) → recovery (ease out). Shared by the
        /// live swing, the preview scrub, and the gizmo so they can't disagree.
        /// </summary>
        public void ComputePose(float nt, out Vector3 pos, out Vector3 euler, out float suppress)
        {
            if (nt < windupEnd)
            {
                float k = Mathf.SmoothStep(0f, 1f, nt / Mathf.Max(0.0001f, windupEnd));
                pos = Vector3.Lerp(Vector3.zero, windupPosition, k);
                euler = Vector3.Lerp(Vector3.zero, windupEuler, k);
                suppress = k;
            }
            else if (nt < slashEnd)
            {
                // Front-loaded (k^0.6 rises steeply) — reads as a strike, not a wave.
                float k = Mathf.Pow((nt - windupEnd) / Mathf.Max(0.0001f, slashEnd - windupEnd), 0.6f);
                pos = Vector3.LerpUnclamped(windupPosition, slashPosition, k);
                euler = Vector3.LerpUnclamped(windupEuler, slashEuler, k);
                suppress = 1f;
            }
            else
            {
                float k = Mathf.SmoothStep(0f, 1f, (nt - slashEnd) / Mathf.Max(0.0001f, 1f - slashEnd));
                pos = Vector3.Lerp(slashPosition, Vector3.zero, k);
                euler = Vector3.Lerp(slashEuler, Vector3.zero, k);
                suppress = 1f - k;
            }
        }
    }
}
