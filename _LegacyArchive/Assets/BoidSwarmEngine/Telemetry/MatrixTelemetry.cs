using System.Text;
using MAAYAI.Matrix.Swarm;
using UnityEngine;

namespace MAAYAI.Matrix.Telemetry
{
    /// <summary>
    /// Raw IMGUI frame telemetry: smoothed FPS, frame time, and the real CPU/GPU work per frame.
    ///
    /// Frame time alone is misleading under Application.targetFrameRate: at a 60 fps cap it
    /// reads ~16.7 ms however cheap the frame is. The CPU and GPU lines come from
    /// FrameTimingManager and show the actual work, which is what reveals the swarm's cost.
    ///
    /// Overhead control: no Canvas; layout pass disabled (useGUILayout = false); drawing
    /// only on the Repaint event; text rebuilt a few times per second, not every frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MatrixTelemetry : MonoBehaviour
    {
        private static readonly string[] MonospaceFonts = { "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono" };

        private const float FrameBudget60Ms = 1000f / 60f;
        private const float FrameBudget30Ms = 1000f / 30f;

        [Tooltip("Exponential smoothing factor per frame. Lower = steadier, slower to react.")]
        [Range(0.01f, 1f)] public float smoothing = 0.1f;
        [Tooltip("Seconds between text refreshes. The readout is smoothed, so faster adds nothing but garbage.")]
        [Min(0.05f)] public float refreshInterval = 0.25f;
        [Min(8)] public int fontSize = 22;
        public Vector2 screenOffset = new Vector2(12f, 12f);

        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        private readonly StringBuilder _text = new StringBuilder(256);
        private readonly GUIContent _content = new GUIContent();

        private SwarmManager _swarm;
        private GUIStyle _style;
        private Texture2D _background;
        private Font _font;
        private Rect _rect;

        private float _smoothedDeltaMs = -1f;
        private double _smoothedCpuMs = -1.0;
        private double _smoothedGpuMs = -1.0;
        private float _nextRefreshTime;

        private void Awake()
        {
            // Skips the IMGUI Layout event entirely; only Repaint reaches OnGUI.
            useGUILayout = false;
            _swarm = GetComponent<SwarmManager>();
        }

        private void Update()
        {
            float deltaMs = Time.unscaledDeltaTime * 1000f;
            _smoothedDeltaMs = _smoothedDeltaMs < 0f ? deltaMs : Mathf.Lerp(_smoothedDeltaMs, deltaMs, smoothing);

            // Needs "Frame Timing Stats" in Player Settings for builds; returns 0 frames if unavailable.
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _frameTimings) > 0)
            {
                _smoothedCpuMs = Smooth(_smoothedCpuMs, _frameTimings[0].cpuFrameTime);
                _smoothedGpuMs = Smooth(_smoothedGpuMs, _frameTimings[0].gpuFrameTime);
            }

            if (Time.unscaledTime >= _nextRefreshTime)
            {
                _nextRefreshTime = Time.unscaledTime + refreshInterval;
                RebuildText();
            }
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _content.text == null)
                return;

            EnsureStyle();
            GUI.Label(_rect, _content, _style);
        }

        private void OnDestroy()
        {
            if (_background != null)
                Destroy(_background);
            if (_font != null)
                Destroy(_font);
        }

        private double Smooth(double current, double sample)
        {
            if (sample <= 0.0)
                return current;   // this API/platform didn't report the value this frame
            return current < 0.0 ? sample : current + (sample - current) * smoothing;
        }

        private void RebuildText()
        {
            float fps = _smoothedDeltaMs > 0f ? 1000f / _smoothedDeltaMs : 0f;

            _text.Clear();
            _text.Append("MATRIX TELEMETRY\n");
            _text.Append("FPS    ").Append(fps.ToString("F1")).Append('\n');
            _text.Append("FRAME  ").Append(_smoothedDeltaMs.ToString("F2")).Append(" ms\n");
            _text.Append("CPU    ").Append(FormatMs(_smoothedCpuMs)).Append('\n');
            _text.Append("GPU    ").Append(FormatMs(_smoothedGpuMs)).Append('\n');
            _text.Append("CAP    ").Append(Application.targetFrameRate > 0 ? Application.targetFrameRate + " fps" : "none");

            if (_swarm != null && _swarm.isActiveAndEnabled)
            {
                _text.Append('\n');
                _text.Append("BOIDS  ").Append(_swarm.boidCount).Append('\n');
                _text.Append("DISP   ").Append(_swarm.DispatchesPerFrame).Append(" / frame");
            }

            _content.text = _text.ToString();

            EnsureStyle();
            _style.normal.textColor = BudgetColor(_smoothedDeltaMs);
            Vector2 size = _style.CalcSize(_content);
            _rect = new Rect(screenOffset.x, screenOffset.y, size.x, size.y);
        }

        private static string FormatMs(double ms)
        {
            return ms < 0.0 ? "n/a" : ms.ToString("F2") + " ms";
        }

        private static Color BudgetColor(float frameMs)
        {
            if (frameMs <= FrameBudget60Ms + 0.5f)
                return new Color(0.2f, 1f, 0.35f);   // on or under 60 fps budget
            if (frameMs <= FrameBudget30Ms)
                return new Color(1f, 0.85f, 0.1f);   // between 30 and 60 fps
            return new Color(1f, 0.25f, 0.2f);       // under 30 fps
        }

        private void EnsureStyle()
        {
            if (_style != null)
                return;

            _background = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            _background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
            _background.Apply();

            _font = Font.CreateDynamicFontFromOSFont(MonospaceFonts, fontSize);

            _style = new GUIStyle
            {
                font = _font,
                fontSize = fontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 8, 8),
                richText = false,
                wordWrap = false
            };
            _style.normal.background = _background;
            _style.normal.textColor = BudgetColor(_smoothedDeltaMs);
        }
    }
}
