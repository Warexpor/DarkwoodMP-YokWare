using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies remote door/window barricade changes (send side: Barricade_Patch).
/// Building replays barricade(byPlayer:false) - no plank consumption - and
/// aligns the barricade health; destruction replays destroyBarricade(silent).
/// Inventory grants during replay are suppressed globally (see Inventory_Patch:
/// addItemTypeToPlayer is skipped while RemoteApply is active), so remote
/// destruction never refunds planks to the wrong player.
/// States for unloaded doors/windows are retried periodically.
/// </summary>
public class BarricadeSync
{
    private readonly NetworkLayer _network;

    private class BarState
    {
        public bool Barricaded;
        public int Health;
    }

    private readonly Dictionary<string, BarState> _pendingDoors = new();
    private readonly Dictionary<string, BarState> _pendingWindows = new();
    private const float PendingRetryInterval = 5f;
    private float _lastPendingRetry;

    public BarricadeSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnUpdate()
    {
        if (_pendingDoors.Count == 0 && _pendingWindows.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;

        if (_pendingDoors.Count > 0)
        {
            var doors = UnityEngine.Object.FindObjectsOfType(typeof(Door));
            List<string> applied = null;
            foreach (var kvp in _pendingDoors)
            {
                if (TryApplyDoor(kvp.Key, kvp.Value, doors))
                    (applied ??= new List<string>()).Add(kvp.Key);
            }
            if (applied != null)
                foreach (var id in applied)
                    _pendingDoors.Remove(id);
        }

        if (_pendingWindows.Count > 0)
        {
            var windows = UnityEngine.Object.FindObjectsOfType(typeof(Window));
            List<string> applied = null;
            foreach (var kvp in _pendingWindows)
            {
                if (TryApplyWindow(kvp.Key, kvp.Value, windows))
                    (applied ??= new List<string>()).Add(kvp.Key);
            }
            if (applied != null)
                foreach (var id in applied)
                    _pendingWindows.Remove(id);
        }
    }

    public void Reset()
    {
        _pendingDoors.Clear();
        _pendingWindows.Clear();
    }

    public void OnRemoteDoorBarricade(string id, bool barricaded, int health)
    {
        var state = new BarState { Barricaded = barricaded, Health = health };
        if (TryApplyDoor(id, state, UnityEngine.Object.FindObjectsOfType(typeof(Door))))
            _pendingDoors.Remove(id);
        else
            _pendingDoors[id] = state;
    }

    public void OnRemoteWindowBarricade(string id, bool barricaded, int health)
    {
        var state = new BarState { Barricaded = barricaded, Health = health };
        if (TryApplyWindow(id, state, UnityEngine.Object.FindObjectsOfType(typeof(Window))))
            _pendingWindows.Remove(id);
        else
            _pendingWindows[id] = state;
    }

    private bool TryApplyDoor(string id, BarState state, UnityEngine.Object[] doors)
    {
        var door = FindByGameId(doors, id) as Door;
        if (door == null) return false;

        try
        {
            RemoteApply.Active = true;
            try
            {
                if (state.Barricaded)
                {
                    if (!door.barricaded)
                        door.barricade(false);
                    if (state.Health > 0)
                        door.barricadeHealth = state.Health;
                }
                else if (door.barricaded)
                {
                    door.destroyBarricade(true);
                }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[BarricadeSync] Door '{id}' -> {(state.Barricaded ? $"barricaded ({state.Health} hp)" : "open")}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BarricadeSync] Door '{id}': {ex.Message}");
        }
        return true;
    }

    private bool TryApplyWindow(string id, BarState state, UnityEngine.Object[] windows)
    {
        var window = FindByGameId(windows, id) as Window;
        if (window == null) return false;

        try
        {
            RemoteApply.Active = true;
            try
            {
                if (state.Barricaded)
                {
                    if (!window.barricaded)
                        window.barricade(Mathf.Max(state.Health, 1), false);
                    else if (state.Health > 0)
                        window.barricadeHealth = state.Health;
                }
                else if (window.barricaded)
                {
                    window.destroyBarricade(true);
                }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            ModLogger.Msg($"[BarricadeSync] Window '{id}' -> {(state.Barricaded ? $"barricaded ({state.Health} hp)" : "open")}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[BarricadeSync] Window '{id}': {ex.Message}");
        }
        return true;
    }

    /// <summary>Host: current barricade states for the join snapshot.</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        var playerId = Math.Max(_network.LocalClientId, 0);

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Door)))
        {
            if (obj is not Door door || !door.barricaded) continue;
            into.Add(new BarricadeStatePacket
            {
                PlayerId = playerId,
                Kind = BarricadeStatePacket.KindDoor,
                ObjectId = GameIds.ForComponent(door),
                Barricaded = true,
                Health = door.barricadeHealth
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Window)))
        {
            if (obj is not Window window || !window.barricaded) continue;
            into.Add(new BarricadeStatePacket
            {
                PlayerId = playerId,
                Kind = BarricadeStatePacket.KindWindow,
                ObjectId = GameIds.ForComponent(window),
                Barricaded = true,
                Health = window.barricadeHealth
            });
        }
    }

    private static Component FindByGameId(UnityEngine.Object[] candidates, string objectId)
    {
        var at = objectId.LastIndexOf('@');
        var name = at > 0 ? objectId.Substring(0, at) : null;
        float x = 0f, z = 0f;
        var hasCoords = false;
        if (at > 0)
        {
            var coords = objectId.Substring(at + 1).Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            hasCoords = coords.Length == 2
                && float.TryParse(coords[0], System.Globalization.NumberStyles.Float, inv, out x)
                && float.TryParse(coords[1], System.Globalization.NumberStyles.Float, inv, out z);
        }

        Component best = null;
        var bestDist = 10f * 10f;
        foreach (var obj in candidates)
        {
            if (obj is not Component c) continue;
            if (GameIds.ForComponent(c) == objectId) return c;

            if (!hasCoords || c.gameObject.name != name) continue;
            var p = c.transform.position;
            var dx = p.x - x;
            var dz = p.z - z;
            var d = dx * dx + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }
}
