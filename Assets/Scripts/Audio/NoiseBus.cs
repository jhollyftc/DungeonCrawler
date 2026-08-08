using System;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>Who made a sound / who a sound is about. Drives what an NpcPerception reacts to.</summary>
    public enum Faction { Player, Dungeon, Neutral }

    /// <summary>
    /// One audible event in the world. Loudness is 0..1, already normalized from a
    /// silence floor by whatever emitted it (that's the same 0..1 ImpactAudio and
    /// the door already produce), so listeners never need to know the source's
    /// physical units. `alertTarget` is usually the position, but a shout (Phase 5)
    /// points listeners at the THREAT rather than at the shouter.
    /// </summary>
    public struct NoiseEvent
    {
        public Vector3 position;
        public float loudness;      // 0..1
        public Transform source;    // may be null
        public Faction faction;
        public Vector3 alertTarget; // where a reacting NPC should look — usually == position
        public float time;
    }

    /// <summary>
    /// Central noise channel. Emitters raise events; NpcPerception listens. A bus
    /// rather than per-NPC subscriptions because props and NPCs are both destroyed
    /// and respawned on every dungeon regen — direct wiring would have to be
    /// rediscovered after every BuildMesh. Here emitters and listeners are mutually
    /// ignorant and nothing needs re-wiring.
    ///
    /// The sensing SIDE already existed and went unconsumed (ImpactAudio.OnImpact,
    /// PhysicsDoor.OnSwingStart, footsteps, IsCrouching); the thin emitter adapters
    /// (ImpactNoiseEmitter, DoorNoiseEmitter, PlayerNoiseEmitter) bridge those into
    /// this bus without any of those systems learning about AI.
    /// </summary>
    public static class NoiseBus
    {
        public static event Action<NoiseEvent> OnNoise;

        public static void Emit(in NoiseEvent e)
        {
            if (e.loudness <= 0f) return;
            OnNoise?.Invoke(e);
        }

        public static void Emit(Vector3 position, float loudness, Transform source = null, Faction faction = Faction.Neutral)
        {
            if (loudness <= 0f) return;
            OnNoise?.Invoke(new NoiseEvent
            {
                position = position,
                loudness = Mathf.Clamp01(loudness),
                source = source,
                faction = faction,
                alertTarget = position,
                time = Time.time,
            });
        }

        // A static event holds delegates across play sessions when "Enter Play Mode
        // without Domain Reload" is on — stale listeners from a previous run would
        // fire into destroyed objects. Reset it as the subsystem registers.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => OnNoise = null;
    }
}
