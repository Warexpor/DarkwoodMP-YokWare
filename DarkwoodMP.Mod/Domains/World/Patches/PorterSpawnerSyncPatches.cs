using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// PorterSpawner co-op (host-authoritative NPC + multi-avatar sight).
    ///
    /// Decompile: <c>Start</c> wires <see cref="InSightOfPlayer"/> callbacks and
    /// <c>checkSight(force)</c>; out-of-sight starts <c>waitToSpawn</c> which
    /// <c>AddPrefab("Characters/NPC/Porter", …)</c>. Sight used only
    /// <c>Player.Instance.isInSight</c>.
    ///
    /// Fix (no new messages, no magic ranges):
    /// 1) Client: Prefix-skip <c>Start</c> / <c>waitToSpawn</c> — only host owns
    ///    the Porter NPC; peers observe via entity snapshots.
    /// 2) Host sight: existing <c>HostInSightOfPlayerCheckSightPatch</c> +
    ///    <c>HostPlayerIdentity.AnyInSight</c> (local Player OR remote proxy FOV).
    /// </summary>
    internal static class PorterSpawnerAuth
    {
        internal static bool IsClientConnected()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.IsConnected
                && ModRuntime.Network.Role == NetworkRole.Client;
        }
    }

    /// <summary>
    /// Clients must not arm the out-of-sight spawn timer or place Porter.
    /// </summary>
    [HarmonyPatch(typeof(PorterSpawner), "Start")]
    public static class PorterSpawnerClientStartPatch
    {
        private static bool Prefix(PorterSpawner __instance)
        {
            if (!PorterSpawnerAuth.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[PorterSpawner] client skipped Start (host-authoritative porter) on "
                    + (__instance != null ? __instance.name : "?"));
            }
            return false;
        }
    }

    /// <summary>
    /// Belt-and-suspenders if Start ran before Role became Client.
    /// </summary>
    [HarmonyPatch(typeof(PorterSpawner), "waitToSpawn")]
    public static class PorterSpawnerClientWaitToSpawnPatch
    {
        private static bool Prefix(PorterSpawner __instance)
        {
            if (!PorterSpawnerAuth.IsClientConnected())
                return true;

            if (ModRuntime.VerboseLogging)
            {
                ModLog.Event(LogCat.Entity,
                    "[PorterSpawner] client skipped waitToSpawn on "
                    + (__instance != null ? __instance.name : "?"));
            }
            return false;
        }
    }
}
