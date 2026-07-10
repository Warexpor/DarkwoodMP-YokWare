using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Co-op map markers + discovery (Horde MultiplayerMapManager slim).
/// </summary>
public sealed class MapSync_Patch : IPatch
{
    private static readonly List<string> _markers = new List<string>(); // "pid:x,y,z"
    private static readonly HashSet<string> _discovered = new HashSet<string>();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name.IndexOf("discover", StringComparison.OrdinalIgnoreCase) >= 0
            || target.Name.IndexOf("Discover", StringComparison.Ordinal) >= 0)
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(MapSync_Patch).GetMethod(nameof(DiscoverPostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(MapSync_Patch).GetMethod(nameof(MarkerPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Map", "addMarker");
        yield return ("Map", "placeMarker");
        yield return ("Map", "removeMarker");
        yield return ("MapElement", "discover");
        yield return ("MapElement", "setDiscovered");
        yield return ("MapElement", "OnDiscover");
    }

    public static void MarkerPostfix(object __instance, object[] __args, MethodBase __originalMethod)
    {
        try
        {
            if (RemoteApply.Active) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;

            Vector3 pos = Vector3.zero;
            if (__args != null)
            {
                foreach (var a in __args)
                {
                    if (a is Vector3 v) { pos = v; break; }
                    if (a is Vector2 v2) { pos = new Vector3(v2.x, 0, v2.y); break; }
                }
            }
            if (pos == Vector3.zero && Player.Instance != null)
                pos = Player.Instance.transform.position;

            bool remove = (__originalMethod?.Name ?? "").IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0;
            var inv = CultureInfo.InvariantCulture;
            var payload = $"{manager.LocalPlayerId}:{pos.x.ToString("F1", inv)},{pos.y.ToString("F1", inv)},{pos.z.ToString("F1", inv)}";
            if (!remove) _markers.Add(payload);
            network.SendReliable(new MapMarkerPacket
            {
                PlayerId = manager.LocalPlayerId,
                Remove = remove,
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex) { ModLogger.Error($"[MapSync] marker: {ex.Message}"); }
    }

    public static void DiscoverPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var name = (__instance as UnityEngine.Object)?.name ?? "elem";
            if (!_discovered.Add(name)) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected) return;
            network.SendReliable(new MapDiscoverPacket
            {
                PlayerId = manager.LocalPlayerId,
                ElementName = name.Replace(":", "_")
            });
        }
        catch (Exception ex) { ModLogger.Error($"[MapSync] discover: {ex.Message}"); }
    }

    public static void ApplyDiscover(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _discovered.Add(name);
        try
        {
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(typeof(Component)))
            {
                if (obj is not Component c) continue;
                if (c.gameObject.name != name) continue;
                if (c.GetType().Name != "MapElement") continue;
                RemoteApply.Active = true;
                try
                {
                    c.GetType().GetMethod("discover", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.Invoke(c, null);
                    c.GetType().GetField("discovered", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(c, true);
                }
                finally { RemoteApply.Active = false; }
                return;
            }
        }
        catch { }
    }

    /// <summary>Best-effort remote marker record (session store; visual apply is game-dependent).</summary>
    public static void ApplyMarker(int playerId, bool remove, Vector3 pos)
    {
        var inv = CultureInfo.InvariantCulture;
        var payload = $"{playerId}:{pos.x.ToString("F1", inv)},{pos.y.ToString("F1", inv)},{pos.z.ToString("F1", inv)}";
        if (remove)
            _markers.RemoveAll(m => m.StartsWith(playerId + ":", StringComparison.Ordinal));
        else if (!_markers.Contains(payload))
            _markers.Add(payload);
    }

    public static void CollectSnapshot(List<Packet> into)
    {
        if (into == null) return;
        var hostPid = NetworkManager.Instance?.LocalPlayerId ?? 0;
        var inv = CultureInfo.InvariantCulture;
        foreach (var m in _markers)
        {
            // "pid:x,y,z"
            var sep = m.IndexOf(':');
            if (sep <= 0) continue;
            if (!int.TryParse(m.Substring(0, sep), out var mpid)) mpid = hostPid;
            var xyz = m.Substring(sep + 1).Split(',');
            if (xyz.Length != 3
                || !float.TryParse(xyz[0], NumberStyles.Float, inv, out var x)
                || !float.TryParse(xyz[1], NumberStyles.Float, inv, out var y)
                || !float.TryParse(xyz[2], NumberStyles.Float, inv, out var z))
                continue;
            into.Add(new MapMarkerPacket { PlayerId = mpid, Remove = false, X = x, Y = y, Z = z });
        }
        foreach (var d in _discovered)
            into.Add(new MapDiscoverPacket { PlayerId = hostPid, ElementName = d });
    }

    public static void Reset()
    {
        _markers.Clear();
        _discovered.Clear();
    }
}
