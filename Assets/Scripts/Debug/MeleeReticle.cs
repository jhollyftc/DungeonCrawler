using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Crosshair for melee, and a DIAGNOSTIC as much as an aiming aid: it lights up only
    /// when a swing fired this instant would actually connect, because it asks
    /// MeleeAttack.PreviewWouldHit — the real sweep geometry and the real rejection rules,
    /// minus the damage. So "I aimed at the barrel and missed" stops being a mystery:
    /// if the reticle never lit, it wasn't your aim.
    ///
    /// Put it on the player (next to PlayerMelee). Drawn with OnGUI so it needs no canvas
    /// — same dev-overlay approach FirstPersonController uses.
    ///
    /// WORTH KNOWING while reading it: the sweep is a VERTICAL CAPSULE, not a ray. It
    /// reaches sweepDownExtent BELOW the aim line (1.3m by default, so short enemies are
    /// catchable from eye level) and sweepUpExtent above. The crosshair marks the aim
    /// line, not the volume — you can legitimately connect with something the crosshair
    /// isn't on top of, which is a feature, not drift.
    /// </summary>
    [DisallowMultipleComponent]
    public class MeleeReticle : MonoBehaviour
    {
        [Header("Display")]
        public bool visible = true;
        public KeyCode toggleKey = KeyCode.F6;
        [Tooltip("Arm length (px) of the crosshair.")]
        public float size = 10f;
        [Tooltip("Gap (px) at the centre, so the crosshair frames a target instead of covering it.")]
        public float gap = 4f;
        public float thickness = 2f;

        [Header("Colours")]
        [Tooltip("Nothing a swing could hit.")]
        public Color idleColor = new Color(1f, 1f, 1f, 0.45f);
        [Tooltip("A swing right now WOULD connect.")]
        public Color targetColor = new Color(1f, 0.35f, 0.3f, 0.95f);
        [Tooltip("Dimmed while an attack is playing, so you can see the swing has been committed.")]
        public Color swingingColor = new Color(1f, 1f, 1f, 0.2f);

        [Header("Diagnostics")]
        [Tooltip("Print WHY a swing wouldn't land ('same faction', 'no line of sight', 'behind the swing', 'already dead', 'nothing in reach') beneath the crosshair. This is the part that tells you a miss wasn't aim.")]
        public bool showReason = false;
        [Tooltip("Seconds between preview queries. It's a couple of physics casts, so this needn't run every frame.")]
        public float queryInterval = 0.05f;

        MeleeAttack melee;
        PlayerMelee playerMelee;
        Texture2D px;
        GUIStyle labelStyle;

        float nextQuery;
        bool wouldHit;
        string reason = "";
        Transform previewTarget;

        void Awake()
        {
            melee = GetComponent<MeleeAttack>();
            playerMelee = GetComponent<PlayerMelee>();

            px = new Texture2D(1, 1);
            px.SetPixel(0, 0, Color.white);
            px.Apply();

            if (melee == null)
                Debug.LogWarning("[MeleeReticle] No MeleeAttack on this object — the crosshair will draw but can't report reach.", this);
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
            if (!visible || melee == null || Time.time < nextQuery) return;

            nextQuery = Time.time + Mathf.Max(0.01f, queryInterval);
            wouldHit = melee.PreviewWouldHit(out previewTarget, out reason);
        }

        void OnGUI()
        {
            if (!visible || !Application.isPlaying) return;

            // Committed swings dim the crosshair: the outcome is already decided, so a
            // live reach readout during the swing would be noise.
            bool swinging = playerMelee != null && (playerMelee.IsSwinging || playerMelee.IsBashing);
            GUI.color = swinging ? swingingColor : (wouldHit ? targetColor : idleColor);

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float t = Mathf.Max(1f, thickness);

            GUI.DrawTexture(new Rect(cx - gap - size, cy - t * 0.5f, size, t), px);   // left
            GUI.DrawTexture(new Rect(cx + gap, cy - t * 0.5f, size, t), px);          // right
            GUI.DrawTexture(new Rect(cx - t * 0.5f, cy - gap - size, t, size), px);   // up
            GUI.DrawTexture(new Rect(cx - t * 0.5f, cy + gap, t, size), px);          // down

            GUI.color = Color.white;
            if (!showReason || swinging) return;

            labelStyle ??= new GUIStyle { fontSize = 11, alignment = TextAnchor.UpperCenter,
                                          normal = { textColor = new Color(1f, 1f, 1f, 0.7f) } };
            string text = wouldHit && previewTarget != null ? $"hit: {previewTarget.name}" : reason;
            GUI.Label(new Rect(cx - 150f, cy + gap + size + 6f, 300f, 18f), text, labelStyle);
        }
    }
}
