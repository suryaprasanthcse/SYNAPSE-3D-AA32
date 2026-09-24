using System.Collections;
using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The Mystery Cave's vault doors: each leaf hangs from a hinge pivot and swings away from Petalo. A short
    /// shudder first, so the mass reads before it moves.
    /// </summary>
    [AddComponentMenu("Funobotz/Gate Reactions/Door Open")]
    public sealed class DoorOpenReaction : GateReaction
    {
        [SerializeField] Transform[] hinges = System.Array.Empty<Transform>();
        [Tooltip("Open yaw per hinge, in degrees, relative to the closed pose.")]
        [SerializeField] float[] openYaw = System.Array.Empty<float>();
        [SerializeField, Min(0f)] float shudderSeconds = 0.6f;
        [SerializeField, Min(0f)] float shudderDegrees = 1.2f;
        [SerializeField, Min(0.1f)] float openSeconds = 2.6f;

        public void Configure(Transform[] leafHinges, float[] yaw)
        {
            hinges = leafHinges;
            openYaw = yaw;
        }

        public override IEnumerator Play()
        {
            int n = hinges.Length;
            var closed = new Quaternion[n];
            for (int i = 0; i < n; i++) closed[i] = hinges[i] != null ? hinges[i].localRotation : Quaternion.identity;

            float t = 0f;
            while (t < shudderSeconds)
            {
                t += Time.deltaTime;
                float s = Mathf.Sin(t * 60f) * shudderDegrees * (1f - t / Mathf.Max(shudderSeconds, 1e-3f));
                for (int i = 0; i < n; i++)
                    if (hinges[i] != null) hinges[i].localRotation = closed[i] * Quaternion.Euler(0f, s, 0f);
                yield return null;
            }

            t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / openSeconds;
                float eased = EaseInOut(Mathf.Clamp01(t));
                for (int i = 0; i < n; i++)
                {
                    if (hinges[i] == null) continue;
                    float yaw = i < openYaw.Length ? openYaw[i] : 90f;
                    hinges[i].localRotation = closed[i] * Quaternion.Euler(0f, yaw * eased, 0f);
                }
                yield return null;
            }
        }
    }
}
