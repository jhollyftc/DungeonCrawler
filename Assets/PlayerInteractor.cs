using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Player-side half of interaction. SphereCasts from the camera (a thick
    /// ray — forgiving aim, no prompt strobing on thin colliders), shows a
    /// minimal prompt, fires Interact on the E key.
    /// </summary>
    public class PlayerInteractor : MonoBehaviour
    {
        public Transform cam;
        [Tooltip("Meters from the camera. Remember eye-to-target is longer than feet-to-target.")]
        public float range = 4.5f;
        [Tooltip("Thickness of the interaction 'ray'. Bigger = more forgiving aim.")]
        public float castRadius = 0.2f;
        public KeyCode key = KeyCode.E;

        IInteractable current;

        void Update()
        {
            current = null;
            if (cam != null &&
                Physics.SphereCast(cam.position, castRadius, cam.forward, out RaycastHit hit,
                                   range, ~0, QueryTriggerInteraction.Ignore))
            {
                current = hit.collider.GetComponentInParent<IInteractable>();
            }

            if (current != null && Input.GetKeyDown(key))
                current.Interact(transform);
        }

        void OnGUI()
        {
            if (current == null) return;
            var rect = new Rect(Screen.width * 0.5f - 100f, Screen.height * 0.6f, 200f, 30f);
            GUI.Label(rect, $"[{key}] {current.Prompt}", new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 20,
            });
        }
    }
}
