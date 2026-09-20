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
        /// Legacy default receive slot (pre permanent-copy picker). Still used as a soft
        /// fallback suggestion when PreferredCoopCopySlot is unset and all slots are full.
        /// </summary>
        public const int ClientReceiveProfileId = 5;

        private static readonly string[] FileNames = { "savs.dat", "sav.dat", "savch.dat" };

        private readonly LanNetworkManager _net;
        private bool _hostShareRunning;
        private bool _clientReceiving;
        private bool _clientApplying;
        private Action _afterHostShare;
        /// <summary>-1 = all handshaked peers; else only that player id.</summary>
        private int _shareTargetPlayerId = -1;

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

        public bool IsBusy => _hostShareRunning || _clientReceiving || _clientApplying
            || _awaitingSlotPick || _awaitingEnterWorld;
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
        }

        public void Reset()
        {
            _hostShareRunning = false;
            _clientReceiving = false;
            _clientApplying = false;
            _afterHostShare = null;
            // Phase-2 enter captures locals then StopNetwork → Reset; do not wipe mid-enter apply.
            if (!_clientApplying)
            {
                _chunkBuffers = null;
                _chunksReceived = 0;
                _chunksExpected = 0;
                _awaitingSlotPick = false;
                _awaitingEnterWorld = false;
                _enterProfileId = 0;
                _enterChapterId = 0;
                _hostSourceProfileId = 0;
                _pendingPackageFingerprint = null;
            }
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
            // waitForGameSave=true enables the single force Save path (see HostShareCoroutine).
            ScheduleHostShare(waitForGameSave: true, afterShare: null, targetPlayerId: -1);
        }

        /// <summary>
        /// Host only: push current world files to one newly joined client (or all if id ≤ 0).
        /// Used when a peer handshakes while the host is already in-game.
        /// </summary>
        public void ScheduleHostShareToPlayer(int playerId)
        {
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
                // Chain: run after current share finishes (include same target)
                if (afterShare != null || targetPlayerId > 0)
                {
                    var prev = _afterHostShare;
                    int chainedTarget = targetPlayerId;
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
            // Mute high-rate entity/physics flood to title joiners only.
            // Soft-reconnect (AlreadyInWorld) peers must keep receiving PlayerState or
            // they never spawn the host proxy.
            if (targetPlayerId > 0)
            {
                if (!_net.IsCoopReconnectPeer(targetPlayerId))
                    _net.MarkPeerLoadingWorld(targetPlayerId);
            }
            else
                _net.MarkAllClientPeersLoadingWorld(excludeCoopReconnect: true);
            _net.StartCoroutine(HostShareCoroutine(waitForGameSave));
        }
    }
}
