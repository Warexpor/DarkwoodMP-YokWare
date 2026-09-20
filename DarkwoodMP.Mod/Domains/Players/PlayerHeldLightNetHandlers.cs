using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: held-light pack (TX) + apply (RX). Awake wires the siblings.
    /// </summary>
    internal sealed class PlayerHeldLightNetHandlers
    {
        private readonly PlayerHeldLightPackNetHandlers _pack;
        private readonly PlayerHeldLightApplyNetHandlers _apply;

        internal PlayerHeldLightNetHandlers(
            PlayerHeldLightPackNetHandlers pack,
            PlayerHeldLightApplyNetHandlers apply)
        {
            _pack = pack ?? throw new System.ArgumentNullException(nameof(pack));
            _apply = apply ?? throw new System.ArgumentNullException(nameof(apply));
        }

        internal void ResetLocalLightSendCache() => _pack.ResetLocalLightSendCache();

        internal void PackContinuousLights(ref PlayerStateMessage msg, Player local) =>
            _pack.PackContinuousLights(ref msg, local);

        internal static bool TryGetLocalHeldFlareLight(Player local, out Light2D light, out Flare flare) =>
            PlayerHeldLightPackNetHandlers.TryGetLocalHeldFlareLight(local, out light, out flare);

        internal static bool TryGetLocalHeldMatchLight(Player local, out Light2D light) =>
            PlayerHeldLightPackNetHandlers.TryGetLocalHeldMatchLight(local, out light);

        internal static bool IsMatchLightItem(Player local) =>
            PlayerHeldLightPackNetHandlers.IsMatchLightItem(local);

        internal void HandleRemoteContinuousLights(PlayerStateMessage state, int playerId = -1) =>
            _apply.HandleRemoteContinuousLights(state, playerId);

        internal void DestroyRemoteFlareLight(int playerId) =>
            _apply.DestroyRemoteFlareLight(playerId);

        internal void DestroyRemoteItemLight(int playerId) =>
            _apply.DestroyRemoteItemLight(playerId);
    }
}
