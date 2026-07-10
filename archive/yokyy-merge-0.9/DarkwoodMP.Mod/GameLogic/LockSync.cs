using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Receive side of lock-state sync (send side: Lock_Patch). Idempotent
/// replays with the usual pending/retry for unloaded areas:
/// - "unlock" -&gt; Locked.locked = false (covers doors, items, containers)
/// - "doorlock" -&gt; Door.lockMe(keyType)
/// - "padunlock" -&gt; Padlock.unlock(true) so the padlock's event triggers
///   open whatever it guards on this machine too
/// </summary>
public class LockSync
{
    private readonly NetworkLayer _network;

    private readonly Dictionary<string, float> _pendingUnlocks = new();   // id -> received
    private readonly Dictionary<string, string> _pendingDoorLocks = new(); // id -> keyType
    private readonly Dictionary<string, float> _pendingPadlocks = new();  // id -> received
    private const float PendingRetryInterval = 5f;
    private const float PendingTtl = 600f;
    private float _lastPendingRetry;

    // Session lock history for the join snapshot (v0.5): a player who joins
    // AFTER something was unlocked/locked would otherwise stay on the old
    // state. Local actions and applied remote actions both land here.
    private readonly HashSet<string> _sessionUnlocks = new();
    private readonly HashSet<string> _sessionPadlocks = new();
    private readonly Dictionary<string, string> _sessionDoorLocks = new();

    public LockSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnRemoteUnlock(string id)
    {
        RecordUnlock(id);
        if (!TryUnlock(id, UnityEngine.Object.FindObjectsOfType(typeof(Locked))))
            _pendingUnlocks[id] = Time.time;
    }

    public void OnRemoteDoorLock(string id, string keyType)
    {
        RecordDoorLock(id, keyType);
        if (!TryDoorLock(id, keyType, UnityEngine.Object.FindObjectsOfType(typeof(Door))))
            _pendingDoorLocks[id] = keyType;
    }

    public void OnRemotePadlockUnlock(string id)
    {
        RecordPadlockUnlock(id);
        if (!TryPadlockUnlock(id, UnityEngine.Object.FindObjectsOfType(typeof(Padlock))))
            _pendingPadlocks[id] = Time.time;
    }

    /// <summary>Session history (send side calls these via Lock_Patch).</summary>
    public void RecordUnlock(string id)
    {
        _sessionUnlocks.Add(id);
        _sessionDoorLocks.Remove(id); // an unlock supersedes an earlier lock
    }

    public void RecordDoorLock(string id, string keyType)
    {
        _sessionDoorLocks[id] = keyType ?? "";
        _sessionUnlocks.Remove(id); // a lock supersedes an earlier unlock
    }

    public void RecordPadlockUnlock(string id)
    {
        _sessionPadlocks.Add(id);
    }

    /// <summary>
    /// Order-independent digest of the session lock history for the desync
    /// check (v0.6). Local and remote actions both land in the session sets,
    /// so working sync makes the digests converge on all machines.
    /// </summary>
    public void GetDigest(out int count, out uint hash)
    {
        count = _sessionUnlocks.Count + _sessionPadlocks.Count + _sessionDoorLocks.Count;
        hash = 0;
        unchecked
        {
            foreach (var id in _sessionUnlocks) hash += SyncCheck.Fnv1a("u:" + id);
            foreach (var id in _sessionPadlocks) hash += SyncCheck.Fnv1a("p:" + id);
            foreach (var kvp in _sessionDoorLocks) hash += SyncCheck.Fnv1a("d:" + kvp.Key + "=" + kvp.Value);
        }
    }

