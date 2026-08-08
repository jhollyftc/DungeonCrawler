using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Simple chest lid.
    /// Assign the lid transform (pivot should be on the hinge).
    /// The lid rotates around a chosen LOCAL axis.
    /// </summary>
    public class ChestHinge : MonoBehaviour, IInteractable
    {
        public enum RotationAxis
        {
            X,
            Y,
            Z
        }

        [Header("Lid")]
        public Transform lid;
        public RotationAxis rotationAxis = RotationAxis.X;

        [Header("Movement")]
        public float openAngle = -110f;
        public float rotationSpeed = 180f;
        public bool startOpen;

        [Header("Source (auto-added 3D if empty)")]
        [Tooltip("One-shot source. Left empty, a 3D source (linear rolloff, 15m) is added at Awake.")]
        [SerializeField] private AudioSource audioSource;
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;

        [Header("Audio (optional)")]
        public AudioClip openClip;
        public AudioClip closeClip;

        bool isOpen;
        float currentAngle;
        float targetAngle;

        Quaternion lidRestRotation;

        public string Prompt => isOpen ? "Close" : "Open";

        void Awake()
        {
            if (lid == null)
            {
                Debug.LogWarning($"Chest '{name}' has no lid assigned.", this);
                enabled = false;
                return;
            }

            lidRestRotation = lid.localRotation;

            isOpen = startOpen;
            currentAngle = targetAngle = startOpen ? openAngle : 0f;

            ApplyRotation();
        }

        public void Interact(Transform interactor)
        {
            isOpen = !isOpen;
            targetAngle = isOpen ? openAngle : 0f;

            Play(isOpen ? openClip : closeClip);
        }

        void Update()
        {
            if (Mathf.Approximately(currentAngle, targetAngle))
                return;

            currentAngle = Mathf.MoveTowards(
                currentAngle,
                targetAngle,
                rotationSpeed * Time.deltaTime);

            ApplyRotation();
        }

        void ApplyRotation()
        {
            Vector3 axis = rotationAxis switch
            {
                RotationAxis.X => Vector3.right,
                RotationAxis.Y => Vector3.up,
                RotationAxis.Z => Vector3.forward,
                _ => Vector3.right
            };

            lid.localRotation =
                Quaternion.AngleAxis(currentAngle, axis) * lidRestRotation;
        }

        void Play(AudioClip clip)
        {
            if (clip == null)
                return;

            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.spatialBlend = 1f;
                audioSource.maxDistance = 15f;
            }

            AudioBus.Route(audioSource, mixerGroup);
            audioSource.PlayOneShot(clip);
        }
    }
}
