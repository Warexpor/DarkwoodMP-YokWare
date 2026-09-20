using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared saw fuel / wood-log stock sync (hideout saw).
    /// Absolute snapshot on addFuel + convert; Forwardable Broadcast for 3+.
    /// </summary>
    internal static class SawSyncHelpers
    {
        internal static SawStateMessage BuildMessage(Saw saw)
        {
            Vector3 p = saw.transform.position;
            int woodLogAmount = 0, woodAmount = 0;
            Inventory inv = GetInventory(saw);
            if (inv != null)
            {
                var logItem = inv.getItem("woodLog");
                if (!InvItemClass.isNull(logItem)) woodLogAmount = logItem.amount;
                var woodItem = inv.getItem("wood");
                if (!InvItemClass.isNull(woodItem)) woodAmount = woodItem.amount;
            }

            return new SawStateMessage
            {
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                Fuel = saw.fuel,
                WoodLogAmount = woodLogAmount,
                WoodAmount = woodAmount
            };
        }

        internal static void SendState(Saw saw, string reason)
        {
            if (saw == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            var msg = BuildMessage(saw);
            ModRuntime.Network.SendSawState(msg);
            ModRuntime.LegacyInfo($"[SawSync] send {reason} at ({msg.PosX:F1},{msg.PosZ:F1}) fuel={msg.Fuel} logs={msg.WoodLogAmount} wood={msg.WoodAmount}");
        }

        internal static Inventory GetInventory(Saw saw)
        {
            if (saw == null) return null;
            // Prefer component; field is private and set in Start.
            Inventory inv = saw.GetComponent<Inventory>();
            if (inv != null) return inv;
            return Traverse.Create(saw).Field("inventory").GetValue<Inventory>();
        }
    }

    [HarmonyPatch(typeof(Saw), "addFuel")]
    public static class SawAddFuelPatch
    {
        private static void Postfix(Saw __instance)
        {
            SawSyncHelpers.SendState(__instance, "addFuel");
        }
    }

    [HarmonyPatch(typeof(Saw), "convert")]
    public static class SawConvertPatch
    {
        private static void Postfix(Saw __instance)
        {
            SawSyncHelpers.SendState(__instance, "convert");
        }
    }
}
