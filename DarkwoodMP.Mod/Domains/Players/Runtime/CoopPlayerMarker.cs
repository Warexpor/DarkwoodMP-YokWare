using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Marks a cloned Player that must not replace Player.Instance on registerMe.
    /// </summary>
    public sealed class CoopPlayerMarker : MonoBehaviour
    {
        /// <summary>
        /// When true, PlayerControlRouter treats this as the second local player rather than a remote proxy.
        /// </summary>
        public bool IsLocalCoopSecond = true;
    }
}
