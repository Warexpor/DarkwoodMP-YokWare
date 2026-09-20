using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Marks a flare GO whose burn-out is owned by network track (host TickThrownLightExpiry),
    /// not vanilla <see cref="Flare"/> waitToDie. Keeps Flare for flicker / lightFlare rotation.
    /// </summary>
    public sealed class NetworkFlareLifetime : MonoBehaviour
    {
        /// <summary>When true, Harmony skips Flare.waitToDie.</summary>
        public bool NetworkOwnsDie = true;
    }
}
