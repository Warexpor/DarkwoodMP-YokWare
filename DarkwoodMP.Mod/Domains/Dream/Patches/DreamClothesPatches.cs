using DWMPHorde.Logging;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// <c>special_changeClothes</c> / <c>special_removeClothes</c> are toggles that vanilla runs
    /// only inside the home dream and the epilogue (start event dresses, end event undresses).
    /// A replay of the dream's end event from another player that lands after this player
    /// already woke flipped the clothes back on in the overworld: the player walked around in
    /// the dream's clothes, and the others saw them so. Outside a dream or the epilogue the toggle
    /// has nothing to do.
    /// </summary>
    [HarmonyPatch(typeof(Player))]
    public static class DreamClothesTogglePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Player.special_changeClothes))]
        private static bool ChangeClothes(Player __instance) => Allowed(__instance, "special_changeClothes");

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Player.special_removeClothes))]
        private static bool RemoveClothes(Player __instance) => Allowed(__instance, "special_removeClothes");

        private static bool Allowed(Player p, string what)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || p == null || p.inEpilogue)
                return true;
            Dreams dreams = Singleton<Dreams>.Instance;
            if ((dreams != null && (dreams.dreaming || dreams.switchingDream || dreams.dreamPrepared))
                || DreamSyncManager.IsLocalDreamActive || Core.EnteringDream)
                return true;
            ModLog.Event(LogCat.Dream, "[Clothes] " + what + " outside a dream dropped (a late dream event)");
            return false;
        }
    }
}