    /// <summary>Host: replay the session's lock changes for a new joiner.</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        var playerId = Math.Max(_network.LocalClientId, 0);
        foreach (var id in _sessionUnlocks)
            into.Add(new WorldLockPacket { PlayerId = playerId, Kind = WorldLockPacket.KindUnlock, ObjectId = id });
        foreach (var id in _sessionPadlocks)
            into.Add(new WorldLockPacket { PlayerId = playerId, Kind = WorldLockPacket.KindPadUnlock, ObjectId = id });
        foreach (var kvp in _sessionDoorLocks)
            into.Add(new WorldLockPacket { PlayerId = playerId, Kind = WorldLockPacket.KindDoorLock, ObjectId = kvp.Key, KeyType = kvp.Value ?? "" });
    }

    public void OnUpdate()
    {
        if (_pendingUnlocks.Count == 0 && _pendingDoorLocks.Count == 0 && _pendingPadlocks.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;

        if (_pendingUnlocks.Count > 0)
        {
            var candidates = UnityEngine.Object.FindObjectsOfType(typeof(Locked));
            List<string> done = null;
            foreach (var kvp in _pendingUnlocks)
                if (TryUnlock(kvp.Key, candidates) || Time.time - kvp.Value > PendingTtl)
                    (done ??= new List<string>()).Add(kvp.Key);
            Remove(_pendingUnlocks, done);
        }

        if (_pendingDoorLocks.Count > 0)
        {
            var candidates = UnityEngine.Object.FindObjectsOfType(typeof(Door));
            List<string> done = null;
            foreach (var kvp in _pendingDoorLocks)
                if (TryDoorLock(kvp.Key, kvp.Value, candidates))
                    (done ??= new List<string>()).Add(kvp.Key);
            Remove(_pendingDoorLocks, done);
        }

        if (_pendingPadlocks.Count > 0)
        {
            var candidates = UnityEngine.Object.FindObjectsOfType(typeof(Padlock));
            List<string> done = null;
            foreach (var kvp in _pendingPadlocks)
                if (TryPadlockUnlock(kvp.Key, candidates) || Time.time - kvp.Value > PendingTtl)
                    (done ??= new List<string>()).Add(kvp.Key);
            Remove(_pendingPadlocks, done);
        }
    }

    public void Reset()
    {
        _pendingUnlocks.Clear();
        _pendingDoorLocks.Clear();
        _pendingPadlocks.Clear();
        _sessionUnlocks.Clear();
        _sessionPadlocks.Clear();
        _sessionDoorLocks.Clear();
    }

    private static void Remove<T>(Dictionary<string, T> dict, List<string> keys)
    {
        if (keys == null) return;
        foreach (var key in keys)
            dict.Remove(key);
    }

    private static bool TryUnlock(string id, UnityEngine.Object[] candidates)
    {
        // The id may come from the Door OR the Locked component - same
        // gameObject, same "name@x,z" either way
        var component = GameIds.FindByGameId(candidates, id);
        if (component is not Locked locked) return false;

        if (locked.locked)
        {
            locked.locked = false;
            ModLogger.Msg($"[LockSync] Unlocked '{id}'");
        }
        return true;
    }

    private static bool TryDoorLock(string id, string keyType, UnityEngine.Object[] candidates)
    {
        var component = GameIds.FindByGameId(candidates, id);
        if (component is not Door door) return false;

        try
        {
            RemoteApply.Active = true;
            try
            {
                door.lockMe(keyType);
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[LockSync] Locked door '{id}' (key: {keyType})");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[LockSync] Door lock '{id}': {ex.Message}");
        }
        return true;
    }

    private static bool TryPadlockUnlock(string id, UnityEngine.Object[] candidates)
    {
        var component = GameIds.FindByGameId(candidates, id);
        if (component is not Padlock padlock) return false;

        if (!padlock.locked) return true; // already done (idempotent)

        try
        {
            RemoteApply.Active = true;
            try
            {
                // manually:true fires the padlock's event triggers - that is
                // what opens the guarded object (gevent broadcasts stay
                // suppressed because RemoteApply is active)
                padlock.unlock(true);
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[LockSync] Padlock '{id}' unlocked");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[LockSync] Padlock '{id}': {ex.Message}");
        }
        return true;
    }
}
