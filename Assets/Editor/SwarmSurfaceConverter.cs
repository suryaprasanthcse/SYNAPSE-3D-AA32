using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Converts every mesh in the active scene (e.g. the Mystery Cave diorama) to MAAYAI/SwarmSurface so it can only be
/// seen where the swarm's VPLs light it. Originals are never modified: converted copies are written to
/// Assets/Funobotz/Materials/SwarmSurface/ and assigned with Undo. Albedo is floored to the authorised 0.25-0.4
/// band (near-black PBR reflects nothing when the swarm is the only light).
/// </summary>
public static class SwarmSurfaceConverter
{
    const string SurfaceShaderName = "MAAYAI/SwarmSurface";
    const string OutputFolder = "Assets/Funobotz/Materials/SwarmSurface";
    const float AlbedoFloor = 0.25f;
    const float AlbedoTarget = 0.3f;

    static readonly int ID_BaseMap = Shader.PropertyToID("_BaseMap");
    static readonly int ID_MainTex = Shader.PropertyToID("_MainTex");
    static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ID_Color = Shader.PropertyToID("_Color");
    static readonly int ID_AlphaClip = Shader.PropertyToID("_AlphaClip");
    static readonly int ID_Cutoff = Shader.PropertyToID("_Cutoff");
    static readonly int ID_Surface = Shader.PropertyToID("_Surface");
    static readonly int ID_Smoothness = Shader.PropertyToID("_Smoothness");
    static readonly int ID_Cull = Shader.PropertyToID("_Cull");

    [MenuItem("Funobotz/Convert Scene To SwarmSurface")]
    public static void ConvertActiveScene()
    {
        var shader = Shader.Find(SurfaceShaderName);
        if (shader == null)
        {
            Debug.LogError($"[SwarmSurfaceConverter] Shader '{SurfaceShaderName}' not found.");
            return;
        }

        Directory.CreateDirectory(OutputFolder);
        AssetDatabase.Refresh();

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Convert Scene To SwarmSurface");

        Scene scene = SceneManager.GetActiveScene();
        var cache = new Dictionary<Material, Material>();
        int renderers = 0, floored = 0;

        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
        {
            if (renderer.gameObject.scene != scene) continue;
            if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;

            var source = renderer.sharedMaterials;
            var replacement = new Material[source.Length];
            bool changed = false;

            for (int i = 0; i < source.Length; i++)
            {
                Material src = source[i];
                replacement[i] = src;
                if (src == null || src.shader == shader || src.shader.name.StartsWith("MAAYAI/SwarmRender")) continue;

                if (!cache.TryGetValue(src, out var converted))
                {
                    converted = Convert(src, shader, out bool wasFloored);
                    if (wasFloored) floored++;
                    cache[src] = converted;
                }

                replacement[i] = converted;
                changed = true;
            }

            if (!changed) continue;
            Undo.RecordObject(renderer, "Convert Scene To SwarmSurface");
            renderer.sharedMaterials = replacement;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            EditorUtility.SetDirty(renderer);
            renderers++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);

        Debug.Log($"[SwarmSurfaceConverter] '{scene.name}': {renderers} renderers, {cache.Count} materials converted " +
                  $"({floored} albedo-floored to {AlbedoTarget}). Output: {OutputFolder}");
    }

    static Material Convert(Material src, Shader shader, out bool floored)
    {
        string path = $"{OutputFolder}/{Sanitize(src.name)}_SwarmSurface.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool isNew = material == null;
        if (isNew) material = new Material(shader);
        else material.shader = shader;

        int texId = src.HasProperty(ID_BaseMap) && src.GetTexture(ID_BaseMap) != null ? ID_BaseMap
                  : src.HasProperty(ID_MainTex) ? ID_MainTex
                  : -1;
        if (texId != -1)
        {
            material.SetTexture(ID_BaseMap, src.GetTexture(texId));
            material.SetTextureScale(ID_BaseMap, src.GetTextureScale(texId));
            material.SetTextureOffset(ID_BaseMap, src.GetTextureOffset(texId));
        }

        Color tint = src.HasProperty(ID_BaseColor) ? src.GetColor(ID_BaseColor)
                   : src.HasProperty(ID_Color) ? src.GetColor(ID_Color)
                   : Color.white;
        material.SetColor(ID_BaseColor, FloorAlbedo(tint, out floored));

        if (src.HasProperty(ID_Smoothness))
            material.SetFloat(ID_Smoothness, Mathf.Clamp01(src.GetFloat(ID_Smoothness)));

        bool clip = (src.HasProperty(ID_AlphaClip) && src.GetFloat(ID_AlphaClip) > 0.5f)
                 || (src.HasProperty(ID_Surface) && src.GetFloat(ID_Surface) > 0.5f)
                 || src.renderQueue >= (int)RenderQueue.AlphaTest;
        material.SetFloat(ID_AlphaClip, clip ? 1f : 0f);
        if (clip)
        {
            material.SetFloat(ID_Cutoff, src.HasProperty(ID_Cutoff) ? src.GetFloat(ID_Cutoff) : 0.5f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)RenderQueue.AlphaTest;
        }
        else
        {
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = -1;
        }

        material.SetFloat(ID_Cull, (float)CullMode.Back);

        if (isNew) AssetDatabase.CreateAsset(material, path);
        else EditorUtility.SetDirty(material);
        return material;
    }

    // Raise near-black tints into the reflective band, keeping hue. Brighter tints are left untouched.
    static Color FloorAlbedo(Color c, out bool floored)
    {
        float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        floored = lum < AlbedoFloor;
        if (!floored) return c;
        if (c.maxColorComponent < 0.02f) return new Color(AlbedoTarget, AlbedoTarget, AlbedoTarget, c.a);

        float k = AlbedoTarget / Mathf.Max(lum, 1e-4f);
        return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);
    }

    static string Sanitize(string name)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Replace(" (Instance)", string.Empty);
    }
}
