using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Phase 2 Game Design Review captures, written to the project root:
/// <list type="bullet">
/// <item><c>Funobotz_Petalo_BeautyShot.png</c>: 4K offline render of Petalo's Aethyra diorama.</item>
/// <item><c>Funobotz_ARMatrix_Scene.png</c>: the live AR matrix scene in Play mode under XR Simulation.</item>
/// </list>
/// </summary>
public static class FunobotzBeautyShots
{
    public const string PetaloShotFile = "Funobotz_Petalo_BeautyShot.png";

    const string LogPrefix = "[FunobotzBeautyShots]";
    const int Width = 3840;
    const int Height = 2160;
    const float FieldOfView = 32f;
    const float PrewarmSeconds = 8f;
    const string PreferredQuality = "PC";   // full render scale and additional-light shadows

    public static string ProjectRoot => Directory.GetParent(Application.dataPath)!.FullName;

    [MenuItem("Funobotz/Capture GDD Review Shots", priority = 1)]
    public static void CaptureGddReviewMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            CaptureGddReview();
    }

    [MenuItem("Funobotz/Capture Petalo Beauty Shot", priority = 3)]
    public static void CaptureMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            Capture();
    }

    [MenuItem("Funobotz/Capture GDD Review Shots", validate = true)]
    [MenuItem("Funobotz/Capture Petalo Beauty Shot", validate = true)]
    static bool ValidateCapture() => !EditorApplication.isPlayingOrWillChangePlaymode;

    /// <summary>Petalo's render first (edit mode), then the AR scene (enters and exits Play mode).</summary>
    public static void CaptureGddReview()
    {
        if (Capture() != null)
            FunobotzArMatrixCapture.Begin();
    }

    /// <summary>Renders Petalo's diorama. Returns the written path, or null on failure.</summary>
    public static string Capture()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != FunobotzDioramaBuilder.ScenePath)
        {
            if (!File.Exists(FunobotzDioramaBuilder.ScenePath))
            {
                Debug.LogError($"{LogPrefix} {FunobotzDioramaBuilder.ScenePath} missing. Run Funobotz > Build Dioramas first.");
                return null;
            }

            scene = EditorSceneManager.OpenScene(FunobotzDioramaBuilder.ScenePath, OpenSceneMode.Single);
        }

        var diorama = scene.GetRootGameObjects()
            .FirstOrDefault(go => go.name == FunobotzDioramaBuilder.DioramaPrefix + FunobotzDioramaBuilder.FocalCharacter)
            ?.transform;
        var shot = diorama != null ? diorama.Find(FunobotzDioramaBuilder.ShotPointName) : null;
        if (shot == null)
        {
            Debug.LogError($"{LogPrefix} Diorama_{FunobotzDioramaBuilder.FocalCharacter}/{FunobotzDioramaBuilder.ShotPointName} not found. Run Funobotz > Build Dioramas.");
            return null;
        }

        var camera = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Camera>(true))
            .OrderByDescending(c => c.CompareTag("MainCamera"))
            .FirstOrDefault();
        if (camera == null)
        {
            Debug.LogError($"{LogPrefix} No camera in '{scene.name}'.");
            return null;
        }

        var file = Path.Combine(ProjectRoot, PetaloShotFile);
        var savedPosition = camera.transform.position;
        var savedRotation = camera.transform.rotation;
        var savedFov = camera.fieldOfView;
        var savedQuality = QualitySettings.GetQualityLevel();
        var savedAsync = ShaderUtil.allowAsyncCompilation;

        RenderTexture target = null;
        Texture2D readback = null;
        try
        {
            EditorUtility.DisplayProgressBar("Funobotz GDD Review", "Rendering Petalo's Aethyra diorama", 0.3f);

            // Async compilation would render placeholder shaders into the frame.
            ShaderUtil.allowAsyncCompilation = false;
            var quality = System.Array.IndexOf(QualitySettings.names, PreferredQuality);
            if (quality >= 0)
                QualitySettings.SetQualityLevel(quality, true);

            target = RenderTexture.GetTemporary(new RenderTextureDescriptor(Width, Height, RenderTextureFormat.ARGB32, 24)
            {
                sRGB = true,
                msaaSamples = 1,
            });
            readback = new Texture2D(Width, Height, TextureFormat.RGB24, false, false);

            camera.transform.SetPositionAndRotation(shot.position, shot.rotation);
            camera.fieldOfView = FieldOfView;
            camera.aspect = (float)Width / Height;

            foreach (var billboard in diorama.GetComponentsInChildren<FunobotzBillboard>(true))
                billboard.Face(shot.position);

            // Edit-mode particles don't advance on their own; fast-forward to a populated state.
            foreach (var ps in diorama.GetComponentsInChildren<ParticleSystem>(true))
                ps.Simulate(PrewarmSeconds, true, true, true);

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                sceneView.AlignViewToObject(shot);
                sceneView.Repaint();
            }

            // Warm-up pass: the first render after a build or quality switch can hit shader
            // variants that aren't loaded yet (error-magenta orbs, alpha clip ignored).
            Render(camera, target);
            Render(camera, target);

            var previous = RenderTexture.active;
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;

            File.WriteAllBytes(file, readback.EncodeToPNG());
        }
        finally
        {
            if (target != null)
                RenderTexture.ReleaseTemporary(target);
            if (readback != null)
                Object.DestroyImmediate(readback);

            camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
            camera.fieldOfView = savedFov;
            camera.ResetAspect();
            QualitySettings.SetQualityLevel(savedQuality, true);
            ShaderUtil.allowAsyncCompilation = savedAsync;
            EditorUtility.ClearProgressBar();
        }

        // Billboard and particle state are generated content; saving keeps the next scene switch prompt-free.
        if (scene.isDirty)
            EditorSceneManager.SaveScene(scene);

        Debug.Log($"{LogPrefix} Wrote {Width}x{Height} Petalo beauty shot: {file}");
        return file;
    }

    static void Render(Camera camera, RenderTexture target)
    {
        var request = new RenderPipeline.StandardRequest { destination = target };
        if (RenderPipeline.SupportsRenderRequest(camera, request))
        {
            RenderPipeline.SubmitRenderRequest(camera, request);
            return;
        }

        var previous = camera.targetTexture;
        camera.targetTexture = target;
        camera.Render();
        camera.targetTexture = previous;
    }
}

