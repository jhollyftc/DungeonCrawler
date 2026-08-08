using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automatically configures materials generated during FBX import.
/// This file must be inside an Editor folder.
/// </summary>
public class AutomaticMaterialImporter : AssetPostprocessor
{
    // Change this to the exact name declared at the top of your shader.
    private const string ShaderName = "Dungeon/ToonLit";

    // Change these to match your shader's actual property names.
    private const string BaseColorProperty = "_BaseMap";
    private const string NormalProperty = "_BumpMap";
    private const string RoughMetalProperty = "_MaskMap";

    
    void OnPostprocessMaterial(Material material)
    {
        Shader customShader = Shader.Find(ShaderName);

        if (customShader == null)
        {
            Debug.LogWarning(
                $"AutomaticMaterialImporter could not find shader '{ShaderName}'.");
            return;
        }

        material.shader = customShader;

        string modelFolder =
            Path.GetDirectoryName(assetPath)?.Replace("\\", "/");

        if (string.IsNullOrEmpty(modelFolder))
            return;

        string materialName = CleanMaterialName(material.name);

        Texture2D baseColor = FindTexture(
            modelFolder,
            materialName,
            "_BaseColor",
            "_BaseColour",
            "_Albedo",
            "_Diffuse",
            "_Color");

        Texture2D normal = FindTexture(
            modelFolder,
            materialName,
            "_Normal",
            "_NormalGL",
            "_NormalDX",
            "_N");

        Texture2D roughMetal = FindTexture(
            modelFolder,
            materialName,
            "_RM",
            "_MR",
            "_MetallicRoughness",
            "_RoughnessMetallic");

        AssignTexture(
            material,
            BaseColorProperty,
            baseColor);

        AssignTexture(
            material,
            NormalProperty,
            normal);

        AssignTexture(
            material,
            RoughMetalProperty,
            roughMetal);

        EditorUtility.SetDirty(material);
    }

    void OnPreprocessTexture()
    {
        TextureImporter textureImporter =
            assetImporter as TextureImporter;

        if (textureImporter == null)
            return;

        string filename =
            Path.GetFileNameWithoutExtension(assetPath)
                .ToLowerInvariant();

        bool isNormalMap =
            filename.EndsWith("_normal") ||
            filename.EndsWith("_normalgl") ||
            filename.EndsWith("_normaldx") ||
            filename.EndsWith("_n");

        if (isNormalMap)
        {
            textureImporter.textureType =
                TextureImporterType.NormalMap;
        }
    }

    private static void AssignTexture(
        Material material,
        string propertyName,
        Texture texture)
    {
        if (texture == null)
            return;

        if (!material.HasProperty(propertyName))
        {
            Debug.LogWarning(
                $"Shader '{material.shader.name}' does not contain " +
                $"a property named '{propertyName}'.");
            return;
        }

        material.SetTexture(propertyName, texture);
    }

    private static Texture2D FindTexture(
        string folder,
        string materialName,
        params string[] suffixes)
    {
        string[] textureGuids = AssetDatabase.FindAssets(
            "t:Texture2D",
            new[] { folder });

        foreach (string suffix in suffixes)
        {
            string expectedName =
                NormalizeName(materialName + suffix);

            foreach (string guid in textureGuids)
            {
                string texturePath =
                    AssetDatabase.GUIDToAssetPath(guid);

                string textureName =
                    Path.GetFileNameWithoutExtension(texturePath);

                if (NormalizeName(textureName) == expectedName)
                {
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(
                        texturePath);
                }
            }
        }

        // Fallback for folders containing one asset and one texture set.
        foreach (string suffix in suffixes)
        {
            string normalizedSuffix = NormalizeName(suffix);

            foreach (string guid in textureGuids)
            {
                string texturePath =
                    AssetDatabase.GUIDToAssetPath(guid);

                string textureName = NormalizeName(
                    Path.GetFileNameWithoutExtension(texturePath));

                if (textureName.EndsWith(normalizedSuffix))
                {
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(
                        texturePath);
                }
            }
        }

        return null;
    }

    private static string CleanMaterialName(string materialName)
    {
        return materialName
            .Replace(" (Instance)", "")
            .Trim();
    }

    private static string NormalizeName(string value)
    {
        return value
            .Replace("_", "")
            .Replace("-", "")
            .Replace(" ", "")
            .ToLowerInvariant();
    }
}