using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-side owner for the next shadow AddPrefab (stack for nested/delayed spawns).
    /// 0 / empty → host LocalPlayerId.
    /// </summary>
    public static class ShadowSpawnContext
    {
        private static readonly System.Collections.Generic.Stack<int> _ownerStack
            = new System.Collections.Generic.Stack<int>();

        public static void PushOwner(int ownerPlayerId) => _ownerStack.Push(ownerPlayerId);

        public static void PopOwner()
        {
            if (_ownerStack.Count > 0)
                _ownerStack.Pop();
        }

        public static int CurrentOwnerOrHost(int hostPlayerId)
            => _ownerStack.Count > 0 ? _ownerStack.Peek() : hostPlayerId;
    }

    /// <summary>
    /// Small component attached to each shadow instance so the client can look up
    /// a shadow by its host-assigned ID when receiving periodic state updates.
    /// </summary>
    public class ShadowSyncInfo : MonoBehaviour
    {
        public short ShadowId;
        public byte ShadowType; // 0 = regular, 1 = immortal
        /// <summary>Perk/ambient owner player id — damage only hits this player.</summary>
        public int OwnerPlayerId;
    }

    /// <summary>
    /// Host: when Player.tryToSpawnShadow() is called, notify client to set up
    /// CharacterSpawner flags (spawnedShadows, shadowsRemove, etc.).
    /// </summary>
    [HarmonyPatch(typeof(Player), "tryToSpawnShadow")]
    public static class HostShadowSyncPatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            net.SendShadowEvent(new ShadowEventMessage());
        }
    }

    /// <summary>
    /// Host: intercept shadow prefab spawning — assign id, owner, sync to clients.
    /// No multi-proxy fan-out (NightShadows is a per-owner curse).
    /// </summary>
    /// <remarks>Applied from <see cref="CoreAddPrefabStringPatch"/> (one detour for all features).</remarks>
    public static class ShadowCaptureOnSpawnPatch
    {
        internal static void OnAddPrefab(GameObject __result, string prefab)
        {
            if (__result == null) return;
            if (prefab != "characters/fakechars/shadow" && prefab != "characters/fakechars/shadow_immortal")
                return;

            if (!NetGuard.Host(out var net))
                return;
            if (!net.IsConnected)
                return;

            int ownerId = ShadowSpawnContext.CurrentOwnerOrHost(net.LocalPlayerId);

            var info = __result.GetComponent<ShadowSyncInfo>();
            if (info == null)
                info = __result.AddComponent<ShadowSyncInfo>();
            info.ShadowId = net.Shadows.MintId();
            info.ShadowType = (byte)(prefab == "characters/fakechars/shadow_immortal" ? 1 : 0);
            info.OwnerPlayerId = ownerId;

            // Client-owned waves: retarget AI to that proxy (vanilla always uses Player.Instance).
            if (ownerId != net.LocalPlayerId)
            {
                RemotePlayerProxy proxy = net.GetProxy(ownerId);
                if (proxy != null)
                {
                    var scProxy = __result.GetComponent<ShadowCreature>();
                    if (scProxy != null)
                    {
                        scProxy.distanceToPlayer = Vector3.Distance(
                            __result.transform.position, proxy.transform.position);
                    }

                    if (__result.GetComponent<ProxyShadowController>() == null)
                    {
                        var ctrl = __result.AddComponent<ProxyShadowController>();
                        ctrl.TargetProxy = proxy.transform;
                        if (scProxy != null)
                        {
                            // Vanilla Update must not cruise toward the host. The
                            // controller still needs the prefab speeds to close.
                            ctrl.CruiseSpeed = scProxy.speed;
                            ctrl.AggroSpeed = scProxy.speedAggressive;
                            ctrl.SpeedsOverridden = true;
                            scProxy.speed = 0f;
                            scProxy.speedAggressive = 0f;
                        }
                    }
                }
            }

            var sc = __result.GetComponent<ShadowCreature>();
            if (sc != null)
                net.Shadows.Register(info.ShadowId, sc);

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
        }
    }

    /// <summary>
    /// Host: when a shadow dies, mark it dead in the tracker (the next broadcast
    /// will skip it and then remove it from the dictionary).
    /// </summary>
    [HarmonyPatch(typeof(ShadowCreature), "die")]
    public static class HostShadowDiePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(ShadowCreature __instance)
        {
            if (!NetGuard.Host(out var net))
                return;

            var info = __instance.GetComponent<ShadowSyncInfo>();
            if (info != null)
                net.WorldSendHandlers.UnregisterShadow(info.ShadowId);
        }
    }
}
