using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Compressor / oxygen co-op helpers.
    /// - Empty tank acquire: fan empty tanks to peers (shared swamp gear).
    /// - Compressor convert: when any peer runs the compressor GameEvents, peers
    ///   convert local empty → full (inventory + hotbar).
    /// </summary>
    internal static class CompressorSyncHelpers
    {
        internal static bool IsGivingTank { get; set; }

        internal static void GiveTankToPlayer()
        {
            if (IsGivingTank) return;
            if (Player.Instance == null) return;

            IsGivingTank = true;
            try
            {
                if (HasItemType("oxygentank_empty"))
                {
                    ModRuntime.LegacyInfo("[CompressorSync] player already has oxygentank_empty");
                    return;
                }

                Inventory inv = Player.Instance.Inventory;
                if (inv == null) return;
                inv.addItemType("oxygentank_empty", 1);
                ModRuntime.LegacyInfo("[CompressorSync] gave oxygentank_empty to player");
            }
            finally
            {
                IsGivingTank = false;
            }
        }

        internal static void SendOxygenGiveMessage()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            // Host → all; client → host (Forwardable rebroadcasts).
            net.Broadcast(NetMessageType.OxygenTankStash,
                w => new OxygenTankStashMessage().Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        internal static void SendCompressorConvertMessage()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.Broadcast(NetMessageType.CompressorTankConvert,
                w => new CompressorTankConvertMessage().Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        internal static bool IsCompressorGameEvents(GameEvents ge)
        {
            if (ge == null) return false;
            Transform t = ge.transform;
            for (int i = 0; i < 5; i++)
            {
                if (t == null) break;
                string name = t.name.ToLowerInvariant();
                if (name.Contains("compressor")) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Convert one empty oxygen tank to full on the local player (inv or hotbar).
        /// Called on remote peers when someone uses the compressor.
        /// </summary>
        internal static void ConvertRemoteTank()
        {
            if (Player.Instance == null) return;

            if (!TryRemoveOne("oxygentank_empty"))
            {
                ModRuntime.LegacyInfo("[CompressorSync] remote player has no oxygentank_empty to convert");
                return;
            }

            Inventory inv = Player.Instance.Inventory;
            if (inv != null)
                inv.addItemType("oxygentank_full", 1);
            else if (Player.Instance.Hotbar != null)
                Player.Instance.Hotbar.addItemType("oxygentank_full", 1);

            ModRuntime.LegacyInfo("[CompressorSync] converted oxygentank_empty -> oxygentank_full on remote player");
        }

        private static bool HasItemType(string type)
        {
            if (CountInInventory(Player.Instance?.Inventory, type) > 0) return true;
            if (CountInInventory(Player.Instance?.Hotbar, type) > 0) return true;
            return false;
        }

        private static int CountInInventory(Inventory inv, string type)
        {
            if (inv == null) return 0;
            var items = inv.getAllItems(type);
            if (items == null) return 0;
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (!InvItemClass.isNull(items[i]))
                    n += items[i].amount;
            }
            return n;
        }

        private static bool TryRemoveOne(string type)
        {
            if (TryRemoveOneFrom(Player.Instance?.Inventory, type)) return true;
            if (TryRemoveOneFrom(Player.Instance?.Hotbar, type)) return true;
            return false;
        }

        private static bool TryRemoveOneFrom(Inventory inv, string type)
        {
            if (inv == null) return false;
            var items = inv.getAllItems(type);
            if (items == null) return false;
            for (int i = 0; i < items.Count; i++)
            {
                if (InvItemClass.isNull(items[i])) continue;
                items[i].removeAmount(1);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// When an empty oxygen tank enters any local inventory slot, notify peers
    /// so they also receive one (shared chapter-2 gear convenience).
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "createItem", new[] { typeof(InvItemClass), typeof(int) })]
    internal static class OxygenTankAcquirePatch
    {
        private static void Postfix(InvSlot __instance, object[] __args)
        {
            InvItemClass invItem = (InvItemClass)__args[0];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (Core.loadingGame) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (CompressorSyncHelpers.IsGivingTank) return;
            if (invItem == null || invItem.type != "oxygentank_empty") return;

            ModRuntime.LegacyInfo("[CompressorSync] oxygentank_empty entering inventory, notifying peers");
            CompressorSyncHelpers.SendOxygenGiveMessage();
        }
    }

    public static class OxygenTankStashHandler
    {
        public static void Handle()
        {
            // Runs under NetworkApplyGuard — do NOT gate on IsApplyingRemoteState.
            CompressorSyncHelpers.GiveTankToPlayer();
        }
    }

    /// <summary>
    /// Any peer that fires a compressor-related GameEvents notifies the rest
    /// so their empty tanks convert (host-only detection missed client use).
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(GameEvents), "fire")]
    internal static class CompressorConvertDetectPatch
    {
        private static void Postfix(GameEvents __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (!CompressorSyncHelpers.IsCompressorGameEvents(__instance)) return;

            ModRuntime.LegacyInfo("[CompressorSync] compressor GameEvents fired, broadcasting convert");
            CompressorSyncHelpers.SendCompressorConvertMessage();
        }
    }

    public static class CompressorTankConvertHandler
    {
        public static void Handle()
        {
            // CRITICAL: must run under NetworkApplyGuard. The previous early-out on
            // IsApplyingRemoteState meant ConvertRemoteTank never executed on peers.
            CompressorSyncHelpers.ConvertRemoteTank();
        }
    }
}
