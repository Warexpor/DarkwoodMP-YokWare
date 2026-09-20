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
        /// <summary>Inbound dispatch slice: Players.</summary>
        private bool TryDispatchPlayers(NetMessageType type, byte[] payload)
        {
            switch (type)
            {
                        case NetMessageType.PlayerState:
                            PlayerStateHandlers.HandlePlayerState(PlayerStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerSound:
                            WorldProxyHandlers.HandlePlayerSound(PlayerSoundMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerScare:
                            WorldProxyHandlers.HandlePlayerScare(PlayerScareMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerEffectSync:
                            WorldProxyHandlers.HandlePlayerEffectSync(PlayerEffectSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DragSync:
                            PlayerInteractHandlers.HandleDragSync(DragSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerLightState:
                            PlayerLightFxHandlers.HandlePlayerLightState(PlayerLightStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerAudio:
                            WorldFxHandlers.HandlePlayerAudio(PlayerAudioMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerAnimation:
                            PlayerFXHandlers.HandlePlayerAnimation(
                                PlayerAnimationMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerAnimLibrary:
                            PlayerFXHandlers.HandlePlayerAnimLibrary(
                                PlayerAnimLibraryMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.BulletImpact:
                            PlayerFXHandlers.HandleBulletImpact(
                                BulletImpactMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerFiredWeapon:
                            PlayerFXHandlers.HandlePlayerFiredWeapon(
                                PlayerFiredWeaponMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DroppedItemSpawn:
                            PlayerFXHandlers.HandleDroppedItemSpawn(
                                DroppedItemSpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DroppedItemPickup:
                            PlayerFXHandlers.HandleDroppedItemPickup(
                                DroppedItemPickupMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ExamineObject:
                            ExaminableHandlers.HandleExamineObject(
                                ExamineObjectMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PeerHasItem:
                            TradeHandlers.HandlePeerHasItem(
                                PeerHasItemMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.RemotePlayerForward:
                            {
                                var fwd = RemotePlayerForwardMessage.Deserialize(new NetReader(payload));
                                // Host trust: only the original player may ask the host to re-broadcast
                                // their own message. A claimed OriginalPlayerId that differs from the
                                // The actual sender does not match the claimed identity.
                                // Drop the forward.
                                if (_role == NetworkRole.Host && fwd.OriginalPlayerId != _currentReceivePlayerId)
                                {
                                    ModLog.Warn(LogCat.Network,
                                        "Reject RemotePlayerForward: claimed p" + fwd.OriginalPlayerId
                                        + " from p" + _currentReceivePlayerId);
                                    return true;
                                }
                                int saved = _currentReceivePlayerId;
                                _currentReceivePlayerId = fwd.OriginalPlayerId;
                                _isForwardedMessage = true;
                                try
                                {
                                    PlayerFXHandlers.DispatchRemotePlayerForward(
                                        (NetMessageType)fwd.InnerType, fwd.InnerPayload);
                                }
                                finally
                                {
                                    _isForwardedMessage = false;
                                    _currentReceivePlayerId = saved;
                                }
                                return true;
                            }
                        case NetMessageType.VaultState:
                            JournalHandlers.HandleVaultState(
                                VaultStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                default:
                    return false;
            }
        }
    }
}
