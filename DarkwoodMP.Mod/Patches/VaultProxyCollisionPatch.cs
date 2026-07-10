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
        private static Vector3 _preVaultPos;
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
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Proxy] disabling {proxyCols.Length} colliders for proxy {proxy.PlayerId}");
                    foreach (var pc in proxyCols)
                    {
                        if (pc == null) continue;
                        pc.enabled = false;
                    }
                }
            }

            // Disable the player's own colliders so they don't scrape walls during vault
            _playerColliders = __instance.GetComponentsInChildren<Collider>(true);
            int playerColCount = 0;
            foreach (var pc in _playerColliders)
            {
                if (pc == null || !pc.enabled) continue;
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Proxy] disabling player collider={pc.name} was enabled={pc.enabled} isTrigger={pc.isTrigger}");
                pc.enabled = false;
                playerColCount++;
            }

            _preVaultPos = __instance.transform.position;
            var rb = __instance.GetComponent<Rigidbody>();
            if (ModRuntime.VerboseLogging)
            {
                ModRuntime.LegacyInfo($"[Proxy] jumpThroughWindow() START");
                ModRuntime.LegacyInfo($"[Proxy]   transform.pos={__instance.transform.position} rb.pos={(rb != null ? rb.position.ToString() : "null")} rb.vel={(rb != null ? rb.velocity.ToString() : "null")} mass={(rb != null ? rb.mass.ToString() : "null")} drag={(rb != null ? rb.drag.ToString() : "null")} constraints={(rb != null ? rb.constraints.ToString() : "null")}");
                ModRuntime.LegacyInfo($"[Proxy]   timeScale={Time.timeScale} Core.Paused={Core.Paused} FreezeTracker.IsFrozen={FreezeTracker.IsFrozen}");
            }

            // Notify remote peers to disable this player's proxy colliders during vault
            SendVaultState(true);
        }

        public static void OnUpdate(Player p)
        {
            if (p == null || !p.jumping) return;

            if (ModRuntime.VerboseLogging)
            {
                Vector3 cur = p.transform.position;
                float dist = Vector3.Distance(cur, _preVaultPos);
                var rb = p.GetComponent<Rigidbody>();
                Vector3 up = p.transform.up;
                ModRuntime.LegacyInfo($"[Proxy] frame={Time.frameCount} distFromStart={dist:F2} pos={cur} vel={(rb != null ? rb.velocity.ToString() : "null")} up=({up.x:F3},{up.y:F3},{up.z:F3}) timeScale={Time.timeScale}");
            }
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

    [HarmonyPatch(typeof(Player), "Update")]
    internal static class VaultMonitorPatch
    {
        static void Postfix(Player __instance)
        {
            VaultStartPatch.OnUpdate(__instance);
        }
    }

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

            var rb = __instance.GetComponent<Rigidbody>();
            if (ModRuntime.VerboseLogging)
            {
                ModRuntime.LegacyInfo($"[Proxy] endJumpThroughWindow() END");
                ModRuntime.LegacyInfo($"[Proxy]   transform.pos={__instance.transform.position} rb.pos={(rb != null ? rb.position.ToString() : "null")}");
            }

            // Notify remote peers to re-enable this player's proxy colliders after vault
            VaultStartPatch.SendVaultState(false);
        }
    }
}
