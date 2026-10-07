using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// One-shot host→client transfer of Darkwood save files when the host finishes
    /// generating a new world (or host presses Resend). Client picks a local PLAY profile
    /// for a <b>permanent</b> world copy (mid-menu), then ENTER WORLD offline-loads that
    /// slot. Profile index is always merged from disk before saveGameProfiles.
    /// Dual-box SecondDarkwood uses isolated save roots so host live slots are never hit.
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
        public const int ChunkSize = 16 * 1024;
        /// <summary>Keep low. Dual-box ReliableOrdered traffic can stall the host.</summary>
        private const int MaxChunksPerFrame = 2;
        private const float HostWaitForSaveSeconds = 2.5f;
        private const int MinProfileId = 1;
        private const int MaxProfileId = 5;

        /// <summary>
        /// The save set: static and dynamic world, chapter save, and the cosmetic roll seeds
        /// (<c>Sync.CosmeticRolls.KeyStoreFileName</c>, optional: a save from before it has none).
        /// </summary>
        private static readonly string[] FileNames = { "savs.dat", "sav.dat", "savch.dat", Sync.CosmeticRolls.KeyStoreFileName };

        private readonly LanNetworkManager _net;
        private bool _hostShareRunning;
        private bool _clientReceiving;
        private bool _clientApplying;
        private Action _afterHostShare;
        /// <summary>-1 = all handshaked peers; else only that player id.</summary>
        private int _shareTargetPlayerId = -1;
        /// <summary>
        /// Broadcast share only: the peers that were connected when <c>WorldSaveBegin</c> went out.
        /// Null until Begin is sent. Chunks and End go to exactly this set, and a peer outside it
        /// (joined mid-broadcast, never got Begin) gets its own share once the broadcast ends.
        /// </summary>
        private HashSet<int> _broadcastRecipients;

        /// <summary>
        /// Bumped by <see cref="Reset"/> (and when a newer package supersedes one being applied).
        /// A share/apply coroutine captures it at start and quits when it no longer matches, so a
        /// share from a dead session can never clear <c>_hostShareRunning</c> or fire the next
        /// session's <c>afterShare</c>.
        /// </summary>
        private int _shareGeneration;
        private Coroutine _hostShareCoroutine;

        /// <summary>
        /// A broadcast share was requested while another share was already running. The running one
        /// may have packed older files or covered fewer peers, so everyone gets a fresh share when it
        /// ends; <see cref="_rerunAfter"/> (the chapter load) waits for that second pass.
        /// </summary>
        private bool _rerunBroadcast;
        private bool _rerunWaitForSave;
        private Action _rerunAfter;

        private WorldSaveBeginMessage _pendingBegin;
        private Dictionary<int, byte[][]> _chunkBuffers;
        private int _chunksReceived;
        private int _chunksExpected;
        /// <summary>Host's profile id from the share package (meta only; client picks local slot).</summary>
        private int _hostSourceProfileId;
        /// <summary>Cached uncompressed package fingerprint while slot-pick buffers are held.</summary>
        private string _pendingPackageFingerprint;

        /// <summary>
        /// Download complete in RAM; waiting for permanent profile slot pick (mid-menu).
        /// </summary>
        private bool _awaitingSlotPick;
        /// <summary>
        /// Files on disk + profile paths ready; waiting for user to press ENTER WORLD
        /// before offline Load (phase 2). Transfer link still up so host keeps peer muted.
        /// </summary>
        private bool _awaitingEnterWorld;
        private int _enterProfileId;
        private int _enterChapterId;

        /// <summary>
        /// Chapter share: package received, verified and inflated, held in RAM until the host's go.
        /// The save slot is not touched before the go (and never if the host aborts).
        /// </summary>
        private List<VerifiedFile> _verifiedPackage;
        private bool _awaitingChapterGo;

        public bool IsBusy => _hostShareRunning || _clientReceiving || _clientApplying
            || _awaitingSlotPick || _awaitingEnterWorld;
        /// <summary>Host is packing or sending a world package.</summary>
        public bool IsHostShareRunning => _hostShareRunning;
        /// <summary>Client is mid download, slot pick, or apply of host world package.</summary>
        public bool IsClientReceivingOrApplying =>
            _clientReceiving || _clientApplying || _awaitingSlotPick;
        /// <summary>Chunks ready; show permanent slot picker before writing.</summary>
        public bool IsAwaitingSlotPick => _awaitingSlotPick;
        /// <summary>World package written; client must click ENTER WORLD to offline-load.</summary>
        public bool IsAwaitingEnterWorld => _awaitingEnterWorld;
        /// <summary>Terminal share failure; do not allow ENTER WORLD.</summary>
        public bool HasTerminalShareFailure =>
            WorldSharePolicy.IsShareFailureTerminal
            && WorldSharePolicy.IsShareFailureMessage(ProgressText);
        public string ProgressText { get; private set; } = string.Empty;

        /// <summary>Join/menu: surface a wrong-save / campaign warning without terminal share failure.</summary>
        internal void SetWrongSaveProgress(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            ProgressText = message;
            if (_net != null)
                _net.StatusText = ProgressText;
        }


        public WorldSaveShareService(LanNetworkManager net)
        {
            _net = net;
            // Automatic host saves check IsQuitting; hook it as soon as networking exists.
            WorldSaveGuards.EnsureQuitHook();
        }

        public void Reset()
        {
            // Session over: no share/apply coroutine of the old session may run another line.
            _shareGeneration++;
            if (_hostShareCoroutine != null)
            {
                if (_net != null)
                    _net.StopCoroutine(_hostShareCoroutine);
                _hostShareCoroutine = null;
            }
            _hostShareRunning = false;
            _shareTargetPlayerId = -1;
            _broadcastRecipients = null;
            _clientReceiving = false;
            _afterHostShare = null;
            _rerunBroadcast = false;
            _rerunWaitForSave = false;
            _rerunAfter = null;
            _clientApplying = false;
            _chunkBuffers = null;
            _chunksReceived = 0;
            _chunksExpected = 0;
            _awaitingSlotPick = false;
            _awaitingEnterWorld = false;
            _enterProfileId = 0;
            _enterChapterId = 0;
            _hostSourceProfileId = 0;
            _pendingPackageFingerprint = null;
            _verifiedPackage = null;
            _awaitingChapterGo = false;
            ProgressText = string.Empty;
        }

        /// <summary>
        /// Host only: after new world gen finishes, wait for the game's own save, then push files.
        /// </summary>
        public void ScheduleHostShareAfterNewWorld()
        {
            ScheduleHostShare(waitForGameSave: true, afterShare: null, targetPlayerId: -1);
        }

        /// <summary>
        /// Host only: manual F2 resend. Force-save, then push; a user-initiated hitch is acceptable.
        /// </summary>
        public void ScheduleHostResend()
        {
            if (!LanNetworkManager.HostIsFullyInWorld())
            {
                ModLog.Warn(LogCat.Save,
                    "Resend world skipped — host not fully in-world yet (loading="
                    + Core.loadingGame + " player=" + (Player.Instance != null) + ")");
                ProgressText = "Host not ready — enter world first";
                if (_net != null)
                    _net.StatusText = ProgressText;
                return;
            }
            // waitForGameSave=true enables the single force Save path (see HostShareCoroutine).
            ScheduleHostShare(waitForGameSave: true, afterShare: null, targetPlayerId: -1);
        }

        /// <summary>
        /// Host only: push current world files to one newly joined client (or all if id ≤ 0).
        /// Used when a peer handshakes while the host is already in-game.
        /// </summary>
        public void ScheduleHostShareToPlayer(int playerId)
        {
            if (!LanNetworkManager.HostIsFullyInWorld())
            {
                ModLog.Warn(LogCat.Save,
                    "World share to p" + playerId
                    + " deferred — host not fully in-world yet");
                return;
            }
            if (playerId <= 0)
            {
                ScheduleHostResend();
                return;
            }
            ScheduleHostShare(waitForGameSave: false, afterShare: null, targetPlayerId: playerId);
        }

        /// <summary>
        /// Host only: push current save files, then run <paramref name="afterShare"/> (e.g. chapter LoadScene).
        /// </summary>
        public void ScheduleHostShareThen(Action afterShare, bool waitForGameSave = false)
        {
            ScheduleHostShare(waitForGameSave, afterShare, targetPlayerId: -1);
        }

        private void ScheduleHostShare(bool waitForGameSave, Action afterShare, int targetPlayerId)
        {
            if (_net == null || _net.Role != NetworkRole.Host)
                return;
            if (!_net.IsConnected || !_net.IsHandshakeComplete)
            {
                ProgressText = "No clients connected";
                _net.StatusText = ProgressText;
                ModLog.Event(LogCat.Save, "World share skipped — no connected clients");
                // Still run afterShare so host chapter load is not stuck.
                try { afterShare?.Invoke(); } catch (Exception ex) {
                    ModLog.Error(LogCat.Save, "afterShare with no clients", ex);
                }
                return;
            }
            if (_hostShareRunning)
            {
                ProgressText = "World share already in progress";
                if (targetPlayerId <= 0)
                {
                    // Broadcast while a share runs: the running one may have packed files that have
                    // since changed (chapter save) or cover fewer peers. Chaining only the callback
                    // let the chapter load proceed with other clients never sent the new world.
                    _rerunBroadcast = true;
                    _rerunWaitForSave |= waitForGameSave;
                    if (afterShare != null)
                        _rerunAfter += afterShare;
                    ModLog.Event(LogCat.Save,
                        "World share running (target=" + _shareTargetPlayerId
                        + ") — broadcast queued to re-run to all peers when it ends");
                    return;
                }
                // Coalesce: a second push to the same peer (or while broadcasting to all)
                // mid-share caused client Missing chunk 0:0 — Begin wiped buffers under apply.
                // Still chain afterShare (chapter load must not stall); skip duplicate share.
                // A running broadcast only covers a peer that was connected when its Begin went out
                // (or any peer, while Begin is still to be sent): a peer that joined after Begin
                // never got it, so it is chained below to get its own share when this one ends.
                bool samePeerAlreadyCovered = targetPlayerId > 0
                    && (_shareTargetPlayerId == targetPlayerId
                        || (_shareTargetPlayerId < 0
                            && (_broadcastRecipients == null
                                || _broadcastRecipients.Contains(targetPlayerId))));
                if (samePeerAlreadyCovered && afterShare == null)
                {
                    ModLog.Event(LogCat.Save,
                        "World share coalesce — skip duplicate push to player " + targetPlayerId
                        + " (share already running, target=" + _shareTargetPlayerId + ")");
                    return;
                }
                if (afterShare != null || (targetPlayerId > 0 && !samePeerAlreadyCovered))
                {
                    var prev = _afterHostShare;
                    int chainedTarget = (targetPlayerId > 0 && !samePeerAlreadyCovered)
                        ? targetPlayerId : -1;
                    _afterHostShare = () =>
                    {
                        try { prev?.Invoke(); } catch { /* ignore */ }
                        try { afterShare?.Invoke(); } catch { /* ignore */ }
                        if (chainedTarget > 0)
                            ScheduleHostShareToPlayer(chainedTarget);
                    };
                }
                return;
            }

            _afterHostShare = afterShare;
            _shareTargetPlayerId = targetPlayerId;
            // Mute high-rate entity/physics flood only for peers that will load this package.
            // Soft-reconnect (AlreadyInWorld) peers must keep receiving PlayerState or
            // they never spawn the host proxy.
            if (targetPlayerId > 0)
            {
                if (!_net.IsCoopReconnectPeer(targetPlayerId))
                    _net.MarkPeerLoadingWorld(targetPlayerId);
                _net.ReserveIdForJoinPipeline(targetPlayerId);
            }
            else if (afterShare != null || Patches.ChapterTransitionHelpers.IsChapterTransitionActive)
            {
                // Chapter share: every peer, in-world ones included, loads the new chapter.
                _net.MarkAllClientPeersLoadingWorld(excludeCoopReconnect: true);
            }
            else
            {
                // Resend / new world: peers already playing ignore the package (HandleBegin) and
                // must keep their traffic; title joiners are muted until their first PlayerState.
                _net.MarkTitlePeersLoadingForWorldShare();
            }
            _hostShareCoroutine = _net.StartCoroutine(HostShareCoroutine(waitForGameSave, _shareGeneration));
        }
    }
}
