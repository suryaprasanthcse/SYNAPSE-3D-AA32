using System.Collections;
using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>What the virtual world does once a card gate is solved: the arch restores, the doors open, the forge fires.</summary>
    public abstract class GateReaction : MonoBehaviour
    {
        /// <summary>Plays the reaction to completion.</summary>
        public abstract IEnumerator Play();

        protected static float EaseInOut(float t) => t * t * (3f - 2f * t);

        protected static float EaseOutBack(float t)
        {
            const float c1 = 1.4f, c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }
}
