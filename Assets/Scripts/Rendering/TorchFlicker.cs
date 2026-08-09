using UnityEngine;
using UnityEngine.VFX;

namespace DungeonGen
{
    /// <summary>
    /// Cheap Perlin-noise intensity flicker for a torch light, and — from the SAME noise
    /// sample — a matching pulse on the torch's flame VFX.
    ///
    /// ONE SAMPLE DRIVES BOTH, which is the whole point. A second noise source in the graph
    /// or in another component would run on its own clock and drift, so the fire would be
    /// brightening while the light it supposedly casts was dimming. Same reasoning as
    /// TorchPlacer resolving the palette colour once for the light and the flame together.
    ///
    /// The flame side needs NO graph changes: the exposed HDR Color the graph already
    /// multiplies its colour-over-life gradient by is simply scaled, so the flame brightens
    /// and dims — and blooms more and less, since magnitude is what bloom sees — in step with
    /// the wall it is lighting.
    /// </summary>
    public class TorchFlicker : MonoBehaviour
    {
        [Range(0f, 1f)] public float amount = 0.25f;
        public float speed = 6f;
        public int noiseSeed;

        [Tooltip("How hard the FLAME pulses, as a multiple of `amount`. 1 = exactly the light's swing. Under 1 is usually better: the light's flicker is read indirectly off walls and tolerates a lot, while the flame is looked at directly and the same swing there reads as strobing. 0 = flame stays steady while the light flickers.")]
        [Range(0f, 2f)] public float flameAmount = 0.6f;

        Light li;
        float baseIntensity;

        VisualEffect flame;
        int flameColorId;
        Color flameBaseColor;
        bool driveFlame;

        void Awake()
        {
            li = FindLight();
            // Capture the AUTHORED intensity, which is correct for a hand-placed torch.
            // A generated one is configured by TorchPlacer AFTER Instantiate returns, by
            // which point this has already run — see SetBaseIntensity.
            if (li != null) baseIntensity = li.intensity;
        }

        /// <summary>
        /// The Light this drives: this GameObject first, then children, then parents.
        ///
        /// SEARCHING BEYOND THIS OBJECT IS NOT DEFENSIVE PADDING. TorchPlacer always adds this
        /// component to the light's own GameObject, so a generated torch is fine either way —
        /// but authoring it on a torch PREFAB is the obvious thing to try, and there the
        /// component usually lands on the prefab root while the Light sits on a child. A
        /// same-object-only lookup then silently returns null and Update early-returns: the
        /// component is present, enabled, correctly configured, and does nothing at all.
        /// </summary>
        Light FindLight()
        {
            var l = GetComponent<Light>();
            if (l == null) l = GetComponentInChildren<Light>(true);
            if (l == null) l = GetComponentInParent<Light>();
            return l;
        }

        /// <summary>
        /// Set the intensity this flickers AROUND.
        ///
        /// MUST be called by anything that writes `Light.intensity` on a torch after it is
        /// instantiated, because Update() overwrites the light from `baseIntensity` every
        /// frame — so an externally-set intensity survives exactly one frame and is then
        /// discarded. That bug presented as "the room's intensityScale does nothing, but the
        /// light is briefly bright at load", with 0 and 100 looking identical, since the value
        /// was being REPLACED rather than scaled.
        ///
        /// Awake cannot be made to do this on its own: it runs DURING Instantiate, before the
        /// caller has had a chance to configure anything. The dependency is therefore explicit
        /// rather than a matter of execution order — the same discipline as PlayerRoomTracker
        /// frame-stamping instead of trusting DefaultExecutionOrder.
        /// </summary>
        public void SetBaseIntensity(float intensity)
        {
            baseIntensity = intensity;
            if (li == null) li = FindLight();
            if (li != null) li.intensity = intensity;   // no dark frame before the first Update
        }

        /// <summary>The intensity being flickered around — the value a culler should restore to.</summary>
        public float BaseIntensity => baseIntensity;

        /// <summary>
        /// Hand this the torch's flame VFX and the colour it should pulse AROUND — the same
        /// palette colour TorchPlacer just wrote to it, so the flicker scales the room's hue
        /// rather than replacing it.
        ///
        /// Explicit for the same reason SetBaseIntensity is: the flame is found and configured
        /// by the placer AFTER Instantiate, so Awake here cannot discover the base colour for
        /// itself. Checked ONCE against the graph rather than every frame — a graph without
        /// the exposed property is a valid authoring state (the flame simply keeps its own
        /// colour), not an error worth paying for 60 times a second.
        /// </summary>
        public void SetFlame(VisualEffect vfx, string colorProperty, Color baseColor)
        {
            flame = vfx;
            flameBaseColor = baseColor;
            driveFlame = false;
            if (flame == null || string.IsNullOrEmpty(colorProperty)) return;

            flameColorId = Shader.PropertyToID(colorProperty);
            driveFlame = flame.HasVector4(flameColorId);
        }

        void Update()
        {
            if (li == null && !driveFlame) return;

            // ONE sample, both consumers. n is 0..1, so this maps to a symmetric swing about
            // the base value: at amount 0.25 the light rides between 0.75x and 1.25x.
            float n = Mathf.PerlinNoise(Time.time * speed, noiseSeed * 0.7919f);
            float swing = (n - 0.5f) * 2f;

            if (li != null) li.intensity = baseIntensity * (1f + swing * amount);

            if (driveFlame)
            {
                // Scale the HDR colour. Its MAGNITUDE is what the flame's brightness and its
                // bloom both read (§7), so scaling it pulses the fire without touching its hue
                // — the room's palette survives, which a lerp toward black would not do.
                float k = Mathf.Max(0f, 1f + swing * amount * flameAmount);
                flame.SetVector4(flameColorId, flameBaseColor * k);
            }
        }
    }
}
