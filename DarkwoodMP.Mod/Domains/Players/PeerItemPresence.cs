using System.Collections.Generic;
using DWMPHorde.Networking;
using LiteNetLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host-side bag presence for remotes (not a full inventory replica).
    /// EventTrigger haveItem ORs this with the host bag.
    /// </summary>
    public static class PeerItemPresence
    {
        private static readonly Dictionary<int, Dictionary<string, int>> _byPlayer =
            new Dictionary<int, Dictionary<string, int>>();

        private static readonly Dictionary<string, int> _lastSent = new Dictionary<string, int>(); // reset-in: Reset
        private static readonly Dictionary<string, int> _scratch = new Dictionary<string, int>(); // process-scoped: scratch, cleared before each use
        private static readonly List<string> _gone = new List<string>(); // process-scoped: scratch, cleared before each use
        private static float _nextSweep; // reset-in: Reset

        public static void Reset()
        {
            _byPlayer.Clear();
            _lastSent.Clear();
            _nextSweep = 0f;
        }

        /// <summary>
        /// Client, once a second: the whole bag and hotbar against what the host was last told,
        /// sending every change (a type that is gone goes out as 0). The event hooks only cover
        /// grants and removals; drags, chest moves, drops and death left the host's view stale,
        /// so a "has item" story trigger could fire with nobody holding it, or never fire.
        /// </summary>
        internal static void Tick(LanNetworkManager net)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextSweep)
                return;
            _nextSweep = now + 1f;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client || Player.Instance == null || Core.loadingGame)
                return;

            _scratch.Clear();
            Count(Player.Instance.Inventory);
            Count(Player.Instance.Hotbar);

            foreach (var kv in _scratch)
            {
                if (!_lastSent.TryGetValue(kv.Key, out int sent) || sent != kv.Value)
                    SendLocalChange(kv.Key, kv.Value);
            }
            _gone.Clear();
            foreach (var kv in _lastSent)
            {
                if (!_scratch.ContainsKey(kv.Key))
                    _gone.Add(kv.Key);
            }
            for (int i = 0; i < _gone.Count; i++)
                SendLocalChange(_gone[i], 0);
        }

        private static void Count(Inventory inv)
        {
            if (inv == null || inv.slots == null)
                return;
            for (int i = 0; i < inv.slots.Count; i++)
            {
                InvItemClass it = inv.slots[i] != null ? inv.slots[i].invItem : null;
                if (InvItemClass.isNull(it) || string.IsNullOrEmpty(it.type))
                    continue;
                _scratch.TryGetValue(it.type, out int n);
                _scratch[it.type] = n + it.amount;
            }
        }

        /// <summary>Drop presence for a disconnected peer (avoids ghost haveItem after leave).</summary>
        public static void ClearPlayer(int playerId)
        {
            if (playerId > 0)
                _byPlayer.Remove(playerId);
        }

        public static void Apply(int playerId, string itemType, int amount)
        {
            if (playerId < 0 || string.IsNullOrEmpty(itemType)) return;
            if (!_byPlayer.TryGetValue(playerId, out Dictionary<string, int> map))
            {
                map = new Dictionary<string, int>();
                _byPlayer[playerId] = map;
            }
            if (amount <= 0)
                map.Remove(itemType);
            else
                map[itemType] = amount;
        }

        /// <summary>The player carries anything in bag or hotbar (vanilla getAllItemsInPlayer().Count &gt; 0).</summary>
        public static bool PlayerHasAnyItem(int playerId)
        {
            if (_byPlayer.TryGetValue(playerId, out Dictionary<string, int> map) && map != null)
            {
                foreach (var kv in map)
                {
                    if (kv.Value > 0)
                        return true;
                }
            }
            return false;
        }

        public static bool AnyPeerHas(string itemType, int minAmount)
        {
            if (string.IsNullOrEmpty(itemType)) return false;
            if (minAmount < 1) minAmount = 1;

            if (LocalHasIncludingHotbar(itemType, minAmount))
                return true;

            foreach (var kvp in _byPlayer)
            {
                if (kvp.Value != null && kvp.Value.TryGetValue(itemType, out int amt) && amt >= minAmount)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Inventory + Hotbar. getItemInPlayer / getItemAmount on Inventory miss Hotbar
        /// (compressor / walkie already check both). Unique haveItem EventTriggers softlock
        /// 3p when the only holder keeps oxygentank_full / keys on the hotbar.
        /// </summary>
        private static bool LocalHasIncludingHotbar(string itemType, int minAmount)
        {
            int n = CountLocalCombined(itemType);
            return n >= minAmount;
        }

        public static void SendLocalChange(string itemType, int amount)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (!NetGuard.Connected(out var net)) return;
            if (string.IsNullOrEmpty(itemType)) return;

            // Always publish inv+hotbar total — Hotbar and Inventory write separately;
            // a hotbar-only stamp must not wipe an inventory count (and vice versa).
            int combined = CountLocalCombined(itemType);
            if (combined >= 0)
                amount = combined;

            if (net.Role == NetworkRole.Host)
            {
                Apply(net.LocalPlayerId, itemType, amount);
                return;
            }

            if (amount > 0)
                _lastSent[itemType] = amount;
            else
                _lastSent.Remove(itemType);
            var msg = new PeerHasItemMessage
            {
                PlayerId = net.LocalPlayerId,
                ItemType = itemType,
                Amount = amount
            };
            net.Send(NetMessageType.PeerHasItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Sum Inventory + Hotbar counts for type; -1 if player missing.</summary>
        private static int CountLocalCombined(string itemType)
        {
            if (Player.Instance == null || string.IsNullOrEmpty(itemType))
                return -1;
            int total = 0;
            try
            {
                if (Player.Instance.Inventory != null)
                    total += Player.Instance.Inventory.getItemAmount(itemType);
                if (Player.Instance.Hotbar != null)
                    total += Player.Instance.Hotbar.getItemAmount(itemType);
            }
            catch
            {
                return -1;
            }
            return total;
        }

        public static void SendFullLocalInventory()
        {
            if (Player.Instance == null) return;
            if (Player.Instance.Inventory != null)
            {
                System.Collections.Generic.List<InvItemClass> items =
                    Player.Instance.Inventory.getAllItemsInPlayer();
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (InvItemClass.isNull(items[i]) || string.IsNullOrEmpty(items[i].type))
                            continue;
                        SendLocalChange(items[i].type, items[i].amount);
                    }
                }
            }
            // Hotbar is a separate Inventory — unique tanks/keys often live here.
            SendHotbarPresence();
        }

        private static void SendHotbarPresence()
        {
            try
            {
                Inventory hotbar = Player.Instance != null ? Player.Instance.Hotbar : null;
                if (hotbar == null || hotbar.slots == null) return;
                for (int i = 0; i < hotbar.slots.Count; i++)
                {
                    var slot = hotbar.slots[i];
                    if (slot == null || InvItemClass.isNull(slot.invItem)
                        || string.IsNullOrEmpty(slot.invItem.type))
                        continue;
                    // Prefer total across hotbar for stackables.
                    int amt = hotbar.getItemAmount(slot.invItem.type);
                    SendLocalChange(slot.invItem.type, amt);
                }
            }
            catch
            {
                /* hotbar mid-teardown */
            }
        }
    }
}
