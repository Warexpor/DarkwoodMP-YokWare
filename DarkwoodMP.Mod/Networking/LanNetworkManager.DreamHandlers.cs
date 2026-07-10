using System;
using UnityEngine;
using DWMPHorde;
using DWMPHorde.Sync;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleDreamStarted(DreamStartedMessage msg)
        {
            Vector3 locPos = new Vector3(msg.LocPosX, msg.LocPosY, msg.LocPosZ);
            int playerId = _currentReceivePlayerId;
            // Shared session: every peer fully enters this dream
            if (!Sync.DreamSession.IsActive)
                Sync.DreamSession.TryBegin(msg.PresetName);
            Sync.DreamSyncManager.OnRemoteDreamStarted(playerId, msg.PresetName, locPos);
            Sync.DreamSession.MarkActive();
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

            // Client story completion → host ends the shared session authoritatively
            if (_role == NetworkRole.Host
                && !string.IsNullOrEmpty(msg.OutcomeName)
                && msg.OutcomeName != "playerDeath"
                && Dreams.Instance != null
                && Dreams.Instance.dreaming)
            {
                ModRuntime.LegacyInfo($"[DreamSession] Host applying client story end: {msg.OutcomeName}");
                // Set outcome then end — DreamEndPatch broadcasts to remaining peers
                Dreams.Instance.outcome = msg.OutcomeName;
                Dreams.Instance.endDreaming();
                return;
            }

            Sync.DreamSession.End(msg.OutcomeName);
            Sync.DreamSyncManager.OnRemoteDreamEnded(playerId, msg.OutcomeName);
        }

        private void HandleDreamStartRequest(DreamStartRequestMessage msg)
        {
            if (_role != NetworkRole.Host)
                return;
            if (string.IsNullOrEmpty(msg.PresetName))
                return;
            if (Sync.DreamSession.IsActive || Sync.DreamSyncManager.IsDreamActive)
            {
                ModRuntime.LegacyInfo($"[DreamSync] Ignoring dream start request — already in a dream: {msg.PresetName}");
                return;
            }
            if (Sync.DreamSession.IsPresetCompleted(msg.PresetName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Ignoring dream start request — already completed: {msg.PresetName}");
                return;
            }

            ModRuntime.LegacyInfo($"[DreamSync] Host handling dream start request: {msg.PresetName}");
            Singleton<Controller>.Instance.StartCoroutine(
                Singleton<Dreams>.Instance.prepareDream(msg.PresetName));
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

            // Best-effort visual: destroy nearest matching world item so spectators
            // do not keep seeing a looted object floating in the dream.
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
            var proxy = GetProxy(playerId);
            if (proxy != null)
            {
                proxy.FreezePosition = false;
                ModRuntime.LegacyInfo($"[DreamSync] Player {playerId} entered dream — proxy unfrozen");
            }
        }
    }
}
