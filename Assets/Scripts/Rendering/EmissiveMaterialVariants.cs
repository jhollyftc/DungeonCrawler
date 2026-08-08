using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Hands out one material per distinct emissive COLOUR, cached, so instanced kit
    /// pieces can glow different colours without leaving the instanced path.
    ///
    /// WHY A MATERIAL AND NOT A PROPERTY BLOCK: on the instanced path nothing renders
    /// through the prefab's MeshRenderer — InstancedDungeonRenderer harvests it once into
    /// a Proto and draws with Graphics.RenderMeshInstanced — so a MaterialPropertyBlock
    /// has no renderer to attach to (this is why an EmissionController on a kit wall does
    /// nothing). True per-instance colour would need the property declared in the shader's
    /// instancing buffer, which URP/Lit doesn't do for _EmissionColor. But BatchKey
    /// already includes the material, so a distinct material is a distinct batch that
    /// draws its own colour — and the colours here come from a PALETTE (a room's torch
    /// colour), not from continuous per-instance variation, so the batch count is bounded
    /// by the number of distinct colours in play rather than by instance count.
    ///
    /// CACHING IS THE WHOLE POINT: same colour must return the SAME material instance, or
    /// every wall becomes its own batch and instancing is lost entirely.
    ///
    /// Variants are runtime `new Material(...)` copies, so they must be destroyed on
    /// regenerate — the dungeon rebuilds on every F1/PgUp and these would otherwise leak
    /// a full set each time.
    /// </summary>
    public static class EmissiveMaterialVariants
    {
        static readonly Dictionary<(Material src, Color color), Material> cache =
            new Dictionary<(Material, Color), Material>();

        /// <summary>
        /// The variant of `source` whose emission is `color`. Returns `source` itself for
        /// a null source, so callers can stay branch-free when nothing is configured.
        /// </summary>
        /// <summary>Log each variant as it's created — source, shader, resolved colour,
        /// and whether the property write actually landed. One line per distinct colour.</summary>
        public static bool debugLog = false;

        public static Material Get(Material source, Color color, string emissionProperty = "_EmissionColor")
        {
            if (source == null) return null;

            var key = (source, color);
            if (cache.TryGetValue(key, out Material m) && m != null) return m;

            // Copying the source preserves its shader, keywords (_EMISSION must already
            // be enabled on the authored material) and every other property — only the
            // emission colour differs.
            m = new Material(source) { name = $"{source.name}_Emissive_{ColorUtility.ToHtmlStringRGB(color)}" };
            if (m.HasProperty(emissionProperty)) m.SetColor(emissionProperty, color);
            m.EnableKeyword("_EMISSION");

            // MUST clear EmissiveIsBlack explicitly. Unity sets that flag on any material
            // whose authored _EmissionColor is black — which is exactly the case here,
            // because the source material was previously driven by a MaterialPropertyBlock
            // (EmissionController) and so has no emission of its own. new Material(source)
            // copies the flag, and writing _EmissionColor afterwards does NOT clear it, so
            // the variant is tinted but never actually glows. Assigning the flag is what
            // un-sticks it.
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

            cache[key] = m;

            if (debugLog)
            {
                bool has = m.HasProperty(emissionProperty);
                Color wrote = has ? m.GetColor(emissionProperty) : default;
                bool instancing = m.enableInstancing;
                Debug.Log(
                    $"[EmissiveVariant] '{source.name}' shader='{m.shader.name}' " +
                    $"prop='{emissionProperty}' exists={has} " +
                    $"asked=({color.r:0.00},{color.g:0.00},{color.b:0.00}) " +
                    $"wrote=({wrote.r:0.00},{wrote.g:0.00},{wrote.b:0.00}) " +
                    $"maxComponent={Mathf.Max(wrote.r, wrote.g, wrote.b):0.00} (needs >1 to bloom) " +
                    $"giFlags={m.globalIlluminationFlags} keyword_EMISSION={m.IsKeywordEnabled("_EMISSION")} " +
                    $"enableInstancing={instancing}");

                if (!instancing)
                    Debug.LogWarning(
                        $"[EmissiveVariant] '{source.name}' has Enable GPU Instancing OFF. The kit renders " +
                        "through Graphics.RenderMeshInstanced, so tick it on the source material.");
            }

            return m;
        }

        /// <summary>
        /// Destroy every variant and empty the cache. Call on regenerate — these are
        /// runtime material instances and Unity will not collect them on its own.
        /// </summary>
        public static void Clear()
        {
            foreach (var kv in cache)
            {
                if (kv.Value == null) continue;
                if (Application.isPlaying) Object.Destroy(kv.Value);
                else Object.DestroyImmediate(kv.Value);
            }
            cache.Clear();
        }

        /// <summary>Statics survive fast enter-playmode; a stale cache would hand out
        /// materials belonging to a destroyed session (same trap NoiseBus guards).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => cache.Clear();
    }
}
