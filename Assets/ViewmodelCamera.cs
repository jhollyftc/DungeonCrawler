using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DungeonGen
{
    /// <summary>
    /// Draws the held weapon/shield through a separate OVERLAY camera that clears
    /// the depth buffer, so the viewmodel is rendered AFTER the world onto cleared
    /// depth and therefore CANNOT clip through geometry — at any rotation, against
    /// any geometry, with no per-weapon tuning. This is the standard FPS solution
    /// (Half-Life, CoD, et al).
    ///
    /// It retires a whole class of bug that no amount of shoulder→tip casting can
    /// close: a shield is a broad VOLUME, so a line cast is simply the wrong model
    /// for it, and retracting along the weapon's own axis can't resolve a sideways
    /// intrusion anyway. Adding more casts just builds a worse approximation of
    /// "does my weapon volume intersect the world" — which a depth clear answers
    /// perfectly, for free.
    ///
    /// ViewmodelCollision still runs, but its JOB CHANGES: it's now a FEEL mechanic
    /// (the weapon pulls back when you press into a wall — "too cramped to extend")
    /// rather than a correctness guarantee. It no longer has to be perfect, so it
    /// can be tuned purely for feel.
    ///
    /// Put this on the player's MAIN camera. Everything is wired at Awake, so the
    /// player prefab stays self-contained — no scene setup, nothing to remember.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class ViewmodelCamera : MonoBehaviour
    {
        [Header("Viewmodel")]
        [Tooltip("Layer the weapon/shield render on. You must create this layer yourself (Project Settings > Tags and Layers) — a script can't add one at runtime. The base camera stops drawing this layer; the overlay camera draws only it.")]
        public string viewmodelLayer = "Viewmodel";

        [Tooltip("Roots of the held items (hands / weapon / shield). Their ENTIRE hierarchies are moved onto the viewmodel layer at Awake, so you never have to set the layer per-mesh or remember it when you swap a weapon.")]
        public Transform[] viewmodelRoots;

        [Header("Overlay camera")]
        [Tooltip("Field of view for the weapon ONLY. Lower than the world FOV (~50-60) is the classic look: it stops the weapon warping at wide world FOVs and decouples weapon framing from the world FOV entirely — retune one without touching the other.")]
        public float viewmodelFieldOfView = 55f;
        [Tooltip("Near clip for the overlay pass. Can be tiny — nothing else is drawn in it.")]
        public float nearClip = 0.01f;
        [Tooltip("Far clip for the overlay pass. The viewmodel is inches away, so this can be small.")]
        public float farClip = 20f;

        [Tooltip("Log the wiring (layer, renderers moved, camera stack) at Awake. Turn on when the viewmodel doesn't appear.")]
        public bool debugSetup = true;

        Camera baseCamera;
        Camera overlayCamera;

        /// <summary>The overlay camera, once built. Null if the layer was missing.</summary>
        public Camera OverlayCamera => overlayCamera;

        /// <summary>
        /// Stow/draw the held items. PlayerCarry hides them while carrying a prop —
        /// hands are full, so no sword and no shield until you drop or throw.
        /// Toggling the ROOTS (not the renderers) also parks their sway and
        /// collision components, which have nothing to do while stowed.
        /// </summary>
        public void SetViewmodelVisible(bool visible)
        {
            if (viewmodelRoots == null) return;
            foreach (Transform root in viewmodelRoots)
                if (root != null) root.gameObject.SetActive(visible);
        }

        void Awake()
        {
            baseCamera = GetComponent<Camera>();

            int layer = LayerMask.NameToLayer(viewmodelLayer);
            if (layer < 0)
            {
                Debug.LogError(
                    $"[ViewmodelCamera] Layer '{viewmodelLayer}' doesn't exist. Create it in " +
                    "Project Settings > Tags and Layers, then set it on this component. " +
                    "Until then the viewmodel renders on the main camera and WILL clip through walls.");
                return; // leave the base camera untouched: weapon still visible, just clips
            }

            int moved = MoveViewmodelToLayer(layer);
            BuildOverlayCamera(layer);

            if (debugSetup) LogSetup(layer, moved);
        }

        int MoveViewmodelToLayer(int layer)
        {
            if (viewmodelRoots == null) return 0;
            int moved = 0;
            foreach (Transform root in viewmodelRoots)
            {
                if (root == null) continue;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = layer;
                    moved++;
                }
            }
            return moved;
        }

        void BuildOverlayCamera(int layer)
        {
            // 1. The world camera stops drawing the viewmodel...
            baseCamera.cullingMask &= ~(1 << layer);

            // 2. ...and a depth-clearing overlay draws ONLY it, after the world.
            //    Parented to the eye at identity, so it shares the exact pose —
            //    the weapon keeps tracking the camera (sway, bob, retraction).
            var go = new GameObject("ViewmodelCamera");
            go.transform.SetParent(transform, false);
            go.SetActive(true);

            overlayCamera = go.AddComponent<Camera>();
            overlayCamera.cullingMask = 1 << layer;
            overlayCamera.fieldOfView = viewmodelFieldOfView;
            overlayCamera.nearClipPlane = nearClip;
            overlayCamera.farClipPlane = farClip;
            overlayCamera.orthographic = false;
            overlayCamera.clearFlags = CameraClearFlags.Depth;

            // Overlay render type IS the depth clear — that's the whole trick.
            UniversalAdditionalCameraData overlayData = overlayCamera.GetUniversalAdditionalCameraData();
            overlayData.renderType = CameraRenderType.Overlay;

            UniversalAdditionalCameraData baseData = baseCamera.GetUniversalAdditionalCameraData();
            baseData.renderType = CameraRenderType.Base;
            if (!baseData.cameraStack.Contains(overlayCamera))
                baseData.cameraStack.Add(overlayCamera);
        }

        bool warnedDisabled;

        void LateUpdate()
        {
            if (overlayCamera == null) return;

            // The overlay camera renders nothing if its GameObject is inactive or
            // its Camera disabled — and it fails SILENTLY: URP still lists it in
            // the stack, so the wiring looks perfect while the viewmodel is just
            // gone. Something in the player prefab was switching it off. Re-assert
            // it rather than leaving the viewmodel invisible, and say so once.
            bool objectOff = !overlayCamera.gameObject.activeSelf;
            bool cameraOff = !overlayCamera.enabled;
            if (!objectOff && !cameraOff) return;

            if (!warnedDisabled)
            {
                warnedDisabled = true;
                Debug.LogWarning(
                    $"[ViewmodelCamera] The overlay camera was disabled by something else " +
                    $"(gameObject.activeSelf={overlayCamera.gameObject.activeSelf}, " +
                    $"camera.enabled={overlayCamera.enabled}) — the viewmodel would have gone " +
                    "invisible. Re-enabling it. Find whatever disables cameras under the player " +
                    "and exclude this one.", this);
            }

            if (objectOff) overlayCamera.gameObject.SetActive(true);
            if (cameraOff) overlayCamera.enabled = true;
        }

        void LogSetup(int layer, int moved)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ViewmodelCamera] layer '{viewmodelLayer}' = {layer} (mask {1 << layer})");
            sb.AppendLine($"  moved {moved} transform(s) onto the viewmodel layer");

            int rendererCount = 0, onLayer = 0;
            if (viewmodelRoots != null)
            {
                foreach (Transform root in viewmodelRoots)
                {
                    if (root == null) continue;
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        rendererCount++;
                        if (r.gameObject.layer == layer) onLayer++;
                        sb.AppendLine($"  renderer '{r.name}' layer={r.gameObject.layer} enabled={r.enabled} activeInHierarchy={r.gameObject.activeInHierarchy}");
                    }
                }
            }
            sb.AppendLine($"  renderers found: {rendererCount} ({onLayer} on the viewmodel layer)");
            if (rendererCount == 0)
                sb.AppendLine("  !! NO RENDERERS under viewmodelRoots — you dragged transforms that contain no meshes.");

            UniversalAdditionalCameraData baseData = baseCamera.GetUniversalAdditionalCameraData();
            sb.AppendLine($"  base camera '{baseCamera.name}': renderType={baseData.renderType} enabled={baseCamera.enabled} " +
                          $"cullingMask={baseCamera.cullingMask} (viewmodel bit set: {(baseCamera.cullingMask & (1 << layer)) != 0}) " +
                          $"stackCount={baseData.cameraStack.Count}");

            if (overlayCamera == null)
            {
                sb.AppendLine("  !! overlay camera was NOT created.");
            }
            else
            {
                UniversalAdditionalCameraData overlayData = overlayCamera.GetUniversalAdditionalCameraData();
                sb.AppendLine($"  overlay camera: renderType={overlayData.renderType} enabled={overlayCamera.enabled} " +
                              $"cullingMask={overlayCamera.cullingMask} fov={overlayCamera.fieldOfView} " +
                              $"clip=[{overlayCamera.nearClipPlane}..{overlayCamera.farClipPlane}] " +
                              $"inStack={baseData.cameraStack.Contains(overlayCamera)}");
            }

            Debug.Log(sb.ToString(), this);
        }

        void OnDestroy()
        {
            // Don't leave a dangling entry in the stack (domain reloads, respawns).
            if (baseCamera == null || overlayCamera == null) return;
            UniversalAdditionalCameraData baseData = baseCamera.GetUniversalAdditionalCameraData();
            if (baseData != null) baseData.cameraStack.Remove(overlayCamera);
        }
    }
}
