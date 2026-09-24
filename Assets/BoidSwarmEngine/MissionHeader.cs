using UnityEngine;
using UnityEngine.UI;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The minimalist header: a dark slab at the top of the screen carrying the frame rate and the current
    /// objective. This is real uGUI rather than the IMGUI diagnostics overlay, because it is part of the game
    /// rather than part of the instrumentation - it ships, and the HUD does not.
    ///
    /// Deliberately built on <see cref="Text"/> and the built-in font rather than TextMeshPro: TMP needs its
    /// essential resources imported into the project, which this project has never done, and a TMP label with no
    /// font asset renders nothing at all on device while logging only a warning.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/Mission Header")]
    public sealed class MissionHeader : MonoBehaviour
    {
        [SerializeField] Text titleLine;
        [SerializeField] Text missionLine;

        [Header("Content")]
        [SerializeField] string title = "SPATIAL MATRIX";
        [SerializeField] string objective = "Route Swarm";
        [SerializeField] string target = "IGNITE";
        [Tooltip("Shown once the Lumenforge has been reached.")]
        [SerializeField] string completedText = "MISSION: Lumenforge <color=#FFC64D>IGNITED</color>";

        [Header("Frame Rate")]
        [Tooltip("Smoothing for the displayed rate, so it reads as a number rather than a flicker.")]
        [SerializeField, Range(0.001f, 0.5f)] float fpsSmoothing = 0.05f;

        float smoothedDt = 1f / 30f;
        bool completed;

        void Awake()
        {
            smoothedDt = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : 1f / 30f;
            RefreshMission();
        }

        void Update()
        {
            smoothedDt = Mathf.Lerp(smoothedDt, Time.unscaledDeltaTime, fpsSmoothing);
            if (titleLine != null)
                titleLine.text = $"{title}   {Mathf.RoundToInt(1f / Mathf.Max(smoothedDt, 1e-4f))} FPS";
        }

        /// <summary>Swap the objective mid-run, e.g. when a zone unlocks.</summary>
        public void SetObjective(string newObjective, string newTarget)
        {
            objective = newObjective;
            target = newTarget;
            completed = false;
            RefreshMission();
        }

        /// <summary>Wired to AnomalyZone.entered: the forge is lit, the run is done.</summary>
        public void SetCompleted()
        {
            completed = true;
            RefreshMission();
        }

        void RefreshMission()
        {
            if (missionLine == null) return;

            // The arrow is written as a literal so it survives any encoding round-trip through the scene file.
            missionLine.text = completed
                ? completedText
                : $"MISSION: {objective} → <color=#FFC64D>{target}</color>";
        }
    }
}
