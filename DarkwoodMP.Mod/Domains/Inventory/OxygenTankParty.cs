using DWMPHorde.Networking;
using LiteNetLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The chapter 2 oxygen tank is every player's. Vanilla has one: an empty tank (the Elephants'
    /// talk or body, the mask family's shed), filled at the hideout 5 compressor (which fills only
    /// its user's), and a full tank in the bag is what lets a player dive (the mi17 hole, the burned
    /// cottage pond, the village cellar passages). One tank in a party left everyone else stuck at
    /// the water once its holder was away or offline. The host keeps the best tank anyone has held
    /// this session (1 empty, 2 full) and sends it; each machine tops its own bag up to it once (a
    /// missing tank is added, empty ones are filled), and again after a backup restore replaces
    /// the bag. A tank dropped or stashed later is not handed out again.
    /// </summary>
    internal static class OxygenTankParty
    {
        internal const string Empty = "oxygenTank_empty";
        internal const string Full = "oxygenTank_full";

        private static int _partyTier; // reset-in: Reset
        private static int _toppedUpTo; // reset-in: Reset
        private static float _nextTick; // reset-in: Reset

        public static void Reset()
        {
            _partyTier = 0;
            _toppedUpTo = 0;
            _nextTick = 0f;
        }

        /// <summary>Client: a backup restore replaced this player's bag; check it against the party again.</summary>
        internal static void OnBagRestored() => _toppedUpTo = 0;

        /// <summary>Host: the party's tank for a joiner.</summary>
        internal static void SendTierTo(LanNetworkManager net, int playerId)
        {
            if (_partyTier <= 0)
                return;
            var msg = new OxygenTankTierMessage { Tier = (byte)_partyTier };
            net.SendToPlayer(playerId, NetMessageType.OxygenTankTier, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal static void HandleTier(LanNetworkManager net, OxygenTankTierMessage msg)
        {
            if (net.Role != NetworkRole.Client)
                return;
            if (msg.Tier > _partyTier)
                _partyTier = msg.Tier;
        }

        internal static void Tick(LanNetworkManager net)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextTick)
                return;
            _nextTick = now + 1f;
            if (net == null || !net.IsConnected)
                return;
            if (net.Role == NetworkRole.Host)
            {
                int seen = BagReady() ? LocalTier() : 0;
                if (PeerItemPresence.AnyRemoteHas(Full))
                    seen = 2;
                else if (seen < 1 && PeerItemPresence.AnyRemoteHas(Empty))
                    seen = 1;
                if (seen > _partyTier)
                {
                    _partyTier = seen;
                    var msg = new OxygenTankTierMessage { Tier = (byte)_partyTier };
                    net.SendToAll(NetMessageType.OxygenTankTier, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo($"[OxygenTank] party tank is now {(_partyTier == 2 ? "full" : "empty")}");
                }
            }
            TopUp(net);
        }

        private static void TopUp(LanNetworkManager net)
        {
            if (_partyTier <= 0 || _toppedUpTo >= _partyTier || !BagReady())
                return;
            if (net.Role == NetworkRole.Client && !LanNetworkManager.ClientCanApplyWorldBulk())
                return;
            int have = LocalTier();
            if (have < _partyTier)
            {
                Inventory inv = Player.Instance.Inventory;
                if (_partyTier == 2 && have == 1)
                {
                    int n = inv.removeItemAmountFromPlayer(Empty, 999);
                    for (int i = 0; i < n; i++)
                        inv.addItemTypeToPlayer(Full, 1, dropIfNoRoom: true);
                    ModRuntime.LegacyInfo($"[OxygenTank] filled {n} empty tank(s): the party has a full one");
                }
                else
                {
                    string type = _partyTier == 2 ? Full : Empty;
                    inv.addItemTypeToPlayer(type, 1, dropIfNoRoom: true);
                    ModRuntime.LegacyInfo($"[OxygenTank] added {type}: the party has one");
                }
            }
            _toppedUpTo = _partyTier;
        }

        /// <summary>This player's own bag can be read and changed: in the world, alive, not in a dream or the prologue.</summary>
        private static bool BagReady()
        {
            Player p = Player.Instance;
            if (p == null || p.Inventory == null || p.Hotbar == null || !p.alive)
                return false;
            if (Core.mainMenu || Core.loadingGame || PersonalPrologue.LocalInPrologue)
                return false;
            if (DreamSyncManager.IsDreamActive || (Dreams.Instance != null && (Dreams.Instance.dreaming || Dreams.Instance.dreamPrepared)))
                return false;
            return true;
        }

        /// <summary>The best tank in this player's bag and hotbar (0 none, 1 empty, 2 full).</summary>
        private static int LocalTier()
        {
            Player p = Player.Instance;
            if (p.Inventory.getItemAmount(Full) + p.Hotbar.getItemAmount(Full) > 0)
                return 2;
            if (p.Inventory.getItemAmount(Empty) + p.Hotbar.getItemAmount(Empty) > 0)
                return 1;
            return 0;
        }
    }
}
