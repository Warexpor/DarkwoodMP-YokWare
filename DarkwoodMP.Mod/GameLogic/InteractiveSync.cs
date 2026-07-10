using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using DarkwoodMP.Patches;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Applies remote switch-state changes to the real components in the scene:
///  - InteractiveItem (levers, switches) via switchOn/switchOff
///  - Item (lamps, standing torches, generator) via turnOn/turnOff
///  - Generator fuel level (absolute value, sent on refuel)
/// Matched by name + rounded position. States that cannot be applied yet
/// (object not loaded) are retried periodically instead of being dropped.
/// </summary>
public class InteractiveSync
{
    private readonly NetworkLayer _network;
    private readonly Dictionary<string, InteractiveObjectState> _interactiveStates = new();

    // Unapplied remote states, keyed like the state dict; retried while set
    private readonly HashSet<string> _pendingStates = new();
    private readonly Dictionary<string, float> _pendingFuel = new();
    private const float PendingRetryInterval = 5f;
    private float _lastPendingRetry;

    private Type _interactiveType;
    private FieldInfo _isOnField;
    private MethodInfo _switchOnMethod;
    private MethodInfo _switchOffMethod;

    private Type _itemType;
    private FieldInfo _itemIsOnField;
    private FieldInfo _itemSwitchableField;
    private MethodInfo _itemTurnOnMethod;
    private MethodInfo _itemTurnOffMethod;

    private Type _generatorType;
    private FieldInfo _generatorFuelField;
    private FieldInfo _generatorMaxFuelField;

    public InteractiveSync(NetworkLayer network)
    {
        _network = network;
    }

    /// <summary>Called every frame by NetworkManager; throttles itself.</summary>
    public void OnUpdate()
    {
        if (_pendingStates.Count == 0 && _pendingFuel.Count == 0) return;
        if (Time.time - _lastPendingRetry < PendingRetryInterval) return;
        _lastPendingRetry = Time.time;

        if (_pendingStates.Count > 0)
        {
            List<string> applied = null;
            foreach (var id in _pendingStates)
            {
                if (!_interactiveStates.TryGetValue(id, out var state)) { (applied ??= new List<string>()).Add(id); continue; }
                if (TryApplyState(state))
                    (applied ??= new List<string>()).Add(id);
            }
            if (applied != null)
                foreach (var id in applied)
                    _pendingStates.Remove(id);
        }

        if (_pendingFuel.Count > 0)
        {
            List<string> applied = null;
            foreach (var kvp in _pendingFuel)
            {
                if (TryApplyFuel(kvp.Key, kvp.Value))
                    (applied ??= new List<string>()).Add(kvp.Key);
            }
            if (applied != null)
                foreach (var id in applied)
                    _pendingFuel.Remove(id);
        }
    }

    public void Reset()
    {
        _interactiveStates.Clear();
        _pendingStates.Clear();
        _pendingFuel.Clear();
    }

    public void OnInteractiveState(InteractiveStatePacket packet)
    {
        var state = new InteractiveObjectState
        {
            ObjectId = packet.ObjectId,
            ObjectType = packet.ObjectType,
            IsActive = packet.IsActive,
            UpdatedBy = packet.PlayerId
        };
        _interactiveStates[packet.ObjectId] = state;

        if (TryApplyState(state))
            _pendingStates.Remove(packet.ObjectId);
        else
            _pendingStates.Add(packet.ObjectId);
    }

    /// <summary>Remote generator refuel: apply the absolute fuel level.</summary>
    public void OnRemoteGeneratorFuel(string objectId, float fuel)
    {
        if (TryApplyFuel(objectId, fuel))
            _pendingFuel.Remove(objectId);
        else
            _pendingFuel[objectId] = fuel;
    }

