using System.Linq;
using MAAYAI.HackMatrix;
using MAAYAI.Swarm;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Turns HackMatrix_Agentic.unity from a copy of the game into the Text-to-Space demo: the three-act game and
/// everything that only served it is removed; the agent (bridge, compiler, field tether, console) is added; and
/// the build points at this scene. Idempotent - run it again and it converges to the same scene.
///
/// Kept: the AR rig (session, origin, trackers, anchors), the GPU particle field and its lighting, the bloom
/// volume, and the diagnostics HUD, because its A/B toggles are the performance half of the demo.
/// </summary>
public static class HackMatrixSceneWiring
{
    const string ScenePath = "Assets/Scenes/HackMatrix_Agentic.unity";

    // The game: director + its UI + dark lighting (re-added below on the agent), the plane-anchored world with
    // the acts and Petalo, the mission header, and two objects the builder had already retired.
    static readonly string[] GameRoots = { "Hybrid_Loop", "Luminara_World", "UI_Header", "MysteryCave_Anchor", "Lumenforge_Puzzle" };

    [MenuItem("Hack Matrix/Wire Demo Scene")]
    public static void Wire() => Wire(ScenePath);

    /// <summary>Wire any copy of the demo scene, make it the build's first scene, and return its agent object.</summary>
    public static GameObject Wire(string scenePath)
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[HackMatrix] Stop Play mode first.");
            return null;
        }

        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        int removed = 0;
        foreach (var name in GameRoots)
        {
            // Re-queried every pass: a cached root array still holds the objects destroyed on earlier passes.
            var go = scene.GetRootGameObjects().FirstOrDefault(g => g.name == name);
            if (go == null) continue;
            Object.DestroyImmediate(go);
            removed++;
        }

        // Screen-centre placement needs a raycast manager; the game never used one.
        var origin = Object.FindAnyObjectByType<XROrigin>();
        if (origin != null && origin.GetComponent<ARRaycastManager>() == null)
            origin.gameObject.AddComponent<ARRaycastManager>();

        // The agent.
        var agent = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "HackMatrix_Agent")
                    ?? new GameObject("HackMatrix_Agent");
        var bridge = GetOrAdd<AgenticLLM_Bridge>(agent);
        var compiler = GetOrAdd<GenerativeCompiler>(agent);
        var planner = GetOrAdd<SpatialPlannerAgent>(agent);
        var tether = GetOrAdd<SwarmLayoutTether>(agent);
        var console = GetOrAdd<SpatialCompilerConsole>(agent);
        var dark = GetOrAdd<DarkARLighting>(agent);

        var c = new SerializedObject(compiler);
        c.FindProperty("bridge").objectReferenceValue = bridge;
        c.FindProperty("planeManager").objectReferenceValue = Object.FindAnyObjectByType<ARPlaneManager>();
        c.FindProperty("raycastManager").objectReferenceValue = Object.FindAnyObjectByType<ARRaycastManager>();
        c.FindProperty("anchorManager").objectReferenceValue = Object.FindAnyObjectByType<ARAnchorManager>();
        c.FindProperty("viewCamera").objectReferenceValue = Camera.main;
        c.ApplyModifiedPropertiesWithoutUndo();

        var t = new SerializedObject(tether);
        t.FindProperty("compiler").objectReferenceValue = compiler;
        t.FindProperty("swarm").objectReferenceValue = Object.FindAnyObjectByType<SwarmGPUArchitect>();
        t.ApplyModifiedPropertiesWithoutUndo();

        var p = new SerializedObject(planner);
        p.FindProperty("bridge").objectReferenceValue = bridge;
        p.FindProperty("compiler").objectReferenceValue = compiler;
        p.ApplyModifiedPropertiesWithoutUndo();

        var ui = new SerializedObject(console);
        ui.FindProperty("bridge").objectReferenceValue = bridge;
        ui.FindProperty("agent").objectReferenceValue = planner;
        ui.FindProperty("compiler").objectReferenceValue = compiler;
        ui.ApplyModifiedPropertiesWithoutUndo();

        // Dark AR stays: the architecture is lit by the particle field's irradiance volume, which is exactly the
        // path the Lighting A/B toggle switches.
        var keyLight = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Key_Light");
        var d = new SerializedObject(dark);
        var lights = d.FindProperty("disabledLights");
        lights.arraySize = keyLight != null ? 1 : 0;
        if (keyLight != null) lights.GetArrayElementAtIndex(0).objectReferenceValue = keyLight.GetComponent<Light>();
        d.ApplyModifiedPropertiesWithoutUndo();

        // A real EventSystem in the scene, so the prompt field and buttons work from the first frame.
        if (Object.FindAnyObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        // The APK boots straight into the demo. The game scene stays listed, disabled, one tick away.
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(scenePath, true),
            new EditorBuildSettingsScene("Assets/Scenes/Phase5_TrojanHorse.unity", false)
        };

        // GPU frame time for the console readout: the A/B toggles move GPU milliseconds, not a 30 fps lock.
        PlayerSettings.enableFrameTimingStats = true;
        AssetDatabase.SaveAssets();

        Debug.Log($"[HackMatrix] Demo scene wired: {removed} game roots removed, agent + console + field tether " +
                  $"added, build scene set to {scenePath}, Frame Timing Stats on.");
        return agent;
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var existing = go.GetComponent<T>();
        return existing != null ? existing : go.AddComponent<T>();
    }
}
