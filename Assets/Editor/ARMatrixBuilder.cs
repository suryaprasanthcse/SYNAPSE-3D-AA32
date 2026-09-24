using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using Debug = UnityEngine.Debug;

/// <summary>
/// One-click Android ARCore configuration: player settings, XR loader, URP AR background,
/// scene rig, git commit, and APK build.
/// </summary>
public static class ARMatrixBuilder
{
    const string k_LogPrefix = "[ARMatrixBuilder]";
    const string k_ARCoreLoaderTypeName = "UnityEngine.XR.ARCore.ARCoreLoader";
    const string k_XRGeneralSettingsPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
    const string k_CommitMessage = "CHORE: Automated Android AR Build Configuration and Scene Rig";
    const string k_BuildOutputPath = "Builds/Android/MAAYAI_Matrix_AR.apk";
    const AndroidSdkVersions k_MinSdk = (AndroidSdkVersions)27; // Android 8.1

    [MenuItem("Tools/Configure and Build AR Matrix")]
    public static void ConfigureAndBuild()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError($"{k_LogPrefix} Exit Play mode before running.");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        try
        {
            Progress("Switching build target to Android", 0.05f);
            ConfigurePlatform();

            Progress("Forcing OpenGLES3 graphics API", 0.20f);
            ConfigureGraphics();

            Progress("Enabling ARCore XR loader", 0.30f);
            EnableARCoreLoader();

            Progress("Injecting AR Background renderer feature", 0.40f);
            EnsureARBackgroundRendererFeature();
            EnsureAlwaysIncludedShader("Universal Render Pipeline/Unlit");

            Progress("Building AR scene rig", 0.50f);
            var scenePath = BuildSceneRig();

            AssetDatabase.SaveAssets();

            Progress("Committing to git", 0.60f);
            GitCommit(k_CommitMessage);

            Progress("Building Android player", 0.70f);
            BuildAndroid(scenePath);
        }
        catch (Exception e)
        {
            Debug.LogError($"{k_LogPrefix} Aborted: {e}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static void Progress(string step, float t)
    {
        Debug.Log($"{k_LogPrefix} {step}");
        EditorUtility.DisplayProgressBar("AR Matrix Builder", step, t);
    }

    // 1. Platform & architecture
    static void ConfigurePlatform()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
            !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            throw new InvalidOperationException("Failed to switch to Android. Is the Android Build Support module installed?");

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = k_MinSdk;
        EditorUserBuildSettings.buildAppBundle = false;
    }

    // 2. Graphics API: GLES3 only. Vulkan removed to avoid black AR camera feed.
    static void ConfigureGraphics()
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
        if (apis.Length != 1 || apis[0] != GraphicsDeviceType.OpenGLES3)
            throw new InvalidOperationException($"Graphics API override did not stick: {string.Join(", ", apis)}");
    }

    // 3. XR Plug-in Management: ARCore loader for Android
    static void EnableARCoreLoader()
    {
        const BuildTargetGroup group = BuildTargetGroup.Android;

        var perTarget = GetOrCreateXRGeneralSettings();
        if (!perTarget.HasManagerSettingsForBuildTarget(group))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

        var generalSettings = perTarget.SettingsForBuildTarget(group);
        generalSettings.InitManagerOnStart = true;

        var manager = generalSettings.Manager;
        var alreadyActive = manager.activeLoaders.Any(l => l != null && l.GetType().FullName == k_ARCoreLoaderTypeName);
        if (!alreadyActive && !XRPackageMetadataStore.AssignLoader(manager, k_ARCoreLoaderTypeName, group))
            throw new InvalidOperationException("Failed to assign ARCoreLoader. Is com.unity.xr.arcore installed?");

        EditorUtility.SetDirty(generalSettings);
        EditorUtility.SetDirty(manager);
        EditorUtility.SetDirty(perTarget);
    }

