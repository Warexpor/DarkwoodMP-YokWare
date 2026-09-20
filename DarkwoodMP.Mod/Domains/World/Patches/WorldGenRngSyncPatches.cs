using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    internal static class WorldGenHostAuth
    {
        internal static bool IsConnectedClient()
        {
            var net = ModRuntime.Network;
            return net != null && net.Role == NetworkRole.Client;
        }
    }

    /// <summary>
    /// Host-authoritative worldgen RNG tables used by padlocks and chapter starts.
    /// <see cref="WorldGenSharePatch"/> blocks connected clients from finishing a
    /// brand-new gen, but <c>RandomNumberGenerator.Awake</c> / early
    /// <c>WorldGenerator.generateWorld</c> can still call these before share load
    /// and diverge padlock combinations / story outcome flags from the host.
    /// Clients receive numbers + flags via WorldSaveShare / FlagSync.
    /// </summary>
    [HarmonyPatch(typeof(RandomNumberGenerator), "init")]
    public static class RandomNumberGeneratorInitHostAuthPatch
    {
        private static bool Prefix()
        {
            if (!WorldGenHostAuth.IsConnectedClient())
                return true;
            if (ModRuntime.VerboseLogging)
                ModLog.Event(LogCat.World,
                    "[WorldGenRng] client skipped RandomNumberGenerator.init (host-authoritative)");
            return false;
        }
    }

    /// <summary>
    /// Decompile <c>WorldGenerator.generateWorld</c> → <c>ChapterPreset.initFlags</c>
    /// (optional <c>randomFlags</c> doctor/village/piotrek/wolf outcomes).
    /// Independent client rolls would set divergent <c>Flags</c> before share.
    /// </summary>
    [HarmonyPatch(typeof(ChapterPreset), "initFlags")]
    public static class ChapterPresetInitFlagsHostAuthPatch
    {
        private static bool Prefix(ChapterPreset __instance)
        {
            if (!WorldGenHostAuth.IsConnectedClient())
                return true;
            if (ModRuntime.VerboseLogging)
                ModLog.Event(LogCat.World,
                    "[WorldGenRng] client skipped ChapterPreset.initFlags (host-authoritative) on "
                    + (__instance != null ? __instance.name : "?"));
            return false;
        }
    }

    /// <summary>
    /// Decompile <c>WorldGenerator.generateWorld</c> / hard-night <c>startDay</c> →
    /// <c>WorldChunk.spawnRandomObjects</c> (RNG via <c>RandomWorldObjects</c> +
    /// AddPrefab + addToSaveable). Connected clients can still execute early gen
    /// before <see cref="WorldGenSharePatch"/> blocks <c>onFinished</c>.
    /// </summary>
    [HarmonyPatch(typeof(WorldChunk), "spawnRandomObjects")]
    public static class WorldChunkSpawnRandomObjectsHostAuthPatch
    {
        private static bool Prefix()
        {
            if (!WorldGenHostAuth.IsConnectedClient())
                return true;
            if (ModRuntime.VerboseLogging)
                ModLog.Event(LogCat.World,
                    "[WorldGenRng] client skipped WorldChunk.spawnRandomObjects (host-authoritative)");
            return false;
        }
    }

    /// <summary>Belt: WorldGenerator fan-out to chunks (same Role gate).</summary>
    [HarmonyPatch(typeof(WorldGenerator), "spawnRandomObjects")]
    public static class WorldGeneratorSpawnRandomObjectsHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }

    /// <summary>
    /// Early <c>generateWorld</c> also spawns misc props / free-roamers / globals
    /// before <c>onFinished</c>. Same client Role gate as spawnRandomObjects.
    /// Load-from-share restores entities from save; live session uses entity sync.
    /// </summary>
    [HarmonyPatch(typeof(WorldGenerator), "spawnMiscObjects")]
    public static class WorldGeneratorSpawnMiscObjectsHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }

    [HarmonyPatch(typeof(WorldGenerator), "spawnFreeRoamingCharacters")]
    public static class WorldGeneratorSpawnFreeRoamingHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }

    [HarmonyPatch(typeof(WorldGenerator), "spawnGlobalCharacters")]
    public static class WorldGeneratorSpawnGlobalCharactersHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }

    [HarmonyPatch(typeof(WorldGenerator), "spawnNightObjects")]
    public static class WorldGeneratorSpawnNightObjectsHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }

    [HarmonyPatch(typeof(WorldGenerator), "respawnAllEnemies")]
    public static class WorldGeneratorRespawnAllEnemiesHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }

    /// <summary>
    /// Belt: controller.spawn drives ObjectPoolSpawner.tryToSpawn (already
    /// client-skipped). Skip the whole early-gen pass on clients too.
    /// </summary>
    [HarmonyPatch(typeof(ObjectPoolSpawnerController), "spawn")]
    public static class ObjectPoolSpawnerControllerSpawnHostAuthPatch
    {
        private static bool Prefix() => !WorldGenHostAuth.IsConnectedClient();
    }
}