/// <summary>
/// Captures the AR matrix scene as it actually runs: opens the first enabled build scene, enters
/// Play mode so XR Simulation tracks the card and the payloads and swarm spawn, screenshots the
/// Game view, then exits Play mode and returns to the previous scene.
/// </summary>
[InitializeOnLoad]
public static class FunobotzArMatrixCapture
{
    public const string ArShotFile = "Funobotz_ARMatrix_Scene.png";

    const string LogPrefix = "[FunobotzArMatrixCapture]";
    const string PendingKey = "Funobotz.ARCapture.Pending";
    const string ReturnSceneKey = "Funobotz.ARCapture.ReturnScene";
    const double SettleSeconds = 10.0;    // XR Simulation start, image detection, swarm spin-up
    const double TimeoutSeconds = 40.0;
    const int TargetWidth = 3840;

    static double s_EnteredAt;
    static double s_CapturedAt;
    static string s_File;
    static bool s_Captured;

    // Play mode here runs without a domain reload, so this handler stays registered across entry;
    // SessionState covers configurations that do reload.
    static FunobotzArMatrixCapture() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

    [MenuItem("Funobotz/Capture AR Matrix Scene", priority = 4)]
    public static void Begin()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError($"{LogPrefix} Already in Play mode.");
            return;
        }

        var arScene = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled)?.path;
        if (string.IsNullOrEmpty(arScene) || !File.Exists(arScene))
        {
            Debug.LogError($"{LogPrefix} No enabled scene in Build Profiles to capture.");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        SessionState.SetString(ReturnSceneKey, SceneManager.GetActiveScene().path);
        if (SceneManager.GetActiveScene().path != arScene)
            EditorSceneManager.OpenScene(arScene, OpenSceneMode.Single);

        SessionState.SetBool(PendingKey, true);
        Debug.Log($"{LogPrefix} Entering Play mode in {arScene}; capturing after {SettleSeconds:0}s.");
        EditorApplication.EnterPlaymode();
    }

    static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (!SessionState.GetBool(PendingKey, false))
            return;

        switch (change)
        {
            case PlayModeStateChange.EnteredPlayMode:
                s_EnteredAt = EditorApplication.timeSinceStartup;
                s_Captured = false;
                s_File = Path.Combine(FunobotzBeautyShots.ProjectRoot, ArShotFile);
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;
                break;

            case PlayModeStateChange.EnteredEditMode:
                EditorApplication.update -= Tick;
                SessionState.EraseBool(PendingKey);
                var returnScene = SessionState.GetString(ReturnSceneKey, string.Empty);
                SessionState.EraseString(ReturnSceneKey);
                if (!string.IsNullOrEmpty(returnScene) && File.Exists(returnScene) && SceneManager.GetActiveScene().path != returnScene)
                    EditorSceneManager.OpenScene(returnScene, OpenSceneMode.Single);
                break;
        }
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying)
            return;

        var now = EditorApplication.timeSinceStartup;

        if (!s_Captured && now - s_EnteredAt >= SettleSeconds)
        {
            if (File.Exists(s_File))
                File.Delete(s_File);

            // Supersample the Game view up to 4K width; the camera's pixel width is the Game view's.
            var camera = Camera.main;
            var viewWidth = camera != null ? camera.pixelWidth : 1920;
            var superSize = Mathf.Clamp(Mathf.CeilToInt((float)TargetWidth / Mathf.Max(1, viewWidth)), 1, 4);
            ScreenCapture.CaptureScreenshot(s_File, superSize);
            s_Captured = true;
            s_CapturedAt = now;
            return;
        }

        // The screenshot is written at the end of a rendered frame; give the file a moment to land.
        var written = s_Captured && File.Exists(s_File) && now - s_CapturedAt > 1.5;
        var timedOut = now - s_EnteredAt > TimeoutSeconds;
        if (!written && !timedOut)
            return;

        EditorApplication.update -= Tick;
        if (written)
            Debug.Log($"{LogPrefix} Wrote AR matrix scene capture: {s_File}");
        else
            Debug.LogError($"{LogPrefix} Timed out waiting for {ArShotFile}. Keep the Game view open and visible, then run Funobotz > Capture AR Matrix Scene.");

        EditorApplication.ExitPlaymode();
    }
}

/// <summary>
/// One-shot trigger: if Library/Funobotz.autorun exists when scripts reload, runs the GDD cleanup
/// and the review captures once, then deletes the flag.
/// </summary>
[InitializeOnLoad]
static class FunobotzAutoRun
{
    const string Flag = "Library/Funobotz.autorun";

    static FunobotzAutoRun()
    {
        if (!File.Exists(Flag))
            return;

        File.Delete(Flag);
        EditorApplication.delayCall += Run;
    }

    static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[FunobotzAutoRun] Skipped: Play mode is active. Use Funobotz > GDD Cleanup, then Funobotz > Capture GDD Review Shots.");
            return;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Run;
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        if (FunobotzGddCleanup.Cleanup())
            FunobotzBeautyShots.CaptureGddReview();
    }
}