    static XRGeneralSettingsPerBuildTarget GetOrCreateXRGeneralSettings()
    {
        if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget settings) && settings != null)
            return settings;

        var guid = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget").FirstOrDefault();
        if (guid != null)
            settings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(guid));

        if (settings == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(k_XRGeneralSettingsPath));
            settings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(settings, k_XRGeneralSettingsPath);
            AssetDatabase.SaveAssets();
        }

        EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, settings, true);
        return settings;
    }

    // URP renders nothing behind the scene unless the AR background feature is on the renderer.
    static void EnsureARBackgroundRendererFeature()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
            if (data == null || data.rendererFeatures.Any(f => f is ARBackgroundRendererFeature))
                continue;

            var feature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
            feature.name = nameof(ARBackgroundRendererFeature);
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            data.rendererFeatures.Add(feature);
            data.SetDirty();
            EditorUtility.SetDirty(data);

            var so = new SerializedObject(data);
            so.Update();
            var map = so.FindProperty("m_RendererFeatureMap");
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"{k_LogPrefix} Added ARBackgroundRendererFeature to {path}");
        }
    }

    // Rebuilding the rig drops the old manager, so the library is re-assigned every run.
    static void AssignReferenceLibrary(ARTrackedImageManager imageManager)
    {
        var guid = AssetDatabase.FindAssets("t:XRReferenceImageLibrary", new[] { "Assets" }).FirstOrDefault();
        if (guid == null)
        {
            Debug.LogWarning($"{k_LogPrefix} No XRReferenceImageLibrary found in Assets. Image tracking will not start until one is assigned.", imageManager);
            return;
        }

        var path = AssetDatabase.GUIDToAssetPath(guid);
        imageManager.referenceLibrary = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(path);
        EditorUtility.SetDirty(imageManager);
        Debug.Log($"{k_LogPrefix} Assigned reference image library: {path}");
    }

    // Shader.Find fails at runtime for shaders no scene material references, so URP/Unlit is pinned.
    static void EnsureAlwaysIncludedShader(string shaderName)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogWarning($"{k_LogPrefix} Shader '{shaderName}' not found; skipping Always Included registration.");
            return;
        }

        var graphicsSettings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
        if (graphicsSettings == null)
        {
            Debug.LogWarning($"{k_LogPrefix} Could not open GraphicsSettings.asset; add '{shaderName}' to Always Included Shaders manually.");
            return;
        }

        var so = new SerializedObject(graphicsSettings);
        var included = so.FindProperty("m_AlwaysIncludedShaders");
        for (var i = 0; i < included.arraySize; i++)
        {
            if (included.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                return;
        }

        included.arraySize++;
        included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log($"{k_LogPrefix} Added '{shaderName}' to Always Included Shaders.");
    }

    // 4. Scene automation
    static string BuildSceneRig()
    {
        var scene = SceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.path))
        {
            var buildScene = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
            if (buildScene == null)
                throw new InvalidOperationException("No saved active scene and no enabled scene in Build Settings.");
            scene = EditorSceneManager.OpenScene(buildScene.path, OpenSceneMode.Single);
        }

        var roots = scene.GetRootGameObjects();

        // Purge the default camera and any rig from a previous run so the tool is idempotent.
        var doomed = roots
            .SelectMany(r => r.GetComponentsInChildren<Camera>(true).Select(c => c.gameObject))
            .Where(go => go.CompareTag("MainCamera") || go.name == "Main Camera")
            .Select(go => go.GetComponentInParent<XROrigin>(true) is XROrigin o ? o.gameObject : go)
            .Concat(roots.SelectMany(r => r.GetComponentsInChildren<XROrigin>(true)).Select(c => c.gameObject))
            .Concat(roots.SelectMany(r => r.GetComponentsInChildren<ARSession>(true)).Select(c => c.gameObject))
            .Distinct()
            .ToList();

        foreach (var go in doomed)
        {
            if (go == null) continue; // already destroyed as a child of an earlier entry
            Debug.Log($"{k_LogPrefix} Destroying '{go.name}'");
            Undo.DestroyObjectImmediate(go);
        }

        ObjectFactory.CreateGameObject("AR Session", typeof(ARSession), typeof(ARInputManager));
        CreateXROrigin();

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new InvalidOperationException($"Failed to save scene {scene.path}");

        var scenes = EditorBuildSettings.scenes.ToList();
        if (!scenes.Any(s => s.path == scene.path))
        {
            scenes.Insert(0, new EditorBuildSettingsScene(scene.path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        return scene.path;
    }

    // Mirrors AR Foundation's GameObject > XR > XR Origin (Mobile AR), plus image tracking.
    static void CreateXROrigin()
    {
        var originGo = ObjectFactory.CreateGameObject("XR Origin (Mobile AR)", typeof(XROrigin), typeof(ARTrackedImageManager));

        var offsetGo = ObjectFactory.CreateGameObject("Camera Offset");
        offsetGo.transform.SetParent(originGo.transform, false);

        var cameraGo = ObjectFactory.CreateGameObject(
            "Main Camera",
            typeof(Camera),
            typeof(AudioListener),
            typeof(ARCameraManager),
            typeof(ARCameraBackground),
            typeof(TrackedPoseDriver));
        cameraGo.transform.SetParent(offsetGo.transform, false);
        cameraGo.tag = "MainCamera";

        var camera = cameraGo.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Color;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 20f;

        var positionAction = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
        positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");
        var rotationAction = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
        rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");

        var poseDriver = cameraGo.GetComponent<TrackedPoseDriver>();
        poseDriver.positionInput = new InputActionProperty(positionAction);
        poseDriver.rotationInput = new InputActionProperty(rotationAction);

        var origin = originGo.GetComponent<XROrigin>();
        origin.CameraFloorOffsetObject = offsetGo;
        origin.Camera = camera;

        var imageManager = originGo.GetComponent<ARTrackedImageManager>();
        AssignReferenceLibrary(imageManager);

        // Arm the procedural payload so the rig spawns holograms without manual wiring.
        originGo.AddComponent<ARMatrixPayload>();

        Undo.RegisterCreatedObjectUndo(originGo, "Create XR Origin (Mobile AR)");
    }

    // Version control
    static void GitCommit(string message)
    {
        var root = Directory.GetParent(Application.dataPath)!.FullName;

        // Explicit paths only: "add -A" sweeps in Unity's IL2CPP/gradle scratch dirs.
        RunGit(root, "add -A -- Assets ProjectSettings Packages .gitignore", out _);
        if (RunGit(root, "diff --cached --quiet", out _) == 0)
        {
            Debug.Log($"{k_LogPrefix} Git: nothing to commit.");
            return;
        }

        if (RunGit(root, $"commit -m \"{message.Replace("\"", "\\\"")}\"", out var output) != 0)
            throw new InvalidOperationException($"git commit failed:\n{output}");

        Debug.Log($"{k_LogPrefix} Git commit created:\n{output}");
    }

    static int RunGit(string workingDirectory, string args, out string output)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            output = stdout.Result + stderr.Result;
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("git executable not found on PATH.", e);
        }
    }

    // Build
    static void BuildAndroid(string primaryScene)
    {
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .Prepend(primaryScene)
            .Distinct()
            .ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(k_BuildOutputPath));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = k_BuildOutputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        });

        var summary = report.summary;
        if (summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException($"Build {summary.result}: {summary.totalErrors} error(s). See Console.");

        Debug.Log($"{k_LogPrefix} Build succeeded: {Path.GetFullPath(k_BuildOutputPath)} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime})");
        EditorUtility.RevealInFinder(k_BuildOutputPath);
    }
}
