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
        [Tooltip("Mixer group this component's audio routes to. Set it HERE rather than on the AudioSource: the source is created at RUNTIME when the prefab has none, and an Output assigned in the inspector would cover only the authored case. Empty = straight to Master, i.e. today's behaviour.")]
        [SerializeField] private UnityEngine.Audio.AudioMixerGroup mixerGroup;

        [Header("Crowd control")]
        [Tooltip("Do not play at all beyond this distance from the listener (m). Rolloff only makes a distant impact QUIET — the source still starts and still holds a voice slot. This matters more here than anywhere else because impacts are the most NUMEROUS sound in the game: a measured crowd fight peaked at 111 simultaneous Physics voices, 84 of them being stolen.\n\n0 disables the cull.")]
        [SerializeField] private float cullDistance = 20f;

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
        [Tooltip("Force (0..1) at or above which an impact IGNORES the interval above and always registers. The interval is there to stop a settling prop machine-gunning, and settling impacts are weak — a genuinely hard hit landing just after a small one is exactly what you must hear.\n\nIt also gates DAMAGE, not just sound: the suppression path skips OnImpact, which DestructibleProp turns into damage, so a suppressed hit deals none. Without this override a barrel could bounce, then slam into a wall, and neither be heard nor break. 1 = never override (old behaviour).")]
        [Range(0f, 1f)][SerializeField] private float alwaysAudibleForce = 0.7f;

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
            // AND STOP IT. playOnAwake only governs a FUTURE start - it cannot undo one already
            // underway, and the engine acts on the authored flag before this runs. A source set to
            // play on awake with NO CLIP enters a playing state that never completes, so it reports
            // isPlaying forever while making no sound: silent, invisible, and holding a voice slot.
            // Measured: 186 phantom voices against a real-voice budget of 32.
            impactSource.Stop();
            impactSource.priority = AudioPriority.WorldImpact;
            AudioBus.Route(impactSource, mixerGroup);
        }

        private void OnCollisionEnter(Collision collision)
        {
            float speed = collision.relativeVelocity.magnitude;

            if (speed < silentBelowSpeed)
            {
                if (debugAudio) Debug.Log($"[ImpactAudio] {name} hit '{collision.collider.name}' at {speed:0.00} m/s — muted (< {silentBelowSpeed})");
                return;
            }
            float force = Mathf.Clamp01((speed - silentBelowSpeed) / Mathf.Max(0.01f, fullForceSpeed - silentBelowSpeed));

            // The retrigger gate stops a SETTLING prop machine-gunning — and settling
            // impacts are low-force by definition, so a hard enough hit overrides it.
            //
            // This matters well beyond the noise: the early return below skips OnImpact
            // TOO, and that event is what DestructibleProp turns into damage. Gating
            // purely on recency therefore swallowed the hit entirely — a barrel that
            // bounced and then slammed into a wall inside the window went silent AND
            // took no damage, so it simply didn't break. The killing blow is precisely
            // the one that must never be suppressed.
            bool overridesGate = force >= alwaysAudibleForce;

            if (Time.time < nextAudibleTime && !overridesGate)
            {
                if (debugAudio)
                    Debug.Log($"[ImpactAudio] {name} hit at {speed:0.00} m/s (force {force:0.00}) — " +
                              $"suppressed (retrigger interval; needs force >= {alwaysAudibleForce:0.00} to override)");
                return;
            }

            nextAudibleTime = Time.time + minimumInterval;
            float volume = Mathf.Lerp(minimumVolume, maximumVolume, force);

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
            OnImpact?.Invoke(point, force);

            // CULL AFTER OnImpact, NEVER BEFORE. That event is what DestructibleProp turns
            // into damage and what NPC alerting listens to — culling above this line would
            // mean a crate you cannot hear also cannot break, which is the exact mistake the
            // retrigger gate above already made once and carries a comment about. Distance
            // decides whether you HEAR the impact, not whether it HAPPENED.
            if (AudioCull.TooFar(transform, cullDistance)) return;

            if (impactClips == null || impactClips.Length == 0)
            {
                if (debugAudio) Debug.LogWarning($"[ImpactAudio] {name} impacted at {speed:0.00} m/s but no clips are assigned.");
                return;
            }

            AudioClip clip = impactClips[Random.Range(0, impactClips.Length)];
            if (clip == null) return;

            impactSource.pitch = Mathf.Lerp(forcePitchRange.x, forcePitchRange.y, force)
                                 * Random.Range(pitchRange.x, pitchRange.y);
            // Occlude from the CONTACT POINT, not the prop's pivot. A barrel resting against a
            // doorway can have its origin on the far side of the wall from where it was struck,
            // and the point you heard is the point the sound came from.
            AudioOcclusion.PlayOneShot(impactSource, clip, volume, point);

            if (debugAudio)
                Debug.Log($"[ImpactAudio] {name} hit '{collision.collider.name}' at {speed:0.00} m/s → '{clip.name}' vol {volume:0.00} (force {force:0.00})");
        }
    }
}
