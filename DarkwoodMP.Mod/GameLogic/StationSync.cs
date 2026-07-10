using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Receive side for shared stations and location NPCs (send side:
/// Station_Patch / LocationNpc_Patch). All replays are idempotent absolute
/// state with the usual pending/retry for unloaded areas.
/// </summary>
public class StationSync
{
    private readonly NetworkLayer _network;

    private readonly Dictionary<string, float> _pendingSawFuel = new();   // id -> fuel
    private readonly Dictionary<string, float> _pendingFeeders = new();   // id -> received
    private readonly Dictionary<string, int> _pendingLures = new();       // id -> health
    private readonly Dictionary<string, string> _pendingLocNpcs = new();  // id -> action
    private const float PendingRetryInterval = 5f;
    private float _lastPendingRetry;

    // Send-side lure outbox: scripted eaters (village pigs etc.) call
    // Lure.removeHealth EVERY FRAME - broadcasting each bite as a reliable
    // packet flooded the channel (delayed player sounds) and the receiver's
    // per-packet FindObjectsOfType + console log froze the client. Only the
    // LATEST health per lure is kept and flushed at 1Hz; destruction (<=0)
    // goes out immediately.
    private readonly Dictionary<string, int> _lureOutbox = new();     // id -> latest health
    private readonly Dictionary<string, int> _lureSentValue = new();  // id -> last broadcast
    private const float LureFlushInterval = 1f;
    private float _lastLureFlush;

    public StationSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnRemoteSawFuel(string id, float fuel)
    {
        if (!TrySawFuel(id, fuel, UnityEngine.Object.FindObjectsOfType(typeof(Saw))))
            _pendingSawFuel[id] = fuel;
        else
            _pendingSawFuel.Remove(id);
    }

    public void OnRemoteFeeder(string id)
    {
        if (!TryFeeder(id, UnityEngine.Object.FindObjectsOfType(typeof(Feeder))))
            _pendingFeeders[id] = Time.time;
    }

    public void OnRemoteLure(string id, int health)
    {
        if (!TryLure(id, health, UnityEngine.Object.FindObjectsOfType(typeof(Lure))))
            _pendingLures[id] = health;
        else
            _pendingLures.Remove(id);
    }

    public void OnRemoteLocationNpc(string id, string action)
    {
        if (!TryLocationNpc(id, action, UnityEngine.Object.FindObjectsOfType(typeof(Location))))
            _pendingLocNpcs[id + "|" + action] = action;
        else
            _pendingLocNpcs.Remove(id + "|" + action);
    }

    /// <summary>
    /// Queue a lure health broadcast (send side, from Station_Patch). Values are
    /// coalesced per lure and flushed at 1Hz; a destruction goes out immediately.
    /// </summary>
    public void QueueLureHealth(string id, int health)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (health < 0) health = 0; // scripted eaters keep chewing past zero
        if (_lureSentValue.TryGetValue(id, out var sent) && sent == health) return;

        _lureOutbox[id] = health;
        if (health <= 0)
            FlushLureOutbox(force: true);
    }

    private void FlushLureOutbox(bool force)
    {
        if (_lureOutbox.Count == 0) return;
        if (!force && Time.time - _lastLureFlush < LureFlushInterval) return;
        _lastLureFlush = Time.time;

        foreach (var kvp in _lureOutbox)
        {
            if (_lureSentValue.TryGetValue(kvp.Key, out var sent) && sent == kvp.Value) continue;
            _lureSentValue[kvp.Key] = kvp.Value;
            SendLure(kvp.Key, kvp.Value);
        }
        _lureOutbox.Clear();
    }

    private void SendLure(string objectId, int health)
    {
        if (_network == null || !_network.IsConnected) return;
        _network.SendReliable(new StationLurePacket
        {
            PlayerId = Math.Max(_network.LocalClientId, 0),
            ObjectId = objectId ?? "",
            Health = health
        });
    }

    public void OnUpdate()
    {
        FlushLureOutbox(force: false);

        if (_pendingSawFuel.Count == 0 && _pendingFeeders.Count == 0
            && _pendingLures.Count == 0 && _pendingLocNpcs.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;

        if (_pendingSawFuel.Count > 0)
        {
            var saws = UnityEngine.Object.FindObjectsOfType(typeof(Saw));
            List<string> done = null;
            foreach (var kvp in _pendingSawFuel)
                if (TrySawFuel(kvp.Key, kvp.Value, saws))
                    (done ??= new List<string>()).Add(kvp.Key);
            RemoveKeys(_pendingSawFuel, done);
        }

        if (_pendingFeeders.Count > 0)
        {
            var feeders = UnityEngine.Object.FindObjectsOfType(typeof(Feeder));
            List<string> done = null;
            foreach (var kvp in _pendingFeeders)
                if (TryFeeder(kvp.Key, feeders))
                    (done ??= new List<string>()).Add(kvp.Key);
            RemoveKeys(_pendingFeeders, done);
        }

        if (_pendingLures.Count > 0)
        {
            var lures = UnityEngine.Object.FindObjectsOfType(typeof(Lure));
            List<string> done = null;
            foreach (var kvp in _pendingLures)
                if (TryLure(kvp.Key, kvp.Value, lures))
                    (done ??= new List<string>()).Add(kvp.Key);
            RemoveKeys(_pendingLures, done);
        }

        if (_pendingLocNpcs.Count > 0)
        {
            var locations = UnityEngine.Object.FindObjectsOfType(typeof(Location));
            List<string> done = null;
            foreach (var kvp in _pendingLocNpcs)
            {
                var sep = kvp.Key.LastIndexOf('|');
                if (sep > 0 && TryLocationNpc(kvp.Key.Substring(0, sep), kvp.Value, locations))
                    (done ??= new List<string>()).Add(kvp.Key);
            }
            RemoveKeys(_pendingLocNpcs, done);
        }
    }

    public void Reset()
    {
        _pendingSawFuel.Clear();
        _pendingFeeders.Clear();
        _pendingLures.Clear();
        _pendingLocNpcs.Clear();
        _lureOutbox.Clear();
        _lureSentValue.Clear();
    }

    /// <summary>Join bulk: absolute saw fuel for every Saw in scene.</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        if (into == null) return;
        try
        {
            var localId = Math.Max(_network.LocalClientId, 0);
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Saw)))
            {
                if (obj is not Saw saw) continue;
                var id = GameIds.ForComponent(saw);
                into.Add(new StationSawFuelPacket
                {
                    PlayerId = localId,
                    ObjectId = id,
                    Fuel = saw.fuel
                });
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StationSync] CollectSnapshot: {ex.Message}");
        }
    }

    private static void RemoveKeys<T>(Dictionary<string, T> dict, List<string> keys)
    {
        if (keys == null) return;
        foreach (var key in keys)
            dict.Remove(key);
    }

    private static bool TrySawFuel(string id, float fuel, UnityEngine.Object[] candidates)
    {
        if (GameIds.FindByGameId(candidates, id) is not Saw saw) return false;
        try
        {
            RemoteApply.Active = true;
            try
            {
                saw.fuel = Mathf.Clamp(fuel, 0f, saw.maxFuel > 0f ? saw.maxFuel : fuel);
                saw.refresh();
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[StationSync] Saw '{id}' fuel -> {fuel:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StationSync] Saw '{id}': {ex.Message}");
        }
        return true;
    }

    private static bool TryFeeder(string id, UnityEngine.Object[] candidates)
    {
        if (GameIds.FindByGameId(candidates, id) is not Feeder feeder) return false;
        try
        {
            RemoteApply.Active = true;
            try
            {
                // activate() = makeInactive + personal buff; only the state
                // flip is shared (the buff belongs to the player who used it)
                feeder.makeInactive();
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[StationSync] Feeder '{id}' used");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StationSync] Feeder '{id}': {ex.Message}");
        }
        return true;
    }

    private static bool TryLure(string id, int health, UnityEngine.Object[] candidates)
    {
        if (GameIds.FindByGameId(candidates, id) is not Lure lure) return true; // destroyed = converged
        try
        {
            if (lure.health > health)
            {
                RemoteApply.Active = true;
                try
                {
                    // Replay the delta so the destruction path (bodypart
                    // markers, map element) runs here too when it hits zero
                    lure.removeHealth(lure.health - health, null);
                }
                finally
                {
                    RemoteApply.Active = false;
                }
                ModLogger.Msg($"[StationSync] Lure '{id}' health -> {health}");
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StationSync] Lure '{id}': {ex.Message}");
        }
        return true;
    }

    private static bool TryLocationNpc(string id, string action, UnityEngine.Object[] candidates)
    {
        if (GameIds.FindByGameId(candidates, id) is not Location location) return false;
        try
        {
            RemoteApply.Active = true;
            try
            {
                // ONLY the porter is replicated: trader/wolf spawns are
                // time-driven (startAfterNight) and the despawns are
                // location-exit-driven - they already run on every machine,
                // a replay would double-fire them (see Porter_Patch)
                if (action == "porter+")
                    location.spawnPorter();
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[StationSync] Location '{id}': {action}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[StationSync] Location '{id}' {action}: {ex.Message}");
        }
        return true;
    }
}
