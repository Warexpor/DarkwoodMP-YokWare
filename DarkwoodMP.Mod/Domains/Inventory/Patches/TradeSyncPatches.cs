using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Shared trader assortment: shop stock is shared across peers.
    /// Reputation stays per-player (2.6) — not touched here.
    ///
    /// Live path: after a successful acceptTrade, broadcast absolute NPC inventory.
    /// Restock path: host-only randomizeTraderInv, then absolute push.
    /// Join path: host SendTradeInventoriesTo(playerId) for every trader NPC.
    ///
    /// The wire carries isRecipe + absolute durability per stack (recipes keep their
    /// recipeFor instead of collapsing to type "recipe", so client trade replies cannot
    /// poison the host stock). Empty-mag firearms (ammo=0) stay on the wire and broken
    /// items keep absolute durability 0. Workbench upgrades + shouldBeActive travel per
    /// entry for player-sold upgraded / flashlight-on items.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "acceptTrade")]
    public static class TradeSyncAcceptPatch
    {
        /// <summary>Prefix snapshot of exchangeTrader (buy tray) type → amount.</summary>
        private static readonly Dictionary<string, int> _buyTraySnapshot = new Dictionary<string, int>();
        private static TradeEntry[] _bought = System.Array.Empty<TradeEntry>(); // reset-in: Reset
        private static TradeEntry[] _sold = System.Array.Empty<TradeEntry>();   // reset-in: Reset

        public static void Reset()
        {
            _buyTraySnapshot.Clear();
            _bought = System.Array.Empty<TradeEntry>();
            _sold = System.Array.Empty<TradeEntry>();
        }

        private static void Prefix(DialogueWindow __instance)
        {
            _buyTraySnapshot.Clear();
            _bought = TradeCommit.Capture(__instance != null ? __instance.exchangeTrader : null);
            _sold = TradeCommit.Capture(__instance != null ? __instance.exchangePlayer : null);
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

            // Renew dialog lock so host auth check survives long trade sessions (>90s lease).
            var net = ModRuntime.Network;
            if (net == null) return;

            int localId = net.LocalPlayerId;
            NpcDialogueLock.RenewLease(__instance.npc, localId);

            // Host fans out authoritative stock; a client sends what it traded for the host to check.
            if (net.Role == NetworkRole.Host)
                TradeInventorySync.BroadcastNpcInventory(__instance.npc);
            else if (net is LanNetworkManager lan)
                TradeCommit.SendFromClient(lan, __instance.npc, _bought, _sold);
        }
    }

    /// <summary>
    /// Host: its own trade window closed with goods still in the buy tray. Vanilla puts them back
    /// into the stock; every snapshot sent while they sat in the tray (another player's trade, a
    /// restock) went out without them, so the others' copies are short until the next one.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.closeTrade))]
    public static class TradeCloseHostResyncPatch
    {
        private static void Prefix(DialogueWindow __instance, out NPC __state)
        {
            __state = null;
            if (__instance == null || __instance.npc == null || __instance.exchangeTrader == null)
                return;
            if (__instance.exchangeTrader.getAllItems().Count > 0)
                __state = __instance.npc;
        }

        private static void Postfix(NPC __state)
        {
            if (__state == null || !__state.trader || LanNetworkManager.IsApplyingRemoteState)
                return;
            if (!NetGuard.ConnectedHost(out _))
                return;
            TradeInventorySync.BroadcastNpcInventory(__state);
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

            var net = ModRuntime.Network;
            if (net == null) return true;

            // Clients must not independently restock — host assortment is shared.
            if (net.Role == NetworkRole.Client)
                return false;

            // A new day while someone trades with it: vanilla clears the stock under the open buy
            // tray, and closing the trade put the tray back into the new stock. Restock after.
            if (TraderRestockDefer.InTrade(__instance))
            {
                TraderRestockDefer.Add(__instance);
                return false;
            }
            return true;
        }

        private static void Postfix(NPC __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            if (!NetGuard.Host(out var net))
                return;
            if (__instance == null || !__instance.trader)
                return;

            TradeInventorySync.BroadcastNpcInventory(__instance);
        }
    }

    /// <summary>Host: trader restocks held while a player is talking or trading with that trader.</summary>
    internal static class TraderRestockDefer
    {
        private static readonly List<NPC> _waiting = new List<NPC>(4); // reset-in: Reset
        private static float _next; // reset-in: Reset

        internal static void Reset()
        {
            _waiting.Clear();
            _next = 0f;
        }

        internal static bool InTrade(NPC npc)
        {
            if (npc == null)
                return false;
            var dw = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;
            if (dw != null && dw.opened && dw.npc == npc)
                return true;
            var net = ModRuntime.Network;
            int owner = NpcDialogueLock.GetOwner(NpcRef.Of(npc));
            return owner > 0 && net != null && owner != net.LocalPlayerId;
        }

        internal static void Add(NPC npc)
        {
            if (!_waiting.Contains(npc))
                _waiting.Add(npc);
            ModRuntime.LegacyInfo($"[TradeSync] restock of '{npc.name}' waits for the trade to end");
        }

        internal static void Tick()
        {
            if (_waiting.Count == 0 || Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 1f;
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                NPC npc = _waiting[i];
                if (npc == null)
                {
                    _waiting.RemoveAt(i);
                    continue;
                }
                if (InTrade(npc))
                    continue;
                _waiting.RemoveAt(i);
                Traverse.Create(npc).Method("randomizeTraderInv").GetValue();
            }
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
                Amounts = System.Array.Empty<int>(),
                IsRecipe = System.Array.Empty<bool>(),
                Durabilities = System.Array.Empty<float>(),
                Upgrades = System.Array.Empty<string[]>(),
                ShouldBeActive = System.Array.Empty<bool>()
            };
            if (npc?.inventory == null) return msg;

            // Per-stack entries (do NOT collapse by item.type). Vanilla recipes all
            // share type "recipe" with distinct recipeFor — aggregating wiped which
            // recipes the trader sold and let client→host trade replies poison stock.
            var types = new System.Collections.Generic.List<string>(16);
            var amounts = new System.Collections.Generic.List<int>(16);
            var recipes = new System.Collections.Generic.List<bool>(16);
            var durs = new System.Collections.Generic.List<float>(16);
            var ups = new System.Collections.Generic.List<string[]>(16);
            var actives = new System.Collections.Generic.List<bool>(16);
            var items = npc.inventory.getAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                InvItemClass it = items[i];
                if (InvItemClass.isNull(it)) continue;
                bool isRecipe = it.isRecipe;
                string type = isRecipe ? it.recipeFor : it.type;
                if (string.IsNullOrEmpty(type)) continue;
                bool hasAmmo = it.baseClass != null && it.baseClass.hasAmmo;
                int amt = hasAmmo ? it.ammo : it.amount;
                // Empty-mag firearms must stay on the wire (Amounts=0), or peers never
                // see sold/restocked empty guns.
                if (amt <= 0 && !isRecipe && !hasAmmo) continue;
                if (amt < 0) amt = 0;
                if (amt <= 0 && isRecipe) amt = 1;
                types.Add(type);
                amounts.Add(amt);
                recipes.Add(isRecipe);
                durs.Add(it.durability);
                ups.Add(Sync.InvItemUpgradeWire.CollectNames(it));
                actives.Add(it.shouldBeActive);
            }

            msg.ItemCount = types.Count;
            msg.ItemTypes = types.ToArray();
            msg.Amounts = amounts.ToArray();
            msg.IsRecipe = recipes.ToArray();
            msg.Durabilities = durs.ToArray();
            msg.Upgrades = ups.ToArray();
            msg.ShouldBeActive = actives.ToArray();
            msg.InDream = NpcIsOnDreamPad(npc);
            if (npc != null)
            {
                Vector3 p = npc.transform.position;
                msg.HasPos = true;
                msg.PosX = p.x;
                msg.PosY = p.y;
                msg.PosZ = p.z;
            }
            return msg;
        }

        private static bool NpcIsOnDreamPad(NPC npc)
        {
            if (npc == null) return false;
            Transform root = DreamSyncManager.GetDreamLocationTransform();
            return root != null && npc.transform.IsChildOf(root);
        }

        public static void BroadcastNpcInventory(NPC npc)
        {
            if (npc == null || string.IsNullOrEmpty(npc.name)) return;
            if (!NetGuard.ConnectedHost(out var net)) return;

            var msg = BuildMessage(npc);
            ModRuntime.LegacyInfo(
                $"[TradeSync] inventory sync '{msg.NpcName}' types={msg.ItemCount}");
            net.SendToAll(NetMessageType.TradeInventorySync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        public static void Handle(TradeInventorySyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;

            NPC npc = FindNpcByName(msg);
            if (npc == null)
            {
                // NPC may not be streamed yet — queue for flush.
                ModRuntime.Network?.TradeHandlers?.QueuePendingTradeInventory(msg);
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
                    if (amount < 0) amount = 0;
                    bool isRecipe = msg.IsRecipe != null && i < msg.IsRecipe.Length && msg.IsRecipe[i];
                    bool hasAbsDur = msg.Durabilities != null && i < msg.Durabilities.Length;
                    float absDur = hasAbsDur ? msg.Durabilities[i] : 0f;

                    // amount==0 is empty-mag firearm only (Build keeps hasAmmo zeros).
                    if (amount <= 0 && !isRecipe)
                    {
                        InvItem def = null;
                        try
                        {
                            if (Singleton<ItemsDatabase>.Instance != null)
                                def = Singleton<ItemsDatabase>.Instance.getItem(type, instantiate: false);
                        }
                        catch { /* title/join race */ }
                        if (def == null || !def.hasAmmo)
                            continue;
                    }

                    InvSlot slot = inv.getNextFreeSlot();
                    if (slot == null) break;
                    // createItem durability arg is a 0..1 multiplier; set absolute after.
                    // hasAmmo: Amount→ammo (0 stays empty). Non-ammo never reaches here at 0.
                    InvItemClass created = slot.createItem(type, amount, 1f,
                        InvItem.ModifierQuality.none, isRecipe);
                    if (created == null) continue;
                    // Always assign absolute durability (0 = broken); createItem's
                    // default is a full bar.
                    bool hasActive = msg.ShouldBeActive != null && i < msg.ShouldBeActive.Length;
                    bool active = hasActive && msg.ShouldBeActive[i];
                    if (hasAbsDur && hasActive)
                        Sync.InvItemTransferApply.ApplyMeta(created, absDur, amount, active);
                    else
                    {
                        if (hasAbsDur)
                            created.durability = absDur;
                        if (created.baseClass != null && created.baseClass.hasAmmo)
                            created.ammo = amount;
                        if (hasActive)
                            created.shouldBeActive = active;
                    }
                    string[] upNames = msg.Upgrades != null && i < msg.Upgrades.Length
                        ? msg.Upgrades[i] : null;
                    Sync.InvItemUpgradeWire.Apply(created, upNames);
                }
            }

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            // This player's own buy tray holds goods taken out of its copy of the stock (vanilla
            // moves them across, and puts them back when the trade closes). The host's stock still
            // has them, so they come back in the snapshot: take them out again, or they were on the
            // shelf twice (a second player trading at the same time sends a snapshot mid-trade).
            if (dw != null && dw.opened && dw.npc == npc && Player.Instance != null && Player.Instance.inShop
                && dw.exchangeTrader != null)
            {
                TradeEntry[] held = TradeCommit.Capture(dw.exchangeTrader);
                for (int i = 0; i < held.Length; i++)
                    TradeCommit.RemoveFrom(inv, held[i]);
            }
            // The trade window's balance line exists only while this trader's trade is open (vanilla
            // refreshReputation reads the talked-to NPC and the exchange panes).
            if (dw != null && dw.opened && dw.npc == npc && dw.currentMenu == DialogueWindow.CurrentMenu.trade)
            {
                if (Player.Instance != null && Player.Instance.talkedToNPC != null)
                    inv.refreshReputation();
                inv.refreshIcons();
                if (dw.exchangeTrader != null)
                    dw.exchangeTrader.refreshIcons();
            }

            ModRuntime.LegacyInfo(
                $"[TradeSync] applied absolute stock '{msg.NpcName}' types={msg.ItemCount}");
        }

        /// <summary>
        /// How far a trader may stand from where the host's copy stands and still be the same one.
        /// Every hideout and camp has its own "wolfman"; the morning one at the hideout is spawned
        /// by the host and reaches a client a moment after its stock. With no limit that stock went
        /// to the closest other Wolfman (another camp), and the hideout one kept a stock of its own.
        /// </summary>
        internal const float SameTraderRadius = 400f;

        public static NPC FindNpcByName(TradeInventorySyncMessage msg)
        {
            if (msg.HasPos)
                return DialogOutcomeCloseNetHandlers.FindNpcByNameNear(
                    msg.NpcName, msg.InDream, new Vector3(msg.PosX, msg.PosY, msg.PosZ), SameTraderRadius);
            return DialogOutcomeCloseNetHandlers.FindNpcByName(msg.NpcName, msg.InDream);
        }

        public static NPC FindNpcByName(string name)
        {
            return DialogOutcomeCloseNetHandlers.FindNpcByName(name, preferDreamPad: false);
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
