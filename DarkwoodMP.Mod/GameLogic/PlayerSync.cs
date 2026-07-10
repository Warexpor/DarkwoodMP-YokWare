using System;
using System.Collections.Generic;
using UnityEngine;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Local pose send (~30Hz) + remote interpolation every frame.
/// Phase 2: Horde multi-peer hygiene — stable PlayerId, position registry,
/// first-packet snap, freeze-aware proxies, leave cleanup.
/// Hop relay / dedicated server still fan out PositionUpdate; this module owns apply.
/// </summary>
public class PlayerSync
{
    private readonly NetworkLayer _network;

    // Send side (~30Hz like Horde PlayerState hot path)
    private const float SendInterval = 1f / 30f;
    private const float KeepAliveInterval = 1f;
    private const float MoveEpsilon = 0.005f;
    private float _lastSendCheck;
    private float _lastSent;
    private Vector3 _lastSentPos;
    private Quaternion _lastSentRot = Quaternion.identity;
    private Quaternion _lastSentLegsRot = Quaternion.identity;

    // Interpolation
    private const float PosSharpness = 14f;
    private const float RotSharpness = 12f;
    private const float SnapDistance = 12f;
    private const float MaxExtrapolation = 0.2f;

    private Transform _localPlayerTransform;
    private readonly Dictionary<int, RemotePlayerState> _remoteStates = new Dictionary<int, RemotePlayerState>();
    private static readonly List<int> _staleBuffer = new List<int>();
    private float _lastPlayerSearch = float.MinValue;

    public Transform LocalPlayerTransform => _localPlayerTransform;

    public PlayerSync(NetworkLayer network)
    {
        _network = network;
    }

    public void OnUpdate()
    {
        if (_localPlayerTransform == null)
        {
            if (Time.time - _lastPlayerSearch > 1f)
            {
                _lastPlayerSearch = Time.time;
                _localPlayerTransform = FindLocalPlayer();
                if (_localPlayerTransform != null)
                    ModLogger.Msg($"[PlayerSync] Found local player: {_localPlayerTransform.name}");
            }
        }

        SendPositionIfNeeded();
        InterpolateRemotePlayers();
    }

    public void OnDisconnected()
    {
        _localPlayerTransform = null;
        _remoteStates.Clear();
        PlayerPositionRegistry.Clear();
    }

    public void OnRemotePlayerRemoved(int playerId)
    {
        _remoteStates.Remove(playerId);
        PlayerPositionRegistry.Remove(playerId);
    }

    private void SendPositionIfNeeded()
    {
        if (_localPlayerTransform == null) return;
        if (Time.time - _lastSendCheck < SendInterval) return;
        _lastSendCheck = Time.time;

        var position = _localPlayerTransform.position;
        var rotation = _localPlayerTransform.rotation;
        var legsRotation = DarkwoodMP.DependencyInjection.ServiceLocator
            .Resolve<PlayerAnimSync>()?.GetLocalLegsRotation() ?? Quaternion.identity;

        // Spectating: peers must see corpse/death pos, not camera-followed body
        var spec = SpectatorModeController.Instance;
        if (spec != null && spec.IsSpectating && spec.NetworkPositionOverride.HasValue)
            position = spec.NetworkPositionOverride.Value;
        if (FinalDreamsceneManager.IsLocalDead && spec != null && spec.NetworkPositionOverride.HasValue)
            position = spec.NetworkPositionOverride.Value;

        // Always keep multi-center book fresh for AI even when we skip the wire packet
        var localId = ResolveLocalPlayerId();
        PlayerPositionRegistry.SetLocalPlayerId(localId);
        PlayerPositionRegistry.ReportLocal(position, rotation.eulerAngles.y);

        var moved = (position - _lastSentPos).sqrMagnitude > MoveEpsilon * MoveEpsilon
                    || Quaternion.Angle(rotation, _lastSentRot) > 0.5f
                    || Quaternion.Angle(legsRotation, _lastSentLegsRot) > 1f;
        if (!moved && Time.time - _lastSent < KeepAliveInterval) return;

        _lastSent = Time.time;
        _lastSentPos = position;
        _lastSentRot = rotation;
        _lastSentLegsRot = legsRotation;

        // Broadcast = host→all / client→server (hop relay fans out to other clients)
        _network.Broadcast(new PositionUpdatePacket
        {
            PlayerId = localId,
            X = position.x, Y = position.y, Z = position.z,
            Rx = rotation.x, Ry = rotation.y, Rz = rotation.z, Rw = rotation.w,
            LegsRx = legsRotation.x, LegsRy = legsRotation.y, LegsRz = legsRotation.z, LegsRw = legsRotation.w
        }, reliable: false);
    }

