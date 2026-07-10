using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.6 Cutscenes / movies — host-authoritative CutsceneManager + transition skip.
    /// Clients do not auto-start cutscenes; host Broadcasts CutsceneSync so all peers
    /// enter/exit together. Remote proxies hidden while playingCutscene.
    /// </summary>
    internal static class CutsceneSyncHelpers
    {
        private static readonly HashSet<int> _hiddenProxyIds = new HashSet<int>();

        internal static bool IsMultiplayerConnected()
        {
            return ModRuntime.Network != null && ModRuntime.Network.IsConnected;
        }

        internal static bool IsHost()
        {
            return IsMultiplayerConnected() && ModRuntime.Network.Role == NetworkRole.Host;
        }

        internal static void HostBroadcast(byte action, CutsceneManager manager, int sceneIndex = 0)
        {
            if (!IsHost()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            Vector3 pos = manager != null ? manager.transform.position : Vector3.zero;
            string name = manager != null ? manager.name : "";

            net.Broadcast(NetMessageType.CutsceneSync,
                w => new CutsceneSyncMessage
                {
                    Action = action,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ManagerName = name,
                    SceneIndex = sceneIndex
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo(
                $"[CutsceneSync] host broadcast action={action} mgr={name} scene={sceneIndex}");
        }

        internal static CutsceneManager FindManager(Vector3 pos, string name)
        {
            CutsceneManager[] all = Object.FindObjectsOfType<CutsceneManager>();
            CutsceneManager best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < all.Length; i++)
            {
                CutsceneManager m = all[i];
                if (m == null) continue;
                if (!string.IsNullOrEmpty(name) && m.name == name)
                {
                    float d = Vector3.Distance(m.transform.position, pos);
                    if (d < 25f)
                        return m;
                }
                float dist = Vector3.Distance(m.transform.position, pos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                }
            }

            if (best != null && bestDist < 40f)
                return best;
            // Fallback: any manager (prologue often has one)
            return all.Length > 0 ? all[0] : null;
        }

        internal static void ApplyBegin(CutsceneSyncMessage msg)
        {
            CutsceneManager mgr = FindManager(new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.ManagerName);
            if (mgr == null)
            {
                ModRuntime.Log?.LogWarning("[CutsceneSync] begin: no CutsceneManager found");
                return;
            }

            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                // Force re-init path: clear private initialized via Traverse if needed.
                var t = Traverse.Create(mgr);
                bool initialized = t.Field("initialized").GetValue<bool>();
                if (!initialized)
                {
                    // Call init() which schedules startScene — same as host.
                    t.Method("init").GetValue();
                }
                else if (!Singleton<Controller>.Instance.playingCutscene)
                {
                    // Already initialized but not playing — jump to startScene.
                    if (msg.SceneIndex >= 0 && msg.SceneIndex < mgr.cutscenes.Count)
                        mgr.currentScene = msg.SceneIndex;
                    mgr.startScene();
                }
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }

            SetProxiesHidden(true);
            ModRuntime.LegacyInfo($"[CutsceneSync] applied begin mgr={mgr.name}");
        }

        internal static void ApplyEnd()
        {
            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null && ctrl.currentCutsceneManager != null)
                {
                    ctrl.currentCutsceneManager.prologue_endCutscene();
                }
                else if (ctrl != null && ctrl.playingCutscene)
                {
                    // No manager ref — clear flags manually like prologue_endCutscene.
                    Core.cantChangeForbidInputs = false;
                    Core.forbidInputs = false;
                    ctrl.playingCutscene = false;
                    ctrl.currentCutscene = null;
                    ctrl.currentCutsceneManager = null;
                    if (Player.Instance != null)
                    {
                        Player.Instance.immobilised = false;
                        Player.Instance.GetComponent<Renderer>().enabled = true;
                        if (Player.Instance.legs != null)
                        {
                            Player.Instance.legs.SetActive(true);
                            var lr = Player.Instance.legs.GetComponent<Renderer>();
                            if (lr != null) lr.enabled = true;
                        }
                    }
                    Core.showGameCursor();
                }
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }

            SetProxiesHidden(false);
            ModRuntime.LegacyInfo("[CutsceneSync] applied end");
        }

        internal static void ApplySkipTransition()
        {
            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                var dreams = Singleton<Dreams>.Instance;
                if (dreams != null && dreams.currentTransition != null && dreams.currentTransition.isPlaying)
                    dreams.currentTransition.skip();
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }
        }

        internal static void SetProxiesHidden(bool hide)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            if (!hide)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy == null) continue;
                    SetProxyRenderers(proxy.gameObject, true);
                }
                _hiddenProxyIds.Clear();
                return;
            }

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                SetProxyRenderers(proxy.gameObject, false);
                if (proxy.PlayerId > 0)
                    _hiddenProxyIds.Add(proxy.PlayerId);
            }
        }

        private static void SetProxyRenderers(GameObject go, bool enabled)
        {
            if (go == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null)
                    r.enabled = enabled;
            }
        }

        internal static void Reset()
        {
            SetProxiesHidden(false);
            _hiddenProxyIds.Clear();
        }
    }

    /// <summary>Clients do not auto-start cutscenes — host kickstarts via CutsceneSync.</summary>
    [HarmonyPatch(typeof(CutsceneManager), "init")]
    public static class CutsceneManagerInitPatch
    {
        private static bool Prefix(CutsceneManager __instance, ref bool __state)
        {
            __state = false;
            if (__instance == null) return true;

            if (!CutsceneSyncHelpers.IsMultiplayerConnected())
            {
                __state = !Traverse.Create(__instance).Field("initialized").GetValue<bool>();
                return true;
            }
            if (LanNetworkManager.IsApplyingRemoteState)
            {
                __state = !Traverse.Create(__instance).Field("initialized").GetValue<bool>();
                return true;
            }
            // Host runs vanilla init; clients wait for host message.
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                ModRuntime.LegacyInfo($"[CutsceneSync] client blocked local init: {__instance.name}");
                return false;
            }

            __state = !Traverse.Create(__instance).Field("initialized").GetValue<bool>();
            return true;
        }

        private static void Postfix(CutsceneManager __instance, bool __state)
        {
            if (__instance == null || !__state) return;
            if (!CutsceneSyncHelpers.IsHost()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            // Host just initialized (first time) — tell peers to begin the same manager.
            CutsceneSyncHelpers.HostBroadcast(CutsceneSyncMessage.ActionBegin, __instance, __instance.currentScene);
            CutsceneSyncHelpers.SetProxiesHidden(true);
        }
    }

    /// <summary>When host finishes whole cutscene sequence, peers must unlock too.</summary>
    [HarmonyPatch(typeof(CutsceneManager), "prologue_endCutscene")]
    public static class CutsceneManagerEndPatch
    {
        private static void Prefix(CutsceneManager __instance)
        {
            if (!CutsceneSyncHelpers.IsHost()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            CutsceneSyncHelpers.HostBroadcast(CutsceneSyncMessage.ActionEnd, __instance, __instance != null ? __instance.currentScene : 0);
        }

        private static void Postfix()
        {
            if (!CutsceneSyncHelpers.IsMultiplayerConnected()) return;
            CutsceneSyncHelpers.SetProxiesHidden(false);
        }
    }

    /// <summary>Hide proxies as soon as a scene starts (host local path).</summary>
    [HarmonyPatch(typeof(CutsceneManager), "startScene")]
    public static class CutsceneManagerStartScenePatch
    {
        private static void Postfix(CutsceneManager __instance)
        {
            if (!CutsceneSyncHelpers.IsMultiplayerConnected()) return;
            if (Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.playingCutscene)
                CutsceneSyncHelpers.SetProxiesHidden(true);
        }
    }

    /// <summary>
    /// DreamTransition.skip — host fans out so video overlays end together.
    /// Clients request via Broadcast (Send to host) + Forwardable.
    /// </summary>
    [HarmonyPatch(typeof(DreamTransition), "skip")]
    public static class DreamTransitionSkipPatch
    {
        private static void Prefix(DreamTransition __instance)
        {
            if (__instance == null || !__instance.skippable) return;
            if (!CutsceneSyncHelpers.IsMultiplayerConnected()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            Vector3 pos = __instance.transform.position;
            net.Broadcast(NetMessageType.CutsceneSync,
                w => new CutsceneSyncMessage
                {
                    Action = CutsceneSyncMessage.ActionSkipTransition,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ManagerName = __instance.name ?? "",
                    SceneIndex = 0
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleCutsceneSync(CutsceneSyncMessage msg)
        {
            // Host already applied locally; only apply on non-originators.
            // When host receives client skip, rebroadcast is Forwardable — host should skip too.
            switch (msg.Action)
            {
                case CutsceneSyncMessage.ActionBegin:
                    if (_role == NetworkRole.Host && _currentReceivePlayerId <= 0)
                        return; // shouldn't happen; begin is host-originated
                    if (_role == NetworkRole.Host)
                        return; // host already running
                    DWMPHorde.Patches.CutsceneSyncHelpers.ApplyBegin(msg);
                    break;

                case CutsceneSyncMessage.ActionEnd:
                    if (_role == NetworkRole.Host)
                        return;
                    DWMPHorde.Patches.CutsceneSyncHelpers.ApplyEnd();
                    break;

                case CutsceneSyncMessage.ActionSkipTransition:
                    // Everyone including host (if client skipped) applies skip under guard.
                    if (LanNetworkManager.IsApplyingRemoteState) return;
                    DWMPHorde.Patches.CutsceneSyncHelpers.ApplySkipTransition();
                    break;
            }
        }
    }
}
