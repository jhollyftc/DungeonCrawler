using System.Collections.Generic;
using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// F7 overlay: how many audio voices are actually in use, and how many are being STOLEN.
    ///
    /// WHY THIS EXISTS BEFORE THE BUDGET IS SET. §5 of SOUNDSYSTEM_PLAN allocates voices per
    /// category, and the numbers written there are explicitly a guess. Setting a budget from a
    /// guess is how you end up tuning against the wrong constraint — the same
    /// instrument-before-hypothesising discipline that resolved the crowd jitter (smoothing at
    /// 0.99 changed nothing) and the separation perf work (zeroing separationStrength before
    /// optimizing anything).
    ///
    /// THE NUMBER THAT MATTERS IS `isVirtual`, NOT THE SOURCE COUNT. Unity keeps playing far
    /// more AudioSources than it can hear: beyond the real-voice limit (32 by default) the
    /// quietest and least important are VIRTUALIZED — still tracked, still advancing, simply
    /// not audible. So "50 sources playing" is not a problem and "6 virtual" is, and only the
    /// second is visible from `AudioSource.isVirtual`. A source count alone would have told you
    /// nothing about whether anything was being lost.
    ///
    /// The failure this is meant to catch is combat sounds and footsteps dropping out during a
    /// busy fight — which gets investigated as a COMBAT bug, because that is when it happens.
    /// A peak watermark is kept for exactly that reason: the moment you most need the number is
    /// the moment you are too busy fighting to read it.
    ///
    /// Scans on an interval rather than per frame — FindObjectsOfType is not cheap, and this is
    /// a diagnostic, not a system anything depends on.
    /// </summary>
    public class AudioBudgetDebug : MonoBehaviour
    {
        [Tooltip("Toggle the overlay.")]
        public KeyCode toggleKey = KeyCode.F7;
        public bool visible = false;
        [Tooltip("Seconds between scans. FindObjectsOfType walks every loaded object, so this is deliberately not per-frame.")]
        public float scanInterval = 0.25f;
        [Tooltip("Screen position of the overlay.")]
        public Vector2 origin = new Vector2(12f, 300f);

        int maxRealVoices;
        int playing, virtualized, total;
        int peakPlaying, peakVirtual;
        readonly Dictionary<string, int> byGroup = new Dictionary<string, int>();
        readonly Dictionary<string, int> virtualByGroup = new Dictionary<string, int>();
        readonly Dictionary<string, int> peakByGroup = new Dictionary<string, int>();
        readonly Dictionary<string, int> peakVirtualByGroup = new Dictionary<string, int>();
        float peakAt = -1f;
        float nextScan;

        void Awake() => maxRealVoices = AudioSettings.GetConfiguration().numRealVoices;

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
            if (!visible || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + Mathf.Max(0.05f, scanInterval);
            Scan();
        }

        void Scan()
        {
            byGroup.Clear();
            virtualByGroup.Clear();
            playing = virtualized = 0;

            var all = Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            total = all.Length;
            foreach (var s in all)
            {
                if (s == null || !s.isPlaying) continue;
                playing++;

                // Ungrouped sources route to Master — worth naming as such rather than
                // "(none)", because an unrouted source is usually an oversight and this is
                // where it becomes visible.
                string g = s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "<unrouted>";
                byGroup.TryGetValue(g, out int n); byGroup[g] = n + 1;

                if (s.isVirtual)
                {
                    virtualized++;
                    virtualByGroup.TryGetValue(g, out int v); virtualByGroup[g] = v + 1;
                }
            }

            // SNAPSHOT THE BREAKDOWN AT THE PEAK, not just the scalar. The live per-group
            // numbers are whatever is playing when you happen to look, and the moment worth
            // seeing is over before you can read it - a screenshot mid-fight showed 8 voices
            // while the peak had been 189. A high-water MARK tells you there is a problem;
            // a high-water BREAKDOWN tells you which category caused it.
            if (playing > peakPlaying)
            {
                peakPlaying = playing;
                peakByGroup.Clear();
                foreach (var kv in byGroup) peakByGroup[kv.Key] = kv.Value;
                peakAt = Time.unscaledTime;
            }
            if (virtualized > peakVirtual)
            {
                peakVirtual = virtualized;
                peakVirtualByGroup.Clear();
                foreach (var kv in virtualByGroup) peakVirtualByGroup[kv.Key] = kv.Value;
            }
        }

        void OnGUI()
        {
            if (!visible) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"AUDIO VOICES   (F7)   real limit {maxRealVoices}");
            sb.AppendLine($"playing {playing}   virtual {virtualized}   sources {total}");
            sb.AppendLine($"peak    {peakPlaying}   peak virt {peakVirtual}");
            sb.AppendLine("");
            foreach (var kv in byGroup)
            {
                virtualByGroup.TryGetValue(kv.Key, out int v);
                sb.AppendLine(v > 0 ? $"  {kv.Key,-12} {kv.Value,3}   ({v} STOLEN)"
                                    : $"  {kv.Key,-12} {kv.Value,3}");
            }
            if (peakByGroup.Count > 0)
            {
                float ago = peakAt < 0f ? 0f : Time.unscaledTime - peakAt;
                sb.AppendLine("");
                sb.AppendLine($"AT PEAK ({peakPlaying} voices, {ago:N0}s ago)");
                foreach (var kv in peakByGroup)
                {
                    peakVirtualByGroup.TryGetValue(kv.Key, out int pv);
                    sb.AppendLine(pv > 0 ? $"  {kv.Key,-12} {kv.Value,3}   ({pv} STOLEN)"
                                         : $"  {kv.Key,-12} {kv.Value,3}");
                }
            }
            if (peakVirtual > 0)
            {
                sb.AppendLine("");
                sb.AppendLine("VOICES ARE BEING STOLEN — LOWER cull distances so fewer");
                sb.AppendLine("sources play at all, and set AudioSource.priority so the");
                sb.AppendLine("ones worth keeping win (scale is INVERTED: lower = kept).");
            }

            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 12,
                richText = false,
            };
            string text = sb.ToString();
            Vector2 size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(origin.x, origin.y, size.x + 16f, size.y + 12f), text, style);
        }

        /// <summary>Clear the watermarks — call before a measured run.</summary>
        public void ResetPeaks()
        {
            peakPlaying = 0; peakVirtual = 0; peakAt = -1f;
            peakByGroup.Clear(); peakVirtualByGroup.Clear();
        }
    }
}
