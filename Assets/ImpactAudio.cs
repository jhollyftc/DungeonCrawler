using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Speed-driven collision sound for any Rigidbody prop — a thrown barrel, a
    /// crate you shouldered into, a chair a fight knocked over.
    ///
    /// Same payoff as PhysicsDoorAudio: FORCE IS AUDIBLE FOR FREE. Every contact
    /// carries its own speed, so a barrel rolled gently off a table taps and one
    /// hurled across a room bangs, with no special-casing — it falls out of the
    /// physics. Volume and pitch both track impact speed.
    ///
    /// The trap here is OnCollisionEnter, which is NOT one-per-throw: a barrel
    /// landing bounces, rolls, and re-contacts the floor many times over a second
    /// or two, and each of those is a fresh Enter. Naively played, that machine-
    /// guns the clip until the prop settles. Two gates stop it — a speed floor
    /// (settling contacts are slow) and a retrigger interval — which is the same
    /// lesson the door's thunkArmAngle taught: an impact sound needs a reason to
    /// be allowed to fire, not just an event.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class ImpactAudio : MonoBehaviour
    {
        [Header("Source (should be 3D — Spatial Blend = 1)")]
        [Tooltip("One-shot impacts. Left empty, a 3D source is added at Awake so a prop can be made audible by dropping this component on it.")]
        [SerializeField] private AudioSource impactSource;

        [Header("Clips")]
        [Tooltip("Impact sounds. Several = free variation, so a barrel bouncing twice doesn't sound like a copy-paste.")]
        [SerializeField] private AudioClip[] impactClips;

        [Header("Speed → loudness")]
        [Tooltip("Impact speed (m/s) that counts as FULL FORCE — roughly the speed this prop lands at when thrown hard. See Carryable.throwSpeed for the number it leaves your hands at.")]
        [SerializeField] private float fullForceSpeed = 8f;
        [Tooltip("Impacts slower than this (m/s) are SILENT. This is what stops a prop ticking as it rolls to a stop and rattles against the floor. Raise it if settling props chatter.")]
        [SerializeField] private float silentBelowSpeed = 1.2f;
        [Tooltip("Quietest an audible impact can be.")]
        [Range(0f, 1f)][SerializeField] private float minimumVolume = 0.25f;
        [Range(0f, 1f)][SerializeField] private float maximumVolume = 1f;

        [Header("Retrigger")]
        [Tooltip("Minimum seconds between impact sounds. A bouncing barrel fires OnCollisionEnter repeatedly; without this it machine-guns the clip.")]
        [SerializeField] private float minimumInterval = 0.08f;

        [Header("Variation")]
        [Tooltip("Random pitch spread, so repeated hits aren't identical.")]
        [SerializeField] private Vector2 pitchRange = new Vector2(0.92f, 1.08f);
        [Tooltip("Harder hits pitch DOWN (a heavier, meatier bang) rather than up. Set both to 1 to disable.")]
        [SerializeField] private Vector2 forcePitchRange = new Vector2(1.05f, 0.9f);

        [Tooltip("Log every contact with its speed and whether it played. The fastest way to find your real fullForceSpeed / silentBelowSpeed: throw the prop hard, read the numbers.")]
        [SerializeField] private bool debugAudio = false;

        /// <summary>
        /// Fired when an impact is loud enough to be heard: (world position, loudness 0..1).
        /// This is the hook for NPC alerting — a barrel thrown across a room makes
        /// noise SOMEWHERE ELSE, which is what turns carrying into a distraction
        /// mechanic rather than a toy. Nothing listens yet.
        /// </summary>
        public event System.Action<Vector3, float> OnImpact;

        private float nextAudibleTime;

        private void Awake()
        {
            if (impactSource == null)
            {
                impactSource = gameObject.AddComponent<AudioSource>();
                impactSource.spatialBlend = 1f;   // 3D, or a barrel across the map is as loud as one at your feet
                impactSource.rolloffMode = AudioRolloffMode.Linear;
                impactSource.maxDistance = 25f;
            }
            impactSource.playOnAwake = false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            float speed = collision.relativeVelocity.magnitude;

            if (speed < silentBelowSpeed)
            {
                if (debugAudio) Debug.Log($"[ImpactAudio] {name} hit '{collision.collider.name}' at {speed:0.00} m/s — muted (< {silentBelowSpeed})");
                return;
            }
            if (Time.time < nextAudibleTime)
            {
                if (debugAudio) Debug.Log($"[ImpactAudio] {name} hit at {speed:0.00} m/s — suppressed (retrigger interval)");
                return;
            }

            nextAudibleTime = Time.time + minimumInterval;

            float force = Mathf.Clamp01((speed - silentBelowSpeed) / Mathf.Max(0.01f, fullForceSpeed - silentBelowSpeed));
            float volume = Mathf.Lerp(minimumVolume, maximumVolume, force);

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
            OnImpact?.Invoke(point, force);

            if (impactClips == null || impactClips.Length == 0)
            {
                if (debugAudio) Debug.LogWarning($"[ImpactAudio] {name} impacted at {speed:0.00} m/s but no clips are assigned.");
                return;
            }

            AudioClip clip = impactClips[Random.Range(0, impactClips.Length)];
            if (clip == null) return;

            impactSource.pitch = Mathf.Lerp(forcePitchRange.x, forcePitchRange.y, force)
                                 * Random.Range(pitchRange.x, pitchRange.y);
            impactSource.PlayOneShot(clip, volume);

            if (debugAudio)
                Debug.Log($"[ImpactAudio] {name} hit '{collision.collider.name}' at {speed:0.00} m/s → '{clip.name}' vol {volume:0.00} (force {force:0.00})");
        }
    }
}
