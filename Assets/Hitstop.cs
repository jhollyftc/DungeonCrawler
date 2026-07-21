using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// THE single owner of Time.timeScale. A landed hit requests a brief global
    /// time dip — the whole scene (goblin flinch, particles, physics) hangs for a
    /// crunchy beat, then snaps back. Overlapping requests coalesce: the strongest
    /// (lowest) scale wins and the deadline extends, so two hits in one frame
    /// can't fight over the value or restore each other early.
    ///
    /// Restoration runs on UNSCALED time via a hidden runner (a dip must not
    /// prolong itself). Keep dips SHORT (≤0.1s) — timeScale also slows
    /// FixedUpdate, so a long dip visibly stalls doors and thrown props.
    ///
    /// If a pause menu ever needs timeScale = 0, route it through here too —
    /// two owners of Time.timeScale is the same oscillation bug as two writers
    /// of a viewmodel pose.
    /// </summary>
    public static class Hitstop
    {
        static float restoreAtUnscaled;
        static bool active;
        static HitstopRunner runner;

        /// <summary>Dip global time to `scale` for `duration` seconds (unscaled).</summary>
        public static void Request(float duration, float scale)
        {
            if (duration <= 0f) return;
            scale = Mathf.Clamp(scale, 0f, 1f);

            EnsureRunner();
            Time.timeScale = active ? Mathf.Min(Time.timeScale, scale) : scale;
            restoreAtUnscaled = Mathf.Max(restoreAtUnscaled, Time.unscaledTime + duration);
            active = true;
        }

        static void EnsureRunner()
        {
            if (runner != null) return;
            var go = new GameObject("Hitstop");
            go.hideFlags = HideFlags.HideInHierarchy;
            Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<HitstopRunner>();
        }

        internal static void Tick()
        {
            if (!active) return;
            if (Time.unscaledTime < restoreAtUnscaled) return;
            Time.timeScale = 1f;
            active = false;
        }

        // Statics survive both scene reloads (F1) and disabled domain reloads —
        // and a stale timeScale is the worst kind of stale. Reset hard.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            active = false;
            restoreAtUnscaled = 0f;
            runner = null;
            Time.timeScale = 1f;
        }
    }

    /// <summary>Hidden ticker for Hitstop — restores time on unscaled time. Never authored onto anything.</summary>
    internal class HitstopRunner : MonoBehaviour
    {
        void Update() => Hitstop.Tick();
    }
}
