using System.Collections.Generic;
using DG.Tweening;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Video;

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
            CutsceneManager[] all = WorldQueryHelper.GetCachedSceneComponents<CutsceneManager>();
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
                if (dreams == null) return;
                // A start overlay, current cutscene, or outcome transition may be playing.
                if (dreams.currentTransition != null && dreams.currentTransition.isPlaying)
                    dreams.currentTransition.skip();
                if (dreams.startTransition != null && dreams.startTransition.isPlaying
                    && dreams.startTransition != dreams.currentTransition)
                    dreams.startTransition.skip();
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }
            // Early peer entry path uses a timer, not only vanilla isPlaying skip.
            DreamSyncManager.OnEntryTransitionSkipped();
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
    /// Dream entry video starts before prepareDream — broadcast immediately so peers
    /// are not stuck free-roaming while the initiator watches startTransition alone.
    /// </summary>
    [HarmonyPatch(typeof(DreamTransition), "transition")]
    public static class DreamTransitionBeginPatch
    {
        private static void Prefix(DreamTransition __instance)
        {
            if (__instance == null) return;
            if (!CutsceneSyncHelpers.IsMultiplayerConnected()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            // Cutscene / chapter intros use other paths; outcome end transitions run while dreaming.
            if (__instance.isCutsceneTransition) return;
            if (Dreams.Instance != null && Dreams.Instance.dreaming) return;
            // Only the shared pre-dream startTransition (skill / wantToDream entry).
            if (Dreams.Instance != null
                && Dreams.Instance.startTransition != null
                && __instance != Dreams.Instance.startTransition)
                return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            Vector3 pos = __instance.transform.position;
            net.Broadcast(NetMessageType.CutsceneSync,
                w => new CutsceneSyncMessage
                {
                    Action = CutsceneSyncMessage.ActionDreamEntryTransition,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ManagerName = __instance.name ?? "",
                    SceneIndex = 0
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo("[DreamSync] Dream entry transition begin → peers");
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

    /// <summary>
    /// New-game opening movie. It is WorldGenerator.firstPlay, not a CutsceneManager.
    /// The host plays it; everyone else in the session plays the same movie and wakes together.
    /// </summary>
    internal static class PrologueSync
    {
        private static bool _hostEndingIntro;

        internal static bool ClientDeferredFirstPlay;

        private static VideoPlayer _prepared;

        internal static void Reset()
        {
            ClientDeferredFirstPlay = false;
            _hostEndingIntro = false;
            if (_prepared != null)
            {
                _prepared.prepareCompleted -= DisplayIntro;
                _prepared.Stop();
                _prepared = null;
            }
        }

        internal static void SendStartTo(int playerId)
        {
            if (!CutsceneSyncHelpers.IsHost() || playerId <= 0)
                return;
            LanNetworkManager net = LanNetworkManager.Instance;
            if (net == null)
                return;
            net.SendToPlayer(playerId, NetMessageType.CutsceneSync,
                w => new CutsceneSyncMessage
                {
                    Action = CutsceneSyncMessage.ActionPrologueStart,
                    ManagerName = "prologue"
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        internal static void SendEndTo(int playerId)
        {
            if (!CutsceneSyncHelpers.IsHost() || playerId <= 0)
                return;
            LanNetworkManager net = LanNetworkManager.Instance;
            if (net == null)
                return;
            net.SendToPlayer(playerId, NetMessageType.CutsceneSync,
                w => new CutsceneSyncMessage
                {
                    Action = CutsceneSyncMessage.ActionPrologueEnd,
                    ManagerName = "prologue"
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        internal static void HostSignalStart()
        {
            if (!CutsceneSyncHelpers.IsHost() || LanNetworkManager.IsApplyingRemoteState)
                return;
            Broadcast(CutsceneSyncMessage.ActionPrologueStart);
            CutsceneSyncHelpers.SetProxiesHidden(true);
        }

        internal static void HostSignalEnd()
        {
            if (!CutsceneSyncHelpers.IsHost() || LanNetworkManager.IsApplyingRemoteState)
                return;
            Broadcast(CutsceneSyncMessage.ActionPrologueEnd);
            CutsceneSyncHelpers.SetProxiesHidden(false);
        }

        internal static void NoteHostActivate(bool playingIntro)
        {
            _hostEndingIntro = playingIntro;
        }

        internal static void AfterHostActivate()
        {
            if (!_hostEndingIntro)
                return;
            _hostEndingIntro = false;
            HostSignalEnd();
        }

        internal static void ApplyStart()
        {
            UI ui = Singleton<UI>.Instance;
            Controller ctrl = Singleton<Controller>.Instance;
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            if (ui == null || ctrl == null || wg == null)
                return;
            if (wg.playingIntro)
                return;

            wg.playingIntro = true;
            Core.forbidInputs = true;
            CutsceneSyncHelpers.SetProxiesHidden(true);

            LanNetworkManager.IsApplyingRemoteState = true;
            try { ui.showPrologueText(); }
            finally { LanNetworkManager.IsApplyingRemoteState = false; }

            ctrl.Invoke(delegate
            {
                AudioController.Play("DW4_1");
                ctrl.Invoke(delegate { StartVideo(); }, 3f, timeScaleDependent: false);
            }, 4f, timeScaleDependent: false);
        }

        internal static void ApplyEnd()
        {
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            UI ui = Singleton<UI>.Instance;
            if (wg != null)
                wg.playingIntro = false;
            Core.forbidInputs = false;
            Time.timeScale = 1f;
            AudioController.Stop("DW4_1", 8f);
            if (_prepared != null)
            {
                _prepared.prepareCompleted -= DisplayIntro;
                _prepared.Stop();
                _prepared = null;
            }
            CutsceneSyncHelpers.SetProxiesHidden(false);

            if (ui != null && ui.blackScreenTop != null)
            {
                tk2dSprite top = ui.blackScreenTop.GetComponent<tk2dSprite>();
                if (top != null)
                    top.color = new Color(0.9f, 0.9f, 0.9f, 1f);
                ui.blackScreenTop.SetActive(true);
            }
            if (ui != null && ui.blackScreen != null)
                ui.blackScreen.SetActive(false);

            if (ui != null && ui.videoOverlay != null)
            {
                Renderer r = ui.videoOverlay.GetComponent<Renderer>();
                if (r != null && r.material != null)
                {
                    r.material.DOFade(0f, 1f).SetUpdate(true).OnComplete(delegate
                    {
                        ui.videoOverlay.SetActive(false);
                    });
                }
                else
                    ui.videoOverlay.SetActive(false);
            }

            Player player = Player.Instance;
            if (player != null)
            {
                ClientDeferredFirstPlay = false;
                player.modifyFOVDot(200f);
                player._transform.rotation = Quaternion.Euler(90f, 180f, 0f);
                Controller ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ctrl.Invoke(delegate { player.modifyFOVDot(85f, 5f); }, 5f, timeScaleDependent: true);
                try
                {
                    OutsideLocations outs = Singleton<OutsideLocations>.Instance;
                    if (outs != null && outs.spawnedLocations != null
                        && outs.spawnedLocations.ContainsKey("dream_tutorial_00"))
                    {
                        outs.spawnedLocations["dream_tutorial_00"].gameObject.SetActive(true);
                        outs.playerInOutsideLocation = true;
                        Singleton<WorldGrid>.Instance.setGrid("dream_tutorial_00");
                        Singleton<WorldGrid>.Instance.refreshPosition(player.transform.position, instant: true, force: true);
                    }
                }
                catch { /* tutorial pad missing on this save */ }
            }
            Core.showGameCursor();
        }

        private static void StartVideo()
        {
            UI ui = Singleton<UI>.Instance;
            if (ui == null || ui.videoOverlay == null)
                return;
            string lang = "en";
            try { lang = GameSettings.GetString("LanguageCode"); }
            catch { /* default */ }
            if (string.IsNullOrEmpty(lang))
                lang = "en";

            VideoClip clip = Resources.Load("Video/intro_cz1_" + lang, typeof(VideoClip)) as VideoClip;
            if (clip == null)
                clip = Resources.Load("Video/intro_cz1_en", typeof(VideoClip)) as VideoClip;
            if (clip == null)
                return;

            ui.videoOverlay.SetActive(true);
            VideoPlayer player = ui.videoOverlay.GetComponent<VideoPlayer>();
            Renderer renderer = ui.videoOverlay.GetComponent<Renderer>();
            if (player == null)
                return;
            if (renderer != null)
                renderer.enabled = false;
            player.clip = clip;
            player.playOnAwake = false;
            if (_prepared != null)
                _prepared.prepareCompleted -= DisplayIntro;
            _prepared = player;
            player.prepareCompleted += DisplayIntro;
            player.Prepare();
        }

        private static void DisplayIntro(VideoPlayer player)
        {
            player.prepareCompleted -= DisplayIntro;
            UI ui = Singleton<UI>.Instance;
            if (ui == null || ui.videoOverlay == null)
                return;
            Renderer renderer = ui.videoOverlay.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.enabled = true;
                renderer.material.color = new Color(1f, 1f, 1f, 0f);
                renderer.material.DOFade(1f, 2f).SetUpdate(true);
            }
            player.Play();
            Core.hideGameCursor();
        }

        private static void Broadcast(byte action)
        {
            LanNetworkManager net = LanNetworkManager.Instance;
            if (net == null)
                return;
            net.Broadcast(NetMessageType.CutsceneSync,
                w => new CutsceneSyncMessage
                {
                    Action = action,
                    ManagerName = "prologue",
                    SceneIndex = 0
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), "tweenLoading")]
    public static class PrologueClientSuppressPatch
    {
        private static void Prefix()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;
            if (Player.Instance != null && Player.Instance.firstPlay)
            {
                PrologueSync.ClientDeferredFirstPlay = true;
                Player.Instance.firstPlay = false;
            }
        }
    }

    /// <summary>Clients do not start their own opening movie. The host's copy is the one everyone sees.</summary>
    [HarmonyPatch(typeof(UI), "showPrologueText")]
    public static class PrologueTextSyncPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;
            return false;
        }

        private static void Postfix()
        {
            PrologueSync.HostSignalStart();
        }
    }

    [HarmonyPatch(typeof(WorldGenerator), "activatePlayer")]
    public static class PrologueEndSyncPatch
    {
        private static void Prefix(WorldGenerator __instance)
        {
            PrologueSync.NoteHostActivate(__instance != null && __instance.playingIntro);
        }

        private static void Postfix()
        {
            PrologueSync.AfterHostActivate();
        }
    }
}
