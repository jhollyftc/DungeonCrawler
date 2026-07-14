using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Sound for a PhysicsDoor. A physics door has no "open"/"close" commands to
    /// hook clips onto — it just gets shoved — so audio is driven by the door's
    /// own physical state and events.
    ///
    /// TWO KINDS OF SOUND, TWO MECHANISMS — this distinction matters:
    ///   - CREAK is continuous. It must last exactly as long as the door is
    ///     actually swinging and die when it stops, so it's a LOOPING source
    ///     whose volume/pitch track live swing speed. (PlayOneShot would play
    ///     the whole clip regardless of what the door did — a long creak would
    ///     keep groaning after the door had already stopped.)
    ///   - THUNK / SLAM are impacts: instantaneous, and the clip should ring out
    ///     to its natural end. Those are PlayOneShot on a separate source.
    ///
    /// The payoff over a scripted door: FORCE IS AUDIBLE FOR FREE. Every impact
    /// carries its speed, so a gentle nudge is quiet and a sprint slams — no
    /// special-casing, it falls out of the physics.
    ///
    /// Turn on PhysicsDoor.logSwingTelemetry to get real numbers for tuning.
    /// </summary>
    [RequireComponent(typeof(PhysicsDoor))]
    public class PhysicsDoorAudio : MonoBehaviour
    {
        [Header("Sources (both should be 3D — Spatial Blend = 1)")]
        [Tooltip("Looping creak. Its clip is set below; volume/pitch are driven by swing speed.")]
        [SerializeField] private AudioSource creakSource;
        [Tooltip("One-shot impacts (thunk / slam).")]
        [SerializeField] private AudioSource impactSource;

        [Header("Clips")]
        [Tooltip("A LOOPING creak/groan. Played continuously while the door swings, silent at rest.")]
        [SerializeField] private AudioClip creakLoop;
        [Tooltip("Thunk as the door hits the closed stop. THE key sound. Several = free variation.")]
        [SerializeField] private AudioClip[] closeClips;
        [Tooltip("Bang when the door slams wide open against its stop.")]
        [SerializeField] private AudioClip[] slamClips;

        // IMPACTS and the CREAK run on completely different speed scales, so they
        // need their own reference speeds. Telemetry shows why: a swing PEAKS at
        // ~2.4-4.0 rad/s (that's what the creak hears), but the door only hits the
        // closed stop at ~1.2-1.5 rad/s (that's what the thunk hears). Driving
        // both from one number made every thunk compute ~0.37 force and play at a
        // flat ~45% volume with no variation.
        [Header("Impacts (thunk / slam) — from telemetry 'closeImpact'")]
        [Tooltip("Impact speed (rad/s) that counts as FULL FORCE. Set from the telemetry's closeImpact on your HARDEST swing — NOT peakSpeed.")]
        [SerializeField] private float impactFullSpeed = 1.6f;
        [Tooltip("Impacts slower than this (rad/s) are silent — stops the door ticking as it settles. Set just under your gentlest deliberate closeImpact.")]
        [SerializeField] private float impactSilentBelowSpeed = 0.4f;
        [Tooltip("Quietest an audible impact can be.")]
        [Range(0f, 1f)][SerializeField] private float minimumVolume = 0.2f;

        [Header("Creak — from telemetry 'peakSpeed'")]
        [Tooltip("Swing speed (rad/s) at which the creak is at full volume/pitch. Set from the telemetry's peakSpeed on your hardest swing.")]
        [SerializeField] private float creakFullSpeed = 4f;
        [Tooltip("Swing speed (rad/s) below which the creak is fully silent.")]
        [SerializeField] private float creakSilentBelowSpeed = 0.3f;
        [Range(0f, 1f)][SerializeField] private float creakMaxVolume = 0.7f;
        [Tooltip("How fast the creak fades in/out (volume per second). Keeps it from popping on/off.")]
        [SerializeField] private float creakFadeSpeed = 4f;
        [Tooltip("Creak pitch at rest → at full speed. A faster swing groans higher.")]
        [SerializeField] private Vector2 creakPitchRange = new Vector2(0.85f, 1.15f);

        [Header("Impact variation")]
        [Tooltip("Random pitch spread so repeated swings don't sound identical.")]
        [SerializeField] private Vector2 impactPitchRange = new Vector2(0.95f, 1.05f);

        [Tooltip("Log every clip actually played (which slot, which clip, what volume). Pair with PhysicsDoor.logSwingTelemetry to prove the sound fires at the same instant as the THUNK event.")]
        [SerializeField] private bool debugAudio = false;

        private PhysicsDoor door;

        private void Awake()
        {
            door = GetComponent<PhysicsDoor>();

            if (creakSource != null)
            {
                creakSource.clip = creakLoop;
                creakSource.loop = true;
                creakSource.playOnAwake = false;
                creakSource.volume = 0f;
            }
            if (impactSource != null)
                impactSource.playOnAwake = false;
        }

        private void OnEnable()
        {
            door.OnClosed += HandleClosed;
            door.OnSlamOpen += HandleSlamOpen;
        }

        private void OnDisable()
        {
            door.OnClosed -= HandleClosed;
            door.OnSlamOpen -= HandleSlamOpen;
        }

        /// <summary>
        /// Maps a speed onto 0..1, measured FROM the silence floor rather than
        /// from zero. The usable band is narrow (the spring returns the door at a
        /// similar speed however hard you shoved it — telemetry shows 1.19-1.51),
        /// so normalising from 0 would squash every impact into the same volume.
        /// Measuring from the floor spreads that band across the full range.
        /// </summary>
        private static float Force(float speed, float floor, float full) =>
            Mathf.Clamp01((speed - floor) / Mathf.Max(0.01f, full - floor));

        private void Update()
        {
            if (creakSource == null || creakLoop == null) return;

            // Creak tracks the door's LIVE speed, so it stops when the door does.
            float speed = door.SwingSpeed;
            float force = Force(speed, creakSilentBelowSpeed, creakFullSpeed);
            float target = speed < creakSilentBelowSpeed ? 0f : creakMaxVolume * force;

            creakSource.volume = Mathf.MoveTowards(creakSource.volume, target, creakFadeSpeed * Time.deltaTime);
            creakSource.pitch = Mathf.Lerp(creakPitchRange.x, creakPitchRange.y, force);

            if (creakSource.volume > 0.001f && !creakSource.isPlaying) creakSource.Play();
            else if (creakSource.volume <= 0.001f && creakSource.isPlaying) creakSource.Stop();
        }

        private void HandleClosed(float speed) => PlayImpact(closeClips, speed, "CLOSE");
        private void HandleSlamOpen(float speed) => PlayImpact(slamClips, speed, "SLAM");

        private void PlayImpact(AudioClip[] clips, float speed, string which)
        {
            if (impactSource == null)
            {
                if (debugAudio) Debug.LogWarning($"[PhysicsDoorAudio] {which} fired but impactSource is NOT ASSIGNED — nothing will play.");
                return;
            }
            if (clips == null || clips.Length == 0)
            {
                if (debugAudio) Debug.LogWarning($"[PhysicsDoorAudio] {which} fired but no clips are assigned in that slot.");
                return;
            }
            if (speed < impactSilentBelowSpeed)
            {
                if (debugAudio) Debug.Log($"[PhysicsDoorAudio] {which} fired but muted — impact {speed:0.00} < impactSilentBelowSpeed {impactSilentBelowSpeed}");
                return;
            }

            AudioClip clip = clips[Random.Range(0, clips.Length)];
            if (clip == null) return;

            // Impact speed drives loudness — a shove and a slam sound different
            // with no extra logic.
            float volume = Mathf.Lerp(minimumVolume, 1f, Force(speed, impactSilentBelowSpeed, impactFullSpeed));

            impactSource.pitch = Random.Range(impactPitchRange.x, impactPitchRange.y);
            impactSource.PlayOneShot(clip, volume);

            if (debugAudio)
                Debug.Log($"[PhysicsDoorAudio] {which} → PLAYING '{clip.name}' ({clip.length:0.00}s) at vol {volume:0.00}");
        }
    }
}
