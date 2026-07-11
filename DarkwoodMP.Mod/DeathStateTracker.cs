using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Tracks death state for night-death coordination between host and clients.
    /// Handles: remote death tracking, morning advance when all players dead.
    /// </summary>
    public static class DeathStateTracker
    {
        private static int _nightParticipantCount = -1;

        public static int TotalRemoteCount
        {
            get
            {
                if (_nightParticipantCount >= 0)
                    return _nightParticipantCount;
                var net = ModRuntime.Network as LanNetworkManager;
                return net?.RemotePlayerCount ?? 0;
            }
            set { /* kept for backward compat */ }
        }

        public static bool LocalNightDeath { get; private set; }

        public static bool AllRemoteDead
        {
            get
            {
                int total = TotalRemoteCount;
                if (total <= 0)
                    return true;
                return RemoteNightDeathCount >= total;
            }
        }

        public static int RemoteNightDeathCount { get; private set; }

        private static readonly Dictionary<int, Vector3> _remoteDeathPositions = new Dictionary<int, Vector3>();

        public static bool AllDeadAtNight => LocalNightDeath && AllRemoteDead;

        public static bool RemoteNightDeath => RemoteNightDeathCount > 0;

        public static Vector3 LocalDeathPosition { get; private set; }

        public static bool LocalBagSynced { get; set; }

        public static bool SkipMorningRepBonus { get; set; }

        public static bool PreventSpectator { get; set; }

        /// <summary>True while host is mid TryResolveNightMorning to avoid re-entry.</summary>
        private static bool _resolvingMorning;

        /// <summary>
        /// Freeze (or raise) the remote-player count used for AllRemoteDead.
        /// First death captures the count; a mid-night join can raise it so
        /// AllDeadAtNight still requires the new peer to die (or disconnect).
        /// </summary>
        public static void SnapshotNightParticipants()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            int current = net?.RemotePlayerCount ?? 0;
            if (_nightParticipantCount < 0)
            {
                _nightParticipantCount = current;
                ModLog.Event(LogCat.Death, $"Night participants snapshot: {_nightParticipantCount} remotes");
            }
            else if (current > _nightParticipantCount)
            {
                ModLog.Event(LogCat.Death,
                    $"Night participants raised {_nightParticipantCount} → {current} (mid-night join)");
                _nightParticipantCount = current;
            }
        }

        public static void Reset()
        {
            LocalNightDeath = false;
            RemoteNightDeathCount = 0;
            _remoteDeathPositions.Clear();
            LocalDeathPosition = Vector3.zero;
            LocalBagSynced = false;
            PreventSpectator = false;
            SkipMorningRepBonus = false;
            _nightParticipantCount = -1;
            _resolvingMorning = false;
        }

        public static void OnLocalNightDeath(Vector3 pos)
        {
            SnapshotNightParticipants();
            LocalNightDeath = true;
            SkipMorningRepBonus = true;
            LocalDeathPosition = pos;
            LocalBagSynced = false;
            ModLog.Event(LogCat.Death, $"Local night death at {pos}");
        }

        public static void OnRemoteNightDeath(int playerId, Vector3 pos)
        {
            SnapshotNightParticipants();
            if (_remoteDeathPositions.ContainsKey(playerId))
                return;
            _remoteDeathPositions[playerId] = pos;
            RemoteNightDeathCount = _remoteDeathPositions.Count;
            ModLog.Event(LogCat.Death, $"Remote night death for player {playerId} (count={RemoteNightDeathCount}/{TotalRemoteCount})");
        }

        /// <summary>True while this remote is still night-dead (until morning Reset).</summary>
        public static bool IsRemoteNightDead(int playerId)
        {
            return playerId > 0 && _remoteDeathPositions.ContainsKey(playerId);
        }

        public static void OnLocalDayDeath()
        {
            LocalNightDeath = false;
            ModLog.Event(LogCat.Death, "Local day death (normal respawn)");
        }

        public static void OnRemoteDayDeath(int playerId)
        {
            if (_remoteDeathPositions.Remove(playerId))
            {
                RemoteNightDeathCount = _remoteDeathPositions.Count;
                ModLog.Event(LogCat.Death, $"Remote day death for player {playerId} (count={RemoteNightDeathCount}/{TotalRemoteCount})");
            }
        }

        public static void OnRemoteDisconnected(int playerId)
        {
            if (playerId <= 0) return;

            bool wasDead = _remoteDeathPositions.Remove(playerId);
            if (wasDead)
                RemoteNightDeathCount = _remoteDeathPositions.Count;

            if (_nightParticipantCount > 0)
            {
                _nightParticipantCount--;
                if (_nightParticipantCount < RemoteNightDeathCount)
                    _nightParticipantCount = RemoteNightDeathCount;
            }

            ModLog.Event(LogCat.Death,
                $"Remote player {playerId} disconnected mid-night " +
                $"(wasDead={wasDead}, dead={RemoteNightDeathCount}/{TotalRemoteCount})");
        }

        /// <summary>
        /// Host-only: if everyone relevant is dead at night, advance morning once.
        /// Used by death handlers and disconnect cleanup (polish P1.6).
        /// </summary>
        public static bool TryResolveNightMorning(string reason)
        {
            if (_resolvingMorning) return false;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return false;
            if (!LocalNightDeath || !AllDeadAtNight)
                return false;

            _resolvingMorning = true;
            try
            {
                ModLog.Event(LogCat.Death, $"All dead at night — resolving morning ({reason})");

                net.Broadcast(NetMessageType.NightDeathState,
                    w => new NightDeathStateMessage { IsDead = true, AllDeadTrigger = true }.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);

                if (SpectatorModeController.Instance != null && SpectatorModeController.Instance.IsSpectating)
                    SpectatorModeController.Instance.ExitAndRespawn();

                if (Singleton<Controller>.Instance != null)
                    Singleton<Controller>.Instance.skipDay();

                if (Singleton<SaveManager>.Instance != null)
                    Singleton<SaveManager>.Instance.Save(doJson: true);

                Reset();
                return true;
            }
            finally
            {
                _resolvingMorning = false;
            }
        }
    }
}
