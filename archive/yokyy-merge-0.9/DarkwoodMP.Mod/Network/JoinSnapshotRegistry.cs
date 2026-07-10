using System;
using System.Collections.Generic;
using DarkwoodMP.Packets;

namespace DarkwoodMP.Network;

/// <summary>
/// Collectors for join-time world state. Authority calls
/// <see cref="CollectAll"/> for the joining player only (not a blast to all peers).
/// </summary>
public static class JoinSnapshotRegistry
{
    private static readonly List<Action<List<Packet>, int>> _collectors = new List<Action<List<Packet>, int>>();

    /// <summary>
    /// Register a collector. <paramref name="collect"/> appends packets for
    /// <paramref name="targetPlayerId"/> (may ignore id if snapshot is global).
    /// </summary>
    public static void Register(Action<List<Packet>, int> collect)
    {
        if (collect != null && !_collectors.Contains(collect))
            _collectors.Add(collect);
    }

    public static void Unregister(Action<List<Packet>, int> collect)
    {
        if (collect != null)
            _collectors.Remove(collect);
    }

    public static void CollectAll(List<Packet> packets, int targetPlayerId)
    {
        for (int i = 0; i < _collectors.Count; i++)
        {
            try
            {
                _collectors[i](packets, targetPlayerId);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[JoinBulk] collector failed: {ex.Message}");
            }
        }
    }

    public static int CollectorCount => _collectors.Count;
}
