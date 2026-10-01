using System;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Gas trail / ignite / burn / liquid-stop combat FX handlers.</summary>
    internal sealed class CombatFxGasBurnNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal CombatFxGasBurnNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleGasTrailSpawn(GasTrailSpawnMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // Host: client ground pour. Validate near the pourer's proxy, then spawn on
            // the host world. Forwardable fans the same payload to other peers (pourer
            // already spawned a local visual in GasolineTrailSpawnPatch).
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
            {
                int playerId = _net.CurrentReceivePlayerId;
                if (!CombatAuthorityPolicy.IsFinitePosition(msg.PosX, msg.PosY, msg.PosZ))
                {
                    _net.SuppressRelay();
                    return;
                }

                RemotePlayerProxy proxy = _net.GetProxy(playerId);
                if (proxy == null
                    || !CombatAuthorityPolicy.IsWithinRange(
                        proxy.transform.position.x,
                        proxy.transform.position.y,
                        proxy.transform.position.z,
                        pos.x, pos.y, pos.z,
                        GameplayConstants.MaxPlayerAttackRange))
                {
                    ModRuntime.Log?.LogWarning(
                        "[GasTrail] rejected client pour from p" + playerId + " at " + pos);
                    _net.SuppressRelay();
                    return;
                }

                Sync.WorldPhysicsSyncService.SpawnGasTrail(pos);
                ModRuntime.LegacyInfo(
                    $"[GasTrail] host adopted client pour from p{playerId} at {pos}");
                return;
            }

            Sync.WorldPhysicsSyncService.SpawnGasTrail(pos);
        }

        internal void HandleGasIgnite(GasIgniteMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // Host: client torch / flaming melee / Burn-trigger ignite. Validate near
            // the actor's proxy, then light on the host world. Forwardable fans peers
            // (igniter already lit a local visual in GasIgnitePatch).
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
            {
                int playerId = _net.CurrentReceivePlayerId;
                if (!CombatAuthorityPolicy.IsFinitePosition(msg.PosX, msg.PosY, msg.PosZ))
                {
                    _net.SuppressRelay();
                    return;
                }

                RemotePlayerProxy proxy = _net.GetProxy(playerId);
                if (proxy == null
                    || !CombatAuthorityPolicy.IsWithinRange(
                        proxy.transform.position.x,
                        proxy.transform.position.y,
                        proxy.transform.position.z,
                        pos.x, pos.y, pos.z,
                        GameplayConstants.MaxPlayerAttackRange))
                {
                    ModRuntime.Log?.LogWarning(
                        "[GasIgnite] rejected client ignite from p" + playerId + " at " + pos);
                    _net.SuppressRelay();
                    return;
                }

                Sync.WorldPhysicsSyncService.IgniteGasAtPos(pos);
                ModRuntime.LegacyInfo(
                    $"[GasIgnite] host adopted client ignite from p{playerId} at {pos}");
                return;
            }

            Sync.WorldPhysicsSyncService.IgniteGasAtPos(pos);
        }

        internal void HandleEntityBurning(EntityBurningMessage msg)
        {
            var entity = Sync.CharacterTracker.FindByStableId(msg.EntityId);
            if (entity == null) return;
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (msg.IsBurning)
                {
                    var burn = entity.GetComponent<Burn>();
                    if (burn == null)
                    {
                        entity.gameObject.AddComponent<Burn>().burnTime = msg.BurnTime;
                    }
                }
                else
                {
                    var burn = entity.GetComponent<Burn>();
                    if (burn != null)
                    {
                        burn.stop();
                    }
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        internal void HandlePlayerBurning(PlayerBurningMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null) return;
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                if (msg.IsBurning)
                {
                    var burn = proxy.GetComponent<Burn>();
                    if (burn == null)
                    {
                        burn = proxy.gameObject.AddComponent<Burn>();
                        burn.burnTime = msg.BurnTime;
                        ModRuntime.LegacyInfo($"[PlayerBurnSync] applied Burn to proxy for player {playerId}");
                    }
                }
                else
                {
                    var burn = proxy.GetComponent<Burn>();
                    if (burn != null)
                    {
                        burn.stop();
                        ModRuntime.LegacyInfo($"[PlayerBurnSync] removed Burn from proxy for player {playerId}");
                    }
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        /// <summary>
        /// Host: push existing flammable liquid trails (+ burning state) to a joiner
        /// so late join sees poured gasoline already in the world.
        /// </summary>
        internal void SendGasStateTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            Liquid[] all = WorldQueryHelper.GetCachedSceneComponents<Liquid>();
            int trails = 0, burning = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Liquid liquid = all[i];
                if (liquid == null || !liquid.flammable) continue;

                Vector3 p = liquid.transform.position;
                // Cap bulk so join flood stays reasonable (trails are dense when pouring).
                if (trails >= 256) break;

                var trailMsg = new GasTrailSpawnMessage { PosX = p.x, PosY = p.y, PosZ = p.z };
                _net.SendBulkOrAll(NetMessageType.GasTrailSpawn, w => trailMsg.Serialize(w), targetPlayerId);
                trails++;

                if (liquid.burning)
                {
                    var igniteMsg = new GasIgniteMessage { PosX = p.x, PosY = p.y, PosZ = p.z };
                    _net.SendBulkOrAll(NetMessageType.GasIgnite, w => igniteMsg.Serialize(w), targetPlayerId);
                    burning++;
                }
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {trails} gas trails ({burning} burning) to player {targetPlayerId}"
                : $"[BulkSync] Sent {trails} gas trails ({burning} burning) to all clients");
        }

        internal void HandleLiquidStopBurning(LiquidStopBurningMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            int hitN = Physics.OverlapSphereNonAlloc(pos, 1.5f, WorldQueryHelper.SharedOverlapBuf);
            for (int i = 0; i < hitN; i++)
            {
                var liq = WorldQueryHelper.SharedOverlapBuf[i].GetComponent<Liquid>();
                if (liq != null)
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    try
                    {
                        Traverse.Create(liq).Method("stopBurning").GetValue();
                    }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                    break;
                }
            }
        }

    }
}
