using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Frame timing on screen, in a BUILD, without the Profiler attached — F9.
    ///
    /// IT LEADS WITH THE WORST FRAME IN THE WINDOW, NOT THE AVERAGE, and that is the whole reason
    /// it is worth having. Every performance problem this project has actually hit was a SPIKE:
    /// torch shadows re-culling on a caster change, an incremental GC overrunning its slot, a
    /// render-graph rebuild. An averaged counter hides all of them — 200 FPS average and a 30ms
    /// hitch twice a second reads as "200 FPS", which is exactly the reading that sent several
    /// rounds of that investigation in the wrong direction. Average tells you whether the game is
    /// fast; the worst frame tells you whether it FEELS fast, and they are different questions.
    ///
    /// MILLISECONDS FIRST, FPS SECOND. Frame time is linear and additive — "that cost 2ms" is a
    /// sentence you can act on — while FPS is a reciprocal, so the same 2ms is worth 60 FPS at the
    /// top of the range and 5 at the bottom. Every measurement in CLAUDE.md §5 is in ms for that
    /// reason, and a counter reporting something else makes them hard to compare against.
    ///
    /// UNSCALED TIME THROUGHOUT: `Hitstop` scales `Time.timeScale` on every melee hit, so a scaled
    /// delta would report a frame-rate collapse every time the player connected with something.
    ///
    /// It is DEV TOOLING and draws through IMGUI, which allocates a couple of KB per frame no
    /// matter what is drawn (§12 — measured as a real contributor when the overlay was on). Fine
    /// for a development build; turn it off when measuring allocation, and it should not ship on.
    /// </summary>
    [DisallowMultipleComponent]
    public class FpsCounter : MonoBehaviour
    {
        [Tooltip("Toggles the readout. F1-F8 are taken; F9 is the next free one.")]
        public KeyCode toggleKey = KeyCode.F9;
        [Tooltip("Shown from the moment the game starts, without pressing anything.")]
        public bool visibleOnStart = false;

        [Tooltip("Seconds of history the average and the WORST frame are measured over. Short enough to react as you walk into a problem, long enough that one bad frame does not define the reading forever.")]
        [Range(0.25f, 5f)] public float window = 1f;
        [Tooltip("How often the displayed text is rebuilt, in seconds. NOT how often frames are measured — every frame is sampled. This only limits string building, which is the one part that allocates, and a number changing 60 times a second is unreadable anyway.")]
        [Range(0.05f, 1f)] public float refreshInterval = 0.25f;

        [Tooltip("Frame time in ms above which the worst-frame figure turns red. 16.7 = a missed 60Hz frame; lower it if you are targeting higher.")]
        public float spikeThresholdMs = 16.7f;

        [Range(8, 32)] public int fontSize = 13;
        [Tooltip("Corner offset in pixels from the top-left.")]
        public Vector2 screenOffset = new Vector2(10f, 10f);

        /// <summary>
        /// STATIC so it survives a regenerate. The player prefab is destroyed and respawned on
        /// every F1, so a per-instance flag would silently switch itself off exactly when you were
        /// mid-investigation — the same reason NpcPerceptionDebug's sight/hearing switches and the
        /// torch shadow toggle are static, and it carries the same play-mode reset, since a static
        /// otherwise keeps its value across fast-enter-playmode.
        /// </summary>
        static bool shown;
        static bool initialised;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { shown = false; initialised = false; }

        float accumulated;      // seconds of frame time in the current window
        int frames;             // frames in the current window
        float worstMs;          // slowest single frame in the current window
        float refreshTimer;
        float windowTimer;

        string text = "";
        bool spiking;
        GUIStyle style;

        void Awake()
        {
            // Applied ONCE per session rather than per spawn: after that the static is whatever
            // the player last chose, and a respawn re-asserting the authored default would undo
            // an F9 press mid-run.
            if (!initialised) { initialised = true; shown = visibleOnStart; }
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) shown = !shown;

            float dt = Time.unscaledDeltaTime;
            accumulated += dt;
            frames++;
            windowTimer += dt;

            float ms = dt * 1000f;
            if (ms > worstMs) worstMs = ms;

            refreshTimer += dt;
            if (refreshTimer < refreshInterval) return;
            refreshTimer = 0f;

            float avgMs = frames > 0 ? (accumulated / frames) * 1000f : 0f;
            float fps = avgMs > 0.0001f ? 1000f / avgMs : 0f;
            spiking = worstMs > spikeThresholdMs;

            // Built here, four times a second, rather than in OnGUI — which runs at least twice
            // per frame (Layout and Repaint), so building it there would triple the allocation
            // for no benefit.
            text = $"{avgMs:0.0} ms   {fps:0} fps\nworst {worstMs:0.0} ms";

            // The window RESETS rather than sliding: a true rolling window needs a ring buffer of
            // samples, and the only thing that buys is the worst frame decaying smoothly instead
            // of in steps. Stepping is arguably better here — a spike stays on screen for a full
            // window, long enough to read, instead of being averaged away before you look up.
            if (windowTimer >= window)
            {
                windowTimer = 0f;
                accumulated = 0f;
                frames = 0;
                worstMs = 0f;
            }
        }

        void OnGUI()
        {
            if (!shown) return;

            if (style == null || style.fontSize != fontSize)
                style = new GUIStyle(GUI.skin.label) { fontSize = fontSize, richText = false };

            var size = style.CalcSize(new GUIContent(text));
            var rect = new Rect(screenOffset.x, screenOffset.y, size.x + 12f, size.y + 8f);

            GUI.Box(rect, GUIContent.none);

            // Colour is the whole readout, not just the worst line: a glance has to answer "is it
            // spiking" without reading numbers, which is what you want while walking around
            // looking for where it happens.
            Color previous = GUI.color;
            GUI.color = spiking ? new Color(1f, 0.55f, 0.45f) : Color.white;
            GUI.Label(new Rect(rect.x + 6f, rect.y + 4f, rect.width, rect.height), text, style);
            GUI.color = previous;
        }
    }
}
