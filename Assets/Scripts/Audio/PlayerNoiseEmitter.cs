using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Bridges the player's footsteps and landings onto the NoiseBus. Put it on
    /// the player root (beside PlayerFootsteps + FirstPersonController). Loudness
    /// tracks how fast you're actually moving and drops hard when crouched — so
    /// crouch-sneaking genuinely shrinks the radius at which NPCs hear you, and it
    /// composes with the door quiet-swing threshold you already built. This is the
    /// player half of the stealth loop the crouch system was built for.
    /// </summary>
    [RequireComponent(typeof(PlayerFootsteps))]
    [DisallowMultipleComponent]
    public class PlayerNoiseEmitter : MonoBehaviour
    {
        [Tooltip("Loudness of a step at a standstill-to-walk pace (min end) and at full sprint (max end).")]
        public Vector2 stepLoudness = new Vector2(0.25f, 1f);
        [Tooltip("Multiplies step loudness while crouched — on TOP of the fact that crouching already slows you. This is the deliberate stealth reward.")]
        [Range(0f, 1f)] public float crouchMultiplier = 0.3f;
        [Tooltip("Loudness of a hard landing from a fall.")]
        [Range(0f, 1f)] public float landLoudness = 0.9f;

        PlayerFootsteps footsteps;
        FirstPersonController controller;

        void Awake()
        {
            footsteps = GetComponent<PlayerFootsteps>();
            controller = GetComponent<FirstPersonController>();
        }

        void OnEnable()
        {
            if (footsteps == null) return;
            footsteps.OnStep += HandleStep;
            footsteps.OnLand += HandleLand;
        }

        void OnDisable()
        {
            if (footsteps == null) return;
            footsteps.OnStep -= HandleStep;
            footsteps.OnLand -= HandleLand;
        }

        void HandleStep()
        {
            float loud = stepLoudness.x;
            if (controller != null)
            {
                float speed01 = Mathf.Clamp01(controller.HorizontalSpeed / Mathf.Max(0.01f, controller.sprintSpeed));
                loud = Mathf.Lerp(stepLoudness.x, stepLoudness.y, speed01);
                if (controller.IsCrouching) loud *= crouchMultiplier;
            }
            NoiseBus.Emit(transform.position, loud, transform, Faction.Player);
        }

        void HandleLand(float impactSpeed) =>
            NoiseBus.Emit(transform.position, landLoudness, transform, Faction.Player);
    }
}
