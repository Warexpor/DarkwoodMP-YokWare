using System;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When any remote proxy opens inventory, destroys their held item to prevent
    /// visual desyncs and duplicate item references on the host side.
    /// </summary>
    [HarmonyPatch(typeof(Player), "getIntoInventory")]
    public static class GetIntoInventoryPatch
    {
        private static void Prefix(Player __instance)
        {
            if (!PlayerControlRouter.HasSecond)
                return;

            Player proxy = PlayerControlRouter.GetProxyByInstance(__instance);
            if (proxy == null)
                return;

            ModRuntime.LegacyInfo($"[getIntoInventory Patch] proxy {proxy.name} called. heldItem={__instance.heldItem}, invOpen={__instance.Inventory?.open}");

            if (__instance.heldItem != null)
            {
                UnityEngine.Object.Destroy(__instance.heldItem);
                __instance.heldItem = null;
            }
        }
    }

    /// <summary>
    /// Logs remote proxy inventory close events.
    /// </summary>
    [HarmonyPatch(typeof(Player), "closeInventory")]
    public static class CloseInventoryPatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance == null) return;

            // Vanilla: talkedToNPC.GetComponent<Inventory>().hide() with no null check.
            // Host world-only dialog / dangling oven NPC → NRE aborts prepareDream mid
            // prepareLocation, so DreamStarted never fans out (black void for peers).
            try
            {
                var npc = __instance.talkedToNPC;
                if (npc != null && npc.GetComponent<Inventory>() == null)
                    __instance.talkedToNPC = null;
            }
            catch { /* ignore */ }

            if (!PlayerControlRouter.HasSecond)
                return;
            Player proxy = PlayerControlRouter.GetProxyByInstance(__instance);
            if (proxy == null)
                return;
            ModRuntime.LegacyInfo($"[Proxy {proxy.name} closeInventory] called. open={__instance.Inventory?.open}, craftOpen={__instance.Crafting?.open}");
        }

        /// <summary>
        /// Swallow closeInventory NREs so OutsideLocations.prepareLocation (dream entry)
        /// can continue. Log + clear stuck dreamPrepared if we were mid-prepare.
        /// </summary>
        private static Exception Finalizer(Player __instance, Exception __exception)
        {
            if (__exception == null) return null;

            ModRuntime.Log?.LogWarning(
                "[DreamSync] closeInventory NRE swallowed (dream/location prepare): "
                + __exception.Message);

            try
            {
                if (__instance != null)
                {
                    __instance.talkedToNPC = null;
                    __instance.openedItemInventory = null;
                    __instance.openedItemInventory2 = null;
                }
            }
            catch { /* ignore */ }

            return null; // swallow — let prepareLocation finish
        }
    }

    /// <summary>
    /// If prepareDream's coroutine still dies after closeInventory harden, reset
    /// dreamPrepared + abort Starting so peers are not left waiting forever.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), "prepareLocation")]
    public static class PrepareLocationDreamRecoveryPatch
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return __exception;

            bool preparingDream = false;
            try
            {
                preparingDream = Dreams.Instance != null && Dreams.Instance.dreamPrepared
                    && !Dreams.Instance.dreaming;
            }
            catch { /* ignore */ }

            if (!preparingDream)
                return __exception;

            ModRuntime.Log?.LogError(
                "[DreamSync] prepareLocation failed during dream prepare — aborting session: "
                + __exception.Message);

            string preset = null;
            try { preset = DreamSession.PresetName; } catch { /* ignore */ }

            try
            {
                if (Dreams.Instance != null)
                    Dreams.Instance.dreamPrepared = false;
            }
            catch { /* ignore */ }

            try
            {
                DreamSession.AbortStarting("prepareLocation: " + __exception.Message);
            }
            catch { /* ignore */ }

            try
            {
                DreamSyncManager.ForceLocalDreamCleanup("prepareLocationFailed");
            }
            catch { /* ignore */ }

            // Tell peers to drop the entry wait / black void.
            try
            {
                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                {
                    string outcome = DreamSession.BuildRejectedOutcome("prepareLocationFailed");
                    net.Broadcast(NetMessageType.DreamEnded,
                        w => DreamEndedMessage.Build(preset ?? "", outcome).Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
            }
            catch { /* ignore */ }

            return null; // don't leave unhandled NRE spinning the host
        }
    }
}