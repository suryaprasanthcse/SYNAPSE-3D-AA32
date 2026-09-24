using System;
using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("Funobotz/Forge Ignition")]
public sealed class ForgeIgnition : MonoBehaviour
{
    static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

    public Renderer coreRenderer;

    [SerializeField, Min(0.01f)] float m_TriggerDistance = 0.2f;
    [SerializeField] Color m_Gold = new(1f, 0.72f, 0.2f);
    [SerializeField, Min(0f)] float m_Intensity = 5f;
    [SerializeField, Min(0.01f)] float m_IgniteDuration = 1.5f;
    [SerializeField, Min(0f)] float m_SearchInterval = 0.25f;

    public event Action Ignited;
    public bool IsIgnited { get; private set; }

    Transform m_Petalo;
    Material m_Material;
    float m_NextSearch;

    void Update()
    {
        if (IsIgnited || coreRenderer == null)
            return;

        if (m_Petalo == null || !m_Petalo.gameObject.activeInHierarchy)
        {
            m_Petalo = null;
            if (Time.time < m_NextSearch)
                return;

            m_NextSearch = Time.time + m_SearchInterval;
            var petalo = GameObject.FindWithTag(PetaloController.PlayerTag);
            if (petalo == null)
                return;
            m_Petalo = petalo.transform;
        }

        var toCore = coreRenderer.bounds.center - m_Petalo.position;
        if (toCore.sqrMagnitude >= m_TriggerDistance * m_TriggerDistance)
            return;

        IsIgnited = true;
        StartCoroutine(Ignite());
    }

    IEnumerator Ignite()
    {
        m_Material = coreRenderer.material;
        m_Material.EnableKeyword("_EMISSION");
        m_Material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        var from = m_Material.HasProperty(EmissionColor) ? m_Material.GetColor(EmissionColor) : Color.black;
        var to = new Color(m_Gold.r * m_Intensity, m_Gold.g * m_Intensity, m_Gold.b * m_Intensity, 1f);

        var elapsed = 0f;
        while (elapsed < m_IgniteDuration)
        {
            elapsed += Time.deltaTime;
            var t = Mathf.SmoothStep(0f, 1f, elapsed / m_IgniteDuration);
            m_Material.SetColor(EmissionColor, Color.Lerp(from, to, t));
            yield return null;
        }

        m_Material.SetColor(EmissionColor, to);
        Ignited?.Invoke();
        Debug.Log($"{nameof(ForgeIgnition)}: Lumenforge ignited.", this);
    }

    void OnDestroy()
    {
        if (m_Material != null)
            Destroy(m_Material);
    }
}
