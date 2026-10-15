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
        /// <summary>Host: the Save behind the pending broadcast asked for the corner indicator (any of the debounced requests).</summary>
        private bool _saveSyncShowIndicator;
        private const float SaveSyncHostCooldownSec = 3f;
        /// <summary>After a peer day/night death save cascade, drop redundant SaveSync.</summary>
        private float _deathSaveSyncSuppressUntil = -999f;
        private const float DeathSaveSyncSuppressSec = 6f;

        private bool _receivedHostClientBackup;
        /// <summary>The host answered that it holds no usable backup for this player: the local file is used at once.</summary>
        private bool _hostHasNoClientBackup;
        /// <summary>Client: the host's night-death character, held until the player exists to take it.</summary>
        private ClientStateBackupData _nightDeathCharacter;
        /// <summary>
        /// Client, from the join's handshake until its character backup (position included) is
        /// applied or found missing: the player is not yet where they will stand
        /// (<see cref="Players.LocalGetUpHold"/> keeps them lying until then).
        /// </summary>
        internal bool ClientPlacementPending { get; private set; }
        private Coroutine _clientBackupRestoreCo;
        /// <summary>Wait for host late-join backup before falling back to local self file.</summary>
        private const float ClientBackupHostWaitSec = 12f;
        /// <summary>Client: when this machine last wrote the world at a coordinated save (party characters of that save are stored only after one).</summary>
        private float _lastLocalWorldSaveAt = -1f;

        internal SaveNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        /// <summary>Clear save-side session state on disconnect/reset.</summary>
        internal void Reset()
        {
            _receivedHostClientBackup = false;
            _hostHasNoClientBackup = false;
            _nightDeathCharacter = null;
            ClientPlacementPending = false;
            _clientBackupRestoreCo = null;
            _saveSyncBroadcastPending = false;
            _saveSyncHostNeedsApply = false;
            _saveSyncShowIndicator = false;
            _lastLocalWorldSaveAt = -1f;
        }

        /// <summary>Arm after local/remote death so vanilla Save spam does not re-Save the host.</summary>
        internal void NoteDeathSaveSyncWindow()
        {
            _deathSaveSyncSuppressUntil = Time.unscaledTime + DeathSaveSyncSuppressSec;
        }

        /// <summary>
        /// After a local Save: host debounces broadcast; client requests host fan-out only.
        /// </summary>
        internal void SendSaveSync(bool hostAlreadySavedLocally = false, bool showIndicator = true)
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
                HostScheduleSaveSyncBroadcast(hostAlreadySavedLocally, showIndicator);
                return;
            }

            ModRuntime.LegacyInfo("[SaveSync] client → host save-sync request");
            _net.Send(NetMessageType.SaveSync, w => new SaveSyncMessage { ShowIndicator = showIndicator }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        private void HostScheduleSaveSyncBroadcast(bool hostAlreadySavedLocally, bool showIndicator)
        {
            _saveSyncShowIndicator |= showIndicator;
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
            bool showIndicator = _saveSyncShowIndicator;
            _saveSyncShowIndicator = false;
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
                ApplySaveSyncLocalSave("host debounced client request", showIndicator);
            }

            // The host's character lives in the world save, which a client taking the host over
            // does not load it from: the snapshot is what brings it back when this player joins
            // that world later as a client.
            string hostCharacter = null;
            try { hostCharacter = PersistClientBackupSnapshot(sendToHost: false); }
            catch (Exception ex) { ModRuntime.Log?.LogWarning("[ClientBackup] host self snapshot: " + ex.Message); }

            ModRuntime.LegacyInfo("[SaveSync] host broadcast coordinated Save to peers");
            _net.SendToAll(NetMessageType.SaveSync, w => new SaveSyncMessage { ShowIndicator = showIndicator }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            // After the SaveSync on the same channel: each client has written its copy of the world
            // when the host's character arrives, and keeps it with that save.
            if (!string.IsNullOrEmpty(hostCharacter))
                _net.SendToAll(NetMessageType.PartyCharacter,
                    w => new PartyCharacterMessage { Kind = PartyCharacterKind.SaveRound, JsonData = hostCharacter }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Host saving with nobody connected: its character still goes with that save. Otherwise
        /// the world a later joiner downloads would be newer than the host's stored character, and
        /// the player who hosts that copy next would hand the old host an older bag.
        /// </summary>
        internal void HostKeepOwnCharacter()
        {
            if (_net.Role != NetworkRole.Host)
                return;
            try { PersistClientBackupSnapshot(sendToHost: false); }
            catch (Exception ex) { ModRuntime.Log?.LogWarning("[ClientBackup] host self snapshot (alone): " + ex.Message); }
        }

        /// <summary>
        /// Client, the link to the host is going (dropped, a host lost, or leaving): the character
        /// as it is now goes to this machine's own file. The host's world went on without a save,
        /// so the last save's character was older than that world: a rejoin got back what it had
        /// dropped or handed over since (and lost what it picked up). The rejoin's restore takes
        /// the newer of this file and the host's copy, as it already did. Not while the body is
        /// not yet this player's own (a join still placing it, a world loading) or is in a scene
        /// that owns it (the host's prologue).
        /// </summary>
        internal void PersistOnLinkLoss(string why)
        {
            if (_net.Role != NetworkRole.Client || ClientPlacementPending)
                return;
            if (Player.Instance == null || GameScreen.AtTitle || Core.loadingGame || !LanNetworkManager.ClientCanApplyWorldBulk())
                return;
            if (Sync.PersonalPrologue.LocalInPrologue)
                return;
            try
            {
                if (PersistClientBackupSnapshot(sendToHost: false) != null)
                    ModLog.Event(LogCat.Save, "[ClientBackup] character kept at link loss (" + why + ")");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] link-loss snapshot: " + ex.Message);
            }
        }

        /// <summary>
        /// Client, at a real world save (the host's coordinated Save): the character (position,
        /// bag, health, skills) as it is in that saved world goes to the host and to this
        /// machine's own file. Leaving or losing the link writes the file too
        /// (<see cref="PersistOnLinkLoss"/>).
        /// </summary>
        internal void SendClientStateBackup()
        {
            if (!_net.IsConnected) return;
            if (_net.Role != NetworkRole.Client) return;
            // Still waiting for its own character after a join: the body is the one the loaded
            // world came with (the host's). Stored now, it replaced this player's real backup
            // on the host and on disk.
            if (ClientPlacementPending)
            {
                ModLog.Event(LogCat.Save, "[ClientBackup] not sent — own character not restored yet");
                return;
            }
            try
            {
                PersistClientBackupSnapshot(sendToHost: true);
                Sync.QuestItemHandoff.OnSaveTimeBackupSent(_net);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to send: " + ex);
            }
        }

        /// <summary>
        /// Client, once its night death has dropped the bag and spent the life: the character as
        /// it is now goes to the host, which keeps it in memory until morning. A rejoin in that
        /// time used to restore the last save's character, full kit and life included, while the
        /// bag still lay at the body. Not written to disk on either side: no world save holds
        /// this death.
        /// </summary>
        internal void SendNightDeathCharacter()
        {
            if (!_net.IsConnected || _net.Role != NetworkRole.Client)
                return;
            try
            {
                string json = ClientStateBackup.SerializeToJson(ClientStateBackup.CollectBackupData());
                if (string.IsNullOrEmpty(json))
                    return;
                _net.Broadcast(NetMessageType.ClientStateBackup,
                    w => new ClientStateBackupMessage { JsonData = json, NightDeath = true }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo("[ClientBackup] sent the night-death character to the host");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] night-death character: " + ex.Message);
            }
        }

        /// <summary>Collect → local self file; optionally push to host. Returns the stored JSON (null when skipped).</summary>
        private string PersistClientBackupSnapshot(bool sendToHost)
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
                return null;
            }
            string json = ClientStateBackup.SerializeToJson(data);
            ClientStateBackup.SaveLocalSelfBackupFile(json);
            if (!sendToHost || !_net.IsConnected)
                return json;
            _net.Broadcast(NetMessageType.ClientStateBackup,
                w => new ClientStateBackupMessage { JsonData = json }.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[ClientBackup] sent backup to host ({(data.InventoryItems?.Count ?? 0) + (data.HotbarItems?.Count ?? 0)} items, {(data.Skills?.Count ?? 0)} skills, pos=({data.PosX.ToString("F0")},{data.PosZ.ToString("F0")})");
            return json;
        }
    }
}
