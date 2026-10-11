using System;
using System.Collections;
using DWMPHorde;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>SaveSync + ClientStateBackup handlers composed for 0.8.</summary>
    internal sealed partial class SaveNetHandlers
    {

        /// <summary>
        /// Host-broadcast SaveSync → clients run full local Save with vanilla Saving UI.
        /// Client-originated SaveSync is host-only (debounced fan-out). Flag blocks loops.
        /// </summary>
        internal void HandleSaveSync()
        {
            if (_net.Role == NetworkRole.Offline)
                return;
            if (LanNetworkManager.RemoteSaveInProgress)
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
            if (LanNetworkManager.RemoteSaveInProgress)
                return;

            SaveManager sm = Singleton<SaveManager>.Instance;
            if (sm == null)
            {
                ModLog.Warn(LogCat.Save, "SaveSync: SaveManager missing");
                return;
            }

            if (GameScreen.AtTitle || Core.loadingGame || Player.Instance == null)
            {
                ModLog.Event(LogCat.Save,
                    "SaveSync ignored — not in playable world (menu/loading/no player)");
                return;
            }

            LanNetworkManager.RemoteSaveInProgress = true;
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
                    try { SendClientStateBackup(atWorldSave: true); }
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
                LanNetworkManager.RemoteSaveInProgress = false;
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
                ulong steamId = 0;
                _net.TryGetSteamIdForPlayer(playerId, out steamId);
                string stableKey = null;
                _net.TryGetStableClientKeyForPlayer(playerId, out stableKey);
                // Cold LAN rejoin reshuffles PlayerId — never push a pN file unless
                // soft-reconnect kept the same id. Steam / StableClientKey are safe.
                bool allowPlayerId = _net.IsCoopReconnectPeer(playerId);
                if (steamId == 0 && string.IsNullOrEmpty(stableKey) && !allowPlayerId)
                {
                    ModRuntime.LegacyInfo(
                        $"[ClientBackup] skip push → p{playerId} — no SteamId/StableClientKey (cold LAN; peer uses local self)");
                    SendNoStoredClientBackup(playerId);
                    return;
                }
                // This world has run on since the player left: they come back as they left it.
                ClientStateBackupData data = TryGetLiveClientBackup(playerId, steamId, stableKey, allowPlayerId);
                bool live = data != null;
                if (data == null)
                    data = ClientStateBackup.LoadBackupFileForPlayer(
                        playerId, steamId, stableKey, allowPlayerIdFallback: allowPlayerId);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] no stored backup for p" + playerId
                        + (steamId != 0 ? " s" + steamId : "")
                        + (stableKey != null ? " k" + stableKey.Substring(0, System.Math.Min(8, stableKey.Length)) : "")
                        + " — peer may use local self");
                    SendNoStoredClientBackup(playerId);
                    return;
                }
                if (ClientStateBackup.LooksLikeStaleBackupOnFreshWorld(data))
                {
                    ModRuntime.LegacyInfo(
                        $"[ClientBackup] skip push → p{playerId} — stale/legacy-poison backup");
                    SendNoStoredClientBackup(playerId);
                    return;
                }
                data.Live = live;
                data.HostRunId = HostRunId();
                string json = ClientStateBackup.SerializeToJson(data);
                if (string.IsNullOrEmpty(json))
                {
                    SendNoStoredClientBackup(playerId);
                    return;
                }
                _net.SendToPlayer(playerId, NetMessageType.ClientStateBackup,
                    w => new ClientStateBackupMessage { JsonData = json }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo(
                    "[ClientBackup] pushed " + (live ? "live" : "save-time") + " backup → p" + playerId
                    + (steamId != 0 ? " s" + steamId : "")
                    + (stableKey != null ? " k" + stableKey.Substring(0, System.Math.Min(8, stableKey.Length)) : "")
                    + " (inv=" + (data.InventoryItems?.Count ?? 0)
                    + " skills=" + (data.Skills?.Count ?? 0) + ")");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] push to p" + playerId + " failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Host: nothing stored for this player. Said out loud (a backup that only names this
        /// run of the world) so the joiner uses its own files at once instead of waiting out
        /// the push that will not come.
        /// </summary>
        private void SendNoStoredClientBackup(int playerId)
        {
            string json = ClientStateBackup.SerializeToJson(
                new ClientStateBackupData { NoBackup = true, HostRunId = HostRunId() }) ?? "";
            _net.SendToPlayer(playerId, NetMessageType.ClientStateBackup,
                w => new ClientStateBackupMessage { JsonData = json }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Host: the world in memory is one run, from its load to the next load (the player
        /// object is made anew by every load). Live snapshots belong to the run they were taken in.
        /// </summary>
        private void EnsureHostRun()
        {
            Player p = Player.Instance;
            int id = p != null ? p.GetInstanceID() : 0;
            if (_ownRunId != null && id == _ownRunPlayerId)
                return;
            _ownRunPlayerId = id;
            _ownRunId = Guid.NewGuid().ToString("N");
            if (_liveClientBackups.Count > 0)
            {
                ModRuntime.LegacyInfo("[ClientBackup] world loaded anew: " + _liveClientBackups.Count
                    + " live snapshot(s) dropped, save-time files stand");
                _liveClientBackups.Clear();
            }
        }

        private string HostRunId()
        {
            EnsureHostRun();
            return _ownRunId;
        }

        private static string LiveBackupKey(int playerId, ulong steamId, string stableKey, bool allowPlayerId)
        {
            if (steamId != 0)
                return "s" + steamId;
            if (!string.IsNullOrEmpty(stableKey))
                return "k" + stableKey;
            return allowPlayerId && playerId > 0 ? "p" + playerId : null;
        }

        private ClientStateBackupData TryGetLiveClientBackup(int playerId, ulong steamId, string stableKey, bool allowPlayerId)
        {
            EnsureHostRun();
            string key = LiveBackupKey(playerId, steamId, stableKey, allowPlayerId);
            if (key == null || !_liveClientBackups.TryGetValue(key, out LiveClientBackup held))
                return null;
            if (!ClientStateBackup.MatchesCurrentCampaign(held.Data))
                return null;
            return held.Data;
        }

        /// <summary>
        /// The host's world was just written to disk: the live snapshots are the players' state
        /// in that saved world, so they become the stored ones.
        /// </summary>
        internal void StoreLiveClientBackupsAtWorldSave()
        {
            if (_net.Role == NetworkRole.Client)
                return;
            EnsureHostRun();
            if (_liveClientBackups.Count == 0)
                return;
            foreach (LiveClientBackup held in _liveClientBackups.Values)
            {
                try
                {
                    held.Data.Live = false;
                    held.Data.HostRunId = null;
                    string json = ClientStateBackup.SerializeToJson(held.Data);
                    if (!string.IsNullOrEmpty(json))
                        ClientStateBackup.SaveBackupFile(json, held.PlayerId, held.SteamId, held.StableKey);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ClientBackup] storing a live snapshot at the world save failed: " + ex.Message);
                }
            }
            ModLog.Event(LogCat.Save, "[ClientBackup] world saved: " + _liveClientBackups.Count
                + " live snapshot(s) stored");
            _liveClientBackups.Clear();
        }

        /// <summary>The chapter is started over: nothing taken before it is handed back.</summary>
        internal void DropLiveClientBackups()
        {
            _liveClientBackups.Clear();
        }

        /// <summary>
        /// Client: the newer of this machine's save-time snapshot and, when this is still the
        /// host run it was taken in, its leave-time one.
        /// </summary>
        private ClientStateBackupData LoadOwnClientBackup()
        {
            ClientStateBackupData self = ClientStateBackup.LoadLocalSelfBackupFile();
            ClientStateBackupData left = ClientStateBackup.LoadLocalLeaveBackupFile(_hostRunId);
            if (left == null)
                return self;
            if (self != null && ClientStateBackup.TryParseBackupTimestamp(self)
                > ClientStateBackup.TryParseBackupTimestamp(left))
                return self;
            return left;
        }

        /// <summary>
        /// Client: a leave-time snapshot the host does not hold (the link was already down when
        /// it was taken) goes to the host now, so its next world save stores it.
        /// </summary>
        private void ShareRestoredLiveBackup(ClientStateBackupData data)
        {
            if (data == null || !data.Live || !_net.IsConnected)
                return;
            string json = ClientStateBackup.SerializeToJson(data);
            if (string.IsNullOrEmpty(json))
                return;
            _net.Broadcast(NetMessageType.ClientStateBackup,
                w => new ClientStateBackupMessage { JsonData = json }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Client: after phase-3 reconnect, wait for host backup push then fall back to local self.
        /// </summary>
        internal void BeginClientBackupRestoreWait()
        {
            if (_net.Role != NetworkRole.Client)
                return;
            _receivedHostClientBackup = false;
            _hostHasNoClientBackup = false;
            _heldClientBackup = null;
            ClientPlacementPending = true;
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
            try
            {
                float t = 0f;
                while (t < ClientBackupHostWaitSec)
                {
                    if (_net.Role != NetworkRole.Client || !_net.IsConnected)
                        yield break;
                    if (_receivedHostClientBackup)
                        yield break;
                    if (Player.Instance != null && !GameScreen.AtTitle && !Core.loadingGame)
                    {
                        // Nothing is coming from the host: the local file, now.
                        if (_hostHasNoClientBackup)
                            break;
                        t += Time.unscaledDeltaTime;
                    }
                    yield return null;
                }

                if (_receivedHostClientBackup || _net.Role != NetworkRole.Client)
                    yield break;

                var data = _heldClientBackup ?? LoadOwnClientBackup();
                _heldClientBackup = null;
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] no host push and no local self backup — keeping loaded world character");
                    yield break;
                }
                if (Player.Instance == null)
                    yield break;

                if (ClientStateBackup.RestoreFromBackup(data))
                    ShareRestoredLiveBackup(data);
                ModRuntime.LegacyInfo(
                    "[ClientBackup] restored local " + (data.Live ? "leave-time" : "save-time")
                    + " fallback (no host backup for this player id)");
            }
            finally
            {
                ClientPlacementPending = false;
            }
        }

        /// <summary>
        /// Host stores client→host snapshots. Client applies host→client push (rejoin restore).
        /// </summary>
        internal void HandleClientStateBackup(ClientStateBackupMessage msg)
        {
            if (string.IsNullOrEmpty(msg.JsonData))
            {
                if (_net.Role == NetworkRole.Client)
                {
                    // The host holds nothing for this player: the wait routine goes to the local file now.
                    _hostHasNoClientBackup = true;
                    ModRuntime.LegacyInfo("[ClientBackup] host has no stored backup for this player");
                }
                else
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

                    _hostRunId = data.HostRunId;
                    if (data.NoBackup)
                    {
                        // The wait routine goes to this machine's own files now.
                        ModRuntime.LegacyInfo("[ClientBackup] host has no stored backup for this player");
                        return;
                    }

                    // Do NOT set _receivedHostClientBackup yet — only after a successful
                    // apply. Rejected stale/mismatch pushes must leave local fallback free.

                    // Both copies were written by this client, so their timestamps are from one
                    // clock: the newer one is the state to come back to. The host's is newer when
                    // it holds a live snapshot; this machine's when the link was down as it was taken.
                    ClientStateBackupData hostData = data;
                    ClientStateBackupData local = null;
                    try { local = LoadOwnClientBackup(); }
                    catch { local = null; }

                    if (local != null && ClientStateBackup.MatchesCurrentCampaign(local))
                    {
                        DateTime localAt = ClientStateBackup.TryParseBackupTimestamp(local);
                        DateTime hostAt = ClientStateBackup.TryParseBackupTimestamp(hostData);
                        int localScore = ClientStateBackup.ProgressScore(local);
                        int hostScore = ClientStateBackup.ProgressScore(hostData);
                        if (localAt != hostAt ? localAt > hostAt : localScore >= hostScore)
                        {
                            data = local;
                            ModRuntime.LegacyInfo(
                                $"[ClientBackup] prefer own {(local.Live ? "leave-time" : "save-time")} file over host push (own={local.Timestamp} host={hostData.Timestamp} localScore={localScore} hostScore={hostScore})");
                        }
                    }

                    if (!ClientStateBackup.MatchesCurrentCampaign(data)
                        || !ClientStateBackup.HasMeaningfulProgress(data))
                    {
                        ModRuntime.LegacyInfo(
                            $"[ClientBackup] ignore host push — no usable backup for this campaign — keeping loaded character / local fallback");
                        WrongSaveWarning.Notify(
                            "character backup does not match this co-op campaign — kept loaded character");
                        return;
                    }

                    if (ClientStateBackup.LooksLikeStaleBackupOnFreshWorld(data))
                    {
                        ModRuntime.LegacyInfo(
                            $"[ClientBackup] ignore host push — stale/legacy-poison backup — local fallback still allowed");
                        WrongSaveWarning.Notify(
                            "stale character backup refused — kept loaded character");
                        return;
                    }

                    // The local self file stays the save-time state: a live snapshot is not written to it.
                    if (!data.Live)
                        ClientStateBackup.SaveLocalSelfBackupFile(ClientStateBackup.SerializeToJson(data));
                    if (Player.Instance != null)
                    {
                        if (ClientStateBackup.RestoreFromBackup(data))
                        {
                            if (ReferenceEquals(data, local))
                                ShareRestoredLiveBackup(data);
                            // Only after restore actually applied — refused stale/mismatch
                            // must leave local fallback free.
                            _receivedHostClientBackup = true;
                            ModRuntime.LegacyInfo(
                                "[ClientBackup] restored backup (inv="
                                + (data.InventoryItems?.Count ?? 0)
                                + " skills=" + (data.Skills?.Count ?? 0)
                                + " src=" + (ReferenceEquals(data, local) ? "local-self" : "host-push") + ")");
                        }
                    }
                    else
                    {
                        // The wait routine applies it once the player is in the world.
                        _heldClientBackup = data;
                        ModRuntime.Log?.LogWarning(
                            "[ClientBackup] host push arrived before Player — held for the wait routine");
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ClientBackup] host push apply failed: " + ex.Message);
                }
                finally
                {
                    // Applied: placed. Refused, or too early to apply: the wait routine goes to
                    // the local file as soon as the player is in the world.
                    if (_receivedHostClientBackup)
                        ClientPlacementPending = false;
                    else
                        _hostHasNoClientBackup = true;
                }
                return;
            }

            if (_net.Role != NetworkRole.Host)
                return;

            // The file key comes from the connection only: the sender's PlayerId, its Steam id from
            // the transport, and the StableClientKey it announced at handshake. Identity fields
            // inside the JSON are client-written and would let one peer overwrite another's backup.
            int playerId = _net.CurrentReceivePlayerId;
            if (playerId <= 0)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] backup from unknown sender ignored");
                return;
            }
            ulong steamId = _net.CurrentReceiveSteamId64;
            if (steamId == 0)
                _net.TryGetSteamIdForPlayer(playerId, out steamId);
            _net.TryGetStableClientKeyForPlayer(playerId, out string stableKey);

            ClientStateBackupData parsed = null;
            try { parsed = ClientStateBackup.DeserializeFromJson(msg.JsonData); }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[ClientBackup] could not parse backup json: " + ex.Message);
            }
            if (parsed == null)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] unreadable backup from p" + playerId + " ignored");
                return;
            }

            ulong claimedSteam = ClientStateBackup.TryParseSteamId(parsed.SteamId);
            string claimedKey = ClientStateBackup.SanitizeStableClientKey(parsed.StableClientKey);
            if ((claimedSteam != 0 && claimedSteam != steamId)
                || (!string.IsNullOrEmpty(claimedKey)
                    && !string.Equals(claimedKey, stableKey, StringComparison.OrdinalIgnoreCase))
                || (parsed.PlayerId > 0 && parsed.PlayerId != playerId))
            {
                ModLog.Warn(LogCat.Save,
                    "[ClientBackup] p" + playerId + " backup claims another identity — stored under the sender's own key");
            }
            parsed.PlayerId = playerId;
            parsed.SteamId = steamId != 0 ? steamId.ToString() : null;
            parsed.StableClientKey = stableKey;
            EnsureHostRun();
            string liveKey = LiveBackupKey(playerId, steamId, stableKey, allowPlayerId: true);
            if (parsed.Live)
            {
                // Not at a world save: held for this run, stored at the next world save.
                if (liveKey != null)
                    _liveClientBackups[liveKey] = new LiveClientBackup
                    {
                        Data = parsed, PlayerId = playerId, SteamId = steamId, StableKey = stableKey
                    };
                ModLog.Event(LogCat.Save, "[ClientBackup] holding live snapshot from p" + playerId
                    + " until the next world save");
                return;
            }
            if (liveKey != null)
                _liveClientBackups.Remove(liveKey);
            string json = ClientStateBackup.SerializeToJson(parsed);
            if (string.IsNullOrEmpty(json))
                return;

            ClientStateBackup.SaveBackupFile(json, playerId, steamId, stableKey);
            ModLog.Event(LogCat.Save, "[ClientBackup] stored backup from p" + playerId + " ("
                + ((parsed.InventoryItems?.Count ?? 0) + (parsed.HotbarItems?.Count ?? 0)) + " items, level "
                + parsed.CurrentLevel + ")");
        }
    }
}
