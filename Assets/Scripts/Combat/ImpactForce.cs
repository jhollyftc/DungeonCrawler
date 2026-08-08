using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// THE impact-normalization formula, shared so audio and damage can't drift:
    /// force is measured FROM the silence floor, not from zero. Normalizing from
    /// zero squashes every impact into the same value because real impacts live
    /// in a narrow band (the lesson PhysicsDoorAudio's tuning taught). ImpactAudio
    /// inlines the same math for sound; ThrownDamage uses this for harm.
    /// </summary>
    public static class ImpactForce
    {
        /// <summary>0 at/below `silentBelow`, 1 at/above `fullForce`, linear between.</summary>
        public static float Evaluate(float speed, float silentBelow, float fullForce) =>
            Mathf.Clamp01((speed - silentBelow) / Mathf.Max(0.01f, fullForce - silentBelow));
    }
}
