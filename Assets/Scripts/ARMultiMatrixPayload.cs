using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Routes each tracked reference image to its own hologram prefab, keyed by reference image name.
/// </summary>
[RequireComponent(typeof(ARTrackedImageManager))]
[AddComponentMenu("XR/AR Matrix/AR Multi Matrix Payload")]
public sealed class ARMultiMatrixPayload : MonoBehaviour
{
    [Serializable]
    public struct TargetPayload
    {
        [Tooltip("Must match the reference image name in the XRReferenceImageLibrary exactly.")]
        public string imageName;

        [Tooltip("Hologram spawned as a child of the tracked image.")]
        public GameObject prefab;
    }

    [SerializeField] List<TargetPayload> m_Payloads = new();

    ARTrackedImageManager m_Manager;
    readonly Dictionary<string, GameObject> m_PrefabsByName = new(StringComparer.Ordinal);
    readonly Dictionary<TrackableId, GameObject> m_Spawned = new();

    void Awake()
    {
        m_Manager = GetComponent<ARTrackedImageManager>();

        foreach (var payload in m_Payloads)
        {
            if (string.IsNullOrWhiteSpace(payload.imageName) || payload.prefab == null)
            {
                Debug.LogWarning($"{nameof(ARMultiMatrixPayload)}: skipping payload with empty name or prefab.", this);
                continue;
            }

            if (!m_PrefabsByName.TryAdd(payload.imageName, payload.prefab))
                Debug.LogWarning($"{nameof(ARMultiMatrixPayload)}: duplicate entry for '{payload.imageName}'; using the first.", this);
        }
    }

    void OnEnable() => m_Manager.trackablesChanged.AddListener(OnTrackablesChanged);

    void OnDisable() => m_Manager.trackablesChanged.RemoveListener(OnTrackablesChanged);

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        foreach (var image in changes.added)
            Spawn(image);

        foreach (var image in changes.updated)
        {
            if (m_Spawned.TryGetValue(image.trackableId, out var instance) && instance != null)
                SetVisible(instance, image.trackingState);
        }

        foreach (var removed in changes.removed)
        {
            if (m_Spawned.Remove(removed.Key, out var instance) && instance != null)
                Destroy(instance);
        }
    }

    void Spawn(ARTrackedImage image)
    {
        if (m_Spawned.ContainsKey(image.trackableId))
            return;

        var imageName = image.referenceImage.name;
        if (string.IsNullOrEmpty(imageName) || !m_PrefabsByName.TryGetValue(imageName, out var prefab))
        {
            Debug.LogWarning($"{nameof(ARMultiMatrixPayload)}: no payload mapped for image '{imageName}'.", this);
            return;
        }

        var instance = Instantiate(prefab, image.transform);
        instance.name = $"{prefab.name}_{imageName}";
        SetVisible(instance, image.trackingState);
        m_Spawned.Add(image.trackableId, instance);
    }

    // Limited means the pose is stale, so hiding avoids holograms hanging where the card used to be.
    static void SetVisible(GameObject instance, TrackingState state)
    {
        var visible = state == TrackingState.Tracking;
        if (instance.activeSelf != visible)
            instance.SetActive(visible);
    }
}
