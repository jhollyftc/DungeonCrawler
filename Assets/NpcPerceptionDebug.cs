using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// DEBUG overlay for NPC senses: an awareness bar over every goblin in view, plus
    /// keys to switch SIGHT and HEARING off independently so each can be tuned in
    /// isolation. Drop one on any scene object.
    ///
    /// Game-view OnGUI rather than gizmos on purpose: NpcPerception's gizmos only draw
    /// for the SELECTED object in the Scene view, which is useless for the actual
    /// question — "what does a whole room of goblins currently know?" This projects
    /// every live perception component to screen at once, so a crowd's state is legible
    /// while you play it.
    ///
    /// WATCH FOR: a bar that is still near ZERO while the NPC already reads SEES.
    /// That's not an overlay bug — TickSight sets CurrentTarget the instant the cone and
    /// LOS test pass, regardless of Awareness01, and NpcBrain goes straight to Alerted on
    /// CurrentTarget != null. So sight detection is binary and INSTANT; the awareness
    /// meter only really gates the hearing → investigate path. That is the likeliest
    /// reason goblins feel over-sensitive, and it is a design decision to make, not a
    /// bug to quietly patch.
    /// </summary>
    [DisallowMultipleComponent]
    public class NpcPerceptionDebug : MonoBehaviour
    {
        [Header("Toggles")]
        [Tooltip("Show/hide the whole overlay.")]
        public KeyCode toggleKey = KeyCode.F3;
        [Tooltip("Switch SIGHT off for every NPC — they fall back to hearing alone.")]
        public KeyCode sightKey = KeyCode.F4;
        [Tooltip("Switch HEARING off for every NPC — they fall back to sight alone.")]
        public KeyCode hearingKey = KeyCode.F5;
        public bool visible = true;

        [Header("Bars")]
        [Tooltip("Only draw NPCs within this distance (m) of the camera — a deep dungeon can hold a lot of them.")]
        public float maxDistance = 35f;
        [Tooltip("Height (m) above the NPC's origin to float the bar.")]
        public float barHeight = 2.3f;
        public float barWidth = 54f;
        public float barThickness = 7f;
        [Tooltip("Also print the numeric awareness and state next to each bar.")]
        public bool showLabels = true;

        Camera cam;
        GUIStyle labelStyle, headerStyle;
        Texture2D px;

        void Awake()
        {
            // 1x1 white texture — GUI.DrawTexture tints it, so one texture draws every
            // bar in any colour without needing an asset.
            px = new Texture2D(1, 1);
            px.SetPixel(0, 0, Color.white);
            px.Apply();
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
            if (Input.GetKeyDown(sightKey)) NpcPerception.SightEnabled = !NpcPerception.SightEnabled;
            if (Input.GetKeyDown(hearingKey)) NpcPerception.HearingEnabled = !NpcPerception.HearingEnabled;
        }

        void OnGUI()
        {
            if (!visible || !Application.isPlaying) return;

            labelStyle ??= new GUIStyle { fontSize = 11, normal = { textColor = Color.white } };
            headerStyle ??= new GUIStyle { fontSize = 14, normal = { textColor = Color.white } };

            // Header: which senses are live, and the population being watched. Stated
            // plainly because a disabled sense is invisible otherwise — easy to leave
            // sight off and spend ten minutes wondering why nobody reacts.
            string sight = NpcPerception.SightEnabled ? "ON" : "<OFF>";
            string hear = NpcPerception.HearingEnabled ? "ON" : "<OFF>";
            GUI.Label(new Rect(12f, Screen.height - 46f, 460f, 20f),
                      $"[{sightKey}] Sight {sight}    [{hearingKey}] Hearing {hear}    " +
                      $"NPCs {NpcPerception.All.Count}", headerStyle);

            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            float maxSq = maxDistance * maxDistance;
            Vector3 camPos = cam.transform.position;

            for (int i = 0; i < NpcPerception.All.Count; i++)
            {
                var p = NpcPerception.All[i];
                if (p == null) continue;

                Vector3 head = p.transform.position + Vector3.up * barHeight;
                if ((head - camPos).sqrMagnitude > maxSq) continue;

                Vector3 sp = cam.WorldToScreenPoint(head);
                if (sp.z <= 0f) continue;   // behind the camera projects to a mirrored point

                float x = sp.x - barWidth * 0.5f;
                float y = Screen.height - sp.y;   // GUI space is y-down

                // Track: makes an EMPTY bar readable. Without it, awareness 0 draws
                // nothing and an oblivious NPC looks identical to one not being drawn.
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x, y, barWidth, barThickness), px);

                float a = Mathf.Clamp01(p.Awareness01);
                GUI.color = p.SeesTarget
                    ? Color.red                                  // in direct view right now
                    : Color.Lerp(new Color(0.4f, 0.9f, 0.4f), new Color(1f, 0.75f, 0.1f), a);
                GUI.DrawTexture(new Rect(x, y, barWidth * a, barThickness), px);

                // Threshold notch — above this the brain will go and investigate.
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                GUI.DrawTexture(new Rect(x + barWidth * p.investigateThreshold - 1f, y - 1f, 1f, barThickness + 2f), px);

                GUI.color = Color.white;
                if (!showLabels) continue;

                string state = p.SeesTarget ? "SEES"
                             : p.HasLastKnown && a >= p.investigateThreshold ? "investigating"
                             : a > 0.01f ? "suspicious"
                             : "idle";
                if (p.SinceHeard < 0.6f) state += " +heard";   // flash so a noise hit is visible as it lands
                GUI.Label(new Rect(x, y + barThickness + 1f, 200f, 16f), $"{a:0.00}  {state}", labelStyle);
            }
        }
    }
}
