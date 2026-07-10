using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Model C: morning trader survival bonus is per-player.
    /// If this peer died at night, set <see cref="Controller.gaveAfterNightRewards"/>
    /// before <c>startAfterNight</c> so the trader <c>reputation +=</c> block is skipped
    /// while spawn/FX still run. Live ReputationSync also ignores isNightTrader, so
    /// host/client bonuses never overwrite each other.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startAfterNight")]
    public static class MorningRepPatch
    {
        private static bool Prefix(Controller __instance)
        {
            if (!DeathStateTracker.SkipMorningRepBonus)
                return true;

            ModRuntime.LegacyInfo("[MorningRep] Player died at night — skipping reputation bonus");
            DeathStateTracker.SkipMorningRepBonus = false;
            // Vanilla gates the +rep block on !gaveAfterNightRewards — pre-set skips only rep.
            __instance.gaveAfterNightRewards = true;
            return true;
        }
    }
}
