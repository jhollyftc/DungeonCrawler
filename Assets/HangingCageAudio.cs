using UnityEngine;

namespace DungeonGen
{
    /// <summary>
    /// Chain-squeak for a hanging cage/chandelier: a LOOPING creak whose volume/
    /// pitch track how fast the whole assembly is swinging, silent at rest. Same
    /// shape as PhysicsDoorAudio's creak (see there for why looping + live-tracking
    /// beats PlayOneShot for a continuous sound), but self-contained — a hanging
    /// cage has no "door" script with an authored SwingSpeed, so this reads the
    /// Rigidbody's own angular velocity directly.
    ///
    /// Put this on the OUTERMOST body of the chain (the cage itself, not a link):
    /// a hinge-chained Rigidbody's angular velocity is measured in WORLD space, so
    /// each link's spin adds onto the one above it — the cage's own angularVelocity
    /// already reflects the combined swing of every link between it and the
    /// ceiling anchor, with no need to sample the whole chain.
    ///
    /// Pair with ImpactAudio (already used project-wide for prop bumps) on the
    /// same body for a metallic clang when the player walks into it — that's a
    /// one-shot gated by collision speed, a different mechanism from this
    /// continuous creak, the same split PhysicsDoorAudio makes between its thunk
    /// and its creak.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class HangingCageAudio : MonoBehaviour
    {
        [Header("Source (should be 3D — Spatial Blend = 1)")]
        [Tooltip("Looping creak/squeak. Left empty, a 3D source is added at Awake so this can be dropped straight onto the cage.")]
        [SerializeField] private AudioSource creakSource;

        [Header("Clip")]
        [SerializeField] private AudioClip creakLoop;

        [Header("Swing speed (rad/s) → loudness")]
        [Tooltip("Angular speed that counts as FULL FORCE. Set from a hard shove — enable debugAudio and read the logged speed.")]
        [SerializeField] private float fullSpeed = 2.5f;
        [Tooltip("Below this angular speed the creak is fully silent — stops it ticking as the swing settles to rest.")]
        [SerializeField] private float silentBelowSpeed = 0.15f;
        [Range(0f, 1f)][SerializeField] private float maxVolume = 0.7f;
        [Tooltip("How fast the creak fades in/out (volume per second). Keeps it from popping on/off.")]
        [SerializeField] private float fadeSpeed = 4f;
        [Tooltip("Creak pitch at the silence floor → at full speed. A faster swing groans higher.")]
        [SerializeField] private Vector2 pitchRange = new Vector2(0.85f, 1.15f);

        [Tooltip("Log live speed/volume every frame — use it to read real numbers for fullSpeed/silentBelowSpeed off an actual push.")]
        [SerializeField] private bool debugAudio = false;

        private Rigidbody body;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();

            if (creakSource == null)
            {
                creakSource = gameObject.AddComponent<AudioSource>();
                creakSource.spatialBlend = 1f;   // 3D, or the cage is as loud from across the room as up close
                creakSource.rolloffMode = AudioRolloffMode.Linear;
                creakSource.maxDistance = 20f;
            }
            creakSource.clip = creakLoop;
            creakSource.loop = true;
            creakSource.playOnAwake = false;
            creakSource.volume = 0f;
        }

        private void Update()
        {
            if (creakLoop == null) return;

            // World-space angularVelocity of the outermost body already sums every
            // hinge above it in the chain — see the class doc.
            float speed = body.angularVelocity.magnitude;
            float force = Mathf.Clamp01((speed - silentBelowSpeed) / Mathf.Max(0.01f, fullSpeed - silentBelowSpeed));
            float target = speed < silentBelowSpeed ? 0f : maxVolume * force;

            creakSource.volume = Mathf.MoveTowards(creakSource.volume, target, fadeSpeed * Time.deltaTime);
            creakSource.pitch = Mathf.Lerp(pitchRange.x, pitchRange.y, force);

            if (creakSource.volume > 0.001f && !creakSource.isPlaying) creakSource.Play();
            else if (creakSource.volume <= 0.001f && creakSource.isPlaying) creakSource.Stop();

            if (debugAudio)
                Debug.Log($"[HangingCageAudio] {name} speed={speed:0.00} rad/s force={force:0.00} vol={creakSource.volume:0.00}");
        }
    }
}
