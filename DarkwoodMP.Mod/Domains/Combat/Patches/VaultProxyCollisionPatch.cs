using System;
using System.Collections.Generic;
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
        // Colliders THIS patch disabled for the running vault (proxies + the player's own), so the
        // restore re-enables exactly those and nothing that was already off for another reason.
        private static readonly List<Collider> _disabled = new List<Collider>(32); // process-scoped: vault-scoped, released by Restore()
        private static bool _active; // process-scoped: vault-scoped, released by Restore()

        static void Prefix(Player __instance)
        {
            // Mod loaded without a co-op session must stay vanilla.
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return;

            // A previous vault that never reached endJumpThroughWindow must not leak.
            Restore();

            var allProxies = net.GetAllProxies();
            if (allProxies != null)
            {
                foreach (var proxy in allProxies)
                {
                    DisableEnabled(proxy.CachedColliders);
                }
            }

            // Disable the player's own colliders so they don't scrape walls during vault
            DisableEnabled(__instance.GetComponentsInChildren<Collider>(true));
            _active = true;

            // Notify remote peers to disable this player's proxy colliders during vault
            SendVaultState(true);
        }

        // Finalizer (not Postfix): if vanilla jumpThroughWindow throws the vault never starts, so
        // nothing would ever call endJumpThroughWindow to hand the colliders back.
        static void Finalizer(Exception __exception)
        {
            if (__exception != null)
                Restore();
        }

        private static void DisableEnabled(Collider[] cols)
        {
            if (cols == null) return;
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null || !c.enabled) continue;
                c.enabled = false;
                _disabled.Add(c);
            }
        }

        /// <summary>
        /// Re-enables the colliders disabled for the vault and tells peers the vault is over.
        /// Called from endJumpThroughWindow and from every vanilla path that ends a vault without
        /// it (death, stopAllPerformingActionAnims, a failed start).
        /// </summary>
        internal static void Restore()
        {
            for (int i = 0; i < _disabled.Count; i++)
            {
                Collider c = _disabled[i];
                if (c != null) c.enabled = true;
            }
            _disabled.Clear();

            if (!_active) return;
            _active = false;
            // Notify remote peers to re-enable this player's proxy colliders after vault
            SendVaultState(false);
        }

        /// <summary>Broadcasts/sends vault state to remote peers so they can
        /// disable/enable this player's proxy colliders during window vault.</summary>
        internal static void SendVaultState(bool isVaulting)
        {
            var net = ModRuntime.Network;
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

    // Player.Update vault frame logging is intentionally omitted to keep Dev
    // logs usable.

    [HarmonyPatch(typeof(Player), "endJumpThroughWindow")]
    internal static class VaultEndPatch
    {
        static void Postfix() => VaultStartPatch.Restore();
    }

    /// <summary>Vanilla ends a vault without endJumpThroughWindow on death.</summary>
    [HarmonyPatch(typeof(Player), "die")]
    internal static class VaultDeathRestorePatch
    {
        static void Postfix() => VaultStartPatch.Restore();
    }

    /// <summary>Vanilla clears <c>jumping</c> here (cutscenes, interrupts) without endJumpThroughWindow.</summary>
    [HarmonyPatch(typeof(Player), "stopAllPerformingActionAnims")]
    internal static class VaultStopAnimsRestorePatch
    {
        static void Postfix() => VaultStartPatch.Restore();
    }
}
