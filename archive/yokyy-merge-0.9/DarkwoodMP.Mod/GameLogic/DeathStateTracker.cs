using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Night-death coordination (Horde DeathStateTracker, slim).
/// Day death: shared morning via pdeath (existing).
/// Night death: hold shared morning until all session participants are dead
/// (or disconnect); spectator UI is Phase 4b.
/// </summary>
public static class DeathStateTracker
{
    private static int _nightParticipantCount = -1;
    private static bool _localNightDeath;
    private static readonly Dictionary<int, Vector3> _remoteNightDeaths = new Dictionary<int, Vector3>();

    public static bool LocalNightDeath => _localNightDeath;
    public static int RemoteNightDeathCount => _remoteNightDeaths.Count;

    /// <summary>Skip morning rep bonuses when anyone died overnight (Horde).</summary>
    public static bool SkipMorningRepBonus { get; set; }

    public static int TotalRemoteCount
    {
        get
        {
            if (_nightParticipantCount >= 0) return _nightParticipantCount;
            var m = NetworkManager.Instance;
            if (m == null) return 0;
            // Connected players excluding self
            var n = 0;
            foreach (var id in m.ConnectedPlayers)
                if (id != m.LocalPlayerId) n++;
            return n;
        }
    }

    public static bool AllRemoteDead
    {
        get
        {
            int total = TotalRemoteCount;
            if (total <= 0) return true;
            return RemoteNightDeathCount >= total;
        }
    }

    public static bool AllDeadAtNight => _localNightDeath && AllRemoteDead;

    /// <summary>True while any night-death hold is active (shared morning blocked).</summary>
    public static bool NightDeathHoldActive =>
        _localNightDeath || _remoteNightDeaths.Count > 0;

    public static void SnapshotNightParticipants()
    {
        var m = NetworkManager.Instance;
        if (m == null) return;
        var current = 0;
        foreach (var id in m.ConnectedPlayers)
            if (id != m.LocalPlayerId) current++;

        if (_nightParticipantCount < 0)
        {
            _nightParticipantCount = current;
            ModLogger.Msg($"[DeathState] Night participants snapshot: {current} remotes");
        }
        else if (current > _nightParticipantCount)
        {
            ModLogger.Msg($"[DeathState] Night participants raised {_nightParticipantCount} → {current}");
            _nightParticipantCount = current;
        }
    }

    public static void OnLocalNightDeath(Vector3 pos)
    {
        SnapshotNightParticipants();
        _localNightDeath = true;
        SkipMorningRepBonus = true;
        ModLogger.Msg($"[DeathState] Local night death at {pos}");
    }

    public static void OnLocalDayDeath()
    {
        _localNightDeath = false;
    }

    public static void OnRemoteNightDeath(int playerId, Vector3 pos)
    {
        if (playerId < 0) return;
        SnapshotNightParticipants();
        if (_remoteNightDeaths.ContainsKey(playerId)) return;
        _remoteNightDeaths[playerId] = pos;
        SkipMorningRepBonus = true;
        ModLogger.Msg($"[DeathState] Remote night death player {playerId} ({RemoteNightDeathCount}/{TotalRemoteCount})");
    }

    public static void OnRemoteDayDeath(int playerId)
    {
        if (_remoteNightDeaths.Remove(playerId))
            ModLogger.Msg($"[DeathState] Remote day death cleared night flag for {playerId}");
    }

    public static void OnRemoteDisconnected(int playerId)
    {
        if (playerId < 0) return;
        bool wasDead = _remoteNightDeaths.Remove(playerId);
        if (_nightParticipantCount > 0)
        {
            _nightParticipantCount--;
            if (_nightParticipantCount < RemoteNightDeathCount)
                _nightParticipantCount = RemoteNightDeathCount;
        }
        ModLogger.Msg($"[DeathState] Remote {playerId} left mid-night (wasDead={wasDead}, dead={RemoteNightDeathCount}/{TotalRemoteCount})");
    }

    public static void Reset()
    {
        _localNightDeath = false;
        _remoteNightDeaths.Clear();
        _nightParticipantCount = -1;
        // Keep SkipMorningRepBonus until morning rewards would have fired once
    }

    public static void ClearMorningRepSkip()
    {
        SkipMorningRepBonus = false;
    }

    /// <summary>Should we broadcast shared morning clock after local death dream?</summary>
    public static bool ShouldBroadcastDeathClock()
    {
        // Day death: always share morning
        if (!_localNightDeath) return true;
        // Night: only when everyone is dead
        return AllDeadAtNight;
    }

    /// <summary>Should we adopt a remote pdeath clock jump?</summary>
    public static bool ShouldAdoptRemoteDeathClock(bool senderNightDeath)
    {
        // Day death from peer: adopt (existing co-op morning)
        if (!senderNightDeath) return true;
        // Night: only when hold is complete (all dead)
        return AllDeadAtNight || !NightDeathHoldActive;
    }
}
