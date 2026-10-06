using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Marks a ThrownItem GO as FX-only (client own throw or peer visualOnly spawn).
    /// Must not grant world pickup or stick-into-character inventory — host combat copy owns that.
    /// </summary>
    internal sealed class MutedThrownFxMarker : MonoBehaviour
    {
    }
}
