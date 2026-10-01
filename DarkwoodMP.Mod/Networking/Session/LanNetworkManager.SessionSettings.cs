using DWMPHorde.Config;
using DWMPHorde.Logging;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        // Host: the last settings value set broadcast to every peer. A roster tick whose current
        // values differ (party size changed on join/leave, or the host edited a gameplay setting)
        // re-broadcasts, so peers never keep stale party/friendly-fire/loot values.
        private SessionSettingsMessage _lastBroadcastSessionSettings;
        private bool _sessionSettingsBroadcastOnce;

        /// <summary>Host's own authoritative settings, as sent on the wire.</summary>
        private SessionSettingsMessage BuildSessionSettingsMessage()
        {
            return new SessionSettingsMessage
            {
                FriendlyFireEnabled = ModConfig.FriendlyFireEnabled == null || ModConfig.FriendlyFireEnabled.Value,
                DoubleItemsEnabled = ModConfig.DoubleItemsEnabled == null || ModConfig.DoubleItemsEnabled.Value,
                LootShare = (byte)ModConfig.GetLootShareMode(),
                PartyMultiplier = CoopBalance.GetPartySize(this)
            };
        }

        /// <summary>Host: send the current settings to one peer (right after its handshake is accepted).</summary>
        private void SendSessionSettingsTo(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0)
                return;
            SessionSettingsMessage msg = BuildSessionSettingsMessage();
            SendToPlayer(playerId, NetMessageType.SessionSettings, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Session,
                "SessionSettings → p" + playerId + " ff=" + msg.FriendlyFireEnabled
                + " lootShare=" + (LootShareMode)msg.LootShare + " doubleItems=" + msg.DoubleItemsEnabled
                + " party=" + msg.PartyMultiplier);
        }

        /// <summary>Host: broadcast the current settings to every peer. No-op off host.</summary>
        internal void BroadcastSessionSettings()
        {
            if (_role != NetworkRole.Host)
                return;
            SessionSettingsMessage msg = BuildSessionSettingsMessage();
            _lastBroadcastSessionSettings = msg;
            _sessionSettingsBroadcastOnce = true;
            Broadcast(NetMessageType.SessionSettings, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Session,
                "SessionSettings broadcast ff=" + msg.FriendlyFireEnabled
                + " lootShare=" + (LootShareMode)msg.LootShare + " doubleItems=" + msg.DoubleItemsEnabled
                + " party=" + msg.PartyMultiplier);
        }

        /// <summary>Host roster tick: re-broadcast only when something a peer applies has changed.</summary>
        private void BroadcastSessionSettingsIfChanged()
        {
            if (_role != NetworkRole.Host)
                return;
            SessionSettingsMessage cur = BuildSessionSettingsMessage();
            SessionSettingsMessage last = _lastBroadcastSessionSettings;
            if (_sessionSettingsBroadcastOnce
                && cur.FriendlyFireEnabled == last.FriendlyFireEnabled
                && cur.LootShare == last.LootShare
                && cur.DoubleItemsEnabled == last.DoubleItemsEnabled
                && cur.PartyMultiplier == last.PartyMultiplier)
                return;
            BroadcastSessionSettings();
        }

        /// <summary>Client: adopt the host's settings. Only the host may send them.</summary>
        internal void HandleSessionSettings(SessionSettingsMessage msg)
        {
            if (_role != NetworkRole.Client)
                return;
            int hostId = _hostPlayerId > 0 ? _hostPlayerId : 1;
            if (_currentReceivePlayerId != hostId)
                return;
            if (!System.Enum.IsDefined(typeof(LootShareMode), (int)msg.LootShare))
            {
                ModLog.Warn(LogCat.Session, "SessionSettings ignored — unknown LootShare " + msg.LootShare);
                return;
            }
            SessionSettings.ApplyFromHost(msg.FriendlyFireEnabled, (LootShareMode)msg.LootShare,
                msg.DoubleItemsEnabled, msg.PartyMultiplier);
            ModLog.Event(LogCat.Session,
                "SessionSettings from host ff=" + msg.FriendlyFireEnabled
                + " lootShare=" + (LootShareMode)msg.LootShare + " doubleItems=" + msg.DoubleItemsEnabled
                + " party=" + msg.PartyMultiplier);
        }
    }
}
