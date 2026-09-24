using System.Collections.Generic;
using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// A light that is not a particle: emissive content (Petalo's singularity, an ignited Lumenforge) that must
    /// cast real light into the same VPL grid the swarm feeds, so the cave is lit by one unified system.
    /// </summary>
    public interface ISwarmBeacon
    {
        bool BeaconActive { get; }
        Vector3 BeaconPosition { get; }
        /// <summary>Linear flux (HDR). Scaled like a VPL cell's flux.</summary>
        Color BeaconFlux { get; }
        /// <summary>Soft-core radius (m): stops 1/d^2 exploding for surfaces inside the emitter.</summary>
        float BeaconSoftRadius { get; }
    }

    /// <summary>
    /// Owns the global shader bindings consumed by SwarmSurface.shader. A zero-light fallback buffer is bound
    /// whenever no swarm is live, so SwarmSurface materials never draw with an unbound SRV (which Unity skips).
    /// </summary>
    public static class SwarmLightingGlobals
    {
        public const int VPLCount = 64;

        /// <summary>Extra light slots appended after the swarm's 64 cells, for beacons.</summary>
        public const int BeaconSlots = 4;

        static readonly List<ISwarmBeacon> s_Beacons = new();
        public static IReadOnlyList<ISwarmBeacon> Beacons => s_Beacons;

        public static void RegisterBeacon(ISwarmBeacon beacon)
        {
            if (beacon != null && !s_Beacons.Contains(beacon)) s_Beacons.Add(beacon);
        }

        public static void UnregisterBeacon(ISwarmBeacon beacon) => s_Beacons.Remove(beacon);
        public const int VPLStride = sizeof(float) * 8;   // float4 posSoftSq + float4 color

        public static readonly int ID_SwarmVPLs = Shader.PropertyToID("_SwarmVPLs");
        public static readonly int ID_SwarmVPLParams = Shader.PropertyToID("_SwarmVPLParams");
        public static readonly int ID_SwarmIrrL0 = Shader.PropertyToID("_SwarmIrrL0");
        public static readonly int ID_SwarmIrrL1 = Shader.PropertyToID("_SwarmIrrL1");
        public static readonly int ID_SwarmVolumeMin = Shader.PropertyToID("_SwarmVolumeMin");
        public static readonly int ID_SwarmVolumeInvSize = Shader.PropertyToID("_SwarmVolumeInvSize");

        static GraphicsBuffer fallback;
        static Texture3D emptyVolume;
        static bool liveBound;

        /// <summary>
        /// Binds the live VPL buffer. params: x = total light count, y = gain, z = 1 / range^2, w = beacon count.
        /// The last <paramref name="beacons"/> entries of the buffer are the ones surfaces still integrate per
        /// pixel; everything before them reaches the surface through the irradiance volume instead.
        /// </summary>
        public static void Bind(ComputeBuffer vplBuffer, int count, float gain, float range, int beacons)
        {
            Shader.SetGlobalBuffer(ID_SwarmVPLs, vplBuffer);
            Shader.SetGlobalVector(ID_SwarmVPLParams, Params(count, gain, range, beacons));
            liveBound = true;
        }

        public static void UpdateParams(int count, float gain, float range, int beacons)
        {
            if (!liveBound) return;
            Shader.SetGlobalVector(ID_SwarmVPLParams, Params(count, gain, range, beacons));
        }

        static Vector4 Params(int count, float gain, float range, int beacons)
        {
            float r = Mathf.Max(range, 0.01f);
            return new Vector4(count, gain, 1f / (r * r), beacons);
        }

        /// <summary>
        /// Binds this frame's irradiance volume and the world-space box it covers. A pixel shader maps a world
        /// position into it with three mads, so the lookup costs two trilinear fetches and no branch.
        /// </summary>
        public static void BindVolume(Texture l0, Texture l1, Vector3 min, Vector3 invSize)
        {
            if (l0 == null || l1 == null)
            {
                ClearVolume();
                return;
            }
            Shader.SetGlobalTexture(ID_SwarmIrrL0, l0);
            Shader.SetGlobalTexture(ID_SwarmIrrL1, l1);
            Shader.SetGlobalVector(ID_SwarmVolumeMin, min);
            Shader.SetGlobalVector(ID_SwarmVolumeInvSize, new Vector4(invSize.x, invSize.y, invSize.z, 1f));
        }

        /// <summary>
        /// Unbinds the volume: an inverse size of zero collapses every lookup onto one black texel, so surfaces
        /// resolve to the void rather than to whatever the last live swarm left behind.
        /// </summary>
        public static void ClearVolume()
        {
            if (emptyVolume == null)
            {
                emptyVolume = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false)
                {
                    name = "SwarmIrradiance (empty)",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                emptyVolume.SetPixels(new[] { Color.clear });
                emptyVolume.Apply(false, true);
            }

            Shader.SetGlobalTexture(ID_SwarmIrrL0, emptyVolume);
            Shader.SetGlobalTexture(ID_SwarmIrrL1, emptyVolume);
            Shader.SetGlobalVector(ID_SwarmVolumeMin, Vector4.zero);
            Shader.SetGlobalVector(ID_SwarmVolumeInvSize, Vector4.zero);
        }

        public static void Unbind()
        {
            liveBound = false;
            BindFallback();
        }

        public static void EnsureBound()
        {
            if (!liveBound) BindFallback();
        }

        static void BindFallback()
        {
            if (fallback == null || !fallback.IsValid())
            {
                fallback = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VPLStride);
                fallback.SetData(new Vector4[2]);
            }

            Shader.SetGlobalBuffer(ID_SwarmVPLs, fallback);
            Shader.SetGlobalVector(ID_SwarmVPLParams, Vector4.zero);   // count 0: loop never runs
            ClearVolume();                                             // and no irradiance to sample
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode with domain reload disabled keeps statics alive.
            liveBound = false;
            s_Beacons.Clear();
            fallback?.Release();
            fallback = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BindOnStartup()
        {
            EnsureBound();
            Application.quitting += ReleaseFallback;
        }

        static void ReleaseFallback()
        {
            fallback?.Release();
            fallback = null;
        }

#if UNITY_EDITOR
        // Edit mode: material previews / Scene view draw SwarmSurface without any swarm running.
        [UnityEditor.InitializeOnLoadMethod]
        static void BindInEditor()
        {
            EnsureBound();
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseFallback;
        }
#endif
    }
}
