using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Feeder + Lure station sync (Saw already has SawSyncPatches).
    /// Absolute state; Forwardable Broadcast. Lure sends are coalesced (~1s).
    /// </summary>
    internal static class StationSyncHelpers
    {
        private struct LureOutboxEntry
        {
            public float X, Y, Z;
            public int Health;
        }

        private static readonly Dictionary<string, LureOutboxEntry> _lureOutbox
            = new Dictionary<string, LureOutboxEntry>();
        private static float _lastLureFlush;
        private const float LureFlushInterval = 1f;

        internal static string PosKey(Vector3 p)
        {
            float x = Mathf.Round(p.x * 10f) / 10f;
            float z = Mathf.Round(p.z * 10f) / 10f;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}:{1:0.0}", x, z);
        }

        internal static void SendFeederState(Feeder feeder, string reason)
        {
            if (feeder == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            Vector3 p = feeder.transform.position;
            var msg = new FeederStateMessage
            {
                PosX = p.x,
                PosY = p.y,
                PosZ = p.z,
                Active = feeder.Active
            };
            ModRuntime.Network.Broadcast(NetMessageType.FeederState,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[FeederSync] send {reason} at ({msg.PosX:F1},{msg.PosZ:F1}) Active={msg.Active}");
        }

        /// <summary>Queue absolute lure health; flush at most once per LureFlushInterval.</summary>
        internal static void QueueLureHealth(Lure lure)
        {
            if (lure == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            Vector3 p = lure.transform.position;
            string key = PosKey(p);
            _lureOutbox[key] = new LureOutboxEntry
            {
                X = p.x,
                Y = p.y,
                Z = p.z,
                Health = lure.health
            };
            // Immediate flush when destroyed / zero so peers don't lag on corpse.
            if (lure.health <= 0)
                FlushLureOutbox(force: true);
            else
                FlushLureOutbox(force: false);
        }

        internal static void FlushLureOutbox(bool force)
        {
            if (_lureOutbox.Count == 0) return;
            if (!force && Time.unscaledTime - _lastLureFlush < LureFlushInterval)
                return;
            _lastLureFlush = Time.unscaledTime;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
            {
                _lureOutbox.Clear();
                return;
            }

            // Use stored pose — never FindNearest here (was a host FOOT hitch every 1s).
            // Skip lures far from every player — clients FOOT-scan them for nothing.
            const float interestSq = 1400f * 1400f;
            foreach (var kvp in _lureOutbox)
            {
                LureOutboxEntry e = kvp.Value;
                Vector3 lurePos = new Vector3(e.X, e.Y, e.Z);
                if (!IsLureNearAnyPlayer(lurePos, interestSq) && e.Health > 0)
                    continue;

                var msg = new LureStateMessage
                {
                    PosX = e.X,
                    PosY = e.Y,
                    PosZ = e.Z,
                    Health = e.Health
                };
                net.Broadcast(NetMessageType.LureState,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            _lureOutbox.Clear();
        }

        private static bool IsLureNearAnyPlayer(Vector3 lurePos, float maxDistSq)
        {
            if (Player.Instance != null)
            {
                Vector3 p = Player.Instance.transform.position;
                float dx = p.x - lurePos.x;
                float dz = p.z - lurePos.z;
                if (dx * dx + dz * dz <= maxDistSq)
                    return true;
            }
            return PlayerPositionManager.IsAnyRemoteWithinSq(lurePos, maxDistSq);
        }

        internal static void Reset()
        {
            _lureOutbox.Clear();
            _lastLureFlush = 0f;
        }
    }

    [HarmonyPatch(typeof(Feeder), "activate")]
    public static class FeederActivatePatch
    {
        private static void Postfix(Feeder __instance)
        {
            StationSyncHelpers.SendFeederState(__instance, "activate");
        }
    }

    [HarmonyPatch(typeof(Lure), "removeHealth")]
    public static class LureRemoveHealthPatch
    {
        private static void Prefix(Lure __instance)
        {
            if (__instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            // Deterministic gore drops when both peers run the delta path.
            try
            {
                var ctrl = Singleton<Controller>.Instance;
                int day = ctrl != null ? ctrl.day : 0;
                Vector3 p = __instance.transform.position;
                UnityEngine.Random.InitState(day
                    ^ ((int)Mathf.Round(p.x) * 73856093)
                    ^ ((int)Mathf.Round(p.z) * 19349663)
                    ^ __instance.health);
            }
            catch { /* ignore */ }
        }

        private static void Postfix(Lure __instance, Character eatingCharacter)
        {
            if (__instance == null) return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            // Only the machine simulating the eater reports (host AI). Null eater = scripted.
            // Remote proxies / client-mirrored AI must not double-report.
            if (eatingCharacter != null)
            {
                var net = ModRuntime.Network;
                if (net != null && net.IsConnected && net.Role == NetworkRole.Client)
                    return;
            }

            StationSyncHelpers.QueueLureHealth(__instance);
        }
    }
}
