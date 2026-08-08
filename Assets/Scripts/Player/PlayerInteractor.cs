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
        [Tooltip("What the interaction cast can hit. MUST exclude the Viewmodel layer (the swinging sword/shield sit right in front of the camera and would block the cast), and usually the Player's own layer. A closer non-interactable collider blanks the prompt — that's the flicker.")]
        public LayerMask mask = ~0;
        public KeyCode key = KeyCode.E;
        [Tooltip("Log what the cast hits when it ISN'T interactable — names the collider + layer that's blocking the prompt, so you can add it to the exclude mask.")]
        public bool debugInteractor = false;

        IInteractable current;
        PlayerCarry carry;

        void Awake()
        {
            carry = GetComponentInParent<PlayerCarry>();
            if (carry == null) carry = transform.root.GetComponentInChildren<PlayerCarry>();

            // The viewmodel (sword/shield) sits right in front of the camera and is
            // never interactable — always strip it from the cast so a held item
            // can't block the prompt, no matter how the mask was authored. (Golden
            // rule: world queries exclude the Viewmodel layer.)
            int viewmodel = LayerMask.NameToLayer("Viewmodel");
            if (viewmodel >= 0) mask &= ~(1 << viewmodel);
        }

        void Update()
        {
            current = null;

            // Hands full: stand down. The carried prop floats right in front of the
            // camera and this SphereCast lands on it every frame (Physics.IgnoreCollision
            // spares the CONTACTS, not the QUERIES), so without this you'd get a
            // "Pick up Barrel" prompt for the barrel already in your arms and E would
            // mean two things at once. While something is held, PlayerCarry owns E.
            if (carry != null && carry.IsCarrying) return;

            if (cam != null &&
                Physics.SphereCast(cam.position, castRadius, cam.forward, out RaycastHit hit,
                                   range, mask, QueryTriggerInteraction.Ignore))
            {
                current = hit.collider.GetComponentInParent<IInteractable>();

                if (debugInteractor && current == null)
                    Debug.Log($"[Interactor] cast BLOCKED by '{hit.collider.name}' " +
                              $"(layer {hit.collider.gameObject.layer} = '{LayerMask.LayerToName(hit.collider.gameObject.layer)}') " +
                              $"at {hit.distance:0.00}m — nothing to interact with. Exclude that layer from the mask.", hit.collider);
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
