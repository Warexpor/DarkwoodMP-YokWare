using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies remote door state changes to the real Door components in the scene.
/// Doors are matched cross-client by name + rounded position (see GameIds).
/// </summary>
public class DoorSync
{
    private readonly NetworkLayer _network;
    private readonly Dictionary<string, DoorStateInfo> _doorStates = new();
    private readonly HashSet<string> _warnedMissing = new();
    private readonly Dictionary<string, DoorStatePacket> _pending = new();
    private const float PendingRetryInterval = 5f;
    private float _lastPendingRetry;

    private Type _doorType;
    private FieldInfo _openedField;
    private FieldInfo _openForceField;
    private MethodInfo _openMethod;
    private MethodInfo _closeMethod;

    public DoorSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnUpdate()
    {
        if (_pending.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;
        if (!ResolveDoorApi()) return;

        List<string> done = null;
        foreach (var kvp in _pending)
        {
            if (TryApplyDoorState(kvp.Value))
                (done ??= new List<string>()).Add(kvp.Key);
        }
        if (done != null)
            foreach (var id in done)
                _pending.Remove(id);
    }

    public void Reset()
    {
        _doorStates.Clear();
        _pending.Clear();
        _warnedMissing.Clear();
    }

    public void OnDoorState(DoorStatePacket packet)
    {
        _doorStates[packet.DoorId] = new DoorStateInfo
        {
            DoorId = packet.DoorId,
            IsOpen = packet.IsOpen,
            ControlledBy = packet.PlayerId,
            LastUpdated = Time.time
        };

        if (!ResolveDoorApi())
        {
            _pending[packet.DoorId] = packet;
            return;
        }

        if (!TryApplyDoorState(packet))
        {
            _pending[packet.DoorId] = packet;
            if (_warnedMissing.Add(packet.DoorId))
                ModLogger.Warning($"[DoorSync] Door '{packet.DoorId}' not found - pending");
        }
        else
        {
            _pending.Remove(packet.DoorId);
        }
    }

    private bool TryApplyDoorState(DoorStatePacket packet)
    {
        var door = FindDoor(packet.DoorId);
        if (door == null) return false;

        try
        {
            var opened = _openedField.GetValue(door) is bool b && b;
            if (opened == packet.IsOpen) return true;

            // Opener position decides the swing direction - use the real one from
            // the packet. As opener transform prefer the remote player's object.
            var openerPos = new Vector3(packet.OpenerX, packet.OpenerY, packet.OpenerZ);
            if (openerPos == Vector3.zero)
                openerPos = door.transform.position;
            var openerTransform = NetworkManager.Instance?.GetRemotePlayer(packet.PlayerId)?.transform
                ?? door.transform;

            RemoteApply.Active = true;
            try
            {
                if (packet.IsOpen)
                {
                    var force = _openForceField?.GetValue(door) is int f ? (float)f : 1f;
                    // open(Vector3 openerPosition, Transform openerTransform, float OpenForce)
                    _openMethod.Invoke(door, new object[] { openerPos, openerTransform, force });
                }
                else
                {
                    // close(Transform openerTransform)
                    _closeMethod.Invoke(door, new object[] { openerTransform });
                }
            }
            finally
            {
                RemoteApply.Active = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[DoorSync] Failed to apply door state: {ex.Message}");
            return false;
        }
    }

    /// <summary>Host: current state of every door, for the join snapshot.</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        if (!ResolveDoorApi()) return;

        var playerId = _network.LocalClientId >= 0 ? _network.LocalClientId : 0;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_doorType))
        {
            if (obj is not Component door) continue;
            var opened = _openedField.GetValue(door) is bool b && b;
            var pos = door.transform.position;
            into.Add(new DoorStatePacket
            {
                DoorId = GameIds.ForComponent(door),
                IsOpen = opened,
                PlayerId = playerId,
                OpenerX = pos.x, OpenerY = pos.y, OpenerZ = pos.z
            });
        }
    }

    public bool IsDoorOpen(string doorId)
    {
        return _doorStates.TryGetValue(doorId, out var s) && s.IsOpen;
    }

    public IReadOnlyDictionary<string, DoorStateInfo> GetDoorStates() => _doorStates;

    private bool ResolveDoorApi()
    {
        if (_doorType != null) return _openMethod != null && _closeMethod != null && _openedField != null;

        _doorType = GameTypes.GetType("Door");
        if (_doorType == null) return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _openedField = _doorType.GetField("opened", flags);
        _openForceField = _doorType.GetField("openForce", flags);
        foreach (var m in _doorType.GetMethods(flags))
        {
            if (m.Name == "open" && m.GetParameters().Length == 3) _openMethod = m;
            if (m.Name == "close" && m.GetParameters().Length == 1) _closeMethod = m;
        }
        return _openMethod != null && _closeMethod != null && _openedField != null;
    }

    private readonly Dictionary<string, Component> _resolvedDoors = new();

    private Component FindDoor(string doorId)
    {
        if (_resolvedDoors.TryGetValue(doorId, out var cached) && cached != null)
            return cached;

        // Exact id match first
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_doorType))
        {
            if (obj is Component c && GameIds.ForComponent(c) == doorId)
            {
                _resolvedDoors[doorId] = c;
                return c;
            }
        }

        // Fallback: worlds may differ slightly (even with a shared seed) -
        // take the nearest door with the same name close to the encoded spot
        var at = doorId.LastIndexOf('@');
        if (at <= 0) return null;
        var name = doorId.Substring(0, at);
        var coords = doorId.Substring(at + 1).Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (coords.Length != 2
            || !float.TryParse(coords[0], System.Globalization.NumberStyles.Float, inv, out var x)
            || !float.TryParse(coords[1], System.Globalization.NumberStyles.Float, inv, out var z))
            return null;

        Component best = null;
        var bestDist = 10f * 10f;
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(_doorType))
        {
            if (obj is not Component c) continue;
            if (c.gameObject.name != name) continue;
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

        if (best != null)
            _resolvedDoors[doorId] = best;
        return best;
    }
}

/// <summary>
/// Stores the current state of a synced door.
/// </summary>
public class DoorStateInfo
{
    public string DoorId = "";
    public bool IsOpen;
    public int ControlledBy;
    public float LastUpdated;
}
