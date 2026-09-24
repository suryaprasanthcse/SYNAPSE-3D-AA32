using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Feeds the AR Depth API into the swarm so particles flow around real furniture instead of through it.
    ///
    /// The depth reaches the GPU through a texture THIS component owns. ARCore's own environment-depth texture is
    /// an external image created and recycled by the native plugin; under Vulkan, handing that image to a compute
    /// dispatch crashed the player outright (the app simply closed) as soon as a warm depth stream was bound -
    /// which is why the first transition survived and the second did not. Instead, the small CPU depth image
    /// (~160x90) is copied into an RFloat texture each frame of a transition: a few kilobytes, and nothing the
    /// GPU reads can be freed or resized underneath it.
    ///
    /// ARCore's depth feature is switched on once, the first time a transition needs it, and then left running.
    /// Toggling it per transition reconfigures the ARCore session every time, which is its own source of hitches
    /// and camera restarts. Occlusion preference is NoOcclusion, so the camera background never consumes it: a
    /// world laid flat on the floor would otherwise flicker in and out against the floor's own depth estimate.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Funobotz/Swarm Depth Field")]
    public sealed class SwarmDepthField : MonoBehaviour
    {
        [SerializeField] AROcclusionManager occlusion;
        [SerializeField] ARCameraManager cameraManager;
        [SerializeField] SwarmGPUArchitect swarm;
        [Tooltip("Feed depth to the swarm all the time rather than only during transitions.")]
        [SerializeField] bool alwaysOn;

        Camera viewCamera;
        Matrix4x4 displayMatrix = Matrix4x4.identity;
        bool haveDisplayMatrix;
        bool active;
        bool depthStarted;

        Texture2D depthTexture;
        NativeArray<float> depthMetres;

        /// <summary>Request depth. On device the first depth image takes a moment to arrive after the first request.</summary>
        public bool Active
        {
            get => active;
            set
            {
                active = value;
                if (active || alwaysOn) StartDepth();
            }
        }

        /// <summary>True while a depth image is actually steering the swarm.</summary>
        public bool DepthLive => swarm != null && swarm.DepthAvoidanceActive;

        void Awake()
        {
            viewCamera = GetComponent<Camera>();
            if (occlusion == null) occlusion = GetComponent<AROcclusionManager>();
            if (cameraManager == null) cameraManager = GetComponent<ARCameraManager>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();

            if (occlusion != null)
            {
                occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
                occlusion.environmentDepthTemporalSmoothingRequested = true;
                occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
                occlusion.enabled = false;
            }
            if (alwaysOn) StartDepth();
        }

        void OnEnable()
        {
            if (cameraManager != null) cameraManager.frameReceived += OnCameraFrame;
        }

        void OnDisable()
        {
            if (cameraManager != null) cameraManager.frameReceived -= OnCameraFrame;
            if (swarm != null) swarm.ClearDepthField();
        }

        void OnDestroy()
        {
            if (depthMetres.IsCreated) depthMetres.Dispose();
            if (depthTexture != null) Destroy(depthTexture);
        }

        void StartDepth()
        {
            if (depthStarted || occlusion == null) return;
            depthStarted = true;
            occlusion.enabled = true;

            // Re-applied after enabling: the manager only forwards this to a subsystem that is running.
            occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (!args.displayMatrix.HasValue) return;
            displayMatrix = args.displayMatrix.Value;
            haveDisplayMatrix = true;
        }

        void Update()
        {
            if (swarm == null) return;

            bool wanted = (active || alwaysOn) && depthStarted && haveDisplayMatrix && occlusion != null && occlusion.enabled;
            if (wanted && TryCopyDepth())
                swarm.SetDepthField(depthTexture, displayMatrix, viewCamera);
            else
                swarm.ClearDepthField();
        }

        /// <summary>Latest CPU depth image into the owned texture, converted to metres. False if there is none.</summary>
        bool TryCopyDepth()
        {
            if (!occlusion.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage image)) return false;

            try
            {
                if (image.planeCount < 1) return false;
                var format = image.format;
                if (format != XRCpuImage.Format.DepthUint16 && format != XRCpuImage.Format.DepthFloat32) return false;

                int width = image.width, height = image.height;
                EnsureTexture(width, height);

                var plane = image.GetPlane(0);
                var data = plane.data;
                int rowStride = plane.rowStride;
                int pixelStride = plane.pixelStride;

                // Rows are copied in order: this is exactly what the native plugin uploads to its own depth texture,
                // so the AR display matrix maps viewport UVs onto this texture the same way it maps them onto that one.
                for (int y = 0; y < height; y++)
                {
                    int row = y * rowStride;
                    int dst = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int at = row + x * pixelStride;
                        depthMetres[dst + x] = format == XRCpuImage.Format.DepthUint16
                            ? (data[at] | (data[at + 1] << 8)) * 0.001f               // millimetres
                            : data.ReinterpretLoad<float>(at);                         // metres
                    }
                }

                depthTexture.SetPixelData(depthMetres, 0);
                depthTexture.Apply(false, false);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SwarmDepthField] Depth copy failed, avoidance off this frame: {e.Message}", this);
                return false;
            }
            finally
            {
                image.Dispose();
            }
        }

        void EnsureTexture(int width, int height)
        {
            if (depthTexture != null && depthTexture.width == width && depthTexture.height == height) return;

            if (depthTexture != null) Destroy(depthTexture);
            if (depthMetres.IsCreated) depthMetres.Dispose();

            depthTexture = new Texture2D(width, height, TextureFormat.RFloat, false, true)
            {
                name = "SwarmEnvironmentDepth",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            depthMetres = new NativeArray<float>(width * height, Allocator.Persistent);
        }
    }
}
