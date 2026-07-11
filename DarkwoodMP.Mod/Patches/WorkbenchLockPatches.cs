using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Exclusive workbench open — one player crafting at a time (host-auth).
    /// Claim after open confirms; release on Workbench.close / inventory close.
    /// </summary>
    [HarmonyPatch(typeof(Workbench), "open")]
    public static class WorkbenchLockOpenPatch
    {
        private static bool Prefix(Workbench __instance, ref bool __state)
        {
            __state = false;
            if (__instance == null) return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;

            string key = WorkbenchOpenLock.KeyFor(__instance);
            int localId = net.LocalPlayerId;

            if (net.Role == NetworkRole.Host)
            {
                if (!WorkbenchOpenLock.HostTryGrant(net, key, localId))
                {
                    BlockMessage();
                    return false;
                }
                __state = true;
                return true;
            }

            if (WorkbenchOpenLock.IsLockedByOther(key, localId))
            {
                BlockMessage();
                return false;
            }

            net.Send(NetMessageType.WorkbenchLock,
                w => new WorkbenchLockMessage
                {
                    WorkbenchKey = key,
                    OwnerPlayerId = localId,
                    Granted = false,
                    Release = false,
                    IsRequest = true
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            WorkbenchOpenLock.TryAcquire(key, localId);
            __state = true;
            return true;
        }

        private static void Postfix(Workbench __instance, bool __state)
        {
            if (!__state || __instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var player = Player.Instance;
            if (player != null && player.openedItemInventory != null)
                return; // open succeeded; keep claim

            // Open refused (busy etc.) — drop claim.
            WorkbenchLockHelpers.ReleaseLocal(__instance);
        }

        private static void BlockMessage()
        {
            try
            {
                Player.Instance?.displayMessage("Someone is already using the workbench…");
            }
            catch { /* ignore */ }
        }
    }

    [HarmonyPatch(typeof(Workbench), "close")]
    public static class WorkbenchLockClosePatch
    {
        private static void Prefix(Workbench __instance)
        {
            WorkbenchLockHelpers.ReleaseLocal(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), "closeOpenedItemInventory")]
    public static class WorkbenchLockInventoryClosePatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance == null) return;
            Inventory inv = __instance.openedItemInventory;
            if (inv == null) return;

            Workbench wb = inv.GetComponent<Workbench>();
            if (wb == null)
                wb = inv.transform.parent?.GetComponent<Workbench>();
            if (wb == null) return;

            WorkbenchLockHelpers.ReleaseLocal(wb);
        }
    }

    internal static class WorkbenchLockHelpers
    {
        internal static void ReleaseLocal(Workbench wb)
        {
            if (wb == null) return;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            string key = WorkbenchOpenLock.KeyFor(wb);
            int localId = net.LocalPlayerId;
            if (WorkbenchOpenLock.GetOwner(key) != localId)
                return;

            if (net.Role == NetworkRole.Host)
                WorkbenchOpenLock.HostRelease(net, key, localId);
            else
            {
                WorkbenchOpenLock.Release(key, localId);
                net.Send(NetMessageType.WorkbenchLock,
                    w => new WorkbenchLockMessage
                    {
                        WorkbenchKey = key,
                        OwnerPlayerId = localId,
                        Granted = true,
                        Release = true,
                        IsRequest = false
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
