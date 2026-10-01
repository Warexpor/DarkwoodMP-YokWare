using DG.Tweening;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// New-game opening movie. It is WorldGenerator.firstPlay, not a CutsceneManager.
    /// The host plays it; everyone else in the session plays the same movie and wakes together.
    /// </summary>
    internal static class PrologueSync
    {
        private static bool _hostEndingIntro;
        private static bool _sessionHadPrologue;
        private static int _pendingAction;

        internal static bool ClientDeferredFirstPlay;

        private static VideoPlayer _prepared;

        internal static void Reset()
        {
            ClientDeferredFirstPlay = false;
            _hostEndingIntro = false;
            _sessionHadPrologue = false;
            _pendingAction = 0;
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
            LanNetworkManager net = ModRuntime.Network;
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

        internal static void SendCatchUpTo(int playerId)
        {
            WorldGenerator intro = Singleton<WorldGenerator>.Instance;
            if (intro != null && intro.playingIntro)
                SendStartTo(playerId);
            else if (_sessionHadPrologue || HostIsPastPrologue())
                SendEndTo(playerId);
        }

        /// <summary>
        /// Host finished prologue this campaign (save already past firstPlay) even if
        /// this process never saw showPrologueText (_sessionHadPrologue false after
        /// cold restart). Used so a stuck peer (forbidInputs / playingIntro leftover)
        /// still gets ActionPrologueEnd on soft-reconnect.
        /// </summary>
        private static bool HostIsPastPrologue()
        {
            try
            {
                WorldGenerator wg = Singleton<WorldGenerator>.Instance;
                Player p = Player.Instance;
                if (wg == null || p == null)
                    return false;
                if (wg.playingIntro || p.firstPlay)
                    return false;
                return Core.loadedGame || Core.coreStarted;
            }
            catch
            {
                return false;
            }
        }

        internal static void FlushPending()
        {
            if (_pendingAction == 0) return;
            if (Singleton<WorldGenerator>.Instance == null) return;
            int action = _pendingAction;
            _pendingAction = 0;
            if (action == CutsceneSyncMessage.ActionPrologueStart)
                ApplyStart();
            else if (action == CutsceneSyncMessage.ActionPrologueEnd)
                ApplyEnd();
        }

        internal static void SendEndTo(int playerId)
        {
            if (!CutsceneSyncHelpers.IsHost() || playerId <= 0)
                return;
            LanNetworkManager net = ModRuntime.Network;
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
            _sessionHadPrologue = true;
            Broadcast(CutsceneSyncMessage.ActionPrologueStart);
            CutsceneSyncHelpers.SetProxiesHidden(true);
        }

        internal static void HostSignalEnd()
        {
            if (!CutsceneSyncHelpers.IsHost() || LanNetworkManager.IsApplyingRemoteState)
                return;
            _sessionHadPrologue = true;
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
            {
                _pendingAction = CutsceneSyncMessage.ActionPrologueStart;
                return;
            }
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
            if (wg == null)
            {
                _pendingAction = CutsceneSyncMessage.ActionPrologueEnd;
                return;
            }
            // Healthy late-join / day-N peer: already past intro — do not re-flash
            // blackScreen / tutorial pad (ApplyEnd used to run on every End catch-up).
            if (!wg.playingIntro && !ClientDeferredFirstPlay && !Core.forbidInputs)
                return;
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
            LanNetworkManager net = ModRuntime.Network;
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

    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    public static class ProloguePendingFlushPatch
    {
        private static void Postfix()
        {
            PrologueSync.FlushPending();
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
