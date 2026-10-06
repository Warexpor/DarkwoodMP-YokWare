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
            if (LanNetworkManager.RemoteSaveInProgress) return;
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

            // A world share reads the profile files over several frames: a Save now would hand
            // clients a sav/savs pair from two different moments. Retry once the share is done.
            if (_net.WorldSaveShare != null && _net.WorldSaveShare.IsHostShareRunning)
                return;

            _saveSyncBroadcastPending = false;
            _lastSaveSyncBroadcastAt = Time.unscaledTime;

            if (_saveSyncHostNeedsApply)
            {
                _saveSyncHostNeedsApply = false;
                string blocked = WorldSaveGuards.GetAutomaticHostSaveBlockReason(_net);
                if (blocked != null)
                {
                    // Declined: peers are not told to Save either, so nobody writes a world the
                    // host itself refuses to persist.
                    ModLog.Event(LogCat.Save, "SaveSync request declined — " + blocked);
                    return;
                }
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
            if (Player.Instance == null || GameScreen.AtTitle || Core.loadingGame)
                return;
            if (ClientStateBackup.ChapterReloadWipePending)
            {
                ClientStateBackup.ChapterReloadWipePending = false;
                ModRuntime.LegacyInfo("[ClientBackup] exit snapshot skipped — chapter start-over");
                return;
            }
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

        /// <summary>
        /// Intentional host StopNetwork while in-world: flush sav.dat so the next session
        /// (same or new host) loads current world ownership. Never on a promoted host (survivor
        /// client world corrupts the slot), a chapter change (the new chapter save is already
        /// written), a dream, a held night death, or application quit (scene is being torn down).
        /// Local Save only — no SaveSync fan-out (peers are tearing down).
        /// </summary>
        internal void TryHostWorldSaveCheckpointOnExit()
        {
            if (_net.Role != NetworkRole.Host)
                return;
            if (Player.Instance == null || GameScreen.AtTitle || Core.loadingGame)
                return;
            if (LanNetworkManager.RemoteSaveInProgress)
                return;
            string blocked = WorldSaveGuards.GetAutomaticHostSaveBlockReason(_net);
            if (blocked != null)
            {
                ModLog.Event(LogCat.Save, "Host leave checkpoint skipped — " + blocked);
                return;
            }
            SaveManager sm = Singleton<SaveManager>.Instance;
            if (sm == null)
                return;
            try
            {
                ModLog.Event(LogCat.Save,
                    "Host leave checkpoint → local Save (intentional StopNetwork)");
                LanNetworkManager.RemoteSaveInProgress = true;
                sm.Save(
                    doJson: true,
                    doSaveProfile: true,
                    force: true,
                    forceSaveStatic: false,
                    showSavingIndicator: false);
                CoopWorldCopyMeta.RefreshAfterLocalSave();
                ModRuntime.LegacyInfo("[HostLeave] world save checkpoint written");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[HostLeave] world save checkpoint failed: " + ex.Message);
            }
            finally
            {
                LanNetworkManager.RemoteSaveInProgress = false;
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
            ModRuntime.LegacyInfo($"[ClientBackup] sent backup to host ({(data.InventoryItems?.Count ?? 0)} items, {(data.Skills?.Count ?? 0)} skills, pos=({data.PosX.ToString("F0")},{data.PosZ.ToString("F0")})");
        }
    }
}
