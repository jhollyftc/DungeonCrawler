using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DungeonGen
{
    /// <summary>
    /// Smooth scale-up (+ optional color pulse) on hover for a UI Button — the
    /// stock Button.Transition (Color Tint / Sprite Swap) can't do a scale pop,
    /// so this fills that gap without authoring an Animator Controller for one
    /// menu button.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class ButtonHoverEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [Tooltip("Scale multiplier while hovered (1 = no change).")]
        public float hoverScale = 1.1f;
        [Tooltip("How fast the scale/color ease toward their target, per second.")]
        public float transitionSpeed = 10f;

        [Header("Optional color pulse")]
        [Tooltip("Left empty, only the scale changes. Assign the button's Image to also shift its color on hover.")]
        public Graphic targetGraphic;
        public Color hoverColor = Color.white;

        RectTransform rt;
        Vector3 baseScale;
        Color baseColor;
        bool hovered;

        void Awake()
        {
            rt = (RectTransform)transform;
            baseScale = rt.localScale;
            if (targetGraphic != null) baseColor = targetGraphic.color;
        }

        void Update()
        {
            Vector3 targetScale = hovered ? baseScale * hoverScale : baseScale;
            rt.localScale = Vector3.Lerp(rt.localScale, targetScale, transitionSpeed * Time.deltaTime);

            if (targetGraphic != null)
            {
                Color targetColor = hovered ? hoverColor : baseColor;
                targetGraphic.color = Color.Lerp(targetGraphic.color, targetColor, transitionSpeed * Time.deltaTime);
            }
        }

        public void OnPointerEnter(PointerEventData eventData) => hovered = true;
        public void OnPointerExit(PointerEventData eventData) => hovered = false;
    }
}
