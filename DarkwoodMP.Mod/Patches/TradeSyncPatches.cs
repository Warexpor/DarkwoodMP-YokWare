using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Shared trader assortment (design model C): shop stock is shared across peers.
    /// Reputation stays per-player (2.6) — not touched here.
    ///
    /// Live path: after a successful acceptTrade, broadcast absolute NPC inventory.
    /// Restock path: host-only randomizeTraderInv, then absolute push.
    /// Join path: host SendTradeInventoriesTo(playerId) for every trader NPC.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "acceptTrade")]
    public static class TradeSyncAcceptPatch
    {
        /// <summary>Prefix snapshot of exchangeTrader (buy tray) type → amount.</summary>
        private static readonly Dictionary<string, int> _buyTraySnapshot = new Dictionary<string, int>();

        public static void Reset()
        {
            _buyTraySnapshot.Clear();
        }

        private static void Prefix(DialogueWindow __instance)
        {
            _buyTraySnapshot.Clear();
            if (__instance?.exchangeTrader == null) return;

            var allItems = __instance.exchangeTrader.getAllItems();
            for (int i = 0; i < allItems.Count; i++)
            {
                if (InvItemClass.isNull(allItems[i])) continue;
                string type = allItems[i].type;
                if (string.IsNullOrEmpty(type)) continue;
                int amount = allItems[i].amount;
                if (_buyTraySnapshot.ContainsKey(type))
                    _buyTraySnapshot[type] += amount;
                else
                    _buyTraySnapshot[type] = amount;
            }
        }

        private static void Postfix(DialogueWindow __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
            {
                _buyTraySnapshot.Clear();
                return;
            }

            if (LanNetworkManager.IsApplyingRemoteState)
            {
                _buyTraySnapshot.Clear();
                return;
            }

            if (__instance?.npc == null)
            {
                _buyTraySnapshot.Clear();
                return;
            }

            // Vanilla acceptTrade is all-or-nothing: success empties both trays.
            // Failed trades (no room / not enough rep) leave items in place.
            bool buyTrayEmpty = true;
            if (__instance.exchangeTrader != null)
            {
                var remaining = __instance.exchangeTrader.getAllItems();
                buyTrayEmpty = remaining == null || remaining.Count == 0;
            }

            bool sellTrayEmpty = true;
            if (__instance.exchangePlayer != null)
            {
                var sellRem = __instance.exchangePlayer.getAllItems();
                sellTrayEmpty = sellRem == null || sellRem.Count == 0;
            }

            _buyTraySnapshot.Clear();

            if (!buyTrayEmpty || !sellTrayEmpty)
                return;

            // Absolute stock after local trade (includes sold-to-trader gains).
            TradeInventorySync.BroadcastNpcInventory(__instance.npc);
        }
    }

    /// <summary>
    /// Host is authoritative for morning / new-day trader randomization.
    /// Clients skip local randomize and wait for TradeInventorySync.
    /// </summary>
    [HarmonyPatch(typeof(NPC), "randomizeTraderInv")]
    public static class TradeRestockHostPatch
    {
        private static bool Prefix(NPC __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null) return true;

            // Clients must not independently restock — host assortment is shared.
            if (net.Role == NetworkRole.Client)
                return false;

            return true;
        }

        private static void Postfix(NPC __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            if (__instance == null || !__instance.trader)
                return;

            TradeInventorySync.BroadcastNpcInventory(__instance);
        }
    }

    /// <summary>
    /// Builds / applies absolute trader inventory snapshots.
    /// </summary>
    internal static class TradeInventorySync
    {
        public static TradeInventorySyncMessage BuildMessage(NPC npc)
        {
            var msg = new TradeInventorySyncMessage
            {
                NpcName = npc != null ? npc.name : "",
                ItemCount = 0,
                ItemTypes = System.Array.Empty<string>(),
                Amounts = System.Array.Empty<int>()
            };
            if (npc?.inventory == null) return msg;

            var totals = new Dictionary<string, int>();
            var items = npc.inventory.getAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (InvItemClass.isNull(items[i])) continue;
                string type = items[i].type;
                if (string.IsNullOrEmpty(type)) continue;
                if (totals.ContainsKey(type))
                    totals[type] += items[i].amount;
                else
                    totals[type] = items[i].amount;
            }

            msg.ItemCount = totals.Count;
            msg.ItemTypes = new string[msg.ItemCount];
            msg.Amounts = new int[msg.ItemCount];
            int idx = 0;
            foreach (var kv in totals)
            {
                msg.ItemTypes[idx] = kv.Key;
                msg.Amounts[idx] = kv.Value;
                idx++;
            }
            return msg;
        }

        public static void BroadcastNpcInventory(NPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            var msg = BuildMessage(npc);
            ModRuntime.LegacyInfo(
                $"[TradeSync] inventory sync '{msg.NpcName}' types={msg.ItemCount}");
            net.Broadcast(NetMessageType.TradeInventorySync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        public static void Handle(TradeInventorySyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;

            NPC npc = FindNpcByName(msg.NpcName);
            if (npc == null)
            {
                // NPC may not be streamed yet — queue for flush.
                LanNetworkManager.Instance?.QueuePendingTradeInventory(msg);
                return;
            }

            ApplyToNpc(npc, msg);
        }

        public static void ApplyToNpc(NPC npc, TradeInventorySyncMessage msg)
        {
            if (npc == null) return;
            Inventory inv = npc.inventory;
            if (inv == null)
            {
                ModRuntime.Log?.LogWarning($"[TradeSync] NPC '{msg.NpcName}' has no inventory");
                return;
            }

            inv.clear();

            if (msg.ItemTypes != null && msg.Amounts != null)
            {
                for (int i = 0; i < msg.ItemCount && i < msg.ItemTypes.Length; i++)
                {
                    string type = msg.ItemTypes[i];
                    if (string.IsNullOrEmpty(type)) continue;
                    int amount = i < msg.Amounts.Length ? msg.Amounts[i] : 0;
                    if (amount <= 0) continue;
                    inv.addItemType(type, amount);
                }
            }

            inv.refreshReputation();

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw != null && dw.opened && dw.npc == npc && dw.currentMenu == DialogueWindow.CurrentMenu.trade)
            {
                inv.refreshIcons();
                if (dw.exchangeTrader != null)
                    dw.exchangeTrader.refreshIcons();
            }

            ModRuntime.LegacyInfo(
                $"[TradeSync] applied absolute stock '{msg.NpcName}' types={msg.ItemCount}");
        }

        public static NPC FindNpcByName(string name)
        {
            NPC[] all = Object.FindObjectsOfType<NPC>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name)
                    return all[i];
            }
            return null;
        }
    }

    /// <summary>
    /// Legacy remove-delta handler (TradeSync). Kept for in-session safety if an old
    /// packet is in flight; primary path is TradeInventorySync absolute stock.
    /// </summary>
    internal static class TradeSyncHandler
    {
        public static void HandleTradeSync(TradeSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;
            if (msg.ItemCount <= 0 || msg.ItemTypes == null) return;

            NPC npc = TradeInventorySync.FindNpcByName(msg.NpcName);
            if (npc == null)
            {
                ModRuntime.Log?.LogWarning($"[TradeSync] NPC '{msg.NpcName}' not found locally");
                return;
            }

            Inventory npcInv = npc.inventory;
            if (npcInv == null)
            {
                ModRuntime.Log?.LogWarning($"[TradeSync] NPC '{msg.NpcName}' has no inventory");
                return;
            }

            for (int i = 0; i < msg.ItemCount && i < msg.ItemTypes.Length; i++)
            {
                string type = msg.ItemTypes[i];
                if (string.IsNullOrEmpty(type)) continue;
                int amount = msg.Amounts != null && i < msg.Amounts.Length ? msg.Amounts[i] : 1;

                InvItemClass existing = npcInv.getItem(type);
                if (!InvItemClass.isNull(existing))
                {
                    int toRemove = Mathf.Min(amount, existing.amount);
                    existing.removeAmount(toRemove);
                    ModRuntime.LegacyInfo($"[TradeSync] removed {toRemove}x {type} from {msg.NpcName}");
                }
            }

            npcInv.refreshReputation();

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw != null && dw.opened && dw.npc == npc && dw.currentMenu == DialogueWindow.CurrentMenu.trade)
                npcInv.refreshIcons();
        }
    }
}
