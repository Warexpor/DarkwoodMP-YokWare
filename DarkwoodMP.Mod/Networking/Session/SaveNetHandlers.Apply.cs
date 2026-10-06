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
                    return;
                }
                var data = ClientStateBackup.LoadBackupFileForPlayer(
                    playerId, steamId, stableKey, allowPlayerIdFallback: allowPlayerId);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] no stored backup for p" + playerId
                        + (steamId != 0 ? " s" + steamId : "")
                        + (stableKey != null ? " k" + stableKey.Substring(0, System.Math.Min(8, stableKey.Length)) : "")
                        + " — peer may use local self");
                    return;
                }
                if (ClientStateBackup.LooksLikeStaleBackupOnFreshWorld(data))
                {
                    ModRuntime.LegacyInfo(
                        $"[ClientBackup] skip push → p{playerId} — stale/legacy-poison backup");
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
                if (Player.Instance != null && !GameScreen.AtTitle && !Core.loadingGame)
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

                    // Do NOT set _receivedHostClientBackup yet — only after a successful
                    // apply. Rejected stale/mismatch pushes must leave local fallback free.

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
                                $"[ClientBackup] prefer local self over host push (localScore={localScore} hostScore={hostScore})");
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

                    string chosenJson = ClientStateBackup.SerializeToJson(data);
                    ClientStateBackup.SaveLocalSelfBackupFile(chosenJson);
                    if (Player.Instance != null)
                    {
                        if (ClientStateBackup.RestoreFromBackup(data))
                        {
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
                        // Saved to local self; allow wait routine / later apply path.
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
            string json = ClientStateBackup.SerializeToJson(parsed);
            if (string.IsNullOrEmpty(json))
                return;

            ClientStateBackup.SaveBackupFile(json, playerId, steamId, stableKey);
        }
    }
}
