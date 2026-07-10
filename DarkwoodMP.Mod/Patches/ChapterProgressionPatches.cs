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
    /// load <c>chapterN</c> together (clients also load via WorldSaveShare apply when
    /// generateSave wrote empty chapter save).
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

            // Clients never start chapter loads — host story GameEvents own this.
            if (net.Role == NetworkRole.Client)
            {
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client blocked local generateChapter({_chapterId}) — wait for host");
                return false;
            }

            // Host
            if (_chapterId < 1)
                _chapterId = 1;

            if (generateSave)
            {
                if (Singleton<WorldGenerator>.Instance != null)
                    Singleton<WorldGenerator>.Instance.chapterID = _chapterId;
                if (Core.currentProfile != null)
                    Core.currentProfile.chapter = _chapterId;

                try
                {
                    if (Singleton<SaveManager>.Instance != null)
                        Singleton<SaveManager>.Instance.saveEmptyChapterSave();
                }
                catch (System.Exception ex)
                {
                    ModLog.Error(LogCat.Save, "saveEmptyChapterSave failed", ex);
                }

                // Tell clients a chapter transition is coming (share will load them).
                net.Broadcast(NetMessageType.ChapterTransition,
                    w => new ChapterTransitionMessage
                    {
                        ChapterId = _chapterId,
                        LoadChapterSave = loadChapterSave,
                        ExpectWorldShare = true
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);

                int chapterId = _chapterId;
                bool loadSave = loadChapterSave;
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Host ch{chapterId} generateSave — share world then load");

                // Push empty-chapter save to clients (they LoadScene in ClientApply), then host loads.
                if (net.WorldSaveShare != null)
                {
                    net.WorldSaveShare.ScheduleHostShareThen(
                        () => ChapterTransitionHelpers.ApplyChapterLoad(chapterId, loadSave, stopNetwork: true),
                        waitForGameSave: false);
                }
                else
                {
                    ChapterTransitionHelpers.ApplyChapterLoad(chapterId, loadSave, stopNetwork: true);
                }

                return false;
            }

            // No empty save — pure scene swap (all peers load same chapter scene).
            net.Broadcast(NetMessageType.ChapterTransition,
                w => new ChapterTransitionMessage
                {
                    ChapterId = _chapterId,
                    LoadChapterSave = loadChapterSave,
                    ExpectWorldShare = false
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ChapterTransitionHelpers.ApplyChapterLoad(_chapterId, loadChapterSave, stopNetwork: true);
            ModLog.Event(LogCat.Session,
                $"[Chapter] Host generateChapter({_chapterId}) coordinated scene load");
            return false;
        }
    }

    internal static class ChapterTransitionHelpers
    {
        private static bool _chapterLoadPending;

        internal static void Reset()
        {
            _chapterLoadPending = false;
        }

        /// <summary>
        /// Set Core flags and LoadScene("chapterN"). Optionally stop multiplayer first.
        /// Idempotent for a single transition wave.
        /// </summary>
        internal static void ApplyChapterLoad(int chapterId, bool loadChapterSave, bool stopNetwork)
        {
            if (chapterId < 1) chapterId = 1;
            if (_chapterLoadPending) return;
            _chapterLoadPending = true;

            string scene = "chapter" + chapterId;
            ModLog.Event(LogCat.Session,
                $"[Chapter] ApplyChapterLoad {scene} loadChapterSave={loadChapterSave} stopNet={stopNetwork}");

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
                    if (stopNetwork && ModRuntime.Network != null && ModRuntime.Network.IsConnected)
                        ModRuntime.Network.StopNetwork();
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

            // Host already applied via generateChapter Prefix.
            if (ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host)
                return;

            // ExpectWorldShare: ClientApplyCoroutine will LoadScene after files land.
            // Still set profile chapter so UI/session match; avoid double LoadScene race
            // unless share never arrives (timeout fallback).
            if (msg.ExpectWorldShare)
            {
                if (Core.currentProfile != null)
                    Core.currentProfile.chapter = msg.ChapterId;
                if (Singleton<WorldGenerator>.Instance != null)
                    Singleton<WorldGenerator>.Instance.chapterID = msg.ChapterId;
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client expect world share for ch{msg.ChapterId} — defer LoadScene to share apply");

                // Fallback if share fails: load after 12s if still not loading.
                var ctrl = Singleton<Controller>.Instance;
                int ch = msg.ChapterId;
                bool loadSave = msg.LoadChapterSave;
                if (ctrl != null)
                {
                    ctrl.Invoke(delegate
                    {
                        if (_chapterLoadPending) return;
                        if (Core.loadingGame || Core.loadedGame) return;
                        // Still on old scene — share never completed.
                        ModLog.Warn(LogCat.Session,
                            $"[Chapter] World share timeout — fallback LoadScene chapter{ch}");
                        ApplyChapterLoad(ch, loadSave, stopNetwork: true);
                    }, 12f, timeScaleDependent: false);
                }
                return;
            }

            ApplyChapterLoad(msg.ChapterId, msg.LoadChapterSave, stopNetwork: true);
        }
    }
}

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleChapterTransition(ChapterTransitionMessage msg)
        {
            DWMPHorde.Patches.ChapterTransitionHelpers.HandleChapterTransition(msg);
        }
    }
}
