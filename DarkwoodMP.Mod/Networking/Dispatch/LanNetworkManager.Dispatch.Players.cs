using System;
using System.IO;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        /// <summary>Inbound handlers: Players.</summary>
        private void RegisterPlayersHandlers()
        {
            On(NetMessageType.PlayerState, PlayerStateMessage.Deserialize, m => PlayerStateHandlers.HandlePlayerState(m));
            On(NetMessageType.PlayerSound, PlayerSoundMessage.Deserialize, m => WorldProxyEffectHandlers.HandlePlayerSound(m));
            On(NetMessageType.PlayerScare, PlayerScareMessage.Deserialize, m => WorldProxyEffectHandlers.HandlePlayerScare(m));
            On(NetMessageType.PlayerEffectSync, PlayerEffectSyncMessage.Deserialize, m => WorldProxyEffectHandlers.HandlePlayerEffectSync(m));
            OnRaw(NetMessageType.DragSync, payload =>
            {
                var drag = DragSyncMessage.Deserialize(new NetReader(payload));
                bool hostStamps = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
                if (hostStamps)
                    drag.ClaimedByPlayerId = _currentReceivePlayerId;
                PlayerInteractHandlers.HandleDragSync(drag);
                // Relay the host-stamped body: the raw payload's client-claimed ClaimedByPlayerId
                // would let one player claim, steal or release another's drag.
                if (hostStamps)
                    RelayStamped(w => drag.Serialize(w));
            });
            On(NetMessageType.PlayerLightState, PlayerLightStateMessage.Deserialize, m => PlayerLightFxApplyHandlers.HandlePlayerLightState(m));
            On(NetMessageType.PlayerAudio, PlayerAudioMessage.Deserialize, m => WorldFxHandlers.HandlePlayerAudio(m));
            On(NetMessageType.PlayerAnimation, PlayerAnimationMessage.Deserialize, m => PlayerFXHandlers.HandlePlayerAnimation(m));
            On(NetMessageType.PlayerAnimLibrary, PlayerAnimLibraryMessage.Deserialize, m => PlayerFXHandlers.HandlePlayerAnimLibrary(m));
            On(NetMessageType.BulletImpact, BulletImpactMessage.Deserialize, m => PlayerFXHandlers.HandleBulletImpact(m));
            On(NetMessageType.PlayerFiredWeapon, PlayerFiredWeaponMessage.Deserialize, m => PlayerFXHandlers.HandlePlayerFiredWeapon(m));
            On(NetMessageType.DroppedItemSpawn, DroppedItemSpawnMessage.Deserialize, m => PlayerFXHandlers.HandleDroppedItemSpawn(m));
            On(NetMessageType.DroppedItemPickup, DroppedItemPickupMessage.Deserialize, m => PlayerFXHandlers.HandleDroppedItemPickup(m));
            On(NetMessageType.ExamineObject, ExamineObjectMessage.Deserialize, m => ExaminableHandlers.HandleExamineObject(m));
            On(NetMessageType.PeerHasItem, PeerHasItemMessage.Deserialize, m => TradeHandlers.HandlePeerHasItem(m));
            OnRaw(NetMessageType.RemotePlayerForward, payload =>
            {
                // Host -> client only ([HostOnly]: the host drops it from clients before
                // dispatch). The inner message is attributed to the original player.
                var fwd = RemotePlayerForwardMessage.Deserialize(new NetReader(payload));
                int saved = _currentReceivePlayerId;
                _currentReceivePlayerId = fwd.OriginalPlayerId;
                try
                {
                    PlayerFXHandlers.DispatchRemotePlayerForward(
                        (NetMessageType)fwd.InnerType, fwd.InnerPayload);
                }
                finally
                {
                    _currentReceivePlayerId = saved;
                }
            });
            On(NetMessageType.VaultState, VaultStateMessage.Deserialize, m => JournalHandlers.HandleVaultState(m));
        }
    }
}
