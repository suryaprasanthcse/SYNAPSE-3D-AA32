using System.Collections;
using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The Lumenforge fires (GDD 5.2 climax). The forge becomes a light in its own right - a beacon in the swarm's
    /// VPL grid - so the sanctum floods gold for the first time, and its Anomaly zone is armed so Petalo can drive
    /// into it and transform. The zone is disarmed until then: walking into it before the card is shown would
    /// skip the puzzle.
    /// </summary>
    [AddComponentMenu("Funobotz/Gate Reactions/Forge Ignite")]
    public sealed class ForgeIgniteReaction : GateReaction, ISwarmBeacon
    {
        [SerializeField] AnomalyZone forge;
        [Tooltip("Height of the forge's light above its origin, in design metres.")]
        [SerializeField] float lightHeight = 3f;
        [ColorUsage(false, true)] [SerializeField] Color flux = new(3.0f, 1.9f, 0.5f);
        [Tooltip("Beacon strength, in the same units as Petalo's beacon intensity.")]
        [SerializeField, Min(0f)] float intensity = 10f;
        [SerializeField, Min(0.001f)] float softRadius = 0.08f;
        [SerializeField, Min(0.1f)] float igniteSeconds = 2.5f;

        float level;

        public AnomalyZone Forge => forge;

        public void Configure(AnomalyZone zone) => forge = zone;

        void Awake()
        {
            // Disarmed until the card is shown.
            var zone = forge != null ? forge.GetComponent<Collider>() : null;
            if (zone != null) zone.enabled = false;
        }

        void OnEnable() => SwarmLightingGlobals.RegisterBeacon(this);
        void OnDisable() => SwarmLightingGlobals.UnregisterBeacon(this);

        public override IEnumerator Play()
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / igniteSeconds;
                // Flicker up, the way a cold forge catches.
                float flicker = 1f + 0.25f * Mathf.Sin(t * 40f) * (1f - t);
                level = Mathf.Clamp01(EaseInOut(Mathf.Clamp01(t)) * flicker);
                yield return null;
            }
            level = 1f;

            var zone = forge != null ? forge.GetComponent<Collider>() : null;
            if (zone != null) zone.enabled = true;
        }

        // --- ISwarmBeacon ---
        public bool BeaconActive => isActiveAndEnabled && level > 1e-3f && forge != null;
        public Vector3 BeaconPosition => forge != null ? forge.transform.TransformPoint(Vector3.up * lightHeight) : transform.position;

        public Color BeaconFlux
        {
            get
            {
                // Same reference area as Petalo's beacon, so 'intensity' is directly comparable to its slider.
                const float ReferenceArea = 0.12f * 0.12f;
                return flux * (intensity * level * ReferenceArea);
            }
        }

        public float BeaconSoftRadius => softRadius;
    }
}
