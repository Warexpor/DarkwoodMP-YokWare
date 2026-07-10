using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Syncs gasoline trails and burning liquids. Verified in IL:
/// Player.waitToSpillLiquid spawns segments via
/// Core.AddPrefab("Items/GasolineTrail", pos, rot, null, ...) while pouring;
/// Liquid.startBurning ignites a puddle (and neighbors spread locally on each
/// machine, converging via the idempotent `burning` check).
/// </summary>
public sealed class Gasoline_Patch : IPatch
{
    private const string TrailPrefab = "Items/GasolineTrail";

    // A liquid only ignites once; dedupe by instance
    private static readonly HashSet<int> _burnSent = new();

    // Fire SPREAD is deterministic and local on both machines (each burning
    // segment chains to overlapping neighbors via OverlapSphere) - only the
    // SEED ignition needs to travel. Broadcasting every segment of a burning
    // gas cloud/trail was a reliable-packet storm, and each packet cost the
    // receiver a whole-scene Liquid scan ("huge lag on igniting gas").
    private static readonly List<RecentBurn> _recentBurns = new();
    private const float SpreadRadius = 50f;  // > a puddle's capsule radius
    private const float SpreadWindow = 60f;  // a fire burns ~20s + margin

    private struct RecentBurn { public float X, Z, Time; }

    /// <summary>Register a burn (local or remotely applied) as a spread seed.</summary>
    public static void RecordBurn(Vector3 pos)
    {
        var now = Time.time;
        _recentBurns.RemoveAll(b => now - b.Time > SpreadWindow);
        if (_recentBurns.Count < 512)
            _recentBurns.Add(new RecentBurn { X = pos.x, Z = pos.z, Time = now });
    }

    private static bool IsSpreadFrom(Vector3 pos)
    {
        var now = Time.time;
        for (var i = _recentBurns.Count - 1; i >= 0; i--)
        {
            var b = _recentBurns[i];
            if (now - b.Time > SpreadWindow) continue;
            var dx = b.X - pos.x;
            var dz = b.Z - pos.z;
            if (dx * dx + dz * dz < SpreadRadius * SpreadRadius) return true;
        }
        return false;
    }

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _burnSent.Clear();
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "AddPrefab")
        {
            // Only the string overload spawns "Items/GasolineTrail" - find it
            // explicitly (the registry may hand us the Object overload)
            var postfix = new HarmonyMethod(typeof(Gasoline_Patch).GetMethod(nameof(AddPrefabPostfix), statics)!);
            foreach (var m in target.DeclaringType!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "AddPrefab") continue;
                var ps = m.GetParameters();
                if (ps.Length > 0 && ps[0].ParameterType == typeof(string))
                    baseHarmony.Patch(m, postfix: postfix);
            }
        }
        else if (target.Name == "stopBurning")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Gasoline_Patch).GetMethod(nameof(StopBurnPrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Gasoline_Patch).GetMethod(nameof(StopBurnPostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Gasoline_Patch).GetMethod(nameof(BurnPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    private static bool _stopWasBurning;

    public static void StopBurnPrefix(object __instance)
    {
        _stopWasBurning = false;
        try
        {
            if (__instance is Liquid liq) _stopWasBurning = liq.burning;
        }
        catch { }
    }

    public static void StopBurnPostfix(object __instance)
    {
        try
        {
            if (!_stopWasBurning || RemoteApply.Active) return;
            if (__instance is not Component liquid) return;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;
            var pos = liquid.transform.position;
            network.SendReliable(new LiquidStopBurnPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                X = pos.x, Y = pos.y, Z = pos.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Gasoline_Patch] stopBurning: {ex.Message}");
        }
    }

    public static void ApplyRemoteStopBurn(Vector3 pos)
    {
        try
        {
            var hits = Physics.OverlapSphere(pos, 1.5f);
            for (var i = 0; i < hits.Length; i++)
            {
                var liq = hits[i].GetComponent<Liquid>();
                if (liq == null) continue;
                RemoteApply.Active = true;
                try
                {
                    // stopBurning is non-public on Liquid in this game build
                    var m = typeof(Liquid).GetMethod("stopBurning",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    m?.Invoke(liq, null);
                }
                finally { RemoteApply.Active = false; }
                break;
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Gasoline_Patch] ApplyRemoteStopBurn: {ex.Message}");
        }
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Core", "AddPrefab");
        yield return ("Liquid", "startBurning");
        yield return ("Liquid", "stopBurning");
    }

    // Typed binding (no __args boxing - AddPrefab is called for every FX in
    // the game). Names match Core.AddPrefab(string prefab, Vector3 position,
    // Quaternion quaternion, GameObject parentGO, bool worldSpace).
    public static void AddPrefabPostfix(string prefab, Vector3 position, Quaternion quaternion)
    {
        try
        {
            if (prefab != TrailPrefab) return;
            if (RemoteApply.Active) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new GasTrailPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                X = position.x, Y = position.y, Z = position.z,
                Rx = quaternion.x, Ry = quaternion.y, Rz = quaternion.z, Rw = quaternion.w
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Gasoline_Patch] {ex.Message}");
        }
    }

    public static void BurnPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Component liquid) return;
            if (!_burnSent.Add(liquid.GetInstanceID())) return;

            var pos = liquid.transform.position;
            var isSpread = IsSpreadFrom(pos);
            RecordBurn(pos);
            if (isSpread) return; // partner's own fire spread lights this one

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;
            network.SendReliable(new BurnLiquidPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                X = pos.x, Z = pos.z
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Gasoline_Patch] {ex.Message}");
        }
    }
}
