using System.Collections.Generic;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Multi-player position book (Horde PlayerPositionManager design).
/// Tracks local + every remote by stable PlayerId for multi-center AI/physics
/// and nearest-player queries — independent of whether a proxy GO exists yet.
/// </summary>
public static class PlayerPositionRegistry
{
    private const float StaleSeconds = 3f;

    private struct Entry
    {
        public Vector3 Position;
        public float RotY;
        public float LastUpdateTime;
    }

    private static int _localPlayerId = -1;
    private static Vector3 _localPos;
    private static float _localRotY;
    private static float _localUpdateTime;
    private static readonly Dictionary<int, Entry> _remotes = new Dictionary<int, Entry>();

    public static void SetLocalPlayerId(int playerId) => _localPlayerId = playerId;

    public static void ReportLocal(Vector3 pos, float rotY = 0f)
    {
        _localPos = pos;
        _localRotY = rotY;
        _localUpdateTime = Time.time;
    }

    public static void UpdateRemote(int playerId, Vector3 pos, float rotY = 0f)
    {
        if (playerId < 0) return;
        if (_localPlayerId >= 0 && playerId == _localPlayerId) return;
        _remotes[playerId] = new Entry
        {
            Position = pos,
            RotY = rotY,
            LastUpdateTime = Time.time
        };
    }

    public static void Remove(int playerId) => _remotes.Remove(playerId);

    public static void Clear()
    {
        _remotes.Clear();
        _localPlayerId = -1;
        _localUpdateTime = 0f;
    }

    public static bool TryGet(int playerId, out Vector3 pos)
    {
        if (_localPlayerId >= 0 && playerId == _localPlayerId && IsFresh(_localUpdateTime))
        {
            pos = _localPos;
            return true;
        }
        if (_remotes.TryGetValue(playerId, out var e) && IsFresh(e.LastUpdateTime))
        {
            pos = e.Position;
            return true;
        }
        pos = default;
        return false;
    }

    public static Vector3 LocalPosition => _localPos;

    public static bool HasAnyRemote
    {
        get
        {
            float now = Time.time;
            foreach (var kvp in _remotes)
                if (now - kvp.Value.LastUpdateTime < StaleSeconds)
                    return true;
            return false;
        }
    }

    /// <summary>Fresh remote positions only (not local).</summary>
    public static void CollectRemotePositions(List<(int id, Vector3 pos)> into)
    {
        if (into == null) return;
        float now = Time.time;
        foreach (var kvp in _remotes)
        {
            if (now - kvp.Value.LastUpdateTime >= StaleSeconds) continue;
            into.Add((kvp.Key, kvp.Value.Position));
        }
    }

    /// <summary>Local (if fresh) + all fresh remotes.</summary>
    public static void CollectAllPositions(List<(int id, Vector3 pos)> into)
    {
        if (into == null) return;
        if (_localPlayerId >= 0 && IsFresh(_localUpdateTime))
            into.Add((_localPlayerId, _localPos));
        CollectRemotePositions(into);
    }

    public static Vector3 GetNearestPlayerPosition(Vector3 fromPos)
    {
        Vector3 nearest = _localPos;
        float nearestSq = Vector3.SqrMagnitude(_localPos - fromPos);
        float now = Time.time;
        foreach (var kvp in _remotes)
        {
            if (now - kvp.Value.LastUpdateTime >= StaleSeconds) continue;
            float d = Vector3.SqrMagnitude(kvp.Value.Position - fromPos);
            if (d < nearestSq)
            {
                nearest = kvp.Value.Position;
                nearestSq = d;
            }
        }
        return nearest;
    }

    public static bool IsAnyPlayerWithinSq(Vector3 fromPos, float sqrDist)
    {
        if (IsFresh(_localUpdateTime) && Vector3.SqrMagnitude(_localPos - fromPos) < sqrDist)
            return true;
        float now = Time.time;
        foreach (var kvp in _remotes)
        {
            if (now - kvp.Value.LastUpdateTime >= StaleSeconds) continue;
            if (Vector3.SqrMagnitude(kvp.Value.Position - fromPos) < sqrDist)
                return true;
        }
        return false;
    }

    public static float SqrDistanceToNearestPlayer(Vector3 fromPos)
    {
        float d = IsFresh(_localUpdateTime)
            ? Vector3.SqrMagnitude(_localPos - fromPos)
            : float.MaxValue;
        float now = Time.time;
        foreach (var kvp in _remotes)
        {
            if (now - kvp.Value.LastUpdateTime >= StaleSeconds) continue;
            float dr = Vector3.SqrMagnitude(kvp.Value.Position - fromPos);
            if (dr < d) d = dr;
        }
        return d;
    }

    private static bool IsFresh(float lastUpdate) => Time.time - lastUpdate < StaleSeconds;
}
