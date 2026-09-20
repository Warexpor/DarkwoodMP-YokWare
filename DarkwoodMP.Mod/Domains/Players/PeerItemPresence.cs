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

        public static void Reset()
        {
            _byPlayer.Clear();
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

        public static bool AnyPeerHas(string itemType, int minAmount)
        {
            if (string.IsNullOrEmpty(itemType)) return false;
            if (minAmount < 1) minAmount = 1;

            if (Player.Instance != null && Player.Instance.Inventory != null)
            {
                InvItemClass local = Player.Instance.Inventory.getItemInPlayer(itemType);
                if (local != null)
                {
                    bool ok = !local.baseClass.stackable || local.amount >= minAmount;
                    if (ok) return true;
                }
            }

            foreach (var kvp in _byPlayer)
            {
                if (kvp.Value != null && kvp.Value.TryGetValue(itemType, out int amt) && amt >= minAmount)
                    return true;
            }
            return false;
        }

        public static void SendLocalChange(string itemType, int amount)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (string.IsNullOrEmpty(itemType)) return;

            if (net.Role == NetworkRole.Host)
            {
                Apply(net.LocalPlayerId, itemType, amount);
                return;
            }

            var msg = new PeerHasItemMessage
            {
                PlayerId = net.LocalPlayerId,
                ItemType = itemType,
                Amount = amount
            };
            net.Send(NetMessageType.PeerHasItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        public static void SendFullLocalInventory()
        {
            if (Player.Instance == null || Player.Instance.Inventory == null) return;
            System.Collections.Generic.List<InvItemClass> items = Player.Instance.Inventory.getAllItemsInPlayer();
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                if (InvItemClass.isNull(items[i]) || string.IsNullOrEmpty(items[i].type))
                    continue;
                SendLocalChange(items[i].type, items[i].amount);
            }
        }
    }
}
