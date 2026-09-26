using System.Collections;

namespace DWMPHorde.Harmony
{
    /// <summary>
    /// Prefix return false on an IEnumerator target without setting __result yields
    /// StartCoroutine(null) → Unity "Value cannot be null. Parameter name: routine"
    /// (same class as the 0.8.37 HelpMessage / GameEvent.fire suppress NRE).
    /// Always assign <see cref="Empty"/> when skipping a coroutine method.
    /// </summary>
    internal static class HarmonyCoroutineUtil
    {
        internal static IEnumerator Empty()
        {
            yield break;
        }
    }
}
