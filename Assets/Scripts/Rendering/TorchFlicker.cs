using UnityEngine;

namespace DungeonGen
{
    /// <summary>Cheap Perlin-noise intensity flicker for a torch light.</summary>
    public class TorchFlicker : MonoBehaviour
    {
        [Range(0f, 1f)] public float amount = 0.25f;
        public float speed = 6f;
        public int noiseSeed;

        Light li;
        float baseIntensity;

        void Awake()
        {
            li = GetComponent<Light>();
            // Capture the AUTHORED intensity, which is correct for a hand-placed torch.
            // A generated one is configured by TorchPlacer AFTER Instantiate returns, by
            // which point this has already run — see SetBaseIntensity.
            if (li != null) baseIntensity = li.intensity;
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
            if (li == null) li = GetComponent<Light>();
            if (li != null) li.intensity = intensity;   // no dark frame before the first Update
        }

        /// <summary>The intensity being flickered around — the value a culler should restore to.</summary>
        public float BaseIntensity => baseIntensity;

        void Update()
        {
            if (li == null) return;
            float n = Mathf.PerlinNoise(Time.time * speed, noiseSeed * 0.7919f);
            li.intensity = baseIntensity * (1f + (n - 0.5f) * 2f * amount);
        }
    }
}
