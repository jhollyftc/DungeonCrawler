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
            if (li != null) baseIntensity = li.intensity;
        }

        void Update()
        {
            if (li == null) return;
            float n = Mathf.PerlinNoise(Time.time * speed, noiseSeed * 0.7919f);
            li.intensity = baseIntensity * (1f + (n - 0.5f) * 2f * amount);
        }
    }
}
