using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A barred gate standing across a corridor, raised and lowered by levers on either side.
    ///
    /// KINEMATIC AND SCRIPT-ANIMATED, NOT PHYSICS. It travels up into the solid rock above the
    /// corridor — guaranteed to be there, since `HallwayPathfinder.SurroundingsOk` demands solid
    /// rock above and below every corridor cell — and a dynamic body would spend its life
    /// fighting that geometry.
    ///
    /// MUST BE `PropTier.FullGameObject`. A moving part cannot be instanced: the instanced path
    /// bakes the mesh into a static matrix with no removal path, so the collider would rise while
    /// the bars stayed welded across the corridor. Four features have hit this now, and the
    /// symptom is always "the collider gizmo moves but the rendered mesh doesn't".
    ///
    /// AND ITS ROOT MUST BE IN `DungeonNavBaker.excludeRoots`. Baked into the navmesh while
    /// closed, it walls that corridor off PERMANENTLY — agents never path it again even once the
    /// gate is up, and it presents as an AI bug rather than a bake problem. Doors are excluded
    /// for exactly this reason.
    /// </summary>
    [DisallowMultipleComponent]
    public class Portcullis : MonoBehaviour, IGateLock
    {
        public enum TravelSpace
        {
            /// <summary>Direction is in WORLD space — "up" means up whatever the prefab's own
            /// rotation happens to be. Almost always what you want.</summary>
            World,
            /// <summary>Direction is in the moving part's PARENT space. Use when the gate should
            /// travel along its own authored axis (a slanted shaft, a gate on a sloped wall).</summary>
            Local,
        }

        [Header("Travel")]
        [Tooltip("The part that moves. Leave empty to move this transform.")]
        public Transform gate;
        [Tooltip("Which space Travel Direction is measured in.\n\nWORLD is the default and the fix for the commonest problem: a gate that slides SIDEWAYS instead of rising. The mover is usually a child of a root carrying the FBX's import rotation, so its LOCAL +Y is not world up — and since the travel is applied to localPosition, a naive 'up' moves it along whatever axis that rotation left pointing upward in the prefab. In World mode the direction is converted into the parent's space every frame, so the gate rises however the prefab is rotated or placed.")]
        public TravelSpace travelSpace = TravelSpace.World;
        [Tooltip("The direction the gate travels when opening. Normalised, so only the direction matters — distance comes from Raise Height.\n\n(0,1,0) with World space is a portcullis rising into the rock above. Flip to (0,-1,0) for a gate that drops into a slot in the floor; use a horizontal vector for one that slides aside.")]
        public Vector3 travelDirection = Vector3.up;
        [Tooltip("How far the gate travels, in metres. Should clear the corridor opening — a corridor cell is 3m and the rock above it is guaranteed solid, so there is room for a full-height lift.")]
        public float raiseHeight = 2.9f;
        [Tooltip("Seconds for a full open or close. Slow reads as heavy, which is most of what sells a portcullis.")]
        public float travelTime = 2.2f;
        [Tooltip("How hard the gate decelerates into its stop. 0 = constant speed, 1 = a long settle.\n\nTHERE IS DELIBERATELY NO EASE-IN. An earlier version eased BOTH ends (a smoothstep on position) and the close appeared to lag badly: the slope is zero at both ends, so the first fraction of travel moves the gate only a few centimetres. Opening, that is a gap appearing at the FLOOR and you see it instantly. Closing, it is the top edge creeping down inside the dark rock overhead where nothing is visible — same motion, and only one of them reads as movement.\n\nA gate responds to a lever the player just pulled, so it must start AT ONCE. Easing belongs at the end, where a heavy thing settling reads as weight.")]
        [Range(0f, 1f)] public float stopEase = 0.7f;

        [Header("Authoring")]
        [Tooltip("Draws the travel path in the scene view when this object is selected — a line from the closed pose to the open one, with the open pose outlined. The fastest way to confirm the gate goes UP and far enough before ever entering play mode.")]
        public bool drawTravelGizmo = true;

        [Header("State")]
        [Tooltip("Starts raised. Generation leaves this OFF — a gate that begins open gates nothing.")]
        public bool startOpen = false;

        [Tooltip("Log toggles.")]
        public bool debugGate = false;

        /// <summary>Fired when a toggle STARTS, carrying the direction. The cue the player hears.</summary>
        public event System.Action<bool> OnToggled;

        public bool IsOpen => target > 0.5f;
        public Transform SoundOrigin => gate != null ? gate : transform;

        Vector3 closedPos;
        float t01;           // 0 = closed, 1 = raised — where the gate IS
        float target;        // where it is heading
        float moveFrom;      // where the CURRENT move began, so the ease measures this move
        float moveProgress;  // 0..1 through the current move, before easing

        void Awake()
        {
            if (gate == null) gate = transform;
            closedPos = gate.localPosition;
            t01 = target = moveFrom = startOpen ? 1f : 0f;
            moveProgress = 1f;   // no move in flight at spawn
            Apply(t01);
        }

        void Update()
        {
            if (Mathf.Approximately(t01, target)) return;

            // EASE THE PROGRESS OF THE MOVE, NOT ABSOLUTE POSITION. Easing position means the
            // curve is flat wherever the gate happens to BE — including where it starts — so a
            // move that begins at the far end crawls out of the gate. Tracking the move's own
            // start lets it leave immediately and settle at the end, whichever way it is going.
            //
            // Scaled by the distance actually being covered, so a reversal mid-travel takes
            // proportionally less time rather than a full travelTime for a short hop.
            float span = Mathf.Abs(target - moveFrom);
            if (travelTime <= 0f || span <= 0.0001f)
            {
                t01 = target;
            }
            else
            {
                moveProgress = Mathf.Clamp01(moveProgress + Time.deltaTime / (travelTime * span));
                // Ease OUT only: exponent 1 is constant speed, 3 a long settle.
                float e = 1f - Mathf.Pow(1f - moveProgress, Mathf.Lerp(1f, 3f, stopEase));
                t01 = Mathf.Lerp(moveFrom, target, e);
            }
            Apply(t01);
        }

        public void Toggle()
        {
            target = target > 0.5f ? 0f : 1f;
            // Captured so the ease measures THIS move. A reversal part-way through starts from
            // wherever the gate actually is rather than snapping or restarting from the end.
            moveFrom = t01;
            moveProgress = 0f;
            OnToggled?.Invoke(target > 0.5f);
            if (debugGate) Debug.Log($"[Portcullis] {(target > 0.5f ? "raising" : "lowering")} from {t01:0.00}.", this);
        }

        /// <summary>
        /// The travel direction expressed in the MOVER'S PARENT space, which is the space
        /// `localPosition` is written in.
        ///
        /// THE CONVERSION IS THE WHOLE FIX. Writing a world direction straight into
        /// localPosition moves the gate along the parent's axes instead — so a gate under a root
        /// carrying an FBX import rotation slides sideways while the inspector says "up".
        /// </summary>
        Vector3 LocalTravel()
        {
            Vector3 dir = travelDirection.sqrMagnitude > 1e-6f ? travelDirection.normalized : Vector3.up;
            if (travelSpace == TravelSpace.Local) return dir;
            Transform parent = gate != null ? gate.parent : null;
            return parent != null ? parent.InverseTransformDirection(dir).normalized : dir;
        }

        /// <summary>Place the gate at an openness of 0 (closed) to 1 (fully travelled). The
        /// easing lives in Update, so this is a straight mapping — which is also what lets the
        /// gizmo and the editor preview reuse it honestly.</summary>
        void Apply(float openness)
        {
            gate.localPosition = closedPos + LocalTravel() * (raiseHeight * openness);
        }

        /// <summary>
        /// Show the travel in the scene view: closed pose, the path, and the open pose.
        ///
        /// Worth the dozen lines because the failure this exists to catch — a gate travelling the
        /// wrong way, or not far enough to clear the opening — is invisible until play mode, and
        /// in play mode it is buried in a corridor somewhere in a generated dungeon.
        /// </summary>
        void OnDrawGizmosSelected()
        {
            if (!drawTravelGizmo) return;
            Transform t = gate != null ? gate : transform;
            Transform parent = t.parent;

            // In edit mode the gate sits at its closed pose, so use it directly; in play mode
            // use the captured one, since the gate may currently be raised.
            Vector3 closedLocal = Application.isPlaying ? closedPos : t.localPosition;
            Vector3 openLocal = closedLocal + LocalTravel() * raiseHeight;

            Vector3 a = parent != null ? parent.TransformPoint(closedLocal) : closedLocal;
            Vector3 b = parent != null ? parent.TransformPoint(openLocal) : openLocal;

            Gizmos.color = new Color(0.95f, 0.75f, 0.2f);
            Gizmos.DrawLine(a, b);
            Gizmos.DrawSphere(a, 0.08f);

            // The open pose outlined at the mover's own bounds, so "does it clear the opening"
            // is answerable by eye rather than by arithmetic.
            var rend = t.GetComponentInChildren<Renderer>();
            Gizmos.color = new Color(0.95f, 0.75f, 0.2f, 0.5f);
            if (rend != null) Gizmos.DrawWireCube(b + (rend.bounds.center - t.position), rend.bounds.size);
            else Gizmos.DrawWireSphere(b, 0.25f);
        }

        [ContextMenu("Preview: Raised")]
        void PreviewRaised()
        {
            Transform t = gate != null ? gate : transform;
            if (!Application.isPlaying && !previewing) { previewClosed = t.localPosition; previewing = true; }
            t.localPosition = (previewing ? previewClosed : closedPos) + LocalTravel() * raiseHeight;
        }

        [ContextMenu("Preview: Closed")]
        void PreviewClosed()
        {
            Transform t = gate != null ? gate : transform;
            t.localPosition = previewing ? previewClosed : closedPos;
            previewing = false;
        }

        // Serialized so the closed pose survives the domain reload between pressing the two
        // context-menu items — otherwise "Preview: Closed" would restore to wherever the last
        // preview left it, and repeated previews would walk the gate up the corridor.
        [SerializeField, HideInInspector] Vector3 previewClosed;
        [SerializeField, HideInInspector] bool previewing;
    }
}
