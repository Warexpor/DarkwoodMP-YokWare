using System.Collections;
using System.Collections.Generic;
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

            var net = ModRuntime.Network;
            if (net == null) return true;

            // Clients never start a new chapter. A permadeath "start over" reload
            // of the current chapter is a request the host runs for the whole party.
            if (net.Role == NetworkRole.Client)
            {
                int chapter = _chapterId < 1 ? 1 : _chapterId;
                bool startOver = PermadeathStartOverPatch.Requested;
                net.Send(NetMessageType.ChapterTransition,
                    w => new ChapterTransitionMessage
                    {
                        ChapterId = chapter,
                        LoadChapterSave = loadChapterSave,
                        ExpectWorldShare = generateSave,
                        StartOver = startOver
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                PermadeathStartOverPatch.Requested = false;
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client requested generateChapter({chapter}) — host decides");
                return false;
            }

            // Host
            if (_chapterId < 1)
                _chapterId = 1;

            ChapterTransitionHelpers.HostCoordinatedChapter(_chapterId, generateSave, loadChapterSave,
                startOver: PermadeathStartOverPatch.Requested);
            PermadeathStartOverPatch.Requested = false;
            return false;
        }
    }

    internal static partial class ChapterTransitionHelpers
    {
        /// <summary>Host: longest wait for every client to confirm the chapter world is written.</summary>
        private const float HostAckTimeoutSec = 90f;
        /// <summary>Host: how many times a failed client gets the world re-sent.</summary>
        private const int HostShareRetryMax = 2;
        /// <summary>Host: longest wait for clients to drop the link after their go, before the scene tear.</summary>
        private const float HostPeerDrainTimeoutSec = 5f;
        /// <summary>Client: longest wait for the host's go after acking a committed world.</summary>
        // Must outlast the host's retry-extended ack wait, or a committed client leaves early.
        private const float ClientGoTimeoutSec = HostAckTimeoutSec * (1 + HostShareRetryMax) + 30f;
        /// <summary>
        /// Host: hard ceiling on the whole commit wait, however much progress peers report. Kept below
        /// the client's go timeout (which only starts when that client acks) so the host always sends
        /// an explicit go or refusal before any client gives up on its own.
        /// </summary>
        private const float HostMaxTotalWaitSec = ClientGoTimeoutSec - 30f;
        private const float ShareFallbackIntervalSec = 12f;
        /// <summary>Client: idle checks (nothing received) before it asks the host to send again.</summary>
        private const int ShareFallbackAskAfter = 2;
        /// <summary>Client: idle checks before it gives up (a resync can wait on the host's own load).</summary>
        private const int ShareFallbackMaxWaits = 15;

        private static bool _chapterLoadPending;
        private static int _shareFallbackWaits;
        private static int _shareFallbackGen;

        // Host: ack collection for a coordinated chapter world share.
        private static bool _hostAckCollecting;
        private static bool _hostCommitWaitRunning;
        private static readonly HashSet<int> _hostExpectedAcks = new HashSet<int>();
        private static readonly Dictionary<int, byte> _hostAcks = new Dictionary<int, byte>();
        private static readonly Dictionary<int, int> _hostShareRetries = new Dictionary<int, int>();
        /// <summary>Host: per-peer ack deadline (unscaled time), pushed out by each sign of progress.</summary>
        private static readonly Dictionary<int, float> _hostPeerDeadline = new Dictionary<int, float>();

        // Client: coordinated load gate.
        private static bool _clientAckRequired;
        private static bool _clientAwaitingGo;
        private static int _clientGoGen;
        private static int _clientChapterId;
        private static float _clientProgressAckAt;
        /// <summary>Client: seconds between receiving heartbeats while a chapter package streams in.</summary>
        private const float ClientProgressAckIntervalSec = 3f;

        /// <summary>Client is in a chapter and waiting for the host's new save before LoadScene.</summary>
        internal static bool ChapterShareExpected { get; private set; }

        internal static void Reset()
        {
            _chapterLoadPending = false;
            ChapterShareExpected = false;
            _shareFallbackWaits = 0;
            _shareFallbackGen++;
            _hostAckCollecting = false;
            _hostCommitWaitRunning = false;
            _hostExpectedAcks.Clear();
            _hostAcks.Clear();
            _hostShareRetries.Clear();
            _hostPeerDeadline.Clear();
            _clientProgressAckAt = 0f;
            _clientAckRequired = false;
            _clientAwaitingGo = false;
            _clientGoGen++;
            _clientChapterId = 0;
        }

        /// <summary>
        /// Host story path and accepted client reload. Broadcasts one transition,
        /// then loads. Does not call generateChapter (that patch is the caller).
        /// </summary>
        internal static void HostCoordinatedChapter(int chapterId, bool generateSave, bool loadChapterSave, bool startOver = false)
        {
            if (chapterId < 1) chapterId = 1;
            var net = ModRuntime.Network;
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

                // Every client present now must confirm the new world before the network is torn down.
                _hostAckCollecting = true;
                _hostAcks.Clear();
                _hostShareRetries.Clear();
                _hostExpectedAcks.Clear();
                foreach (int id in net.GetHandshakedPeerIds())
                    _hostExpectedAcks.Add(id);

                net.Broadcast(NetMessageType.ChapterTransition,
                    w => new ChapterTransitionMessage
                    {
                        ChapterId = chapterId,
                        LoadChapterSave = loadChapterSave,
                        ExpectWorldShare = true,
                        AckRequired = true
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);

                ModLog.Event(LogCat.Session,
                    $"[Chapter] Host ch{chapterId} generateSave — share world, wait for {_hostExpectedAcks.Count} client ack(s), then load + resume");

                if (net.WorldSaveShare != null)
                {
                    net.WorldSaveShare.ScheduleHostShareThen(
                        () => BeginHostCommitWait(net, chapterId, loadChapterSave),
                        waitForGameSave: false);
                }
                else
                {
                    _hostAckCollecting = false;
                    ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true);
                }
                return;
            }

            net.Broadcast(NetMessageType.ChapterTransition,
                w => new ChapterTransitionMessage
                {
                    ChapterId = chapterId,
                    LoadChapterSave = loadChapterSave,
                    ExpectWorldShare = false,
                    StartOver = startOver
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            // The permadeath start-over: nobody keeps pre-wipe progress.
            if (startOver)
                ClientStateBackup.DiscardAllForChapterReload();

            ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true);
            ModLog.Event(LogCat.Session,
                $"[Chapter] Host generateChapter({chapterId}) coordinated scene load + resume");
        }

        /// <summary>
        /// Set Core flags and LoadScene("chapterN"). Stops multiplayer for the scene tear,
        /// then schedules auto rehost/reconnect via <see cref="ChapterSessionResume"/>.
        /// Host: waits for clients to drop the link (their go was sent) before the tear
        /// instead of a fixed delay.
        /// </summary>
        internal static void ApplyChapterLoad(int chapterId, bool loadChapterSave, bool resumeAfter)
        {
            if (chapterId < 1) chapterId = 1;
            ChapterShareExpected = false;
            ChapterWaitScreen.Forget();
            if (_chapterLoadPending) return;
            _chapterLoadPending = true;

            string scene = "chapter" + chapterId;
            ModLog.Event(LogCat.Session,
                $"[Chapter] ApplyChapterLoad {scene} loadChapterSave={loadChapterSave} resumeAfter={resumeAfter}");

            // Capture while peers are still connected: the host roster (stable key -> PlayerId)
            // and the transport identity are gone once they drop the link.
            var netNow = ModRuntime.Network;
            if (resumeAfter && ChapterSessionPolicy.ShouldAutoResumeNetworkAfterChapter
                && netNow != null && netNow.IsConnected && !ChapterSessionResume.IsPending)
                ChapterSessionResume.CaptureForResume(netNow);

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
            // New world: a night death from the old one must not skip this world's morning
            // reward even when no network session was up to run the registry reset.
            DeathStateTracker.ResetSession();

            System.Action load = () =>
            {
                try
                {
                    var net = ModRuntime.Network;
                    if (net != null && net.Role != NetworkRole.Offline
                        && (net.IsConnected || ChapterSessionResume.IsPending))
                        net.StopNetwork(keepSteamLobby: ChapterSessionResume.KeepSteamLobbyOnStop);
                }
                catch (System.Exception ex)
                {
                    ModLog.Error(LogCat.Session, "[Chapter] StopNetwork before LoadScene failed", ex);
                }

                try
                {
                    bool prevApply1 = LanNetworkManager.GetExplicitApplyingRemoteState();
                    LanNetworkManager.IsApplyingRemoteState = true;
                    try
                    {
                        SceneManager.LoadScene(scene);
                    }
                    finally
                    {
                        LanNetworkManager.SetExplicitApplyingRemoteState(prevApply1);
                    }
                }
                catch (System.Exception ex)
                {
                    ModLog.Error(LogCat.Session, "Chapter LoadScene failed", ex);
                    _chapterLoadPending = false;
                    ChapterSessionResume.Reset();
                }
            };

            if (netNow != null && netNow.Role == NetworkRole.Host && netNow.PeerCount > 0)
                netNow.StartCoroutine(DrainPeersThenLoad(netNow, load));
            else
                load();
        }

        /// <summary>
        /// Host: clients drop the link when they get the transition / go. Wait for that (bounded)
        /// so queued reliable packets have actually been delivered before the network is torn down.
        /// </summary>
        private static IEnumerator DrainPeersThenLoad(LanNetworkManager net, System.Action load)
        {
            float until = Time.unscaledTime + HostPeerDrainTimeoutSec;
            while (net != null && net.PeerCount > 0 && Time.unscaledTime < until)
                yield return null;
            if (net != null && net.PeerCount > 0)
                ModLog.Warn(LogCat.Session,
                    $"[Chapter] {net.PeerCount} client(s) still connected {HostPeerDrainTimeoutSec:F0}s after the chapter go — tearing down anyway");
            load();
        }

        internal static void HandleChapterTransition(ChapterTransitionMessage msg)
        {
            if (msg.ChapterId < 1) return;

            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Host)
            {
                if (net.CurrentReceivePlayerId <= 0)
                    return;
                // The client's packet is Forwardable. Do not also fan that copy out;
                // the host reload below broadcasts one coordinated transition.
                net.SuppressRelay();

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
                HostCoordinatedChapter(hostChapter, generateSave: false, loadChapterSave: true, startOver: msg.StartOver);
                return;
            }

            // ExpectWorldShare: ClientApplyCoroutine will LoadScene after files land.
            // The local chapter id is NOT advanced here: until the new save is written the
            // world in memory is still the old chapter, and the handshake identity must say so.
            if (msg.ExpectWorldShare)
            {
                // Vanilla's chapter jump blacks the screen and locks the player while the next
                // chapter is made; on a client that step is the host's, so the client played on
                // through the whole share (open to death and menus) with no cue.
                if (!GameScreen.AtTitle && Player.Instance != null)
                    ChapterWaitScreen.Hold();
                ChapterShareExpected = true;
                _clientChapterId = msg.ChapterId;
                bool inGame = !GameScreen.AtTitle && Player.Instance != null;
                _clientAckRequired = msg.AckRequired && inGame;
                _clientAwaitingGo = false;
                _clientGoGen++;
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Client expect world share for ch{msg.ChapterId} — defer LoadScene to share apply"
                    + (_clientAckRequired ? " (ack + wait for host go)" : ""));

                if (msg.AckRequired && !inGame)
                {
                    // Title client: joins through the normal download / ENTER WORLD flow.
                    if (net != null && net.IsConnected)
                        SendAck(net, msg.ChapterId, ChapterShareAckMessage.StatusNotInWorld, "on title screen");
                }

                // Capture resume early so share-apply LoadScene still rebinds.
                var netEarly = ModRuntime.Network;
                if (netEarly != null && netEarly.IsConnected)
                    ChapterSessionResume.CaptureForResume(netEarly);

                _shareFallbackWaits = 0;
                _shareFallbackGen++;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ScheduleChapterShareFallback(ctrl, _shareFallbackGen);
                return;
            }

            if (msg.StartOver)
                ClientStateBackup.DiscardAllForChapterReload();
            ApplyChapterLoad(msg.ChapterId, msg.LoadChapterSave, resumeAfter: true);
        }
    }

    /// <summary>The permadeath box's "start over" (vanilla Button: reload this chapter's save).</summary>
    [HarmonyPatch(typeof(global::Button), "permadeathBox_startOverFromCh2")]
    public static class PermadeathStartOverPatch
    {
        /// <summary>Set until the chapter reload it leads to is sent.</summary>
        internal static bool Requested; // process-scoped: set by the button, consumed by the generateChapter prefix

        private static void Prefix() => Requested = true;
    }

    /// <summary>Client: black screen, locked and unhurt while the host makes and shares the next chapter.</summary>
    internal static class ChapterWaitScreen
    {
        private static bool _held; // reset-in: Release (called from the chapter leave path and the scene load)

        internal static void Hold()
        {
            if (_held)
                return;
            _held = true;
            Core.forbidInputs = true;
            if (Player.Instance != null)
                Player.Instance.invulnerable = true;
            try { Singleton<UI>.Instance?.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 1f); }
            catch { /* UI not ready */ }
        }

        /// <summary>The share failed and this client stays in the old world: hand it back.</summary>
        internal static void Release()
        {
            if (!_held)
                return;
            _held = false;
            Core.forbidInputs = false;
            if (Player.Instance != null)
                Player.Instance.invulnerable = false;
            try { Singleton<UI>.Instance?.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 1f); }
            catch { /* UI not ready */ }
        }

        /// <summary>The new chapter's scene replaced everything; forget the hold.</summary>
        internal static void Forget() => _held = false;
    }
}
