using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative CharacterSpawnPoint (location / biome NPC place).
    ///
    /// Decompile: <c>spawnCharacter</c> → instant <c>actuallySpawn</c> or
    /// <c>waitToSpawnCharacter</c> (0.1–1s then actuallySpawn). WorldGenerator
    /// BigBiome also calls <c>actuallySpawn</c> directly. Roll is
    /// <c>Random.Range</c> vs <c>spawnChance</c>, then
    /// <c>Core.AddPrefab("Characters/"+type, …)</c> + waypoints / location list.
    /// Independent client rolls diverge which NPCs exist.
    ///
    /// Fix (no new messages):
    /// 1) Client: Prefix-skip <c>actuallySpawn</c> (covers all callers) and
    ///    <c>waitToSpawnCharacter</c> (belt if Role becomes Client mid-wait).
    /// 2) Offline + Host: vanilla.
    /// Observation: host <c>EntityStateBroadcast</c> + client pending match /
    /// <c>SpawnEntityLocally</c> (same family as RandomObjectSpawner / Porter).
    /// </summary>
    internal static class CharacterSpawnPointAuth
    {
        internal static bool IsClientConnected()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.IsConnected
                && ModRuntime.Network.Role == NetworkRole.Client;
        }
    }

    [HarmonyPatch(typeof(CharacterSpawnPoint), "actuallySpawn")]
    public static class CharacterSpawnPointActuallySpawnPatch
    {
        private static bool Prefix(CharacterSpawnPoint __instance)
        {
            if (!CharacterSpawnPointAuth.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[CharacterSpawnPoint] client skipped actuallySpawn (host-authoritative) on "
                    + (__instance != null ? __instance.name : "?")
                    + " type=" + (__instance != null ? __instance.type.ToString() : "?"));
            }
            return false;
        }
    }

    /// <summary>
    /// Belt-and-suspenders if the wait coroutine started before Role was Client.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawnPoint), "waitToSpawnCharacter")]
    public static class CharacterSpawnPointWaitToSpawnPatch
    {
        private static bool Prefix(CharacterSpawnPoint __instance)
        {
            if (!CharacterSpawnPointAuth.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[CharacterSpawnPoint] client skipped waitToSpawnCharacter on "
                    + (__instance != null ? __instance.name : "?"));
            }
            return false;
        }
    }
}
