using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Partial night death must not run single-player world mutations.
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
            {
                // A client respawning home: the teleport only. Vanilla's world half (enemies
                // back to spawn, home area cleared) runs on the host's shared world when it
                // hears of the death (HostClearHomeForRespawn); here it only split the worlds.
                if (ModRuntime.Network.Role != NetworkRole.Client || Player.Instance == null)
                    return true;
                DeathStateTracker.TeleportClientHome(Player.Instance);
                return false;
            }

            ModRuntime.LegacyInfo("[Death] Partial night death — suppressing transportToHome");
            return false;
        }
    }

    /// <summary>
    /// Vanilla resets every enemy in the world when the player dies (<c>respawnAllEnemies</c>, and
    /// <c>despawnCharacters</c> as its fallback): fine alone, but in co-op it wiped the fights and
    /// chases of everyone still alive. It runs only when the death leaves nobody alive (or on
    /// nobody's world but the host's: clients never run it).
    /// </summary>
    internal static class DeathWorldReset
    {
        internal static bool Suppress()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return false;
            if (NightDeathPolicy.ShouldSuppressWorldDeathMutations(
                    true, DeathStateTracker.LocalNightDeath, DeathStateTracker.AllDeadAtNight))
                return true;
            return net.Role == NetworkRole.Host && !DeathStateTracker.AllRemoteDead;
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), "respawnAllEnemies")]
    public static class NightDeathEnemyRespawnSuppressPatch
    {
        private static bool Prefix()
        {
            if (!DeathWorldReset.Suppress())
                return true;
            ModRuntime.LegacyInfo("[Death] other players alive — suppressing respawnAllEnemies");
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
            if (!DeathWorldReset.Suppress())
                return true;
            ModRuntime.LegacyInfo("[Death] other players alive — suppressing despawnCharacters");
            return false;
        }
    }
}
