using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Hooks InputScript.Update so the multiplayer menu (F2) works reliably in this Unity build.</summary>
    [HarmonyPatch(typeof(InputScript), "Update")]
    public static class InputScriptUpdatePatch
    {
        private static void Postfix()
        {
            ModRuntime.EnsureRunning();
            // Native title MULTIPLAYER inject + join timeout (must run even if IMGUI closed)
            MainMenuMultiplayerInject.OnUpdate();
            if (Input.GetKeyDown(KeyCode.F2))
                MultiplayerMenu.ToggleVisible();
        }
    }

    /// <summary>Hooks InputScript.Awake to ensure runtime is bootstrapped before other patches fire.</summary>
    [HarmonyPatch(typeof(InputScript), "Awake")]
    public static class InputScriptAwakePatch
    {
        private static void Postfix()
        {
            ModRuntime.EnsureRunning();
            ModRuntime.LegacyInfo("Hooked into InputScript — multiplayer menu active in-game.");
        }
    }


}
