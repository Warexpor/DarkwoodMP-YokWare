using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Exclusive workbench open — one player crafting at a time (host-auth).
    /// Claim on open; release on Inventory.hide (Workbench.close is empty in vanilla).
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

            // Optimistic local claim until host deny arrives.
            WorkbenchOpenLock.TryAcquire(key, localId);
            __state = true;
            return true;
        }

        private static void Postfix(Workbench __instance, bool __state)
        {
            if (!__state || __instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var player = Player.Instance;
            // open() always calls initiateOpenCloseInventory(workbenchInventory).
            if (player != null && player.openedItemInventory != null
                && (player.openedItemInventory.isWorkbench
                    || player.openedItemInventory.workbench == __instance))
                return; // open succeeded; keep claim

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

    /// <summary>
    /// Vanilla Workbench.close() is empty — real teardown is Inventory.hide when isWorkbench.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "hide")]
    public static class WorkbenchLockInventoryHidePatch
    {
        private static void Prefix(Inventory __instance)
        {
            if (__instance == null || !__instance.open) return;
            Workbench wb = ResolveWorkbench(__instance);
            if (wb == null) return;
            WorkbenchLockHelpers.ReleaseLocal(wb);
        }

        internal static Workbench ResolveWorkbench(Inventory inv)
        {
            if (inv == null) return null;
            // open() sets workbenchInventory.workbench = this — primary path.
            if (inv.workbench != null)
                return inv.workbench;
            if (!inv.isWorkbench) return null;
            Workbench wb = inv.GetComponent<Workbench>();
            if (wb != null) return wb;
            if (inv.transform.parent != null)
            {
                wb = inv.transform.parent.GetComponent<Workbench>();
                if (wb != null) return wb;
                wb = inv.transform.parent.GetComponentInParent<Workbench>();
            }
            return wb;
        }
    }

    [HarmonyPatch(typeof(Player), "closeInventory")]
    public static class WorkbenchLockPlayerCloseInventoryPatch
    {
        private static void Prefix(Player __instance)
        {
            if (__instance == null) return;
            // Capture before hide() nulls openedItemInventory.
            Inventory inv = __instance.openedItemInventory;
            Workbench wb = WorkbenchLockInventoryHidePatch.ResolveWorkbench(inv);
            if (wb == null && __instance.openedItemInventory2 != null)
            {
                // Storage half of workbench pair — walk sibling / parent for Workbench.
                wb = __instance.openedItemInventory2.GetComponentInParent<Workbench>();
            }
            if (wb != null)
                WorkbenchLockHelpers.ReleaseLocal(wb);
        }
    }

    [HarmonyPatch(typeof(Workbench), "close")]
    public static class WorkbenchLockClosePatch
    {
        private static void Prefix(Workbench __instance)
        {
            // Empty in vanilla but may be called by mods / deny path.
            WorkbenchLockHelpers.ReleaseLocal(__instance);
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
            int owner = WorkbenchOpenLock.GetOwner(key);
            // Allow release if we own, or host force-clearing a dead lock for self.
            if (owner >= 0 && owner != localId)
                return;

            // Even if local dict already empty, client must tell host (stale host hold).
            if (net.Role == NetworkRole.Host)
            {
                if (owner == localId || owner < 0)
                    WorkbenchOpenLock.HostRelease(net, key, localId);
            }
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
                ModLog.Event(LogCat.Session, $"[WorkbenchLock] client release TX key={key} owner={localId}");
            }
        }
    }
}
