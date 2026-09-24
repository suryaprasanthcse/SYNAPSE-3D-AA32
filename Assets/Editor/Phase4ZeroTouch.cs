using System.Linq;
using MAAYAI.Swarm;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;

public static class Phase4ZeroTouch
{
    const string MaterialPath = "Assets/Anomaly_Material.mat";
    const string BloomProfilePath = "Assets/Anomaly_BloomProfile.asset";
    const string ComputePath = "Assets/BoidSwarmEngine/CurlSwarm/SwarmCompute.compute";
    const string SwarmShaderName = "MAAYAI/SwarmRender";

    static readonly Color HdrCyan = new Color(0f, 1f, 1f, 1f) * 8f;
    static readonly Color HdrCyanHot = new Color(0.6f, 1f, 1f, 1f) * 16f;

    [MenuItem("Funobotz/Deploy Phase 4 Architecture")]
    public static void Deploy()
    {
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Deploy Phase 4 Architecture");

        var mainCamera = EnsureARCamera();
        if (mainCamera != null) EnablePostProcessing(mainCamera);
        InjectBloomVolume();
        InjectAnomalyCore();

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        Debug.Log("[Phase4ZeroTouch] Phase 4 architecture deployed.");
    }

    // 1. AR Rig Check
    static Camera EnsureARCamera()
    {
        var origin = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include).FirstOrDefault();
        if (origin == null)
        {
            Debug.LogError("[Phase4ZeroTouch] No XR Origin found in the active scene. Skipping AR rig and post-processing steps.");
            return null;
        }

        Transform offset = origin.CameraFloorOffsetObject != null
            ? origin.CameraFloorOffsetObject.transform
            : origin.transform.Find("Camera Offset");

        if (offset == null)
        {
            var offsetGo = new GameObject("Camera Offset");
            Undo.RegisterCreatedObjectUndo(offsetGo, "Create Camera Offset");
            offsetGo.transform.SetParent(origin.transform, false);
            Undo.RecordObject(origin, "Assign Camera Offset");
            origin.CameraFloorOffsetObject = offsetGo;
            offset = offsetGo.transform;
        }

        var existing = offset.GetComponentsInChildren<Camera>(true)
            .FirstOrDefault(c => c.name == "Main Camera" || c.CompareTag("MainCamera"));
        if (existing != null)
        {
            Undo.RecordObject(origin, "Assign XR Camera");
            origin.Camera = existing;
            return existing;
        }

        var cameraGo = new GameObject("Main Camera",
            typeof(Camera),
            typeof(AudioListener),
            typeof(ARCameraManager),
            typeof(ARCameraBackground),
            typeof(TrackedPoseDriver));
        Undo.RegisterCreatedObjectUndo(cameraGo, "Create AR Main Camera");
        cameraGo.transform.SetParent(offset, false);
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

        Undo.RecordObject(origin, "Assign XR Camera");
        origin.Camera = camera;
        return camera;
    }

    // 2. Post-Processing
    static void EnablePostProcessing(Camera camera)
    {
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        if (data == null) data = Undo.AddComponent<UniversalAdditionalCameraData>(camera.gameObject);

        Undo.RecordObject(data, "Enable Post Processing");
        data.renderPostProcessing = true;
        EditorUtility.SetDirty(data);
    }

    // 3. Optical Injector
    static void InjectBloomVolume()
    {
        var volumeGo = GameObject.Find("Global_Bloom_Volume");
        if (volumeGo == null)
        {
            volumeGo = new GameObject("Global_Bloom_Volume");
            Undo.RegisterCreatedObjectUndo(volumeGo, "Create Global Bloom Volume");
        }

        var volume = volumeGo.GetComponent<Volume>();
        if (volume == null) volume = Undo.AddComponent<Volume>(volumeGo);

        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(BloomProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, BloomProfilePath);
        }

        if (!profile.TryGet(out Bloom bloom))
        {
            bloom = profile.Add<Bloom>(true);
            bloom.name = nameof(Bloom);
            AssetDatabase.AddObjectToAsset(bloom, profile);
        }

        bloom.active = true;
        bloom.threshold.Override(0.9f);
        bloom.intensity.Override(3.0f);
        EditorUtility.SetDirty(bloom);
        EditorUtility.SetDirty(profile);

        Undo.RecordObject(volume, "Configure Global Bloom Volume");
        volume.isGlobal = true;
        volume.priority = 1f;
        volume.sharedProfile = profile;
        EditorUtility.SetDirty(volume);
    }

    // 4 + 5. Core Injection & Material Generation
    static void InjectAnomalyCore()
    {
        var coreGo = GameObject.Find("Anomaly_Core");
        if (coreGo == null)
        {
            coreGo = new GameObject("Anomaly_Core");
            Undo.RegisterCreatedObjectUndo(coreGo, "Create Anomaly Core");
            coreGo.transform.position = new Vector3(0f, 0f, 1.5f);
        }

        var architect = coreGo.GetComponent<SwarmGPUArchitect>();
        if (architect == null) architect = Undo.AddComponent<SwarmGPUArchitect>(coreGo);

        var material = BuildAnomalyMaterial();
        var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        if (compute == null)
            Debug.LogError($"[Phase4ZeroTouch] Compute shader not found at '{ComputePath}'.");

        var so = new SerializedObject(architect);
        so.FindProperty("swarmCompute").objectReferenceValue = compute;
        so.FindProperty("swarmMaterial").objectReferenceValue = material;
        so.ApplyModifiedProperties();
    }

    static Material BuildAnomalyMaterial()
    {
        var shader = Shader.Find(SwarmShaderName);
        if (shader == null)
        {
            Debug.LogError($"[Phase4ZeroTouch] Shader '{SwarmShaderName}' not found.");
            return null;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(shader) { name = "Anomaly_Material" };
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.SetColor("_ColorSlow", HdrCyan);
        material.SetColor("_ColorFast", HdrCyanHot);
        EditorUtility.SetDirty(material);
        return material;
    }
}
