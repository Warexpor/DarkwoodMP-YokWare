using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A client's finished trade. The client used to send its whole local copy of the trader's
    /// stock and the host took it as the truth: a stale or empty copy (a trader whose stock had
    /// not reached the client yet) wiped the real stock for everyone, and a refused copy left the
    /// client holding what it bought while the stock reverted (a duplicate).
    /// Now the client sends what it bought and sold; the host checks the purchases against its own
    /// stock, applies the trade and sends the stock to the others, or refuses and the client
    /// gives the purchases back and gets its goods back.
    /// </summary>
    internal static class TradeCommit
    {
        internal static TradeEntry[] Capture(Inventory tray)
        {
            if (tray == null)
                return System.Array.Empty<TradeEntry>();
            var list = new List<TradeEntry>(8);
            foreach (InvItemClass it in tray.getAllItems())
            {
                if (InvItemClass.isNull(it))
                    continue;
                bool isRecipe = it.isRecipe;
                string type = isRecipe ? it.recipeFor : it.type;
                if (string.IsNullOrEmpty(type))
                    continue;
                list.Add(new TradeEntry
                {
                    Type = type,
                    IsRecipe = isRecipe,
                    Count = it.amount > 0 ? it.amount : 1,
                    Durability = it.durability,
                    Ammo = it.ammo,
                    Active = it.shouldBeActive,
                    Upgrades = InvItemUpgradeWire.CollectNames(it)
                });
            }
            return list.ToArray();
        }

        internal static void SendFromClient(LanNetworkManager net, NPC npc, TradeEntry[] bought, TradeEntry[] sold)
        {
            Vector3 p = npc.transform.position;
            Transform pad = DreamSyncManager.GetDreamLocationTransform();
            var msg = new TradeCommitMessage
            {
                NpcName = npc.name,
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                InDream = pad != null && npc.transform.IsChildOf(pad),
                Bought = bought,
                Sold = sold
            };
            net.Send(NetMessageType.TradeCommit, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[TradeSync] trade → host '{npc.name}' bought={bought.Length} sold={sold.Length}");
        }

        internal static void Handle(LanNetworkManager net, TradeCommitMessage msg)
        {
            if (net.Role == NetworkRole.Host)
                HostApply(net, msg);
            else if (msg.Denied)
                ClientUndo(msg);
        }

        private static void HostApply(LanNetworkManager net, TradeCommitMessage msg)
        {
            int sender = net.CurrentReceivePlayerId;
            if (sender <= 0 || string.IsNullOrEmpty(msg.NpcName))
                return;
            // Each hideout's morning trader is "nightTrader": the one at the sender's spot.
            var trader = new NpcRef
            {
                Name = msg.NpcName,
                HasPos = true,
                Pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ),
                Dream = msg.InDream
            };
            NpcDialogueLock.HostRenewLeaseForSender(trader, sender);
            NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByNameNear(
                msg.NpcName, msg.InDream, trader.Pos, TradeInventorySync.SameTraderRadius, wake: false);
            Inventory stock = npc != null ? npc.inventory : null;
            string why = null;
            if (stock == null)
                why = "trader not found";
            else if (!DialogMirror.HostInTalk(NpcRef.Of(npc), sender))
                why = "not in a talk with it";
            else if (!StockHas(stock, msg.Bought))
                why = "stock lacks the purchase";

            if (why != null)
            {
                ModRuntime.LegacyInfo($"[TradeSync] refused p{sender}'s trade with '{msg.NpcName}': {why}");
                var deny = msg;
                deny.Denied = true;
                net.SendToPlayer(sender, NetMessageType.TradeCommit, w => deny.Serialize(w), DeliveryMethod.ReliableOrdered);
                if (npc != null)
                {
                    var truth = TradeInventorySync.BuildMessage(npc);
                    net.SendToPlayer(sender, NetMessageType.TradeInventorySync, w => truth.Serialize(w), DeliveryMethod.ReliableOrdered);
                }
                return;
            }

            for (int i = 0; i < msg.Bought.Length; i++)
                RemoveFrom(stock, msg.Bought[i]);
            for (int i = 0; i < msg.Sold.Length; i++)
            {
                InvSlot slot = stock.getNextFreeSlot();
                if (slot == null)
                    break;
                Create(slot, msg.Sold[i]);
            }
            try { stock.refreshReputation(); }
            catch { /* reputation UI not up */ }
            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw != null && dw.opened && dw.npc == npc && dw.currentMenu == DialogueWindow.CurrentMenu.trade)
                stock.refreshIcons();

            // The trader's copy on the trading client already shows this result; sending it back
            // rebuilt its stock under a trade window that may hold a new tray.
            var after = TradeInventorySync.BuildMessage(npc);
            net.SendToAllExcept(sender, NetMessageType.TradeInventorySync, w => after.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[TradeSync] applied p{sender}'s trade with '{msg.NpcName}'");
        }

        private static bool StockHas(Inventory stock, TradeEntry[] wanted)
        {
            var need = new Dictionary<string, int>();
            for (int i = 0; i < wanted.Length; i++)
            {
                string key = (wanted[i].IsRecipe ? "r:" : "i:") + wanted[i].Type;
                need.TryGetValue(key, out int n);
                need[key] = n + wanted[i].Count;
            }
            foreach (var kv in need)
            {
                bool recipe = kv.Key[0] == 'r';
                string type = kv.Key.Substring(2);
                int have = 0;
                for (int s = 0; s < stock.slots.Count; s++)
                {
                    InvItemClass it = stock.slots[s].invItem;
                    if (ContainerSyncHelpers.ItemTypeMatchesWire(it, type, recipe))
                        have += it.amount > 0 ? it.amount : 1;
                }
                if (have < kv.Value)
                    return false;
            }
            return true;
        }

        internal static void RemoveFrom(Inventory inv, TradeEntry e)
        {
            int left = e.Count;
            // First the stack that matches exactly (same wear and magazine), then any.
            for (int pass = 0; pass < 2 && left > 0; pass++)
            {
                for (int s = inv.slots.Count - 1; s >= 0 && left > 0; s--)
                {
                    InvItemClass it = inv.slots[s].invItem;
                    if (!ContainerSyncHelpers.ItemTypeMatchesWire(it, e.Type, e.IsRecipe))
                        continue;
                    if (pass == 0 && !(Mathf.Abs(it.durability - e.Durability) < 0.001f && it.ammo == e.Ammo))
                        continue;
                    int take = Mathf.Min(it.amount > 0 ? it.amount : 1, left);
                    left -= take;
                    if (take >= it.amount)
                        inv.slots[s].removeItem();
                    else
                        it.removeAmount(take);
                }
            }
        }

        private static InvItemClass Create(InvSlot slot, TradeEntry e)
        {
            InvItem def = Singleton<ItemsDatabase>.Instance != null
                ? Singleton<ItemsDatabase>.Instance.getItem(e.Type, instantiate: false)
                : null;
            bool hasAmmo = def != null && def.hasAmmo;
            InvItemClass created = slot.createItem(e.Type, hasAmmo ? e.Ammo : e.Count, 1f, InvItem.ModifierQuality.none, e.IsRecipe);
            if (InvItemClass.isNull(created))
                return null;
            InvItemTransferApply.ApplyMeta(created, e.Durability, e.Ammo, e.Active);
            InvItemUpgradeWire.Apply(created, e.Upgrades);
            return created;
        }

        /// <summary>The host refused: give the purchases back and take the traded goods back.</summary>
        private static void ClientUndo(TradeCommitMessage msg)
        {
            Player p = Player.Instance;
            if (p == null)
                return;
            for (int i = 0; i < msg.Bought.Length; i++)
            {
                TradeEntry e = msg.Bought[i];
                ContainerSyncHelpers.RemoveGrantedFromPlayer(e.Type, e.IsRecipe, e.Count, e.Durability, e.Ammo);
            }
            for (int i = 0; i < msg.Sold.Length; i++)
            {
                InvSlot slot = p.Inventory.getNextFreeSlotInPlayer();
                if (slot == null)
                {
                    ModRuntime.Log?.LogWarning("[TradeSync] refused trade: no room to give back " + msg.Sold[i].Type);
                    continue;
                }
                Create(slot, msg.Sold[i]);
            }
            p.Inventory.refreshIcons();
            p.Hotbar.refreshIcons();
            ModRuntime.LegacyInfo($"[TradeSync] host refused the trade with '{msg.NpcName}' — undone");
        }
    }
}
