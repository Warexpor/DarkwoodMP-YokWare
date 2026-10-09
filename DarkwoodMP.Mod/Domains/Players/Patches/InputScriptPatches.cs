using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>Drives the MULTIPLAYER entry, its screens and the title-screen join flow every frame.</summary>
    [HarmonyPatch(typeof(InputScript), "Update")]
    public static class InputScriptUpdatePatch
    {
        private static void Postfix()
        {
            ModRuntime.EnsureRunning();
            MainMenuMultiplayerInject.OnUpdate();
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
