using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Bridges ImpactAudio.OnImpact onto the NoiseBus. Put it on any prop that
    /// should be HEARD when it's hit or thrown — the same prefabs that already
    /// carry ImpactAudio (barrels, crates, skulls). ImpactAudio already computes
    /// the 0..1 loudness from impact speed, so this just forwards it: a barrel
    /// hurled across a room makes noise where it LANDS, which is what turns
    /// carrying-and-throwing into a distraction mechanic.
    /// </summary>
    [RequireComponent(typeof(ImpactAudio))]
    [DisallowMultipleComponent]
    public class ImpactNoiseEmitter : MonoBehaviour
    {
        [Tooltip("Faction of this noise. Neutral for props: any faction reacts. (A prop the player throws is still Neutral — the barrel isn't on anyone's side.)")]
        public Faction faction = Faction.Neutral;
        [Tooltip("Scales the audio loudness into hearing loudness. >1 makes a prop 'louder' to AI than to your ears; <1 quieter.")]
        public float loudnessScale = 1f;

        ImpactAudio impact;

        void Awake() => impact = GetComponent<ImpactAudio>();
        void OnEnable() { if (impact != null) impact.OnImpact += Handle; }
        void OnDisable() { if (impact != null) impact.OnImpact -= Handle; }

        void Handle(Vector3 point, float loudness) =>
            NoiseBus.Emit(point, loudness * loudnessScale, transform, faction);
    }
}
