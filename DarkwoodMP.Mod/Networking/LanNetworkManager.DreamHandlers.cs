using System;
using UnityEngine;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleDreamStarted(DreamStartedMessage msg)
        {
            Vector3 locPos = new Vector3(msg.LocPosX, msg.LocPosY, msg.LocPosZ);
            int playerId = _currentReceivePlayerId;

            // Merge host completed + lvl flags before entry.
            DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);

            if (!string.IsNullOrEmpty(msg.PresetName))
            {
                DreamSession.SetPendingHostPreset(msg.PresetName);
                DreamSession.MirrorPoolRemove(msg.PresetName);
            }

            if (!DreamSession.IsActive)
                DreamSession.TryBegin(msg.PresetName);
            else if (!string.IsNullOrEmpty(msg.PresetName)
                && !string.Equals(DreamSession.PresetName, msg.PresetName, StringComparison.OrdinalIgnoreCase))
            {
                // Host chain may arrive as DreamStarted after ChainStart; keep session.
                DreamSession.SetChainedPreset(msg.PresetName);
            }

            DreamSyncManager.OnRemoteDreamStarted(playerId, msg.PresetName, locPos);
            DreamSession.MarkActive();
        }

        /// <summary>
        /// Dream ended: host may receive a client story-end request and must tear down
        /// the shared session for everyone; peers apply remote cleanup.
        /// </summary>
        private void HandleDreamEnded(DreamEndedMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            if (_remotePlayers.TryGetValue(playerId, out var deadState))
                deadState.IsDeadInDream = false;

            DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);

            // Client story completion → host runs full initiateEndDreaming (transition + end).
            if (_role == NetworkRole.Host
                && !string.IsNullOrEmpty(msg.OutcomeName)
                && msg.OutcomeName != "playerDeath"
                && Dreams.Instance != null
                && Dreams.Instance.dreaming)
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSession] Host applying client story end via initiateEndDreaming: {msg.OutcomeName}");
                Dreams.Instance.outcome = msg.OutcomeName;
                // Vanilla: transition video/fade then endDreaming. Do not hard-cut.
                Dreams.Instance.initiateEndDreaming();
                return;
            }

            // Avoid double-end if we already Idle.
            if (DreamSession.IsActive)
                DreamSession.End(msg.OutcomeName);
            DreamSyncManager.OnRemoteDreamEnded(playerId, msg.OutcomeName);
        }

        private void HandleDreamStartRequest(DreamStartRequestMessage msg)
        {
            if (_role != NetworkRole.Host)
                return;
            if (string.IsNullOrEmpty(msg.PresetName))
                return;

            // Client may have leveled (hadDreamAtLvl*) — union before prepare.
            if (msg.LvlFlags != 0)
                DreamSession.ApplyLvlFlags(msg.LvlFlags);

            if (DreamSession.IsActive)
            {
                // Dialogue startDream often races DialogOutcome host prepare + client DreamStartRequest.
                if (DreamSession.IsStarting
                    && string.Equals(DreamSession.PresetName, msg.PresetName, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Dream,
                        "[DreamSync] dedupe start request (already Starting): " + msg.PresetName);
                    return;
                }
                ModLog.Event(LogCat.Dream,
                    "[DreamSync] ignore start request — session " + DreamSession.Current
                    + " preset=" + DreamSession.PresetName + " req=" + msg.PresetName);
                return;
            }
            if (DreamSession.IsPresetCompleted(msg.PresetName))
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Ignoring dream start request — already completed: {msg.PresetName}");
                return;
            }

            if (!DreamSession.TryBegin(msg.PresetName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] TryBegin failed for request: {msg.PresetName}");
                return;
            }

            // Named prepare on host does not hit random pool — mirror client's roll consume.
            DreamSession.MirrorPoolRemove(msg.PresetName);

            ModRuntime.LegacyInfo($"[DreamSync] Host handling dream start request: {msg.PresetName}");
            try
            {
                Singleton<Controller>.Instance.StartCoroutine(
                    Singleton<Dreams>.Instance.prepareDream(msg.PresetName));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[DreamSync] prepareDream failed: " + ex);
                DreamSession.AbortStarting(ex.Message);
            }
        }

        private void HandleDreamSessionBulk(DreamSessionBulkMessage msg)
        {
            DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);
            if (!string.IsNullOrEmpty(msg.ActivePreset))
            {
                DreamSession.SetPendingHostPreset(msg.ActivePreset);
                // Remotes that never empty-roll still need pool parity for later random dreams.
                DreamSession.MirrorPoolRemove(msg.ActivePreset);
            }
            ModRuntime.LegacyInfo(
                $"[DreamSync] Session bulk: completed={msg.CompletedPresets?.Length ?? 0} "
                + $"active={msg.SessionActive} preset={msg.ActivePreset}");
        }

        private void HandleDreamChainStart(DreamChainStartMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NextPresetName))
                return;
            if (_role == NetworkRole.Host)
                return; // host already preparing

            ModRuntime.LegacyInfo($"[DreamSync] DreamChainStart → {msg.NextPresetName}");
            DreamSession.SetChainedPreset(msg.NextPresetName);
            DreamSession.MirrorPoolRemove(msg.NextPresetName);
            DreamSyncManager.OnDreamChain(msg.NextPresetName);
        }

        /// <summary>
        /// Dreamer→Spectator: an item was picked up in a dream.
        /// Removes the matching world object near the reported position so the
        /// spectator sees the pick-up, and logs for support.
        /// </summary>
        private void HandleDreamItemPickup(DreamItemPickupMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ItemType)) return;
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ModRuntime.LegacyInfo($"[DreamSync] Dreamer picked up: {msg.ItemType} x{msg.Amount} at {pos}");

            try
            {
                Sync.WorldPhysicsSyncService.DestroyObjectByPos(pos, msg.ItemType);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] DreamItemPickup apply failed: " + ex.Message);
            }
        }

        private void HandleDreamAudio(DreamAudioMessage msg)
        {
            Sync.DreamAudioPlayer.PlayForwardedAudio(msg);
        }

        private void HandleDreamEntered(DreamEnteredMessage msg)
        {
            int playerId = _currentReceivePlayerId;
            FinalDreamsceneManager.RefreshConnectedPlayers();
            var proxy = GetProxy(playerId);
            if (proxy != null)
            {
                proxy.FreezePosition = false;
                ModRuntime.LegacyInfo($"[DreamSync] Player {playerId} entered dream — proxy unfrozen");
            }
        }

        /// <summary>Host: late-join dream completed + level flags.</summary>
        internal void SendDreamSessionBulkTo(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 0) return;
            var bulk = DreamSessionBulkMessage.FromLocal();
            SendToPlayer(playerId, NetMessageType.DreamSessionBulk,
                w => bulk.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
