using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Hard frame cap for the spatial matrix. Uncapped rendering burns Adreno power budget on frames nobody sees:
    /// the swarm dispatch + VPL grid + SwarmSurface loop then throttle the moment the GPU heats up, and AR image
    /// tracking (which shares the same silicon) degrades with it.
    ///
    /// No component to wire: this arms itself before the first scene loads, in the Editor and on device.
    /// </summary>
    public static class ThermalLock
    {
        public const int TargetFrameRate = 30;

        /// <summary>Frame cap currently enforced.</summary>
        public static int CurrentCap { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Arm() => Apply(TargetFrameRate);

        /// <summary>Re-apply or override the cap (e.g. 60 when running on a cooled desktop Editor).</summary>
        public static void Apply(int frameRate)
        {
            // vSync overrides targetFrameRate on desktop/Editor, so it must be off for the cap to bite.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = frameRate;
            CurrentCap = frameRate;

            // The matrix is a hands-off AR experience: no touch input means the screen would otherwise sleep
            // mid-recording.
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            Debug.Log($"[ThermalLock] Frame cap {frameRate} fps, vSync off.");
        }
    }
}
