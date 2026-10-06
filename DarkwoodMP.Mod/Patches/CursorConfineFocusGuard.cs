using DWMPHorde.Config;
using DWMPHorde.Logging;
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

    /// <summary>
    /// The game's mouse position stays inside the window while the OS pointer is free.
    /// Vanilla never sees a pointer outside its window: it confines it. Every mouse reader
    /// relies on that; <c>CamMain.FixedUpdate</c> pushes the camera toward the cursor by
    /// <c>(clamp(cursor, -Screen.width, Screen.width) - Screen.width/2) / seeDistance</c>, so a
    /// cursor at the window edge looks a third of a screen ahead. With the pointer free, the
    /// Wine/Proton build keeps reporting it when it sits over the other game window (negative
    /// or past the edge), and the camera ran up to a full screen width ahead to the left and
    /// below: the client could look about three times as far as the host, whose native X11
    /// build stops getting pointer motion at its window edge. <c>Core.MouseKeyboardCursorPos</c>
    /// and <c>Core.ControllerCursorPos</c> are the game's cursor (camera look, aim, throws,
    /// hover); when they return the raw mouse, it is clamped to the window as the confine
    /// would. Acts only with <c>FreeCursorForDualBox</c> on, like the rest of this file.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch]
    public static class FreeCursorWindowClampPatch
    {
        private static bool _wasOutside; // process-scoped: pointer-edge state of this window
        private static int _outsideEdges; // process-scoped: caps the diagnostic log to the first edges per run

        // An empty target list aborts PatchAll; skip the class instead.
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            System.Reflection.MethodBase mk = AccessTools.Method(typeof(Core), "MouseKeyboardCursorPos");
            if (mk != null) yield return mk;
            System.Reflection.MethodBase ctl = AccessTools.Method(typeof(Core), "ControllerCursorPos");
            if (ctl != null) yield return ctl;
        }

        private static void Postfix(ref Vector2 __result)
        {
            if (ModConfig.FreeCursorForDualBox == null || !ModConfig.FreeCursorForDualBox.Value)
                return;
            if (!ReturnsRawMouse())
                return;

            float w = Screen.width;
            float h = Screen.height;
            if (w <= 0f || h <= 0f)
                return;
            bool outside = __result.x < 0f || __result.x > w || __result.y < 0f || __result.y > h;
            if (outside && !_wasOutside)
            {
                _outsideEdges++;
                string line = $"[Cursor] pointer outside the window at ({__result.x:F0},{__result.y:F0}) "
                    + $"window={w:F0}x{h:F0}; game cursor held at the edge (vanilla confine)";
                if (_outsideEdges <= 3)
                    ModLog.Event(LogCat.UI, line);
                else
                    ModLog.TraceRate(LogCat.UI, "cursor-outside", line, 10f);
            }
            _wasOutside = outside;
            if (!outside)
                return;
            __result = new Vector2(Mathf.Clamp(__result.x, 0f, w), Mathf.Clamp(__result.y, 0f, h));
        }

        /// <summary>Both methods fall through to <c>Input.mousePosition</c> unless this holds.</summary>
        private static bool ReturnsRawMouse()
        {
            if (Singleton<Controller>.Instance == null || Singleton<Globals>.Instance == null
                || !Singleton<Globals>.Instance.controllerMode)
                return true;
            return Player.Instance == null || Singleton<InputScript>.Instance == null
                || Singleton<InputScript>.Instance.rewiredPlayer == null;
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
