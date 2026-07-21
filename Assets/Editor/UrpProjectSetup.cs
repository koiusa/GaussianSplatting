#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

internal static class UrpProjectSetup
{
    private const string SettingsFolder = "Assets/Settings";
    private const string RendererPath = SettingsFolder + "/UniversalRenderer.asset";
    private const string PipelinePath = SettingsFolder + "/UniversalRenderPipelineAsset.asset";

    [InitializeOnLoadMethod]
    private static void ConfigureAfterPackageImport()
    {
        if (GraphicsSettings.defaultRenderPipeline == null)
            EditorApplication.delayCall += Configure;
    }

    [MenuItem("Tools/Gaussian Splatting/Configure URP")]
    public static void Configure()
    {
        if (!AssetDatabase.IsValidFolder(SettingsFolder))
            AssetDatabase.CreateFolder("Assets", "Settings");

        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        if (renderer == null)
        {
            renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(renderer, RendererPath);
        }

        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        if (pipeline == null)
        {
            pipeline = UniversalRenderPipelineAsset.Create(renderer);
            AssetDatabase.CreateAsset(pipeline, PipelinePath);
        }

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;
        EditorUtility.SetDirty(pipeline);
        AssetDatabase.SaveAssets();
        Debug.Log("Gaussian Splatting test project configured for URP.");
    }
}
#endif
