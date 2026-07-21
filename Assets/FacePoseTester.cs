using UnityEngine;

[ExecuteAlways]
public class JawSineAnimation : MonoBehaviour
{
    public Transform jaw;
    public Transform eyeBrow;

    [Header("Rotation")]
    public float jawMin = 90f;
    public float jawMax = 155f;
    public float eyeBrowMin = 30f;
    public float eyeBrowMax = 90f;

    [Header("Animation")]
    public float jawSpeed = 0.5f;
    public float eyeBrowSpeed = 0.5f;

    // Sum of the sine amplitudes below (0.15 + 0.05 + 0.02).
    // Used to normalize the combined wave back into a 0-1 range.
    private const float WaveAmplitudeSum = 0.15f + 0.05f + 0.02f;

    private Quaternion jawBaseRotation;
    private Quaternion eyeBrowBaseRotation;
    private bool initialized;

    private void OnEnable()
    {
        CaptureBaseRotations();
    }

    private void LateUpdate()
    {
        if (jaw == null || eyeBrow == null)
            return;

        if (!initialized)
            CaptureBaseRotations();

        float time;
        if (Application.isPlaying)
        {
            time = Time.time;
        }
        else
        {
#if UNITY_EDITOR
            time = (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
            time = 0f;
#endif
        }

        float jawWave =
            Mathf.Sin(time * jawSpeed * 0.8f) * 0.15f
            + Mathf.Sin(time * jawSpeed * 2.3f) * 0.05f
            + Mathf.Sin(time * jawSpeed * 5.7f) * 0.02f;

        float eyeBrowWave =
            Mathf.Sin(time * eyeBrowSpeed * 0.5f) * 0.15f
            + Mathf.Sin(time * eyeBrowSpeed * 2.1f) * 0.05f
            + Mathf.Sin(time * eyeBrowSpeed * 5.3f) * 0.02f;

        // Normalize from [-amplitudeSum, +amplitudeSum] into [0, 1].
        float jawValue = 0.5f + (jawWave / WaveAmplitudeSum) * 0.5f;
        float eyeBrowValue = 0.5f + (eyeBrowWave / WaveAmplitudeSum) * 0.5f;

        // Safety clamp in case amplitudes are tuned to no longer sum correctly.
        jawValue = Mathf.Clamp01(jawValue);
        eyeBrowValue = Mathf.Clamp01(eyeBrowValue);

        float jawAngle = Mathf.Lerp(jawMin, jawMax, jawValue);
        float eyeBrowAngle = Mathf.Lerp(eyeBrowMin, eyeBrowMax, eyeBrowValue);

        jaw.localRotation =
            Quaternion.Euler(jawAngle, 0f, 0f);

        eyeBrow.localRotation =
            Quaternion.Euler(eyeBrowAngle, 0f, 0f);
    }

    [ContextMenu("Capture Current Rotations As Base")]
    private void CaptureBaseRotations()
    {
        if (jaw != null)
            jawBaseRotation = jaw.localRotation;
        if (eyeBrow != null)
            eyeBrowBaseRotation = eyeBrow.localRotation;

        initialized = jaw != null && eyeBrow != null;
    }
}