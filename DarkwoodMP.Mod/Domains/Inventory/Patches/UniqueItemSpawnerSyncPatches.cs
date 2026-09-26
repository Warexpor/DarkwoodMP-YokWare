using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative UniqueItemSpawner (TeddyBear RNG).
    /// Vanilla: Start Invokes spawn after 3s; spawn picks a random container and
    /// createItem("TeddyBear"). Independent client rolls diverge which chest holds
    /// the bear. Clients skip spawn; host owns placement. Peers observe via existing
    /// container sync: host PlaceItem fan-out when connected, otherwise
    /// ContainerStateRequest on open (ContainerSearchedPatch) / ContainerStateSync.
    /// Spawn before handshake: Offline/Host still runs; item stays on host for later
    /// open-state sync — no new message id.
    /// </summary>
    [HarmonyPatch(typeof(UniqueItemSpawner), "spawn")]
    public static class UniqueItemSpawnerSpawnPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.Container,
                        "[UniqueItemSpawner] client skipped spawn (host-authoritative TeddyBear)");
                return false;
            }
            return true;
        }

        private static void Postfix(UniqueItemSpawner __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            Inventory teddy = __instance != null ? __instance.teddyBear : null;
            if (teddy == null || teddy.slots == null)
                return;

            int slotIdx = -1;
            InvItemClass item = null;
            for (int i = 0; i < teddy.slots.Count; i++)
            {
                InvSlot slot = teddy.slots[i];
                if (InvItemClass.isNull(slot.invItem))
                    continue;
                if (!string.Equals(slot.invItem.type, "TeddyBear", System.StringComparison.Ordinal))
                    continue;
                slotIdx = i;
                item = slot.invItem;
                break;
            }
            if (slotIdx < 0 || item == null)
                return;

            Vector3 pos = teddy.transform.position;
            if (ModRuntime.VerboseLogging)
                ModLog.Event(LogCat.Container,
                    "[UniqueItemSpawner] host fan-out PlaceItem TeddyBear slot="
                    + slotIdx + " at " + pos);

            // Existing Forwardable ContainerItem PlaceItem — no new message id.
            // isPlayerPlaced=false: world spawn, not a player put.
            ContainerSyncHelpers.SendContainerAction(
                ContainerAction.PlaceItem,
                pos,
                slotIdx,
                item.type,
                item.amount,
                item.durability,
                item.ammo,
                isPlayerPlaced: false,
                shouldBeActive: item.shouldBeActive);
        }
    }
}
