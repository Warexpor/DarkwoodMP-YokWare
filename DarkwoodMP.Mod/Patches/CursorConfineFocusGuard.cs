using DWMPHorde.Config;
using HarmonyLib;
using UnityEngine;
using DWMPHorde.Harmony;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla Darkwood sets <see cref="CursorLockMode.Confined"/>. Native Linux
    /// Wayland turns that into a pointer confine; Wine/Proton turns it into
    /// Win32 ClipCursor. Focus-based release is unreliable under XWayland (the
    /// game often keeps <see cref="Application.isFocused"/> true), and toggling
    /// ClipCursor on blur can hard-freeze the Wine window. Dual-box needs a free
    /// pointer always — rewrite Confined/Locked to None and never re-engage. Acts only when the
    /// <c>FreeCursorForDualBox</c> config is true; otherwise the cursor stays vanilla.
    /// </summary>
    /// <remarks>
    /// Must use <c>UnityEngine.Cursor</c> — Assembly-CSharp also defines a
    /// <c>Cursor</c> type that would otherwise win the unqualified name.
    /// </remarks>
    public sealed class CursorConfineFocusGuard : MonoBehaviour
    {
        private int _tick;

        private void Start()
        {
            ForceFreeIfEnabled();
        }

        private void OnApplicationFocus(bool _)
        {
            ForceFreeIfEnabled();
        }

        private void Update()
        {
            if (!IsFreeCursorEnabled())
                return;
            // Rare re-assert only — per-frame ClipCursor thrash freezes Wine.
            if ((++_tick & 31) != 0)
                return;
            ForceFreeIfEnabled();
        }

        private static bool IsFreeCursorEnabled()
        {
            // Opt-in only: vanilla cursor handling stays untouched unless the setting is bound and true
            // (unbound = config not loaded yet → vanilla).
            return ModConfig.FreeCursorForDualBox != null && ModConfig.FreeCursorForDualBox.Value;
        }

        internal static void ForceFreeIfEnabled()
        {
            if (!IsFreeCursorEnabled())
                return;
            if (UnityEngine.Cursor.lockState != CursorLockMode.None)
                UnityEngine.Cursor.lockState = CursorLockMode.None;
        }
    }

    /// <summary>Rewrite Confined/Locked to None so Wine never ClipCursor-confines.</summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(UnityEngine.Cursor), "set_lockState")]
    public static class CursorLockStatePatch
    {
        private static void Prefix(ref CursorLockMode value)
        {
            if (ModConfig.FreeCursorForDualBox == null || !ModConfig.FreeCursorForDualBox.Value)
                return;
            if (value != CursorLockMode.None)
                value = CursorLockMode.None;
        }
    }
}
