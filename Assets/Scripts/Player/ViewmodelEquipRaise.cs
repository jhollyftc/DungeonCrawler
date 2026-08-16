using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Lifts a freshly equipped weapon into view: hands empty for a beat after the old one is
    /// dropped, then the new one rises from below the frame into its rest pose.
    ///
    /// IT ANIMATES A WRAPPER, NEVER THE WEAPON ITSELF, and that is forced rather than chosen.
    /// <see cref="ViewmodelSway"/> writes `transform.localPosition` and `localRotation` on its
    /// own transform EVERY LateUpdate from a rest pose captured at startup — it is a write-only
    /// owner of that transform (§5). Anything else animating the same values is discarded one
    /// frame later, with no error and no clue. So PlayerWeaponSlots parents each weapon under a
    /// holder and this drives the HOLDER: sway keeps working in its own local space, composed on
    /// top of the raise, and neither knows the other exists.
    ///
    /// IT TOUCHES NO VISIBILITY AT ALL, only the holder's local pose. PlayerLoadout owns every
    /// activeSelf in the viewmodel hierarchy, and this deliberately stays out of that — the
    /// "empty hands" beat is a HOLD at the low pose rather than a hide, which also sidesteps the
    /// trap that Update does not run on an inactive GameObject.
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewmodelEquipRaise : MonoBehaviour
    {
        float delay, riseTime, elapsed;
        Vector3 fromPos;
        Quaternion fromRot;
        bool running;

        /// <summary>
        /// Start the hidden beat and the lift. Safe to call again mid-raise — a fast second
        /// pickup restarts cleanly rather than stacking, since everything is recomputed here.
        /// </summary>
        public void Begin(float hiddenDelay, float raiseTime, Vector3 lowerOffset, Vector3 lowerEuler)
        {
            delay = Mathf.Max(0f, hiddenDelay);
            riseTime = Mathf.Max(0f, raiseTime);
            fromPos = lowerOffset;
            fromRot = Quaternion.Euler(lowerEuler);
            elapsed = 0f;
            running = true;
            enabled = true;
            Apply(0f);
        }

        void Update()
        {
            if (!running) { enabled = false; return; }

            elapsed += Time.deltaTime;

            // THE DELAY IS A HOLD AT THE LOW POSE, not a deactivation — Update does not run on
            // an inactive GameObject, so hiding this object would freeze the very timer meant to
            // un-hide it. It is also why the low offset MUST sit below the frame: the weapon is
            // genuinely present during the beat, just out of view, and an offset tuned too small
            // shows it parked at the bottom of the screen instead of reading as empty hands.
            if (elapsed < delay) return;

            float k = riseTime <= 0f ? 1f : Mathf.Clamp01((elapsed - delay) / riseTime);

            // Smoothstep, and EASE-OUT is the half that matters: a weapon arriving at rest
            // abruptly reads as a snap however long the travel was, while a slow start reads as
            // deliberate. Cheap enough not to warrant a curve field until someone wants one.
            Apply(k * k * (3f - 2f * k));

            if (k >= 1f) { running = false; enabled = false; }
        }

        void Apply(float k)
        {
            transform.localPosition = Vector3.Lerp(fromPos, Vector3.zero, k);
            transform.localRotation = Quaternion.Slerp(fromRot, Quaternion.identity, k);
        }

        /// <summary>Snap to rest and stop — for a swap that must be instant.</summary>
        public void Finish()
        {
            running = false;
            enabled = false;
            Apply(1f);
        }
    }
}
