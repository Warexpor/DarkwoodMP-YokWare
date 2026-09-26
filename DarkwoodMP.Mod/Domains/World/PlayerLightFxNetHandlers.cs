using System.Collections.Generic;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: player-light apply + ambient/emitter helpers. Awake wires siblings.
    /// </summary>
    internal sealed class PlayerLightFxNetHandlers
    {
        private readonly PlayerLightFxApplyNetHandlers _apply;

        internal PlayerLightFxNetHandlers(PlayerLightFxApplyNetHandlers apply)
        {
            _apply = apply ?? throw new System.ArgumentNullException(nameof(apply));
        }

        internal Dictionary<int, PlayerLightStateMessage> PendingPlayerLights =>
            _apply.PendingPlayerLights;

        internal void ClearPendingPlayerLights() => _apply.ClearPendingPlayerLights();

        internal void ClearPendingPlayerLightsFor(int playerId) =>
            _apply.ClearPendingPlayerLightsFor(playerId);

        internal static bool IsAmbientLanternType(string type) =>
            PlayerLightFxApplyNetHandlers.IsAmbientLanternType(type);

        internal void HandlePlayerLightState(PlayerLightStateMessage msg) =>
            _apply.HandlePlayerLightState(msg);

        internal static void EnsureEmitterVisible(GameObject emitter) =>
            PlayerLightFxAmbientNetHandlers.EnsureEmitterVisible(emitter);

        internal static void PlayAllParticleSystems(GameObject emitter) =>
            PlayerLightFxAmbientNetHandlers.PlayAllParticleSystems(emitter);

        internal static void SetupParticleSorting(GameObject emitter) =>
            PlayerLightFxAmbientNetHandlers.SetupParticleSorting(emitter);

        internal static void SetupLight2D(GameObject emitter) =>
            PlayerLightFxAmbientNetHandlers.SetupLight2D(emitter);

        internal static void ApplyRemoteLanternAmbient(
            RemotePlayerProxy proxy, int playerId, bool wantOn, PlayerLightStateMessage msg) =>
            PlayerLightFxAmbientNetHandlers.ApplyRemoteLanternAmbient(proxy, playerId, wantOn, msg);

        internal static Light2D CreateRemoteLanternLight(Transform proxyRoot) =>
            PlayerLightFxAmbientNetHandlers.CreateRemoteLanternLight(proxyRoot);

        internal static void EnsureRadialMaterial(Light2D light) =>
            PlayerLightFxAmbientNetHandlers.EnsureRadialMaterial(light);

        internal static void DestroyLegacyClonedLanterns(Transform proxyRoot) =>
            PlayerLightFxAmbientNetHandlers.DestroyLegacyClonedLanterns(proxyRoot);

        internal static Transform FindChildIncludingInactive(Transform root, string childName) =>
            PlayerLightFxAmbientNetHandlers.FindChildIncludingInactive(root, childName);

        internal static void NeutralizeClonedPlayerLightDots(Transform proxyRoot) =>
            PlayerLightFxAmbientNetHandlers.NeutralizeClonedPlayerLightDots(proxyRoot);

        internal static void RemoveAllItemEmitters(Transform proxyRoot) =>
            PlayerLightFxAmbientNetHandlers.RemoveAllItemEmitters(proxyRoot);

        internal static void RemoveClonedEmitters(Transform proxyRoot) =>
            PlayerLightFxAmbientNetHandlers.RemoveClonedEmitters(proxyRoot);
    }
}
