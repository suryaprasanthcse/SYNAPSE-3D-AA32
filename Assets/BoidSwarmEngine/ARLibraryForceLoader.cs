using System.Collections;
using System.Text;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_EDITOR
using UnityEngine.XR.Simulation;
#endif

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Brute-force runtime bind of the reference image library, plus a full tracking-chain dump.
    /// Attach to the XR Origin. On Start it re-asserts the library on the ARTrackedImageManager, restarts the
    /// image tracking subsystem, and reports every reason the subsystem could be failing to register images.
    ///
    /// In the Editor it also dumps AR Foundation's own simulated-image discovery inputs (frustum, distance, surface
    /// angle, occlusion) which is what actually decides whether a SimulatedTrackedImage becomes a trackable.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ARLibraryForceLoader : MonoBehaviour
    {
        const string LibraryResourceName = "AR_Targets";

        [Tooltip("Direct asset reference (preferred). Falls back to Resources, then to any library in the project.")]
        [SerializeField] XRReferenceImageLibrary library;
        [SerializeField] ARTrackedImageManager trackedImageManager;
        [SerializeField, Min(1)] int maxMovingImages = 5;
        [Tooltip("Re-assert the library and restart the subsystem on Start.")]
        [SerializeField] bool forceOnStart = true;   // only acts when the library is actually null
        [Tooltip("Repeat the diagnostic dump every N seconds (0 = once).")]
        [SerializeField, Min(0f)] float repeatDiagnosticsInterval = 3f;

        void Awake()
        {
            if (trackedImageManager == null) trackedImageManager = GetComponent<ARTrackedImageManager>();
            if (trackedImageManager == null) trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();
            if (library == null) library = Resources.Load<XRReferenceImageLibrary>(LibraryResourceName);
        }

        IEnumerator Start()
        {
            if (forceOnStart) yield return ForceBind();
            Dump();

            while (repeatDiagnosticsInterval > 0f)
            {
                yield return new WaitForSeconds(repeatDiagnosticsInterval);
                Dump();
            }
        }

        /// <summary>Re-assign the library and restart the image tracking subsystem.</summary>
        public IEnumerator ForceBind()
        {
            if (trackedImageManager == null)
            {
                Debug.LogError("[ARLibraryForceLoader] No ARTrackedImageManager found.", this);
                yield break;
            }

            XRReferenceImageLibrary target = library != null ? library : trackedImageManager.referenceLibrary as XRReferenceImageLibrary;
            if (target == null)
            {
                Debug.LogError("[ARLibraryForceLoader] No reference library available (serialized field empty, " +
                               $"no 'Resources/{LibraryResourceName}', manager unbound).", this);
                yield break;
            }

            // Hands off a healthy subsystem. At runtime referenceLibrary is a RuntimeReferenceImageLibrary built
            // from the asset, never the asset itself, so "is it bound?" cannot be an equality test. And XR
            // Simulation's image discoverer initialises once, on an event fired during environment setup:
            // toggling the manager afterwards leaves it waiting for an event that never fires again.
            if (trackedImageManager.referenceLibrary != null)
            {
                if (trackedImageManager.requestedMaxNumberOfMovingImages != maxMovingImages)
                    trackedImageManager.requestedMaxNumberOfMovingImages = maxMovingImages;
                Debug.Log($"[ARLibraryForceLoader] Library already bound ({trackedImageManager.referenceLibrary.count} images); " +
                          "subsystem untouched.", this);
                yield break;
            }

            // Genuinely unbound: this is the only case worth a restart.
            trackedImageManager.enabled = false;
            yield return null;

            trackedImageManager.referenceLibrary = target;
            trackedImageManager.requestedMaxNumberOfMovingImages = maxMovingImages;
            trackedImageManager.enabled = true;
            yield return null;

            Debug.Log($"[ARLibraryForceLoader] Was unbound: bound '{target.name}' ({target.count} images), " +
                      $"max moving {maxMovingImages}, subsystem restarted.", this);
        }

        /// <summary>Dump every link of the tracking chain to the console.</summary>
        public void Dump()
        {
            var sb = new StringBuilder("[ARLibraryForceLoader] Tracking chain\n");

            if (trackedImageManager == null)
            {
                Debug.LogError(sb.Append("  manager: MISSING").ToString(), this);
                return;
            }

            var lib = trackedImageManager.referenceLibrary;
            var subsystem = trackedImageManager.subsystem;
            sb.Append($"  manager enabled: {trackedImageManager.enabled}, descriptor: ")
              .Append(subsystem == null ? "NO SUBSYSTEM (image tracking not supported/loaded)\n" : $"{subsystem.running} running\n");
            string libName = lib switch
            {
                XRReferenceImageLibrary asset => asset.name,
                null => "NULL",
                _ => lib.GetType().Name
            };
            sb.Append($"  library: {libName}, {(lib == null ? 0 : lib.count)} images\n");
            sb.Append($"  trackables registered: {trackedImageManager.trackables.count}\n");

            var origin = GetComponent<XROrigin>() ?? FindAnyObjectByType<XROrigin>();
            Camera cam = origin != null ? origin.Camera : Camera.main;
            if (origin != null)
                sb.Append($"  XR Origin world pos: {origin.transform.position}\n");
            if (cam != null)
                sb.Append($"  XR camera world pos: {cam.transform.position}, forward {cam.transform.forward}\n");

#if UNITY_EDITOR
            // AR Foundation's simulated discovery compares the XR camera's WORLD pose against the simulated image's
            // WORLD pose. An XR Origin that is offset from the environment puts the camera kilometres away from the
            // card even when the simulated device pose looks correct.
            // The simulation environment lives in its own scene with hidden objects, so FindObjectsByType misses
            // them; walk the environment scene's roots instead.
            var images = new System.Collections.Generic.List<SimulatedTrackedImage>();
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    images.AddRange(root.GetComponentsInChildren<SimulatedTrackedImage>(true));
            }
            sb.Append($"  SimulatedTrackedImage components in environment: {images.Count}\n");
            foreach (var image in images)
            {
                Transform t = image.transform;
                sb.Append($"    - '{image.gameObject.name}' pos {t.position}, size {image.size}\n");
                if (cam == null) continue;

                float distance = Vector3.Distance(t.position, cam.transform.position);
                Vector3 viewport = cam.WorldToViewportPoint(t.position);
                bool inFrustum = viewport.z > 0f && viewport.x is > -0.1f and <= 1.1f && viewport.y is > -0.1f and <= 1.1f;

                // AR Foundation: 10 cm image tracks up to 2.5 m; quality needs dot(camForward, image.up) <= 0.1.
                float maxRange = (image.size.x + image.size.y) * 0.5f / 0.1f * 2.5f;
                float normalDot = Vector3.Dot(cam.transform.forward, t.up);

                sb.Append($"        distance {distance:0.00} m (max {maxRange:0.00} m) -> {(distance <= maxRange ? "OK" : "TOO FAR")}\n");
                sb.Append($"        in frustum: {inFrustum}\n");
                sb.Append($"        dot(camForward, image.up) {normalDot:0.00} (needs <= 0.10) -> {(normalDot <= 0.1f ? "OK" : "WRONG SIDE/ANGLE")}\n");
            }
#endif
            Debug.Log(sb.ToString(), this);
        }
    }
}
