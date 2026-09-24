using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Spawns a procedurally generated holographic payload on every tracked reference image.
/// No prefab, no material asset, no inspector wiring required.
/// </summary>
/// <remarks>
/// All payloads share one mesh and one instanced material, and every payload is spun from this
/// component's single Update loop rather than from a per-payload MonoBehaviour.
/// </remarks>
[RequireComponent(typeof(ARTrackedImageManager))]
[AddComponentMenu("XR/AR Matrix/AR Matrix Payload")]
public sealed class ARMatrixPayload : MonoBehaviour
{
    public enum PayloadShape
    {
        Cube,
        Sphere,
    }

    [Header("Payload")]
    [SerializeField] PayloadShape m_Shape = PayloadShape.Cube;

    [Tooltip("Uniform size in meters. 0.05 sits neatly on a playing card.")]
    [SerializeField, Min(0.001f)] float m_Scale = 0.05f;

    [Tooltip("Hover height above the image plane, in meters, along the image normal (+Y).")]
    [SerializeField] float m_HoverHeight = 0.05f;

    [Tooltip("Degrees per second around the payload's local up axis.")]
    [SerializeField] float m_RotationSpeed = 45f;

    [Header("Hologram")]
    [SerializeField] Color m_Color = new Color(0f, 1f, 0f, 0.5f);

    [Tooltip("Hide the payload while its image is not actively tracked.")]
    [SerializeField] bool m_HideWhenNotTracking = true;

    ARTrackedImageManager m_Manager;
    Material m_Material;
    Mesh m_Mesh;

    // Parallel containers: the dictionary resolves removals, the list is the allocation-free spin loop.
    readonly Dictionary<TrackableId, Transform> m_PayloadsById = new();
    readonly List<Transform> m_Payloads = new();

    void Awake() => m_Manager = GetComponent<ARTrackedImageManager>();

    void OnEnable() => m_Manager.trackablesChanged.AddListener(OnTrackablesChanged);

    void OnDisable() => m_Manager.trackablesChanged.RemoveListener(OnTrackablesChanged);

    void OnDestroy()
    {
        if (m_Material != null)
            Destroy(m_Material);
    }

    void Update()
    {
        if (m_Payloads.Count == 0 || m_RotationSpeed == 0f)
            return;

        var step = m_RotationSpeed * Time.deltaTime;
        for (var i = m_Payloads.Count - 1; i >= 0; i--)
        {
            var payload = m_Payloads[i];
            if (payload == null)
            {
                m_Payloads.RemoveAt(i); // trackable destroyed without a removal event
                continue;
            }

            payload.Rotate(0f, step, 0f, Space.Self);
        }
    }

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        var added = changes.added;
        for (var i = 0; i < added.Count; i++)
            Spawn(added[i]);

        if (m_HideWhenNotTracking)
        {
            var updated = changes.updated;
            for (var i = 0; i < updated.Count; i++)
            {
                var image = updated[i];
                if (m_PayloadsById.TryGetValue(image.trackableId, out var payload) && payload != null)
                {
                    var visible = image.trackingState == TrackingState.Tracking;
                    if (payload.gameObject.activeSelf != visible)
                        payload.gameObject.SetActive(visible);
                }
            }
        }

        var removed = changes.removed;
        for (var i = 0; i < removed.Count; i++)
            Despawn(removed[i].Key);
    }

    void Spawn(ARTrackedImage image)
    {
        if (image == null || m_PayloadsById.ContainsKey(image.trackableId))
            return;

        var go = new GameObject($"Payload_{image.referenceImage.name}");
        var payload = go.transform;
        payload.SetParent(image.transform, false);

        // The tracked image's +Y is the normal out of the image plane, so Y is "up" off the card.
        payload.localPosition = new Vector3(0f, m_HoverHeight, 0f);
        payload.localRotation = Quaternion.identity;
        payload.localScale = Vector3.one * m_Scale;

        go.AddComponent<MeshFilter>().sharedMesh = GetMesh();

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = GetMaterial();
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        renderer.allowOcclusionWhenDynamic = false;

        if (m_HideWhenNotTracking && image.trackingState != TrackingState.Tracking)
            go.SetActive(false);

        m_PayloadsById.Add(image.trackableId, payload);
        m_Payloads.Add(payload);
    }

    void Despawn(TrackableId id)
    {
        if (!m_PayloadsById.Remove(id, out var payload))
            return;

        m_Payloads.Remove(payload);
        if (payload != null)
            Destroy(payload.gameObject);
    }

    Mesh GetMesh()
    {
        if (m_Mesh != null)
            return m_Mesh;

        // Harvest the built-in primitive mesh, then discard the throwaway GameObject and its collider.
        var primitive = GameObject.CreatePrimitive(m_Shape == PayloadShape.Sphere ? PrimitiveType.Sphere : PrimitiveType.Cube);
        primitive.SetActive(false);
        m_Mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
        Destroy(primitive);
        return m_Mesh;
    }

    Material GetMaterial()
    {
        if (m_Material != null)
            return m_Material;

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                     Shader.Find("Unlit/Color") ??
                     Shader.Find("Sprites/Default");

        m_Material = new Material(shader)
        {
            name = "MatrixHologram (Runtime)",
            enableInstancing = true,
        };

        // URP surface-type plumbing. Setting only the color leaves the material opaque.
        m_Material.SetOverrideTag("RenderType", "Transparent");
        m_Material.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
        m_Material.SetFloat("_Blend", 0f);     // Alpha blending
        m_Material.SetFloat("_ZWrite", 0f);
        m_Material.SetFloat("_AlphaClip", 0f);
        m_Material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        m_Material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        m_Material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m_Material.DisableKeyword("_ALPHATEST_ON");
        m_Material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m_Material.renderQueue = (int)RenderQueue.Transparent;

        if (m_Material.HasProperty("_BaseColor"))
            m_Material.SetColor("_BaseColor", m_Color);
        if (m_Material.HasProperty("_Color"))
            m_Material.SetColor("_Color", m_Color);

        return m_Material;
    }
}
