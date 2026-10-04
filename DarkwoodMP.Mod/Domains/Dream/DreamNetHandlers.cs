using System;
using UnityEngine;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    /// <summary>Dream wire handlers composed out of LanNetworkManager.DreamHandlers (0.8).</summary>
    internal sealed class DreamNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal DreamNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleDreamStarted(DreamStartedMessage msg)
        {
            Vector3 locPos = new Vector3(msg.LocPosX, msg.LocPosY, msg.LocPosZ);
            int playerId = _net.CurrentReceivePlayerId;

            // Drop stale SessionId when a newer session is already active.
            if (DreamSession.IsActive && DreamSession.SessionId != 0 && msg.SessionId != 0
                && msg.SessionId != DreamSession.SessionId)
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Drop stale DreamStarted session {msg.SessionId} "
                    + $"(active {DreamSession.SessionId})");
                return;
            }

            // Merge host completed + lvl flags before entry.
            DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);

            // Party-once: ignore stale DreamStarted for a preset the party already finished.
            if (!string.IsNullOrEmpty(msg.PresetName)
                && DreamSession.IsPresetCompleted(msg.PresetName))
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Drop DreamStarted — party already completed: {msg.PresetName}");
                return;
            }

            if (!string.IsNullOrEmpty(msg.PresetName))
            {
                DreamSession.SetPendingHostPreset(msg.PresetName);
                DreamSession.MirrorPoolRemove(msg.PresetName);
            }

            if (!DreamSession.IsActive)
            {
                // Use BeginFromHost, never TryBegin. TryBegin mints a local SessionId
                // that would conflict with Adopt.
                DreamSession.BeginFromHost(msg.PresetName, msg.SessionId);
            }
            else if (!string.IsNullOrEmpty(msg.PresetName)
                && !string.Equals(DreamSession.PresetName, msg.PresetName, StringComparison.OrdinalIgnoreCase))
            {
                // Host chain may arrive as DreamStarted after ChainStart; keep session.
                DreamSession.SetChainedPreset(msg.PresetName);
                if (msg.SessionId != 0)
                    DreamSession.AdoptSessionId(msg.SessionId);
            }
            else
            {
                if (!string.IsNullOrEmpty(msg.PresetName))
                    DreamSession.UpdateActivePreset(msg.PresetName);
                if (msg.SessionId != 0)
                    DreamSession.AdoptSessionId(msg.SessionId);
            }

            DreamSyncManager.OnRemoteDreamStarted(playerId, msg.PresetName, locPos, msg.EntryTransition);
            DreamSession.MarkActive();
        }

        /// <summary>
        /// Dream ended: host may receive a client story-end request and must tear down
        /// the shared session for everyone; peers apply remote cleanup.
        /// </summary>
        internal void HandleDreamEnded(DreamEndedMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            bool wasDeadInDream = false;
            if (_net.TryGetRemoteState(playerId, out var peerState))
                wasDeadInDream = peerState.IsDeadInDream;

            // Host-to-client rejection for a deferred story-end request.
            if (_net.Role == NetworkRole.Client && DreamSession.IsRejectedOutcome(msg.OutcomeName))
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSession] Host rejected story end — {msg.OutcomeName}");
                DreamSyncManager.ForceLocalDreamCleanup(msg.OutcomeName);
                return;
            }

            // Client story completion → host runs full initiateEndDreaming (transition + end).
            if (_net.Role == NetworkRole.Host
                && !string.IsNullOrEmpty(msg.OutcomeName)
                && msg.OutcomeName != "playerDeath"
                && !DreamSession.IsRejectedOutcome(msg.OutcomeName)
                && Dreams.Instance != null
                && Dreams.Instance.dreaming)
            {
                string rejectReason = null;
                if (playerId <= 0 || !DreamSession.IsActive)
                    rejectReason = "no_session";
                else if (msg.SessionId != DreamSession.SessionId)
                    rejectReason = "session_mismatch";
                else if (!_net.IsHandshakedPeer(playerId))
                    rejectReason = "unknown_peer";
                else if (wasDeadInDream)
                    rejectReason = "dead_in_dream";
                else if (!string.IsNullOrEmpty(msg.PresetName)
                    && !string.IsNullOrEmpty(DreamSession.PresetName)
                    && !string.Equals(msg.PresetName, DreamSession.PresetName, StringComparison.OrdinalIgnoreCase))
                    rejectReason = "preset_mismatch";

                if (rejectReason != null)
                {
                    ModRuntime.LegacyInfo(
                        $"[DreamSession] Rejected story end from p{playerId}: {rejectReason}");
                    SendDreamEndedRejected(playerId, rejectReason);
                    return;
                }

                DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);

                if (peerState != null)
                    peerState.IsDeadInDream = false;

                ModRuntime.LegacyInfo(
                    $"[DreamSession] Host applying client story end via initiateEndDreaming: {msg.OutcomeName}");
                Dreams.Instance.outcome = msg.OutcomeName;
                // The initiateEndDreaming authority patch fans DreamEnded out, but it stands down
                // inside this handler's apply guard: peers (the requester too) waited for the
                // host's whole exit and then played theirs alone. Fan out here.
                DreamSyncManager.NotifyPeersStoryEndBeginning(
                    DreamSession.PresetName ?? msg.PresetName, msg.OutcomeName);
                // Vanilla: transition video/fade then endDreaming. Do not hard-cut.
                Dreams.Instance.initiateEndDreaming();
                return;
            }

            // Stale DreamEnded while a newer session is active.
            if (DreamSession.IsActive && DreamSession.SessionId != 0 && msg.SessionId != 0
                && msg.SessionId != DreamSession.SessionId
                && !DreamSession.IsRejectedOutcome(msg.OutcomeName))
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Drop stale DreamEnded session {msg.SessionId} "
                    + $"(active {DreamSession.SessionId})");
                return;
            }

            DreamSyncManager.ClearStoryEndDefer();
            DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);

            // playerDeath, spectate, and remote cleanup clear dream-death tracking.
            if (_net.TryGetRemoteState(playerId, out peerState))
                peerState.IsDeadInDream = false;

            // Chain outcomes: keep the session Active so host-ordered wantToSwitchDream
            // → SetChainedPreset keeps the death roster (End would Idle then TryBegin wipe).
            bool chains = DreamSyncManager.OutcomeChainsToNextDream(
                Dreams.Instance, msg.OutcomeName);
            if (DreamSession.IsActive && !chains)
                DreamSession.End(msg.OutcomeName);
            DreamSyncManager.OnRemoteDreamEnded(playerId, msg.OutcomeName);
        }

        private void SendDreamEndedRejected(int playerId, string reason)
        {
            if (playerId <= 0) return;
            string outcome = DreamSession.BuildRejectedOutcome(reason);
            _net.SendToPlayer(playerId, NetMessageType.DreamEnded,
                w => DreamEndedMessage.Build(DreamSession.PresetName ?? "", outcome).Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }

        internal void HandleDreamStartRequest(DreamStartRequestMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
                return;

            int requesterId = _net.CurrentReceivePlayerId;

            // Client may have leveled (hadDreamAtLvl*); union before prepare.
            if (msg.LvlFlags != 0)
                DreamSession.ApplyLvlFlags(msg.LvlFlags);

            if (string.IsNullOrEmpty(msg.PresetName))
            {
                // Fix 2: Random dream (empty name from onFinishedVideo). Host rolls
                // via getPreset inside prepareDream; DreamGetPresetPatch handles TryBegin.
                if (DreamSession.IsActive)
                {
                    ModLog.Event(LogCat.Dream,
                        "[DreamSync] ignore empty start request — session active");
                    SendDreamEndedRejected(requesterId, "session_active");
                    return;
                }
                if (Singleton<Dreams>.Instance == null
                    || Singleton<Dreams>.Instance.dreamPrepared
                    || Singleton<Dreams>.Instance.dreaming)
                {
                    ModLog.Event(LogCat.Dream,
                        "[DreamSync] ignore empty start request — dream already prepared/active");
                    SendDreamEndedRejected(requesterId, "already_prepared");
                    return;
                }

                ModRuntime.LegacyInfo("[DreamSync] Host handling empty dream start request (random roll)");
                // Next frame, outside this handler's apply guard: the host's roll hooks
                // (pool refill, TryBegin, early bulk to clients) stand down inside it, so the
                // roll ran untracked and a depleted pool threw.
                Singleton<Controller>.Instance.waitFramesAndRun(() =>
                {
                    try
                    {
                        if (DreamSession.IsActive || Singleton<Dreams>.Instance == null
                            || Singleton<Dreams>.Instance.dreamPrepared || Singleton<Dreams>.Instance.dreaming)
                            return;
                        Singleton<Controller>.Instance.StartCoroutine(
                            Singleton<Dreams>.Instance.prepareDream(""));
                    }
                    catch (Exception ex)
                    {
                        ModRuntime.Log?.LogError("[DreamSync] prepareDream('') failed: " + ex);
                        try
                        {
                            if (Singleton<Dreams>.Instance != null)
                                Singleton<Dreams>.Instance.dreamPrepared = false;
                        }
                        catch { /* ignore */ }
                        DreamSession.AbortStarting(ex.Message);
                        SendDreamEndedRejected(requesterId, "prepare_failed");
                    }
                }, 1);
                return;
            }

            // A name the game has no preset for threw inside prepareDream after TryBegin, leaving
            // the session Starting (joins refused, overworld deaths counted as dream deaths) until
            // its 60 s timeout.
            if (DreamSyncManager.FindDreamPreset(msg.PresetName) == null)
            {
                ModLog.Event(LogCat.Dream,
                    "[DreamSync] reject start request — no such preset: " + msg.PresetName);
                SendDreamEndedRejected(requesterId, "unknown_preset");
                return;
            }

            // Party-once: named skill/dialogue dream already finished by any peer.
            if (DreamSession.IsPresetCompleted(msg.PresetName))
            {
                ModLog.Event(LogCat.Dream,
                    "[DreamSync] reject start request — party already completed: " + msg.PresetName);
                SendDreamEndedRejected(requesterId, "already_completed");
                return;
            }

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
                SendDreamEndedRejected(requesterId, "session_active");
                return;
            }

            if (!DreamSession.TryBegin(msg.PresetName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] TryBegin failed for request: {msg.PresetName}");
                SendDreamEndedRejected(requesterId,
                    DreamSession.IsPresetCompleted(msg.PresetName)
                        ? "already_completed"
                        : "try_begin_failed");
                return;
            }

            // Named prepare on host does not hit the random pool; mirror the client's roll consume.
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
                SendDreamEndedRejected(requesterId, "prepare_failed");
            }
        }

        internal void HandleDreamSessionBulk(DreamSessionBulkMessage msg)
        {
            DreamSession.ApplySnapshot(msg.CompletedPresets, msg.LvlFlags);
            // Reconnected (host migration, soft reconnect) while on the dream pad: confirm or leave.
            if (DreamSyncManager.OnSessionBulkWhileInsideDream(msg, _net))
                return;
            if (msg.SessionId != 0)
                DreamSession.AdoptSessionId(msg.SessionId);
            if (!string.IsNullOrEmpty(msg.ActivePreset))
            {
                DreamSession.SetPendingHostPreset(msg.ActivePreset);
                // Remotes that never empty-roll still need pool parity for later random dreams.
                DreamSession.MirrorPoolRemove(msg.ActivePreset);
                if (msg.SessionActive && DreamSession.IsActive)
                    DreamSession.UpdateActivePreset(msg.ActivePreset);
            }
            ModRuntime.LegacyInfo(
                $"[DreamSync] Session bulk: completed={msg.CompletedPresets?.Length ?? 0} "
                + $"active={msg.SessionActive} preset={msg.ActivePreset} session={msg.SessionId}"
                + (msg.HasPadPosition ? " pad" : ""));

            // Late join (or a missed DreamStarted): the snapshot alone left the peer
            // in the overworld while the party was on the pad.
            if (_net.Role == NetworkRole.Client
                && msg.SessionActive
                && msg.HasPadPosition
                && !string.IsNullOrEmpty(msg.ActivePreset)
                && (Dreams.Instance == null || !Dreams.Instance.dreaming)
                && !DreamSyncManager.IsLocalDreamActive
                && !DreamSyncManager.HasPendingEntryTransition)
            {
                DreamSession.BeginFromHost(msg.ActivePreset, msg.SessionId);
                int hostId = _net.CurrentReceivePlayerId > 0
                    ? _net.CurrentReceivePlayerId
                    : (_net.HostPlayerId > 0 ? _net.HostPlayerId : 1);
                // They missed the entry movie. Load the live pad instead of replaying it.
                DreamSyncManager.MarkLocalEntryTransitionPlayed();
                DreamSyncManager.OnRemoteDreamStarted(
                    hostId,
                    msg.ActivePreset,
                    new Vector3(msg.PadX, msg.PadY, msg.PadZ));
            }
        }

        internal void HandleDreamChainStart(DreamChainStartMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NextPresetName))
                return;
            if (_net.Role == NetworkRole.Host)
                return; // host already preparing

            // Reject chain packets from a different session.
            if (DreamSession.IsActive && DreamSession.SessionId != 0 && msg.SessionId != 0
                && msg.SessionId != DreamSession.SessionId)
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Drop DreamChainStart session {msg.SessionId} "
                    + $"(active {DreamSession.SessionId})");
                return;
            }

            if (msg.SessionId != 0)
                DreamSession.AdoptSessionId(msg.SessionId);

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
        internal void HandleDreamItemPickup(DreamItemPickupMessage msg)
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

        internal void HandleDreamAudio(DreamAudioMessage msg)
        {
            Sync.DreamAudioPlayer.PlayForwardedAudio(msg);
        }

        internal void HandleDreamEntered(DreamEnteredMessage msg)
        {
            int playerId = _net.CurrentReceivePlayerId;
            if (_net.Role == NetworkRole.Host && playerId > 0)
                DreamSyncManager.ConfirmRemoteInDream(playerId);
            FinalDreamsceneManager.RefreshConnectedPlayers();
            var proxy = _net.GetProxy(playerId);
            if (proxy != null)
            {
                proxy.FreezePosition = false;
                ModRuntime.LegacyInfo($"[DreamSync] Player {playerId} entered dream — proxy unfrozen");
            }
            // Peer pad ready; push collider parity (lamp trigger and bell solid).
            if (_net.Role == NetworkRole.Host)
                WorldPhysicsSyncService.HostBroadcastDreamPropColliders();
        }

        internal void HandleDreamPropCollider(DreamPropColliderMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            WorldPhysicsSyncService.ApplyDreamPropColliders(msg);
        }

        /// <summary>Host: late-join dream completed + level flags.</summary>
        internal void SendDreamSessionBulkTo(int playerId)
        {
            if (_net.Role != NetworkRole.Host || playerId <= 0) return;
            var bulk = DreamSessionBulkMessage.FromLocal();
            _net.SendToPlayer(playerId, NetMessageType.DreamSessionBulk,
                w => bulk.Serialize(w), DeliveryMethod.ReliableOrdered);
            if (bulk.SessionActive && bulk.HasPadPosition)
            {
                DreamSyncManager.NoteRemoteInDream(playerId);
                var proxy = _net.GetProxy(playerId);
                if (proxy != null)
                    proxy.FreezePosition = true;
            }
        }
    }
}
