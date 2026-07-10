using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static class PlayerPositionManager
    {
        private struct RemotePlayerState
        {
            public Vector3 Position;
            public float RotY;
            public float LastUpdateTime;
        }

        private static Vector3 _hostPos;
        private static readonly Dictionary<int, RemotePlayerState> _remotePlayers = new Dictionary<int, RemotePlayerState>();

        public static void ReportHostPosition(Vector3 pos)
        {
            _hostPos = pos;
        }

        public static void UpdateRemotePlayer(int playerId, Vector3 pos, float rotY)
        {
            _remotePlayers[playerId] = new RemotePlayerState
            {
                Position = pos,
                RotY = rotY,
                LastUpdateTime = Time.time
            };
        }

        public static bool HasRemotePlayer
        {
            get
            {
                if (_remotePlayers.Count == 0) return false;
                float now = Time.time;
                foreach (var kvp in _remotePlayers)
                    if (now - kvp.Value.LastUpdateTime < 3f)
                        return true;
                return false;
            }
        }

        /// <summary>Get all remote player positions (stale entries excluded).</summary>
        public static IEnumerable<Vector3> GetAllRemotePositions()
        {
            float now = Time.time;
            foreach (var kvp in _remotePlayers)
            {
                if (now - kvp.Value.LastUpdateTime < 3f)
                    yield return kvp.Value.Position;
            }
        }

        public static Vector3 GetNearestPlayerPosition(Vector3 fromPos)
        {
            Vector3 nearest = _hostPos;
            float nearestSq = Vector3.SqrMagnitude(_hostPos - fromPos);
            foreach (var kvp in _remotePlayers)
            {
                if ((Time.time - kvp.Value.LastUpdateTime) >= 3f) continue;
                float d = Vector3.SqrMagnitude(kvp.Value.Position - fromPos);
                if (d < nearestSq)
                {
                    nearest = kvp.Value.Position;
                    nearestSq = d;
                }
            }
            return nearest;
        }

        /// <summary>Returns true if any player (host or remote) is within sqrDist of fromPos.</summary>
        public static bool IsAnyPlayerWithinSq(Vector3 fromPos, float sqrDist)
        {
            if (Vector3.SqrMagnitude(_hostPos - fromPos) < sqrDist) return true;
            foreach (var kvp in _remotePlayers)
            {
                if ((Time.time - kvp.Value.LastUpdateTime) >= 3f) continue;
                if (Vector3.SqrMagnitude(kvp.Value.Position - fromPos) < sqrDist) return true;
            }
            return false;
        }

        /// <summary>Returns true if any remote player is within sqrDist of fromPos.</summary>
        public static bool IsAnyRemoteWithinSq(Vector3 fromPos, float sqrDist)
        {
            float now = Time.time;
            foreach (var kvp in _remotePlayers)
            {
                if (now - kvp.Value.LastUpdateTime >= 3f) continue;
                if (Vector3.SqrMagnitude(kvp.Value.Position - fromPos) < sqrDist) return true;
            }
            return false;
        }

        public static float SqrDistanceToNearestPlayer(Vector3 fromPos)
        {
            float d = Vector3.SqrMagnitude(_hostPos - fromPos);
            foreach (var kvp in _remotePlayers)
            {
                if ((Time.time - kvp.Value.LastUpdateTime) >= 3f) continue;
                float dr = Vector3.SqrMagnitude(kvp.Value.Position - fromPos);
                if (dr < d) d = dr;
            }
            return d;
        }

        public static void RemovePlayer(int playerId)
        {
            _remotePlayers.Remove(playerId);
        }

        public static void Clear()
        {
            _remotePlayers.Clear();
        }
    }
}