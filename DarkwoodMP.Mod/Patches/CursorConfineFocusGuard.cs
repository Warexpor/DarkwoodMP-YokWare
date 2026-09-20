using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla Darkwood sets <see cref="CursorLockMode.Confined"/>. On Wayland
    /// (Hyprland) that becomes a pointer confine that keeps trapping the mouse
    /// inside the game window even after keyboard focus moves elsewhere — so
    /// dual-box layout / Super+drag cannot work. Release on blur; restore on focus.
    /// </summary>
    /// <remarks>
    /// Must use <c>UnityEngine.Cursor</c> — Assembly-CSharp also defines a
    /// <c>Cursor</c> type that would otherwise win the unqualified name.
    /// </remarks>
    public sealed class CursorConfineFocusGuard : MonoBehaviour
    {
        private bool _lastFocused = true;

        private void OnApplicationFocus(bool hasFocus)
        {
            Apply(hasFocus);
        }

        private void Update()
        {
            bool focused = Application.isFocused;
            if (focused == _lastFocused)
            {
                // Re-assert release if Core/Controller re-confines while blurred.
                if (!focused && UnityEngine.Cursor.lockState != CursorLockMode.None)
                    UnityEngine.Cursor.lockState = CursorLockMode.None;
                return;
            }

            _lastFocused = focused;
            Apply(focused);
        }

        internal static void Apply(bool hasFocus)
        {
            if (hasFocus)
            {
                if (UnityEngine.Cursor.lockState == CursorLockMode.None)
                    UnityEngine.Cursor.lockState = CursorLockMode.Confined;
                return;
            }

            UnityEngine.Cursor.lockState = CursorLockMode.None;
        }
    }

    /// <summary>Block Confined/Locked writes while the game window is unfocused.</summary>
    [HarmonyPatch(typeof(UnityEngine.Cursor), "set_lockState")]
    public static class CursorLockStatePatch
    {
        private static void Prefix(ref CursorLockMode value)
        {
            if (!Application.isFocused && value != CursorLockMode.None)
                value = CursorLockMode.None;
        }
    }
}
