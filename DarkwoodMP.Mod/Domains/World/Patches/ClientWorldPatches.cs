using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>Blocks client-side spawning that the host should control.</summary>
    internal static class ClientWorldHelper
    {
        internal static bool IsClient => ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Client;
    }

    /// <summary>Blocks client-side night-time creature spawn (host spawns all nocturnal NPCs).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnNightChar")]
    public static class ClientDisableNightSpawnPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>Blocks client-side shadow spawn (shadows synced from host via ShadowSpawnMessage).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "waitToSpawnShadow")]
    public static class ClientDisableShadowSpawnPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>Blocks client-side worm spawn (host-authoritative spawn).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "waitToSpawnWorm")]
    public static class ClientDisableWormSpawnPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>Blocks client-side nocturnal character despawn (host controls despawning).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "despawnNocturnalCharacters")]
    public static class ClientDisableNocturnalDespawnPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>Blocks client-side forest spirit spawn (host-authoritative).</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnForestSpirit")]
    public static class ClientDisableForestSpiritPatch
    {
        private static bool Prefix()
        {
            return !ClientWorldHelper.IsClient;
        }
    }

    /// <summary>
    /// Host-authoritative night random events: clients never fire RandomEvent.
    /// Host spawns (e.g. Redneck via spawnCharacterAround) and entity snapshots
    /// reach clients — dual fire would duplicate near each player.
    /// </summary>
    [HarmonyPatch(typeof(RandomEvent), "fire", typeof(bool), typeof(bool))]
    public static class ClientBlockRandomEventFirePatch
    {
        private static bool Prefix(RandomEvent __instance)
        {
            if (!ClientWorldHelper.IsClient) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
            return false;
        }
    }

}
