using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Host rate-limit for client NightShadowSpawnRequest (8s per peer).</summary>
    internal static class NightShadowsRateLimit
    {
        private static readonly System.Collections.Generic.Dictionary<int, float> Last =
            new System.Collections.Generic.Dictionary<int, float>();

        public static bool TryAllow(int playerId)
        {
            float now = Time.realtimeSinceStartup;
            if (Last.TryGetValue(playerId, out float prev) && now - prev < 8f)
                return false;
            Last[playerId] = now;
            return true;
        }
    }

    /// <summary>
    /// Decompile left the darknessCounter &gt; 0.4 branch empty. Restore spawn trigger
    /// for the NightShadows perk (host runs full tryToSpawnShadow; client requests host wave).
    /// </summary>
    [HarmonyPatch(typeof(Player), "updateVars")]
    public static class NightShadowsThresholdPatch
    {
        private static float _lastLocalWaveRealtime;

        private static void Postfix(Player __instance)
        {
            if (__instance == null || __instance.skills == null || !__instance.skills.NightShadows)
                return;
            if (Core.isDay()) return;
            if (Singleton<Controller>.Instance == null || !Singleton<Controller>.Instance.isHardNight)
                return;
            if (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming)
                return;
            if (!__instance.alive || __instance.endingSleep)
                return;
            if (__instance.darknessCounter <= 0.4f)
                return;

            // Belt: avoid multi-call same frame / stuck counter edge cases
            float now = Time.realtimeSinceStartup;
            if (now - _lastLocalWaveRealtime < 1f)
                return;
            _lastLocalWaveRealtime = now;

            __instance.tryToSpawnShadow();
        }
    }

    /// <summary>
    /// Client: do not spawn shadows locally (AI is host-driven). Request a host wave instead.
    /// Host: vanilla tryToSpawnShadow continues (Postfix sends ShadowEvent).
    /// </summary>
    [HarmonyPatch(typeof(Player), "tryToSpawnShadow")]
    public static class ClientNightShadowRequestPatch
    {
        private static bool Prefix(Player __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;
            if (net.Role != NetworkRole.Client)
                return true;

            // Mirror vanilla side-effects the host wave will not apply on the client body.
            __instance.darknessCounter -= 0.3f;
            __instance.disableHeldNaturalLight();

            var cs = Singleton<CharacterSpawner>.Instance;
            if (cs != null)
            {
                cs.shadowsRemove = false;
                cs.shadowsPaused = false;
                cs.spawnedShadows = true;
                cs.spawnedShadowsAmount = 8;
            }

            net.Send(NetMessageType.NightShadowSpawnRequest,
                w => new NightShadowSpawnRequestMessage().Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo("[NightShadow] client requested host perk wave");
            return false;
        }
    }

    /// <summary>
    /// Vanilla spawnMeleeSensor hits Player.Instance. Skip when this shadow is owned by a remote peer.
    /// </summary>
    [HarmonyPatch(typeof(ShadowCreature), "spawnMeleeSensor")]
    public static class ShadowMeleeOwnerHostSkipPatch
    {
        private static bool Prefix(ShadowCreature __instance)
        {
            if (__instance == null) return true;
            if (__instance.GetComponent<ProxyShadowController>() != null)
                return false; // ProxyShadowController handles its own sensors

            var info = __instance.GetComponent<ShadowSyncInfo>();
            if (info == null || info.OwnerPlayerId <= 0)
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;

            // Remote-owned shadow must not slap the host local player
            if (info.OwnerPlayerId != net.LocalPlayerId)
                return false;

            return true;
        }
    }
}
