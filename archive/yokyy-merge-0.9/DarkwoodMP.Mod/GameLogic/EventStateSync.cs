using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Receive side of scripted-event sync (send side: GameEvents_Patch).
/// Horde design: remote one-shots actually <see cref="GameEvents.fire"/> under
/// apply guard so cutscene/spawn GO side-effects run; not mark-only.
/// </summary>
public class EventStateSync
{
    private readonly NetworkLayer _network;

    private readonly Dictionary<string, float> _pending = new Dictionary<string, float>();
    private readonly HashSet<string> _sessionFired = new HashSet<string>();
    private const float PendingRetryInterval = 5f;
    private const float PendingTtl = 600f;
    private float _lastPendingRetry;

    public EventStateSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnRemoteEventFired(string eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        _sessionFired.Add(eventId);
        if (TryApply(eventId, UnityEngine.Object.FindObjectsOfType(typeof(GameEvents))))
            return;
        _pending[eventId] = Time.time;
    }

    /// <summary>Local authority just fired — remember for join bulk.</summary>
    public void RecordLocalFired(string eventId)
    {
        if (!string.IsNullOrEmpty(eventId))
            _sessionFired.Add(eventId);
    }

    public void CollectSnapshot(List<Packets.Packet> into)
    {
        if (into == null || _sessionFired.Count == 0) return;
        var localId = Math.Max(_network.LocalClientId, 0);
        foreach (var id in _sessionFired)
        {
            into.Add(new GameEventFirePacket
            {
                PlayerId = localId,
                EventId = id
            });
        }
    }

    public void OnUpdate()
    {
        if (_pending.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;

        var candidates = UnityEngine.Object.FindObjectsOfType(typeof(GameEvents));
        List<string> done = null;
        foreach (var kvp in _pending)
        {
            if (TryApply(kvp.Key, candidates) || Time.time - kvp.Value > PendingTtl)
                (done ??= new List<string>()).Add(kvp.Key);
        }
        if (done != null)
            foreach (var id in done)
                _pending.Remove(id);
    }

    public void Reset()
    {
        _pending.Clear();
        _sessionFired.Clear();
    }

    private static bool TryApply(string eventId, UnityEngine.Object[] candidates)
    {
        var component = GameIds.FindByGameId(candidates, eventId);
        if (component is not GameEvents ge) return false;

        if (ge.fired && !ge.multipleFire)
            return true;

        try
        {
            RemoteApply.Active = true;
            try
            {
                // Horde: execute the batch so local story GOs run, not mark-only
                ge.fire();
            }
            finally
            {
                RemoteApply.Active = false;
            }
            // Ensure mark even if fire() no-ops
            if (!ge.fired)
            {
                ge.fired = true;
                var controller = UnityEngine.Object.FindObjectOfType<Controller>();
                if (controller != null)
                    ge.timeFired = controller.gameTime;
            }
            ModLogger.Msg($"[EventStateSync] Fired remote '{eventId}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[EventStateSync] fire '{eventId}': {ex.Message}");
            // Fallback mark so we don't infinite-retry broken batches
            ge.fired = true;
        }
        return true;
    }
}
