using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Lowers a viewmodel out of frame and raises it back — the motion under every weapon
    /// transition: equipping a pickup, swapping slots, and stowing to carry something.
    ///
    /// IT ANIMATES A WRAPPER, NEVER THE VIEWMODEL ITSELF, and that is forced rather than chosen.
    /// <see cref="ViewmodelSway"/> writes `transform.localPosition` and `localRotation` on its own
    /// transform EVERY LateUpdate from a rest pose captured at startup — it is a write-only owner
    /// of that transform (§5), so anything else animating those values is discarded one frame
    /// later, with no error and no clue. Driving a PARENT sidesteps it completely: the two are
    /// different transforms, sway keeps working in its own local space, and neither knows the
    /// other exists. `EnsureOn` is what guarantees the wrapper exists.
    ///
    /// IT TOUCHES NO VISIBILITY. `PlayerLoadout` owns every `activeSelf` in the weapon hierarchy
    /// and `ViewmodelCamera` owns the top-level roots; this stays out of both. "Empty hands" is a
    /// HOLD at the low pose, not a hide — which also sidesteps the trap that `Update` does not run
    /// on an inactive GameObject, so hiding the thing would freeze the timer meant to un-hide it.
    /// The corollary is that the low offset MUST sit below the frame: the weapon is genuinely
    /// present throughout, just out of view, and an offset tuned too small parks it visibly at the
    /// bottom of the screen instead of reading as empty hands.
    ///
    /// WAS `ViewmodelEquipRaise`, which only ever went one way. Renamed with its file so the
    /// GUID travels (rule 3) — it is only ever added at runtime, so no prefab referenced it.
    /// </summary>
    [DisallowMultipleComponent]
    public class ViewmodelHolster : MonoBehaviour
    {
        float delay, duration, elapsed;
        float fromT, toT, t;
        Vector3 camOffset;   // camera-space, see Apply
        bool running;
        Action onDone;

        /// <summary>Is it currently at or heading toward the lowered pose?</summary>
        public bool Lowered { get; private set; }

        /// <summary>
        /// Ensure a holster wrapper sits directly above <paramref name="viewmodel"/>, inserting one
        /// if needed, and return it.
        ///
        /// INSERTING A PARENT IS SAFE EVEN WHEN `ViewmodelSway` IS ON THE ROOT ITSELF, which is why
        /// this can be applied to authored hierarchies without knowing how they are built: sway
        /// writes its OWN transform, the wrapper is a different one above it, and they compose.
        ///
        /// The wrapper is created at IDENTITY local TRS under the original parent and the viewmodel
        /// keeps its own local values (`SetParent(..., false)` preserves them), so the net
        /// transform chain is unchanged and nothing authored moves. Getting that backwards is the
        /// classic half-cell offset — and worse here, where the rig is centimetres from the camera.
        /// </summary>
        public static ViewmodelHolster EnsureOn(GameObject viewmodel)
        {
            if (viewmodel == null) return null;

            var existing = viewmodel.transform.parent != null
                ? viewmodel.transform.parent.GetComponent<ViewmodelHolster>() : null;
            if (existing != null) return existing;

            Transform original = viewmodel.transform.parent;
            var holder = new GameObject(viewmodel.name + " (holster)");
            holder.layer = viewmodel.layer;
            holder.transform.SetParent(original, false);
            viewmodel.transform.SetParent(holder.transform, false);

            var h = holder.AddComponent<ViewmodelHolster>();
            h.enabled = false;
            return h;
        }

        /// <summary>
        /// Raise into view: hold at the low pose for <paramref name="hiddenDelay"/>, then rise.
        /// Safe to call mid-move — everything is recomputed, so a fast second swap restarts
        /// cleanly rather than stacking, and reversing direction picks up from wherever it is.
        /// </summary>
        public void Raise(float hiddenDelay, float raiseTime, Vector3 lowerCameraOffset,
                          Action done = null)
            => Begin(hiddenDelay, raiseTime, Lowered || running ? t : 1f, 0f, lowerCameraOffset, done);

        /// <summary>
        /// Lower out of frame, calling <paramref name="done"/> when it arrives.
        ///
        /// THE CALLBACK IS THE POINT: a slot swap has to exchange weapons at the BOTTOM of the
        /// motion, where nothing is on screen, or the change is visible and the whole effect is
        /// pointless. Fires even when duration is 0, so an instant swap still sequences correctly.
        /// </summary>
        public void Lower(float lowerTime, Vector3 lowerCameraOffset, Action done = null)
            => Begin(0f, lowerTime, t, 1f, lowerCameraOffset, done);

        void Begin(float hiddenDelay, float time, float from, float to, Vector3 offset, Action done)
        {
            delay = Mathf.Max(0f, hiddenDelay);
            duration = Mathf.Max(0f, time);
            fromT = from; toT = to;
            camOffset = offset;
            elapsed = 0f;
            Lowered = to > 0.5f;
            onDone = done;
            running = true;
            enabled = true;

            Apply(0f);
            // Zero-length moves must still complete THIS call rather than next frame: callers
            // sequence off the callback, and a swap that waits a frame for a 0s lower would show
            // the exchange.
            if (duration <= 0f && delay <= 0f) Complete();
        }

        void Update()
        {
            if (!running) { enabled = false; return; }

            elapsed += Time.deltaTime;
            // Re-applied during the hold, not merely at its start: the offset is CAMERA-relative,
            // so looking around during the empty-hands beat would otherwise leave the weapon
            // parked along a direction that pointed off-screen when the beat began.
            if (elapsed < delay) { Apply(0f); return; }

            float k = duration <= 0f ? 1f : Mathf.Clamp01((elapsed - delay) / duration);

            // Smoothstep, and EASE-OUT is the half that matters: a weapon arriving at rest
            // abruptly reads as a snap however long the travel was, while a slow start reads as
            // deliberate. Cheap enough not to warrant a curve field until someone wants one.
            Apply(k * k * (3f - 2f * k));

            if (k >= 1f) Complete();
        }

        void Complete()
        {
            running = false;
            enabled = false;
            Apply(1f);

            // CLEARED BEFORE INVOKING. The callback commonly starts the NEXT move on this same
            // holster (lower, then raise), which assigns a new callback — clearing afterwards
            // would wipe the one just set and silently break the second half of every swap.
            Action d = onDone;
            onDone = null;
            d?.Invoke();
        }

        /// <summary>
        /// THE OFFSET IS IN CAMERA SPACE, RESOLVED INTO LOCAL SPACE EVERY FRAME. Both of the
        /// obvious alternatives were tried and both are wrong, which is worth recording so
        /// neither gets reintroduced as a simplification:
        ///
        /// **LOCAL to each viewmodel** — wrong because every viewmodel carries its OWN authored
        /// orientation. The sword's local down, the shield's and the bow's do not agree, so one
        /// offset sent each of them a different way and a swap lowering sword and shield together
        /// had them visibly diverge.
        ///
        /// **WORLD down** — fixes that, and breaks on PITCH. Looking up brings the weapon into
        /// your face and looking down buries it in the floor, because "down" stops having
        /// anything to do with the bottom of the screen the moment the camera tilts.
        ///
        /// **CAMERA down** is the one that holds. It is the same direction for every viewmodel,
        /// so they lower together, and it is always off the bottom of the frame whatever the
        /// player is looking at — which is what "stowed" actually means for a viewmodel.
        ///
        /// Re-resolved per frame rather than converted once at Begin, or turning mid-swap would
        /// leave the weapon travelling along a direction that was correct when the move started
        /// and is not any more.
        ///
        /// NO ROTATION. An earlier version tilted the weapon nose-down as it dropped, and the note
        /// here claimed that tilt was most of what made it read as lifting rather than sliding.
        /// With misaligned local axes it was as inconsistent as the offset, and a straight drop
        /// preserving the authored pose reads better — so it is gone rather than corrected.
        ///
        /// `InverseTransformVector`, not `InverseTransformDirection`: the former accounts for
        /// scale, so 0.6m is 0.6m of travel whatever scale the rig carries — and these viewmodel
        /// rigs do carry non-unit scale.
        /// </summary>
        void Apply(float k)
        {
            t = Mathf.Lerp(fromT, toT, k);

            Transform reference = Reference();
            Vector3 world = reference != null ? reference.TransformVector(camOffset) : camOffset;
            Vector3 local = transform.parent != null
                ? transform.parent.InverseTransformVector(world)
                : world;
            transform.localPosition = local * t;
        }

        Transform reference;

        /// <summary>
        /// The camera the offset is measured against — the one this viewmodel hangs beneath,
        /// found by walking up, so a rig with more than one camera cannot pick the wrong one.
        /// `Camera.main` is only the fallback.
        /// </summary>
        Transform Reference()
        {
            if (reference != null) return reference;
            var cam = GetComponentInParent<Camera>();
            reference = cam != null ? cam.transform
                      : (Camera.main != null ? Camera.main.transform : null);
            return reference;
        }

        /// <summary>Snap to rest and stop — for a swap that must be instant.</summary>
        public void Finish()
        {
            running = false;
            enabled = false;
            onDone = null;
            Lowered = false;
            t = 0f;
            transform.localPosition = Vector3.zero;
        }
    }
}
