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

        /// <summary>Session boundary: realtime stamps of departed peers must not throttle new ones.</summary>
        public static void Reset() => Last.Clear();
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
            if (!NetGuard.Connected(out var net))
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

            if (!NetGuard.Connected(out var net))
                return true;

            // Remote-owned shadow must not slap the host local player
            if (info.OwnerPlayerId != net.LocalPlayerId)
                return false;

            return true;
        }
    }
}
