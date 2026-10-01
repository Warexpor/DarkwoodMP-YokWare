using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared saw fuel / wood-log stock sync (hideout saw).
    /// Host broadcasts absolute stock; clients send addFuel / convert as delta requests that
    /// the host validates against its own stock before broadcasting the result.
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

        /// <summary>Fuel / log / wood stock captured before a local addFuel or convert.</summary>
        internal struct Stock
        {
            public bool Valid;
            public float Fuel;
            public int WoodLogs;
            public int Wood;
        }

        internal static Stock Capture(Saw saw)
        {
            if (saw == null) return default;
            SawStateMessage m = BuildMessage(saw);
            return new Stock { Valid = true, Fuel = m.Fuel, WoodLogs = m.WoodLogAmount, Wood = m.WoodAmount };
        }

        /// <summary>
        /// Host: broadcast absolute stock. Client: send only the local change as a delta request
        /// (<paramref name="fuelDelta"/> for addFuel, or the stock difference since
        /// <paramref name="before"/> for convert); the host answers with an absolute.
        /// </summary>
        internal static void SendState(Saw saw, string reason, float fuelDelta = 0f, Stock before = default)
        {
            if (saw == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            var msg = BuildMessage(saw);
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                msg.Kind = SawStateKind.Delta;
                if (before.Valid)
                {
                    msg.FuelDelta = msg.Fuel - before.Fuel;
                    msg.WoodLogDelta = msg.WoodLogAmount - before.WoodLogs;
                    msg.WoodDelta = msg.WoodAmount - before.Wood;
                }
                else
                {
                    msg.FuelDelta = fuelDelta;
                }
                if (Mathf.Abs(msg.FuelDelta) < 0.01f && msg.WoodLogDelta == 0 && msg.WoodDelta == 0)
                    return;
            }
            else
            {
                msg.Kind = SawStateKind.Absolute;
                msg.FuelDelta = 0f;
            }

            ModRuntime.Network.SendSawState(msg);
            ModRuntime.LegacyInfo($"[SawSync] send {reason} {msg.Kind} at ({msg.PosX:F1},{msg.PosZ:F1}) fuel={msg.Fuel} logs={msg.WoodLogAmount} wood={msg.WoodAmount}"
                + (msg.Kind == SawStateKind.Delta ? $" d=({msg.FuelDelta:F1},{msg.WoodLogDelta},{msg.WoodDelta})" : ""));
        }

        /// <summary>Host → one peer: absolute stock (answer to a rejected request).</summary>
        internal static void SendAbsoluteTo(Saw saw, int playerId)
        {
            if (saw == null || playerId <= 0) return;
            if (!NetGuard.ConnectedHost(out var net))
                return;
            var msg = BuildMessage(saw);
            net.SendToPlayer(playerId, NetMessageType.SawState, w => msg.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host rebroadcast after delta apply (bypasses IsApplyingRemoteState send guard).</summary>
        internal static void BroadcastAbsoluteFromHost(Saw saw, string reason)
        {
            if (saw == null) return;
            if (!NetGuard.ConnectedHost(out var net))
                return;
            var msg = BuildMessage(saw);
            net.Broadcast(NetMessageType.SawState, w => msg.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[SawSync] host-auth {reason} at ({msg.PosX:F1},{msg.PosZ:F1}) fuel={msg.Fuel}");
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
        private static void Prefix(float amount, out float __state)
        {
            __state = amount;
        }

        private static void Postfix(Saw __instance, float __state)
        {
            float delta = 0f;
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client && __state > 0.01f)
                delta = __state;
            SawSyncHelpers.SendState(__instance, "addFuel", delta);
        }
    }

    [HarmonyPatch(typeof(Saw), "convert")]
    public static class SawConvertPatch
    {
        private static void Prefix(Saw __instance, out SawSyncHelpers.Stock __state)
        {
            __state = SawSyncHelpers.Capture(__instance);
        }

        private static void Postfix(Saw __instance, SawSyncHelpers.Stock __state)
        {
            SawSyncHelpers.SendState(__instance, "convert", 0f, __state);
        }
    }
}