    public void OnPositionReceived(PositionUpdatePacket packet)
    {
        var manager = NetworkManager.Instance;
        if (manager != null && packet.PlayerId == manager.LocalPlayerId) return;

        var newPos = new Vector3(packet.X, packet.Y, packet.Z);
        var newRot = new Quaternion(packet.Rx, packet.Ry, packet.Rz, packet.Rw);
        var now = Time.time;

        PlayerPositionRegistry.UpdateRemote(packet.PlayerId, newPos, newRot.eulerAngles.y);

        if (!_remoteStates.TryGetValue(packet.PlayerId, out var state))
        {
            state = new RemotePlayerState();
            _remoteStates[packet.PlayerId] = state;
        }

        if (state.Initialized)
        {
            var dt = Mathf.Max(now - state.LastPacketTime, 0.01f);
            var velocity = (newPos - state.TargetPos) / dt;
            state.Velocity = velocity.sqrMagnitude < 400f ? velocity : Vector3.zero;
        }

        state.TargetPos = newPos;
        state.TargetRot = newRot;
        state.LastPacketTime = now;
        state.Initialized = true;

        // First packet: snap proxy immediately (avoid multi-second glide from spawn origin)
        var playerObj = manager?.GetRemotePlayer(packet.PlayerId);
        if (playerObj != null && !state.HasRendered)
        {
            var proxy = playerObj.GetComponent<RemotePlayerProxy>();
            if (proxy == null || !proxy.FreezePosition)
            {
                playerObj.transform.position = newPos;
                playerObj.transform.rotation = newRot;
            }
            state.HasRendered = true;
        }

        if (playerObj != null)
        {
            var proxyHint = playerObj.GetComponent<RemotePlayerProxy>();
            proxyHint?.ApplyNetworkHint(newPos, newRot.eulerAngles.y);
        }

        DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<PlayerAnimSync>()
            ?.SetRemoteLegsRotation(packet.PlayerId,
                new Quaternion(packet.LegsRx, packet.LegsRy, packet.LegsRz, packet.LegsRw));
    }

    private void InterpolateRemotePlayers()
    {
        var manager = NetworkManager.Instance;
        if (manager == null || _remoteStates.Count == 0) return;

        var now = Time.time;
        var dt = Time.deltaTime;
        var posT = 1f - Mathf.Exp(-PosSharpness * dt);
        var rotT = 1f - Mathf.Exp(-RotSharpness * dt);

        _staleBuffer.Clear();

        foreach (var kvp in _remoteStates)
        {
            var state = kvp.Value;
            if (!state.Initialized) continue;

            if (now - state.LastPacketTime > 30f)
            {
                _staleBuffer.Add(kvp.Key);
                continue;
            }

            var playerObj = manager.GetRemotePlayer(kvp.Key);
            if (playerObj == null) continue;

            var proxy = playerObj.GetComponent<RemotePlayerProxy>();
            if (proxy != null && proxy.FreezePosition) continue;

            var age = Mathf.Min(now - state.LastPacketTime, MaxExtrapolation);
            var target = state.TargetPos + state.Velocity * age;

            var t = playerObj.transform;
            if ((t.position - target).sqrMagnitude > SnapDistance * SnapDistance)
            {
                t.position = target;
                t.rotation = state.TargetRot;
            }
            else
            {
                t.position = Vector3.Lerp(t.position, target, posT);
                t.rotation = Quaternion.Slerp(t.rotation, state.TargetRot, rotT);
            }

            // Keep registry on interpolated pose so multi-center uses display pos
            PlayerPositionRegistry.UpdateRemote(kvp.Key, t.position, t.eulerAngles.y);
        }

        foreach (var id in _staleBuffer)
        {
            _remoteStates.Remove(id);
            PlayerPositionRegistry.Remove(id);
        }
    }

    private int ResolveLocalPlayerId()
    {
        var manager = NetworkManager.Instance;
        if (manager != null && manager.LocalPlayerId >= 0)
            return manager.LocalPlayerId;
        return _network.LocalClientId >= 0 ? _network.LocalClientId : 0;
    }

    private Transform FindLocalPlayer()
    {
        var playerType = DarkwoodMP.Patches.GameTypes.GetType("Player");
        if (playerType != null)
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(playerType))
            {
                if (obj is not Behaviour behaviour || !behaviour.enabled) continue;
                if (behaviour.gameObject.name.StartsWith("RemotePlayer_")) continue;
                if (behaviour.GetComponent<RemotePlayerProxy>() != null) continue;
                return behaviour.transform;
            }
        }

        var names = new[] { "MainPlayer", "Player", "PlayerController", "Hero" };
        foreach (var name in names)
        {
            var obj = GameObject.Find(name);
            if (obj != null && obj.GetComponent<RemotePlayerProxy>() == null)
                return obj.transform;
        }
        return null;
    }
}

public class RemotePlayerState
{
    public Vector3 TargetPos;
    public Quaternion TargetRot = Quaternion.identity;
    public Vector3 Velocity;
    public float LastPacketTime;
    public bool Initialized;
    /// <summary>False until first network pose applied (snap vs lerp from origin).</summary>
    public bool HasRendered;
}
