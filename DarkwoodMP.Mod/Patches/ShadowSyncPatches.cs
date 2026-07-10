using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Small component attached to each shadow instance so the client can look up
    /// a shadow by its host-assigned ID when receiving periodic state updates.
    /// </summary>
    public class ShadowSyncInfo : MonoBehaviour
    {
        public short ShadowId;
        public byte ShadowType; // 0 = regular, 1 = immortal
    }

    /// <summary>
    /// Host: when Player.tryToSpawnShadow() is called, notify client to set up
    /// CharacterSpawner flags (spawnedShadows, shadowsRemove, etc.).
    /// Client: the corresponding handler sets flags only — host sends individual
    /// spawn messages for each shadow with exact positions.
    /// </summary>
    [HarmonyPatch(typeof(Player), "tryToSpawnShadow")]
    public static class HostShadowSyncPatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            net.SendShadowEvent(new ShadowEventMessage());
        }
    }

    /// <summary>
    /// Host: intercept shadow prefab spawning and:
    /// 1. Send ShadowSpawnMessage to client with exact position + shadow ID
    /// 2. Register the shadow in the host tracker for continuous state updates
    /// 3. Also spawn a copy near the remote proxy so shadows attack both players
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class ShadowCaptureOnSpawnPatch
    {
        private static bool _spawningProxyShadow;

        private static void Postfix(GameObject __result, object[] __args)
        {
            string prefab = (string)__args[0];

            if (__result == null) return;
            if (prefab != "characters/fakechars/shadow" && prefab != "characters/fakechars/shadow_immortal")
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            if (!net.IsConnected)
                return;

            // Assign a unique shadow ID and attach metadata
            var info = __result.GetComponent<ShadowSyncInfo>();
            if (info == null)
                info = __result.AddComponent<ShadowSyncInfo>();
            info.ShadowId = net.GetNextShadowId();
            info.ShadowType = (byte)(prefab == "characters/fakechars/shadow_immortal" ? 1 : 0);

            // Register in host tracker
            var sc = __result.GetComponent<ShadowCreature>();
            if (sc != null)
                net.RegisterShadow(info.ShadowId, sc);

            // Send initial spawn to client
            Vector3 pos = __result.transform.position;
            float rotY = __result.transform.rotation.eulerAngles.y;
            net.SendShadowSpawn(new ShadowSpawnMessage
            {
                ShadowId = info.ShadowId,
                ShadowType = info.ShadowType,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotY = rotY,
                DistanceToPlayer = sc != null ? sc.distanceToPlayer : 0f,
                Flags = (byte)((sc != null && sc.dead) ? 2 : 0)
            });

            // Also spawn a shadow near ALL remote proxies so each client player
            // is attacked by shadows too. Each proxy-shadow gets its own ID,
            // is synced to the client, and uses ProxyShadowController for
            // target-redirected AI.
            if (!_spawningProxyShadow)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null) continue;
                    Transform proxyT = proxy.transform;
                    _spawningProxyShadow = true;
                    try
                    {
                        Vector3 proxyPos = proxyT.position;
                        Vector3 spawnPos = Core.randomPosAround(proxyPos, 200f, 400f, canBeInside: true, mustBeInsideGraph: false);
                        Quaternion spawnRot = Quaternion.Euler(90f, UnityEngine.Random.Range(0f, 360f), 0f);
                        GameObject proxyShadow = Core.AddPrefab(prefab, spawnPos, spawnRot, null);
                        if (proxyShadow != null)
                        {
                            var proxySc = proxyShadow.GetComponent<ShadowCreature>();
                            if (proxySc != null)
                            {
                                proxySc.distanceToPlayer = Vector3.Distance(spawnPos, proxyPos);
                                proxySc.speed = 0f;
                                proxySc.speedAggressive = 0f;
                            }
                            var ctrl = proxyShadow.AddComponent<ProxyShadowController>();
                            ctrl.TargetProxy = proxyT;
                        }
                    }
                    finally
                    {
                        _spawningProxyShadow = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Host: when a shadow dies, mark it dead in the tracker (the next broadcast
    /// will skip it and then remove it from the dictionary).
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(ShadowCreature), "die")]
    public static class HostShadowDiePatch
    {
        private static void Prefix(ShadowCreature __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host)
                return;

            var info = __instance.GetComponent<ShadowSyncInfo>();
            if (info != null)
                net.UnregisterShadow(info.ShadowId);
        }
    }
}
