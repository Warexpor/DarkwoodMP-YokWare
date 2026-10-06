using DWMPHorde.Networking;
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

    /// <summary>
    /// Harmony patch: deregisters characters from the tracker on destroy. On the host, a body
    /// destroyed in play is also removed on peers: only <c>removeMe</c> used to say so, and vanilla
    /// destroys creatures and NPCs directly in many places (a story step replacing a character, the
    /// morning trader, porter and wolf despawns, old corpses cleared on entry, the spawner's far
    /// despawn). Peers kept the last pose of a body the host no longer had.
    /// </summary>
    [HarmonyPatch(typeof(Character), "OnDestroy")]
    public static class CharacterDestroyPatch
    {
        private static void Prefix(Character __instance)
        {
            HostSendDespawn(__instance);
            CharacterTracker.Remove(__instance);
        }

        private static void HostSendDespawn(Character c)
        {
            // Scene loads and quits clear coreStarted before their teardown; a save load runs under
            // loadingGame. Neither is a body leaving the world.
            if (!Core.coreStarted || Core.loadingGame || !Core.worldGenFinished())
                return;
            if (!NetGuard.ConnectedHost(out LanNetworkManager net))
                return;
            if (!CharacterTracker.TryGetStableId(c, out short id) || id == 0)
                return;
            net.SendEntityDespawn(id);
        }
    }
}
