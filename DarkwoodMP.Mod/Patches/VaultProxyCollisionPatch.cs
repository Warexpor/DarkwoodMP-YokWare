using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(Player), "jumpThroughWindow")]
    internal static class VaultStartPatch
    {
        // Track player colliders so we can disable them during vault to prevent
        // the player's own collider from scraping walls and reversing velocity.
        internal static Collider[] _playerColliders;

        static void Prefix(Player __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            var allProxies = net?.GetAllProxies();
            if (allProxies != null)
            {
                foreach (var proxy in allProxies)
                {
                    Collider[] proxyCols = proxy.GetComponentsInChildren<Collider>(true);
                    foreach (var pc in proxyCols)
                    {
                        if (pc == null) continue;
                        pc.enabled = false;
                    }
                }
            }

            // Disable the player's own colliders so they don't scrape walls during vault
            _playerColliders = __instance.GetComponentsInChildren<Collider>(true);
            foreach (var pc in _playerColliders)
            {
                if (pc == null || !pc.enabled) continue;
                pc.enabled = false;
            }

            // Notify remote peers to disable this player's proxy colliders during vault
            SendVaultState(true);
        }

        /// <summary>Broadcasts/sends vault state to remote peers so they can
        /// disable/enable this player's proxy colliders during window vault.</summary>
        internal static void SendVaultState(bool isVaulting)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            var msg = new VaultStateMessage
            {
                IsVaulting = isVaulting,
                PlayerId = net.LocalPlayerId
            };
            // Host broadcasts; client → host (Forwardable rebroadcasts to other clients).
            net.Broadcast(NetMessageType.VaultState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    // ponytail: removed Player.Update vault frame logger — was ~400 lines/vault under LogPreset=Dev.

    [HarmonyPatch(typeof(Player), "endJumpThroughWindow")]
    internal static class VaultEndPatch
    {
        static void Postfix(Player __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            var allProxies = net?.GetAllProxies();
            if (allProxies != null)
            {
                foreach (var proxy in allProxies)
                {
                    Collider[] proxyCols = proxy.GetComponentsInChildren<Collider>(true);
                    foreach (var pc in proxyCols)
                    {
                        if (pc == null) continue;
                        pc.enabled = true;
                    }
                }
            }

            // Re-enable player's own colliders
            if (VaultStartPatch._playerColliders != null)
            {
                foreach (var pc in VaultStartPatch._playerColliders)
                {
                    if (pc == null) continue;
                    pc.enabled = true;
                }
                VaultStartPatch._playerColliders = null;
            }

            // Notify remote peers to re-enable this player's proxy colliders after vault
            VaultStartPatch.SendVaultState(false);
        }
    }
}