    private bool TryApplyState(InteractiveObjectState state)
    {
        try
        {
            if (state.ObjectType == "Item")
                return TryApplyItemState(state);
            return TryApplyInteractiveState(state);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractiveSync] Failed to apply state for '{state.ObjectId}': {ex.Message}");
            return true; // don't retry a throwing target forever
        }
    }

    private bool TryApplyInteractiveState(InteractiveObjectState state)
    {
        if (!ResolveApi()) return false;

        var item = FindByGameId(_interactiveType, state.ObjectId);
        if (item == null) return false;

        var isOn = _isOnField.GetValue(item) is bool b && b;
        if (isOn == state.IsActive) return true;

        RemoteApply.Active = true;
        try
        {
            var method = state.IsActive ? _switchOnMethod : _switchOffMethod;
            method.Invoke(item, null);
        }
        finally
        {
            RemoteApply.Active = false;
        }

        ModLogger.Msg($"[InteractiveSync] '{state.ObjectId}' switched {(state.IsActive ? "ON" : "OFF")} by player {state.UpdatedBy}");
        return true;
    }

    private bool TryApplyItemState(InteractiveObjectState state)
    {
        if (!ResolveItemApi()) return false;

        var item = FindByGameId(_itemType, state.ObjectId);
        if (item == null) return false;

        var isOn = _itemIsOnField.GetValue(item) is bool b && b;
        if (isOn == state.IsActive) return true;

        RemoteApply.Active = true;
        try
        {
            var method = state.IsActive ? _itemTurnOnMethod : _itemTurnOffMethod;
            method.Invoke(item, null);
        }
        finally
        {
            RemoteApply.Active = false;
        }

        ModLogger.Msg($"[InteractiveSync] Item '{state.ObjectId}' turned {(state.IsActive ? "ON" : "OFF")} by player {state.UpdatedBy}");
        return true;
    }

    private bool TryApplyFuel(string objectId, float fuel)
    {
        if (!ResolveGeneratorApi()) return false;

        var generator = FindByGameId(_generatorType, objectId);
        if (generator == null) return false;

        try
        {
            if (_generatorMaxFuelField?.GetValue(generator) is float maxFuel && maxFuel > 0f)
                fuel = Mathf.Min(fuel, maxFuel);
            _generatorFuelField.SetValue(generator, fuel);
            ModLogger.Msg($"[InteractiveSync] Generator '{objectId}' fuel set to {fuel:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractiveSync] Failed to set fuel on '{objectId}': {ex.Message}");
        }
        return true;
    }

    /// <summary>Host: current switch states + generator fuel, for the join snapshot.</summary>
    public void CollectSnapshot(List<Packets.Packet> into)
    {
        var playerId = _network.LocalClientId >= 0 ? _network.LocalClientId : 0;

        if (ResolveApi())
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(_interactiveType))
            {
                if (obj is not Component item) continue;
                var isOn = _isOnField.GetValue(item) is bool b && b;
                into.Add(new InteractiveStatePacket
                {
                    ObjectId = GameIds.ForComponent(item),
                    ObjectType = "InteractiveItem",
                    IsActive = isOn,
                    PlayerId = playerId
                });
            }
        }

        if (ResolveItemApi())
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(_itemType))
            {
                if (obj is not Component item) continue;
                if (item.transform.root.name.StartsWith("RemotePlayer_")) continue;
                if (_itemSwitchableField?.GetValue(item) is not bool switchable || !switchable) continue;
                var isOn = _itemIsOnField.GetValue(item) is bool b && b;
                into.Add(new InteractiveStatePacket
                {
                    ObjectId = GameIds.ForComponent(item),
                    ObjectType = "Item",
                    IsActive = isOn,
                    PlayerId = playerId
                });
            }
        }

        if (ResolveGeneratorApi())
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(_generatorType))
            {
                if (obj is not Component generator) continue;
                if (_generatorFuelField.GetValue(generator) is not float fuel) continue;
                into.Add(new GeneratorFuelPacket
                {
                    PlayerId = playerId,
                    ObjectId = GameIds.ForComponent(generator),
                    Fuel = fuel
                });
            }
        }
    }

    public bool IsInteractiveActive(string objectId)
    {
        return _interactiveStates.TryGetValue(objectId, out var s) && s.IsActive;
    }

    public IReadOnlyDictionary<string, InteractiveObjectState> GetInteractiveStates() => _interactiveStates;

    private bool ResolveApi()
    {
        if (_interactiveType != null) return _switchOnMethod != null && _switchOffMethod != null && _isOnField != null;

        _interactiveType = GameTypes.GetType("InteractiveItem");
        if (_interactiveType == null) return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _isOnField = _interactiveType.GetField("isOn", flags);
        _switchOnMethod = _interactiveType.GetMethod("switchOn", flags);
        _switchOffMethod = _interactiveType.GetMethod("switchOff", flags);
        return _switchOnMethod != null && _switchOffMethod != null && _isOnField != null;
    }

    private bool ResolveItemApi()
    {
        if (_itemType != null) return _itemTurnOnMethod != null && _itemTurnOffMethod != null && _itemIsOnField != null;

        _itemType = GameTypes.GetType("Item");
        if (_itemType == null) return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _itemIsOnField = _itemType.GetField("isOn", flags);
        _itemSwitchableField = _itemType.GetField("switchable", flags);
        _itemTurnOnMethod = _itemType.GetMethod("turnOn", flags);
        _itemTurnOffMethod = _itemType.GetMethod("turnOff", flags);
        return _itemTurnOnMethod != null && _itemTurnOffMethod != null && _itemIsOnField != null;
    }

    private bool ResolveGeneratorApi()
    {
        if (_generatorType != null) return _generatorFuelField != null;

        _generatorType = GameTypes.GetType("Generator");
        if (_generatorType == null) return false;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _generatorFuelField = _generatorType.GetField("fuel", flags);
        _generatorMaxFuelField = _generatorType.GetField("maxFuel", flags);
        return _generatorFuelField != null;
    }

    // Per-frame scan cache: a retry pass with several pending states used to
    // run one full FindObjectsOfType scan PER state (stutter source)
    private readonly Dictionary<Type, UnityEngine.Object[]> _scanCache = new();
    private int _scanCacheFrame = -1;

    private UnityEngine.Object[] Scan(Type type)
    {
        if (_scanCacheFrame != Time.frameCount)
        {
            _scanCache.Clear();
            _scanCacheFrame = Time.frameCount;
        }
        if (!_scanCache.TryGetValue(type, out var arr))
        {
            arr = UnityEngine.Object.FindObjectsOfType(type);
            _scanCache[type] = arr;
        }
        return arr;
    }

    /// <summary>Exact GameIds match with a same-name nearest fallback (10m).</summary>
    private Component FindByGameId(Type componentType, string objectId)
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
        foreach (var obj in Scan(componentType))
        {
            if (obj is not Component c) continue;
            if (c.transform.root.name.StartsWith("RemotePlayer_")) continue;
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

public class InteractiveObjectState
{
    public string ObjectId = "";
    public string ObjectType = "";
    public bool IsActive;
    public int UpdatedBy;
}
