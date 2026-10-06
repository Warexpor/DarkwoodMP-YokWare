using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Chapter world commit handshake: host collects client acks and sends the go (or a refusal);
    /// client acks a committed world, waits for the go, and never loads a save it did not receive.
    /// </summary>
    internal static partial class ChapterTransitionHelpers
    {
        private static void BeginHostCommitWait(LanNetworkManager net, int chapterId, bool loadChapterSave)
        {
            if (_hostCommitWaitRunning) return;
            _hostCommitWaitRunning = true;
            net.StartCoroutine(HostCommitWait(net, chapterId, loadChapterSave));
        }

        /// <summary>
        /// Host: the world files are sent; wait until every client that was told about the chapter
        /// reports its copy written (or failed / left), re-send to failures, then tell clients to go
        /// and tear down. A client that never confirms is told to stay out and the failure is logged;
        /// nobody silently loads a world they do not have.
        /// </summary>
        private static IEnumerator HostCommitWait(LanNetworkManager net, int chapterId, bool loadChapterSave)
        {
            // Per-peer deadline: it starts when the share finished being queued, not delivered, and
            // moves out every time that peer proves progress (receiving heartbeat, ack, re-send).
            _hostPeerDeadline.Clear();
            float hardCap = Time.unscaledTime + HostMaxTotalWaitSec;
            float startAt = Time.unscaledTime + HostAckTimeoutSec;
            foreach (int pid in _hostExpectedAcks)
                _hostPeerDeadline[pid] = startAt;
            var committed = new List<int>();
            var refused = new Dictionary<int, string>();

            while (net != null && net.Role == NetworkRole.Host)
            {
                bool pending = false;
                float now = Time.unscaledTime;
                foreach (int pid in new List<int>(net.GetHandshakedPeerIds()))
                {
                    if (!_hostExpectedAcks.Contains(pid) || committed.Contains(pid) || refused.ContainsKey(pid))
                        continue;
                    if (!_hostAcks.TryGetValue(pid, out byte status))
                    {
                        // No verdict yet. A peer that stopped making progress is refused on its own
                        // deadline (it gets an explicit ChapterLoadGo(Proceed=false) below); the
                        // others keep waiting.
                        _hostPeerDeadline.TryGetValue(pid, out float peerDeadline);
                        if (now >= hardCap)
                            refused[pid] = "no confirmation within " + (int)HostMaxTotalWaitSec + "s";
                        else if (now >= peerDeadline)
                            refused[pid] = "no confirmation within " + (int)HostAckTimeoutSec + "s of its last progress";
                        else
                            pending = true;
                        continue;
                    }
                    if (status == ChapterShareAckMessage.StatusCommitted)
                    {
                        committed.Add(pid);
                    }
                    else if (status == ChapterShareAckMessage.StatusFailed)
                    {
                        _hostAcks.Remove(pid);
                        _hostShareRetries.TryGetValue(pid, out int tries);
                        if (tries < HostShareRetryMax && net.WorldSaveShare != null)
                        {
                            _hostShareRetries[pid] = tries + 1;
                            ModLog.Warn(LogCat.Session,
                                $"[Chapter] p{pid} could not verify chapter{chapterId} world — re-sending (try {tries + 1}/{HostShareRetryMax})");
                            net.WorldSaveShare.ScheduleHostShareToPlayer(pid);
                            _hostPeerDeadline[pid] = Time.unscaledTime + HostAckTimeoutSec;
                            pending = true;
                        }
                        else
                        {
                            refused[pid] = "chapter world transfer failed";
                        }
                    }
                    // StatusNotInWorld: a title client does not take part in the transition.
                }

                if (!pending)
                    break;
                yield return null;
            }

            if (net != null && net.Role == NetworkRole.Host)
            {
                foreach (int pid in committed)
                    net.SendToPlayer(pid, NetMessageType.ChapterLoadGo,
                        w => new ChapterLoadGoMessage { ChapterId = chapterId, Proceed = true }.Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                foreach (var kvp in refused)
                {
                    int pid = kvp.Key;
                    ModLog.Error(LogCat.Session,
                        $"[Chapter] p{pid} did not receive the chapter{chapterId} world ({kvp.Value}) — "
                        + "told to leave without loading; it must rejoin to download the host world");
                    string reason = WorldSharePolicy.FormatShareFailure(kvp.Value)
                        + " The host has moved on to chapter " + chapterId + ".";
                    net.SendToPlayer(pid, NetMessageType.ChapterLoadGo,
                        w => new ChapterLoadGoMessage { ChapterId = chapterId, Proceed = false, Reason = reason }.Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                }
                ModLog.Event(LogCat.Session,
                    $"[Chapter] Host commit wait done: {committed.Count} committed, {refused.Count} refused — loading chapter{chapterId}");
            }
            else
            {
                ModLog.Warn(LogCat.Session, "[Chapter] Host network gone during commit wait — loading chapter anyway");
            }

            _hostAckCollecting = false;
            _hostCommitWaitRunning = false;
            _hostPeerDeadline.Clear();
            ApplyChapterLoad(chapterId, loadChapterSave, resumeAfter: true);
        }

        /// <summary>
        /// Host: the world share was restarted (a broadcast re-run). Acks counted so far vouch for the
        /// files sent before, not these, so only acks for the new pass count.
        /// </summary>
        internal static void HostShareRestarted()
        {
            if (!_hostAckCollecting)
                return;
            // Acks for the previous pass still in flight are rejected by pass number
            // (HandleChapterShareAck). A title client's NotInWorld verdict is pass-independent: it
            // ignores the new Begin and would never re-ack, so it is kept.
            _hostSharePass++;
            var notInWorld = new List<int>();
            foreach (var kvp in _hostAcks)
                if (kvp.Value == ChapterShareAckMessage.StatusNotInWorld)
                    notInWorld.Add(kvp.Key);
            _hostAcks.Clear();
            foreach (int pid in notInWorld)
                _hostAcks[pid] = ChapterShareAckMessage.StatusNotInWorld;
            float until = Time.unscaledTime + HostAckTimeoutSec;
            foreach (int pid in _hostExpectedAcks)
                _hostPeerDeadline[pid] = until;
        }

        // Share pass numbering: every broadcast Begin opens a new pass (per-peer re-sends reuse
        // the running one); a chapter ack echoes the pass it verified.
        private static int _hostSharePass;
        private static int _clientSharePass;

        internal static int HostSharePass => _hostSharePass;
        internal static int NextHostSharePass() => ++_hostSharePass;

        /// <summary>Client: the Begin of the package now being received carries this pass.</summary>
        internal static void ClientNoteSharePass(int pass) => _clientSharePass = pass;

        /// <summary>
        /// Session end. Safe across the chapter reload: every pass is begun, acked and closed
        /// (ChapterLoadGo) before the teardown StopNetwork, and the next session's first share
        /// opens a fresh pass on both sides; acks cannot cross sessions (new peers).
        /// </summary>
        internal static void ResetSharePasses()
        {
            _hostSharePass = 0;
            _clientSharePass = 0;
        }

        /// <summary>Client: a newer share superseded the package held (or being verified) for the go.</summary>
        internal static void ClientShareSuperseded()
        {
            _clientAwaitingGo = false;
            _clientGoGen++;
        }

        /// <summary>Host: a client reported the outcome of its chapter world share.</summary>
        internal static void HandleChapterShareAck(ChapterShareAckMessage msg)
        {
            if (!NetGuard.Host(out var net))
                return;
            int pid = net.CurrentReceivePlayerId;
            if (pid <= 1)
                return;

            ModLog.Event(LogCat.Session,
                $"[Chapter] Ack from p{pid}: ch{msg.ChapterId} status={msg.Status}"
                + (string.IsNullOrEmpty(msg.Reason) ? "" : " (" + msg.Reason + ")"));

            if (_hostAckCollecting)
            {
                // Any message from the peer is progress: move its deadline out.
                _hostPeerDeadline[pid] = Time.unscaledTime + HostAckTimeoutSec;
                // A receiving heartbeat carries no verdict (and must not overwrite a committed ack).
                if (msg.Status == ChapterShareAckMessage.StatusReceiving)
                    return;
                // A verdict about an earlier pass (the share was re-run since) vouches for files
                // this peer no longer holds; the new Begin superseded them on the client.
                if (msg.Status != ChapterShareAckMessage.StatusNotInWorld && msg.SharePass != _hostSharePass)
                {
                    ModLog.Event(LogCat.Session,
                        $"[Chapter] Ack from p{pid} is for share pass {msg.SharePass} (current {_hostSharePass}) — ignored");
                    return;
                }
                _hostAcks[pid] = msg.Status;
                return;
            }
            if (msg.Status == ChapterShareAckMessage.StatusReceiving)
                return;

            // No coordinated transition running: a single-peer world resync failed on the client.
            if (msg.Status != ChapterShareAckMessage.StatusFailed || net.WorldSaveShare == null)
                return;
            _hostShareRetries.TryGetValue(pid, out int tries);
            if (tries >= HostShareRetryMax)
            {
                ModLog.Error(LogCat.Session,
                    $"[Chapter] p{pid} still cannot commit the host world after {tries} re-sends — giving up; it must rejoin");
                net.RejectPeerWorld(pid, msg.ChapterId,
                    WorldSharePolicy.FormatShareFailure("host world could not be written after " + tries + " retries"));
                return;
            }
            _hostShareRetries[pid] = tries + 1;
            ModLog.Warn(LogCat.Session, $"[Chapter] Re-sending world to p{pid} (try {tries + 1}/{HostShareRetryMax})");
            net.WorldSaveShare.ScheduleHostShareToPlayer(pid);
        }

        // --- client: coordinated chapter world commit ---

        /// <summary>
        /// Client: the chapter world was received, verified and inflated into memory (the save slot is
        /// NOT written yet). When the host is coordinating, ack it and wait for its go; the slot is
        /// committed only at the go (returns true: caller must NOT write or enter the world).
        /// </summary>
        internal static bool ClientChapterVerified(int chapterId)
        {
            var net = ModRuntime.Network;
            if (!_clientAckRequired || net == null || !net.IsConnected)
                return false;

            SendAck(net, chapterId > 0 ? chapterId : _clientChapterId,
                ChapterShareAckMessage.StatusCommitted, null);
            _clientAwaitingGo = true;
            int gen = ++_clientGoGen;
            ModLog.Event(LogCat.Session,
                $"[Chapter] Client verified chapter{chapterId} world (held in memory, slot untouched) — waiting for host go (max {ClientGoTimeoutSec:F0}s)");

            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
            {
                ctrl.Invoke(delegate
                {
                    if (gen != _clientGoGen || !_clientAwaitingGo) return;
                    // Backstop only: the host sends an explicit go or refusal to every peer it waited on.
                    ClientLeaveSession(WorldSharePolicy.FormatShareFailure(
                        "host never confirmed the chapter load within " + (int)ClientGoTimeoutSec + "s"));
                }, ClientGoTimeoutSec, timeScaleDependent: false);
            }
            return true;
        }

        /// <summary>
        /// Client: heartbeat while the chapter package is still arriving, so the host's per-peer ack
        /// deadline follows delivery instead of expiring while a slow link is still downloading.
        /// </summary>
        internal static void ClientShareProgress(bool force)
        {
            if (!_clientAckRequired || !ChapterShareExpected)
                return;
            if (!NetGuard.Connected(out var net))
                return;
            float now = Time.unscaledTime;
            if (!force && now - _clientProgressAckAt < ClientProgressAckIntervalSec)
                return;
            _clientProgressAckAt = now;
            SendAck(net, _clientChapterId, ChapterShareAckMessage.StatusReceiving, null);
        }

        /// <summary>
        /// Client: the chapter world could not be received or written. Tell the host (it re-sends)
        /// and keep waiting; nothing loads a save this client does not have.
        /// </summary>
        internal static void ClientChapterShareFailed(string reason)
        {
            if (!ChapterShareExpected)
                return;
            var net = ModRuntime.Network;
            ModLog.Error(LogCat.Save, "[Chapter] Client chapter world failed: " + reason);
            if (net == null || !net.IsConnected)
                return;
            SendAck(net, _clientChapterId, ChapterShareAckMessage.StatusFailed, reason);
        }

        /// <summary>Host → client go / refusal for the chapter load.</summary>
        internal static void HandleChapterLoadGo(ChapterLoadGoMessage msg)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client)
                return;

            if (!msg.Proceed)
            {
                ClientLeaveSession(string.IsNullOrEmpty(msg.Reason)
                    ? WorldSharePolicy.FormatShareFailure("host refused the chapter load")
                    : msg.Reason);
                return;
            }

            if (!_clientAwaitingGo)
            {
                ModLog.Warn(LogCat.Session, "[Chapter] Host go ignored — no committed chapter world is waiting");
                return;
            }
            _clientAwaitingGo = false;
            _clientGoGen++;
            ModLog.Event(LogCat.Session, $"[Chapter] Host go for chapter{msg.ChapterId} — committing the held world and entering it");
            // The slot is written only now (atomic swap), then entering drops the link. Do it on the
            // next frame, not inside the packet handler.
            Defer(() =>
            {
                var share = net.WorldSaveShare;
                string commitError = "no network";
                if (share == null || !share.TryCommitBufferedChapterAndEnter(out commitError))
                    ClientLeaveSessionNow(WorldSharePolicy.FormatShareFailure(
                        "chapter world could not be written or entered: " + (commitError ?? "unknown")));
            });
        }

        private static void SendAck(LanNetworkManager net, int chapterId, byte status, string reason)
        {
            net.Send(NetMessageType.ChapterShareAck,
                w => new ChapterShareAckMessage
                {
                    ChapterId = chapterId,
                    Status = status,
                    Reason = reason,
                    SharePass = _clientSharePass
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Client: leave the session with a visible reason instead of carrying on in a world the
        /// host has moved past (host migration on that disconnect would promote a stale world).
        /// </summary>
        private static void ClientLeaveSession(string message)
        {
            // Called from packet handlers: tear the transport on the next frame, not inside PollEvents.
            Defer(() => ClientLeaveSessionNow(message));
        }

        private static void Defer(System.Action action)
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
                ctrl.Invoke(delegate { action(); }, 0.05f, timeScaleDependent: false);
            else
                action();
        }

        private static void ClientLeaveSessionNow(string message)
        {
            ModLog.Error(LogCat.Session, "[Chapter] Leaving session: " + message);
            ChapterWaitScreen.Release();
            ChapterSessionResume.Reset();
            var net = ModRuntime.Network;
            // Abort / timeout / refusal: the buffered package is dropped and the save slot stays as it was.
            net?.WorldSaveShare?.DiscardPendingChapterPackage();
            if (net != null)
            {
                try { net.StopNetwork(); }
                catch (System.Exception ex) { ModLog.Error(LogCat.Session, "[Chapter] StopNetwork failed", ex); }
                net.StatusText = message;
                net.WorldSaveShare?.SetWrongSaveProgress(message);
            }
            try
            {
                if (Player.Instance != null && !GameScreen.AtTitle && !Core.loadingGame)
                {
                    PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage(message); }
                    finally { PersonalFlavorHud.EndBypass(); }
                }
            }
            catch { /* non-fatal */ }
        }

        /// <summary>
        /// Client: nothing usable has arrived yet. Keep waiting (a resync can sit behind the host's own
        /// load), ask the host to send again once, and give up with a clear error. Never loads a
        /// chapter save this client did not receive.
        /// </summary>
        private static void ScheduleChapterShareFallback(Controller ctrl, int generation)
        {
            if (ctrl == null) return;
            ctrl.Invoke(delegate
            {
                if (generation != _shareFallbackGen) return;
                if (_chapterLoadPending) return;
                if (Core.loadingGame) return;
                if (!ChapterShareExpected || _clientAwaitingGo) return;

                // Title clients take the normal download / slot pick / ENTER WORLD flow; no timeout applies.
                if (GameScreen.AtTitle || Player.Instance == null) return;

                var shareNet = ModRuntime.Network;
                bool busy = shareNet != null && shareNet.WorldSaveShare != null && shareNet.WorldSaveShare.IsBusy;
                if (busy)
                {
                    // Receiving / applying: progress is being made, do not count it against the wait.
                    ModLog.Event(LogCat.Session,
                        $"[Chapter] World share still moving — wait again for chapter{_clientChapterId}");
                    ScheduleChapterShareFallback(ctrl, generation);
                    return;
                }

                _shareFallbackWaits++;
                if (_shareFallbackWaits >= ShareFallbackMaxWaits)
                {
                    ClientLeaveSession(WorldSharePolicy.FormatShareFailure(
                        "chapter world never arrived from the host"));
                    return;
                }

                if (_shareFallbackWaits == ShareFallbackAskAfter && shareNet != null && shareNet.IsConnected)
                {
                    ModLog.Warn(LogCat.Session,
                        $"[Chapter] No chapter{_clientChapterId} world received yet — asking the host to send it again");
                    SendAck(shareNet, _clientChapterId, ChapterShareAckMessage.StatusFailed,
                        "no chapter world received");
                }
                ScheduleChapterShareFallback(ctrl, generation);
            }, ShareFallbackIntervalSec, timeScaleDependent: false);
        }
    }
}
