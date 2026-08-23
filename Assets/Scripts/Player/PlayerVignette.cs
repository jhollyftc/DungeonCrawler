using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DungeonGen
{
    /// <summary>
    /// The single owner of the player's screen vignette, on the same contract as
    /// <see cref="PlayerFov"/>: FRAME-STAMPED ADDITIVE REQUESTS that ease home on their own when
    /// nothing asks.
    ///
    /// AN OWNER RATHER THAN A FIELD ON PlayerBow, because §10 already recorded what happens the
    /// second time something wants the same property. FOV was written twice with a lazily-captured
    /// base — safe only while `PlayerLoadout` guaranteed one consumer at a time — and the THIRD
    /// consumer (the throw heave, which is active while melee is also enabled) broke the
    /// convention rather than stretching it. A vignette has obvious future askers: low health,
    /// drowning, a heavy-carry strain. Written as an owner now, they compose; written as a field
    /// on the bow, the second one silently fights the first.
    ///
    /// IT DRIVES A RUNTIME VOLUME'S **WEIGHT**, NOT THE VIGNETTE'S INTENSITY, and both halves of
    /// that matter:
    /// - A runtime profile, never the project's shared one. `VolumeProfile` is an ASSET, so
    ///   writing to the global profile's Vignette at runtime EDITS THE ASSET ON DISK — the change
    ///   survives play mode and applies to everything. The same trap `EmissiveMaterialVariants`
    ///   carries for materials, with worse consequences because it is version-controlled.
    /// - Weight, because a volume at weight 0 contributes NOTHING and at 1 contributes fully.
    ///   That IS the fade, for free, and it blends with whatever the global profile already says
    ///   rather than overwriting it.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerVignette : MonoBehaviour
    {
        [Tooltip("Vignette colour at full weight.")]
        public Color color = Color.black;
        [Tooltip("Vignette intensity at full weight. The REQUEST scales toward this, so a request of 0.5 lands at half of it.")]
        [Range(0f, 1f)] public float intensity = 0.6f;
        [Tooltip("Edge softness. Low values give a hard-edged tunnel; high values a gentle darkening.")]
        [Range(0.01f, 1f)] public float smoothness = 0.16f;

        [Tooltip("How fast the vignette eases toward whatever was requested, per second. Used when a caller does not specify its own speed.")]
        public float defaultSpeed = 8f;

        [Tooltip("Volume priority. Above the project's global profile so this wins, but low enough that a deliberate cutscene volume could still sit over it.")]
        public float volumePriority = 100f;

        Volume volume;
        VolumeProfile profile;
        Vignette vignette;

        float current;
        float pendingWeight;
        float pendingSpeed;
        int pendingFrame = -1;

        /// <summary>
        /// Find-or-create, on the FirstPersonController's GameObject where possible.
        ///
        /// SAME REASON `PlayerFov.Ensure` EXISTS: Awake order between sibling components is
        /// undefined, so whichever consumer ran first would cache a null — and a missing owner
        /// does not throw, the effect simply never happens. That was found by raising a throw's
        /// FOV from 6 to 100 and seeing nothing at all. Creating it on demand removes the prefab
        /// step there is no way to be reminded of.
        /// </summary>
        public static PlayerVignette Ensure(Component owner)
        {
            if (owner == null) return null;
            var found = owner.GetComponentInParent<PlayerVignette>();
            if (found != null) return found;

            var controller = owner.GetComponentInParent<FirstPersonController>();
            GameObject host = controller != null ? controller.gameObject : owner.gameObject;
            return host.AddComponent<PlayerVignette>();
        }

        /// <summary>
        /// Ask for the vignette this frame, 0..1 of the authored intensity.
        ///
        /// FRAME-STAMPED, NOT LATCHED — stop calling and it eases away. Same contract as
        /// `PlayerFov.AddOffset`, `CameraKick.SetSustained` and `SetSustainedVelocity`, and for
        /// the same reason: a caller that dies mid-effect (weapon swapped, player killed, dungeon
        /// regenerated) must not be able to leave the screen permanently darkened.
        /// Requests SUM, so no caller can silently stomp another's.
        /// </summary>
        public void Request(float weight01, float speed = 0f)
        {
            if (pendingFrame != Time.frameCount)
            {
                pendingFrame = Time.frameCount;
                pendingWeight = 0f;
                pendingSpeed = 0f;
            }
            pendingWeight += Mathf.Max(0f, weight01);
            pendingSpeed = Mathf.Max(pendingSpeed, speed);
        }

        void LateUpdate()
        {
            float want = pendingFrame == Time.frameCount ? Mathf.Clamp01(pendingWeight) : 0f;
            float speed = pendingSpeed > 0f ? pendingSpeed : defaultSpeed;

            current = Mathf.MoveTowards(current, want, speed * Time.deltaTime);

            // Nothing to show and nothing built yet: do not create the volume until something
            // actually asks, so a player rig that never uses a vignette pays nothing for it.
            if (current <= 0.0001f && volume == null) return;
            EnsureVolume();
            if (volume == null) return;

            volume.weight = current;
            // Re-pushed each frame so the inspector values are live while tuning — they are a
            // look decision and get found by dragging sliders, not by reasoning.
            if (vignette != null)
            {
                vignette.color.value = color;
                vignette.intensity.value = intensity;
                vignette.smoothness.value = smoothness;
            }
        }

        void EnsureVolume()
        {
            if (volume != null) return;

            var go = new GameObject("PlayerVignette");
            go.transform.SetParent(transform, false);

            // THE VOLUME'S LAYER MUST BE IN THE CAMERA'S volumeLayerMask OR IT IS SIMPLY IGNORED —
            // silently, with the volume looking perfectly configured. §10 records the same trap on
            // the viewmodel overlay, which copies its mask from the base camera precisely so the
            // two cannot disagree. Rather than hardcode Default and hope it is included, the layer
            // is DERIVED from the mask the camera actually uses.
            go.layer = LayerInMask();

            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = volumePriority;
            volume.weight = current;

            // CreateInstance, not the project's profile. See the class summary — writing to a
            // shared VolumeProfile at runtime edits the ASSET.
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "PlayerVignette (runtime)";
            volume.profile = profile;

            vignette = profile.Add<Vignette>(true);
            // Every override starts DISABLED, so without these the values are authored and inert
            // — the volume blends nothing and reads as the effect not working.
            vignette.color.overrideState = true;
            vignette.intensity.overrideState = true;
            vignette.smoothness.overrideState = true;
        }

        int LayerInMask()
        {
            Camera cam = Camera.main;
            var data = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
            int mask = data != null ? data.volumeLayerMask.value : ~0;
            if (mask == 0) return 0;
            for (int i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0) return i;
            return 0;
        }

        void OnDestroy()
        {
            // The profile is a runtime ScriptableObject and is not owned by anything else, so it
            // leaks per player rig without this — the same rule EmissiveMaterialVariants follows
            // for its cached material copies.
            if (profile != null) Destroy(profile);
        }
    }
}
