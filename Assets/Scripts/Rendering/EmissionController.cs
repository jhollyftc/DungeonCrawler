using UnityEngine;

[ExecuteAlways]
public class EmissionController : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Renderer targetRenderer;

    [Tooltip("Second material slot is index 1.")]
    [SerializeField] private int materialIndex = 1;

    [Header("Emission")]
    [ColorUsage(true, true)]
    [SerializeField] private Color emissionColor = Color.white;

    [Min(0f)]
    [SerializeField] private float intensity = 1f;

    [Tooltip("Usually _EmissionColor for Unity URP Lit.")]
    [SerializeField] private string emissionProperty = "_EmissionColor";

    private MaterialPropertyBlock propertyBlock;
    private int emissionPropertyID;

    private void OnEnable()
    {
        ApplyEmission();
    }

    private void OnValidate()
    {
        ApplyEmission();
    }

    [ContextMenu("Apply Emission")]
    public void ApplyEmission()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        if (targetRenderer == null)
            return;

        // sharedMaterials is safe for checking prefab material slots.
        Material[] sharedMaterials = targetRenderer.sharedMaterials;

        if (materialIndex < 0 || materialIndex >= sharedMaterials.Length)
        {
            Debug.LogWarning(
                $"{name}: Material slot {materialIndex} does not exist.",
                this);

            return;
        }

        if (sharedMaterials[materialIndex] == null)
            return;

        propertyBlock ??= new MaterialPropertyBlock();

        emissionPropertyID = Shader.PropertyToID(emissionProperty);

        // Read any existing overrides for only the second material slot.
        targetRenderer.GetPropertyBlock(propertyBlock, materialIndex);

        propertyBlock.SetColor(
            emissionPropertyID,
            emissionColor * intensity);

        // Apply only to the second material slot.
        targetRenderer.SetPropertyBlock(propertyBlock, materialIndex);
    }

    public void SetEmission(Color color, float newIntensity)
    {
        emissionColor = color;
        intensity = newIntensity;
        ApplyEmission();
    }

    public void SetColor(Color color)
    {
        emissionColor = color;
        ApplyEmission();
    }

    public void SetIntensity(float newIntensity)
    {
        intensity = Mathf.Max(0f, newIntensity);
        ApplyEmission();
    }

    [ContextMenu("Clear Emission Override")]
    public void ClearEmissionOverride()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        if (targetRenderer != null)
            targetRenderer.SetPropertyBlock(null, materialIndex);
    }
}