using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Misc world outbound sends (shadows, FX, sticky props, player) composed for 0.8.</summary>
    internal sealed class WorldSendNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldSendNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendShadowEvent(ShadowEventMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.ShadowEvent, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendShadowSpawn(ShadowSpawnMessage msg)
        {
            if (!_net.IsConnected) return;
            _net.Broadcast(NetMessageType.ShadowSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            if (!_net.IsConnected) return;
            // Alive ticks: Unreliable OK. Death flag must be reliable so clients
            // do not keep ghost shadows after UnregisterShadow.
            bool isDead = (msg.Flags & 2) != 0;
            _net.Broadcast(NetMessageType.ShadowStateUpdate, w => msg.Serialize(w),
                isDead ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        internal void BroadcastShadowStates()
        {
            if (_net.Role != NetworkRole.Host) return;
            if (!_net.IsConnected) return;
            if (_net.ShadowTracked.Count == 0) return;

            // Clean dead/null shadows (send death first), broadcast living ones.
            List<short> deadIds = null;
            foreach (var kvp in _net.ShadowTracked)
            {
                if (kvp.Value == null || kvp.Value.dead)
                {
                    if (deadIds == null) deadIds = new List<short>();
                    deadIds.Add(kvp.Key);
                    if (kvp.Value != null)
                    {
                        Vector3 p = kvp.Value.transform.position;
                        SendShadowStateUpdate(new ShadowStateUpdateMessage
                        {
                            ShadowId = kvp.Key,
                            PosX = p.x,
                            PosY = p.y,
                            PosZ = p.z,
                            RotY = kvp.Value.transform.rotation.eulerAngles.y,
                            DistanceToPlayer = kvp.Value.distanceToPlayer,
                            Flags = 2
                        });
                    }
                    continue;
                }

                var sc = kvp.Value;
                var msg = new ShadowStateUpdateMessage
                {
                    ShadowId = kvp.Key,
                    PosX = sc.transform.position.x,
                    PosY = sc.transform.position.y,
                    PosZ = sc.transform.position.z,
                    RotY = sc.transform.rotation.eulerAngles.y,
                    DistanceToPlayer = sc.distanceToPlayer,
                    Flags = 0
                };
                SendShadowStateUpdate(msg);
            }

            if (deadIds != null)
            {
                for (int i = 0; i < deadIds.Count; i++)
                    _net.ShadowTracked.Remove(deadIds[i]);
            }
        }

        /// <summary>
        /// Host: re-send ShadowEvent + all tracked shadows to a late joiner.
        /// </summary>
        internal void SendShadowsTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            if (_net.ShadowTracked.Count == 0) return;

            _net.SendBulkOrAll(NetMessageType.ShadowEvent,
                w => new ShadowEventMessage().Serialize(w), targetPlayerId);

            int sent = 0;
            foreach (var kvp in _net.ShadowTracked)
            {
                ShadowCreature sc = kvp.Value;
                if (sc == null || sc.dead) continue;

                var info = sc.GetComponent<ShadowSyncInfo>();
                byte shadowType = info != null ? info.ShadowType : (byte)0;
                Vector3 p = sc.transform.position;
                var msg = new ShadowSpawnMessage
                {
                    ShadowId = kvp.Key,
                    ShadowType = shadowType,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    RotY = sc.transform.rotation.eulerAngles.y,
                    DistanceToPlayer = sc.distanceToPlayer,
                    Flags = 0
                };
                _net.SendBulkOrAll(NetMessageType.ShadowSpawn, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} shadows to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} shadows to all clients");
        }

        internal void SendScenarioSync(ScenarioSyncMessage msg)
        {
            if (!_net.IsConnected) return;
            _net.Broadcast(NetMessageType.ScenarioSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendScenarioEventFired(int nightId, int eventIndex)
        {
            if (!_net.IsConnected) return;
            var msg = new ScenarioEventFiredMessage { NightId = nightId, EventIndex = eventIndex };
            _net.Broadcast(NetMessageType.ScenarioEventFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendEntityBurning(short entityId, bool isBurning, float burnTime = 0, float modifier = 0, float interval = 0)
        {
            if (!_net.IsConnected) return;
            var msg = new EntityBurningMessage { EntityId = entityId, IsBurning = isBurning, BurnTime = burnTime, Modifier = modifier, Interval = interval };
            _net.Broadcast(NetMessageType.EntityBurning, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendLiquidStopBurning(Vector3 pos)
        {
            if (!_net.IsConnected) return;
            var msg = new LiquidStopBurningMessage { PosX = pos.x, PosY = pos.y, PosZ = pos.z };
            _net.Broadcast(NetMessageType.LiquidStopBurning, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendPlayerBurning(bool isBurning, float burnTime = 0)
        {
            if (!_net.IsConnected) return;
            var msg = new PlayerBurningMessage { IsBurning = isBurning, BurnTime = burnTime };
            _net.Broadcast(NetMessageType.PlayerBurning, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendExplosionSpawnObject(string prefabName, Vector3 pos, Vector3 rot)
        {
            if (!_net.IsConnected) return;
            var msg = new ExplosionSpawnObjectMessage { PrefabName = prefabName, PosX = pos.x, PosY = pos.y, PosZ = pos.z, RotX = rot.x, RotY = rot.y, RotZ = rot.z };
            _net.Broadcast(NetMessageType.ExplosionSpawnObject, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendConstructible(ConstructibleMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.ConstructibleConstruction, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendInteractiveItemSwitch(InteractiveItemSwitchMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.InteractiveItemSwitch, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendPadlockUnlock(PadlockUnlockMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active) return;
            _net.Broadcast(NetMessageType.PadlockUnlock, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendLockedUnlock(LockedUnlockMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active) return;
            _net.Broadcast(NetMessageType.LockedUnlock, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendGameEventsFired(GameEventsFiredMessage msg)
        {
            if (!_net.IsConnected) return;
            // ProcessInboundMessage holds LanNetworkManager.IsApplyingRemoteState for DialogNpcLock Release.
            // HostFireNpcCloseDialogue runs under DialogHostApplyGuard and MUST fan out
            // Leave-door GEs must not return early after logging "fired"; the client door
            // otherwise remains stuck.
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active) return;
            _net.Broadcast(NetMessageType.GameEventsFired, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.HideoutUpgrade, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendGeneratorState(GeneratorState gs)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            var msg = Sync.WorldPhysicsSyncService.StampSnapshot(
                new PhysicsStateMessage { Generators = new[] { gs } });
            _net.Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendLightState(LightStateMessage ls)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // World light state is sticky; lost Unreliable packets permanently desync lamps.
            _net.Broadcast(NetMessageType.LightState, w => ls.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendWorldObjectRemoved(WorldObjectRemovedMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // Disarm destroy path used to fire TrapDestroy + disarm-postfix + progressBar
            // (3x WorldObjectRemoved would cause peer scan thrash and NRE). One wire send per key.
            if (!Sync.WorldPhysicsSyncService.TryClaimOutboundObjectRemove(msg.PosX, msg.PosY, msg.PosZ, msg.ObjectName))
                return;
            _net.Broadcast(NetMessageType.WorldObjectRemoved, w => msg.Serialize(w));
        }

        internal void SendPlayerLightState(PlayerLightStateMessage msg, DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.PlayerLightState, w => msg.Serialize(w), method);
        }

        internal void SyncCurrentLightState()
        {
            Player local = Player.Instance;
            if (local == null) return;
            // BuildLightState already packs lantern ambient + held torch/flash.
            var msg = LightStateHelper.BuildLightState(local);

            // Save-loaded lantern: lightDot may still be default if modifyLightDot never
            // re-ran. It scanned activeItems but only activated ambient lights,
            // not every lightRadius.
            if (!msg.HasAmbientLight)
            {
                try
                {
                    foreach (var activeItem in Player.Instance.activeItems)
                    {
                        if (activeItem == null || !activeItem.activated || activeItem.baseClass == null)
                            continue;
                        if (LightStateHelper.IsLanternItem(activeItem)
                            || (activeItem.baseClass.lightRadius > 0f
                                && activeItem.baseClass.lightEmitter == null
                                && !activeItem.baseClass.isFlashlight))
                        {
                            msg.HasAmbientLight = true;
                            msg.LightOn = true;
                            msg.LightRadius = activeItem.baseClass.lightRadius > 0f
                                ? activeItem.baseClass.lightRadius
                                : 650f;
                            msg.LightIntensity = 1f;
                            msg.LightColorR = 1f;
                            msg.LightColorG = 1f;
                            msg.LightColorB = 1f;
                            if (string.IsNullOrEmpty(msg.ItemType))
                                msg.ItemType = activeItem.type ?? "lantern";
                            break;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    ModLog.Warn(LogCat.World, $"[Light] activeItems scan failed: {ex.Message}");
                }
            }

            ModLog.Event(LogCat.World,
                $"[Light] TX SyncCurrent on={msg.LightOn} type={msg.ItemType ?? "-"} flash={msg.IsFlashlight} emit={msg.HasLightEmitter} ambient={msg.HasAmbientLight} r={msg.LightRadius:F0}");
            SendPlayerLightState(msg, DeliveryMethod.ReliableOrdered);
        }

        internal void SendExplosionTrigger(ExplosionTriggerMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // Must be reliable; lost barrel or molotov triggers desync combat and effects.
            _net.Broadcast(NetMessageType.ExplosionTrigger, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        internal void SendPlayerAudio(PlayerAudioMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // One-shots and stop signals must not drop under lossy LAN.
            _net.Broadcast(NetMessageType.PlayerAudio, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendGasTrailSpawn(GasTrailSpawnMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.GasTrailSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendMeleeWorldHit(MeleeWorldHitMessage msg)
        {
            if (!_net.IsConnected) return;
            if (_net.Role != NetworkRole.Client) return;
            _net.Broadcast(NetMessageType.MeleeWorldHit, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendGasIgnite(GasIgniteMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.GasIgnite, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendSawState(SawStateMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.SawState, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        internal void SendPlayerAnimation(PlayerAnimationMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.PlayerAnimation, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendPlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // Reliable: weapon sprite library must not drop (one-shot equip event)
            _net.Broadcast(NetMessageType.PlayerAnimLibrary, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Sticky: re-broadcast the local torso library (late join / proxy create race).
        /// Mirrors <see cref="SyncCurrentLightState"/>.
        /// </summary>
        internal void SyncCurrentAnimLibrary()
        {
            if (!_net.IsConnected) return;
            Player local = Player.Instance;
            if (local == null || local.torsoAnimator == null || local.torsoAnimator.Library == null)
                return;
            string libName = local.torsoAnimator.Library.name;
            if (string.IsNullOrEmpty(libName)) return;
            SendPlayerAnimLibrary(new PlayerAnimLibraryMessage { LibraryName = libName });
            ModRuntime.Log?.LogDebug("[AnimLib] SyncCurrent library: " + libName);
        }

        internal void SendBulletImpact(BulletImpactMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.BulletImpact, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendPlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.PlayerFiredWeapon, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Deliver damage to a specific remote player. Prefer this over broadcast;
        /// multi-client broadcast would apply the same hit to every peer.
        /// </summary>
        internal void SendDamagePlayer(int victimPlayerId, DamagePlayerMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (victimPlayerId <= 0) return;
            _net.SendToPlayer(victimPlayerId, NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Broadcast is only safe for single-client sessions. Prefer the player-ID overload.</summary>
        internal void SendDamagePlayer(DamagePlayerMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // Route to the only connected client when possible; otherwise no-op for multi-peer.
            if (_net.PeerCount == 1)
            {
                foreach (int peerId in _net.EnumeratePeerIds())
                {
                    _net.SendToPlayer(peerId, NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                    return;
                }
            }
            if (_net.PeerCount > 1)
            {
                ModRuntime.Log?.LogWarning("[DamagePlayer] broadcast skipped — use SendDamagePlayer(playerId, msg) for multi-client");
                return;
            }
            _net.Broadcast(NetMessageType.DamagePlayer, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

    }
}
