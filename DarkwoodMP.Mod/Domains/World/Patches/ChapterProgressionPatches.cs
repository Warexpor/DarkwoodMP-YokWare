using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.8 Chapter progression (prolog→ch1→ch2→epilog):
    /// Vanilla <see cref="Controller.generateChapter"/> LoadScene alone leaves clients
    /// stranded. Host-authoritative: chapter flags + optional world share, then all peers
    /// load <c>chapterN</c> together. Network stops for the scene tear, then
    /// <see cref="ChapterSessionResume"/> rehosts and reconnects after the
    /// scene transition.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "generateChapter")]
    public static class GenerateChapterPatch
    {
        private static bool Prefix(int _chapterId, bool generateSave, bool loadChapterSave)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null) return true;

            // Clients never start a new chapter. A permadeath "start over" reload
            // of the current chapter is a request the host runs for the whole party.
            if (net.Role == NetworkRole.Client)
            {
                int chapter = _chapterId < 1 ? 1 : _chapterId;
                net.Send(NetMessageType.ChapterTransition,
                    w => new ChapterTransitionMessage
                    {
                        ChapterId = chapter,
                        LoadChapterSave = loadChapterSave,
                        ExpectWorldShare = generateSave
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client requested generateChapter({chapter}) — host decides");
                return false;
            }

            // Host
            if (_chapterId < 1)
                _chapterId = 1;

            ChapterTransitionHelpers.HostCoordinatedChapter(_chapterId, generateSave, loadChapterSave);
            return false;
        }
    }

    internal static class ChapterTransitionHelpers
    {
        private static bool _chapterLoadPending;
        private static int _shareFallbackWaits;
        private static int _shareFallbackGen;
        /// <summary>Client is in a chapter and waiting for the host's new save before LoadScene.</summary>
        internal static bool ChapterShareExpected { get; private set; }

        internal static void Reset()
        {
            _chapterLoadPending = false;
            ChapterShareExpected = false;
            _shareFallbackWaits = 0;
            _shareFallbackGen++;
        }

        /// <summary>
        /// Host story path and accepted client reload. Broadcasts one transition,
        /// then loads. Does not call generateChapter (that patch is the caller).
        /// </summary>
        internal static void HostCoordinatedChapter(int chapterId, bool generateSave, bool loadChapterSave)
        {
            if (chapterId < 1) chapterId = 1;
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            if (generateSave)
            {
                if (Singleton<WorldGenerator>.Instance != null)
                    Singleton<WorldGenerator>.Instance.chapterID = chapterId;
                if (Core.currentProfile != null)
                    Core.currentProfile.chapter = chapterId;

                try
                {
                    if (Singleton<SaveManager>.Instance != null)
                        Singleton<SaveManager>.Instance.saveEmptyChapterSave();
                }
                catch (System.Exception ex)
                {
                    ModLog.Error(LogCat.Save, "saveEmptyChapterSave failed", ex);
                }

                net.Broadcast(NetMessageType.ChapterTransition,
                    w => new ChapterTransitionMessage
                    {
                        ChapterId = chapterId,
                        LoadChapterSave = loadChapterSave,
                        ExpectWorldShare = true
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);

                ModLog.Event(LogCat.Session,
                    $"[Chapter] Host ch{chapterId} generateSave — share world then load + resume");

                if (net.WorldSaveShare != null)
                {
                    net.WorldSaveShare.ScheduleHostShareThen(
                        () => ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true),
                        waitForGameSave: false);
                }
                else
                {
                    ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true);
                }
                return;
            }

            net.Broadcast(NetMessageType.ChapterTransition,
                w => new ChapterTransitionMessage
                {
                    ChapterId = chapterId,
                    LoadChapterSave = loadChapterSave,
                    ExpectWorldShare = false
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true);
            ModLog.Event(LogCat.Session,
                $"[Chapter] Host generateChapter({chapterId}) coordinated scene load + resume");
        }

        /// <summary>
        /// Set Core flags and LoadScene("chapterN"). Stops multiplayer for the scene tear,
        /// then schedules auto rehost/reconnect via <see cref="ChapterSessionResume"/>.
        /// </summary>
        internal static void ApplyChapterLoad(int chapterId, bool loadChapterSave, bool resumeAfter)
        {
            if (chapterId < 1) chapterId = 1;
            ChapterShareExpected = false;
            if (_chapterLoadPending) return;
            _chapterLoadPending = true;

            string scene = "chapter" + chapterId;
            ModLog.Event(LogCat.Session,
                $"[Chapter] ApplyChapterLoad {scene} loadChapterSave={loadChapterSave} resumeAfter={resumeAfter}");

            try
            {
                DreamSession.End("chapter:" + chapterId);
            }
            catch { /* ignore */ }

            if (Singleton<WorldGenerator>.Instance != null)
                Singleton<WorldGenerator>.Instance.chapterID = chapterId;
            if (Core.currentProfile != null)
                Core.currentProfile.chapter = chapterId;

            Core.loadedGame = false;
            Core.coreStarted = false;
            Core.loadingGame = false;
            if (loadChapterSave)
                Core.doLoadChapterSave = true;

            System.Action load = () =>
            {
                try
                {
                    var net = ModRuntime.Network as LanNetworkManager;
                    if (net != null && net.IsConnected)
                    {
                        if (resumeAfter && ChapterSessionPolicy.ShouldAutoResumeNetworkAfterChapter)
                            ChapterSessionResume.CaptureForResume(net);
                        net.StopNetwork();
                    }
                }
                catch { /* ignore */ }

                try
                {
                    LanNetworkManager.IsApplyingRemoteState = true;
                    try
                    {
                        SceneManager.LoadScene(scene);
                    }
                    finally
                    {
                        LanNetworkManager.IsApplyingRemoteState = false;
                    }
                }
                catch (System.Exception ex)
                {
                    ModLog.Error(LogCat.Session, "Chapter LoadScene failed", ex);
                    _chapterLoadPending = false;
                    ChapterSessionResume.Reset();
                }
            };

            // Brief delay so ChapterTransition packets can leave the host.
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
                ctrl.Invoke(delegate { load(); }, 0.4f, timeScaleDependent: false);
            else
                load();
        }

        internal static void HandleChapterTransition(ChapterTransitionMessage msg)
        {
            if (msg.ChapterId < 1) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.Role == NetworkRole.Host)
            {
                if (net.CurrentReceivePlayerId <= 0)
                    return;
                // The client's packet is Forwardable. Do not also fan that copy out;
                // the host reload below broadcasts one coordinated transition.
                net._suppressForwardThisMessage = true;

                int hostChapter = 1;
                if (Singleton<WorldGenerator>.Instance != null && Singleton<WorldGenerator>.Instance.chapterID > 0)
                    hostChapter = Singleton<WorldGenerator>.Instance.chapterID;
                // New chapters stay host GameEvents. A peer may only reload the chapter
                // the party is already in (permadeath start-over).
                if (msg.ExpectWorldShare || !msg.LoadChapterSave || msg.ChapterId != hostChapter)
                {
                    ModLog.Event(LogCat.Session,
                        $"[Chapter] Rejected client chapter request ch{msg.ChapterId} "
                        + $"share={msg.ExpectWorldShare} loadSave={msg.LoadChapterSave} hostCh={hostChapter}");
                    return;
                }

                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client p{net.CurrentReceivePlayerId} reload chapter{hostChapter}");
                HostCoordinatedChapter(hostChapter, generateSave: false, loadChapterSave: true);
                return;
            }

            // ExpectWorldShare: ClientApplyCoroutine will LoadScene after files land.
            // Still set profile chapter so UI/session match; avoid double LoadScene race
            // unless share never arrives (timeout fallback).
            if (msg.ExpectWorldShare)
            {
                ChapterShareExpected = true;
                if (Core.currentProfile != null)
                    Core.currentProfile.chapter = msg.ChapterId;
                if (Singleton<WorldGenerator>.Instance != null)
                    Singleton<WorldGenerator>.Instance.chapterID = msg.ChapterId;
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client expect world share for ch{msg.ChapterId} — defer LoadScene to share apply");

                // Capture resume early so share-apply LoadScene still rebinds.
                var netEarly = ModRuntime.Network as LanNetworkManager;
                if (netEarly != null && netEarly.IsConnected)
                    ChapterSessionResume.CaptureForResume(netEarly);

                _shareFallbackWaits = 0;
                _shareFallbackGen++;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ScheduleChapterShareFallback(ctrl, msg.ChapterId, msg.LoadChapterSave, _shareFallbackGen);
                return;
            }

            ApplyChapterLoad(msg.ChapterId, msg.LoadChapterSave, resumeAfter: true);
        }

        /// <summary>
        /// If the chapter save is still transferring, wait and check again.
        /// After three waits, load anyway so a stuck transfer cannot leave the party behind.
        /// </summary>
        private static void ScheduleChapterShareFallback(Controller ctrl, int chapterId, bool loadChapterSave, int generation)
        {
            if (ctrl == null) return;
            ctrl.Invoke(delegate
            {
                if (generation != _shareFallbackGen) return;
                if (_chapterLoadPending) return;
                if (Core.loadingGame) return;
                if (!ChapterShareExpected) return;
                var shareNet = ModRuntime.Network as LanNetworkManager;
                bool busy = shareNet != null && shareNet.WorldSaveShare != null && shareNet.WorldSaveShare.IsBusy;
                if (busy && _shareFallbackWaits < 2)
                {
                    _shareFallbackWaits++;
                    ModLog.Event(LogCat.Session,
                        $"[Chapter] World share still moving — wait again ({_shareFallbackWaits}) for chapter{chapterId}");
                    ScheduleChapterShareFallback(ctrl, chapterId, loadChapterSave, generation);
                    return;
                }
                ModLog.Warn(LogCat.Session,
                    $"[Chapter] World share timeout — fallback LoadScene chapter{chapterId}");
                ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true);
            }, 12f, timeScaleDependent: false);
        }
    }
}
