using HarmonyLib;

namespace DWMPHorde.Sync
{
    /// <summary>Harmony patch: registers characters with the tracker on Start.</summary>
    [HarmonyPatch(typeof(Character), "Start")]
    public static class CharacterStartPatch
    {
        private static void Postfix(Character __instance)
        {
            CharacterTracker.Add(__instance);
        }
    }

    /// <summary>Harmony patch: deregisters characters from the tracker on destroy.</summary>
    [HarmonyPatch(typeof(Character), "OnDestroy")]
    public static class CharacterDestroyPatch
    {
        private static void Prefix(Character __instance)
        {
            CharacterTracker.Remove(__instance);
        }
    }
}
