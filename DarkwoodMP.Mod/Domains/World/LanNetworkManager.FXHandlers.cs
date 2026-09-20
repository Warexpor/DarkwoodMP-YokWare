using System;
using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to world / light / combat FX NetHandlers.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal WorldFxNetHandlers WorldFxHandlers { get; private set; }
        internal PlayerLightFxApplyNetHandlers PlayerLightFxApplyHandlers { get; private set; }
        internal PlayerLightFxAmbientNetHandlers PlayerLightFxAmbientHandlers { get; private set; }
        internal PlayerLightFxNetHandlers PlayerLightFxHandlers { get; private set; }
        internal CombatFxImpactNetHandlers CombatFxImpactHandlers { get; private set; }
        internal CombatFxGasBurnNetHandlers CombatFxGasBurnHandlers { get; private set; }
        internal CombatFxNetHandlers CombatFxHandlers { get; private set; }

        private void HandleEntitySound(EntitySoundMessage msg)
        {
            WorldFxHandlers.HandleEntitySound(msg);
        }
        private void HandleWorldObjectRemoved(WorldObjectRemovedMessage msg)
        {
            WorldFxHandlers.HandleWorldObjectRemoved(msg);
        }
        private static bool IsAmbientLanternType(string type)
        {
            return PlayerLightFxNetHandlers.IsAmbientLanternType(type);
        }
        internal void HandlePlayerLightState(PlayerLightStateMessage msg)
        {
            PlayerLightFxHandlers.HandlePlayerLightState(msg);
        }
        internal static void EnsureEmitterVisible(GameObject emitter)
        {
            PlayerLightFxNetHandlers.EnsureEmitterVisible(emitter);
        }
        internal static void PlayAllParticleSystems(GameObject emitter)
        {
            PlayerLightFxNetHandlers.PlayAllParticleSystems(emitter);
        }
        internal static void SetupParticleSorting(GameObject emitter)
        {
            PlayerLightFxNetHandlers.SetupParticleSorting(emitter);
        }
        private static void SetupLight2D(GameObject emitter)
        {
            PlayerLightFxNetHandlers.SetupLight2D(emitter);
        }
        private static void ApplyRemoteLanternAmbient(RemotePlayerProxy proxy, int playerId, bool wantOn, PlayerLightStateMessage msg)
        {
            PlayerLightFxNetHandlers.ApplyRemoteLanternAmbient(proxy, playerId, wantOn, msg);
        }
        private static Light2D CreateRemoteLanternLight(Transform proxyRoot)
        {
            return PlayerLightFxNetHandlers.CreateRemoteLanternLight(proxyRoot);
        }
        private static void EnsureRadialMaterial(Light2D light)
        {
            PlayerLightFxNetHandlers.EnsureRadialMaterial(light);
        }
        private static void DestroyLegacyClonedLanterns(Transform proxyRoot)
        {
            PlayerLightFxNetHandlers.DestroyLegacyClonedLanterns(proxyRoot);
        }
        private static Transform FindChildIncludingInactive(Transform root, string childName)
        {
            return PlayerLightFxNetHandlers.FindChildIncludingInactive(root, childName);
        }
        private static void NeutralizeClonedPlayerLightDots(Transform proxyRoot)
        {
            PlayerLightFxNetHandlers.NeutralizeClonedPlayerLightDots(proxyRoot);
        }
        private static void RemoveAllItemEmitters(Transform proxyRoot)
        {
            PlayerLightFxNetHandlers.RemoveAllItemEmitters(proxyRoot);
        }
        private static void RemoveClonedEmitters(Transform proxyRoot)
        {
            PlayerLightFxNetHandlers.RemoveClonedEmitters(proxyRoot);
        }
        internal void HandleThrowableSpawn(ThrowableSpawnMessage msg)
        {
            CombatFxHandlers.HandleThrowableSpawn(msg);
        }
        private void HandleExplosionTrigger(ExplosionTriggerMessage msg)
        {
            CombatFxHandlers.HandleExplosionTrigger(msg);
        }
        internal void HandlePlayerAudio(PlayerAudioMessage msg)
        {
            WorldFxHandlers.HandlePlayerAudio(msg);
        }
        private void HandleMeleeWorldHit(MeleeWorldHitMessage msg)
        {
            CombatFxHandlers.HandleMeleeWorldHit(msg);
        }
        private static bool TryHitDestructibleItemAt(Vector3 pos, float radius, int damage, Transform attackerT)
        {
            return CombatFxNetHandlers.TryHitDestructibleItemAt(pos, radius, damage, attackerT);
        }
        private void HandleGasTrailSpawn(GasTrailSpawnMessage msg)
        {
            CombatFxHandlers.HandleGasTrailSpawn(msg);
        }
        private void HandleGasIgnite(GasIgniteMessage msg)
        {
            CombatFxHandlers.HandleGasIgnite(msg);
        }
        private void HandleEntityBurning(EntityBurningMessage msg)
        {
            CombatFxHandlers.HandleEntityBurning(msg);
        }
        internal void HandlePlayerBurning(PlayerBurningMessage msg)
        {
            CombatFxHandlers.HandlePlayerBurning(msg);
        }
        internal void SendGasStateTo(int targetPlayerId)
        {
            CombatFxHandlers.SendGasStateTo(targetPlayerId);
        }

        /// <summary>Host late-join: living Infection splats via EntitySpawn (86).</summary>
        internal void SendInfectionStatesTo(int targetPlayerId)
        {
            DWMPHorde.Patches.InfectionSyncHelpers.SendInfectionStatesTo(this, targetPlayerId);
        }

        private void HandleLiquidStopBurning(LiquidStopBurningMessage msg)
        {
            CombatFxHandlers.HandleLiquidStopBurning(msg);
        }
        private void HandleExplosionSpawnObject(ExplosionSpawnObjectMessage msg)
        {
            CombatFxHandlers.HandleExplosionSpawnObject(msg);
        }
    }
}
