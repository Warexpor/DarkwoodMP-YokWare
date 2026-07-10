using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Audit H3: partial night death must not run SP world mutations
    /// (home transport, enemy respawn) that soft-desync the living peer's night.
    /// skipDay/Save already suppressed; these close the remaining onDeath side effects.
    /// </summary>
    [HarmonyPatch(typeof(Player), "transportToHome")]
    public static class NightDeathTransportSuppressPatch
    {
        private static bool Prefix()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            if (!NightDeathPolicy.ShouldSuppressWorldDeathMutations(
                    true,
                    DeathStateTracker.LocalNightDeath,
                    DeathStateTracker.AllDeadAtNight))
                return true;

            ModRuntime.LegacyInfo("[Death] Partial night death — suppressing transportToHome");
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), "respawnAllEnemies")]
    public static class NightDeathEnemyRespawnSuppressPatch
    {
        private static bool Prefix()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            if (!NightDeathPolicy.ShouldSuppressWorldDeathMutations(
                    true,
                    DeathStateTracker.LocalNightDeath,
                    DeathStateTracker.AllDeadAtNight))
                return true;

            ModRuntime.LegacyInfo("[Death] Partial night death — suppressing respawnAllEnemies");
            return false;
        }
    }

    /// <summary>
    /// CharacterSpawner.despawnCharacters is the WorldGenerator-null fallback in onDeath.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "despawnCharacters")]
    public static class NightDeathDespawnCharsSuppressPatch
    {
        private static bool Prefix()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            if (!NightDeathPolicy.ShouldSuppressWorldDeathMutations(
                    true,
                    DeathStateTracker.LocalNightDeath,
                    DeathStateTracker.AllDeadAtNight))
                return true;

            // Only suppress when called during death resolution (LocalNightDeath set).
            ModRuntime.LegacyInfo("[Death] Partial night death — suppressing despawnCharacters");
            return false;
        }
    }
}
