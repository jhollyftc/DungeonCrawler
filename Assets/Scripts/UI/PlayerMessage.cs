using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// A transient one-line message — "The door is locked."
    ///
    /// THE PROJECT HAD NO CHANNEL FOR THIS. `PlayerInteractor` draws a PROMPT, which is a
    /// different thing: a prompt describes what pressing a key WOULD do and is shown while you
    /// look at something. This is feedback about what just happened, and it has to survive you
    /// looking away.
    ///
    /// Deliberately the same shape as the dev overlay — `OnGUI`, no prefab, no canvas, no
    /// authoring step — because the alternative is a UI dependency for one line of text. When a
    /// real HUD exists this becomes a thin adapter onto it and every caller stays unchanged.
    ///
    /// SELF-INSTALLING via a static accessor, so nothing has to remember to add it to the player
    /// prefab. That is the `PlayerFov.Ensure` lesson: a missing component that silently swallows
    /// the effect is the most expensive kind of wiring bug here.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerMessage : MonoBehaviour
    {
        [Tooltip("Seconds the line stays fully visible before it starts fading.")]
        public float holdTime = 1.6f;
        [Tooltip("Seconds the fade takes.")]
        public float fadeTime = 0.8f;
        [Tooltip("Font size. Matches the dev overlay's default.")]
        public int fontSize = 16;
        [Tooltip("Fraction of screen height above centre to draw at. Kept clear of the interact prompt.")]
        [Range(0f, 0.5f)] public float heightFraction = 0.28f;

        static PlayerMessage instance;

        string text;
        float shownAt;

        /// <summary>Show a line. Creates the holder on first use, so callers need no wiring.</summary>
        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (instance == null)
            {
                var go = new GameObject("PlayerMessage");
                instance = go.AddComponent<PlayerMessage>();
                DontDestroyOnLoad(go);
            }
            instance.text = message;
            instance.shownAt = Time.time;
        }

        void Awake()
        {
            // A scene-authored one wins over the auto-created holder, so tuning is possible.
            if (instance != null && instance != this) { Destroy(instance.gameObject); }
            instance = this;
        }

        void OnGUI()
        {
            if (string.IsNullOrEmpty(text)) return;

            float age = Time.time - shownAt;
            if (age > holdTime + fadeTime) { text = null; return; }

            float alpha = age <= holdTime ? 1f : 1f - (age - holdTime) / Mathf.Max(0.0001f, fadeTime);

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter,
            };
            style.normal.textColor = new Color(1f, 0.92f, 0.75f, alpha);

            Vector2 size = style.CalcSize(new GUIContent(text));
            var rect = new Rect((Screen.width - size.x) * 0.5f,
                                Screen.height * (0.5f - heightFraction),
                                size.x, size.y);

            // A soft shadow, because this draws over dungeon walls of every brightness and plain
            // text vanishes against a lit one.
            var shadow = new GUIStyle(style);
            shadow.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.8f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, shadow);
            GUI.Label(rect, text, style);
        }
    }
}
