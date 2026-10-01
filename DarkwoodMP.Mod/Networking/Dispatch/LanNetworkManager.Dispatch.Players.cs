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
                            {
                                var drag = DragSyncMessage.Deserialize(new NetReader(payload));
                                bool hostStamps = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
                                if (hostStamps)
                                    drag.ClaimedByPlayerId = _currentReceivePlayerId;
                                PlayerInteractHandlers.HandleDragSync(drag);
                                if (hostStamps)
                                {
                                    // Relay the host-stamped body ourselves: the generic Forwardable relay
                                    // re-sends the raw inbound payload, so a client-claimed
                                    // ClaimedByPlayerId would reach the other clients unchanged and let
                                    // one player claim, steal or release another's drag.
                                    var dragWriter = new NetWriter();
                                    drag.Serialize(dragWriter);
                                    byte[] dragBody = dragWriter.CopyData();
                                    SendToAllExcept(_currentReceivePlayerId, NetMessageType.DragSync,
                                        w => w.PutRaw(dragBody), RelayMethodFor(_currentReceiveMethod));
                                    _suppressForwardThisMessage = true;
                                }
                                return true;
                            }
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
