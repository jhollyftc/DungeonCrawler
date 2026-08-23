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

        [Tooltip("Index of the URP Renderer the overlay camera should use — the position in the Renderer List on your Universal Render Pipeline Asset (PC_RPAsset). -1 leaves it on the default, which is the old behaviour.\n\nWHY THIS IS WORTH SETTING: the overlay inherits the world's renderer by default, so a camera drawing two objects onto CLEARED DEPTH runs the whole pipeline anyway — measured in the Frame Debugger as its own additional-lights shadowmap pass, a depth-normals prepass and FOUR SSAO passes, for a sword and a shield. Point it at a stripped renderer (duplicate the main one, delete the SSAO feature) and all of that goes away with no change to the world.\n\nAN INDEX, NOT A REFERENCE, because that is all UniversalAdditionalCameraData.SetRenderer takes — so REORDERING THE RENDERER LIST SILENTLY REPOINTS THIS. Unity falls back to the default renderer on an out-of-range index with only a console warning, and the symptom is the overhead quietly returning. The setup log below prints the index that was applied.")]
        public int overlayRendererIndex = -1;

        [Tooltip("Include the viewmodel in the scene's post-processing — bloom, tonemapping, colour grading, vignette. OFF means the weapon is drawn AFTER the post-processed world and keeps raw, ungraded colours, which reads as a sticker pasted on the screen: it doesn't dim in a dark corridor and its emissives never bloom.\n\nURP applies a stack's post-processing ONCE, at the end. The overlay camera is the last camera in this stack, so enabling it here is what pulls the whole composited image — world plus weapon — through that single pass.\n\nTURN THIS OFF IF YOU USE DEPTH OF FIELD. The overlay CLEARS DEPTH and then writes the weapon's own depth at ~0.5m, so a DoF effect focused across the room will blur the weapon heavily. That is a real conflict with the depth-clear trick, not a bug to fix.\n\nMEASURED COST, AND IT WAS ACCEPTED DELIBERATELY — do not 'optimise' it away without re-reading this. URP runs the post stack once per camera that has post-processing enabled, so having it on BOTH the base and this overlay runs the whole chain twice (16 bloom mipmap blits and a colour LUT each, in the frame debugger). The single-stack alternatives both cost something real:\n  base OFF / overlay ON  = one stack, but post then evaluates from a DEPTH-CLEARED camera, so every depth-reading effect sees the weapon instead of the world.\n  base ON / overlay OFF  = one stack and a correct world, but the weapon is ungraded again.\nTwo stacks is the only configuration that gets a correct world AND a graded weapon, and at this resolution the duplicate blits measured as a fraction of a millisecond — far cheaper than the 72-draw depth-normals prepass that the SSAO 'After Opaque' setting removed in the same investigation.")]
        public bool postProcessViewmodel = true;

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

        [Header("Stow feel")]
        [Tooltip("Seconds to lower the hands out of frame when they are stowed — picking something up, gripping a grate. The roots are only HIDDEN once they are out of view, so the stow reads as putting your weapons down rather than them vanishing.\n\n0 restores the old instant hide.")]
        public float stowLowerTime = 0.15f;
        [Tooltip("Seconds for the hands to come back up after a drop or a throw.")]
        public float stowRaiseTime = 0.2f;
        [Tooltip("How far stowed hands drop, in CAMERA space — straight down the screen by default. Not local to each viewmodel (their authored orientations disagree, so one offset sends them three different ways) and not world (world down ignores pitch, so looking up brings the weapon into your face).")]
        public Vector3 stowLowerOffset = new Vector3(0f, -0.6f, 0f);

        int stowGeneration;

        /// <summary>
        /// Stow or restore the hands with the same lower/raise motion a weapon swap uses.
        ///
        /// SEPARATE FROM `SetViewmodelVisible`, WHICH STAYS INSTANT. Teardown paths — a disabled
        /// component, a destroyed rig — must restore immediately and have no frames left to
        /// animate in; giving them a duration would leave the hands stowed forever if the object
        /// went away mid-move. Gameplay stows call this, cleanup calls the other.
        ///
        /// STILL HIDES, rather than relying on the low pose alone. A carried barrel is held right
        /// where a lowered sword swings, so out-of-frame is not far enough — but the hide now
        /// happens at the BOTTOM of the motion, which is the only part that was wrong before.
        ///
        /// A GENERATION COUNTER GUARDS THE CALLBACK. Grabbing something else during the stow, or
        /// dropping before it finishes, must not have a stale completion hide hands that a newer
        /// call just raised — the same stale-timer problem `RestoreViewmodelAfterThrow` already
        /// solves by re-checking `IsCarrying`, generalised so every path is covered.
        /// </summary>
        public void SetViewmodelStowed(bool stowed)
        {
            if (viewmodelRoots == null) return;
            if (stowLowerTime <= 0f && stowRaiseTime <= 0f) { SetViewmodelVisible(!stowed); return; }

            int generation = ++stowGeneration;

            foreach (Transform root in viewmodelRoots)
            {
                if (root == null) continue;
                var holster = ViewmodelHolster.EnsureOn(root.gameObject);
                if (holster == null) continue;

                if (stowed)
                {
                    if (!root.gameObject.activeSelf) continue;   // already away
                    holster.Lower(stowLowerTime, stowLowerOffset, () =>
                    {
                        if (generation != stowGeneration) return;   // superseded mid-move
                        if (root != null) root.gameObject.SetActive(false);
                    });
                }
                else
                {
                    root.gameObject.SetActive(true);
                    holster.Raise(0f, stowRaiseTime, stowLowerOffset);
                }
            }
        }

        /// <summary>
        /// Put a hierarchy spawned AFTER Awake onto the viewmodel layer.
        ///
        /// The Awake sweep covers the authored roots once and can never see anything created
        /// later — a picked-up weapon's viewmodel, an enchant VFX added to a hand. Missing the
        /// layer does not throw or warn: the object renders through the BASE camera instead,
        /// so it clips through walls and is graded by world post-processing while looking
        /// otherwise correct. Any runtime viewmodel spawn must call this.
        /// </summary>
        public void AdoptViewmodel(Transform root)
        {
            if (root == null) return;
            int layer = LayerMask.NameToLayer(viewmodelLayer);
            if (layer < 0) return;   // already errored loudly in Awake
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
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

            // POST-PROCESSING. `renderPostProcessing` defaults to FALSE on a camera built in
            // code, which is why the global Volume graded the whole world and stopped dead at
            // the weapon — the viewmodel was drawn after the post-processed image and kept its
            // raw colours. URP runs a stack's post-processing ONCE at the end, and this overlay
            // is the last camera in the stack, so the flag here is what brings the composited
            // result through that pass.
            overlayData.renderPostProcessing = postProcessViewmodel;

            // COPIED FROM THE BASE CAMERA, NOT DEFAULTED. A Volume only affects a camera whose
            // volumeLayerMask includes the Volume's layer, and a code-built camera gets the
            // bare default — so a project whose global Volume sits on anything but Default
            // would have the overlay silently ignore it while the base camera obeyed it, with
            // both cameras looking correctly configured. Deriving from the base is what makes
            // "the weapon is graded like the world" true by construction rather than by two
            // settings happening to match.
            // A STRIPPED RENDERER FOR THE OVERLAY. Set before anything else reads the camera, so
            // the pipeline it runs is decided once at construction rather than on the first
            // frame. Left alone at -1, which keeps the pre-existing behaviour for any rig that
            // has not been given one.
            if (overlayRendererIndex >= 0) overlayData.SetRenderer(overlayRendererIndex);

            overlayData.volumeLayerMask = baseData.volumeLayerMask;
            overlayData.volumeTrigger = baseData.volumeTrigger;
            overlayData.antialiasing = baseData.antialiasing;
            overlayData.antialiasingQuality = baseData.antialiasingQuality;

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
                sb.AppendLine($"  post-processing: overlay={overlayData.renderPostProcessing} base={baseData.renderPostProcessing} " +
                              $"volumeMask=0x{overlayData.volumeLayerMask.value:X} (base 0x{baseData.volumeLayerMask.value:X})");
                sb.AppendLine($"  overlay renderer index: {(overlayRendererIndex >= 0 ? overlayRendererIndex.ToString() : "default (unset)")}" +
                              " — reordering the Renderer List on the URP asset silently repoints this, " +
                              "and an out-of-range index falls back to the default with only a console warning.");

                // Both halves of "why is the weapon ungraded?" stated outright, because neither
                // is visible from the inspector: a code-built camera has no serialized row to
                // look at, and a Volume on a layer outside the mask fails identically to no
                // Volume at all.
                if (!overlayData.renderPostProcessing && postProcessViewmodel)
                    sb.AppendLine("  !! postProcessViewmodel is ON but the overlay has post-processing OFF.");
                if (postProcessViewmodel && !baseData.renderPostProcessing)
                    sb.AppendLine("  NB the BASE camera has post-processing off — the world itself is ungraded, " +
                                  "so grading the weapon alone will make it stand out MORE, not less.");
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
