using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// One act of the hybrid loop: a self-contained stretch of architecture with a spawn point, a card gate and
    /// an exit. Acts live side by side under the plane-anchored world root but only one is ever active; the
    /// director moves the root so the active act lands on its own patch of the real floor.
    ///
    /// The act owns the dissolve. Every architectural renderer in it shares ONE runtime instance of the dark
    /// material, so the whole act can be unmade or re-made by writing a single float, and the SRP Batcher still
    /// sees a single material for the entire act.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/Hybrid Act")]
    public sealed class HybridAct : MonoBehaviour
    {
        static readonly int ID_Dissolve = Shader.PropertyToID("_Dissolve");
        static readonly int ID_DissolveScale = Shader.PropertyToID("_DissolveScale");

        [Header("Presentation")]
        [SerializeField, Min(1)] int actNumber = 1;
        [SerializeField] string title = "THE GRAND GATEWAY";
        [SerializeField] string subtitle = "";

        [Header("Flow")]
        [Tooltip("Where Petalo stands when the act begins. Its X/Z (at design height 0) is also the point of the act " +
                 "that is dropped onto the real floor in front of the player.")]
        [SerializeField] Transform spawnPoint;
        [SerializeField] CardGate gate;
        [SerializeField] string approachObjective = "Reach the Gateway";
        [SerializeField] string exitObjective = "Pass through";
        [Tooltip("The last act: completing its gate ends the game instead of travelling on.")]
        [SerializeField] bool finale;

        [Header("Dissolve")]
        [Tooltip("The shared dark architecture material. Each act makes one runtime instance of it.")]
        [SerializeField] Material architectureMaterial;
        [SerializeField, Min(0.1f)] float dissolveSeconds = 1.8f;
        [Tooltip("Size of the dissolve's noise cells, in design metres.")]
        [SerializeField, Min(0.1f)] float dissolveCellDesignMetres = 1.6f;

        Material instance;
        float dissolve;

        public int ActNumber => actNumber;
        public string Title => title;
        public string Subtitle => subtitle;
        public Transform SpawnPoint => spawnPoint != null ? spawnPoint : transform;
        public CardGate Gate => gate;
        public string ApproachObjective => approachObjective;
        public string ExitObjective => exitObjective;
        public bool IsFinale => finale;
        public float Dissolve => dissolve;

        void Awake() => EnsureMaterial();

        void OnDestroy()
        {
            if (instance != null) Destroy(instance);
        }

        /// <summary>
        /// Swap the shared material for this act's own instance. Idempotent, and callable while the act is still
        /// inactive, so the director can set it fully dissolved before its first frame is ever drawn.
        /// </summary>
        public void EnsureMaterial()
        {
            if (instance != null || architectureMaterial == null) return;

            instance = new Material(architectureMaterial) { name = $"{architectureMaterial.name} (Act {actNumber})" };
            var renderers = new List<Renderer>();
            GetComponentsInChildren(true, renderers);
            foreach (var r in renderers)
            {
                var shared = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] != architectureMaterial) continue;
                    shared[i] = instance;
                    changed = true;
                }
                if (changed) r.sharedMaterials = shared;
            }
        }

        /// <summary>0 = fully built, 1 = fully unmade. <paramref name="worldScale"/> keeps the noise a constant size in design space.</summary>
        public void SetDissolve(float value, float worldScale)
        {
            EnsureMaterial();
            dissolve = Mathf.Clamp01(value);
            if (instance == null) return;
            instance.SetFloat(ID_Dissolve, dissolve);
            instance.SetFloat(ID_DissolveScale, 1f / Mathf.Max(worldScale * dissolveCellDesignMetres, 1e-5f));
        }

        public IEnumerator Materialize(float worldScale) => Animate(1f, 0f, worldScale);

        public IEnumerator Unmake(float worldScale) => Animate(0f, 1f, worldScale);

        IEnumerator Animate(float from, float to, float worldScale)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / dissolveSeconds;
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                SetDissolve(Mathf.Lerp(from, to, eased), worldScale);
                yield return null;
            }
            SetDissolve(to, worldScale);
        }
    }
}
