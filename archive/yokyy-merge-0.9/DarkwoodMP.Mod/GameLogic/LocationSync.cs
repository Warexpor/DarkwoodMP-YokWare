using System;
using System.Collections.Generic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Outside location enter/exit sync (Horde LocationEnter/Exit).
/// Keeps remotes snapped into basements/bunkers with the player who entered.
/// </summary>
public class LocationSync
{
    private readonly NetworkLayer _network;
    // playerId -> last known location name (empty = outside)
    private readonly Dictionary<int, string> _inside = new Dictionary<int, string>();
    private readonly Dictionary<int, Vector3> _exitPos = new Dictionary<int, Vector3>();

    public LocationSync(NetworkLayer network) { _network = network; }

    public void OnLocalEnter(string locName, Vector3 pos)
    {
        if (string.IsNullOrEmpty(locName) || _network == null || !_network.IsConnected) return;
        var pid = MyId();
        _inside[pid] = locName;
        _network.SendReliable(new LocationEnterPacket
        {
            PlayerId = pid,
            TargetPlayerId = pid,
            LocName = Sanitize(locName),
            X = pos.x, Y = pos.y, Z = pos.z
        });
    }

    public void OnLocalExit(Vector3 pos)
    {
        if (_network == null || !_network.IsConnected) return;
        var pid = MyId();
        _inside.Remove(pid);
        _exitPos[pid] = pos;
        _network.SendReliable(new LocationExitPacket
        {
            PlayerId = pid,
            TargetPlayerId = pid,
            X = pos.x, Y = pos.y, Z = pos.z
        });
    }

    public void OnRemoteEnter(int playerId, string locName, Vector3 pos)
    {
        _inside[playerId] = locName;
        SnapProxy(playerId, pos);
        // Try to put proxy into location hierarchy if OutsideLocations exposes it
        try
        {
            var ol = Singleton<OutsideLocations>.Instance;
            // Best-effort: teleport is enough for visibility
        }
        catch { }
        ModLogger.Msg($"[LocationSync] p{playerId} entered '{locName}'");
    }

    public void OnRemoteExit(int playerId, Vector3 pos)
    {
        _inside.Remove(playerId);
        _exitPos[playerId] = pos;
        SnapProxy(playerId, pos);
        ModLogger.Msg($"[LocationSync] p{playerId} exited location");
    }

    public void CollectSnapshot(List<Packet> into)
    {
        if (into == null) return;
        foreach (var kvp in _inside)
        {
            var pos = Vector3.zero;
            var go = NetworkManager.Instance?.GetRemotePlayer(kvp.Key);
            if (go != null) pos = go.transform.position;
            else if (kvp.Key == MyId() && Player.Instance != null)
                pos = Player.Instance.transform.position;
            into.Add(new LocationEnterPacket
            {
                PlayerId = MyId(),
                TargetPlayerId = kvp.Key,
                LocName = Sanitize(kvp.Value),
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
    }

    public void Reset()
    {
        _inside.Clear();
        _exitPos.Clear();
    }

    private static void SnapProxy(int playerId, Vector3 pos)
    {
        var go = NetworkManager.Instance?.GetRemotePlayer(playerId);
        if (go == null) return;
        var proxy = go.GetComponent<RemotePlayerProxy>();
        if (proxy != null) proxy.FreezePosition = false;
        go.transform.position = pos;
        PlayerPositionRegistry.UpdateRemote(playerId, pos);
    }

    private int MyId()
    {
        var m = NetworkManager.Instance;
        return m != null && m.LocalPlayerId >= 0 ? m.LocalPlayerId : Math.Max(_network.LocalClientId, 0);
    }

    private static string Sanitize(string s) => (s ?? "").Replace(":", "_");
}
