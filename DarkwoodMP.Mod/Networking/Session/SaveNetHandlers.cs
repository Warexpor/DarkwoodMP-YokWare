using System;
using System.Collections;
using DWMPHorde;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>SaveSync + ClientStateBackup handlers composed for 0.8.</summary>
    internal sealed class SaveNetHandlers
    {
        private readonly LanNetworkManager _net;

        private float _lastSaveSyncBroadcastAt = -999f;
        private bool _saveSyncBroadcastPending;
        private bool _saveSyncHostNeedsApply;
        private const float SaveSyncHostCooldownSec = 3f;
        /// <summary>After a peer day/night death save cascade, drop redundant SaveSync.</summary>
        private float _deathSaveSyncSuppressUntil = -999f;
        private const float DeathSaveSyncSuppressSec = 6f;

        private bool _receivedHostClientBackup;
        private Coroutine _clientBackupRestoreCo;
        /// <summary>Wait for host late-join backup before falling back to local self file.</summary>
        private const float ClientBackupHostWaitSec = 12f;

        internal SaveNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        /// <summary>Clear save-side session state on disconnect/reset.</summary>
        internal void Reset()
        {
            _receivedHostClientBackup = false;
            _clientBackupRestoreCo = null;
            _saveSyncBroadcastPending = false;
            _saveSyncHostNeedsApply = false;
        }

        /// <summary>Arm after local/remote death so vanilla Save spam does not re-Save the host.</summary>
        internal void NoteDeathSaveSyncWindow()
        {
            _deathSaveSyncSuppressUntil = Time.unscaledTime + DeathSaveSyncSuppressSec;
        }

        /// <summary>
        /// After a local Save: host debounces broadcast; client requests host fan-out only.
        /// </summary>
        internal void SendSaveSync(bool hostAlreadySavedLocally = false)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager._isRemoteSaveInProgress) return;
            if (_net.Role == NetworkRole.Offline) return;

            if (Time.unscaledTime < _deathSaveSyncSuppressUntil)
            {
                ModLog.Event(LogCat.Save,
                    "SaveSync suppressed (death window) role=" + _net.Role);
                return;
            }

            if (_net.Role == NetworkRole.Host)
            {
                HostScheduleSaveSyncBroadcast(hostAlreadySavedLocally);
                return;
            }

            ModRuntime.LegacyInfo("[SaveSync] client → host save-sync request");
            _net.Send(NetMessageType.SaveSync, w => new SaveSyncMessage().Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private void HostScheduleSaveSyncBroadcast(bool hostAlreadySavedLocally)
        {
            if (!hostAlreadySavedLocally)
                _saveSyncHostNeedsApply = true;
            _saveSyncBroadcastPending = true;
            TryFlushSaveSyncBroadcast();
        }

        internal void TickSaveSyncBroadcast()
        {
            if (_saveSyncBroadcastPending)
                TryFlushSaveSyncBroadcast();
        }

        private void TryFlushSaveSyncBroadcast()
        {
            if (!_saveSyncBroadcastPending || _net.Role != NetworkRole.Host || !_net.IsConnected)
                return;
            if (Time.unscaledTime - _lastSaveSyncBroadcastAt < SaveSyncHostCooldownSec)
                return;

            _saveSyncBroadcastPending = false;
            _lastSaveSyncBroadcastAt = Time.unscaledTime;

            if (_saveSyncHostNeedsApply)
            {
                _saveSyncHostNeedsApply = false;
                ApplySaveSyncLocalSave("host debounced client request");
            }

            ModRuntime.LegacyInfo("[SaveSync] host broadcast coordinated Save to peers");
            _net.SendToAll(NetMessageType.SaveSync, w => new SaveSyncMessage().Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Sends the client's inventory/skills/state backup to the host, and mirrors it
        /// to local self so RESTORE SELF / rejoin fallback stay current.
        /// </summary>
        internal void SendClientStateBackup()
        {
            if (!_net.IsConnected) return;
            if (_net.Role != NetworkRole.Client) return;
            try
            {
                PersistClientBackupSnapshot(sendToHost: true);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to send: " + ex);
            }
        }

        /// <summary>
        /// Client disconnect / quit while in-world: write local self (+ host if still linked)
        /// so exit position is not only whatever the last Save captured.
        /// </summary>
        internal void TrySnapshotClientBackupOnExit()
        {
            if (_net.Role != NetworkRole.Client)
                return;
            if (Player.Instance == null || Core.mainMenu || Core.loadingGame)
                return;
            try
            {
                PersistClientBackupSnapshot(sendToHost: _net.IsConnected);
                ModRuntime.LegacyInfo("[ClientBackup] exit snapshot (disconnect/quit)");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] exit snapshot failed: " + ex.Message);
            }
        }

        /// <summary>Collect → local self file; optionally push to host.</summary>
        private void PersistClientBackupSnapshot(bool sendToHost)
        {
            var data = ClientStateBackup.CollectBackupData();
            // Mid-dream omission leaves 0,0,0; keep the prior exit pose from the existing self file.
            if (data != null && data.PosX == 0f && data.PosY == 0f && data.PosZ == 0f)
            {
                try
                {
                    var prev = ClientStateBackup.LoadLocalSelfBackupFile();
                    if (prev != null && (prev.PosX != 0f || prev.PosZ != 0f)
                        && !ClientStateBackup.IsDreamPadCoordinate(
                            new Vector3(prev.PosX, prev.PosY, prev.PosZ)))
                    {
                        data.PosX = prev.PosX;
                        data.PosY = prev.PosY;
                        data.PosZ = prev.PosZ;
                    }
                }
                catch { /* keep zeros */ }
            }
            if (!ClientStateBackup.HasMeaningfulProgress(data))
            {
                ModRuntime.LegacyInfo("[ClientBackup] skip persist — empty snapshot");
                return;
            }
            string json = ClientStateBackup.SerializeToJson(data);
            ClientStateBackup.SaveLocalSelfBackupFile(json);
            if (!sendToHost || !_net.IsConnected)
                return;
            _net.Broadcast(NetMessageType.ClientStateBackup,
                w => new ClientStateBackupMessage { JsonData = json }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo("[ClientBackup] sent backup to host (" + (data.InventoryItems?.Count ?? 0)
                + " items, " + (data.Skills?.Count ?? 0) + " skills, pos=("
                + data.PosX.ToString("F0") + "," + data.PosZ.ToString("F0") + ")");
        }

        /// <summary>
        /// Host-broadcast SaveSync → clients run full local Save with vanilla Saving UI.
        /// Client-originated SaveSync is host-only (debounced fan-out). Flag blocks loops.
        /// </summary>
        internal void HandleSaveSync()
        {
            if (_net.Role == NetworkRole.Offline)
                return;
            if (LanNetworkManager._isRemoteSaveInProgress)
                return;

            if (_net.Role == NetworkRole.Host)
            {
                if (_net.CurrentReceivePlayerId <= 0)
                    return;
                if (Time.unscaledTime < _deathSaveSyncSuppressUntil)
                {
                    ModLog.Event(LogCat.Save,
                        "SaveSync ignore p" + _net.CurrentReceivePlayerId
                        + " request — death suppress window");
                    return;
                }
                // Host + peers already coordinated a Save within the debounce window
                // (typical: end-dream vanilla Save on every box → host broadcast then
                // client requests → second host Save ~3s later, upd≈400ms). Drop the
                // redundant request; do not re-Save or re-broadcast.
                if (Time.unscaledTime - _lastSaveSyncBroadcastAt < SaveSyncHostCooldownSec)
                {
                    ModLog.Event(LogCat.Save,
                        "SaveSync ignore p" + _net.CurrentReceivePlayerId
                        + " request — recent host broadcast");
                    return;
                }
                ModLog.Event(LogCat.Save,
                    "SaveSync request from p" + _net.CurrentReceivePlayerId + " → host debounce");
                HostScheduleSaveSyncBroadcast(hostAlreadySavedLocally: false);
                return;
            }

            int hostId = _net.HostPlayerId > 0 ? _net.HostPlayerId : 1;
            if (_net.CurrentReceivePlayerId != hostId)
            {
                ModLog.Event(LogCat.Save,
                    "SaveSync ignored — not from host (p" + _net.CurrentReceivePlayerId + ")");
                return;
            }

            ApplySaveSyncLocalSave("host broadcast");
        }

        private void ApplySaveSyncLocalSave(string reason)
        {
            if (LanNetworkManager._isRemoteSaveInProgress)
                return;

            SaveManager sm = Singleton<SaveManager>.Instance;
            if (sm == null)
            {
                ModLog.Warn(LogCat.Save, "SaveSync: SaveManager missing");
                return;
            }

            if (Core.mainMenu || Core.loadingGame || Player.Instance == null)
            {
                ModLog.Event(LogCat.Save,
                    "SaveSync ignored — not in playable world (menu/loading/no player)");
                return;
            }

            LanNetworkManager._isRemoteSaveInProgress = true;
            try
            {
                ModLog.Event(LogCat.Save,
                    "SaveSync " + reason + " → local coordinated Save (force + Saving indicator) role="
                    + _net.Role);

                sm.Save(
                    doJson: true,
                    doSaveProfile: true,
                    force: true,
                    forceSaveStatic: false,
                    showSavingIndicator: true);

                if (_net.Role == NetworkRole.Client)
                {
                    try { SendClientStateBackup(); }
                    catch { /* non-fatal */ }
                }

                CoopWorldCopyMeta.RefreshAfterLocalSave();
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "SaveSync coordinated Save failed", ex);
            }
            finally
            {
                LanNetworkManager._isRemoteSaveInProgress = false;
            }
        }

        /// <summary>
        /// Host→client: push last stored per-player backup after late-join settle so
        /// week-later rejoins restore inv/skills (host world sav has host character).
        /// </summary>
        internal void SendStoredClientBackupTo(int playerId)
        {
            if (_net.Role != NetworkRole.Host || playerId <= 0)
                return;
            try
            {
                var data = ClientStateBackup.LoadBackupFileForPlayer(playerId);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] no stored backup for p" + playerId + " — peer may use local self");
                    return;
                }
                if (ClientStateBackup.LooksLikeStaleBackupOnFreshWorld(data))
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip push → p" + playerId
                        + " — stale backup on fresh day-1 world");
                    return;
                }
                string json = ClientStateBackup.SerializeToJson(data);
                if (string.IsNullOrEmpty(json))
                    return;
                _net.SendToPlayer(playerId, NetMessageType.ClientStateBackup,
                    w => new ClientStateBackupMessage { JsonData = json }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo(
                    "[ClientBackup] pushed stored backup → p" + playerId
                    + " (inv=" + (data.InventoryItems?.Count ?? 0)
                    + " skills=" + (data.Skills?.Count ?? 0) + ")");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] push to p" + playerId + " failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Client: after phase-3 reconnect, wait for host backup push then fall back to local self.
        /// </summary>
        internal void BeginClientBackupRestoreWait()
        {
            if (_net.Role != NetworkRole.Client)
                return;
            _receivedHostClientBackup = false;
            if (_clientBackupRestoreCo != null)
            {
                try { _net.StopCoroutine(_clientBackupRestoreCo); }
                catch { /* ignore */ }
                _clientBackupRestoreCo = null;
            }
            _clientBackupRestoreCo = _net.StartCoroutine(ClientBackupRestoreWaitRoutine());
        }

        private IEnumerator ClientBackupRestoreWaitRoutine()
        {
            float t = 0f;
            while (t < ClientBackupHostWaitSec)
            {
                if (_net.Role != NetworkRole.Client || !_net.IsConnected)
                    yield break;
                if (_receivedHostClientBackup)
                    yield break;
                if (Player.Instance != null && !Core.mainMenu && !Core.loadingGame)
                    t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_receivedHostClientBackup || _net.Role != NetworkRole.Client)
                yield break;

            var data = ClientStateBackup.LoadLocalSelfBackupFile();
            if (data == null)
            {
                ModRuntime.LegacyInfo(
                    "[ClientBackup] no host push and no local self backup — keeping loaded world character");
                yield break;
            }
            if (Player.Instance == null)
                yield break;

            ClientStateBackup.RestoreFromBackup(data);
            ModRuntime.LegacyInfo(
                "[ClientBackup] restored local self fallback (no host backup for this player id)");
        }

        /// <summary>
        /// Host stores client→host snapshots. Client applies host→client push (rejoin restore).
        /// </summary>
        internal void HandleClientStateBackup(ClientStateBackupMessage msg)
        {
            if (string.IsNullOrEmpty(msg.JsonData))
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] received empty backup data");
                return;
            }

            if (_net.Role == NetworkRole.Client)
            {
                try
                {
                    var data = ClientStateBackup.DeserializeFromJson(msg.JsonData);
                    if (data == null)
                    {
                        ModRuntime.Log?.LogWarning("[ClientBackup] host push deserialize failed");
                        return;
                    }
                    _receivedHostClientBackup = true;

                    // Local self is source of truth on this box. Host push only fills a gap.
                    ClientStateBackupData hostData = data;
                    ClientStateBackupData local = null;
                    try { local = ClientStateBackup.LoadLocalSelfBackupFile(); }
                    catch { local = null; }

                    if (local != null && ClientStateBackup.MatchesCurrentCampaign(local))
                    {
                        int localScore = ClientStateBackup.ProgressScore(local);
                        int hostScore = ClientStateBackup.ProgressScore(hostData);
                        bool localNewer = ClientStateBackup.TryParseBackupTimestamp(local)
                            >= ClientStateBackup.TryParseBackupTimestamp(hostData);
                        if (localScore >= hostScore || localNewer)
                        {
                            data = local;
                            ModRuntime.LegacyInfo(
                                "[ClientBackup] prefer local self over host push (localScore="
                                + localScore + " hostScore=" + hostScore + ")");
                        }
                    }

                    if (!ClientStateBackup.MatchesCurrentCampaign(data)
                        || !ClientStateBackup.HasMeaningfulProgress(data))
                    {
                        ModRuntime.LegacyInfo(
                            "[ClientBackup] ignore host push — no usable backup for this campaign"
                            + " — keeping loaded character");
                        return;
                    }

                    if (ClientStateBackup.LooksLikeStaleBackupOnFreshWorld(data))
                    {
                        ModRuntime.LegacyInfo(
                            "[ClientBackup] ignore host push — stale backup on fresh day-1 world");
                        return;
                    }

                    string chosenJson = ClientStateBackup.SerializeToJson(data);
                    ClientStateBackup.SaveLocalSelfBackupFile(chosenJson);
                    if (Player.Instance != null)
                    {
                        ClientStateBackup.RestoreFromBackup(data);
                        ModRuntime.LegacyInfo(
                            "[ClientBackup] restored backup (inv="
                            + (data.InventoryItems?.Count ?? 0)
                            + " skills=" + (data.Skills?.Count ?? 0)
                            + " src=" + (ReferenceEquals(data, local) ? "local-self" : "host-push") + ")");
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning(
                            "[ClientBackup] host push arrived before Player — kept as local self for fallback");
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ClientBackup] host push apply failed: " + ex.Message);
                }
                return;
            }

            if (_net.Role != NetworkRole.Host)
                return;

            int playerId = _net.CurrentReceivePlayerId;
            if (playerId <= 0)
            {
                // Prefer id embedded in JSON if peer map was stale
                try
                {
                    var parsed = ClientStateBackup.DeserializeFromJson(msg.JsonData);
                    if (parsed != null && parsed.PlayerId > 0)
                        playerId = parsed.PlayerId;
                }
                catch (Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[ClientBackup] could not parse PlayerId from json: " + ex.Message);
                }
            }

            if (playerId <= 0)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] cannot key backup — unknown sender player id");
                return;
            }

            ClientStateBackup.SaveBackupFile(msg.JsonData, playerId);
        }
    }
}
